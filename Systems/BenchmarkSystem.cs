using System;
using System.Diagnostics;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Full 12-system benchmark with per-system timing breakdown.
    /// </summary>
    public class BenchmarkSystem
    {
        private ComponentStore store;
        private Stopwatch stopwatch;
        private Stopwatch sw;

        public BenchmarkSystem(ComponentStore store)
        {
            this.store = store;
            this.stopwatch = new Stopwatch();
            this.sw = new Stopwatch();
        }

        public void RunBenchmark(int enemyCount)
        {
            Console.WriteLine($"\n[BENCHMARK] Full 12-System Benchmark: {enemyCount} entities");

            // --- Setup ---
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
                float x = random.Next(0, 10);
                float y = (float)random.Next(10, 19);
                int id = store.AddEnemy(x, y, 1f, 100f, 100f, 10f, 10, 1);
                store.SetEnemyAIAction(id, "");
                store.SetEntityName(id, $"NormalL1W1E{i}");
                // Pre-cache BT so EnemyAISystem reads from SOA array (O(1)) instead of calling GetCachedBehaviorTree
                store.EnemyBehaviorTree[id] = gameConfig.GetCachedBehaviorTree("Normal");
            }
            Console.WriteLine($"[BENCHMARK] Spawned {enemyCount} enemies");

            // --- Create all active systems ---
            var waveSpawning  = new WaveSpawningSystem(store, logger, gameConfig);
            var enemyAI       = new EnemyAISystem(store, logger, playerId, gameConfig);
            var enemyMovement = new EnemyMovementSystem(store, playerId);
            var playerAttack  = new PlayerTowerAttackSystem(store, logger, playerId, gameConfig);
            var towerAttack   = new TowerAttackSystem(store, logger);
            var upgrade       = new UpgradeSystem(store, logger, playerId);
            var skill         = new SkillSystem(store, logger, playerId, gameConfig);

            // Place towers
            int t1 = store.CreateEntity();
            store.TowerType[t1] = "弓箭塔";
            store.TowerActive[t1] = true;
            store.PositionX[t1] = 3f;
            store.PositionY[t1] = 15f;
            store.TowerAttackDamage[t1] = 15f;
            store.TowerRange[t1] = 3;
            store.TowerLevel[t1] = 1;

            int t2 = store.CreateEntity();
            store.TowerType[t2] = "魔法塔";
            store.TowerActive[t2] = true;
            store.PositionX[t2] = 7f;
            store.PositionY[t2] = 15f;
            store.TowerAttackDamage[t2] = 25f;
            store.TowerRange[t2] = 5;
            store.TowerLevel[t2] = 1;

            int frames = 200;

            // --- Warm-up: 5 frames ---
            for (int f = 0; f < 5; f++)
            {
                enemyAI.SetTurn(f + 1);
                enemyAI.Update();
                enemyMovement.Update();
                playerAttack.Update();
            }

            ConsoleLogger.EnableLog = false;

            long tWaveSpawn = 0, tEnemyAI = 0, tMovement = 0;
            long tPlayerAttack = 0, tTowerAttack = 0, tUpgrade = 0, tSkill = 0;

            stopwatch.Restart();

            for (int f = 0; f < frames; f++)
            {
                int turn = f + 6;

                sw.Restart(); waveSpawning.Update(); tWaveSpawn += sw.ElapsedMilliseconds;
                sw.Restart(); enemyAI.SetTurn(turn); enemyAI.Update(); tEnemyAI += sw.ElapsedMilliseconds;
                sw.Restart(); enemyMovement.Update(); tMovement += sw.ElapsedMilliseconds;
                sw.Restart(); playerAttack.Update(); tPlayerAttack += sw.ElapsedMilliseconds;
                sw.Restart(); towerAttack.Update(1f); tTowerAttack += sw.ElapsedMilliseconds;
                sw.Restart(); upgrade.Update(); tUpgrade += sw.ElapsedMilliseconds;
                sw.Restart(); skill.Update(1f); tSkill += sw.ElapsedMilliseconds;
            }

            stopwatch.Stop();
            ConsoleLogger.EnableLog = true;

            double msTotal = stopwatch.Elapsed.TotalMilliseconds;
            double fps = 1000.0 / (msTotal / frames);

            Console.WriteLine($"\n[BENCHMARK] Per-system timing ({frames} frames, {enemyCount} enemies):");
            Console.WriteLine($"[BENCHMARK]   WaveSpawning:   {tWaveSpawn,7:F2} ms  ({(tWaveSpawn/msTotal*100),5:F1}%)");
            Console.WriteLine($"[BENCHMARK]   EnemyAI:        {tEnemyAI,7:F2} ms  ({(tEnemyAI/msTotal*100),5:F1}%)");
            Console.WriteLine($"[BENCHMARK]   Movement:       {tMovement,7:F2} ms  ({(tMovement/msTotal*100),5:F1}%)");
            Console.WriteLine($"[BENCHMARK]   PlayerAttack:   {tPlayerAttack,7:F2} ms  ({(tPlayerAttack/msTotal*100),5:F1}%)");
            Console.WriteLine($"[BENCHMARK]   TowerAttack:    {tTowerAttack,7:F2} ms  ({(tTowerAttack/msTotal*100),5:F1}%)");
            Console.WriteLine($"[BENCHMARK]   Upgrade:        {tUpgrade,7:F2} ms  ({(tUpgrade/msTotal*100),5:F1}%)");
            Console.WriteLine($"[BENCHMARK]   Skill:          {tSkill,7:F2} ms  ({(tSkill/msTotal*100),5:F1}%)");
            Console.WriteLine($"[BENCHMARK]   ----------------------------------------");
            Console.WriteLine($"[BENCHMARK]   TOTAL:          {msTotal,7:F2} ms");
            Console.WriteLine($"\n[BENCHMARK] Throughput: {fps:F0} FPS  ({msTotal/frames:F2} ms/frame)");
        }
    }
}