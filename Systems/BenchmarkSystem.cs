using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Components;

namespace BattleSystemECS.Systems
{
    internal enum BenchmarkCompositionContract
    {
        ManualMergedLoop,
        ProductionRegistryGraph
    }

    internal enum BenchmarkRunnerKind
    {
        ManualMerged,
        ProductionGraphFixed,
        GraphFullGame
    }

    internal readonly struct BenchmarkScenarioDefinition
    {
        public int Mode { get; }
        public BenchmarkRunnerKind Runner { get; }
        public BenchmarkCompositionContract Composition { get; }
        public int EnemyCount { get; }
        public int Frames { get; }
        public int WarmupFrames { get; }
        public FrameScenarioKind ScenarioKind { get; }
        public bool IsHarness { get; }

        public BenchmarkScenarioDefinition(int mode,BenchmarkRunnerKind runner,
            BenchmarkCompositionContract composition,int enemyCount,int frames,
            int warmupFrames,FrameScenarioKind scenarioKind=FrameScenarioKind.Gameplay,bool isHarness=false)
        {Mode=mode;Runner=runner;Composition=composition;EnemyCount=enemyCount;Frames=frames;WarmupFrames=warmupFrames;ScenarioKind=scenarioKind;IsHarness=isHarness;}

        public BenchmarkScenarioDefinition ForHarness(int enemyCount) =>
            new BenchmarkScenarioDefinition(Mode,Runner,Composition,enemyCount,1,0,ScenarioKind,true);
    }

    internal readonly struct BenchmarkExecutionEvidence
    {
        public BenchmarkCompositionContract Composition { get; }
        public int FramesExecuted { get; }
        public int BeginFrameCalls { get; }
        public int ManualMergedCalls { get; }
        public int GraphTickCalls { get; }
        public bool GraphSealed { get; }
        public string CompositionFingerprint { get; }
        public GameState FinalState { get; }
        public int PopulationMin { get; }
        public int PopulationMax { get; }
        public int PopulationEnd { get; }
        public int Kills { get; }
        private readonly int[] _stateEntryCounts;
        public BenchmarkExecutionEvidence(BenchmarkCompositionContract composition,int framesExecuted,
            int beginFrameCalls,int manualMergedCalls,int graphTickCalls,bool graphSealed,string compositionFingerprint,
            GameState finalState,int[] stateEntryCounts,int populationMin,int populationMax,int populationEnd,int kills)
        {
            Composition=composition;FramesExecuted=framesExecuted;BeginFrameCalls=beginFrameCalls;
            ManualMergedCalls=manualMergedCalls;GraphTickCalls=graphTickCalls;GraphSealed=graphSealed;
            CompositionFingerprint=compositionFingerprint;FinalState=finalState;
            _stateEntryCounts=(int[])stateEntryCounts.Clone();
            PopulationMin=populationMin;PopulationMax=populationMax;PopulationEnd=populationEnd;Kills=kills;
        }
        public int StateEntryCount(GameState state)=>_stateEntryCounts[(int)state];
    }

    /// <summary>
    /// 压测入口。mode 2/4 是固定人口吞吐：目标数量的敌人在全部测量帧保持存活，
    /// 禁止靠「开场秒杀后测空场」刷 FPS。mode 5 是完整局观察，人口随波次变化。
    /// </summary>
    public class BenchmarkSystem
    {
        // 高 HP 保证 500 帧战斗写入仍不把人口打空；移速 0 钉在交战带内。
        // 这不是回满血 soak：开局一次设血，测量期间不补 HP、不补波。
        private const float BENCH_ENEMY_HEALTH = 1e9f;
        private const float BENCH_ENEMY_SPEED = 0f;
        private const string MergedLoopFingerprint = "manual-merged-loop:v2-fixed-population";
        // mode 2/4 铺满 PlayerMaxTowers=20；mode 5 中场 8 座。只放会开火的类型。
        private const int BenchFullTowerCount = 20;
        private const int BenchObservationTowerCount = 8;
        // mode 5 只缩放内存里的关卡表，不写 game_config.json。批量加大避免「每波变多但仍每帧 5 只」。
        private const int BenchWaveCountMultiplier = 3;
        private const int BenchObservationSpawnBatch = 15;

        private readonly struct BenchTowerSpec
        {
            public readonly TowerType Type;
            public readonly float X, Y, Damage, Speed, Cost;
            public readonly int Range;
            public BenchTowerSpec(TowerType type, float x, float y, float damage, int range, float speed, float cost)
            {
                Type = type; X = x; Y = y; Damage = damage; Range = range; Speed = speed; Cost = cost;
            }
        }

