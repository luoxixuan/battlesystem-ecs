using System;
using System.Collections.Generic;
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

            var entityManager = new EntityManager();
            var logger = new ConsoleLogger();

            // Load configuration from JSON
            var gameConfig = GameConfigLoader.LoadConfig(logger);

            // Initialize systems
            var mapSystem = new MapSystem(logger);
            mapSystem.SetMapSize(10, 50);

            var playerTowerAttackSystem = new PlayerTowerAttackSystem(entityManager, logger, 1, gameConfig);

            var enemyMovementSystem = new EnemyMovementSystem(entityManager, logger);

            var goldRewardSystem = new GoldRewardSystem(entityManager, logger, 1);

            var waveSpawningSystem = new WaveSpawningSystem(entityManager, logger, gameConfig);

            var upgradeSystem = new UpgradeSystem(entityManager, logger, 1);

            // Create player
            var playerEntity = CreatePlayer(entityManager, gameConfig.Player, logger);
            int playerId = playerEntity.Id;

            // Game level loop
            int currentLevel = 1;
            int maxLevels = gameConfig.Levels.Count;

            while (currentLevel <= maxLevels)
            {
                waveSpawningSystem.SetLevel(currentLevel);

                Console.WriteLine();
                logger.Log("[INFO] ========== Level " + currentLevel + " ==========");

                // Set current level in wave spawning system
                waveSpawningSystem.Update();

                // Render initial map
                Console.WriteLine();
                logger.Log("========================================");
                mapSystem.Update(entityManager);
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

                    // Spawn enemies for current wave
                    waveSpawningSystem.Update();

                    // Move enemies
                    enemyMovementSystem.Update();

                    // Player attack
                    playerTowerAttackSystem.Update();

                    // Check upgrade
                    goldRewardSystem.Update();

                    // Apply upgrade
                    upgradeSystem.Update();

                    // Render map
                    mapSystem.Update(entityManager);

                    // Check enemies at bottom
                    if (CheckEnemiesAtBottom(entityManager, logger))
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

        private static Entity CreatePlayer(EntityManager entityManager, PlayerConfig playerConfig, IRenderer logger)
        {
            var playerEntity = entityManager.CreateEntity();
            entityManager.SetName(playerEntity, "Player");

            entityManager.AddComponent(playerEntity, new PositionComponent(5f, 0f));

            entityManager.AddComponent(playerEntity, new PlayerComponent
            {
                AttackRange = playerConfig.AttackRange,
                AttackSpeed = playerConfig.AttackInterval,
                AttackDamage = playerConfig.AttackDamage,
                CurrentLevel = playerConfig.CurrentLevel
            });

            entityManager.AddComponent(playerEntity, new GoldComponent { Amount = 0f });

            entityManager.AddComponent(playerEntity, new UpgradeComponent
            {
                NextUpgradeThreshold = playerConfig.UpgradeThreshold
            });

            Console.WriteLine("[INFO] Player created! Position: x=5, y=0");
            logger.Log("[INFO]   - Attack Range: " + playerConfig.AttackRange + " grids");
            logger.Log("[INFO]   - Attack Interval: " + playerConfig.AttackInterval + " seconds");
            logger.Log("[INFO]   - Attack Damage: " + playerConfig.AttackDamage + " points");
            logger.Log("[INFO]   - Current Level: " + playerConfig.CurrentLevel);
            logger.Log("[INFO]   - Upgrade Threshold: " + playerConfig.UpgradeThreshold + " gold");

            return playerEntity;
        }

        private static bool CheckEnemiesAtBottom(EntityManager entityManager, IRenderer logger)
        {
            var enemies = entityManager.GetAllEntities();

            foreach (var enemy in enemies)
            {
                if (enemy.Id == 1) continue; // Skip player

                var pos = entityManager.GetComponent<PositionComponent>(enemy);
                var enemyHealth = entityManager.GetComponent<EnemyComponent>(enemy);

                // Skip dead enemies
                if (enemyHealth.Health <= 0f)
                    continue;

                // Check if reached bottom
                if (pos.Y <= 0f)
                {
                    var monsterName = entityManager.GetName(enemy);
                    logger.Log("[INFO] Enemy " + monsterName + " reached bottom (y=" + pos.Y + "), Game Over!");
                    return true;
                }
            }

            return false;
        }
    }
}
