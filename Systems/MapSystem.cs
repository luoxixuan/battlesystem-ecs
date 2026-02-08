using System;
using BattleSystemECS.Components;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    public class MapSystem
    {
        private IRenderer renderer;
        private int mapWidth = 10;
        private int mapHeight = 50;

        public MapSystem(IRenderer renderer)
        {
            this.renderer = renderer;
        }

        public void Update(EntityManager entityManager)
        {
            RenderMap(entityManager);
        }

        public void RenderMap(EntityManager entityManager)
        {
            renderer.Log($"[MAP] {mapWidth}x{mapHeight} map");
            renderer.Log("[MAP] P = Player, E = Enemy, . = Empty");

            for (int y = mapHeight - 1; y >= 0; y--)
            {
                string row = "";
                for (int x = 0; x < mapWidth; x++)
                {
                    bool hasPlayer = false;
                    bool hasEnemy = false;

                    var entities = entityManager.GetAllEntities();
                    foreach (var entity in entities)
                    {
                        // Check player
                        if (entityManager.HasComponent<PlayerComponent>(entity))
                        {
                            var pos = entityManager.GetComponent<PositionComponent>(entity);
                            if (entityManager.HasComponent<PositionComponent>(entity) && Math.Abs(pos.X - x) < 0.5f && Math.Abs(pos.Y - y) < 0.5f)
                            {
                                hasPlayer = true;
                                break;
                            }
                        }
                        // Check enemy
                        else if (entityManager.HasComponent<EnemyComponent>(entity))
                        {
                            var pos = entityManager.GetComponent<PositionComponent>(entity);
                            var enemyHealth = entityManager.GetComponent<EnemyComponent>(entity);
                            if (entityManager.HasComponent<PositionComponent>(entity) && enemyHealth.Health > 0f)
                            {
                                if (Math.Abs(pos.X - x) < 0.5f && Math.Abs(pos.Y - y) < 0.5f)
                                {
                                    hasEnemy = true;
                                    break;
                                }
                            }
                        }
                    }

                    if (hasPlayer)
                        row += "P ";
                    else if (hasEnemy)
                        row += "E ";
                    else
                        row += ". ";
                }
                Console.WriteLine("[MAP] " + row);
            }
        }

        public void SetMapSize(int width, int height)
        {
            this.mapWidth = width;
            this.mapHeight = height;
        }
    }
}