        // 8 Basic + 4 Sniper + 4 AOE + 4 Tesla，对齐 per-type cap，覆盖 y=4..16 交战带。
        private static readonly BenchTowerSpec[] FullTowerLoadout =
        {
            new BenchTowerSpec(TowerType.Sniper, 1f, 16f, 25f, 5, 0.8f, 200f),
            new BenchTowerSpec(TowerType.Sniper, 3f, 16f, 25f, 5, 0.8f, 200f),
            new BenchTowerSpec(TowerType.Sniper, 6f, 16f, 25f, 5, 0.8f, 200f),
            new BenchTowerSpec(TowerType.Sniper, 8f, 16f, 25f, 5, 0.8f, 200f),
            new BenchTowerSpec(TowerType.Tesla, 1f, 13f, 18f, 3, 1.2f, 120f),
            new BenchTowerSpec(TowerType.Tesla, 3f, 13f, 18f, 3, 1.2f, 120f),
            new BenchTowerSpec(TowerType.Tesla, 6f, 13f, 18f, 3, 1.2f, 120f),
            new BenchTowerSpec(TowerType.Tesla, 8f, 13f, 18f, 3, 1.2f, 120f),
            new BenchTowerSpec(TowerType.AOE, 1f, 10f, 20f, 3, 1.0f, 150f),
            new BenchTowerSpec(TowerType.AOE, 3f, 10f, 20f, 3, 1.0f, 150f),
            new BenchTowerSpec(TowerType.AOE, 6f, 10f, 20f, 3, 1.0f, 150f),
            new BenchTowerSpec(TowerType.AOE, 8f, 10f, 20f, 3, 1.0f, 150f),
            new BenchTowerSpec(TowerType.Basic, 1f, 7f, 15f, 3, 1.0f, 50f),
            new BenchTowerSpec(TowerType.Basic, 3f, 7f, 15f, 3, 1.0f, 50f),
            new BenchTowerSpec(TowerType.Basic, 6f, 7f, 15f, 3, 1.0f, 50f),
            new BenchTowerSpec(TowerType.Basic, 8f, 7f, 15f, 3, 1.0f, 50f),
            new BenchTowerSpec(TowerType.Basic, 2f, 4f, 15f, 3, 1.0f, 50f),
            new BenchTowerSpec(TowerType.Basic, 4f, 4f, 15f, 3, 1.0f, 50f),
            new BenchTowerSpec(TowerType.Basic, 5f, 4f, 15f, 3, 1.0f, 50f),
            new BenchTowerSpec(TowerType.Basic, 7f, 4f, 15f, 3, 1.0f, 50f),
        };

        private static readonly BenchTowerSpec[] ObservationTowerLoadout =
        {
            new BenchTowerSpec(TowerType.Basic, 2f, 5f, 15f, 3, 1.5f, 100f),
            new BenchTowerSpec(TowerType.Basic, 7f, 5f, 15f, 3, 1.5f, 100f),
            new BenchTowerSpec(TowerType.AOE, 2f, 9f, 20f, 3, 1.0f, 150f),
            new BenchTowerSpec(TowerType.Sniper, 7f, 9f, 25f, 5, 0.8f, 200f),
            new BenchTowerSpec(TowerType.Tesla, 2f, 13f, 18f, 3, 1.2f, 120f),
            new BenchTowerSpec(TowerType.Basic, 7f, 13f, 15f, 3, 1.5f, 100f),
            new BenchTowerSpec(TowerType.Sniper, 4f, 16f, 25f, 5, 0.8f, 200f),
            new BenchTowerSpec(TowerType.AOE, 6f, 16f, 20f, 3, 1.0f, 150f),
        };

        private ComponentStore store;
        private BenchmarkCompositionContract _executedComposition;
        private int _executedFrames;
        private int _beginFrameCalls;
        private int _manualMergedCalls;
        private int _graphTickCalls;
        private bool _graphSealed;
        private string _compositionFingerprint=string.Empty;
        private readonly int[] _stateEntryCounts=new int[Enum.GetValues(typeof(GameState)).Length];
        private GameState _finalState=GameState.Init;
        private int _populationMin;
        private int _populationMax;
        private int _populationEnd;

        public BenchmarkSystem(ComponentStore store) { this.store = store; }

        internal static BenchmarkScenarioDefinition GetScenarioDefinition(int mode) => mode switch
        {
            2 => new BenchmarkScenarioDefinition(2,BenchmarkRunnerKind.ManualMerged,
                BenchmarkCompositionContract.ManualMergedLoop,10000,500,5),
            4 => new BenchmarkScenarioDefinition(4,BenchmarkRunnerKind.ProductionGraphFixed,
                BenchmarkCompositionContract.ProductionRegistryGraph,10000,500,0,
                FrameScenarioKind.FixedPopulationBenchmark),
            5 => new BenchmarkScenarioDefinition(5,BenchmarkRunnerKind.GraphFullGame,
                BenchmarkCompositionContract.ProductionRegistryGraph,0,0,0),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported benchmark mode.")
        };

        internal BenchmarkExecutionEvidence RunCompositionHarness(int mode,int enemyCount=64)
        {
            return Dispatch(GetScenarioDefinition(mode).ForHarness(enemyCount));
        }

        private void SpawnFixedCombatPopulation(int count, GameConfig gameConfig, Random random)
        {
            for (int i = 0; i < count; i++)
            {
                float x = random.Next(0, 10);
                float y = (float)random.Next(10, 19);
                int id = store.AddEnemy(x, y, BENCH_ENEMY_SPEED, BENCH_ENEMY_HEALTH, BENCH_ENEMY_HEALTH, 10f, 0, 1);
                store.SetEnemyAIAction(id, "");
                store.SetEntityName(id, $"NormalL1W1E{i}");
                store.EnemyBehaviorTree[id] = gameConfig.GetCachedBehaviorTree("Normal");
            }
        }

        private int PlaceBenchmarkTowers(BenchTowerSpec[] loadout)
        {
            for (int i = 0; i < loadout.Length; i++)
            {
                BenchTowerSpec spec = loadout[i];
                int id = store.CreateEntity();
                store.AddTower(id, spec.Type, spec.Damage, spec.Range, spec.Speed, 1, spec.Cost);
                store.PositionX[id] = spec.X;
                store.PositionY[id] = spec.Y;
            }
            store.RebuildSpatialGrid();
            return loadout.Length;
        }

