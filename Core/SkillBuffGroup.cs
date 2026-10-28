#nullable enable
using System;
namespace BattleSystemECS.Core
{
    /// <summary>Skill resolution, buff DoT, bleed damage.</summary>
    internal sealed class SkillBuffGroup
    {
        public Systems.BuffSystem? Buff { get; set; }
        public Systems.SkillSystem? Skill { get; set; }
        // Elemental reactions: element timer decay + shield-break reaction drain + exposure
        // window maintenance. Must run in this phase — Update() needs to see the shield breaks
        // that Combat (Phase 8) appended to PendingShieldBreaks this frame, and its reaction
        // damage must be resolved before the Phase 10 death pass.
        public Systems.ElementalReactionSystem? ElementalReaction { get; set; }
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
        // Round 187 Direction 4 — Rally Buff. combat.rally.consume 在 tower-attack 前
        // 消费 DamageApplied；本节点 tick 时长/CD，并再消费一次战斗段伤害。
        public Systems.RallySystem? Rally { get; set; }
        internal void RegisterFrameBindings(FrameScheduler s)
        {
            if(Buff!=null){s.RegisterFrameBinding(FrameBindingFacts.Get("skill-buff.buff.update"),c=>Buff?.Update(c.Delta));s.RegisterFrameBinding(FrameBindingFacts.Get("skill-buff.buff.resolve-dot"),c=>Buff?.ResolveDotDamage());}
            if(Skill!=null){s.RegisterFrameBinding(FrameBindingFacts.Get("skill-buff.skill.resolve-damage"),c=>Skill?.ResolveSkillDamage());s.RegisterFrameBinding(FrameBindingFacts.Get("skill-buff.skill.update"),c=>Skill?.Update(c.Delta));}
            if(ElementalReaction!=null){s.RegisterFrameBinding(FrameBindingFacts.Get("skill-buff.elemental.update"),c=>ElementalReaction?.Update(c.Delta));s.RegisterFrameBinding(FrameBindingFacts.Get("skill-buff.elemental.resolve"),c=>ElementalReaction?.ResolveReactionDamage());}
            if(Bleed!=null){s.RegisterFrameBinding(FrameBindingFacts.Get("skill-buff.bleed.update"),c=>Bleed?.Update(c.Delta));s.RegisterFrameBinding(FrameBindingFacts.Get("skill-buff.bleed.resolve"),c=>Bleed?.ResolveBleedDamage());}
            if(Frostbite!=null){s.RegisterFrameBinding(FrameBindingFacts.Get("skill-buff.frostbite.update"),c=>Frostbite?.Update(c.Delta));s.RegisterFrameBinding(FrameBindingFacts.Get("skill-buff.frostbite.resolve"),c=>Frostbite?.ResolveFrostbiteDamage());}
            if(HealingZone!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("skill-buff.healing-zone.update"),c=>HealingZone?.Update(c.Delta));
            if(Mark!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("skill-buff.mark.update"),c=>Mark?.Update(c.Delta));
            if(DeathMark!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("skill-buff.death-mark.update"),c=>DeathMark?.Update(c.Delta));
            if(HealAura!=null){s.RegisterFrameBinding(FrameBindingFacts.Get("skill-buff.heal-aura.prepare"),c=>HealAura?.SetTurn());s.RegisterFrameBinding(FrameBindingFacts.Get("skill-buff.heal-aura.update"),c=>HealAura?.Update(c.Delta));}
            if(ThornsAura!=null){s.RegisterFrameBinding(FrameBindingFacts.Get("skill-buff.thorns-aura.prepare"),c=>ThornsAura?.SetTurn());s.RegisterFrameBinding(FrameBindingFacts.Get("skill-buff.thorns-aura.update"),c=>ThornsAura?.Update(c.Delta,ThornsAuraPlayerId));}
            if(Wisp!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("skill-buff.wisp.update"),c=>Wisp?.Update(c.Delta));
            if(Rally!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("skill-buff.rally.update"),c=>Rally?.Update(c.Delta));
        }
        internal void ConsumeRally()
        {
            Rally?.ConsumePlayerDamageFacts();
            Rally?.ApplyActiveBonuses();
        }
    }
}
