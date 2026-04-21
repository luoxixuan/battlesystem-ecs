namespace BattleSystemECS.Components
{
    /// <summary>
    /// 游戏状态组件 - SOA (Struct of Arrays) 优化
    /// 管理游戏状态
    /// </summary>
    public struct GameStateComponent
    {
        /// <summary>
        /// 当前波次
        /// </summary>
        public int CurrentWave;

        /// <summary>
        /// 总波次
        /// </summary>
        public int TotalWaves;

        /// <summary>
        /// 游戏是否进行中
        /// </summary>
        public bool IsGameRunning;

        /// <summary>
        /// 玩家生命值
        /// </summary>
        public float PlayerHealth;

        /// <summary>
        /// 玩家最大生命值
        /// </summary>
        public float PlayerMaxHealth;

        public GameStateComponent(int currentWave, int totalWaves, bool isGameRunning, float playerHealth, float playerMaxHealth)
        {
            CurrentWave = currentWave;
            TotalWaves = totalWaves;
            IsGameRunning = isGameRunning;
            PlayerHealth = playerHealth;
            PlayerMaxHealth = playerMaxHealth;
        }
    }
}