        private static void ScaleBenchmarkWaves(GameConfig gameConfig, int multiplier)
        {
            if (gameConfig?.Levels == null || multiplier == 1) return;
            for (int li = 0; li < gameConfig.Levels.Count; li++)
            {
                var level = gameConfig.Levels[li];
                if (level?.Waves == null) continue;
                for (int wi = 0; wi < level.Waves.Count; wi++)
                {
                    var wave = level.Waves[wi];
                    if (wave == null) continue;
                    wave.EnemyCount *= multiplier;
                    wave.ExpectedKillCount *= multiplier;
                    if (wave.EnemyTypes == null) continue;
                    for (int ti = 0; ti < wave.EnemyTypes.Count; ti++)
                    {
                        var entry = wave.EnemyTypes[ti];
                        if (entry != null) entry.Count *= multiplier;
                    }
                }
            }
        }

        private void NotePopulation()
        {
            int n = store.GetActiveEnemyCount();
            if (n < _populationMin) _populationMin = n;
            if (n > _populationMax) _populationMax = n;
        }

        private void WriteFixedPopulationReport(int expected, int frames, double msTotal)
        {
            _populationEnd = store.GetActiveEnemyCount();
            bool valid = expected > 0 && _populationMin == expected && _populationMax == expected &&
                         _populationEnd == expected;
            double fps = 1000.0 / (msTotal / frames);
            Console.WriteLine($"[BENCHMARK] Contract: fixed-population {expected} alive × {frames} frames (high-HP, speed 0, no extra waves)");
            Console.WriteLine($"[BENCHMARK] Population: min={_populationMin} max={_populationMax} end={_populationEnd} kills={store.TotalKills} DeathResolve={store.DeathResolveCount} valid={(valid ? "true" : "FALSE")}");
            Console.WriteLine($"[BENCHMARK] Throughput: {fps:F0} FPS  ({msTotal/frames:F2} ms/frame)");
            if (!valid)
                Console.WriteLine("[BENCHMARK] INVALID: population drifted; FPS is not a 10K-load measurement.");
        }

        private BenchmarkExecutionEvidence Dispatch(BenchmarkScenarioDefinition definition)
        {
            _executedComposition=definition.Composition;
            _executedFrames=0;
            _beginFrameCalls=0;
            _manualMergedCalls=0;
            _graphTickCalls=0;
            _graphSealed=false;
            _compositionFingerprint=string.Empty;
            Array.Clear(_stateEntryCounts,0,_stateEntryCounts.Length);
            _finalState=GameState.Init;
            _populationMin=int.MaxValue;
            _populationMax=0;
            _populationEnd=0;
            switch(definition.Runner)
            {
                case BenchmarkRunnerKind.ManualMerged:
                    RunMergedSystemBenchmark(definition.EnemyCount,definition.Frames,definition.WarmupFrames);
                    break;
                case BenchmarkRunnerKind.ProductionGraphFixed:
                    RunProductionGraphBenchmark(definition);
                    break;
                case BenchmarkRunnerKind.GraphFullGame:
                    RunFullGameBenchmark(definition);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(definition),definition.Runner,"Unsupported benchmark runner.");
            }
            return new BenchmarkExecutionEvidence(_executedComposition,_executedFrames,_beginFrameCalls,
                _manualMergedCalls,_graphTickCalls,_graphSealed,_compositionFingerprint,_finalState,_stateEntryCounts,
                _populationMin==int.MaxValue?0:_populationMin,_populationMax,_populationEnd,store.TotalKills);
        }

        internal BenchmarkExecutionEvidence RunFixedPopulationSample(int mode,int enemyCount,int frames)
        {
            var baseline=GetScenarioDefinition(mode);
            if(baseline.Runner==BenchmarkRunnerKind.GraphFullGame)
                throw new ArgumentOutOfRangeException(nameof(mode),"mode 5 is a full-game observation, not fixed-population.");
            var definition=new BenchmarkScenarioDefinition(baseline.Mode,baseline.Runner,baseline.Composition,
                enemyCount,frames,0,baseline.ScenarioKind,false);
            return Dispatch(definition);
        }

        public void RunBenchmark(int scenario)
        {
            if (scenario == 3)
            {
                RunMicroBenchmark(10000, 500);
                return;
            }

            int mode=scenario==4||scenario==5?scenario:2;
            BenchmarkScenarioDefinition definition=GetScenarioDefinition(mode);
            if(mode==2&&scenario!=2)
                definition=new BenchmarkScenarioDefinition(2,definition.Runner,definition.Composition,
                    scenario,definition.Frames,definition.WarmupFrames);
            Dispatch(definition);
        }

