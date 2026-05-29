using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Components;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Curse Aura System — applies range-based debuffs to nearby enemies.
    /// Two-phase: parallel collection of curse effects, serial application.
    /// Only towers marked as IsCurseTower participate in the curse logic.
    /// Curse effects accumulate additively when multiple curse towers overlap.
    /// </summary>
    public class CurseAuraSystem
    {
        private ComponentStore store;
        private List<int> _curseTowerIds;

        public CurseAuraSystem(ComponentStore store)
        {
            this.store = store;
            _curseTowerIds = new List<int>(64);
        }

        /// <summary>
        /// Collect all curse tower IDs. Called after SpatialGrid is rebuilt.
        /// </summary>
        public void SetTurn()
        {
            _curseTowerIds.Clear();
            var activeTowerIds = store.ActiveTowerIds;
            for (int i = 0; i < activeTowerIds.Count; i++)
            {
                int towerId = activeTowerIds[i];
                if (store.TowerIsCurseTower[towerId])
                    _curseTowerIds.Add(towerId);
            }
        }

        /// <summary>
        /// Resolve curse debuffs from all curse towers onto nearby enemies.
        /// Called in the serial phase after the parallel damage/debuff collection.
        /// Accumulates curse effects additively across all curse towers in range.
        /// </summary>
        public void ResolveCurseDebuffs()
        {
            if (_curseTowerIds.Count == 0) return;

            var enemyIds = store.ActiveEnemyIds;
            int enemyCount = enemyIds.Count;
            if (enemyCount == 0) return;

            // Temporary accumulation arrays — cleared each frame
            // We reuse small arrays per tower to avoid O(n_enemies * n_curse_towers) full scan
            // Strategy: for each curse tower, scan all enemies in range, accumulate into enemy arrays

            for (int ci = 0; ci < _curseTowerIds.Count; ci++)
            {
                int curseTowerId = _curseTowerIds[ci];
                float curseRadius = store.TowerCurseRadius[curseTowerId];
                if (curseRadius <= 0f) continue;

                float cx = store.PositionX[curseTowerId];
                float cy = store.PositionY[curseTowerId];
                int curseRadiusSq = (int)(curseRadius * curseRadius);

                float dmgReduction = store.TowerCurseDmgReduction[curseTowerId];
                float speedReduction = store.TowerCurseSpeedReduction[curseTowerId];
                float armorReduction = store.TowerCurseArmorReduction[curseTowerId];
                float dmgTakenIncrease = store.TowerCurseDmgTakenIncrease[curseTowerId];

                // Scan all active enemies — linear O(n_enemies) per curse tower
                // Acceptable: curse towers are rare (support role), and curse scan is serial (no parallelism needed)
                for (int ei = 0; ei < enemyCount; ei++)
                {
                    int enemyId = enemyIds[ei];
                    if (!store.EnemyActive[enemyId]) continue;

                    float ex = store.PositionX[enemyId];
                    float ey = store.PositionY[enemyId];
                    float dx = ex - cx;
                    float dy = ey - cy;
                    float distSq = dx * dx + dy * dy;
                    if (distSq > curseRadiusSq) continue;

                    // Accumulate curse effects additively
                    store.EnemyCurseDmgReduction[enemyId] += dmgReduction;
                    store.EnemyCurseSpeedReduction[enemyId] += speedReduction;
                    store.EnemyCurseArmorReduction[enemyId] += armorReduction;
                    store.EnemyCurseDmgTakenIncrease[enemyId] += dmgTakenIncrease;
                }
            }
        }
    }
}