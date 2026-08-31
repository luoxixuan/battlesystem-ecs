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
    /// <summary>
    /// Full 12-system benchmark with per-system timing breakdown.
    /// Run via: echo 2 | dotnet run
    /// </summary>
    public class BenchmarkSystem
    {
        private const float BENCH_ENEMY_HEALTH = 100f;

        private ComponentStore store;

        public BenchmarkSystem(ComponentStore store) { this.store = store; }

        public void RunBenchmark(int scenario)
        {
            if (scenario == 3)
            {
                RunMicroBenchmark(10000, 500);
                return;
            }

            if (scenario == 4)
            {
                RunRealSystemChainBenchmark(10000, 500);
                return;
            }

            if (scenario == 5)
            {
                RunFullGameBenchmark();
                return;
            }

            Console.WriteLine($"\n[BENCHMARK] Full 12-System Benchmark: {scenario} entities");

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
            for (int i = 0; i < scenario; i++)
            {
                float x = random.Next(0, 10);
                float y = (float)random.Next(10, 19);
                int id = store.AddEnemy(x, y, 1f, BENCH_ENEMY_HEALTH, BENCH_ENEMY_HEALTH, 10f, 10, 1);
                store.SetEnemyAIAction(id, "");
                store.SetEntityName(id, $"NormalL1W1E{i}");
                store.EnemyBehaviorTree[id] = gameConfig.GetCachedBehaviorTree("Normal");
            }
            Console.WriteLine($"[BENCHMARK] Spawned {scenario} enemies");

            // 13 active game systems (EnemyAbilitySystem included)
            var waveSpawning   = new WaveSpawningSystem(store, logger, gameConfig);
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
            var upgrade      = new UpgradeSystem(store, logger, playerId, gameConfig);
            var skill        = new SkillSystem(store, logger, playerId, gameConfig);
            var buffSystem   = new BuffSystem(store, playerId);
            skill.InjectDotSystem(buffSystem);
            var comboSystem  = new ComboSystem(store, gameConfig.Combo);
            var map          = new MapSystem(logger, store);
            map.SetMapSize(10, 20);
            var pathfinding  = new PathfindingSystem(store);
            enemyMovement.SetPathfindingSystem(pathfinding);

            // Place towers — use AddTower so ActiveTowerIds is populated (matching real game flow)
            int t1 = store.CreateEntity();
            store.AddTower(t1, TowerType.Basic, 15f, 3, 1f, 1, 50f);
            store.PositionX[t1] = 3f; store.PositionY[t1] = 15f;

            int t2 = store.CreateEntity();
            store.AddTower(t2, TowerType.Sniper, 25f, 5, 1f, 1, 100f);
            store.PositionX[t2] = 7f; store.PositionY[t2] = 15f;

            int frames = 500;

            // Warm-up (BeginFrame is optional since Resolve clears _deathQueue)
            for (int f = 0; f < 5; f++)
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

            long tWaveSpawn = 0, tEnemyAI = 0, tMoveAttack = 0;
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
                var sw = new Stopwatch();

                sw.Start(); waveSpawning.Update(); tWaveSpawn += sw.ElapsedTicks;
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
            }

            totalSw.Stop();
            ConsoleLogger.EnableLog = true;

            double ticksPerMs = Stopwatch.Frequency / 1000.0;
            double msTotal = totalSw.Elapsed.TotalMilliseconds;
            double fps = 1000.0 / (msTotal / frames);

            Console.WriteLine($"\n[BENCHMARK] Per-system timing ({frames} frames, {scenario} enemies):");
            Console.WriteLine($"[BENCHMARK]   WaveSpawning:   {tWaveSpawn/ticksPerMs,7:F2} ms  ({tWaveSpawn/ticksPerMs/msTotal*100,5:F1}%)");
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
            Console.WriteLine($"\n[BENCHMARK] Throughput: {fps:F0} FPS  ({msTotal/frames:F2} ms/frame)");
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
            for (int i = 0; i < enemyCount; i++)
            {
                int id = store.AddEnemy(random.Next(0, 10), random.Next(10, 19), 1f, BENCH_ENEMY_HEALTH, BENCH_ENEMY_HEALTH, 10f, 10, 1);
                store.SetEntityName(id, $"NormalL1W1E{i}");
                store.EnemyBehaviorTree[id] = gameConfig.GetCachedBehaviorTree("Normal");
            }

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

        // ── Mode 4: Real system-chain benchmark ──────────────────────────────────
        private void RunRealSystemChainBenchmark(int scenario, int frames)
        {
            Console.WriteLine($"\n[BENCHMARK] Real System Chain: {scenario} enemies x {frames} frames");
            Console.WriteLine("[BENCHMARK] Using actual system.Update() calls, not hand-merged loops.");

            var logger = new ConsoleLogger();
            var gameConfig = new GameConfig();
            GameConfigLoader.LoadConfig(logger);

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

            var random = new Random(42);
            for (int i = 0; i < scenario; i++)
            {
                float x = random.Next(0, 10);
                float y = (float)random.Next(10, 19);
                int id = store.AddEnemy(x, y, 1f, BENCH_ENEMY_HEALTH, BENCH_ENEMY_HEALTH, 10f, 10, 1);
                store.SetEnemyAIAction(id, "");
                store.SetEntityName(id, $"NormalL1W1E{i}");
                store.EnemyBehaviorTree[id] = gameConfig.GetCachedBehaviorTree("Normal");
            }

            int t1 = store.CreateEntity();
            store.AddTower(t1, TowerType.Basic, 15f, 3, 1f, 1, 50f);
            store.PositionX[t1] = 3f; store.PositionY[t1] = 15f;

            int t2 = store.CreateEntity();
            store.AddTower(t2, TowerType.Sniper, 25f, 5, 1f, 1, 100f);
            store.PositionX[t2] = 7f; store.PositionY[t2] = 15f;

            var enemyAbility = new EnemyAbilitySystem(store, logger, playerId, gameConfig);
            var benchTechTree = new TechTreeSystem(store, logger, playerId, null, gameConfig);
            var enemyAI       = new EnemyAISystem(store, logger, playerId, gameConfig, enemyAbility, benchTechTree);
            var enemyMovement = new EnemyMovementSystem(store, playerId);
            var playerAttack  = new PlayerTowerAttackSystem(store, logger, playerId, gameConfig);
            var towerAttack   = new TowerAttackSystem(store, logger, null);
            var auraTower     = new AuraTowerSystem(store);
            // Round 173 Direction 1 — Shrine Tower. No-op when no Shrine is on the field.
            var towerShrine   = new TowerShrineSystem(store);
            var projectile    = new ProjectileSystem(store, logger);
            var gold          = new GoldSystem(store, logger);
            var upgrade       = new UpgradeSystem(store, logger, playerId, gameConfig);
            var skill         = new SkillSystem(store, logger, playerId, gameConfig);
            var buffSystem    = new BuffSystem(store, playerId);
            var comboSystem   = new ComboSystem(store, gameConfig.Combo);
            skill.InjectDotSystem(buffSystem);
            towerAttack.SetBuffSystem(buffSystem);
            var map           = new MapSystem(logger, store);
            map.SetMapSize(10, 20);
            var pathfinding   = new PathfindingSystem(store);
            enemyMovement.SetPathfindingSystem(pathfinding);
            // Round 182 Direction 6 — No FrameScheduler in this benchmark path (mode 4
            // stress-test calls systems directly); TickBlinkerCycle is not invoked here,
            // so the optional pathfinding injection is deferred to the mode 5 path
            // below where the FrameScheduler is actually constructed.
            var waveSpawning  = new WaveSpawningSystem(store, logger, gameConfig);

            long tWaveSpawn = 0, tEnemyAI = 0, tMoveAttack = 0;
            long tPlayerAttack = 0, tTowerAttack = 0, tGold = 0, tUpgrade = 0, tSkill = 0;
            long tGridRebuildMode4 = 0;

            ConsoleLogger.EnableLog = false;
            var totalSw = Stopwatch.StartNew();

            for (int f = 0; f < frames; f++)
            {
                int turn = f + 6;
                store.BeginFrame();
                var sw = new Stopwatch();

                sw.Start(); store.RebuildSpatialGrid(); tGridRebuildMode4 += sw.ElapsedTicks;
                // mode 4 is a fixed 10K-entity chain benchmark; wave spawning is
                // intentionally disabled so the measured population remains stable.
                sw.Restart(); tWaveSpawn += sw.ElapsedTicks;
                sw.Restart(); enemyAI.SetTurn(turn); enemyAI.Update(); tEnemyAI += sw.ElapsedTicks;
                sw.Restart(); enemyMovement.SetTurn(turn); enemyMovement.Update(); tMoveAttack += sw.ElapsedTicks;
                sw.Restart(); playerAttack.SetTurn(turn); playerAttack.Update(); tPlayerAttack += sw.ElapsedTicks;
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
                store.ResolveEnemiesKilledThisFrame();
                long tSkillAndBuff = sw.ElapsedTicks;
                sw.Restart(); tSkill += tSkillAndBuff;
            }

            totalSw.Stop();
            ConsoleLogger.EnableLog = true;

            double ticksPerMs = Stopwatch.Frequency / 1000.0;
            double msTotal = totalSw.Elapsed.TotalMilliseconds;
            double fps = 1000.0 / (msTotal / frames);

            Console.WriteLine($"\n[BENCHMARK] Real-system-chain timing ({frames} frames, {scenario} enemies):");
            Console.WriteLine($"[BENCHMARK]   GridRebuild:    {tGridRebuildMode4/ticksPerMs,7:F2} ms");
            Console.WriteLine($"[BENCHMARK]   WaveSpawning:   {tWaveSpawn/ticksPerMs,7:F2} ms");
Console.WriteLine($"[BENCHMARK]   EnemyAI:        {tEnemyAI/ticksPerMs,7:F2} ms");
            Console.WriteLine($"[BENCHMARK]   EnemyMovement: {tMoveAttack/ticksPerMs,7:F2} ms");
            Console.WriteLine($"[BENCHMARK]   PlayerAttack:  {tPlayerAttack/ticksPerMs,7:F2} ms");
            Console.WriteLine($"[BENCHMARK]   TowerAttack:    {tTowerAttack/ticksPerMs,7:F2} ms");
            Console.WriteLine($"[BENCHMARK]   Gold:           {tGold/ticksPerMs,7:F2} ms");
            Console.WriteLine($"[BENCHMARK]   Upgrade:        {tUpgrade/ticksPerMs,7:F2} ms");
            Console.WriteLine($"[BENCHMARK]   Skill:          {tSkill/ticksPerMs,7:F2} ms");
            Console.WriteLine($"[BENCHMARK]   ----------------------------------------");
            Console.WriteLine($"[BENCHMARK]   TOTAL:          {msTotal,7:F2} ms");
            Console.WriteLine($"\n[BENCHMARK] Throughput: {fps:F0} FPS  ({msTotal/frames:F2} ms/frame)");
        }

        /// <summary>
        /// 完整一局压测 — 5 关、真实波次生成、完整战斗流程。
        /// 测量从第一帧到最后一帧的总帧数和墙钟时间，计算真实游戏吞吐量。
        /// </summary>
        private void RunFullGameBenchmark()
        {
            Console.WriteLine("\n[BENCHMARK] Full Game: 5 levels, real wave spawning, full combat pipeline");

            var logger = new ConsoleLogger();
            var gameConfig = GameConfigLoader.LoadConfig(logger);

            int playerId = 1;
            store.AddPlayer(playerId, 10f, 1f, 50f, 1, 20);
            store.PlayerMaxHealth[playerId] = 200f;
            store.PlayerCurrentHealth[playerId] = 200f;
            store.SetPlayerGold(playerId, 9999f);
            store.PositionX[playerId] = 5f;
            store.PositionY[playerId] = 0f;
            store.PositionActive[playerId] = true;

            // 创建所有系统（对齐 GameManager.Initialize）
            var scheduler = new FrameScheduler(store, gameConfig);

            var waveSpawning = new WaveSpawningSystem(store, logger, gameConfig);
            var enemyAbility  = new EnemyAbilitySystem(store, logger, playerId, gameConfig);
            var benchTechTree = new TechTreeSystem(store, logger, playerId, null, gameConfig);
            var enemyAI       = new EnemyAISystem(store, logger, playerId, gameConfig, enemyAbility, benchTechTree);
            var enemyMovement = new EnemyMovementSystem(store, playerId);
            var playerAttack  = new PlayerTowerAttackSystem(store, logger, playerId, gameConfig);
            var towerAttack   = new TowerAttackSystem(store, logger, null);
            var auraTower     = new AuraTowerSystem(store);
            // Round 173 Direction 1 — Shrine Tower. No-op when no Shrine is on the field.
            var towerShrine   = new TowerShrineSystem(store);
            var projectile    = new ProjectileSystem(store, logger);
            var gold          = new GoldSystem(store, logger);
            var upgrade       = new UpgradeSystem(store, logger, playerId, gameConfig);
            var skill         = new SkillSystem(store, logger, playerId, gameConfig);
            var buffSystem    = new BuffSystem(store, playerId);
            var comboSystem   = new ComboSystem(store, gameConfig.Combo);
            skill.InjectDotSystem(buffSystem);
            towerAttack.SetBuffSystem(buffSystem);
            scheduler.Combat.TowerShrine = towerShrine;
            var pathfinding   = new PathfindingSystem(store);
            enemyMovement.SetPathfindingSystem(pathfinding);
            // Round 182 Direction 6 — Inject PathfindingSystem into FrameScheduler so
            // TickBlinkerCycle can validate path waypoint counts before advancing node
            // indices on a blink. Optional dependency; TickBlinkerCycle gracefully
            // degrades to a no-advance when pathfinding is null.
            scheduler.SetPathfindingSystem(pathfinding);

            // 布线 FrameScheduler — 按 Phase 分组注入
            scheduler.Spawning.WaveSpawning   = waveSpawning;
            scheduler.AI.EnemyAI        = enemyAI;
            scheduler.AI.EnemyAbility   = enemyAbility;
            scheduler.AI.Lifesteal     = null; // EnemyLifestealSystem (placeholder, lifesteal inline in EnemyAISystem)
            scheduler.Movement.EnemyMovement  = enemyMovement;
            scheduler.Movement.Pathfinding    = pathfinding;
            scheduler.Combat.PlayerTowerAttack = playerAttack;
            scheduler.CombatSetup.PlayerTowerAttack = playerAttack;
            scheduler.Combat.TowerAttack    = towerAttack;
            scheduler.CombatSetup.TowerAttack    = towerAttack;
            scheduler.Combat.AuraTower      = auraTower;
            scheduler.CombatSetup.AuraTower      = auraTower;
            scheduler.Combat.Projectile     = projectile;
            scheduler.Build.Gold           = gold;
            scheduler.Build.Upgrade        = upgrade;
            scheduler.Build.Skill          = skill;
            scheduler.CombatSetup.Skill    = skill;
            scheduler.SkillBuff.Skill      = skill;
            scheduler.SkillBuff.Buff       = buffSystem;
            scheduler.PostDeath.Combo      = comboSystem;
            scheduler.Phase          = GameState.BuildPhase;

            // 放塔（对齐交互式游戏）
            int t1 = store.CreateEntity();
            store.PositionX[t1] = 2f; store.PositionY[t1] = 5f;
            store.AddTower(t1, TowerType.Basic, 15f, 3, 1.5f, 1, 100f);

            int t2 = store.CreateEntity();
            store.PositionX[t2] = 7f; store.PositionY[t2] = 12f;
            store.AddTower(t2, TowerType.Sniper, 25f, 5, 0.8f, 1, 200f);

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

                waveSpawning.SetLevel(level);
                store.RebuildSpatialGrid();

                // ── BuildPhase ──────────────────────────────────
                scheduler.Phase = GameState.BuildPhase;
                for (int bf = 0; bf < 10; bf++)
                {
                    totalFrames++;
                    scheduler.TickGameTurn(1f, totalFrames);
                }

                // ── WavePhase ───────────────────────────────────
                scheduler.Phase = GameState.WavePhase;
                bool levelDone = false;
                int levelMaxFrames = 10000;  // 安全上限，防止死循环
                int levelFrameStart = totalFrames;
                while (!levelDone && (totalFrames - levelFrameStart) < levelMaxFrames)
                {
                    totalFrames++;
                    scheduler.TickGameTurn(1f, totalFrames);

                    // 游戏结束检测：玩家死亡
                    if (store.PlayerCurrentHealth[playerId] <= 0f)
                    {
                        endReason = "PlayerDeath";
                        levelDone = true;
                        break;
                    }

                    // 敌人到达底部检测
                    var activeEnemyIds = store.GetCachedActiveEnemyIds();
                    bool leaked = false;
                    foreach (var eid in activeEnemyIds)
                    {
                        if (store.EnemyActive[eid] && store.PositionY[eid] <= 0f)
                        {
                            store.DecrementPlayerBaseLives(playerId);
                            store.EnemiesLeakedThisWave[playerId]++; // track leak for adaptive difficulty
                            store.QueueEnemyDeath(eid, playerId);
                            if (store.GetPlayerBaseLives(playerId) <= 0)
                            {
                                endReason = "BaseDestroyed";
                                leaked = true;
                                break;
                            }
                        }
                    }
                    if (leaked) { levelDone = true; break; }

                    // 关卡完成检测：WaveSpawningSystem 内部会将 currentLevel++ 当所有波次完成
                    int spawnedLevel = waveSpawning.GetCurrentLevel();
                    bool allWavesDone = spawnedLevel > level;
                    int activeCount = store.GetCachedActiveEnemyIds().Count;
                    if (allWavesDone && activeCount == 0)
                    {
                        completedLevels++;
                        levelDone = true;
                    }
                }

                // 游戏结束则停止后续关卡
                if (endReason != "Victory") break;

                // 安全上限触发
                if ((totalFrames - levelFrameStart) >= levelMaxFrames)
                {
                    endReason = $"Level{level}Timeout";
                    Console.WriteLine($"[BENCHMARK] WARNING: Level {level} hit {levelMaxFrames} frame limit!");
                    break;
                }
            }

            totalSw.Stop();
            ConsoleLogger.EnableLog = true;

            double msTotal = totalSw.Elapsed.TotalMilliseconds;
            double fps = 1000.0 * totalFrames / msTotal;

            Console.WriteLine($"\n[BENCHMARK] Full Game Results:");
            Console.WriteLine($"[BENCHMARK]   End reason:  {endReason}");
            Console.WriteLine($"[BENCHMARK]   Levels:      {completedLevels}/{maxLevels}");
            Console.WriteLine($"[BENCHMARK]   Total frames: {totalFrames}");
            Console.WriteLine($"[BENCHMARK]   Wall-clock:  {msTotal:F2} ms ({msTotal/1000:F2} s)");
            Console.WriteLine($"[BENCHMARK]   Avg frame:   {msTotal/totalFrames:F3} ms/frame");
            Console.WriteLine($"[BENCHMARK]   Throughput:  {fps:F0} FPS");
        }
    }
}