        private void RunMergedSystemBenchmark(int scenario,int frames,int warmupFrames)
        {
            _compositionFingerprint=MergedLoopFingerprint;
            Console.WriteLine($"\n[BENCHMARK] Merged hot path (fixed population): {scenario} entities");
            Console.WriteLine($"[BENCHMARK] Composition: {_compositionFingerprint} (lower bound; not production FrameGraph wiring evidence).");
            Console.WriteLine($"[BENCHMARK] Composition-Fingerprint: {_compositionFingerprint}");

            var logger = new ConsoleLogger();
            var gameConfig = new GameConfig();
            GameConfigLoader.LoadConfig(logger);

            int playerId = 1;
            store.AddPlayer(playerId, 10f, 1f, 100f, 1, 20);
            store.PlayerMaxHealth[playerId] = 200f;
            store.PlayerCurrentHealth[playerId] = 200f;
            store.PlayerAttackDamage[playerId] = 100f;
            store.PlayerAttackRange[playerId] = 10f;
            store.PositionX[playerId] = 5f;
            store.PositionY[playerId] = 0f;
            store.SetPlayerGold(playerId, 9999f);

            var random = new Random(42);
            SpawnFixedCombatPopulation(scenario, gameConfig, random);
            Console.WriteLine($"[BENCHMARK] Spawned {scenario} enemies (health={BENCH_ENEMY_HEALTH:E0}, speed={BENCH_ENEMY_SPEED})");

            // 固定人口吞吐：不跑 WaveSpawning，避免补波或把战场打空。
            var enemyAbility   = new EnemyAbilitySystem(store, logger, playerId, gameConfig);
            var benchTechTree  = new TechTreeSystem(store, logger, playerId, null, gameConfig);
            var enemyAI       = new EnemyAISystem(store, logger, playerId, gameConfig, enemyAbility, benchTechTree);
            var enemyMovement = new EnemyMovementSystem(store, playerId);
            var playerAttack  = new PlayerTowerAttackSystem(store, logger, playerId, gameConfig);
            var towerAttack  = new TowerAttackSystem(store, logger, null);
            var auraTower    = new AuraTowerSystem(store);
            // Round 173 Direction 1 — Shrine Tower. No-op when no Shrine is on the field.
            var towerShrine  = new TowerShrineSystem(store);
            var projectile   = new ProjectileSystem(store, logger);
            var gold         = new GoldSystem(store, logger);
            var towerUpgrade = new TowerUpgradeSystem(store, logger, gameConfig);
            var upgrade      = new UpgradeSystem(store, logger, playerId, gameConfig, towerUpgrade);
            var skill        = new SkillSystem(store, logger, playerId, gameConfig);
            skill.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            var buffSystem   = new BuffSystem(store, playerId);
            skill.InjectDotSystem(buffSystem);
            var comboSystem  = new ComboSystem(store, gameConfig.Combo);
            var map          = new MapSystem(logger, store);
            map.SetMapSize(10, 20);
            var pathfinding  = new PathfindingSystem(store);
            enemyMovement.SetPathfindingSystem(pathfinding);

            int towers = PlaceBenchmarkTowers(FullTowerLoadout);
            Console.WriteLine($"[BENCHMARK] Towers: {towers} (full cap {BenchFullTowerCount})");

            // Warm-up (BeginFrame is optional since Resolve clears _deathQueue)
            for (int f = 0; f < warmupFrames; f++)
            {
                int turn = f + 6;
                store.BeginFrame();
                enemyAI.SetTurn(turn); enemyAI.Update();
                enemyMovement.SetTurn(turn); enemyMovement.Update();
                playerAttack.SetTurn(turn); playerAttack.Update();
                towerAttack.SetTurn(turn); towerAttack.Update(1f);
                gold.SetTurn(turn); gold.Update();
                store.ResolveEnemiesKilledThisFrame();
            }

            ConsoleLogger.EnableLog = false;

            long tEnemyAI = 0, tMoveAttack = 0;
            long tTowerAttack = 0, tGold = 0;
            long tUpgrade = 0, tSkill = 0, tMap = 0;
            long tGridRebuild = 0;

            var totalSw = Stopwatch.StartNew();

            // Pre-compute move direction lookup to eliminate switch in hot path
            // 12 values: Forward(0), Retreat(1), FastRetreat(2), StrafeLeft(3), StrafeRight(4), Charge(5), Dodge(6), Knockback(7), Die(8), StunAoe(9), SlowAoe(10), Unknown(11)
            var moveDir = new sbyte[] { -1, 1, 1, -1, 1, -1, 0, 0, 0, 0, 0, 0 };
            // index: (int)EnemyActionType → direction (-1=forward, 0=stand, 1=retreat)

            for (int f = 0; f < frames; f++)
            {
                int turn = f + 6;
                store.BeginFrame(); // BeginFrame called each frame so Resolve clears _deathQueue
                _beginFrameCalls++;
                _manualMergedCalls++;
                _executedFrames++;
                var sw = new Stopwatch();

                // WaveSpawning 故意不跑：固定人口合同。
                sw.Restart(); enemyAI.SetTurn(turn); enemyAI.Update(); tEnemyAI += sw.ElapsedTicks;
                sw.Restart(); store.RebuildSpatialGrid(); tGridRebuild += sw.ElapsedTicks;
                sw.Restart();

                // Merged Movement + PlayerAttack in one Parallel.For
                var activeList = store.GetAllActiveEnemyIds();
                int count = activeList.Count;
                float px = store.PositionX[playerId];
                float py = store.PositionY[playerId];
                float ad = store.GetPlayerAttackDamage(playerId);
                float ar = store.GetPlayerAttackRange(playerId);
                int rsq = (int)(ar * ar);

                // Process buffs
                var buffs = store.GetPlayerBuffs(playerId);
                float fad = ad;
                if (buffs.Count > 0)
                {
                    foreach (string buff in buffs)
                    {
                        if (buff == "Attack+10%") fad *= 1.1f;
                    }
                }

                // long goldAcc = 0; // kept for future gold accumulation tracking
                const int batchSize = 512;
                int numBatches = (count + batchSize - 1) / batchSize;

                Parallel.For(0, numBatches, ParallelOptionsCache.HotPath, batchIdx =>
                {
                    int start = batchIdx * batchSize;
                    int end = Math.Min(start + batchSize, count);

                    for (int i = start; i < end; i++)
                    {
                        int enemyId = activeList[i];
                        if (!store.EnemyActive[enemyId]) continue;

                        // --- Movement ---
                        float moveSpeed = store.EnemyMoveSpeed[enemyId];
                        float x = store.PositionX[enemyId];
                        float y = store.PositionY[enemyId];
                        var ae = store.GetEnemyActionEnum(enemyId);

                        if (ae != EnemyActionType.Dodge)
                        {
                            int actionIdx = (int)ae;
                            sbyte dir = actionIdx < moveDir.Length ? moveDir[actionIdx] : (sbyte)0;
                            store.PositionY[enemyId] = y + dir * moveSpeed;
                        }
                        else
                        {
                            store.PositionY[enemyId] = y - moveSpeed * 0.5f;
                        }

                        // --- PlayerAttack ---
                        y = store.PositionY[enemyId];
                        if (y <= py) continue;
                        float dx = x - px;
                        if (dx * dx > rsq) continue;
                        float hp = store.EnemyHealth[enemyId];
                        if (hp <= 0f) continue;
                        hp -= fad;
                        store.EnemyHealth[enemyId] = hp;
                        if (hp <= 0f)
                        {
                            store.QueueEnemyDeath(enemyId, playerId);
                        }
                    }
                });
                store.ResolveEnemiesKilledThisFrame();
                tMoveAttack += sw.ElapsedTicks;

                sw.Restart(); towerAttack.SetTurn(turn); towerAttack.Update(1f); tTowerAttack += sw.ElapsedTicks;
                sw.Restart(); auraTower.SetTurn(); auraTower.ResolveAuraBuffs();
                // Round 173 Direction 1 — Shrine aura resolve. O(1) fast-path when
                //   no Shrine is on the field (sentinel _anyShrineOnField).
                towerShrine.SetTurn(); towerShrine.ResolveShrineBuffs();
                sw.Restart(); projectile.Update(1f);
                sw.Restart(); gold.SetTurn(turn); gold.Update(); tGold += sw.ElapsedTicks;
                sw.Restart(); upgrade.Update(); tUpgrade += sw.ElapsedTicks;
                sw.Restart(); skill.Update(1f);
                skill.AutoCastBestSkill();
                skill.ResolveSkillDamage();
                buffSystem.Update(1f);
                comboSystem.Update(1f);
                buffSystem.ResolveDotDamage();
                store.ResolveEnemiesKilledThisFrame();  // after DoT deaths
                long tSkillAndBuff = sw.ElapsedTicks;
                sw.Restart(); tSkill += tSkillAndBuff;
                /* map.Update() = skip */
                NotePopulation();
            }

            totalSw.Stop();
            ConsoleLogger.EnableLog = true;

            double ticksPerMs = Stopwatch.Frequency / 1000.0;
            double msTotal = totalSw.Elapsed.TotalMilliseconds;

            Console.WriteLine($"\n[BENCHMARK] Per-system timing ({frames} frames, {scenario} enemies):");
            Console.WriteLine($"[BENCHMARK]   WaveSpawning:   (suppressed; fixed-population contract)");
            Console.WriteLine($"[BENCHMARK]   EnemyAI:        {tEnemyAI/ticksPerMs,7:F2} ms  ({tEnemyAI/ticksPerMs/msTotal*100,5:F1}%)");
            Console.WriteLine($"[BENCHMARK]   GridRebuild:   {tGridRebuild/ticksPerMs,7:F2} ms  ({tGridRebuild/ticksPerMs/msTotal*100,5:F1}%)");
            Console.WriteLine($"[BENCHMARK]   MoveAttack:     {tMoveAttack/ticksPerMs,7:F2} ms  ({tMoveAttack/ticksPerMs/msTotal*100,5:F1}%)");
            Console.WriteLine($"[BENCHMARK]   TowerAttack:    {tTowerAttack/ticksPerMs,7:F2} ms  ({tTowerAttack/ticksPerMs/msTotal*100,5:F1}%)");
            Console.WriteLine($"[BENCHMARK]   Gold:           {tGold/ticksPerMs,7:F2} ms  ({tGold/ticksPerMs/msTotal*100,5:F1}%)");
            Console.WriteLine($"[BENCHMARK]   Upgrade:        {tUpgrade/ticksPerMs,7:F2} ms  ({tUpgrade/ticksPerMs/msTotal*100,5:F1}%)");
            Console.WriteLine($"[BENCHMARK]   Skill:          {tSkill/ticksPerMs,7:F2} ms  ({tSkill/ticksPerMs/msTotal*100,5:F1}%)");
            Console.WriteLine($"[BENCHMARK]   Map:            {tMap/ticksPerMs,7:F2} ms  ({tMap/ticksPerMs/msTotal*100,5:F1}%)");
            Console.WriteLine($"[BENCHMARK]   ----------------------------------------");
            Console.WriteLine($"[BENCHMARK]   TOTAL:          {msTotal,7:F2} ms");
            WriteFixedPopulationReport(scenario, frames, msTotal);
        }

