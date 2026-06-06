using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Corpse Effect System — manages ground effects spawned when enemies die.
    /// 
    /// Two-phase pattern:
    ///   - Phase 1 (ResolveEnemiesKilledThisFrame): subscribe to OnEnemyKilled, queue corpse effects
    ///   - Phase 2 (Update): tick durations, apply effects, expire zones
    /// 
    /// Effect types:
    ///   0 = Poison (DoT), 1 = Slow, 2 = Ice (freeze), 3 = Fire (DoT), 4 = Healing, 5 = DamageBoost
    ///   6 = HallowedGround (positive DoT, holy smite — Round 168 Direction 3)
    ///   7 = ThornyBramble (DoT + slow combo — Round 169 Direction 10)
    /// 
    /// Integration points:
    ///   - FrameScheduler.Tick() Phase 9.6 calls CorpseEffectSystem.Update()
    ///   - FrameScheduler registers CorpseEffectSystem via scheduler.CorpseEffect
    ///   - GameConfigLoader loads CorpseEffectDefs from Data/Configs/corpse_effects.json
    /// </summary>
    public class CorpseEffectSystem
    {
        private readonly ComponentStore _store;
        private readonly GameConfig _gameConfig;
        private readonly BuffSystem _buffSystem;
        private readonly IRenderer _logger;

        // Monster type name → CorpseEffectDef lookup (built at startup)
        private Dictionary<string, CorpseEffectDef> _monsterTypeToEffect = new Dictionary<string, CorpseEffectDef>();

        // CorpseEffectDef list (from config)
        private List<CorpseEffectDef> _corpseEffectDefs = new List<CorpseEffectDef>();

        public CorpseEffectSystem(ComponentStore store, GameConfig gameConfig, BuffSystem buffSystem, IRenderer logger = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _gameConfig = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
            _buffSystem = buffSystem;
            _logger = logger;
        }

        /// <summary>
        /// Load corpse effect definitions from GameConfig.
        /// Must be called after GameConfig.CorpseEffectDefs is populated.
        /// </summary>
        public void LoadCorpseEffects()
        {
            _corpseEffectDefs.Clear();
            _monsterTypeToEffect.Clear();

            if (_gameConfig.CorpseEffectDefs == null || _gameConfig.CorpseEffectDefs.Count == 0)
            {
                _logger?.Log("[CORPSE] No corpse effect definitions found.");
                return;
            }

            foreach (var def in _gameConfig.CorpseEffectDefs)
            {
                _corpseEffectDefs.Add(def);
                if (def.MonsterTypes != null)
                {
                    foreach (var monsterType in def.MonsterTypes)
                    {
                        _monsterTypeToEffect[monsterType] = def;
                    }
                }
            }

            _logger?.Log($"[CORPSE] Loaded {_corpseEffectDefs.Count} corpse effect definitions covering {_monsterTypeToEffect.Count} monster types.");
        }

        /// <summary>
        /// Subscribe to OnEnemyKilled to spawn corpse effects on death.
        /// Called during GameManager bootstrap.
        /// </summary>
        public void SubscribeToOnEnemyKilled()
        {
            _store.OnEnemyKilled += HandleEnemyKilled;
        }

        private void HandleEnemyKilled(int enemyId, int playerId)
        {
            // Look up the monster type for this enemy
            string typeName = _store.EnemyTypeName[enemyId];
            if (string.IsNullOrEmpty(typeName)) return;

            if (!_monsterTypeToEffect.TryGetValue(typeName, out var effectDef))
                return;

            float x = _store.PositionX[enemyId];
            float y = _store.PositionY[enemyId];

            _store.AddCorpseEffect(
                x, y,
                effectDef.EffectType,
                effectDef.Radius,
                effectDef.Duration,
                effectDef.DamagePerTick,
                effectDef.SlowAmount,
                effectDef.TickInterval
            );

            _logger?.Log($"[CORPSE] Spawned {effectDef.Name} at ({x:F1}, {y:F1}) for {effectDef.Duration:F1}s");
        }

        /// <summary>
        /// Update all active corpse effects — decrement duration, apply effects, expire.
        /// Called from FrameScheduler during Phase 9.6.
        /// </summary>
        public void Update(float deltaTime)
        {
            var activeIds = _store.GetCachedActiveCorpseEffectIds();

            for (int i = activeIds.Count - 1; i >= 0; i--)
            {
                int zoneId = activeIds[i];
                if (!_store.CorpseEffectActive[zoneId]) continue;

                // Tick duration
                _store.CorpseEffectDuration[zoneId] -= deltaTime;

                // Tick timer for DoT effects
                int curEffectType = _store.CorpseEffectType[zoneId];
                if (curEffectType == 0 || curEffectType == 3 || curEffectType == 6 || curEffectType == 7)
                {
                    // Poison (0), Fire (3), HallowedGround (6), ThornyBramble (7) — all DoT effects
                    _store.CorpseEffectTickTimer[zoneId] += deltaTime;
                    float interval = _store.CorpseEffectTickInterval[zoneId];
                    if (interval <= 0f) interval = 1f; // fallback
                    if (_store.CorpseEffectTickTimer[zoneId] >= interval)
                    {
                        _store.CorpseEffectTickTimer[zoneId] -= interval;
                        ApplyDoTTick(zoneId);
                    }
                }

                // Check expiration
                if (_store.CorpseEffectDuration[zoneId] <= 0f)
                {
                    _store.RemoveCorpseEffect(zoneId);
                    continue;
                }

                // Apply per-frame effects (slow, ice freeze)
                ApplyContinuousEffect(zoneId);
            }
        }

        /// <summary>
        /// Apply a DoT tick to all enemies within range of a corpse effect zone.
        /// </summary>
        private void ApplyDoTTick(int zoneId)
        {
            float cx = _store.CorpseEffectX[zoneId];
            float cy = _store.CorpseEffectY[zoneId];
            float radius = _store.CorpseEffectRadius[zoneId];
            float damage = _store.CorpseEffectDamagePerTick[zoneId];
            int effectType = _store.CorpseEffectType[zoneId];

            var enemies = _store.GetCachedActiveEnemyIds();
            foreach (int enemyId in enemies)
            {
                if (!_store.EnemyActive[enemyId]) continue;

                float dx = _store.PositionX[enemyId] - cx;
                float dy = _store.PositionY[enemyId] - cy;
                float distSq = dx * dx + dy * dy;
                if (distSq <= radius * radius)
                {
                    // Apply DoT via BuffSystem
                    // effectType 0 = Poison, 3 = Fire, 6 = HallowedGround, 7 = ThornyBramble
                    if (_buffSystem != null)
                    {
                        _ = effectType; // keep switch-like intent explicit
                        // Build a properly-initialized Periodic DoT (legacy ApplyDot(int,float,int)
                        // overload leaves TicksRemaining=0 so it never ticks). Use the full
                        // GameplayEffectDef.Periodic factory which initializes TicksRemaining.
                        var dotDef = GameplayEffectDef.Periodic(
                            name: $"corpse_zone_tick_{effectType}",
                            attrIdx: AttributeSetDefinitions.ENEMY_HEALTH,
                            damagePerTick: damage,
                            totalDuration: 1f,             // 1s — one tick per zone pulse
                            tickInterval: 1f
                        );
                        _buffSystem.ApplyDot(enemyId, dotDef);
                    }
                }
            }
        }

        /// <summary>
        /// Apply continuous effects (slow, ice) to enemies within range each frame.
        /// Ice (type 2) applies a brief stun/slow; Slow (type 1) reduces speed.
        /// </summary>
        private void ApplyContinuousEffect(int zoneId)
        {
            float cx = _store.CorpseEffectX[zoneId];
            float cy = _store.CorpseEffectY[zoneId];
            float radius = _store.CorpseEffectRadius[zoneId];
            int effectType = _store.CorpseEffectType[zoneId];
            float slowAmount = _store.CorpseEffectSlowAmount[zoneId];

            if (effectType != 1 && effectType != 2 && effectType != 7) return; // Slow, Ice, ThornyBramble need per-frame

            var enemies = _store.GetCachedActiveEnemyIds();
            foreach (int enemyId in enemies)
            {
                if (!_store.EnemyActive[enemyId]) continue;

                float dx = _store.PositionX[enemyId] - cx;
                float dy = _store.PositionY[enemyId] - cy;
                float distSq = dx * dx + dy * dy;
                if (distSq > radius * radius) continue;

                if (effectType == 1) // Slow
                {
                    // Apply slow if stronger than existing
                    float existingSlow = _store.EnemyTerrainMoveSpeedMult[enemyId];
                    if (slowAmount < existingSlow)
                    {
                        _store.EnemyTerrainMoveSpeedMult[enemyId] = slowAmount;
                    }
                }
                else if (effectType == 2) // Ice — brief stun/slow
                {
                    // Ice applies a brief stun (handled via EnemyStunDurationLeft in the movement system)
                    // For simplicity, we just slow them significantly
                    float existingSlow = _store.EnemyTerrainMoveSpeedMult[enemyId];
                    float iceSlow = 0.2f; // 80% slow
                    if (iceSlow < existingSlow)
                    {
                        _store.EnemyTerrainMoveSpeedMult[enemyId] = iceSlow;
                    }
                }
                else if (effectType == 7) // ThornyBramble — DoT + slow combo
                {
                    // Same slow application rule as type 1 (Slow): use the slowAmount from JSON
                    float existingSlow = _store.EnemyTerrainMoveSpeedMult[enemyId];
                    if (slowAmount < existingSlow)
                    {
                        _store.EnemyTerrainMoveSpeedMult[enemyId] = slowAmount;
                    }
                }
            }
        }

        /// <summary>
        /// Count of active corpse effects.
        /// </summary>
        public int ActiveCorpseEffectCount
        {
            get
            {
                var ids = _store.GetCachedActiveCorpseEffectIds();
                int count = 0;
                foreach (int id in ids)
                    if (_store.CorpseEffectActive[id]) count++;
                return count;
            }
        }
    }
}