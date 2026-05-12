using System;
using System.IO;

namespace BattleSystemECS.Core
{
    /// <summary>
    /// 文件日志渲染器 - 实现 IRenderer 接口
    /// </summary>
    public class FileLogger : IRenderer
    {
        private string logFilePath;

        public FileLogger(string logFilePath = "battle_log.txt")
        {
            this.logFilePath = logFilePath;
        }

        public void Log(string message)
        {
            WriteToFile($"[INFO] {message}");
        }

        public void LogBattle(string message)
        {
            WriteToFile($"[BATTLE] {message}");
        }

        public void LogDamage(string attacker, string defender, float damage, bool isCritical = false)
        {
            string crit = isCritical ? " [暴击!]" : "";
            WriteToFile($"[DAMAGE] {attacker} 攻击 {defender}，造成 {damage:F1} 点伤害{crit}");
        }

        public void LogDeath(string entity)
        {
            WriteToFile($"[DEATH] {entity} 已死亡！");
        }

        public void LogWin(string winner)
        {
            WriteToFile($"[WIN] 战斗结束，{winner} 获胜！");
            WriteToFile("========================================");
        }

        public void LogBattleStart(string battleName)
        {
            WriteToFile("========================================");
            WriteToFile($"[BATTLE] 战斗开始：{battleName}");
            WriteToFile("========================================");
        }

        public void LogTurn(int turn)
        {
            WriteToFile($"[BATTLE] --- 第 {turn} 回合 ---");
        }

        private void WriteToFile(string message)
        {
            try
            {
                File.AppendAllText(logFilePath, message + Environment.NewLine, System.Text.Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] 写入日志文件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 清空日志文件
        /// </summary>
        public void ClearLog()
        {
            try
            {
                File.WriteAllText(logFilePath, "", System.Text.Encoding.UTF8);
                Console.WriteLine($"[INFO] 日志文件已清空: {logFilePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] 清空日志文件失败: {ex.Message}");
            }
        }
    }
}
