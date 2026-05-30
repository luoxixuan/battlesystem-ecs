using System;
using System.Collections.Generic;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Tower Sabotage System — handles enemy EMP/sabotage abilities that disable towers.
    /// 
    /// Sabotage logic:
    /// - Enemies with EnemyCanSabotage=true periodically apply AoE disable to nearby towers
    /// - Disabled towers cannot attack (TowerIsDisabled=true, skipped in TowerAttackSystem)
    /// - Timer counts down; when it reaches 0 the tower re-enables automatically
    /// 
    /// Execution point: CombatGroup, before TowerAttack (so disabled towers skip their attack)
    /// </summary>
    public class TowerSabotageSystem
    {
        private ComponentStore store;

        public TowerSabotageSystem(ComponentStore store)
        {
            this.store = store;
        }

        public void SetTurn()
        {
            // Nothing to cache per-turn for this system (stateless across turns)
        }

        /// <summary>
        /// Update sabotage timers and apply disable effects from sabotage-capable enemies.
        /// Called once per frame from CombatGroup.
        /// </summary>
        public void Update(float deltaTime)
        {
            // Phase 1: Decrement disable timers on disabled towers and re-enable expired ones
            DecrementDisableTimers(deltaTime);

            // Phase 2: Sabotage-capable enemies apply disable to nearby towers
            ApplySabotageEffects(deltaTime);
        }

        private void DecrementDisableTimers(float deltaTime)
        {
            var activeTowerIds = store.ActiveTowerIds;
            for (int i = 0; i < activeTowerIds.Count; i++)
            {
                int towerId = activeTowerIds[i];
                if (!store.TowerIsDisabled[towerId]) continue;

                store.TowerDisabledTimer[towerId] -= deltaTime;
                if (store.TowerDisabledTimer[towerId] <= 0f)
                {
                    // Disable expired — re-enable the tower
                    store.TowerIsDisabled[towerId] = false;
                    store.TowerDisabledTimer[towerId] = 0f;
                    // TowerDisabledDuration is preserved for reference
                }
            }
        }

        private void ApplySabotageEffects(float deltaTime)
        {
            var activeEnemyIds = store.ActiveEnemyIds;
            for (int i = 0; i < activeEnemyIds.Count; i++)
            {
                int enemyId = activeEnemyIds[i];
                if (!store.EnemyCanSabotage[enemyId]) continue;

                // Decrement sabotage cooldown timer
                if (store.EnemySabotageTimer[enemyId] > 0f)
                {
                    store.EnemySabotageTimer[enemyId] -= deltaTime;
                    continue; // Still on cooldown
                }

                // Timer expired — ready to sabotage
                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];
                float radius = store.EnemySabotageRadius[enemyId];
                float duration = store.EnemySabotageDuration[enemyId];

                // Find all towers within sabotage radius and disable them
                var activeTowerIds = store.ActiveTowerIds;
                for (int j = 0; j < activeTowerIds.Count; j++)
                {
                    int towerId = activeTowerIds[j];
                    // Skip towers that are already disabled (don't reset their timer)
                    if (store.TowerIsDisabled[towerId]) continue;
                    // Skip towers that are invulnerable during construction
                    if (store.TowerIsConstructing[towerId] && !store.TowerIsVulnerableDuringConstruction[towerId]) continue;

                    float tx = store.PositionX[towerId];
                    float ty = store.PositionY[towerId];
                    float dx = tx - enemyX;
                    float dy = ty - enemyY;
                    float distSq = dx * dx + dy * dy;
                    float radiusSq = radius * radius;

                    if (distSq <= radiusSq)
                    {
                        // Disable this tower
                        store.TowerIsDisabled[towerId] = true;
                        store.TowerDisabledTimer[towerId] = duration;
                        store.TowerDisabledDuration[towerId] = duration;
                    }
                }

                // Reset cooldown for next sabotage attack
                store.EnemySabotageTimer[enemyId] = store.EnemySabotageCooldown[enemyId];
            }
        }

        /// <summary>
        /// Manually repair a tower (player-initiated, e.g. via build menu).
        /// Immediately clears the disabled state.
        /// </summary>
        public void RepairTower(int towerId)
        {
            if (!ComponentStore.IsValidEntity(towerId)) return;
            store.TowerIsDisabled[towerId] = false;
            store.TowerDisabledTimer[towerId] = 0f;
        }

        /// <summary>
        /// Check if a tower is currently disabled.
        /// </summary>
        public bool IsDisabled(int towerId)
        {
            return store.TowerIsDisabled[towerId];
        }
    }
}