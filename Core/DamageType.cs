using System;

namespace BattleSystemECS.Core
{
    /// <summary>
    /// Damage type classification — determines which resistance stat the target applies.
    /// Physical: reduced by EnemyArmor. Magic: reduced by EnemyMagicResist. True: ignores all defenses.
    /// </summary>
    public enum DamageTypeEnum
    {
        Physical = 0,
        Magic = 1,
        True = 2
    }
}