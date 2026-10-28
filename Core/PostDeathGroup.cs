#nullable enable
using System;
namespace BattleSystemECS.Core
{
    /// <summary>Post-death resolution: fission, objective, resources, income, corpses, combo.</summary>
    internal sealed class PostDeathGroup
    {
        public Systems.EnemyFissionSystem? EnemyFission { get; set; }
        public Systems.ObjectiveSystem? Objective { get; set; }
        public Systems.ResourceNodeSystem? ResourceNode { get; set; }
        public Systems.CorpseEffectSystem? CorpseEffect { get; set; }
        public Systems.WaveBranchSystem? WaveBranch { get; set; }
        public Systems.ComboSystem? Combo { get; set; }
        // Round 110 Direction 10 — DoomClock objective helper. Lives in PostDeath
        // because the countdown runs alongside objective state and is not
        // combat-critical (zero cost when inactive). It also advances the wave
        // cleared counter and the cycle index when a wave completes.
        public Systems.DoomClockSystem? DoomClock { get; set; }

        // Round 196 Direction 3 — Soul Harvest per-frame regen tick. Lives in
        // PostDeath (alongside DoomClock) so the regen cadence matches combo /
        // objective bookkeeping. Sentinel-gated fast path: zero overhead when
        // every player has PlayerSoulRegen == 0. OnEnemyKilled credit is
        // event-driven (synchronous in ResolveEnemiesKilledThisFrame) so kills
        // are visible to the next Update's regen check.
        public Systems.SoulHarvestSystem? SoulHarvest { get; set; }

        internal void RegisterFrameBindings(FrameScheduler s)
        {
            if(EnemyFission!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("post-death.fission.update"),c=>EnemyFission?.Update());
            if(Objective!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("post-death.objective.update"),c=>Objective?.Update(c.Delta,GameState.WavePhase));
            if(ResourceNode!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("post-death.resource-node.update"),c=>ResourceNode?.Update(c.Delta,GameState.WavePhase));
            if(CorpseEffect!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("post-death.corpse.update"),c=>CorpseEffect?.Update(c.Delta));
            if(Combo!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("post-death.combo.update"),c=>Combo?.Update(c.Delta));
            if(DoomClock!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("post-death.doom-clock.update"),c=>DoomClock?.Update(c.Delta,GameState.WavePhase));
            if(SoulHarvest!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("post-death.soul-harvest.update"),c=>SoulHarvest?.Update(c.Delta));
        }

    }
}
