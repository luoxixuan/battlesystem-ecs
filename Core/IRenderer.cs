namespace BattleSystemECS.Core
{
    /// <summary>
    /// 渲染器接口 - 用于逻辑和渲染分离
    /// </summary>
    public interface IRenderer
    {
        void Log(string message);
        void LogBattle(string message);
        void LogDamage(string attacker, string defender, float damage, bool isCritical);
        void LogDeath(string entity);
        void LogWin(string winner);
        void LogBattleStart(string battleName);
        void LogTurn(int turn);
    }
}
