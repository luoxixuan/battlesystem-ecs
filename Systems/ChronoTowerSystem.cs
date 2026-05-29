using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Components;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Chrono Tower System — applies local time dilation fields from Chrono Towers.
    /// Each Chrono Tower creates a field that slows all enemies within its radius.
    /// Multiple chrono fields take the minimum (slowest) time scale.
    /// Two-phase: parallel collection of chrono tower effects, serial application via minimum accumulation.
    /// </summary>
    public class ChronoTowerSystem
    {
        private ComponentStore store;
        private List<int> _chronoTowerIds;

        public ChronoTowerSystem(ComponentStore store)
        {
            this.store = store;
            _chronoTowerIds = new List<int>(64);
        }

        /// <summary>
        /// Collect all chrono tower IDs. Called after SpatialGrid is rebuilt (Phase 5).
        /// </summary>
        public void SetTurn()
        {
            _chronoTowerIds.Clear();
            var activeTowerIds = store.ActiveTowerIds;
            for (int i = 0; i < activeTowerIds.Count; i++)
            {
                int towerId = activeTowerIds[i];
                if (store.TowerIsChronoTower[towerId])
                    _chronoTowerIds.Add(towerId);
            }
        }

        /// <summary>
        /// Apply time scale to all enemies within each Chrono Tower's field.
        /// Called in serial phase after SetTurn(). Accumulates minimum time scale per enemy
        /// (multiple chrono towers = take the slowest / minimum scale).
        /// </summary>
        public void Update()
        {
            if (_chronoTowerIds.Count == 0) return;

            var enemyIds = store.ActiveEnemyIds;
            int enemyCount = enemyIds.Count;
            if (enemyCount == 0) return;

            // For each chrono tower, apply its time field to all enemies in range
            for (int t = 0; t < _chronoTowerIds.Count; t++)
            {
                int towerId = _chronoTowerIds[t];
                if (!store.TowerActive[towerId]) continue;

                float fieldRadius = store.TowerTimeFieldRadius[towerId];
                if (fieldRadius <= 0f) continue;

                float timeScale = store.TowerTimeScale[towerId];
                if (timeScale >= 1f) continue; // no point applying >= 1 (already normal/faster)

                float towerX = store.PositionX[towerId];
                float towerY = store.PositionY[towerId];

                // Iterate enemies: check if within radius, accumulate minimum time scale
                for (int i = 0; i < enemyCount; i++)
                {
                    int enemyId = enemyIds[i];
                    if (!store.EnemyActive[enemyId]) continue;

                    float dx = store.PositionX[enemyId] - towerX;
                    float dy = store.PositionY[enemyId] - towerY;
                    float distSq = dx * dx + dy * dy;

                    if (distSq <= fieldRadius * fieldRadius)
                    {
                        // Take the minimum (slowest) time scale across all chrono towers
                        if (timeScale < store.EnemyTimeScale[enemyId])
                            store.EnemyTimeScale[enemyId] = timeScale;
                    }
                }
            }
        }
    }
}
