using System;
using System.Collections.Concurrent;
using System.Threading;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Components;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Enemy Morph System — implements mid-wave transformation for morph-capable enemies.
    ///
    /// Design:
    /// - Update() runs every frame during WavePhase (after BeginFrame, before combat resolution)
    /// - Checks each active enemy with a valid morphDefId for trigger conditions
    /// - For HP_THRESHOLD: fires when EnemyHealth / EnemyMaxHealth <= TriggerValue
    /// - For TIME: fires when EnemyAgeSeconds >= TriggerValue (future)
    /// - When triggered: applies stat multipliers in-place, marks EnemyIsMorphed = true
    /// - Morph is one-way (EnemyIsMorphed prevents re-triggering)
    ///
    /// Two-phase pattern:
    /// - Phase 1 (Update): scan all active enemies, identify triggers, collect morph events
    /// - Phase 2 (serial apply): apply stat multipliers in-place, update TypeName
    ///
    /// Stat multipliers multiply current stats (not max stats). Enemy does NOT heal on morph —
    /// this is a transformation, not a restoration.
    /// </summary>
    public class EnemyMorphSystem
    {
        private readonly ComponentStore store;
        private readonly GameConfig gameConfig;
        private readonly IRenderer renderer;

        // Morph events: processed serially to avoid concurrent writes to ComponentStore
        private readonly ConcurrentBag<(int enemyId, int morphDefId)> _morphQueue =
            new ConcurrentBag<(int, int)>();

        public EnemyMorphSystem(ComponentStore store, GameConfig gameConfig, IRenderer renderer)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.gameConfig = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
            this.renderer = renderer;
        }

        /// <summary>
        /// Called once per frame during WavePhase.
        /// Scans all active enemies for morph trigger conditions and queues morph events.
        /// </summary>
        public void Update(float deltaTime)
        {
            var active = store.ActiveEnemyIds;
            int count = active.Count;

            for (int i = 0; i < count; i++)
            {
                int enemyId = active[i];
                if (!store.PositionActive[enemyId]) continue;

                // Already morphed — skip
                if (store.EnemyIsMorphed[enemyId]) continue;

                int morphDefId = store.EnemyMorphDefId[enemyId];
                if (morphDefId < 0) continue;

                // Bounds check
                if (morphDefId >= gameConfig.MorphDefs.Length) continue;

                MorphDef def = gameConfig.MorphDefs[morphDefId];
                if (def == null) continue;

                // Check trigger condition
                bool shouldMorph = false;

                if (def.TriggerType == "HP_THRESHOLD")
                {
                    float currentHealth = store.EnemyHealth[enemyId];
                    float maxHealth = store.EnemyMaxHealth[enemyId];
                    if (maxHealth > 0f)
                    {
                        float healthFraction = currentHealth / maxHealth;
                        shouldMorph = healthFraction <= def.TriggerValue;
                    }
                }
                else if (def.TriggerType == "TIME")
                {
                    // TIME trigger not yet implemented — no EnemyAgeSeconds tracking in current codebase
                    // When age tracking is added, uncomment below:
                    // float age = store.EnemyAgeSeconds[enemyId];
                    // shouldMorph = age >= def.TriggerValue;
                }

                if (shouldMorph)
                {
                    _morphQueue.Add((enemyId, morphDefId));
                }
            }

            // Apply all queued morphs serially
            while (_morphQueue.TryTake(out var morphEvent))
            {
                ResolveMorph(morphEvent.enemyId, morphEvent.morphDefId);
            }
        }

        /// <summary>
        /// Apply morph transformation to a single enemy.
        /// Called serially from Update() loop.
        /// </summary>
        private void ResolveMorph(int enemyId, int morphDefId)
        {
            MorphDef def = gameConfig.MorphDefs[morphDefId];
            if (def == null) return;

            // Read current stats
            float currentHealth = store.EnemyHealth[enemyId];
            float maxHealth = store.EnemyMaxHealth[enemyId];
            float moveSpeed = store.EnemyMoveSpeed[enemyId];
            float damage = store.EnemyDamage[enemyId];
            string sourceType = store.EnemyTypeName[enemyId];

            // Apply stat multipliers (multiply current stats — NOT max health)
            // The enemy does NOT heal on morph; morph is a transformation, not restoration
            float newMoveSpeed = moveSpeed * def.SpeedMultOnMorph;
            float newDamage = damage * def.DamageMultOnMorph;
            // Current health scales with health mult (the health bar shrinks/grows proportionally)
            float newHealth = Math.Max(1f, currentHealth * def.HealthMultOnMorph);
            float newMaxHealth = Math.Max(1f, maxHealth * def.HealthMultOnMorph);

            store.EnemyMoveSpeed[enemyId] = newMoveSpeed;
            store.EnemyDamage[enemyId] = newDamage;
            store.SetEnemyResourceAuthority(enemyId, enemyId, new Core.GAS.AttributeKey(3), newHealth);
            store.SetEnemyResourceAuthority(enemyId, enemyId, new Core.GAS.AttributeKey(2), newMaxHealth);

            // Update type name to target monster type
            store.EnemyTypeName[enemyId] = def.TargetMonsterType;

            // Mark as morphed (one-way transformation)
            store.EnemyIsMorphed[enemyId] = true;
            store.EnemyMorphTriggered[enemyId] = false;

            // Re-initialize behavior tree to target monster type's BT (if different)
            store.EnemyBehaviorTree[enemyId] = gameConfig.GetCachedBehaviorTree(def.TargetMonsterType);

            // Log morph event (rate-limited to avoid spam)
            if (renderer != null)
            {
                renderer.Log($"[MORPH] {sourceType} → {def.TargetMonsterType} (id={enemyId}) at {newHealth:F0}/{newMaxHealth:F0} HP");
            }
        }
    }
}
