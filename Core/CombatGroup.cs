#nullable enable
namespace BattleSystemECS.Core
{
    /// <summary>Main combat phase: attacks, synergy, auras, curses, projectiles, mana, skills.</summary>
    internal sealed class CombatGroup
    {
        public Systems.PlayerTowerAttackSystem? PlayerTowerAttack { get; set; }
        public Systems.TowerAttackSystem? TowerAttack { get; set; }
        public Systems.TowerSynergySystem? TowerSynergy { get; set; }
        // Round 180 Direction 5 — Fortress Aura. SetTurn happens in CombatSetupGroup
        //   so the cached bonuses are already populated when TowerAttackSystem reads
        //   them. No per-frame Update needed (Fortress is a placement-time stat, not
        //   a tick-based effect).
        public Systems.TowerFortressSystem? TowerFortress { get; set; }
        public Systems.AuraTowerSystem? AuraTower { get; set; }
        // Round 173 Direction 1 — Shrine Tower System. Persistent pure-buff aura on
        //   friendly towers; no auto-attack, no projectile, no enemy targeting. Runs
        //   alongside AuraTower.ResolveAuraBuffs in the serial aura phase so the
        //   cached damage / attack-speed bonuses are visible to downstream consumers
        //   this same frame.
        public Systems.TowerShrineSystem? TowerShrine { get; set; }
        // Round 177 Direction 2 — Beacon Tower System. Active command-post broadcast
        //   buff (damage + attack-speed) to friendly towers; no auto-attack, no
        //   projectile, no enemy targeting. Runs alongside Shrine/ResolveShrineBuffs
        //   in the serial aura phase so the cached bonuses are visible to downstream
        //   consumers this same frame.
        public Systems.TowerBeaconSystem? TowerBeacon { get; set; }
        public Systems.CurseAuraSystem? Curse { get; set; }
        public Systems.PullTowerSystem? PullTower { get; set; }
        public Systems.ProjectileSystem? Projectile { get; set; }
        public Systems.PickupSystem? Pickup { get; set; }
        public Systems.ManaSystem? Mana { get; set; }
        // Round 175 Direction 1 — Mana Shield System. Runs immediately after Mana
        //   so it can read the freshly-regenerated PlayerMana and decide whether
        //   to convert excess into PlayerManaShield. Sentinel-gated: when
        //   ManaShieldConfig.Enabled = false the system only forces the per-player
        //   absorb ratio to 0 (so the damage hot-path stays cheap).
        public Systems.ManaShieldSystem? ManaShield { get; set; }
        public Systems.GlobalSkillSystem? GlobalSkill { get; set; }
        public Systems.HitShieldSystem? HitShield { get; set; }
        public Systems.TowerSabotageSystem? TowerSabotage { get; set; }
        public Systems.HeroSystem? Hero { get; set; }
        public Systems.ReflectTowerSystem? ReflectTower { get; set; }
        public Systems.TowerMorphSystem? TowerMorph { get; set; }
        public Systems.TauntSystem? Taunt { get; set; }
        // Round 138 — Per-Tower Active Skill (manual cast, cooldown-tick). Wired after
        //   TowerAttack so its per-frame cooldown tick happens alongside other tower
        //   state updates. Effect dispatch is a no-op log + cooldown flip until the
        //   SkillSystem.CastByTower refactor lands; the gate itself is the value.
        public Systems.TowerActiveSkillSystem? TowerActiveSkill { get; set; }
        // Round 142 方向5 — Aggro / Focus Fire System. Player-driven mark-focus command
        //   that lets enemies prioritize a chosen tower for N seconds. Update() is
        //   O(n_active_enemies) only when at least one focus is active; otherwise
        //   O(1) fast-path (single bool sentinel). Runs last in Combat so the focus
        //   duration tick happens after all attack resolution for the frame.
        public Systems.AggroSystem? Aggro { get; set; }
        // Round 144 方向4 — Hero Active Skill Set. Per-frame cooldown tick for the
        //   4-slot skill set bound to each deployed hero. O(1) when no skill is
        //   configured (sentinel _anySkillConfigured in the system).
        public Systems.HeroSkillSystem? HeroSkill { get; set; }
        // Round 201 Direction 8 — Echo Clone System. Per-frame cooldown tick +
        //   spawn roll + lifetime expiry for transient phantom-tower clones.
        //   Sentinel-gated: O(1) when no parent tower on the field is configured
        //   to spawn echoes. Runs last in Combat so the spawn roll sees the
        //   parent's freshly-cached damage / attack-speed (aura phase done).
        public Systems.EchoCloneSystem? EchoClone { get; set; }
        // Round 176 Direction 2 — Bloodlust System. Per-frame tick that walks
        //   every active tower, sheds stacks past the decay window, and re-derives
        //   the cached damage / speed mults that TowerAttackSystem reads inline.
        //   Sentinel-gated: when BloodlustConfig.Enabled = false the body is
        //   a single O(activeTowers) clear-pass that forces mults to 0.
        public Systems.BloodlustSystem? Bloodlust { get; set; }
        // Round174+ Direction3 — Momentum System. Per-frame tick that advances
        //   the per-player wave-time timer (only while _waveRunning is true),
        //   recomputes the current tier, and re-derives the cached damage /
        //   speed bonuses that TowerAttackSystem reads inline. Sentinel-gated:
        //   when MomentumConfig.Enabled = false the body is a single
        //   O(activeTowers) clear-pass that forces bonuses to 0. Runs last in
        //   Combat (after Bloodlust) so the next frame's hot path sees the
        //   freshly-computed values.
        public Systems.MomentumSystem? Momentum { get; set; }
        // Round 207 Direction 2 — Adrenaline System. Per-frame tick that walks
        // the MAX_PLAYERS slots, derives the tier from the live HP ratio, and
        // stamps the cached attack-speed bonus (additive) + cooldown mult
        // (multiplicative) into the per-player cache arrays. The rush window is
        // detected on tier change (1 → 2 entry) and force-fires player towers
        // for RushDurationFrames (read by PlayerTowerAttackSystem). Wired after
        // Momentum so the cache fields it writes are visible on the *next*
        // frame's PlayerTowerAttackSystem call. Sentinel-gated: Enabled=false /
        // degenerate thresholds → single O(MAX_PLAYERS) clear-pass.
        public Systems.AdrenalineSystem? Adrenaline { get; set; }
        // Round 178+ Direction 5 — Crest / Tide System. Event-driven (no
        // per-frame Update needed), but exposed in CombatGroup so the
        // scheduler tick-list can include it uniformly. The per-frame
        // Update() is a no-op (sentinel).
        public Systems.CrestSystem? Crest { get; set; }
        // Round 206 Direction 1 — Culling System. Per-frame Update is a no-op
        // (event-driven; the hot path is invoked from TowerAttackSystem via the
        // injected CullingSystem reference). Exposed in CombatGroup so the
        // scheduler can call it uniformly. OnWaveStart resets per-player stacks.
        public Systems.CullingSystem? Culling { get; set; }

