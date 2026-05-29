using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Manages placeable obstacles (barricades, ice walls, spike traps).
    /// Handles obstacle lifecycle, trap damage, and enemy-obstacle interaction.
    /// </summary>
    public class ObstacleSystem
    {
        private ComponentStore store;
        private GameConfig gameConfig;
        private IRenderer logger;

        // Simple ID allocator — linear scan from last used ID
        private int _nextObstacleId = 0;

        // Damage queue for trap damage (processed at frame end)
        private List<(int enemyId, float damage, int playerId)> _trapDamageQueue = new List<(int, float, int)>();

        public ObstacleSystem(ComponentStore store, GameConfig gameConfig, IRenderer logger)
        {
            this.store = store;
            this.gameConfig = gameConfig;
            this.logger = logger;
        }

        /// <summary>
        /// Place an obstacle of the given type at (gridX, gridY).
        /// Returns obstacleId, or -1 on failure.
        /// </summary>
        public int PlaceObstacle(int typeId, int gridX, int gridY, int playerId)
        {
            if (typeId < 0 || typeId >= gameConfig.ObstacleDefs.Length)
                return -1;

            var def = gameConfig.ObstacleDefs[typeId];

            // Find next available obstacle ID
            int attempts = 0;
            int startId = _nextObstacleId;
            while (attempts < ComponentStore.MAX_OBSTACLES)
            {
                if (!store.ObstacleActive[_nextObstacleId])
                    break;
                _nextObstacleId = (_nextObstacleId + 1) % ComponentStore.MAX_OBSTACLES;
                attempts++;
            }
            if (attempts >= ComponentStore.MAX_OBSTACLES)
                return -1;

            int obstacleId = _nextObstacleId;
            store.AddObstacle(obstacleId, typeId, gridX, gridY, def.MaxHealth);
            _nextObstacleId = (_nextObstacleId + 1) % ComponentStore.MAX_OBSTACLES;
            return obstacleId;
        }

        /// <summary>
        /// Remove an existing obstacle.
        /// </summary>
        public void RemoveObstacle(int obstacleId)
        {
            if (obstacleId < 0 || obstacleId >= ComponentStore.MAX_OBSTACLES)
                return;
            if (!store.ObstacleActive[obstacleId])
                return;
            store.RemoveObstacle(obstacleId);
        }

        /// <summary>
        /// Per-frame update: apply trap damage to enemies standing on spike traps.
        /// </summary>
        public void Update(float deltaTime)
        {
            _trapDamageQueue.Clear();

            // Fast path: no obstacles placed
            if (store.ActiveObstacleIds.Count == 0)
                return;

            var activeEnemies = store.GetCachedActiveEnemyIds();
            foreach (int enemyId in activeEnemies)
            {
                if (!store.EnemyActive[enemyId])
                    continue;

                // Flying enemies ignore obstacle traps (they fly over barricades/spike traps)
                if (store.EnemyIsFlying[enemyId])
                    continue;

                int enemyGridX = (int)store.PositionX[enemyId];
                int enemyGridY = (int)store.PositionY[enemyId];

                // Check all active obstacles for overlap with this enemy
                foreach (int obstacleId in store.ActiveObstacleIds)
                {
                    if (!store.ObstacleActive[obstacleId])
                        continue;

                    int obsX = (int)store.ObstacleX[obstacleId];
                    int obsY = (int)store.ObstacleY[obstacleId];

                    // Simple cell occupation check
                    if (enemyGridX != obsX || enemyGridY != obsY)
                        continue;

                    int typeId = store.ObstacleType[obstacleId];
                    if (typeId < 0 || typeId >= gameConfig.ObstacleDefs.Length)
                        continue;

                    var def = gameConfig.ObstacleDefs[typeId];
                    if (def.TrapDamage > 0f)
                    {
                        // Queue trap damage for serial processing
                        _trapDamageQueue.Add((enemyId, def.TrapDamage, 0));
                    }
                }
            }

            // Apply trap damage serially
            foreach (var (enemyId, damage, playerId) in _trapDamageQueue)
            {
                if (!store.EnemyActive[enemyId])
                    continue;
                store.EnemyHealth[enemyId] -= damage;
                if (store.EnemyHealth[enemyId] <= 0f)
                {
                    store.QueueEnemyDeath(enemyId, playerId);
                }
            }
        }

        /// <summary>
        /// Called at start of each turn to clear dead obstacles.
        /// </summary>
        public void SetTurn(int turn)
        {
            // Remove destroyed obstacles
            var toRemove = new List<int>();
            foreach (int obstacleId in store.ActiveObstacleIds)
            {
                if (!store.ObstacleActive[obstacleId])
                    continue;
                if (store.ObstacleHealth[obstacleId] <= 0f)
                    toRemove.Add(obstacleId);
            }
            foreach (int id in toRemove)
                store.RemoveObstacle(id);
        }
    }
}
