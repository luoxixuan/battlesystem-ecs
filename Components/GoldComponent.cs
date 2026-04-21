namespace BattleSystemECS.Components
{
    /// <summary>
    /// 金币组件 - SOA (Struct of Arrays) 优化
    /// 管理玩家金币
    /// </summary>
    public struct GoldComponent
    {
        /// <summary>
        /// 当前金币数量
        /// </summary>
        public float CurrentGold;

        /// <summary>
        /// 总金币数量
        /// </summary>
        public float TotalGold;

        public GoldComponent(float currentGold, float totalGold)
        {
            CurrentGold = currentGold;
            TotalGold = totalGold;
        }
    }
}