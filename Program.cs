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

            // Create player
            Entity playerEntity = CreatePlayer(entityManager, gameConfig.Player, logger);
            int playerId = playerEntity.Id;

            // Game level loop
            int currentLevel = 1;
            int maxLevels = gameConfig.Levels.Count;

            while (currentLevel <= maxLevels)
            {
                var levelConfig = gameConfig.GetLevelConfig(currentLevel);
                if (levelConfig == null)
                {
                    logger.Log("[ERROR] Level " + currentLevel + " not found!");
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

                // Create enemies for current level
                List<int> enemies = new List<int>();
                foreach (var wave in levelConfig.Waves)
                {
                    for (int i = 0; i < wave.EnemyCount; i++)
                    {
                        int enemyId = CreateEnemy(entityManager, currentLevel, wave.WaveNumber, i, wave.MonsterType, gameConfig);
                        if (enemyId > 0)
                        {
                            enemies.Add(enemyId);
                        }
                    }
                }

                Console.WriteLine();
                logger.Log("[MAP] Current Map");
                logger.Log("========================================");
                RenderMap(logger, entityManager);
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
                    ProcessPlayerTurn(entityManager, logger, playerEntity);

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

        private static int CreateEnemy(EntityManager entityManager, int levelNumber, int waveNumber, int enemyIndex, string monsterType, GameConfig gameConfig)
        {
            var monsterConfig = gameConfig.GetMonsterConfig(monsterType);
            if (monsterConfig == null)
            {
                Console.WriteLine("[ERROR] Monster type '" + monsterType + "' not found!");
                return 0;
            }

            var enemyEntity = entityManager.CreateEntity();
            string enemyName = monsterType + "L" + levelNumber + "W" + waveNumber + "E" + enemyIndex;
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
                WaveNumber = waveNumber
            });

            Console.WriteLine("[INFO] " + enemyName + " created! Position: x=" + startX + ", y=" + startY);

            return enemyEntity.Id;
        }

        private static void ProcessPlayerTurn(EntityManager entityManager, IRenderer logger, Entity playerEntity)
        {
            var player = entityManager.GetComponent<PlayerComponent>(playerEntity);
            var playerPos = entityManager.GetComponent<PositionComponent>(playerEntity);
            var gold = entityManager.GetComponent<GoldComponent>(playerEntity);
            var upgrade = entityManager.GetComponent<UpgradeComponent>(playerEntity);

            // Move enemies
            var enemies = entityManager.GetAllEntities();
            foreach (var enemy in enemies)
            {
                if (enemy.Id == playerEntity.Id) continue; // Skip player

                var enemyPos = entityManager.GetComponent<PositionComponent>(enemy);
                var enemyHealth = entityManager.GetComponent<EnemyComponent>(enemy);

                // Skip dead enemies
                if (enemyHealth.Health <= 0f)
                    continue;

                // Enemy moves downward
                enemyPos.Y -= enemyHealth.MoveSpeed;
                entityManager.SetComponent(enemy, enemyPos);
            }

            // Player attack
            foreach (var enemy in enemies)
            {
                if (enemy.Id == playerEntity.Id) continue; // Skip player

                var enemyPos = entityManager.GetComponent<PositionComponent>(enemy);
                var enemyHealth = entityManager.GetComponent<EnemyComponent>(enemy);

                // Skip dead enemies
                if (enemyHealth.Health <= 0f)
                    continue;

                // Check if in attack range
                float distance = Math.Abs(enemyPos.X - playerPos.X);
                if (distance <= player.AttackRange && enemyPos.Y > playerPos.Y)
                {
                    float damage = player.AttackDamage;
                    enemyHealth.Health = Math.Max(0f, enemyHealth.Health - damage);
                    entityManager.SetComponent(enemy, enemyHealth);

                    var monsterName = entityManager.GetName(enemy);
                    logger.Log("[ATTACK] Player attacks enemy " + enemy.Id + ", damage: " + damage + ", position: x=" + enemyPos.X + ", y=" + enemyPos.Y);

                    if (enemyHealth.Health <= 0f)
                    {
                        gold.Amount += enemyHealth.GoldReward;
                        entityManager.SetComponent(playerEntity, gold);

                        logger.Log("[GOLD] Killed " + monsterName + ", gained " + enemyHealth.GoldReward + " gold");
                        logger.Log("[GOLD] Total gold: " + gold.Amount);

                        if (gold.Amount >= upgrade.NextUpgradeThreshold)
                        {
                            ProcessUpgrade(entityManager, logger, playerEntity, upgrade);
                        }
                    }
                }
            }
        }

        private static void ProcessUpgrade(EntityManager entityManager, IRenderer logger, Entity playerEntity, UpgradeComponent upgrade)
        {
            var player = entityManager.GetComponent<PlayerComponent>(playerEntity);

            player.CurrentLevel++;
            player.AttackDamage += 5f;
            player.AttackRange += 1f;
            upgrade.NextUpgradeThreshold *= 1.5f;

            entityManager.SetComponent(playerEntity, player);
            entityManager.SetComponent(playerEntity, upgrade);

            logger.Log("[UPGRADE] Player upgraded to level " + player.CurrentLevel + "!");
            logger.Log("[UPGRADE] Attack damage increased to " + player.AttackDamage);
            logger.Log("[UPGRADE] Attack range increased to " + player.AttackRange + " grids");
            logger.Log("[UPGRADE] Next upgrade needs " + upgrade.NextUpgradeThreshold + " gold");

            RandomlyGainBuff(upgrade);
        }

        private static void RandomlyGainBuff(UpgradeComponent upgrade)
        {
            string[] buffs = { "Attack+10%", "Defense+10%", "Attack Speed+20%", "Crit Rate+5%", "Health+20%" };
            int randomIndex = new Random().Next(buffs.Length);
            string newBuff = buffs[randomIndex];

            if (!upgrade.Skills.Contains(newBuff))
            {
                upgrade.Skills.Add(newBuff);
                Console.WriteLine("[BUFF] Gained new buff: " + newBuff + "!");
            }
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

        private static void RenderMap(IRenderer logger, EntityManager entityManager)
        {
            logger.Log("[MAP] 10x50 map");
            logger.Log("[MAP] P = Player, E = Enemy, . = Empty");

            for (int y = 49; y >= 0; y--)
            {
                string row = "";
                for (int x = 0; x < 10; x++)
                {
                    bool hasEnemy = false;

                    var entities = entityManager.GetAllEntities();
                    foreach (var entity in entities)
                    {
                        if (entity.Id == 1) continue; // Skip player

                        var pos = entityManager.GetComponent<PositionComponent>(entity);
                        var posNotNull = entityManager.HasComponent<PositionComponent>(entity);
                        if (posNotNull && Math.Abs(pos.X - x) < 0.5f && Math.Abs(pos.Y - y) < 0.5f)
                        {
                            hasEnemy = true;
                            break;
                        }
                    }

                    if (y == 0 && x == 5)
                        row += "P ";
                    else if (hasEnemy)
                        row += "E ";
                    else
                        row += ". ";
                }
                Console.WriteLine("[MAP] " + row);
            }
        }
    }
}
