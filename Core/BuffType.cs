namespace BattleSystemECS.Components
{
    /// <summary>
    /// Bit-flag buff types for O(1) lookup in PlayerBuffFlags.
    /// </summary>
    public enum BuffType
    {
        None = 0,
        AttackBoost = 1 << 0,   // "Attack+10%" → damage × 1.1
        CritRateBoost = 1 << 1, // "Crit Rate+5%" → crit chance +5%
        DefenseBoost = 1 << 2,  // "Defense+10%" → future use
        Stun       = 1 << 8,    // 晕眩：本回合不移动
        Slow       = 1 << 9,    // 减速：移动速度 × slow_factor
    }
}