        internal void RegisterFrameBindings(FrameScheduler s)
        {
            if(PlayerTowerAttack!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.player-attack.update"),c=>PlayerTowerAttack?.Update());
            if(HitShield!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.hit-shield.update"),c=>HitShield?.Update(c.Delta));
            if(TowerSabotage!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.sabotage.update"),c=>TowerSabotage?.Update(c.Delta));
            if(TowerSynergy!=null){s.RegisterFrameBinding(FrameBindingFacts.Get("combat.synergy.resolve-buff-shares"),c=>TowerSynergy?.ResolveBuffShares());s.RegisterFrameBinding(FrameBindingFacts.Get("combat.synergy.update"),c=>TowerSynergy?.Update());}
            if(TowerAttack!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.tower-attack.update"),c=>TowerAttack?.Update(c.Delta));
            if(AuraTower!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.aura.resolve"),c=>AuraTower?.ResolveAuraBuffs());
            if(TowerShrine!=null){s.RegisterFrameBinding(FrameBindingFacts.Get("combat.shrine.prepare"),c=>TowerShrine?.SetTurn());s.RegisterFrameBinding(FrameBindingFacts.Get("combat.shrine.resolve"),c=>TowerShrine?.ResolveShrineBuffs());}
            if(TowerBeacon!=null){s.RegisterFrameBinding(FrameBindingFacts.Get("combat.beacon.prepare"),c=>TowerBeacon?.SetTurn());s.RegisterFrameBinding(FrameBindingFacts.Get("combat.beacon.resolve"),c=>TowerBeacon?.ResolveBeaconBuffs());}
            if(Curse!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.curse.resolve"),c=>Curse?.ResolveCurseDebuffs());
            if(PullTower!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.pull-tower.update"),c=>PullTower?.Update(c.Delta));
            if(Projectile!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.projectile.update"),c=>Projectile?.Update(c.Delta));
            if(Pickup!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.pickup.update"),c=>Pickup?.Update(c.Delta));
            if(Mana!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.mana.update"),c=>Mana?.Update(c.Delta,false));
            if(ManaShield!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.mana-shield.update"),c=>ManaShield?.Update(c.Delta));
            if(GlobalSkill!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.global-skill.update"),c=>GlobalSkill?.Update(c.Delta,false));
            if(Hero!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.hero.update"),c=>Hero?.Update(c.Delta));
            if(ReflectTower!=null){s.RegisterFrameBinding(FrameBindingFacts.Get("combat.reflect.resolve"),c=>ReflectTower?.ResolveReflect());s.RegisterFrameBinding(FrameBindingFacts.Get("combat.reflect.apply"),c=>ReflectTower?.ApplyReflectDamage());}
            if(TowerMorph!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.tower-morph.update"),c=>TowerMorph?.Update(c.Delta));
            if(Taunt!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.taunt.resolve"),c=>Taunt?.ResolveTauntAssignments());
            if(TowerActiveSkill!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.tower-active-skill.update"),c=>TowerActiveSkill?.Update(c.Delta));
            if(Aggro!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.aggro.update"),c=>Aggro?.Update(c.Delta));
            if(HeroSkill!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.hero-skill.update"),c=>HeroSkill?.Update(c.Delta));
            if(EchoClone!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.echo-clone.update"),c=>EchoClone?.Update(c.Delta));
            if(Bloodlust!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.bloodlust.update"),c=>Bloodlust?.Update(c.Turn));
            if(Momentum!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.momentum.update"),c=>Momentum?.Update(c.Delta));
            if(Adrenaline!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.adrenaline.update"),c=>Adrenaline?.Update(c.Delta));
            if(Crest!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.crest.update"),c=>Crest?.Update(c.Delta));
            if(Culling!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.culling.update"),c=>Culling?.Update(c.Delta));
        }

    }
}
