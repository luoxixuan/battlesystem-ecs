using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Terrain Tile Modifier System.
    /// Applies terrain effects (Mud slow, Ice slow, Lava DoT, HighGround range bonus) per enemy position.
    /// Serial-only — terrain effects are lightweight and don't need parallelism.
    /// </summary>
    public class TerrainSystem
    {
        private ComponentStore store;
        private int playerId;
        private GameConfig gameConfig;
        private BuffSystem buffSystem;

        private Dictionary<int, int> _lavaDotApplied = new Dictionary<int, int>();

        public TerrainSystem(ComponentStore store, int playerId, GameConfig gameConfig)
        {
            this.store = store;
            this.playerId = playerId;
            this.gameConfig = gameConfig;
        }

        public void SetBuffSystem(BuffSystem buffSystem)
        {
            this.buffSystem = buffSystem;
        }

        public void SetTurn()
        {
            var deadIds = new List<int>();
            foreach (var kvp in _lavaDotApplied)
            {
                if (!store.EnemyActive[kvp.Key])
                    deadIds.Add(kvp.Key);
            }
            foreach (var id in deadIds)
                _lavaDotApplied.Remove(id);
        }

        public void Update(float deltaTime)
        {
            var activeEnemies = store.GetCachedActiveEnemyIds();
            var terrainTypes = gameConfig.TerrainTypes;
            if (terrainTypes == null || terrainTypes.Count == 0) return;

            foreach (var enemyId in activeEnemies)
            {
                if (!store.EnemyActive[enemyId]) continue;

                // Flying enemies ignore terrain effects (mud, ice, lava)
                if (store.EnemyIsFlying[enemyId]) continue;

                float worldX = store.PositionX[enemyId];
                float worldY = store.PositionY[enemyId];
                int terrainId = store.GetTerrainAtPosition(worldX, worldY);

                if (terrainId < 0 || terrainId >= terrainTypes.Count) continue;

                var terrain = terrainTypes[terrainId];
                if (terrain == null) continue;

                // Mud (1) and Ice (2): apply slow
                if (terrainId == 1 || terrainId == 2)
                {
                    store.EnemyTerrainMoveSpeedMult[enemyId] = terrain.MoveSpeedMult;
                }
                else
                {
                    store.EnemyTerrainMoveSpeedMult[enemyId] = 1f;
                }

                // Lava (3): apply DoT
                if (terrainId == 3 && buffSystem != null && terrain.DotDamagePerTick > 0f && terrain.DotDuration > 0)
                {
                    if (!_lavaDotApplied.ContainsKey(enemyId))
                        _lavaDotApplied[enemyId] = 1;
                    buffSystem.ApplyDot(enemyId, terrain.DotDamagePerTick, terrain.DotDuration);
                }
            }
        }
    }
}
