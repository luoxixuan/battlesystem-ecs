using System;
using System.Diagnostics;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
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
            Console.WriteLine($"\n[BENCHMARK] 开始基准测试: {enemyCount} 个实体");
            
            // 准备数据
            for (int i = 0; i < enemyCount; i++)
            {
                store.AddEnemy(0f, 0f, 1f, 100f, 100f, 10f, 10, 1);
            }

            // 测试逻辑
            stopwatch.Restart();
            
            // 模拟 100 次更新循环
            for (int i = 0; i < 100; i++)
            {
                // 模拟简单的位置更新
                // 确保不越界：循环上限应为数组长度或实际激活实体数
                int limit = Math.Min(store.NextEntityId, 20000); 
                for (int e = 0; e < limit; e++)
                {
                    if (e < store.EnemyActive.Length && store.EnemyActive[e])
                    {
                        store.PositionX[e] += 0.1f;
                    }
                }
            }
            
            stopwatch.Stop();
            
            double msPer100Loops = stopwatch.Elapsed.TotalMilliseconds;
            Console.WriteLine($"[BENCHMARK] 完成！");
            Console.WriteLine($"[BENCHMARK] 总耗时 (100次循环): {msPer100Loops:F2} ms");
            Console.WriteLine($"[BENCHMARK] 平均每循环耗时: {(msPer100Loops / 100):F4} ms");
            Console.WriteLine($"[BENCHMARK] 预估单帧处理能力: {(1000 / (msPer100Loops / 100)):F0} FPS (仅模拟逻辑)");
        }
    }
}
