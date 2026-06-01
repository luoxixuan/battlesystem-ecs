using System;

namespace BattleSystemECS.Core
{
    /// <summary>
    /// 控制台日志渲染器 - 实现 IRenderer 接口
    /// </summary>
    public class ConsoleLogger : IRenderer
    {
        public static bool EnableLog { get; set; } = true;

        public void Log(string message)
        {
            if (EnableLog)
                Console.WriteLine($"[INFO] {message}");
        }

        public void LogBattle(string message)
        {
            if (EnableLog)
                Console.WriteLine($"[BATTLE] {message}");
        }

        public void LogDamage(string attacker, string defender, float damage, bool isCritical = false)
        {
            if (EnableLog)
            {
                string crit = isCritical ? " [暴击!]" : "";
                Console.WriteLine($"[DAMAGE] {attacker} 攻击 {defender}，造成 {damage:F1} 点伤害{crit}");
            }
        }

        public void LogDeath(string entity)
        {
            if (EnableLog)
                Console.WriteLine($"[DEATH] {entity} 已死亡！");
        }

        public void LogWin(string winner)
        {
            if (EnableLog)
            {
                Console.WriteLine($"[WIN] 战斗结束，{winner} 获胜！");
                Console.WriteLine("========================================");
            }
        }

        public void LogBattleStart(string battleName)
        {
            if (EnableLog)
            {
                Console.WriteLine("========================================");
                Console.WriteLine($"[BATTLE] 战斗开始：{battleName}");
                Console.WriteLine("========================================");
            }
        }

        public void LogTurn(int turn)
        {
            if (EnableLog)
                Console.WriteLine($"[BATTLE] --- 第 {turn} 回合 ---");
        }

        public void RenderGhostTower(int x, int y, int range, bool valid, string towerType)
        {
            if (!EnableLog) return;
            string status = valid ? "合法" : "非法";
            string marker = valid ? "[预览 ✓]" : "[预览 ✗]";
            Console.WriteLine($"{marker} 塔种={towerType} 位置=({x},{y}) 射程={range} 状态={status}");
        }
    }
}