        private void RunMicroBenchmark(int enemyCount, int frames)
        {
            Console.WriteLine($"\n[MICRO] EnemyAI.Update() cost breakdown: {enemyCount} enemies x {frames} frames");

            var logger = new ConsoleLogger();
            var gameConfig = new GameConfig();
            GameConfigLoader.LoadConfig(logger);

            int playerId = 1;
            store.PlayerMaxHealth[playerId] = 200f;
            store.PlayerCurrentHealth[playerId] = 200f;
            store.PositionX[playerId] = 5f;
            store.PositionY[playerId] = 0f;
            store.SetPlayerGold(playerId, 9999f);

            var random = new Random(42);
            SpawnFixedCombatPopulation(enemyCount, gameConfig, random);

            var activeEnemyIds = store.GetAllActiveEnemyIds();
            int totalIters = enemyCount * frames;

            foreach (var eid in activeEnemyIds)
            {
                var bt = store.EnemyBehaviorTree[eid];
                string action = BTCachedTreeEvaluator.Evaluate(bt, eid, store, playerId, 1);
                _ = EnemyAISystem.StringToActionEnum(action);
            }

            double ticksPerMs = Stopwatch.Frequency / 1000.0;
            long t1 = 0, t2 = 0, t3 = 0, t4 = 0, t5 = 0, t6 = 0;

            for (int f = 0; f < frames; f++)
            {
                long start;

                start = Stopwatch.GetTimestamp();
                foreach (int enemyId in activeEnemyIds) { /* no-op */ }
                t1 += Stopwatch.GetTimestamp() - start;

                start = Stopwatch.GetTimestamp();
                foreach (int enemyId in activeEnemyIds) { var _ = store.EnemyBehaviorTree[enemyId]; }
                t2 += Stopwatch.GetTimestamp() - start;

                start = Stopwatch.GetTimestamp();
                foreach (int enemyId in activeEnemyIds)
                {
                    var bt = store.EnemyBehaviorTree[enemyId];
                    string _a = BTCachedTreeEvaluator.Evaluate(bt, enemyId, store, playerId, f + 1);
                }
                t3 += Stopwatch.GetTimestamp() - start;

                start = Stopwatch.GetTimestamp();
                foreach (int enemyId in activeEnemyIds)
                {
                    var bt = store.EnemyBehaviorTree[enemyId];
                    string a = BTCachedTreeEvaluator.Evaluate(bt, enemyId, store, playerId, f + 1);
                    var _e = EnemyAISystem.StringToActionEnum(a);
                }
                t4 += Stopwatch.GetTimestamp() - start;

                start = Stopwatch.GetTimestamp();
                foreach (int enemyId in activeEnemyIds)
                {
                    var bt = store.EnemyBehaviorTree[enemyId];
                    string a = BTCachedTreeEvaluator.Evaluate(bt, enemyId, store, playerId, f + 1);
                    var e = EnemyAISystem.StringToActionEnum(a);
                    store.SetEnemyActionEnum(enemyId, e);
                }
                t5 += Stopwatch.GetTimestamp() - start;

                start = Stopwatch.GetTimestamp();
                foreach (int enemyId in activeEnemyIds)
                {
                    if (!store.EnemyActive[enemyId]) continue;
                    var bt = store.EnemyBehaviorTree[enemyId];
                    string a = BTCachedTreeEvaluator.Evaluate(bt, enemyId, store, playerId, f + 1);
                    var e = EnemyAISystem.StringToActionEnum(a);
                    store.SetEnemyActionEnum(enemyId, e);
                }
                t6 += Stopwatch.GetTimestamp() - start;
            }

            Console.WriteLine($"[MICRO] Per-operation incremental cost ({totalIters:N0} iterations):");
            Console.WriteLine($"[MICRO]   1. Empty foreach:           {t1/ticksPerMs,7:F2} ms");
            Console.WriteLine($"[MICRO]   2. + EnemyBehaviorTree[] r: {t2/ticksPerMs - t1/ticksPerMs,7:F2} ms  (incremental)");
            Console.WriteLine($"[MICRO]   3. + BTCachedTreeEval:       {t3/ticksPerMs - t2/ticksPerMs,7:F2} ms  (incremental)");
            Console.WriteLine($"[MICRO]   4. + StringToActionEnum:    {t4/ticksPerMs - t3/ticksPerMs,7:F2} ms  (incremental)");
            Console.WriteLine($"[MICRO]   5. + SetEnemyActionEnum:    {t5/ticksPerMs - t4/ticksPerMs,7:F2} ms  (incremental)");
            Console.WriteLine($"[MICRO]   6. + EnemyActive check:     {t6/ticksPerMs - t5/ticksPerMs,7:F2} ms  (incremental)");
            Console.WriteLine($"[MICRO]   ----------------------------------------");
            Console.WriteLine($"[MICRO]   Steps 1-6 sum:               {t6/ticksPerMs,7:F2} ms");
        }

