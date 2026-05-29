namespace BattleSystemECS.Components
{
    /// <summary>
    /// Optimized enemy action type as enum instead of string.
    /// Eliminates O(n) string comparison per enemy per frame.
    /// </summary>
    public enum EnemyActionType
    {
        None = 0,
        MoveToTarget = 1,
        AttackMelee = 2,
        RangedAttack = 3,
        ChargeAttack = 4,
        Dodge = 5,
        Retreat = 6,
        // Enemy ability actions (enemy_cast_* BT action nodes)
        SelfHeal = 7,
        AoeDamage = 8,
        BuffAllies = 9,
        StunAoe = 10,
        SlowAoe = 11,
        HealAllies = 12,
        StealthAttack = 13,
        Fear = 14,      // 恐惧：向反方向逃跑（远离玩家）
        Taunt = 15,     // 嘲讽：强制攻击特定目标
        Charm = 16,     // 魅惑：攻击其他敌人
        Land = 17,      // 着陆：飞行敌人着陆变为地面单位
        Burrow = 18,    // 钻地：敌人进入地下，不可被选中
        Emerge = 19,    // 出土：敌人从地下钻出，可造成 AoE 伤害
        Resurrect = 20  // 复活：亡灵法师复活附近尸体为次级亡灵
    }
}
