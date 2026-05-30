namespace BattleSystemECS.Components
{
    /// <summary>
    /// Damage type determines which enemy resistance is used for mitigation.
    /// </summary>
    public enum DamageType
    {
        Physical = 0,  // Reduced by armor (armor penetration + shred applied)
        Magic    = 1,  // Reduced by magic resist only
        True     = 2   // Bypasses all defenses
    }
}
