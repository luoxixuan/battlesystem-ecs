#nullable enable
namespace BattleSystemECS.Core
{
    /// <summary>Skill resolution, buff DoT, bleed damage.</summary>
    public class SkillBuffGroup : ISystemGroup
    {
        public Systems.BuffSystem? Buff { get; set; }
        public Systems.SkillSystem? Skill { get; set; }
        public Systems.BleedSystem? Bleed { get; set; }
        public Systems.HealingZoneSystem? HealingZone { get; set; }
        // Wisp aura pets — runs after HealingZone so wisp heal/slow/curse are layered
        // on top of any healing-zone heals in the same frame.
        public Systems.WispSystem? Wisp { get; set; }
        // Round 107 Direction 6 — Target Mark decay. Runs after Bleed (combat debuff
        // resolution) but before Skill cooldown update, so mark events triggered by
        // a hit this frame are observable to SkillSystem in the same frame.
        public Systems.MarkSystem? Mark { get; set; }
        // Round 122 Direction 2 — Heal Aura System (passive tower-to-tower healing).
        // Runs after Bleed and HealingZone (other healing/debuff systems) so heal ticks
        // are layered on top of any other heal effects in the same frame. SetTurn first
        // rebuilds the healer cache; Update fires the actual heal ticks.
        public Systems.HealAuraSystem? HealAura { get; set; }

        public void Execute(ComponentStore store, float deltaTime, int turn)
        {
            Buff?.Update(deltaTime);
            Skill?.ResolveSkillDamage();
            Buff?.ResolveDotDamage();
            Bleed?.Update(deltaTime);
            Bleed?.ResolveBleedDamage();
            HealingZone?.Update(deltaTime);
            Mark?.Update(deltaTime);
            // Heal aura: cache healer tower IDs first, then fire heal ticks. Both calls
            // are zero-cost when no heal-aura tower is on the field (SetTurn filter early
            // returns, Update early returns on empty healer cache).
            HealAura?.SetTurn();
            HealAura?.Update(deltaTime);
            Skill?.Update(deltaTime);
            Wisp?.Update(deltaTime);
        }
    }
}
