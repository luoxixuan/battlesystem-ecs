#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Enemy Healer System — manages continuous heal-over-time for enemy healer units.
    /// 
    /// Unlike EnemyAbilitySystem's ExecuteHealAllies (cooldown-triggered AoE heal),
    /// EnemyHealerSystem runs every frame with a per-enemy cooldown timer, providing
    /// sustained healing to nearby wounded allies.
    /// 
    /// Design: two-phase (parallel collection + serial apply) for thread safety.
    /// Phase: inserted after EnemyMovement (Phase 3), before combat systems.
    /// </summary>
    public class EnemyHealerSystem
    {
        private readonly ComponentStore _store;
        private readonly IRenderer _logger;
        private readonly GameConfig _gameConfig;

        // Per-enemy cooldown timers (indexed by enemyId)
        private readonly float[] _healCooldownTimers;

        public EnemyHealerSystem(ComponentStore store, IRenderer logger, GameConfig gameConfig)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _gameConfig = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
            _healCooldownTimers = new float[ComponentStore.MAX_ENTITIES];
        }

        public void SetTurn(int turn)
        {
            // No per-turn reset needed — cooldown timers persist across turns
        }

        public void Update(float deltaTime)
        {
            var activeIds = _store.GetCachedActiveEnemyIds();
            if (activeIds.Count == 0) return;

            // ── Phase 1: Parallel — collect heal events (thread-local) ──────────
            var healEvents = new List<(int healerId, int allyId, float healAmount)>();

            foreach (var healerId in activeIds)
            {
                if (!_store.EnemyActive[healerId]) continue;
                if (_store.EnemyHealerHealAmount[healerId] <= 0f) continue; // not a healer

                float cooldown = _healCooldownTimers[healerId];
                if (cooldown > 0f)
                {
                    _healCooldownTimers[healerId] -= deltaTime;
                    continue;
                }

                float healX = _store.PositionX[healerId];
                float healY = _store.PositionY[healerId];
                float healRange = _store.EnemyHealerHealInterval[healerId];
                float healAmount = _store.EnemyHealerHealAmount[healerId];
                if (healAmount <= 0f || healRange <= 0f) continue;

                // Priority: lowest health wounded allies within range
                int? bestAllyId = null;
                float bestHealthFrac = float.MaxValue;

                foreach (var allyId in activeIds)
                {
                    if (!_store.EnemyActive[allyId]) continue;
                    if (allyId == healerId) continue;

                    float ax = _store.PositionX[allyId];
                    float ay = _store.PositionY[allyId];
                    float dist = Math.Abs(healX - ax) + Math.Abs(healY - ay);
                    if (dist > healRange) continue;

                    float maxHp = _store.EnemyMaxHealth[allyId];
                    if (maxHp <= 0f) continue;
                    float healthFrac = _store.EnemyHealth[allyId] / maxHp;
                    if (healthFrac >= 1f) continue; // fully healthy, skip
                    if (healthFrac < bestHealthFrac)
                    {
                        bestHealthFrac = healthFrac;
                        bestAllyId = allyId;
                    }
                }

                if (bestAllyId.HasValue)
                {
                    healEvents.Add((healerId, bestAllyId.Value, healAmount));
                    _healCooldownTimers[healerId] = healRange; // reuse interval field as cooldown
                }
            }

            // ── Phase 2: Serial — apply heal events ──────────────────────────────
            foreach (var (healerId, allyId, healAmount) in healEvents)
            {
                if (!_store.EnemyActive[allyId]) continue;
                float maxHp = _store.EnemyMaxHealth[allyId];
                float reduction = _store.EnemyHealingReduction[allyId];
                float effectiveHeal = reduction > 0f ? healAmount * (1f - reduction) : healAmount;
                float newHealth = Math.Min(_store.EnemyHealth[allyId] + effectiveHeal, maxHp);
                _store.EnemyHealth[allyId] = newHealth;
                _logger.Log($"[HEALER] Enemy {healerId} heals ally {allyId} for {healAmount:F1} HP (suppressed: {effectiveHeal:F1} by {reduction:P0}) ({newHealth:F1}/{maxHp:F1})");
            }
        }
    }
}