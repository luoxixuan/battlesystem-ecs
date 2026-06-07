#nullable enable
namespace BattleSystemECS.Core
{
    /// <summary>Skill resolution, buff DoT, bleed damage.</summary>
    public class SkillBuffGroup : ISystemGroup
    {
        public Systems.BuffSystem? Buff { get; set; }
        public Systems.SkillSystem? Skill { get; set; }
        public Systems.BleedSystem? Bleed { get; set; }
        // Round 170 Direction 6 — Frostbite (non-stacking %-of-maxHP DoT).
        // Runs after Bleed (combat debuff resolution) so %-based damage is layered
        // on top of any bleed damage in the same frame.
        public Systems.FrostbiteSystem? Frostbite { get; set; }
        public Systems.HealingZoneSystem? HealingZone { get; set; }
        // Wisp aura pets — runs after HealingZone so wisp heal/slow/curse are layered
        // on top of any healing-zone heals in the same frame.
        public Systems.WispSystem? Wisp { get; set; }
        // Round 107 Direction 6 — Target Mark decay. Runs after Bleed (combat debuff
        // resolution) but before Skill cooldown update, so mark events triggered by
        // a hit this frame are observable to SkillSystem in the same frame.
        public Systems.MarkSystem? Mark { get; set; }
        // Round 200 Direction 5 — Death Mark decay. Runs after Mark so Death Mark
        // events are processed in the same frame as Target Mark events (both are
        // hit-counter debuffs; Death Mark additionally fires the auto-execute).
        public Systems.DeathMarkSystem? DeathMark { get; set; }
        // Round 122 Direction 2 — Heal Aura System (passive tower-to-tower healing).
        // Runs after Bleed and HealingZone (other healing/debuff systems) so heal ticks
        // are layered on top of any other heal effects in the same frame. SetTurn first
        // rebuilds the healer cache; Update fires the actual heal ticks.
        public Systems.HealAuraSystem? HealAura { get; set; }
        // Round 126 Direction 4 — Thorns Aura System (passive tower-centered damage on enemies).
        // Runs after HealAura so thorns damage is layered on top of any same-frame heal
        // effects (heal→thorns in the same frame can still kill a wounded enemy). The
        // playerId is plumbed through the group so Update can attribute QueueEnemyDeath
        // to the killing player.
        public Systems.ThornsAuraSystem? ThornsAura { get; set; }
        public int ThornsAuraPlayerId { get; set; } = 0;
        // Round 187 Direction 4 — Rally Buff. Per-frame tick: decrement PlayerRallyCooldown
        // and PlayerRallyDurationLeft, recompute per-tower TowerRallyAtkSpdBonus from
        // the live PlayerRallyActive set. Subscribes to PlayerDamaged in its constructor
        // (via SystemRegistry) to activate the rally on player damage.
        public Systems.RallySystem? Rally { get; set; }

        public void Execute(ComponentStore store, float deltaTime, int turn)
        {
            Buff?.Update(deltaTime);
            Skill?.ResolveSkillDamage();
            Buff?.ResolveDotDamage();
            Bleed?.Update(deltaTime);
            Bleed?.ResolveBleedDamage();
            // Round 170 Direction 6 — Frostbite (non-stacking %-of-maxHP DoT)
            Frostbite?.Update(deltaTime);
            Frostbite?.ResolveFrostbiteDamage();
            HealingZone?.Update(deltaTime);
            Mark?.Update(deltaTime);
            // Round 200 Direction 5 — Death Mark decay (after Mark). Auto-execute payoff
            // queues enemy death in the same frame as the final stack hit, which the
            // death-resolution pass handles cleanly at frame boundary.
            DeathMark?.Update(deltaTime);
            // Heal aura: cache healer tower IDs first, then fire heal ticks. Both calls
            // are zero-cost when no heal-aura tower is on the field (SetTurn filter early
            // returns, Update early returns on empty healer cache).
            HealAura?.SetTurn();
            HealAura?.Update(deltaTime);
            // Thorns aura: cache thorns-emitter tower IDs first, then fire thorns ticks.
            // Same zero-overhead contract as HealAura. SetTurn early-returns on no
            // IsThornsTower flag set; Update early-returns on empty cache or empty
            // active-enemy set. The thorns damage write happens in serial Phase 9,
            // before ResolveEnemiesKilledThisFrame in Phase 10, so any deaths
            // queued here are resolved cleanly at the frame boundary.
            ThornsAura?.SetTurn();
            ThornsAura?.Update(deltaTime, ThornsAuraPlayerId);
            Skill?.Update(deltaTime);
            Wisp?.Update(deltaTime);
            // Round 187 Direction 4 — Rally Buff. Runs at the end of SkillBuffGroup
            // (after all other time-based buffs have ticked this frame) so the
            // recomputed TowerRallyAtkSpdBonus is observable to TowerAttackSystem
            // on the next frame's hot-path read. (Same gate order as Bleed/Frostbite.)
            Rally?.Update(deltaTime);
        }
    }
}
