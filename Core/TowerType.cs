namespace BattleSystemECS.Components
{
    /// <summary>
    /// Tower type — determines special attack mechanics and synergies.
    /// Replaces runtime string comparisons with compile-time enum safety.
    /// </summary>
    public enum TowerType
    {
        Basic    = 0,
        AOE      = 1,
        Sniper   = 2,
        Tesla    = 3,
        Leech    = 4,
        Frost    = 5,
        Stun     = 6,
        EMP      = 7,
        Firewall = 8
    }
}
