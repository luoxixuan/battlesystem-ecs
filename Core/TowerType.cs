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
        Firewall = 8,
        // Round 100: Palisade — control-type tower, no attack, stuns nearby enemies
        // (delays movement by N frames via EnemyStunDurationLeft). HP-based destructible.
        Palisade = 9,
        // Round 106 Direction 2: Mine — defensive trap tower, no auto-attack, detonates AoE
        // damage on enemy proximity after a short arm time. Stacks allow multi-charge mines.
        Mine = 10
    }
}
