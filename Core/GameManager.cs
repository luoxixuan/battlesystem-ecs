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
        private ComboSystem comboSystem;                   // Combo Kill 连击系统
        private TowerExperienceSystem towerExperienceSystem; // Tower XP / Mastery 系统
        private AutoSkillSystem autoSkillSystem;           // 自动技能施放系统（BuildPhase）
        private TowerSynergySystem towerSynergySystem;    // 塔协同增益系统
        private AuraTowerSystem auraTowerSystem;          // 光环辅助塔系统
        private ProjectileSystem projectileSystem;        // 弹道/飞行道具系统
        private TerrainSystem terrainSystem;              // 地形效果系统
        private PathfindingSystem pathfindingSystem;     // 路径分叉/路点系统
        private WaveMutatorSystem waveMutatorSystem;    // 波次词缀/突变器系统
        private InterestSystem interestSystem;          // 银行/利息系统
        private SaveSystem saveSystem;                  // 存档/回放系统

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

            logger.Log("[BOOTSTRAP]    - Creating PathfindingSystem...");
            pathfindingSystem = new PathfindingSystem(store);
            enemyMovementSystem.SetPathfindingSystem(pathfindingSystem);
            logger.Log("[BOOTSTRAP]      PathfindingSystem created and wired to EnemyMovementSystem!");

            // 初始化塔防系统
            logger.Log("[BOOTSTRAP]    - Creating TowerPlacementSystem...");
            towerPlacementSystem = new TowerPlacementSystem(store, logger, gameConfig);
            logger.Log("[BOOTSTRAP]      TowerPlacementSystem created successfully!");

            logger.Log("[BOOTSTRAP]    - Creating TechTreeSystem...");
            var techConfig = TechTreeSystem.LoadConfig(logger);
            techTreeSystem = new TechTreeSystem(store, logger, playerId, techConfig, gameConfig);
            logger.Log("[BOOTSTRAP]      TechTreeSystem created successfully!");

            logger.Log("[BOOTSTRAP]    - Creating TowerAttackSystem...");
            towerAttackSystem = new TowerAttackSystem(store, logger, techTreeSystem);
            logger.Log("[BOOTSTRAP]      TowerAttackSystem created successfully!");

            logger.Log("[BOOTSTRAP]    - Creating TowerSynergySystem...");
            towerSynergySystem = new TowerSynergySystem(store, logger);
            towerSynergySystem.LoadSynergyConfig();
            logger.Log("[BOOTSTRAP]      TowerSynergySystem created successfully!");

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

            logger.Log("[BOOTSTRAP]    - Creating ComboSystem (Combo Kill tracking)...");
            comboSystem = new ComboSystem(store, gameConfig.Combo);
            logger.Log("      ComboSystem created successfully!");

            logger.Log("[BOOTSTRAP]    - Creating TowerExperienceSystem (Tower XP & Mastery)...");
            towerExperienceSystem = new TowerExperienceSystem(store, gameConfig);
            logger.Log("      TowerExperienceSystem created successfully!");

            logger.Log("[BOOTSTRAP]    - Creating AutoSkillSystem (BuildPhase auto-casting)...");
            autoSkillSystem = new AutoSkillSystem(store, logger, playerId, skillSystem, gameConfig.AutoSkill);
            logger.Log("[BOOTSTRAP]      AutoSkillSystem created successfully!");

            logger.Log("[BOOTSTRAP]    - Creating TowerSynergySystem...");
            towerSynergySystem = new TowerSynergySystem(store, logger);
            logger.Log("[BOOTSTRAP]      TowerSynergySystem created successfully!");

            logger.Log("[BOOTSTRAP]    - Creating AuraTowerSystem...");
            auraTowerSystem = new AuraTowerSystem(store);
            logger.Log("[BOOTSTRAP]      AuraTowerSystem created successfully!");

            logger.Log("[BOOTSTRAP]    - Creating ProjectileSystem...");
            projectileSystem = new ProjectileSystem(store, logger);
            logger.Log("[BOOTSTRAP]      ProjectileSystem created successfully!");

            logger.Log("[BOOTSTRAP]    - Creating TerrainSystem...");
            terrainSystem = new TerrainSystem(store, playerId, gameConfig);
            terrainSystem.SetBuffSystem(buffSystem);
            logger.Log("[BOOTSTRAP]      TerrainSystem created successfully!");

            logger.Log("[BOOTSTRAP]    - Creating WaveMutatorSystem...");
            waveMutatorSystem = new WaveMutatorSystem(store, playerId, logger);
            waveMutatorSystem.LoadMutators(gameConfig.WaveMutatorDefs);
            logger.Log("[BOOTSTRAP]      WaveMutatorSystem created successfully!");

            logger.Log("[BOOTSTRAP]    - Creating InterestSystem...");
            interestSystem = new InterestSystem(store, logger, gameConfig, playerId);
            logger.Log("      InterestSystem created successfully!");

            logger.Log("[BOOTSTRAP]    - Creating SaveSystem...");
            saveSystem = new SaveSystem(store, playerId);
            logger.Log("      SaveSystem created successfully!");

            // Wire OnEnemyKilled → ComboSystem (连击计数链路)
            store.OnEnemyKilled += (enemyId, playerId) => comboSystem.HandleComboIncrement(playerId);
            // Wire OnTowerKill → TowerExperienceSystem (XP 授予链路)
            store.OnTowerKill += (enemyId, playerId, towerId) => towerExperienceSystem.HandleEnemyKilled(enemyId, playerId, towerId);

            // Wire BuffSystem into SkillSystem for Poison Nova DoT application
            skillSystem.InjectDotSystem(buffSystem);

            // Wire BuffSystem into TowerAttackSystem for Firewall DoT and Leech lifesteal
            towerAttackSystem.SetBuffSystem(buffSystem);
            // Wire TowerExperienceSystem into TowerAttackSystem for XP grant on kills
            towerAttackSystem.SetTowerExperienceSystem(towerExperienceSystem);

            logger.Log("[BOOTSTRAP]    - Creating PlayerTowerAttackSystem...");
            playerTowerAttackSystem = new PlayerTowerAttackSystem(store, logger, playerId, gameConfig, techTreeSystem);
            logger.Log("[BOOTSTRAP]      PlayerTowerAttackSystem created successfully!");

            // 订阅波次完成事件 → 产出研究点数
            waveSpawningSystem.OnWaveComplete += () => techTreeSystem.OnWaveComplete();
            waveSpawningSystem.OnWaveComplete += () => interestSystem.OnWaveComplete();
            waveSpawningSystem.OnWaveComplete += () => saveSystem?.SaveCheckpoint();
            // 订阅波次开始事件 → 同步波次伤害缩放到所有攻击系统
            waveSpawningSystem.OnWaveStart += () =>
            {
                int wave = waveSpawningSystem.GetCurrentWave();
                playerTowerAttackSystem.SetWaveNumber(wave);
                towerAttackSystem.SetWaveNumber(wave);
                skillSystem.SetWaveNumber(wave);
                comboSystem.ResetCombo(playerId);
                waveMutatorSystem.OnWaveStart(wave);
            };

            // 初始化统一帧调度器
            scheduler = new FrameScheduler(store, gameConfig);
            scheduler.WaveSpawning = waveSpawningSystem;
            scheduler.EnemyAI = enemyAISystem;
            scheduler.EnemyAbility = enemyAbilitySystem;
            scheduler.EnemyMovement = enemyMovementSystem;
            scheduler.PlayerTowerAttack = playerTowerAttackSystem;
            scheduler.TowerAttack = towerAttackSystem;
            scheduler.TowerSynergy = towerSynergySystem;
            scheduler.AuraTower = auraTowerSystem;
            scheduler.Projectile = projectileSystem;
            scheduler.Terrain = terrainSystem;
            scheduler.Pathfinding = pathfindingSystem;
            scheduler.WaveMutator = waveMutatorSystem;
            scheduler.Interest = interestSystem;
            scheduler.Skill = skillSystem;
            scheduler.Buff = buffSystem;
            scheduler.Combo = comboSystem;
            scheduler.AutoSkill = autoSkillSystem;
            scheduler.Gold = goldSystem;
            scheduler.Upgrade = upgradeSystem;

            // 初始化地形网格（方向二：地图地块系统）
            if (gameConfig.MapTerrainGrid != null && gameConfig.MapTerrainGrid.Length > 0)
            {
                int h = gameConfig.MapTerrainGrid.Length;
                int w = h > 0 ? gameConfig.MapTerrainGrid[0].Length : 0;
                store.InitTerrainGrid(w, h, gameConfig.MapTerrainGrid);
                logger.Log($"[BOOTSTRAP]    - Terrain grid initialized: {w}x{h}");
            }

            // 初始化状态机
            stateMachine = new StateMachine();
            scheduler.Phase = GameState.BuildPhase;

            // 注册 phase 切换回调：scheduler.Phase 跟随状态机同步
            stateMachine.OnEnter(GameState.BuildPhase, () => { scheduler.Phase = GameState.BuildPhase; });
            stateMachine.OnEnter(GameState.WavePhase, () => { scheduler.Phase = GameState.WavePhase; });
            stateMachine.OnEnter(GameState.Intermission, () => { scheduler.Phase = GameState.WavePhase; }); // intermission 仍运行战斗引擎（显示信息）

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

                // ── Phase: BuildPhase ──────────────────────────────────────────
                // Transition from Init → BuildPhase and show enter message
                if (stateMachine.TransitionTo(GameState.BuildPhase))
                {
                    var pb = gameConfig.GetPhaseBehavior("BuildPhase");
                    string msg = pb?.EnterMessage ?? "[PHASE] Build Phase — place your towers!";
                    Console.WriteLine();
                    Console.WriteLine("═══════════════════════════════════════════");
                    Console.WriteLine("  " + msg);
                    Console.WriteLine("═══════════════════════════════════════════");
                    Console.WriteLine();
                }

                // 渲染初始地图（SOA）
                Console.WriteLine();
                logger.Log("========================================");
                store.RebuildSpatialGrid();
                mapSystem.Update();
                logger.Log("========================================");

                // [测试] 自动部署防御塔（使用真实 TowerConfig.Type 名称，使 debuff 参数生效 — P1 修复）
                logger.Log("[TEST] 自动部署防御塔...");
                int towerId1 = towerPlacementSystem.PlaceTower(2, 5, "Basic", 15.0f, 3, 1.5f, 100f);
                int towerId2 = towerPlacementSystem.PlaceTower(7, 12, "Sniper", 25.0f, 5, 0.8f, 200f);

                // [测试] 升级塔（使用真实分配的 ID）
                logger.Log("[TEST] 尝试升级塔...");
                store.SetPlayerGold(store.PlayerEntityId, 500f); // 给金币
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
                    // 注意：Gold/Upgrade/Skill cooldown 已由 TickGameTurn 处理
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
                    // Enemy leaked — decrement base lives
                    store.DecrementPlayerBaseLives(playerId);
                    int remaining = store.GetPlayerBaseLives(playerId);
                    logger.Log("[INFO] Enemy reached bottom! Base lives: " + remaining + " remaining.");

                    // Remove the enemy that reached bottom (leaked)
                    store.QueueEnemyDeath(enemyId, playerId);

                    if (remaining <= 0)
                    {
                        logger.Log("[INFO] Game Over! No base lives remaining.");
                        return true;  // game over
                    }
                    return false;  // still alive, continue
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
