using System;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    public class WaveSpawningSystem
    {
        private EntityManager entityManager;
        private IRenderer renderer;
        private GameConfig gameConfig;
        private int currentWave = 1;
        private int currentLevel = 1;
        private int enemiesSpawnedInWave = 0;
        private int totalEnemiesSpawned = 0;

        public WaveSpawningSystem(EntityManager entityManager, IRenderer renderer, GameConfig gameConfig)
        {
            this.entityManager = entityManager;
            this.renderer = renderer;
            this.gameConfig = gameConfig;
        }

        public void SetLevel(int levelNumber)
        {
            currentLevel = levelNumber;
            currentWave = 1;
            enemiesSpawnedInWave = 0;
            totalEnemiesSpawned = 0;
        }

        public void Update()
        {
            var levelConfig = gameConfig.GetLevelConfig(currentLevel);
            if (levelConfig == null)
            {
                renderer.Log("[SPAWN] Level " + currentLevel + " not found!");
                return;
            }

            if (currentWave > levelConfig.WaveCount)
            {
                renderer.Log("[SPAWN] Level " + currentLevel + " complete!");
                return;
            }

            var waveConfig = levelConfig.Waves[currentWave - 1];
            if (waveConfig == null)
            {
                renderer.Log("[SPAWN] Wave " + currentWave + " not found!");
                return;
            }

            if (enemiesSpawnedInWave < waveConfig.EnemyCount)
            {
                // Spawn enemy
                var monsterConfig = gameConfig.GetMonsterConfig(waveConfig.MonsterType);
                if (monsterConfig == null)
                {
                    renderer.Log("[SPAWN] Monster type '" + waveConfig.MonsterType + "' not found!");
                    return;
                }

                var enemyEntity = entityManager.CreateEntity();
                string enemyName = waveConfig.MonsterType + "L" + currentLevel + "W" + currentWave + "E" + enemiesSpawnedInWave;
                entityManager.SetName(enemyEntity, enemyName);

                Random random = new Random();
                float startX = (float)random.Next(0, 10);
                float startY = 49f;

                entityManager.AddComponent(enemyEntity, new PositionComponent(startX, startY));

                entityManager.AddComponent(enemyEntity, new EnemyComponent
                {
                    MoveSpeed = monsterConfig.MoveSpeed,
                    Health = monsterConfig.Health,
                    MaxHealth = monsterConfig.MaxHealth,
                    Damage = monsterConfig.Damage,
                    GoldReward = monsterConfig.GoldReward,
                    WaveNumber = currentWave
                });

                enemiesSpawnedInWave++;
                totalEnemiesSpawned++;

                renderer.Log("[SPAWN] Spawned " + enemyName + " at x=" + startX + ", y=" + startY);
            }
            else
            {
                // Wave complete
                renderer.Log("[SPAWN] Wave " + currentWave + " complete! Spawned " + enemiesSpawnedInWave + " enemies");
                enemiesSpawnedInWave = 0;
                currentWave++;

                if (currentWave > levelConfig.WaveCount)
                {
                    renderer.Log("[SPAWN] Level " + currentLevel + " complete! Total enemies spawned: " + totalEnemiesSpawned);
                currentWave = 1;
                    totalEnemiesSpawned = 0;
                    currentLevel++;
                }
            }
        }
    }
}
