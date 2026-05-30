using System;

namespace BattleSystemECS.Components
{
    /// <summary>
    /// Damage type determines which enemy resistance is used for mitigation.
    /// Values are powers of 2 for bit-mask compatibility with DamageImmunityFlags.
    /// </summary>
    public enum DamageType
    {
        Physical = 1,  // Reduced by armor (armor penetration + shred applied)
        Magic    = 2,  // Reduced by magic resist only
        Fire     = 4,  // Reduced by fire resist; immune if Fire bit set in immunity mask
        Ice      = 8,  // Reduced by ice resist; immune if Ice bit set in immunity mask
        Lightning = 16,// Reduced by lightning resist; immune if Lightning bit set in immunity mask
        True     = 32  // Bypasses all defenses (no resistance, no immunity)
    }

    /// <summary>
    /// Damage type flag mask for enemy damage immunity.
    /// Each bit corresponds to a DamageType value (bit 0 = Physical, bit 1 = Magic, etc.).
    /// If (damageTypeMask &amp; enemyImmunityMask) != 0, the enemy takes 0 damage from that type.
    /// True damage (bit 5 = 32) cannot be immuned — it bypasses immunity entirely.
    /// </summary>
    [Flags]
    public enum DamageImmunityFlags
    {
        None = 0,
        Physical = 1,
        Magic = 2,
        Fire = 4,
        Ice = 8,
        Lightning = 16
    }
}
