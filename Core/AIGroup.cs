#nullable enable
namespace BattleSystemECS.Core
{
    /// <summary>Enemy AI, abilities, burrow, necromancer, life link, affixes, mana burn, fear, zone control.</summary>
    internal sealed class AIGroup : ISystemGroup
    {
        public Systems.EnemyAISystem? EnemyAI { get; set; }
        public Systems.EnemyAbilitySystem? EnemyAbility { get; set; }
        public Systems.EnemyBurrowSystem? Burrow { get; set; }
        public Systems.NecromancerSystem? Necromancer { get; set; }
        public Systems.EnemyLifeLinkSystem? LifeLink { get; set; }
        public Systems.EnemyAffixSystem? EnemyAffix { get; set; }
        public Systems.ManaBurnSystem? ManaBurn { get; set; }
        public Systems.EnemyLifestealSystem? Lifesteal { get; set; }
        public Systems.PhaseSystem? Phase { get; set; }
        public Systems.FearSystem? Fear { get; set; }
        public Systems.ZoneControlSystem? ZoneControl { get; set; }
        public Systems.EnemyStrafeSystem? EnemyStrafe { get; set; }
        public Systems.ReflectTowerSystem? ReflectTower { get; set; }
        public Systems.MagnetizeSystem? Magnetize { get; set; }
        // Round 186 Direction 2 — Sapper (engineer) enemies that attack the nearest
        // tower and apply a stackable attack-speed slow. Optional, lazy-initialized.
        public Systems.SapperSystem? Sapper { get; set; }

        internal void RegisterFrameBindings(FrameScheduler scheduler)
        {
            if (ZoneControl != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.zone-control.update"), c => ZoneControl?.Update(c.Delta));
            if (Magnetize != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.magnetize.update"), c => Magnetize?.Update(c.Delta));
            if (EnemyStrafe != null) { scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.enemy-strafe.prepare"), c => EnemyStrafe?.SetTurn()); scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.enemy-strafe.update"), c => EnemyStrafe?.Update()); }
            if (EnemyAI != null) { scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.enemy.prepare"), c => EnemyAI?.SetTurn(c.Turn, c.Delta)); scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.enemy.update"), c => EnemyAI?.Update()); }
            if (EnemyAbility != null)
            {
                scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.enemy-ability.prepare"), c => EnemyAbility?.SetTurn(c.Turn));
                scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.enemy-ability.cooldowns"), c => EnemyAbility?.UpdateCooldowns(c.Delta));
                scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.enemy-ability.execute"), c => EnemyAbility?.ExecuteAbilities());
                scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.enemy-ability.cast-timers"), c => EnemyAbility?.TickCastTimers(c.Delta));
                scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.enemy-ability.update"), c => EnemyAbility?.Update());
            }
            if (Burrow != null) { scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.burrow.prepare"), c => Burrow?.SetTurn(c.Turn)); scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.burrow.update"), c => Burrow?.Update(c.Delta)); scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.burrow.apply"), c => Burrow?.ApplyBurrowEffects()); }
            if (Necromancer != null) { scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.necromancer.prepare"), c => Necromancer?.SetTurn(c.Turn, c.Turn)); scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.necromancer.update"), c => Necromancer?.Update(c.Delta)); }
            if (LifeLink != null) { scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.life-link.prepare"), c => LifeLink?.SetTurn(c.Turn)); scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.life-link.update"), c => LifeLink?.Update()); scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.life-link.cooldowns"), c => LifeLink?.DecrementCooldowns(c.Delta)); }
            if (EnemyAffix != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.enemy-affix.update"), c => EnemyAffix?.Update(c.Delta));
            if (ManaBurn != null) { scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.mana-burn.prepare"), c => ManaBurn?.SetTurn(c.Turn)); scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.mana-burn.update"), c => ManaBurn?.Update()); }
            if (Lifesteal != null) { scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.lifesteal.prepare"), c => Lifesteal?.SetTurn(c.Turn)); scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.lifesteal.update"), c => Lifesteal?.Update()); }
            if (Phase != null) { scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.phase.prepare"), c => Phase?.SetTurn(c.Turn)); scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.phase.update"), c => Phase?.Update(c.Delta)); }
            if (Fear != null) { scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.fear.prepare"), c => Fear?.SetTurn(c.Turn)); scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.fear.update"), c => Fear?.Update(c.Delta)); }
            if (Sapper != null) { scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.sapper.prepare"), c => Sapper?.SetTurn(c.Turn, c.Delta)); scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.sapper.update"), c => Sapper?.Update(c.Delta)); scheduler.RegisterFrameBinding(FrameBindingFacts.Get("ai.sapper.recompute"), c => Sapper?.RecomputeTowerSlows()); }
        }

        public void Execute(ComponentStore store, float deltaTime, int turn)
        {
            // Zone control (CC zones: Slow/Stun/Freeze/Root) — runs before AI so CC is applied this turn
            ZoneControl?.Update(deltaTime);

            // Magnetize zones (displacement fields) — apply pull/repel force BEFORE
            // enemy AI/movement so the force is layered into the same frame's motion
            // as a pre-step (no double-iteration over enemy positions).
            Magnetize?.Update(deltaTime);

            // Enemy strafe/dodge: decrement timers and cooldowns before AI evaluates
            EnemyStrafe?.SetTurn();
            EnemyStrafe?.Update();

            EnemyAI?.SetTurn(turn, deltaTime);
            EnemyAI?.Update();

            EnemyAbility?.SetTurn(turn);
            EnemyAbility?.UpdateCooldowns(deltaTime);
            EnemyAbility?.ExecuteAbilities();
            // Tick cast timers right after ExecuteAbilities so any newly-started casts in this
            // frame (via EnqueueAbility) are visible to Movement and TowerAttack in the same
            // frame. Casts that resolve this frame will be enqueued above and executed next
            // turn (we don't re-enter ExecuteAbilities to keep the frame's resolve order stable).
            EnemyAbility?.TickCastTimers(deltaTime);
            EnemyAbility?.Update();

            Burrow?.SetTurn(turn);
            Burrow?.Update(deltaTime);
            Burrow?.ApplyBurrowEffects();

            Necromancer?.SetTurn(turn, turn);
            Necromancer?.Update(deltaTime);

            LifeLink?.SetTurn(turn);
            LifeLink?.Update();
            LifeLink?.DecrementCooldowns(deltaTime);

            EnemyAffix?.Update(deltaTime);

            ManaBurn?.SetTurn(turn);
            ManaBurn?.Update();

            Lifesteal?.SetTurn(turn);
            Lifesteal?.Update();

            Phase?.SetTurn(turn);
            Phase?.Update(deltaTime);

            Fear?.SetTurn(turn);
            Fear?.Update(deltaTime);

            // Round 186 Direction 2 — Sapper attacks (tower-damage + slow stacks).
            // Runs at the end of the AI group, AFTER all enemy-side abilities have
            // queued their effects but BEFORE movement applies path-derivative
            // damage. The two-phase split (Update = decide & damage, RecomputeTowerSlows
            // = roll up TowerSapperSlowMult) keeps the per-tower slow multiplier
            // consistent with the same frame's swing decisions.
            Sapper?.SetTurn(turn, deltaTime);
            Sapper?.Update(deltaTime);
            Sapper?.RecomputeTowerSlows();
        }
    }
}
