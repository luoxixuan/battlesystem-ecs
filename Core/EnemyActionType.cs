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
        Resurrect = 20,  // 复活：亡灵法师复活附近尸体为次级亡灵
        EstablishLink = 21,  // 建立生命链接：与附近敌人建立生命链接
        Clone = 22,          // 克隆：生成自身功能性克隆体
        Banished = 23,       // 放逐：敌人被移出战场，冻结在原位置/不可行动/不可被攻击
        Staggered = 24,      // 失衡/破防：敌人姿态条满后强制硬直，暂停所有动作/可被处决
        Tethered = 25        // 锁链/连接：与另一敌人被链子绑定，超距时减速 + 互拉；partner 受 DoT 伤害按比例传染
    }
}
