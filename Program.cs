using System;
using BattleSystemECS.Components;
using BattleSystemECS.Systems;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("========================================");
            Console.WriteLine("     Roguelike Tower Defense - ECS");
            Console.WriteLine("========================================");
            Console.WriteLine();
            Console.WriteLine("Press any key to start...");

            try
            {
                Console.ReadKey();
            }
            catch
            {
                // Ignore key read errors when running in non-interactive mode
            }

            // Initialize ECS (SOA 架构）
            var store = new ComponentStore();
            var entityManager = new EntityManager(store);
            IRenderer logger = new ConsoleLogger();

            // Load configuration from JSON
            var gameConfig = GameConfigLoader.LoadConfig(logger);

            // Initialize systems (SOA 优化）
            var mapSystem = new MapSystem(logger, store);
            mapSystem.SetMapSize(10, 50);

            var playerTowerAttackSystem = new PlayerTowerAttackSystem(store, logger, 1, gameConfig);

            var enemyMovementSystem = new EnemyMovementSystem(store);

            var goldRewardSystem = new GoldRewardSystem(store, logger, 1);

            var waveSpawningSystem = new WaveSpawningSystem(store, logger, gameConfig);

            var upgradeSystem = new UpgradeSystem(store, logger, 1);

            // Create player (SOA)
            var playerEntity = entityManager.CreateEntity();
            entityManager.SetName(playerEntity, "Player");
            int playerId = playerEntity.Id;

            // SOA: 初始化玩家组件
            store.AddPosition(playerId, 5f, 0f);
            store.AddPlayer(playerId, 3f, 1f, 10f, 1);

            Console.WriteLine("[INFO] Player created! Position: x=5, y=0");
            logger.Log("[INFO]   - Attack Range: 3 grids");
            logger.Log("[INFO]   - Attack Interval: 1 seconds");
            logger.Log("[INFO]   - Attack Damage: 10 points");
            logger.Log("[INFO]   - Current Level: 1");
            logger.Log("[INFO]   - Upgrade Threshold: 100 gold");

            // Game level loop
            int currentLevel = 1;
            int maxLevels = gameConfig.Levels.Count;

            while (currentLevel <= maxLevels)
            {
                var levelConfig = gameConfig.GetLevelConfig(currentLevel);
                if (levelConfig == null)
                {
                    logger.Log("[INFO] [ERROR] Level " + currentLevel + " not found!");
                    currentLevel++;
                    continue;
                }

                Console.WriteLine();
                logger.Log("[INFO] ========== Level " + currentLevel + " ==========");
                logger.Log("[INFO] Total Waves: " + levelConfig.WaveCount);
                foreach (var wave in levelConfig.Waves)
                {
                    logger.Log("[INFO]   - Wave " + wave.WaveNumber + ": " + wave.MonsterType + ", " + wave.EnemyCount + " enemies");
                }
                logger.Log("[INFO] =======================================");

                // Set wave level
                waveSpawningSystem.SetLevel(currentLevel);

                // Render initial map (SOA)
                Console.WriteLine();
                logger.Log("========================================");
                mapSystem.Update();
                logger.Log("========================================");

                Console.WriteLine();
                logger.Log("Game Start!");

                // Game main loop
                bool gameRunning = true;
                int turn = 0;
                int maxTurns = 20;

                while (gameRunning && turn < maxTurns)
                {
                    turn++;
                    System.Threading.Thread.Sleep(1000);

                    logger.Log("[INFO] --- Turn " + turn + " ---");

                    // Spawn enemies for current wave (SOA)
                    waveSpawningSystem.Update();

                    // Move enemies (SOA)
                    enemyMovementSystem.Update();

                    // Player attack (SOA)
                    playerTowerAttackSystem.Update();

                    // Check upgrade (SOA)
                    goldRewardSystem.Update();

                    // Apply upgrade (SOA)
                    upgradeSystem.Update();

                    // Render map (SOA)
                    mapSystem.Update();

                    // Check enemies at bottom
                    if (CheckEnemiesAtBottom(store, logger))
                    {
                        gameRunning = false;
                        logger.Log("[INFO] Game Over! Enemy reached bottom.");
                    }
                }

                currentLevel++;
            }

            Console.WriteLine();
            logger.Log("Game Over! Completed " + (currentLevel - 1) + " levels.");
            Console.WriteLine();
        }

        private static bool CheckEnemiesAtBottom(ComponentStore store, IRenderer logger)
        {
            var activeEnemyIds = store.GetAllActiveEnemyIds();

            foreach (var enemyId in activeEnemyIds)
            {
                // SOA: 直接数组访问，无字典查询，无 struct 复制
                float y = store.PositionY[enemyId];

                if (store.EnemyActive[enemyId] && y <= 0f)
                {
                    logger.Log("[INFO] Enemy " + new Entity(enemyId).ToString() + " reached bottom (y=" + y + "), Game Over!");
                    return true;
                }
            }

            return false;
        }
    }
}
