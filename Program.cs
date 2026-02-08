using BattleSystemECS.Core;

namespace BattleSystemECS
{
    class Program
    {
        static void Main(string[] args)
        {
            // 创建游戏管理器（SOA 架构）
            GameManager gameManager = new GameManager();

            // 初始化游戏
            gameManager.Initialize();

            // 运行游戏主循环
            gameManager.Run();
        }
    }
}
