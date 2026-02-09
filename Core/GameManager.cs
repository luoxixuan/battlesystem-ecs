using System;
using BattleSystemECS.Components;
using BattleSystemECS.Systems;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Core
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
        private SkillSystem skillSystem;
        private EnemyAttackSystem enemyAttackSystem;  // 添加敌人攻击系统

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
        private float playerMaxHealth = 200f;  // 主角最大血量

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
            Console.WriteLine();
            logger.Log("[BOOTSTRAP] ========== Game Initialization ==========");
            logger.Log("[BOOTSTRAP] 1. Loading Game Configuration...");

            // 加载游戏配置
            gameConfig = GameConfigLoader.LoadConfig(logger);

            logger.Log("[BOOTSTRAP]    - Configuration loaded successfully!");
            logger.Log("[BOOTSTRAP]    - Monster Types: " + gameConfig.MonsterTypes.Count);
            logger.Log("[BOOTSTRAP]    - Levels: " + gameConfig.Levels.Count);
            logger.Log("[BOOTSTRAP]    - Skills: " + gameConfig.Skills.Count);

            Console.WriteLine();
            logger.Log("[BOOTSTRAP] 2. Initializing Game Systems...");

            // 初始化地图大小
            logger.Log("[BOOTSTRAP]    - Creating MapSystem (10x20 map)...");
            mapSystem = new MapSystem(logger, store);
            mapSystem.SetMapSize(10, 20);  // 地图改为 10x20
            logger.Log("[BOOTSTRAP]      MapSystem created successfully!");

            // 初始化其他系统
            logger.Log("[BOOTSTRAP]    - Creating EnemyMovementSystem...");
            enemyMovementSystem = new EnemyMovementSystem(store);
            logger.Log("[BOOTSTRAP]      EnemyMovementSystem created successfully!");

            // 初始化玩家（血量 200）
            logger.Log("[BOOTSTRAP]    - Creating Player Entity...");
            InitializePlayer();
            logger.Log("[BOOTSTRAP]      Player Entity created successfully!");

            logger.Log("[BOOTSTRAP] 3. Initializing Player Skills (from config)...");

            // 初始化其他系统
            logger.Log("[BOOTSTRAP]    - Creating PlayerTowerAttackSystem...");
            playerTowerAttackSystem = new PlayerTowerAttackSystem(store, logger, playerId, gameConfig);
            logger.Log("[BOOTSTRAP]      PlayerTowerAttackSystem created successfully!");

            logger.Log("[BOOTSTRAP]    - Creating GoldRewardSystem...");
            goldRewardSystem = new GoldRewardSystem(store, logger, playerId);
            logger.Log("[BOOTSTRAP]      GoldRewardSystem created successfully!");

            logger.Log("[BOOTSTRAP]    - Creating WaveSpawningSystem...");
            waveSpawningSystem = new WaveSpawningSystem(store, logger, gameConfig);
            logger.Log("[BOOTSTRAP]      WaveSpawningSystem created successfully!");

            logger.Log("[BOOTSTRAP]    - Creating UpgradeSystem...");
            upgradeSystem = new UpgradeSystem(store, logger, playerId);
            logger.Log("[BOOTSTRAP]      UpgradeSystem created successfully!");

            logger.Log("[BOOTSTRAP]    - Creating SkillSystem (config-driven)...");
            skillSystem = new SkillSystem(store, logger, playerId, gameConfig);  // 初始化技能系统（从配置加载）
            logger.Log("[BOOTSTRAP]      SkillSystem created successfully!");

            // 初始化玩家技能（从配置加载）
            skillSystem.InitializePlayerSkills();  // 初始化技能系统
            logger.Log("[BOOTSTRAP]      Player Skills initialized successfully!");

            logger.Log("[BOOTSTRAP]    - Creating EnemyAttackSystem...");
            enemyAttackSystem = new EnemyAttackSystem(store, logger, playerId);  // 初始化敌人攻击系统
            logger.Log("[BOOTSTRAP]      EnemyAttackSystem created successfully!");

            logger.Log("[BOOTSTRAP] ========== Game Initialization Complete ==========");
            Console.WriteLine();
        }

        /// <summary>
        /// 初始化玩家实体
        /// </summary>
        private void InitializePlayer()
        {
            var playerEntity = entityManager.CreateEntity();
            entityManager.SetName(playerEntity, "Player");
            int id = playerEntity.Id;

            // SOA: 添加玩家组件（从配置加载）
            store.AddPosition(id, 5f, 0f);

            // 从配置加载玩家属性
            float attackRange = gameConfig.Player.AttackRange;
            float attackSpeed = gameConfig.Player.AttackSpeed;
            float attackDamage = gameConfig.Player.AttackDamage;
            float maxHealth = gameConfig.Player.MaxHealth;
            int currentLevel = gameConfig.Player.CurrentLevel;
            float upgradeThreshold = gameConfig.Player.UpgradeThreshold;

            // 添加玩家组件（SOA）
            store.AddPlayer(id, attackRange, attackSpeed, attackDamage, currentLevel);
            store.SetPlayerMaxHealth(id, maxHealth);
            store.SetPlayerCurrentHealth(id, maxHealth);
            store.SetPlayerUpgradeThreshold(id, upgradeThreshold);

            playerId = id;

            logger.Log("[BOOTSTRAP]    - Creating Player Entity...");
            logger.Log("[INFO] Player created! Position: x=5, y=0");
            logger.Log("[INFO]   - Max Health: " + maxHealth + " (from config)");
            logger.Log("[INFO]   - Current Health: " + maxHealth + " / " + maxHealth);
            logger.Log("[INFO]   - Attack Range: " + attackRange + " grids");
            logger.Log("[INFO]   - Attack Interval: " + attackSpeed + " seconds");
            logger.Log("[INFO]   - Attack Damage: " + attackDamage + " points");
            logger.Log("[INFO]   - Current Level: " + currentLevel);
            logger.Log("[INFO]   - Upgrade Threshold: " + upgradeThreshold + " gold");
            logger.Log("[BOOTSTRAP]      Player Entity created successfully!");
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
                    logger.Log("[INFO]   - Wave " + wave.WaveNumber + ": " + wave.MonsterType + ", " + wave.EnemyCount + " enemies (100 per wave)");
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

                    // 生成敌人（SOA）- 每波 100 只怪
                    waveSpawningSystem.Update();

                    // 移动敌人（SOA）
                    enemyMovementSystem.Update();

                    // 敌人攻击玩家（SOA）- 检查相邻敌人，减少玩家生命值
                    enemyAttackSystem.Update();

                    // 玩家攻击（SOA）
                    playerTowerAttackSystem.Update();

                    // 检查玩家是否存活
                    if (!enemyAttackSystem.IsPlayerAlive())
                    {
                        logger.Log("[INFO] Player died! Game Over.");
                        gameRunning = false;
                        break;
                    }

                    // 检查升级（SOA）
                    goldRewardSystem.Update();

                    // 应用升级（SOA）
                    upgradeSystem.Update();

                    // 更新技能系统冷却
                    skillSystem.Update(1f);  // 每回合 1 秒

                    // 自动释放技能（根据冷却时间）
                    // skillSystem.AutoCastSkill();  // 暂时注释掉，避免重复执行

                    // 渲染地图（SOA）
                    mapSystem.Update();

                    // 显示玩家血量（200）
                    logger.Log("[HEALTH] Player Health: " + store.GetPlayerCurrentHealth(playerId) + " / " + store.GetPlayerMaxHealth(playerId));

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
