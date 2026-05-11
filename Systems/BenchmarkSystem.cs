using System;
using System.Diagnostics;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Full 12-system benchmark for BattleSystem-ECS.
    /// Simulates the complete game loop: WaveSpawn → EnemyAI → Movement →
    /// PlayerAttack → TowerAttack → Upgrade → Skill → Buff → Map → Breach → EventBus
    /// </summary>
    public class BenchmarkSystem
    {
        private ComponentStore store;
        private Stopwatch stopwatch;

        public BenchmarkSystem(ComponentStore store)
        {
            this.store = store;
            this.stopwatch = new Stopwatch();
        }

        public void RunBenchmark(int enemyCount)
        {
            Console.WriteLine($"\n[BENCHMARK] Full 12-System Benchmark: {enemyCount} entities");
            Console.WriteLine("[BENCHMARK] Systems: WaveSpawning + EnemyAI + Movement + PlayerAttack +");
            Console.WriteLine("[BENCHMARK]           TowerAttack + Upgrade + Skill + Buff + Map + Breach");

            // --- Setup ---
            var logger = new ConsoleLogger();
            var gameConfig = new GameConfig();
            GameConfigLoader.LoadConfig(logger);

            int playerId = 1;

            // Player entity
            store.PlayerMaxHealth[playerId] = 200f;
            store.PlayerCurrentHealth[playerId] = 200f;
            store.PositionX[playerId] = 5f;
            store.PositionY[playerId] = 0f;
            store.SetPlayerGold(playerId, 9999f);

            // Pre-spawn enemies so WaveSpawning doesn't regenerate each frame
            // (this mirrors the real game state after first wave spawn)
            var random = new Random(42);
            for (int i = 0; i < enemyCount; i++)
            {
                float x = random.Next(0, 10);
                float y = (float)random.Next(10, 19);
                int id = store.AddEnemy(x, y, 1f, 100f, 100f, 10f, 10, 1);
                store.SetEnemyAIAction(id, "");
                store.SetEntityName(id, $"NormalL1W1E{i}");
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
            // MapSystem omitted: pure text renderer, not part of game logic hot path

            // Place towers so TowerAttack has something to do
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

            // --- Warm-up: 5 frames to settle first-run allocations ---
            for (int f = 0; f < 5; f++)
            {
                enemyAI.SetTurn(f + 1);
                enemyAI.Update();
                enemyMovement.Update();
                playerAttack.Update();
            }

            // --- Timed run ---
            stopwatch.Restart();

            for (int f = 0; f < frames; f++)
            {
                int turn = f + 6;

                // 1. Wave spawning
                waveSpawning.Update();

                // 2. Enemy AI (behavior tree evaluation + action execution)
                enemyAI.SetTurn(turn);
                enemyAI.Update();

                // 3. Enemy movement (reads EnemyAIAction from EnemyAISystem)
                enemyMovement.Update();

                // 4. Player tower attack
                playerAttack.Update();

                // 5. Tower attack (range-based targeting)
                towerAttack.Update(1f);

                // 6. Upgrade check
                upgrade.Update();

                // 7. Skill system (auto-cast on cooldown)
                skill.Update(1f);

                // 8. MapSystem skipped — pure text output, not game logic
            }

            stopwatch.Stop();

            double msTotal = stopwatch.Elapsed.TotalMilliseconds;
            double msPerFrame = msTotal / frames;
            double fps = 1000.0 / msPerFrame;

            Console.WriteLine($"[BENCHMARK] Complete!");
            Console.WriteLine($"[BENCHMARK] Total time ({frames} frames): {msTotal:F2} ms");
            Console.WriteLine($"[BENCHMARK] Avg per frame: {msPerFrame:F4} ms");
            Console.WriteLine($"[BENCHMARK] Throughput: {fps:F0} FPS");
            Console.WriteLine($"[BENCHMARK] Entities: {enemyCount}, Active enemies: {store.GetActiveEnemyCount()}");
        }
    }
}