        // ── 模式 4：生产 Registry FrameGraph 压测 ──────────────────────────────────
        private void RunProductionGraphBenchmark(BenchmarkScenarioDefinition definition)
        {
            Console.WriteLine($"\n[BENCHMARK] Production FrameGraph (fixed population): {definition.EnemyCount} enemies x {definition.Frames} frames");

            var logger = new ConsoleLogger();
            var gameConfig = GameConfigLoader.LoadConfig(logger);

            int playerId = 1;
            store.AddPlayer(playerId, 10f, 1f, 100f, 1, 20);
            store.PlayerMaxHealth[playerId] = 200f;
            store.PlayerCurrentHealth[playerId] = 200f;
            store.PlayerAttackDamage[playerId] = 100f;
            store.PlayerAttackRange[playerId] = 10f;
            store.PlayerAttackSpeed[playerId] = 1f;
            store.PositionX[playerId] = 5f;
            store.PositionY[playerId] = 0f;
            store.PositionActive[playerId] = true;
            store.SetPlayerGold(playerId, 9999f);

            var runtime=BenchmarkCompositionFactory.Create(store,gameConfig,logger,playerId,
                scenarioKind:definition.ScenarioKind);
            FrameScheduler scheduler=runtime.Scheduler;
            _graphSealed=scheduler.IsCompositionSealed;
            _compositionFingerprint=runtime.ExecutionFingerprint(definition.EnemyCount);
            AttachStateEvidence(runtime.StateMachine);
            TransitionBenchmarkState(runtime.StateMachine,GameState.BuildPhase);
            Console.WriteLine($"[BENCHMARK] Composition: production-registry-frame-graph ({runtime.Fingerprint}).");
            Console.WriteLine($"[BENCHMARK] Composition-Fingerprint: {_compositionFingerprint}");

            var random = new Random(42);
            SpawnFixedCombatPopulation(definition.EnemyCount, gameConfig, random);

            int towers = PlaceBenchmarkTowers(FullTowerLoadout);
            Console.WriteLine($"[BENCHMARK] Towers: {towers} (full cap {BenchFullTowerCount})");

            TransitionBenchmarkState(runtime.StateMachine,GameState.WavePhase);

            if(definition.IsHarness)
            {
                RunGraphFrame(scheduler,1);
                NotePopulation();
                _populationEnd=store.GetActiveEnemyCount();
                return;
            }

            ConsoleLogger.EnableLog = false;
            var totalSw = Stopwatch.StartNew();

            for (int f = 0; f < definition.Frames; f++)
            {
                RunGraphFrame(scheduler,f+6);
                NotePopulation();
            }

            totalSw.Stop();
            ConsoleLogger.EnableLog = true;

            double msTotal = totalSw.Elapsed.TotalMilliseconds;
            Console.WriteLine($"\n[BENCHMARK] Production FrameGraph timing ({definition.Frames} frames, {definition.EnemyCount} enemies):");
            Console.WriteLine($"[BENCHMARK]   TOTAL:          {msTotal,7:F2} ms");
            WriteFixedPopulationReport(definition.EnemyCount, definition.Frames, msTotal);
        }

