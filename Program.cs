using System;
using BattleSystemECS.Core;

namespace BattleSystemECS
{
    class Program
    {
        static void Main(string[] args)
        {
            // Non-interactive benchmark runner:
            //   dotnet run 2        → mode 2 benchmark (hand-merged loop)
            //   dotnet run 4        → mode 4 benchmark (real system chain)
            //   dotnet run          → interactive game
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
                else if (mode == 3)
                {
                    RunBenchmarkDirect(3);
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
            Console.WriteLine("4. 真实系统链路压测");
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
