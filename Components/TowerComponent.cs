namespace BattleSystemECS.Components
{
    /// <summary>
    /// 塔组件 - SOA (Struct of Arrays) 优化
    /// 定义塔的属性：类型、攻击力、射程、攻击速度、等级
    /// </summary>
    public struct TowerComponent
    {
        /// <summary>
        /// 塔类型
        /// </summary>
        public string Type;

        /// <summary>
        /// 攻击力
        /// </summary>
        public float AttackDamage;

        /// <summary>
        /// 射程（格）
        /// </summary>
        public int Range;

        /// <summary>
        /// 攻击速度（每秒攻击次数）
        /// </summary>
        public float AttackSpeed;

        /// <summary>
        /// 等级
        /// </summary>
        public int Level;

        /// <summary>
        /// 升级成本
        /// </summary>
        public float UpgradeCost;

        /// <summary>
        /// 是否已激活
        /// </summary>
        public bool IsActive;

        /// <summary>
        /// 最后攻击时间
        /// </summary>
        public float LastAttackTime;

        public TowerComponent(string type, float attackDamage, int range, float attackSpeed, int level, float upgradeCost)
        {
            Type = type;
            AttackDamage = attackDamage;
            Range = range;
            AttackSpeed = attackSpeed;
            Level = level;
            UpgradeCost = upgradeCost;
            IsActive = true;
            LastAttackTime = 0f;
        }
    }
}