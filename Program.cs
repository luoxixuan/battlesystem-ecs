using System;
using BattleSystemECS.Core;

namespace BattleSystemECS
{
    class Program
    {
        static void Main(string[] args)
        {
            // 非交互压测入口：
            //   dotnet run 2        → 手工合并循环性能下界
            //   dotnet run 4        → 生产 Registry FrameGraph 固定负载
            //   dotnet run 5        → 生产 Registry FrameGraph 完整局观察
            //   dotnet run          → 交互游戏
            if (args.Length > 0 && int.TryParse(args[0], out int mode))
            {
                if (mode == 2)
                {
                    RunBenchmarkDirect(10000);
                }
                else if (mode == 4)
                {
                    RunBenchmarkDirect(4);
                }
                else if (mode == 5)
                {
                    RunBenchmarkDirect(5);
                }
                else if (mode == 3)
                {
                    RunBenchmarkDirect(3);
                }
                else if (string.Equals(args[0], "daily", StringComparison.OrdinalIgnoreCase))
                {
                    // Round 105 Direction 9: Run the daily challenge — equivalent to
                    // an interactive run with the daily modifiers baked into GameConfig.
                    // The modifiers are loaded + resolved inside GameConfigLoader.LoadConfig.
                    // The CLI just shows the daily summary and starts the game.
                    GameManager dailyManager = new GameManager();
                    dailyManager.Initialize();
                    dailyManager.PrintDailySummary();
                    dailyManager.Run();
                }
                else
                {
                    RunBenchmarkDirect(mode);
                }
                return;
            }

            // Interactive game (original behavior)
            GameManager gameManager = new GameManager();
            Console.WriteLine("选择模式：");
            Console.WriteLine("1. 运行塔防游戏");
            Console.WriteLine("2. 运行性能测试");
            Console.WriteLine("3. 微基准测试");
            Console.WriteLine("4. 生产 Registry FrameGraph 固定负载压测");
            string input = Console.ReadLine();

            if (input == "2")
            {
                gameManager.RunBenchmark(10000);
            }
            else if (input == "3")
            {
                gameManager.RunBenchmark(3);
            }
            else if (input == "4")
            {
                gameManager.RunBenchmark(4);
            }
            else
            {
                gameManager.Initialize();
                gameManager.Run();
            }
        }

        private static void RunBenchmarkDirect(int scenario)
        {
            var store = new Core.ComponentStore();
            var gameConfig = new Config.GameConfig();
            Config.GameConfigLoader.LoadConfig(new Core.ConsoleLogger());
            var bench = new Systems.BenchmarkSystem(store);
            bench.RunBenchmark(scenario);
        }
    }
}
