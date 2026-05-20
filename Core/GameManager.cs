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
        private GoldSystem goldSystem;
        private WaveSpawningSystem waveSpawningSystem;
        private UpgradeSystem upgradeSystem;
        private SkillSystem skillSystem;
        private EnemyAISystem enemyAISystem;
        private EnemyAbilitySystem enemyAbilitySystem;
        private TowerPlacementSystem towerPlacementSystem;  // 塔建造系统
        private TowerAttackSystem towerAttackSystem;       // 塔攻击系统
        private TowerUpgradeSystem towerUpgradeSystem;     // 塔升级系统
        private TechTreeSystem techTreeSystem;            // 科技树系统
        private BuffSystem buffSystem;                    // Buff/DoT 追踪系统

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
            mapSystem.SetMapSize(gameConfig.MapWidth, gameConfig.MapHeight);  // Bug#30: use config values instead of magic numbers
            store.SetMapSize(gameConfig.MapWidth, gameConfig.MapHeight);     // Bug#2: sync SpatialGrid with MapSystem
            logger.Log("[BOOTSTRAP]      MapSystem created successfully!");

            // 初始化其他系统
            logger.Log("[BOOTSTRAP]    - Creating EnemyMovementSystem...");
            enemyMovementSystem = new EnemyMovementSystem(store, playerId, gameConfig.MapWidth);
            logger.Log("[BOOTSTRAP]      EnemyMovementSystem created successfully!");

            // 初始化塔防系统
            logger.Log("[BOOTSTRAP]    - Creating TowerPlacementSystem...");
            towerPlacementSystem = new TowerPlacementSystem(store, logger);
            logger.Log("[BOOTSTRAP]      TowerPlacementSystem created successfully!");

            logger.Log("[BOOTSTRAP]    - Creating TechTreeSystem...");
            var techConfig = TechTreeSystem.LoadConfig(logger);
            techTreeSystem = new TechTreeSystem(store, logger, playerId, techConfig, gameConfig);
            logger.Log("[BOOTSTRAP]      TechTreeSystem created successfully!");

            logger.Log("[BOOTSTRAP]    - Creating TowerAttackSystem...");
            towerAttackSystem = new TowerAttackSystem(store, logger, techTreeSystem);
            logger.Log("[BOOTSTRAP]      TowerAttackSystem created successfully!");

            logger.Log("[BOOTSTRAP]    - Creating TowerUpgradeSystem...");
            towerUpgradeSystem = new TowerUpgradeSystem(store, logger, gameConfig);
            logger.Log("[BOOTSTRAP]      TowerUpgradeSystem created successfully!");

            // 初始化玩家（血量 200）
            logger.Log("[BOOTSTRAP]    - Creating Player Entity...");
            InitializePlayer();
            logger.Log("[BOOTSTRAP]      Player Entity created successfully!");

            logger.Log("[BOOTSTRAP] 3. Initializing Player Skills (from config)...");
            waveSpawningSystem = new WaveSpawningSystem(store, logger, gameConfig);
            logger.Log("[BOOTSTRAP]      WaveSpawningSystem created successfully!");

            logger.Log("[BOOTSTRAP]    - Creating UpgradeSystem...");
            upgradeSystem = new UpgradeSystem(store, logger, playerId, gameConfig);
            logger.Log("[BOOTSTRAP]      UpgradeSystem created successfully!");

            logger.Log("[BOOTSTRAP]    - Creating SkillSystem (config-driven)...");
            skillSystem = new SkillSystem(store, logger, playerId, gameConfig, techTreeSystem);  // 初始化技能系统（从配置加载）
            logger.Log("[BOOTSTRAP]      SkillSystem created successfully!");

            // 初始化玩家技能（从配置加载）
            skillSystem.InitializePlayerSkills();  // 初始化技能系统
            logger.Log("[BOOTSTRAP]      Player Skills initialized successfully!");

            logger.Log("[BOOTSTRAP]    - Creating EnemyAbilitySystem...");
            enemyAbilitySystem = new EnemyAbilitySystem(store, logger, playerId, gameConfig);
            logger.Log("[BOOTSTRAP]      EnemyAbilitySystem created successfully!");

            logger.Log("[BOOTSTRAP]    - Creating EnemyAISystem...");
            enemyAISystem = new EnemyAISystem(store, logger, playerId, gameConfig, enemyAbilitySystem, techTreeSystem);  // 初始化敌人 AI 系统（行为树驱动）
            logger.Log("[BOOTSTRAP]      EnemyAISystem created successfully!");

            logger.Log("[BOOTSTRAP]    - Creating GoldSystem...");
            goldSystem = new GoldSystem(store, logger, techTreeSystem);
            logger.Log("[BOOTSTRAP]      GoldSystem created successfully!");

            logger.Log("[BOOTSTRAP]    - Creating BuffSystem (DoT tracking)...");
            buffSystem = new BuffSystem(store, playerId);
            logger.Log("[BOOTSTRAP]      BuffSystem created successfully!");

            // Wire BuffSystem into SkillSystem for Poison Nova DoT application
            skillSystem.InjectDotSystem(buffSystem);

            logger.Log("[BOOTSTRAP]    - Creating PlayerTowerAttackSystem...");
            playerTowerAttackSystem = new PlayerTowerAttackSystem(store, logger, playerId, gameConfig, techTreeSystem);
            logger.Log("[BOOTSTRAP]      PlayerTowerAttackSystem created successfully!");

            // 订阅波次完成事件 → 产出研究点数
            waveSpawningSystem.OnWaveComplete += () => techTreeSystem.OnWaveComplete();
            // 订阅波次开始事件 → 同步波次伤害缩放到所有攻击系统
            waveSpawningSystem.OnWaveStart += () =>
            {
                int wave = waveSpawningSystem.GetCurrentWave();
                playerTowerAttackSystem.SetWaveNumber(wave);
                towerAttackSystem.SetWaveNumber(wave);
                skillSystem.SetWaveNumber(wave);
            };

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
            store.SetPlayerGold(id, 200f); // 初始金币，允许第一波前建造 1-2 个初始塔

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
        /// 运行性能基准测试
        /// </summary>
        public void RunBenchmark(int count)
        {
            var benchmark = new BenchmarkSystem(store);
            benchmark.RunBenchmark(count);
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
                store.RebuildSpatialGrid();
                mapSystem.Update();
                logger.Log("========================================");

                // [测试] 自动部署防御塔（PlaceTower 返回真实 ID，不再硬编码 — Bug #3）
                logger.Log("[TEST] 自动部署防御塔...");
                int towerId1 = towerPlacementSystem.PlaceTower(2, 5, "弓箭塔", 15.0f, 3, 1.5f, 100f);
                int towerId2 = towerPlacementSystem.PlaceTower(7, 12, "魔法塔", 25.0f, 5, 0.8f, 200f);

                // [测试] 升级塔（使用真实分配的 ID）
                logger.Log("[TEST] 尝试升级塔...");
                store.SetPlayerGold(store.PlayerEntityId, 500f); // 给金币
                if (towerId1 >= 0) towerUpgradeSystem.UpgradeTower(towerId1);
                if (towerId2 >= 0) towerUpgradeSystem.UpgradeTower(towerId2);

                Console.WriteLine();
                logger.Log("Game Start!");

                // 游戏主循环
                gameRunning = true;
                turn = 0;

                while (gameRunning && turn < maxTurns)
                {
                    turn++;
                    store.BeginFrame(); // Reset two-phase queues each turn
                    store.SetTurnCCFlags(); // Decrement player CC durations (enemy stun flags cleared in EnemyMovementSystem.SetTurn)

                    System.Threading.Thread.Sleep(1000);

                    logger.Log("[INFO] --- Turn " + turn + " ---");

                    // 生成敌人（SOA）- 每波 100 只怪
                    waveSpawningSystem.Update();

                    // 敌人 AI 评估（行为树）- 在移动之前执行
                    enemyAISystem.SetTurn(turn);
                    enemyAISystem.Update();
                    // 敌人技能执行（串行，与 attack event 合并）
                    enemyAbilitySystem.SetTurn(turn);
                    enemyAbilitySystem.UpdateCooldowns(1f);
                    enemyAbilitySystem.ExecuteAbilities();
                    enemyAbilitySystem.Update(); // 回合末：减少 buff 持续时间，清除过期 buff

                    // 移动敌人（SOA）
                    enemyMovementSystem.SetTurn(turn);
                    enemyMovementSystem.Update();

                    // 玩家攻击（SOA）
                    playerTowerAttackSystem.SetTurn(turn);
                    towerAttackSystem.SetTurn(turn);
                    playerTowerAttackSystem.Update();

                    // 技能系统缓存（与玩家攻击/敌人AI保持一致的 SetTurn 模式）
                    skillSystem.SetTurn(turn);

                    // Spatial Grid — rebuild all active enemies for this frame.
                    // EnemyMovementSystem tracks MovedEnemyIds but the incremental update
                    // path has correctness complexity; full rebuild is fast enough (~0.03ms).
                    store.RebuildSpatialGrid();

// [测试] 塔攻击逻辑
                    towerAttackSystem.Update(1.0f);

                    // Buff/DoT 系统更新（减少持续时间，触发周期性伤害）— 在帧末结算前执行，使 DoT 伤害本帧生效
                    buffSystem.Update(1f);

                    // 技能系统串行段伤害结算（两阶段：并行收集 → 串行 apply）
                    skillSystem.ResolveSkillDamage();

                    // DoT/Buff 系统伤害结算（两阶段：收集 → 串行 apply）— 在帧末死亡结算前执行，确保 DoT 伤害与攻击伤害同一帧结算
                    buffSystem.ResolveDotDamage();

                    // 统一帧末死亡结算（所有攻击系统已完成伤害/死亡入队）
                    store.ResolveEnemiesKilledThisFrame();

                    // 检查玩家是否存活
                    if (!store.IsPlayerAlive(playerId))
                    {
                        // Try不朽科技复活（消耗一次复活机会）
                        if (techTreeSystem.TryRespawn())
                        {
                            logger.Log("[INFO] 不朽科技触发！玩家复活，继续游戏...");
                            logger.Log("[HEALTH] Player Health: " + store.GetPlayerCurrentHealth(playerId) + " / " + store.GetPlayerMaxHealth(playerId));
                        }
                        else
                        {
                            logger.Log("[INFO] Player died! Game Over.");
                            gameRunning = false;
                            break;
                        }
                    }

                    // 检查升级（SOA）
                    goldSystem.Update();

                    // 应用升级（SOA）
                    upgradeSystem.Update();

                    // 更新技能系统冷却
                    skillSystem.Update(1f);  // 每回合 1 秒

                    // 低血量回血科技（喘息）生效
                    float healed = techTreeSystem.TickLowHpRegen();
                    if (healed > 0f)
                        logger.Log("[TECH] 喘息触发，回复 " + healed.ToString("F1") + " 生命");

                    // 自动释放技能（根据冷却时间）
                    // skillSystem.AutoCastSkill();  // 暂时注释掉，避免重复执行

                    // 渲染地图（SOA）
                    // Note: RebuildSpatialGrid 已在上方系统链之前（line ~303）调用。
                    // 冗余调用已移除（2026-05-17）。渲染会使用上一帧的敌人位置。
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
            // Uses frame-cached enemy list — zero allocation (no new List<int> per call)
            var activeEnemyIds = store.GetCachedActiveEnemyIds();

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
