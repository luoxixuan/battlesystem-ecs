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
        private ComponentStore store;

        public BenchmarkSystem(ComponentStore store) { this.store = store; }

        public void RunBenchmark(int scenario)
        {
            if (scenario == 3)
            {
                RunMicroBenchmark(10000, 200);
                return;
            }

            Console.WriteLine($"\n[BENCHMARK] Full 12-System Benchmark: {scenario} entities");

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
            for (int i = 0; i < scenario; i++)
            {
                float x = random.Next(0, 10);
                float y = (float)random.Next(10, 19);
                int id = store.AddEnemy(x, y, 1f, 100f, 100f, 10f, 10, 1);
                store.SetEnemyAIAction(id, "");
                store.SetEntityName(id, $"NormalL1W1E{i}");
                store.EnemyBehaviorTree[id] = gameConfig.GetCachedBehaviorTree("Normal");
            }
            Console.WriteLine($"[BENCHMARK] Spawned {scenario} enemies");

            // 11 active game systems (no BuffSystem/BreachSystem in this project)
            var waveSpawning   = new WaveSpawningSystem(store, logger, gameConfig);
            var enemyAI       = new EnemyAISystem(store, logger, playerId, gameConfig);
            var enemyMovement = new EnemyMovementSystem(store, playerId);
            var playerAttack  = new PlayerTowerAttackSystem(store, logger, playerId, gameConfig);
            var towerAttack  = new TowerAttackSystem(store, logger);
            var gold         = new GoldSystem(store, logger);
            var upgrade      = new UpgradeSystem(store, logger, playerId);
            var skill        = new SkillSystem(store, logger, playerId, gameConfig);
            var map          = new MapSystem(logger, store);
            map.SetMapSize(10, 20);

            // Place towers
            int t1 = store.CreateEntity();
            store.TowerType[t1] = "弓箭塔"; store.TowerActive[t1] = true;
            store.PositionX[t1] = 3f; store.PositionY[t1] = 15f;
            store.TowerAttackDamage[t1] = 15f; store.TowerRange[t1] = 3; store.TowerLevel[t1] = 1;

            int t2 = store.CreateEntity();
            store.TowerType[t2] = "魔法塔"; store.TowerActive[t2] = true;
            store.PositionX[t2] = 7f; store.PositionY[t2] = 15f;
            store.TowerAttackDamage[t2] = 25f; store.TowerRange[t2] = 5; store.TowerLevel[t2] = 1;

            int frames = 200;

            // Warm-up
            for (int f = 0; f < 5; f++)
            {
                int turn = f + 6;
                enemyAI.SetTurn(turn);
                enemyAI.Update();
                enemyMovement.SetTurn(turn);
                enemyMovement.Update();
                playerAttack.SetTurn(turn);
                playerAttack.Update();
                towerAttack.SetTurn(turn);
                towerAttack.Update(1f);
                gold.SetTurn(turn);
                gold.Update();
            }

            ConsoleLogger.EnableLog = false;

            long tWaveSpawn = 0, tEnemyAI = 0, tMoveAttack = 0;
            long tTowerAttack = 0, tGold = 0;
            long tUpgrade = 0, tSkill = 0, tMap = 0;

            var totalSw = Stopwatch.StartNew();

            // Pre-compute move direction lookup to eliminate switch in hot path
            var moveDir = new sbyte[] { -1, 0, 0, 0, 0, 1, -1 };
            // index: (int)EnemyActionType → direction (-1=forward, 0=stand, 1=retreat)

            for (int f = 0; f < frames; f++)
            {
                int turn = f + 6;
                var sw = new Stopwatch();

                sw.Start(); waveSpawning.Update(); tWaveSpawn += sw.ElapsedTicks;
                sw.Restart(); enemyAI.SetTurn(turn); enemyAI.Update(); tEnemyAI += sw.ElapsedTicks;
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
                var buffs = store.PlayerBuffs[playerId];
                float fad = ad;
                if (buffs.Count > 0)
                {
                    foreach (string buff in buffs)
                    {
                        if (buff == "Attack+10%") fad *= 1.1f;
                    }
                }

                long goldAcc = 0;
                const int batchSize = 512;
                int numBatches = (count + batchSize - 1) / batchSize;

                Parallel.For(0, numBatches, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, batchIdx =>
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
                            store.PositionY[enemyId] = y + moveDir[(int)ae] * moveSpeed;
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
                            store.EnemyActive[enemyId] = false;
                            Interlocked.Add(ref goldAcc, store.EnemyGoldReward[enemyId]);
                        }
                    }
                });

                if (goldAcc > 0) store.PlayerGold[playerId] += (int)goldAcc;
                tMoveAttack += sw.ElapsedTicks;

                sw.Restart(); towerAttack.SetTurn(turn); towerAttack.Update(1f); tTowerAttack += sw.ElapsedTicks;
                sw.Restart(); gold.SetTurn(turn); gold.Update(); tGold += sw.ElapsedTicks;
                sw.Restart(); upgrade.Update(); tUpgrade += sw.ElapsedTicks;
                sw.Restart(); skill.Update(1f); tSkill += sw.ElapsedTicks;
                /* map.Update() = skip */
            }

            totalSw.Stop();
            ConsoleLogger.EnableLog = true;

            double ticksPerMs = Stopwatch.Frequency / 1000.0;
            double msTotal = totalSw.Elapsed.TotalMilliseconds;
            double fps = 1000.0 / (msTotal / frames);

            Console.WriteLine($"\n[BENCHMARK] Per-system timing ({frames} frames, {scenario} enemies):");
            Console.WriteLine($"[BENCHMARK]   WaveSpawning:   {tWaveSpawn/ticksPerMs,7:F2} ms  ({(tWaveSpawn/msTotal*100),5:F1}%)");
            Console.WriteLine($"[BENCHMARK]   EnemyAI:        {tEnemyAI/ticksPerMs,7:F2} ms  ({(tEnemyAI/msTotal*100),5:F1}%)");
            Console.WriteLine($"[BENCHMARK]   MoveAttack:     {tMoveAttack/ticksPerMs,7:F2} ms  ({(tMoveAttack/msTotal*100),5:F1}%)");
            Console.WriteLine($"[BENCHMARK]   TowerAttack:    {tTowerAttack/ticksPerMs,7:F2} ms  ({(tTowerAttack/msTotal*100),5:F1}%)");
            Console.WriteLine($"[BENCHMARK]   Gold:           {tGold/ticksPerMs,7:F2} ms  ({(tGold/msTotal*100),5:F1}%)");
            Console.WriteLine($"[BENCHMARK]   Upgrade:        {tUpgrade/ticksPerMs,7:F2} ms  ({(tUpgrade/msTotal*100),5:F1}%)");
            Console.WriteLine($"[BENCHMARK]   Skill:          {tSkill/ticksPerMs,7:F2} ms  ({(tSkill/msTotal*100),5:F1}%)");
            Console.WriteLine($"[BENCHMARK]   Map:            {tMap/ticksPerMs,7:F2} ms  ({(tMap/msTotal*100),5:F1}%)");
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
                int id = store.AddEnemy(random.Next(0, 10), random.Next(10, 19), 1f, 100f, 100f, 10f, 10, 1);
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
    }
}