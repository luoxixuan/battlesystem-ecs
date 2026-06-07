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
        Mine = 10,
        // Round 173 Direction 1: Shrine — pure-buff "tower-form totem" with no auto-attack.
        // Provides a persistent radius-based buff (aura type: gold=1, mana=2, damage=3,
        // attack-speed=4) to all friendly towers in TowerShrineRadius. Zero attack damage
        // and zero attack range by design; the value is the aura, not the kill.
        Shrine = 11,
        // Round 177 Direction 2: Beacon — active "command post" tower that broadcasts
        // attack-related buffs to ALL friendly towers in radius. Distinct from Shrine
        // (which has one typed aura: gold/mana/dmg/atk-spd only) and from AuraTower
        // (which exists in the SOACopy style with TowerAura* — but no separate TowerType).
        // Beacon provides BOTH damage and attack-speed bonuses simultaneously to every
        // friendly tower in range, stacks additively across multiple Beacons.
        // Designers opt-in by setting non-zero radius + non-zero bonus in tower config.
        // Zero attack damage / zero range by design; the value is the broadcast buff.
        Beacon = 12
    }
}
