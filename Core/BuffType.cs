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

        // ── Per-Enemy Affix Flags（词缀位掩码）─────────────────────────────
        // 每个敌人出生时随机分配 1-3 个词缀，每个词缀有独立效果
        // 注意：占用 bit 16-31，避免与玩家 buff (bit 0-15) 冲突
        AffixExtraFast   = 1 << 16, // 移动速度 ×1.5
        AffixVampiric    = 1 << 17, // 击杀敌人时回复自身 maxHealth×0.05/秒
        AffixMolten      = 1 << 18, // 死亡时对周围 2 格敌人造成 maxHealth×0.3 伤害
        AffixShielding   = 1 << 19, // 初始护盾 = maxHealth×0.5
        AffixTeleporter  = 1 << 20, // 随机传送（冷却 5 秒）
        AffixRegen       = 1 << 21, // 每秒回复 maxHealth×0.02
        AffixExplosive   = 1 << 22, // 死亡时对所有敌人造成 maxHealth×0.2 爆炸伤害
    }
}