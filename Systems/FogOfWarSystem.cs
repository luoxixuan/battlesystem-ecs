using System;
using System.Collections.Generic;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Fog of War / Vision System — controls which enemies are visible to which towers.
    /// 
    /// Design:
    /// - Each tower has a vision radius. Only enemies within that radius are visible.
    /// - GlobalFogDensity scales all vision radii (weather/night effects).
    /// - Enemy visibility is computed per-frame based on distance from each tower.
    /// - TowerAttackSystem filters out invisible enemies during target acquisition.
    /// 
    /// Performance: O(towers * enemies_in_range) per frame — limited by spatial grid range queries.
    /// Uses Dictionary[towerId] -> bool[enemyId] to avoid 10B-entry flat array.
    /// </summary>
    public class FogOfWarSystem
    {
        private ComponentStore store;
        private List<int> _workList = new List<int>();
        private HashSet<int> _towersWithFog = new HashSet<int>(); // track which towers have fog enabled

        public FogOfWarSystem(ComponentStore store)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>
        /// Called at the start of each frame (after spatial grid rebuild, before TowerAttack).
        /// Recomputes visibility for all active towers.
        /// </summary>
        public void SetTurn()
        {
            // No-op: all state is recomputed in Update()
        }

        /// <summary>
        /// Update visibility: for each active tower, compute which enemies are in vision range.
        /// Stores results in TowerVisibilityByTower (Dictionary[towerId, bool[enemyId]]).
        /// </summary>
        public void Update()
        {
            var activeTowers = store.ActiveTowerIds;
            float globalFogMult = store.GlobalFogDensity[0]; // player 0 for now

            // Pre-scan: identify which towers now have fog (VisionRadius > 0)
            // Add new ones, remove ones that lost fog
            HashSet<int> currentFogTowers = new HashSet<int>();
            for (int ti = 0; ti < activeTowers.Count; ti++)
            {
                int towerId = activeTowers[ti];
                float visionRadius = store.TowerVisionRadius[towerId];
                if (visionRadius > 0f)
                    currentFogTowers.Add(towerId);
            }

            // Remove visibility data for towers that no longer have fog
            _towersWithFog.RemoveWhere(tid => !currentFogTowers.Contains(tid));

            // Process each fog-enabled tower
            foreach (int towerId in currentFogTowers)
            {
                float visionRadius = store.TowerVisionRadius[towerId];
                float effectiveRadius = visionRadius * globalFogMult;

                float tx = store.PositionX[towerId];
                float ty = store.PositionY[towerId];

                // Get or create visibility array for this tower
                if (!store.TowerVisibilityByTower.TryGetValue(towerId, out bool[] visArray) || visArray == null)
                {
                    visArray = new bool[ComponentStore.MAX_ENTITIES];
                    store.TowerVisibilityByTower[towerId] = visArray;
                }

                // Query spatial grid for enemies in range
                _workList.Clear();
                store.SpatialGrid.GetEnemiesInRange(store, tx, ty, (int)Math.Ceiling(effectiveRadius), _workList);

                // Reset visibility for enemies that were in range
                for (int wi = 0; wi < _workList.Count; wi++)
                {
                    int enemyId = _workList[wi];
                    if (enemyId >= 0 && enemyId < ComponentStore.MAX_ENTITIES)
                        visArray[enemyId] = false;
                }

                // Mark enemies within effective radius as visible
                for (int wi = 0; wi < _workList.Count; wi++)
                {
                    int enemyId = _workList[wi];
                    if (enemyId < 0 || enemyId >= ComponentStore.MAX_ENTITIES)
                        continue;

                    float ex = store.PositionX[enemyId];
                    float ey = store.PositionY[enemyId];

                    float dx = ex - tx;
                    float dy = ey - ty;
                    float dist = (float)Math.Sqrt(dx * dx + dy * dy);

                    if (dist <= effectiveRadius)
                    {
                        visArray[enemyId] = true;
                    }
                }

                _towersWithFog.Add(towerId);
            }
        }

        /// <summary>
        /// Query whether a specific enemy is visible to a specific tower.
        /// Returns true if no fog (towerVisionRadius <= 0) or enemy is within range.
        /// </summary>
        public bool IsEnemyVisibleToTower(int enemyId, int towerId)
        {
            float visionRadius = store.TowerVisionRadius[towerId];
            if (visionRadius <= 0f)
                return true; // no fog for this tower

            float fogMult = store.GlobalFogDensity[0];
            float effectiveRadius = visionRadius * fogMult;

            float dx = store.PositionX[enemyId] - store.PositionX[towerId];
            float dy = store.PositionY[enemyId] - store.PositionY[towerId];
            float dist = (float)Math.Sqrt(dx * dx + dy * dy);

            return dist <= effectiveRadius;
        }
    }
}