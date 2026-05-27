namespace BattleSystemECS.Components
{
    /// <summary>
    /// Element types for the Elemental Reaction System.
    /// Each element can be applied to an enemy and triggers reactions when combined.
    /// </summary>
    public enum ElementType
    {
        None = 0,
        Fire   = 1 << 0,   // 火焰：持续灼烧，可与冰/雷/毒触发反应
        Ice    = 1 << 1,   // 冰冻：减速，可与火触发反应
        Lightning = 1 << 2, // 雷电：链式传导，可与火/毒触发反应
        Poison = 1 << 3    // 毒素：持续掉血，可与火/雷触发反应
    }

    /// <summary>
    /// Elemental reaction types — triggered when two elements interact on a target.
    /// </summary>
    public enum ElementalReactionType
    {
        None = 0,
        Frozen        = 1, // Ice + Fire: 冻结目标，暂停移动
        Shatter       = 2, // Frozen + 物理攻击: 碎冰，额外伤害 + 短暂晕眩
        Superconduct  = 3, // Lightning + Poison: AoE 雷伤，冰冷地带
        Overload      = 4, // Fire + Lightning: 爆炸伤害
        Pyroclastic   = 5, // Fire + Poison: 火焰爆发
        Melt          = 6  // Fire + Ice: 火属性增伤
    }
}