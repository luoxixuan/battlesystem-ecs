using BattleSystemECS.Systems;
using BattleSystemECS.Core;

namespace BattleSystemECS
{
    class Program
    {
        static void Main(string[] args)
        {
            // 创建游戏管理器（SOA 架构）
            Systems.GameManager gameManager = new Systems.GameManager();

            // 初始化游戏
            gameManager.Initialize();

            // 运行游戏主循环
            gameManager.Run();
        }
    }
}
