#nullable enable
using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Healing Zone System — manages ground-effect healing zones placed by skills.
    /// 
    /// Two-phase pattern:
    ///   - Phase 1 (Skill casts): creates CorpseEffect zone entries (type=4, Healing)
    ///   - Phase 2 (Update): ticks duration, applies heal per second to allies in range
    ///
    /// Integration points:
    ///   - SkillSystem CastGroundTarget with AreaShapeType.HealingZone calls AddHealingZone()
    ///   - FrameScheduler SkillBuff group calls HealingZone.Update()
    ///   - Heals both the player (hero) and nearby summoned units
    ///
    /// Design notes:
    ///   - Uses CorpseEffect infrastructure (type=4) — no duplicate data structures
    ///   - Healing applies to PlayerCurrentHealth (hero) and any summoned allies
    ///   - CorpseEffect type 4 is reused: existing CorpseEffectSystem skips it (not DoT)
    ///   - Max healing zones = MAX_CORPSE_EFFECTS (2000) — shared pool with corpse effects
    /// </summary>
    public class HealingZoneSystem
    {
        private readonly ComponentStore _store;
        private readonly IRenderer? _logger;

        // Ground target skill damage queue (shared with SkillSystem for GroundTarget area shape)
        private List<(int enemyId, float damage)>[] _skillDamageQueue = new List<(int, float)>[2];
        private readonly object _skillDamageQueueLock = new object();
        private int _skillDamageQueueIdx = 0;

        public HealingZoneSystem(ComponentStore store, IRenderer? logger = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger;
            _skillDamageQueue[0] = new List<(int, float)>(256);
            _skillDamageQueue[1] = new List<(int, float)>(256);
        }

        /// <summary>
        /// Place a healing zone at the specified world coordinates.
        /// Returns zone ID (CorpseEffect slot) or -1 if no free slots.
        /// </summary>
        /// <param name="x">World X coordinate</param>
        /// <param name="y">World Y coordinate</param>
        /// <param name="radius">Healing radius in tiles</param>
        /// <param name="duration">Duration in seconds</param>
        /// <param name="healPerSec">Healing amount per second</param>
        public int AddHealingZone(float x, float y, float radius, float duration, float healPerSec)
        {
            // Use CorpseEffect infrastructure — type 4 = Healing
            // AddCorpseEffect(x, y, effectType, radius, duration, damagePerTick, slowAmount, tickInterval)
            // For healing zones: damagePerTick stores healPerSec, tickInterval stores 1.0 (1 second tick)
            int zoneId = _store.AddCorpseEffect(x, y, 4, radius, duration, healPerSec, 1f, 1f);

            if (zoneId >= 0)
            {
                _logger?.Log($"[HEALZONE] Placed healing zone at ({x:F1}, {y:F1}), radius={radius}, duration={duration}s, hps={healPerSec}");
            }
            else
            {
                _logger?.Log($"[HEALZONE] Failed to place healing zone — pool full");
            }

            return zoneId;
        }

        /// <summary>
        /// Update all active healing zones — decrement duration, apply heals.
        /// Called from SkillBuffGroup during Phase 9.
        /// </summary>
        public void Update(float deltaTime)
        {
            var activeIds = _store.GetCachedActiveCorpseEffectIds();

            for (int i = activeIds.Count - 1; i >= 0; i--)
            {
                int zoneId = activeIds[i];
                if (!_store.CorpseEffectActive[zoneId]) continue;

                // Only process healing zones (type 4)
                if (_store.CorpseEffectType[zoneId] != 4) continue;

                // Tick duration
                _store.CorpseEffectDuration[zoneId] -= deltaTime;

                // Accumulate heal over time (apply each frame for smooth healing)
                ApplyHealingTick(zoneId, deltaTime);

                // Check expiration
                if (_store.CorpseEffectDuration[zoneId] <= 0f)
                {
                    _store.RemoveCorpseEffect(zoneId);
                }
            }

            // Resolve queued skill damage (for GroundTarget healing abilities)
            ResolveSkillDamage();
        }

        /// <summary>
        /// Apply a healing tick to all allies within range of a healing zone.
        /// Heals both the player (hero) and any active summoned units.
        /// </summary>
        private void ApplyHealingTick(int zoneId, float deltaTime)
        {
            float cx = _store.CorpseEffectX[zoneId];
            float cy = _store.CorpseEffectY[zoneId];
            float radius = _store.CorpseEffectRadius[zoneId];
            float healPerSec = _store.CorpseEffectDamagePerTick[zoneId]; // stored as damagePerTick

            if (healPerSec <= 0f) return;

            // Frame heal = healPerSec * deltaTime (smooth per-frame healing)
            float frameHeal = healPerSec * deltaTime;
            float radiusSq = radius * radius;

            // Heal player (hero) — player entity ID is in PlayerEntityId
            int playerId = _store.PlayerEntityId;
            if (playerId >= 0 && _store.PositionActive[playerId])
            {
                float dx = _store.PositionX[playerId] - cx;
                float dy = _store.PositionY[playerId] - cy;
                float distSq = dx * dx + dy * dy;
                if (distSq <= radiusSq)
                {
                    HealPlayer(playerId, frameHeal);
                }
            }

            // Heal player-summoned units — check ComponentStore_Enemy.SummonedUnitActive
            var activeEnemyIds = _store.GetCachedActiveEnemyIds();
            for (int i = 0; i < activeEnemyIds.Count; i++)
            {
                int entityId = activeEnemyIds[i];
                if (!_store.SummonedUnitActive[entityId]) continue;

                float dx = _store.PositionX[entityId] - cx;
                float dy = _store.PositionY[entityId] - cy;
                float distSq = dx * dx + dy * dy;
                if (distSq <= radiusSq)
                {
                    HealSummonedUnit(entityId, frameHeal);
                }
            }
        }

        /// <summary>
        /// Heal a player (hero) by the specified amount.
        /// </summary>
        private void HealPlayer(int playerId, float healAmount)
        {
            float currentHealth = _store.PlayerCurrentHealth[playerId];
            float maxHealth = _store.PlayerMaxHealth[playerId];

            if (currentHealth >= maxHealth) return; // Already at full health
            if (currentHealth <= 0f) return;       // Player dead

            float newHealth = Math.Min(currentHealth + healAmount, maxHealth);
            _store.PlayerCurrentHealth[playerId] = newHealth;
        }

        /// <summary>
        /// Heal a summoned unit by the specified amount.
        /// Uses ComponentStore_Enemy.SummonedUnitActive[] and SummonedUnitHealth[].
        /// </summary>
        private void HealSummonedUnit(int entityId, float healAmount)
        {
            // Only process player-summoned units
            if (!_store.SummonedUnitActive[entityId]) return;

            float currentHealth = _store.SummonedUnitHealth[entityId];
            if (currentHealth <= 0f) return;

            float newHealth = Math.Min(currentHealth + healAmount, _store.SummonedUnitMaxHealth[entityId]);
            _store.SummonedUnitHealth[entityId] = newHealth;
        }

        /// <summary>
        /// Resolve queued skill damage from GroundTarget healing abilities.
        /// Follows two-phase pattern: parallel collect → serial apply.
        /// </summary>
        private void ResolveSkillDamage()
        {
            int readIdx = _skillDamageQueueIdx;
            int writeIdx = 1 - _skillDamageQueueIdx;
            _skillDamageQueueIdx = writeIdx;
            _skillDamageQueue[writeIdx].Clear();

            foreach (var (enemyId, damage) in _skillDamageQueue[readIdx])
            {
                if (enemyId == _store.PlayerEntityId)
                {
                    // Healing to player
                    float currentHealth = _store.PlayerCurrentHealth[enemyId];
                    float maxHealth = _store.PlayerMaxHealth[enemyId];
                    if (currentHealth > 0f && currentHealth < maxHealth)
                    {
                        float newHealth = Math.Min(currentHealth + damage, maxHealth);
                        _store.PlayerCurrentHealth[enemyId] = newHealth;
                    }
                }
            }
        }

        /// <summary>
        /// Queue a GroundTarget healing ability's damage (heal) for resolution.
        /// Called from SkillSystem.CastGroundTarget when areaShape = HealingZone.
        /// </summary>
        public void QueueGroundTargetHeal(int targetId, float healAmount)
        {
            lock (_skillDamageQueueLock)
            {
                _skillDamageQueue[_skillDamageQueueIdx].Add((targetId, healAmount));
            }
        }
    }
}
