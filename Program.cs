using System;
using BattleSystemECS.Core;

namespace BattleSystemECS
{
    class Program
    {
        static void Main(string[] args)
        {
            // 创建游戏管理器（SOA 架构）
            GameManager gameManager = new GameManager();

            Console.WriteLine("选择模式：");
            Console.WriteLine("1. 运行塔防游戏");
            Console.WriteLine("2. 运行性能测试");
            Console.WriteLine("3. 微基准测试");
            string input = Console.ReadLine();

            if (input == "2")
            {
                gameManager.RunBenchmark(10000);
            }
            else if (input == "3")
            {
                gameManager.RunBenchmark(3);
            }
            else
            {
                // 初始化游戏
                gameManager.Initialize();
                // 运行游戏主循环
                gameManager.Run();
            }
        }
    }
}