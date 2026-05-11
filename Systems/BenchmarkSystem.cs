using System;
using System.Diagnostics;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Micro-benchmark: isolates each cost inside EnemyAI.Update() loop.
    /// Run via: echo 3 | dotnet run
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

            var waveSpawning  = new WaveSpawningSystem(store, logger, gameConfig);
            var enemyAI       = new EnemyAISystem(store, logger, playerId, gameConfig);
            var enemyMovement = new EnemyMovementSystem(store, playerId);
            var playerAttack  = new PlayerTowerAttackSystem(store, logger, playerId, gameConfig);
            var towerAttack   = new TowerAttackSystem(store, logger);
            var upgrade       = new UpgradeSystem(store, logger, playerId);
            var skill         = new SkillSystem(store, logger, playerId, gameConfig);

            int t1 = store.CreateEntity();
            store.TowerType[t1] = "弓箭塔"; store.TowerActive[t1] = true;
            store.PositionX[t1] = 3f; store.PositionY[t1] = 15f;
            store.TowerAttackDamage[t1] = 15f; store.TowerRange[t1] = 3; store.TowerLevel[t1] = 1;

            int t2 = store.CreateEntity();
            store.TowerType[t2] = "魔法塔"; store.TowerActive[t2] = true;
            store.PositionX[t2] = 7f; store.PositionY[t2] = 15f;
            store.TowerAttackDamage[t2] = 25f; store.TowerRange[t2] = 5; store.TowerLevel[t2] = 1;

            int frames = 200;

            for (int f = 0; f < 5; f++)
            {
                enemyAI.SetTurn(f + 6);
                enemyAI.Update();
                enemyMovement.Update();
                playerAttack.Update();
            }

            ConsoleLogger.EnableLog = false;

            long tWaveSpawn = 0, tEnemyAI = 0, tMovement = 0;
            long tPlayerAttack = 0, tTowerAttack = 0, tUpgrade = 0, tSkill = 0;

            var totalSw = Stopwatch.StartNew();

            for (int f = 0; f < frames; f++)
            {
                int turn = f + 6;
                var sw = new Stopwatch();
                sw.Start(); waveSpawning.Update(); tWaveSpawn += sw.ElapsedTicks;
                sw.Restart(); enemyAI.SetTurn(turn); enemyAI.Update(); tEnemyAI += sw.ElapsedTicks;
                sw.Restart(); enemyMovement.Update(); tMovement += sw.ElapsedTicks;
                sw.Restart(); playerAttack.Update(); tPlayerAttack += sw.ElapsedTicks;
                sw.Restart(); towerAttack.Update(1f); tTowerAttack += sw.ElapsedTicks;
                sw.Restart(); upgrade.Update(); tUpgrade += sw.ElapsedTicks;
                sw.Restart(); skill.Update(1f); tSkill += sw.ElapsedTicks;
            }

            totalSw.Stop();
            ConsoleLogger.EnableLog = true;

            double msTotal = totalSw.Elapsed.TotalMilliseconds;
            double fps = 1000.0 / (msTotal / frames);
            double ticksPerMs = Stopwatch.Frequency / 1000.0;

            Console.WriteLine($"\n[BENCHMARK] Per-system timing ({frames} frames, {scenario} enemies):");
            Console.WriteLine($"[BENCHMARK]   WaveSpawning:   {tWaveSpawn/ticksPerMs,7:F2} ms  ({(tWaveSpawn/(double)totalSw.ElapsedTicks*100),5:F1}%)");
            Console.WriteLine($"[BENCHMARK]   EnemyAI:        {tEnemyAI/ticksPerMs,7:F2} ms  ({(tEnemyAI/(double)totalSw.ElapsedTicks*100),5:F1}%)");
            Console.WriteLine($"[BENCHMARK]   Movement:       {tMovement/ticksPerMs,7:F2} ms  ({(tMovement/(double)totalSw.ElapsedTicks*100),5:F1}%)");
            Console.WriteLine($"[BENCHMARK]   PlayerAttack:   {tPlayerAttack/ticksPerMs,7:F2} ms  ({(tPlayerAttack/(double)totalSw.ElapsedTicks*100),5:F1}%)");
            Console.WriteLine($"[BENCHMARK]   TowerAttack:    {tTowerAttack/ticksPerMs,7:F2} ms  ({(tTowerAttack/(double)totalSw.ElapsedTicks*100),5:F1}%)");
            Console.WriteLine($"[BENCHMARK]   Upgrade:        {tUpgrade/ticksPerMs,7:F2} ms  ({(tUpgrade/(double)totalSw.ElapsedTicks*100),5:F1}%)");
            Console.WriteLine($"[BENCHMARK]   Skill:          {tSkill/ticksPerMs,7:F2} ms  ({(tSkill/(double)totalSw.ElapsedTicks*100),5:F1}%)");
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

            // Pre-warm the action cache
            foreach (var eid in activeEnemyIds)
            {
                var bt = store.EnemyBehaviorTree[eid];
                string action = BTCachedTreeEvaluator.Evaluate(bt, eid, store, playerId, 1);
                _ = EnemyAISystem.StringToActionEnum(action);
            }

            double ticksPerMs = Stopwatch.Frequency / 1000.0;

            long t1 = 0, t2 = 0, t3 = 0, t4 = 0, t5 = 0, t6 = 0, t7 = 0;

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
                    store.SetEnemyAIAction(enemyId, a);
                }
                t4 += Stopwatch.GetTimestamp() - start;

                start = Stopwatch.GetTimestamp();
                foreach (int enemyId in activeEnemyIds)
                {
                    var bt = store.EnemyBehaviorTree[enemyId];
                    string a = BTCachedTreeEvaluator.Evaluate(bt, enemyId, store, playerId, f + 1);
                    store.SetEnemyAIAction(enemyId, a);
                    var _e = EnemyAISystem.StringToActionEnum(a);
                }
                t5 += Stopwatch.GetTimestamp() - start;

                start = Stopwatch.GetTimestamp();
                foreach (int enemyId in activeEnemyIds)
                {
                    var bt = store.EnemyBehaviorTree[enemyId];
                    string a = BTCachedTreeEvaluator.Evaluate(bt, enemyId, store, playerId, f + 1);
                    store.SetEnemyAIAction(enemyId, a);
                    var e = EnemyAISystem.StringToActionEnum(a);
                    store.SetEnemyActionEnum(enemyId, e);
                }
                t6 += Stopwatch.GetTimestamp() - start;

                start = Stopwatch.GetTimestamp();
                foreach (int enemyId in activeEnemyIds)
                {
                    if (!store.EnemyActive[enemyId]) continue;
                    var bt = store.EnemyBehaviorTree[enemyId];
                    string a = BTCachedTreeEvaluator.Evaluate(bt, enemyId, store, playerId, f + 1);
                    store.SetEnemyAIAction(enemyId, a);
                    var e = EnemyAISystem.StringToActionEnum(a);
                    store.SetEnemyActionEnum(enemyId, e);
                }
                t7 += Stopwatch.GetTimestamp() - start;
            }

            Console.WriteLine($"[MICRO] Per-operation incremental cost ({totalIters:N0} iterations):");
            Console.WriteLine($"[MICRO]   1. Empty foreach:           {t1/ticksPerMs,7:F2} ms");
            Console.WriteLine($"[MICRO]   2. + EnemyBehaviorTree[] r: {t2/ticksPerMs - t1/ticksPerMs,7:F2} ms  (incremental)");
            Console.WriteLine($"[MICRO]   3. + BTCachedTreeEval:       {t3/ticksPerMs - t2/ticksPerMs,7:F2} ms  (incremental)");
            Console.WriteLine($"[MICRO]   4. + SetEnemyAIAction(str):  {t4/ticksPerMs - t3/ticksPerMs,7:F2} ms  (incremental)");
            Console.WriteLine($"[MICRO]   5. + StringToActionEnum:    {t5/ticksPerMs - t4/ticksPerMs,7:F2} ms  (incremental)");
            Console.WriteLine($"[MICRO]   6. + SetEnemyActionEnum:    {t6/ticksPerMs - t5/ticksPerMs,7:F2} ms  (incremental)");
            Console.WriteLine($"[MICRO]   7. + EnemyActive check:     {t7/ticksPerMs - t6/ticksPerMs,7:F2} ms  (incremental)");
            Console.WriteLine($"[MICRO]   ----------------------------------------");
            Console.WriteLine($"[MICRO]   Steps 1-7 sum:               {t7/ticksPerMs,7:F2} ms");
            Console.WriteLine($"[MICRO]   vs full EnemyAI.Update: ~204 ms");
            Console.WriteLine($"[MICRO]   Gap (loop overhead): ~{204-t7/ticksPerMs:F0} ms");
            Console.WriteLine($"[MICRO]   Loop overhead per iteration: ~{(204-t7/ticksPerMs)*1e6/totalIters:F0} ns");
        }
    }
}