        /// <summary>
        /// 完整一局压测 — 5 关、真实波次生成、完整战斗流程。
        /// 测量从第一帧到最后一帧的总帧数和墙钟时间，计算真实游戏吞吐量。
        /// </summary>
        private void RunFullGameBenchmark(BenchmarkScenarioDefinition definition)
        {
            Console.WriteLine("\n[BENCHMARK] Full Game: 5 levels, real wave spawning, full combat pipeline");

            var logger = new ConsoleLogger();
            var gameConfig = GameConfigLoader.LoadConfig(logger);
            if (!definition.IsHarness)
                ScaleBenchmarkWaves(gameConfig, BenchWaveCountMultiplier);

            int playerId = 1;
            store.AddPlayer(playerId, 10f, 1f, 50f, 1, 20);
            store.PlayerMaxHealth[playerId] = 200f;
            store.PlayerCurrentHealth[playerId] = 200f;
            store.SetPlayerGold(playerId, 9999f);
            store.PositionX[playerId] = 5f;
            store.PositionY[playerId] = 0f;
            store.PositionActive[playerId] = true;

            // 生产压测与回归测试共用同一个组合入口，避免接线测试复制实现。
            var runtime = BenchmarkCompositionFactory.Create(store,gameConfig,logger,playerId,
                scenarioKind:definition.ScenarioKind);
            FrameScheduler scheduler = runtime.Scheduler;
            WaveSpawningSystem waveSpawning = runtime.Registry.WaveSpawning
                ?? throw new InvalidOperationException("Production benchmark requires WaveSpawningSystem.");
            if (!definition.IsHarness)
                waveSpawning.SetSpawnBatchSize(BenchObservationSpawnBatch);
            SkillSystem skill = runtime.Registry.Skill
                ?? throw new InvalidOperationException("Production benchmark requires SkillSystem.");
            _graphSealed=scheduler.IsCompositionSealed;
            _compositionFingerprint=runtime.ExecutionFingerprint(definition.EnemyCount);
            AttachStateEvidence(runtime.StateMachine);
            TransitionBenchmarkState(runtime.StateMachine,GameState.BuildPhase);
            Console.WriteLine($"[BENCHMARK] Composition: production-registry-frame-graph ({runtime.Fingerprint}).");
            Console.WriteLine($"[BENCHMARK] Composition-Fingerprint: {_compositionFingerprint}");

            int towers = PlaceBenchmarkTowers(ObservationTowerLoadout);
            Console.WriteLine($"[BENCHMARK] Towers: {towers} (observation loadout, {BenchObservationTowerCount} expected)");
            if (!definition.IsHarness)
                Console.WriteLine($"[BENCHMARK] Waves: EnemyCount ×{BenchWaveCountMultiplier}, spawn batch {BenchObservationSpawnBatch} (in-memory only; game_config.json unchanged)");

            if(definition.IsHarness)
            {
                RunGraphFrame(scheduler,1);
                TransitionBenchmarkState(runtime.StateMachine,GameState.WavePhase);
                RunGraphFrame(scheduler,2);
                TransitionBenchmarkState(runtime.StateMachine,GameState.Intermission);
                RunGraphFrame(scheduler,3);
                TransitionBenchmarkState(runtime.StateMachine,GameState.WavePhase);
                TransitionBenchmarkState(runtime.StateMachine,GameState.LevelComplete);
                RunGraphFrame(scheduler,4);
                TransitionBenchmarkState(runtime.StateMachine,GameState.BuildPhase);
                TransitionBenchmarkState(runtime.StateMachine,GameState.GameOver);
                return;
            }

            ConsoleLogger.EnableLog = false;
            var totalSw = Stopwatch.StartNew();

            int totalFrames = 0;
            int maxLevels = gameConfig.Levels.Count;
            int completedLevels = 0;
            string endReason = "Victory";

            for (int level = 1; level <= maxLevels; level++)
            {
                var levelConfig = gameConfig.GetLevelConfig(level);
                if (levelConfig == null) continue;

                if(runtime.StateMachine.CurrentState==GameState.LevelComplete)
                    TransitionBenchmarkState(runtime.StateMachine,GameState.BuildPhase);

                waveSpawning.SetLevel(level);
                store.RebuildSpatialGrid();

                // ── BuildPhase ──────────────────────────────────
                Debug.Assert(runtime.StateMachine.CurrentState==GameState.BuildPhase,"完整局压测必须由状态机进入 Build 阶段。");
                Debug.Assert(skill.CurrentPhaseContext == PhaseContextKind.Build, "完整局压测的 Build 阶段上下文未同步。");
                for (int bf = 0; bf < 10; bf++)
                {
                    totalFrames++;
                    RunGraphFrame(scheduler,totalFrames);
                    NotePopulation();
                }

                // ── WavePhase ───────────────────────────────────
                TransitionBenchmarkState(runtime.StateMachine,GameState.WavePhase);
                Debug.Assert(skill.CurrentPhaseContext == PhaseContextKind.Wave, "完整局压测的 Wave 阶段上下文未同步。");
                bool levelDone = false;
                int levelMaxFrames = 10000;  // 安全上限，防止死循环
                int levelFrameStart = totalFrames;
                while (!levelDone && (totalFrames - levelFrameStart) < levelMaxFrames)
                {
                    int waveBefore=waveSpawning.GetCurrentWave();
                    int spawningLevelBefore=waveSpawning.GetCurrentLevel();
                    totalFrames++;
                    RunGraphFrame(scheduler,totalFrames);
                    NotePopulation();

                    // 游戏结束检测：玩家死亡
                    if (store.PlayerCurrentHealth[playerId] <= 0f)
                    {
                        endReason = "PlayerDeath";
                        TransitionBenchmarkState(runtime.StateMachine,GameState.GameOver);
                        levelDone = true;
                        break;
                    }

                    // 敌人到达底部检测
                    var activeEnemyIds = store.GetCachedActiveEnemyIds();
                    bool leaked = false;
                    bool queuedLeakDeath = false;
                    foreach (var eid in activeEnemyIds)
                    {
                        if (store.EnemyActive[eid] && store.PositionY[eid] <= 0f)
                        {
                            store.DecrementPlayerBaseLives(playerId);
                            store.EnemiesLeakedThisWave[playerId]++; // track leak for adaptive difficulty
                            scheduler.QueueCurrentFrameEnemyDeath(eid, playerId);
                            queuedLeakDeath = true;
                            if (store.GetPlayerBaseLives(playerId) <= 0)
                            {
                                endReason = "BaseDestroyed";
                                leaked = true;
                                break;
                            }
                        }
                    }
                    if (queuedLeakDeath)
                        scheduler.ResolveCurrentFrameDeaths();
                    if (leaked)
                    {
                        TransitionBenchmarkState(runtime.StateMachine,GameState.GameOver);
                        levelDone = true;
                        break;
                    }

                    // 关卡完成检测：WaveSpawningSystem 内部会将 currentLevel++ 当所有波次完成
                    int spawnedLevel = waveSpawning.GetCurrentLevel();
                    bool allWavesDone = spawnedLevel > level;
                    int activeCount = store.GetCachedActiveEnemyIds().Count;
                    if (allWavesDone && activeCount == 0)
                    {
                        completedLevels++;
                        TransitionBenchmarkState(runtime.StateMachine,GameState.LevelComplete);
                        levelDone = true;
                    }
                    else if(spawningLevelBefore==waveSpawning.GetCurrentLevel()&&waveBefore!=waveSpawning.GetCurrentWave())
                    {
                        TransitionBenchmarkState(runtime.StateMachine,GameState.Intermission);
                        totalFrames++;
                        RunGraphFrame(scheduler,totalFrames);
                        NotePopulation();
                        TransitionBenchmarkState(runtime.StateMachine,GameState.WavePhase);
                    }
                }

                // 游戏结束则停止后续关卡
                if (endReason != "Victory") break;

                // 安全上限触发
                if ((totalFrames - levelFrameStart) >= levelMaxFrames)
                {
                    endReason = $"Level{level}Timeout";
                    TransitionBenchmarkState(runtime.StateMachine,GameState.GameOver);
                    Console.WriteLine($"[BENCHMARK] WARNING: Level {level} hit {levelMaxFrames} frame limit!");
                    break;
                }
            }

            if(endReason=="Victory"&&completedLevels==maxLevels)
                TransitionBenchmarkState(runtime.StateMachine,GameState.Victory);

            totalSw.Stop();
            ConsoleLogger.EnableLog = true;

            double msTotal = totalSw.Elapsed.TotalMilliseconds;
            double fps = 1000.0 * totalFrames / msTotal;

            Console.WriteLine($"\n[BENCHMARK] Full Game Results (dynamic population; not a 10K hold):");
            Console.WriteLine($"[BENCHMARK]   End reason:  {endReason}");
            Console.WriteLine($"[BENCHMARK]   Levels:      {completedLevels}/{maxLevels}");
            Console.WriteLine($"[BENCHMARK]   Total frames: {totalFrames}");
            Console.WriteLine($"[BENCHMARK]   Enemy pop:   peak={_populationMax} end={store.GetActiveEnemyCount()}");
            Console.WriteLine($"[BENCHMARK]   Towers:      {store.ActiveTowerIds.Count}");
            Console.WriteLine($"[BENCHMARK]   Wall-clock:  {msTotal:F2} ms ({msTotal/1000:F2} s)");
            Console.WriteLine($"[BENCHMARK]   Avg frame:   {msTotal/totalFrames:F3} ms/frame");
            Console.WriteLine($"[BENCHMARK]   Throughput:  {fps:F0} FPS");
        }

        private void RunGraphFrame(FrameScheduler scheduler,int turn)
        {
            scheduler.TickGameTurn(1f,turn);
            _beginFrameCalls++;
            _graphTickCalls++;
            _executedFrames++;
        }

        private void AttachStateEvidence(StateMachine stateMachine)
        {
            _finalState=stateMachine.CurrentState;
            foreach(GameState state in Enum.GetValues(typeof(GameState)))
            {
                GameState captured=state;
                stateMachine.OnEnter(captured,()=>
                {
                    _stateEntryCounts[(int)captured]++;
                    _finalState=captured;
                });
            }
        }

        private static void TransitionBenchmarkState(StateMachine stateMachine,GameState target)
        {
            if(stateMachine.CurrentState==target)return;
            if(!stateMachine.TransitionTo(target))
                throw new InvalidOperationException($"Benchmark state transition failed: {stateMachine.CurrentState} -> {target}.");
        }
    }
}
