using BattleSystemECS.Components;
using BattleSystemECS.Core;

namespace BattleSystemECS.Components
{
    /// <summary>
    /// 技能组件 - SOA (Struct of Arrays) 优化
    /// 定义技能属性：名称、伤害倍率、范围形状、攻击距离、冷却时间
    /// </summary>
    public struct SkillComponent
    {
        /// <summary>
        /// 技能名称
        /// </summary>
        public string Name;

        /// <summary>
        /// 伤害倍率（1.0 = 正常伤害，4.0 = 400% 伤害）
        /// </summary>
        public float DamageMultiplier;

        /// <summary>
        /// 范围宽度（1 = 单体目标，3 = 3x3 范围）
        /// </summary>
        public int AreaWidth;

        /// <summary>
        /// 范围高度（1 = 单体目标，3 = 3x3 范围）
        /// </summary>
        public int AreaHeight;

        /// <summary>
        /// 攻击距离（格）
        /// </summary>
        public int AttackRange;

        /// <summary>
        /// 冷却时间（秒）
        /// </summary>
        public float Cooldown;

        /// <summary>
        /// 当前冷却时间（秒）
        /// </summary>
        public float CurrentCooldown;
    }
}
