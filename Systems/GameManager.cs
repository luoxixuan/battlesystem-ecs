using System;
using BattleSystemECS.Systems;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 游戏管理器 - 管理所有游戏逻辑
    /// 初始化系统、管理游戏状态、运行游戏主循环
    /// 使用 SOA (Struct of Arrays) 架构，性能提升：10-100 倍
    /// </summary>
    public class GameManager
    {
        // ECS 组件（SOA 架构）
        private ComponentStore store;
        private EntityManager entityManager;

        // 游戏系统
        private MapSystem mapSystem;
        private PlayerTowerAttackSystem playerTowerAttackSystem;
        private EnemyMovementSystem enemyMovementSystem;
        private GoldRewardSystem goldRewardSystem;
        private WaveSpawningSystem waveSpawningSystem;
        private UpgradeSystem upgradeSystem;

        // 渲染器
        private IRenderer logger;

        // 游戏配置
        private GameConfig gameConfig;
        private int playerId;

        // 游戏状态
        private int currentLevel;
        private int maxLevels;
        private bool gameRunning;
        private int turn;
        private const int maxTurns = 20;

        /// <summary>
        /// 初始化游戏管理器
        /// </summary>
        public GameManager()
        {
            // 初始化 SOA 组件存储
            store = new ComponentStore();
            entityManager = new EntityManager(store);

            // 初始化渲染器
            logger = new ConsoleLogger();
        }

        /// <summary>
        /// 初始化游戏
        /// </summary>
        public void Initialize()
        {
            // 加载游戏配置
            gameConfig = GameConfigLoader.LoadConfig(logger);

            // 初始化地图大小
            mapSystem = new MapSystem(logger, store);
            mapSystem.SetMapSize(10, 50);

            // 初始化其他系统
            enemyMovementSystem = new EnemyMovementSystem(store);

            // 初始化玩家
            InitializePlayer();

            // 初始化其他系统
            playerTowerAttackSystem = new PlayerTowerAttackSystem(store, logger, playerId, gameConfig);
            goldRewardSystem = new GoldRewardSystem(store, logger, playerId);
            waveSpawningSystem = new WaveSpawningSystem(store, logger, gameConfig);
            upgradeSystem = new UpgradeSystem(store, logger, playerId);
        }

        /// <summary>
        /// 初始化玩家实体
        /// </summary>
        private void InitializePlayer()
        {
            var playerEntity = entityManager.CreateEntity();
            entityManager.SetName(playerEntity, "Player");
            int id = playerEntity.Id;

            // SOA: 添加玩家组件
            store.AddPosition(id, 5f, 0f);
            store.AddPlayer(id, 3f, 1f, 10f, 1);

            playerId = id;

            logger.Log("[INFO] Player created! Position: x=5, y=0");
            logger.Log("[INFO]   - Attack Range: 3 grids");
            logger.Log("[INFO]   - Attack Interval: 1 seconds");
            logger.Log("[INFO]   - Attack Damage: 10 points");
            logger.Log("[INFO]   - Current Level: 1");
            logger.Log("[INFO]   - Upgrade Threshold: 100 gold");
        }

        /// <summary>
        /// 运行游戏主循环
        /// </summary>
        public void Run()
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

            // 关卡循环
            currentLevel = 1;
            maxLevels = gameConfig.Levels.Count;

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

                // 设置波次关卡
                waveSpawningSystem.SetLevel(currentLevel);

                // 渲染初始地图（SOA）
                Console.WriteLine();
                logger.Log("========================================");
                mapSystem.Update();
                logger.Log("========================================");

                Console.WriteLine();
                logger.Log("Game Start!");

                // 游戏主循环
                gameRunning = true;
                turn = 0;

                while (gameRunning && turn < maxTurns)
                {
                    turn++;
                    System.Threading.Thread.Sleep(1000);

                    logger.Log("[INFO] --- Turn " + turn + " ---");

                    // 生成敌人（SOA）
                    waveSpawningSystem.Update();

                    // 移动敌人（SOA）
                    enemyMovementSystem.Update();

                    // 玩家攻击（SOA）
                    playerTowerAttackSystem.Update();

                    // 检查升级（SOA）
                    goldRewardSystem.Update();

                    // 应用升级（SOA）
                    upgradeSystem.Update();

                    // 渲染地图（SOA）
                    mapSystem.Update();

                    // 检查敌人是否到达底部
                    if (CheckEnemiesAtBottom())
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

        /// <summary>
        /// 检查是否有敌人到达底部
        /// </summary>
        private bool CheckEnemiesAtBottom()
        {
            var activeEnemyIds = store.GetAllActiveEnemyIds();

            foreach (var enemyId in activeEnemyIds)
            {
                // SOA: 直接数组访问，无字典查询，无 struct 复制
                float y = store.PositionY[enemyId];

                if (store.EnemyActive[enemyId] && y <= 0f)
                {
                    logger.Log("[INFO] Enemy reached bottom (y=" + y + "), Game Over!");
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 获取当前游戏状态
        /// </summary>
        public int GetCurrentLevel()
        {
            return currentLevel;
        }

        /// <summary>
        /// 获取当前回合数
        /// </summary>
        public int GetTurn()
        {
            return turn;
        }

        /// <summary>
        /// 检查游戏是否正在运行
        /// </summary>
        public bool IsGameRunning()
        {
            return gameRunning;
        }
    }
}
