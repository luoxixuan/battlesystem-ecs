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

        // 系统注册中心（集中创建/依赖注入/分组赋值）
        private SystemRegistry registry;

        // Prestige / Meta Progression (cross-run unlocks, persistent)
        private PrestigeSystem prestigeSystem;

        // 游戏系统（从 registry 暴露，Run() 中直接访问）
        private WaveSpawningSystem waveSpawningSystem => registry.WaveSpawning!;
        private TowerPlacementSystem towerPlacementSystem => registry.TowerPlacement!;
        private TowerUpgradeSystem towerUpgradeSystem => registry.TowerUpgrade!;
        private TechTreeSystem techTreeSystem => registry.TechTree!;
        private ObjectiveSystem objectiveSystem => registry.Objective!;
        private ResourceNodeSystem resourceNodeSystem => registry.ResourceNode!;
        private SkillSystem skillSystem => registry.Skill!;

        // 统一帧调度器（所有帧路径统一入口）
        private FrameScheduler scheduler;

        // 游戏状态机（管理 BuildPhase / WavePhase / Intermission 切换）
        private StateMachine stateMachine;

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
            logger.Log("[BOOTSTRAP] 1b. Loading Meta Progression (prestige saves)...");

            // ── Prestige: load cross-run unlocks and resolve to GameConfig multipliers ──
            prestigeSystem = new PrestigeSystem(logger, gameConfig);
            prestigeSystem.Load();
            prestigeSystem.ApplyToConfig();

            Console.WriteLine();
            logger.Log("[BOOTSTRAP] 2. Initializing Player & Map...");

            // 初始化地图大小
            var mapSystem = new MapSystem(logger, store);
            mapSystem.SetMapSize(gameConfig.MapWidth, gameConfig.MapHeight);
            store.SetMapSize(gameConfig.MapWidth, gameConfig.MapHeight);

            // 初始化玩家
            InitializePlayer();

            // ── 初始化状态机（在系统创建之前，因为 WaveBranchSystem 需要它）──
            stateMachine = new StateMachine();

            Console.WriteLine();
            logger.Log("[BOOTSTRAP] 3. Creating all game systems (SystemRegistry)...");

            // ══════════════════════════════════════════════════════════
            //  将所有系统创建/依赖注入/分组赋值委托给 SystemRegistry
            // ══════════════════════════════════════════════════════════
            registry = new SystemRegistry();
            registry.CreateAll(store, gameConfig, logger, playerId, stateMachine);
            registry.WireDependencies(store, playerId);

            scheduler = new FrameScheduler(store, gameConfig);
            registry.AssignToGroups(scheduler);

            // 初始化地形网格
            if (gameConfig.MapTerrainGrid != null && gameConfig.MapTerrainGrid.Length > 0)
            {
                int h = gameConfig.MapTerrainGrid.Length;
                int w = h > 0 ? gameConfig.MapTerrainGrid[0].Length : 0;
                store.InitTerrainGrid(w, h, gameConfig.MapTerrainGrid);
                logger.Log($"[BOOTSTRAP]    - Terrain grid initialized: {w}x{h}");
            }

            // ── Phase + StateMachine 线路 ──
            scheduler.Phase = GameState.BuildPhase;
            stateMachine.OnEnter(GameState.BuildPhase, () => { scheduler.Phase = GameState.BuildPhase; });
            stateMachine.OnEnter(GameState.WavePhase, () => { scheduler.Phase = GameState.WavePhase; });
            stateMachine.OnEnter(GameState.Intermission, () => { scheduler.Phase = GameState.WavePhase; });
            stateMachine.OnEnter(GameState.BranchSelection, () => { scheduler.Phase = GameState.WavePhase; });

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
            store.AddPlayer(id, attackRange, attackSpeed, attackDamage, currentLevel, gameConfig.Player.StartingLives);
            store.SetPlayerMaxHealth(id, maxHealth);
            store.SetPlayerCurrentHealth(id, maxHealth);
            store.SetPlayerUpgradeThreshold(id, upgradeThreshold);
            store.SetPlayerGold(id, 200f);
            // Reincarnation: opt-in one-time save (config-driven; default 0 = disabled).
            store.SetPlayerReincarnationConfig(id, gameConfig.Player.ReincarnationCharges, gameConfig.Player.ReincarnationHealFraction);

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

                // 初始化目标系统（特殊目标模式：Escort / Survival / Timed / Endless）
                objectiveSystem.InitializeFromLevel(levelConfig, gameConfig.MapHeight);

                // 初始化资源节点系统（地图资源节点：金矿/法力泉/科技遗迹）
                resourceNodeSystem.InitializeFromLevel(levelConfig);

                // ── Phase: BuildPhase ──────────────────────────────────────────
                if (stateMachine.TransitionTo(GameState.BuildPhase))
                {
                    var pb = gameConfig.GetPhaseBehavior("BuildPhase");
                    string msg = pb?.EnterMessage ?? "[PHASE] Build Phase — place your towers!";
                    Console.WriteLine();
                    Console.WriteLine("═══════════════════════════════════════════");
                    Console.WriteLine("  " + msg);
                    Console.WriteLine("═══════════════════════════════════════════");
                    Console.WriteLine();

                    // 商店洗牌：进入 BuildPhase 时初始化 offer 池
                    registry.ShopReroll?.OnEnterBuildPhase();
                }

                // 渲染初始地图（SOA）
                Console.WriteLine();
                logger.Log("========================================");
                store.RebuildSpatialGrid();
                registry.Map?.Update();
                logger.Log("========================================");

                // [测试] 自动部署防御塔
                logger.Log("[TEST] 自动部署防御塔...");
                int towerId1 = towerPlacementSystem.PlaceTower(2, 5, TowerType.Basic, 15.0f, 3, 1.5f, 100f);
                int towerId2 = towerPlacementSystem.PlaceTower(7, 12, TowerType.Sniper, 25.0f, 5, 0.8f, 200f);

                // [测试] 升级塔
                logger.Log("[TEST] 尝试升级塔...");
                store.SetPlayerGold(store.PlayerEntityId, 500f);
                if (towerId1 >= 0) towerUpgradeSystem.UpgradeTower(towerId1);
                if (towerId2 >= 0) towerUpgradeSystem.UpgradeTower(towerId2);

                Console.WriteLine();
                logger.Log("Game Start!");

                // ── Phase transition: BuildPhase → WavePhase ────────────────────
                if (stateMachine.TransitionTo(GameState.WavePhase))
                {
                    var pb = gameConfig.GetPhaseBehavior("WavePhase");
                    string msg = pb?.WaveStartMessage ?? "[PHASE] Wave Phase — FIGHT!";
                    Console.WriteLine();
                    Console.WriteLine("═══════════════════════════════════════════");
                    Console.WriteLine("  " + msg);
                    Console.WriteLine("═══════════════════════════════════════════");
                    Console.WriteLine();
                }

                // 游戏主循环
                gameRunning = true;
                turn = 0;

                while (gameRunning && turn < maxTurns)
                {
                    turn++;

                    System.Threading.Thread.Sleep(1000);

                    logger.Log("[INFO] --- Turn " + turn + " ---");

                    // ── 帧调度（统一入口）──
                    scheduler.TickGameTurn(1f, turn);

                    // ── 游戏级逻辑 ───────────────────────────────
                    if (!store.IsPlayerAlive(playerId))
                    {
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

                    // 低血量回血科技（喘息）生效
                    float healed = techTreeSystem.TickLowHpRegen();
                    if (healed > 0f)
                        logger.Log("[TECH] 喘息触发，回复 " + healed.ToString("F1") + " 生命");

                    // 渲染地图（SOA）
                    registry.Map?.Update();

                    // 显示玩家血量
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
            var activeEnemyIds = store.GetCachedActiveEnemyIds();

            foreach (var enemyId in activeEnemyIds)
            {
                float y = store.PositionY[enemyId];

                if (store.EnemyActive[enemyId] && y <= 0f)
                {
                    store.DecrementPlayerBaseLives(playerId);
                    int remaining = store.GetPlayerBaseLives(playerId);
                    logger.Log("[INFO] Enemy reached bottom! Base lives: " + remaining + " remaining.");

                    store.QueueEnemyDeath(enemyId, playerId);

                    if (remaining <= 0)
                    {
                        logger.Log("[INFO] Game Over! No base lives remaining.");
                        return true;
                    }
                    return false;
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
