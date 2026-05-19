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
        BuffAllies = 9
    }
}
