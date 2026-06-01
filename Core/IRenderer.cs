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

        /// <summary>
        /// Render a "ghost" preview of a tower before placement.
        /// x/y = position, range = tower's attack range (render as radius),
        /// valid = whether the location is legal (green vs red color in real UIs),
        /// towerType = tower archetype name for label display.
        /// Default implementation is a no-op for renderers that don't need it
        /// (e.g., file-only loggers, test mocks). Renderers with a UI override this.
        /// </summary>
        void RenderGhostTower(int x, int y, int range, bool valid, string towerType)
        {
            // No-op default — keep IRenderer backward-compatible.
        }
    }
}
