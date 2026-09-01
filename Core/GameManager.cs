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
        private readonly FrameSchedulerExecutionMode _schedulerExecutionMode;
        private readonly Core.GAS.ClockId _effectClock;

        // 游戏状态机（管理 BuildPhase / WavePhase / Intermission 切换）
        private StateMachine stateMachine;

        internal FrameScheduler SchedulerDiagnostics => scheduler;
        internal StateMachine StateMachineDiagnostics => stateMachine;
        internal SystemRegistry RegistryDiagnostics => registry;
        internal FrameSchedulerExecutionMode ConfiguredExecutionMode => _schedulerExecutionMode;

        // 渲染器
        private IRenderer logger;
        private IBattleEventBus _eventBus;

        // 游戏配置
        private GameConfig gameConfig;
        private int playerId;

        // 游戏状态
        private int currentLevel;
        private int maxLevels;
        private bool gameRunning;
        private int turn;
        private const int maxTurns = 20;

        // ── Fixed timestep logic (Unity / external caller integration) ──
        private float _accumulatedTime = 0f;
        private const float FIXED_TIMESTEP = 1f; // 1 second per logic tick, matching Thread.Sleep(1000)

        /// <summary>
        /// 初始化游戏管理器
        /// </summary>
        public GameManager(FrameSchedulerExecutionMode schedulerExecutionMode = FrameSchedulerExecutionMode.Graph,
            Core.GAS.ClockId effectClock = Core.GAS.ClockId.Combat)
        {
            if (!Enum.IsDefined(typeof(FrameSchedulerExecutionMode), schedulerExecutionMode))
                throw new ArgumentOutOfRangeException(nameof(schedulerExecutionMode), schedulerExecutionMode, "Unknown scheduler execution mode.");
            if (!Enum.IsDefined(typeof(Core.GAS.ClockId), effectClock))
                throw new ArgumentOutOfRangeException(nameof(effectClock), effectClock, "Unknown effect clock.");
            _schedulerExecutionMode = schedulerExecutionMode;
            _effectClock = effectClock;
            // 初始化 SOA 组件存储
            store = new ComponentStore();
            entityManager = new EntityManager(store);

            // 初始化渲染器
            logger = new ConsoleLogger();
            _eventBus = new ConsoleEventBus();
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
            // Production bootstrap validates the typed ability catalog before any
            // system is constructed. Legacy fallback remains available only to
            // explicit tests/tools via LoadConfig.
            gameConfig = GameConfigLoader.LoadStrictCatalog(logger);

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
            registry.CreateAll(store, gameConfig, logger, playerId, stateMachine, _eventBus);
            registry.WireDependencies(store, playerId);

            scheduler = new FrameScheduler(store, gameConfig, _eventBus, _schedulerExecutionMode, _effectClock);
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
            scheduler.BindStateMachine(stateMachine);

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
        /// Print the daily challenge summary to stdout (Round 105 Direction 9).
        /// When the daily system is disabled (empty pool / null result), prints a
        /// "stock run" notice so the player knows the daily bonus is inactive.
        /// Safe to call on any GameConfig — uses null-checks throughout.
        /// </summary>
        public void PrintDailySummary()
        {
            try
            {
                Console.WriteLine();
                Console.WriteLine("═══════════════════════════════════════════");
                Console.WriteLine("  📅  Daily Challenge");
                Console.WriteLine("═══════════════════════════════════════════");
                if (gameConfig == null || gameConfig.DailyLastResult == null
                    || gameConfig.DailyLastResult.Selected == null
                    || gameConfig.DailyLastResult.Selected.Count == 0)
                {
                    Console.WriteLine("  Stock run — daily challenge disabled.");
                    Console.WriteLine("═══════════════════════════════════════════");
                    Console.WriteLine();
                    return;
                }
                var r = gameConfig.DailyLastResult;
                Console.WriteLine("  Date:  " + r.Date);
                Console.WriteLine("  Seed:  " + r.Seed);
                Console.WriteLine("  Modifiers:");
                for (int i = 0; i < r.Selected.Count; i++)
                {
                    var m = r.Selected[i];
                    string name = string.IsNullOrEmpty(m.Name) ? m.Id : m.Name;
                    Console.WriteLine("    • " + name + "  —  " + (string.IsNullOrEmpty(m.Description) ? "(no description)" : m.Description));
                }
                Console.WriteLine("  Effective multipliers:");
                Console.WriteLine(string.Format("    damage ×{0:F2}  gold ×{1:F2}  enemyHp ×{2:F2}  startGoldBonus {3:+0;-0;0}",
                    gameConfig.DailyDamageMult, gameConfig.DailyGoldMult,
                    gameConfig.DailyEnemyHpMult, gameConfig.DailyStartingGoldBonus));
                Console.WriteLine("═══════════════════════════════════════════");
                Console.WriteLine();
            }
            catch
            {
                // Defensive: never let summary printing break game startup
            }
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

                // 初始化关卡可破坏物（Round 95 Direction 5：路径上的木箱/油桶，可被塔攻击打爆触发效果）
                SpawnDestructiblesForLevel(levelConfig);

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

                    if (!ExecuteTurn(turn))
                    {
                        gameRunning = false;
                        break;
                    }
                }

                currentLevel++;
            }

            Console.WriteLine();
            logger.Log("Game Over! Completed " + (currentLevel - 1) + " levels.");
            Console.WriteLine();
        }

        /// <summary>
        /// 执行单个回合的共享主体：帧调度 + 游戏级逻辑（玩家存活/复活、低血量回血、渲染、血量显示、底部漏怪检查）。
        /// Run()（交互式循环）与 FixedUpdate()（Unity 固定步长）共用此方法，避免两条路径逻辑漂移。
        /// 返回 false 表示本局应停止（玩家死亡且无复活、或敌人到达底部）。
        /// </summary>
        private bool ExecuteTurn(int turnNumber)
        {
            logger.Log("[INFO] --- Turn " + turnNumber + " ---");

            // ── 帧调度（统一入口）──
            scheduler.TickGameTurn(FIXED_TIMESTEP, turnNumber);

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
                    return false;
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
                logger.Log("[INFO] Game Over! Enemy reached bottom.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Fixed-timestep update for Unity / external caller integration.
        /// Accumulates deltaTime and advances logic at FIXED_TIMESTEP (1s) intervals.
        /// Call this from Unity's Update() with Time.deltaTime to decouple render FPS from logic FPS.
        /// </summary>
        public void FixedUpdate(float deltaTime)
        {
            if (!gameRunning) return;
            if (turn >= maxTurns) return;

            _accumulatedTime += deltaTime;

            while (_accumulatedTime >= FIXED_TIMESTEP)
            {
                turn++;
                _accumulatedTime -= FIXED_TIMESTEP;

                if (!ExecuteTurn(turn))
                {
                    gameRunning = false;
                    break;
                }
            }
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
        /// Spawn destructible objects (crates, oil barrels) for the given level.
        /// Round 95 Direction 5. Reads the `Destructibles` array from the level JSON
        /// (each entry: {DefId, X, Y}), looks up the matching DestructibleDef by Id,
        /// and registers it with the ComponentStore via AddObstacle. Each destructible
        /// occupies one ObstacleActive slot and gets an OnDestroyEffect + OnDestroyValue
        /// attached so TowerAttackSystem can apply the right effect on destruction.
        /// Opt-in: levels without a Destructibles array spawn zero destructibles,
        /// which means zero hot-path overhead (ActiveObstacleIds is empty).
        /// </summary>
        private void SpawnDestructiblesForLevel(LevelConfig levelConfig)
        {
            if (levelConfig == null) return;
            if (gameConfig.DestructibleDefs == null || gameConfig.DestructibleDefs.Length == 0)
            {
                // No destructible prototypes loaded — skip silently (opt-in)
                return;
            }
            if (levelConfig.Destructibles == null || levelConfig.Destructibles.Count == 0)
            {
                // Level doesn't place any destructibles
                return;
            }

            int spawned = 0;
            for (int i = 0; i < levelConfig.Destructibles.Count; i++)
            {
                var entry = levelConfig.Destructibles[i];
                if (entry == null) continue;

                // Find the DestructibleDef by Id
                int typeId = -1;
                for (int t = 0; t < gameConfig.DestructibleDefs.Length; t++)
                {
                    if (gameConfig.DestructibleDefs[t].Id == entry.DefId)
                    {
                        typeId = t;
                        break;
                    }
                }
                if (typeId < 0)
                {
                    logger.Log("[DESTRUCTIBLE] Unknown destructible DefId: " + entry.DefId + " — skipping");
                    continue;
                }

                var def = gameConfig.DestructibleDefs[typeId];
                if (def.MaxHealth <= 0f)
                {
                    // Disabled prototype (MaxHealth=0) — skip
                    continue;
                }

                // Find a free obstacle slot (linear scan from the last-used id)
                int oid = -1;
                int attempts = 0;
                int startScan = 0;
                while (attempts < ComponentStore.MAX_OBSTACLES)
                {
                    int candidate = (startScan + attempts) % ComponentStore.MAX_OBSTACLES;
                    if (!store.ObstacleActive[candidate])
                    {
                        oid = candidate;
                        break;
                    }
                    attempts++;
                }
                if (oid < 0)
                {
                    logger.Log("[DESTRUCTIBLE] No free obstacle slot for destructible " + entry.DefId);
                    continue;
                }

                store.AddObstacle(oid, typeId, entry.X, entry.Y, def.MaxHealth, def.OnDestroyEffect, def.OnDestroyValue);
                spawned++;
            }
            if (spawned > 0)
            {
                logger.Log("[DESTRUCTIBLE] Spawned " + spawned + " destructibles for level " + levelConfig.LevelNumber);
            }
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
