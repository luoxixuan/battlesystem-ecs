using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Components;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Pull Tower System — applies gravitational pull to nearby enemies, dragging them toward the tower.
    /// Two-phase: serial collection of pull events, serial application of position displacement.
    /// Only towers marked as IsPullTower participate in the pull logic.
    /// Pulled enemies have their positionY moved toward the tower each frame.
    /// EnemyMovementSystem reads EnemyIsBeingPulled and applies the pull offset.
    /// </summary>
    public class PullTowerSystem
    {
        private ComponentStore store;
        private List<int> _pullTowerIds;

        public PullTowerSystem(ComponentStore store)
        {
            this.store = store;
            _pullTowerIds = new List<int>(64);
        }

        /// <summary>
        /// Collect all pull tower IDs. Called after SpatialGrid is rebuilt.
        /// </summary>
        public void SetTurn()
        {
            _pullTowerIds.Clear();
            var activeTowerIds = store.ActiveTowerIds;
            for (int i = 0; i < activeTowerIds.Count; i++)
            {
                int towerId = activeTowerIds[i];
                if (store.TowerIsPullTower[towerId])
                    _pullTowerIds.Add(towerId);
            }
        }

        /// <summary>
        /// Apply gravitational pull from all pull towers to nearby enemies.
        /// Pull is applied as a position offset toward the tower center.
        /// Runs in serial phase after movement, before damage resolution.
        /// 
        /// Strategy: for each pull tower, scan enemies in range and apply pull.
        /// Pull is velocity-based: each frame, enemy Y position is adjusted toward tower.
        /// The actual position modification is deferred to EnemyMovementSystem which
        /// reads EnemyIsBeingPulled and applies the pull offset per-frame.
        /// </summary>
        public void Update(float deltaTime)
        {
            if (_pullTowerIds.Count == 0) return;

            // Decrement cooldown timers and apply pull for each active pull tower
            for (int i = 0; i < _pullTowerIds.Count; i++)
            {
                int towerId = _pullTowerIds[i];
                float pullRadius = store.TowerPullRadius[towerId];
                if (pullRadius <= 0f) continue;

                float pullStrength = store.TowerPullStrength[towerId];
                if (pullStrength <= 0f) continue;

                // Handle cooldown: skip if timer > 0
                float cooldown = store.TowerPullCooldown[towerId];
                float timer = store.TowerPullTimer[towerId];
                if (cooldown > 0f && timer > 0f)
                {
                    // Decrement timer
                    store.TowerPullTimer[towerId] = timer - deltaTime;
                    continue; // Pull not active this frame
                }

                // Reset timer if it expired (for pulse-based pull)
                if (cooldown > 0f && timer <= 0f)
                    store.TowerPullTimer[towerId] = cooldown;

                float towerX = store.PositionX[towerId];
                float towerY = store.PositionY[towerId];
                int pullRadiusSq = (int)(pullRadius * pullRadius);

                // Scan all active enemies
                var enemyIds = store.ActiveEnemyIds;
                int enemyCount = enemyIds.Count;
                for (int e = 0; e < enemyCount; e++)
                {
                    int enemyId = enemyIds[e];
                    if (!store.EnemyActive[enemyId]) continue;

                    float ex = store.PositionX[enemyId];
                    float ey = store.PositionY[enemyId];

                    float dx = ex - towerX;
                    float dy = ey - towerY;
                    int distSq = (int)(dx * dx + dy * dy);

                    if (distSq <= pullRadiusSq && distSq > 0)
                    {
                        // Mark enemy as being pulled (EnemyMovementSystem reads this)
                        store.EnemyIsBeingPulled[enemyId] = true;
                    }
                }
            }
        }

        /// <summary>
        /// Clear pull flag for all enemies at the end of the frame.
        /// Called once per frame after all pull effects are resolved.
        /// </summary>
        public void ClearPullFlags()
        {
            var enemyIds = store.ActiveEnemyIds;
            for (int i = 0; i < enemyIds.Count; i++)
            {
                store.EnemyIsBeingPulled[enemyIds[i]] = false;
            }
        }
    }
}