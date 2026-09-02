#nullable enable
namespace BattleSystemECS.Core
{
    /// <summary>Main combat phase: attacks, synergy, auras, curses, projectiles, mana, skills.</summary>
    internal sealed class CombatGroup : ISystemGroup
    {
        public Systems.PlayerTowerAttackSystem? PlayerTowerAttack { get; set; }
        public Systems.TowerOverchargeSystem? TowerOvercharge { get; set; }
        public Systems.HeatSystem? Heat { get; set; }
        public Systems.TowerEnergySystem? Energy { get; set; }
        public Systems.TowerDemolishSystem? Demolish { get; set; }
        public Systems.TowerAttackSystem? TowerAttack { get; set; }
        public Systems.TowerSynergySystem? TowerSynergy { get; set; }
        // Round 180 Direction 5 — Fortress Aura. SetTurn happens in CombatSetupGroup
        //   so the cached bonuses are already populated when TowerAttackSystem reads
        //   them. No per-frame Update needed (Fortress is a placement-time stat, not
        //   a tick-based effect).
        public Systems.TowerFortressSystem? TowerFortress { get; set; }
        public Systems.TowerLinkSystem? TowerLink { get; set; }
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
        public Systems.TowerSilenceSystem? TowerSilence { get; set; }
        public Systems.TowerDispelSystem? Dispel { get; set; }
        public Systems.ProjectileSystem? Projectile { get; set; }
        public Systems.EnemyProjectileSystem? EnemyProjectile { get; set; }
        public Systems.PickupSystem? Pickup { get; set; }
        public Systems.ManaSystem? Mana { get; set; }
        // Round 175 Direction 1 — Mana Shield System. Runs immediately after Mana
        //   so it can read the freshly-regenerated PlayerMana and decide whether
        //   to convert excess into PlayerManaShield. Sentinel-gated: when
        //   ManaShieldConfig.Enabled = false the system only forces the per-player
        //   absorb ratio to 0 (so the damage hot-path stays cheap).
        public Systems.ManaShieldSystem? ManaShield { get; set; }
        public Systems.GlobalSkillSystem? GlobalSkill { get; set; }
        public Systems.BeamTowerSystem? BeamTower { get; set; }
        public Systems.HitShieldSystem? HitShield { get; set; }
        public Systems.TowerSabotageSystem? TowerSabotage { get; set; }
        public Systems.HeroSystem? Hero { get; set; }
        public Systems.SuicideBombSystem? SuicideBomb { get; set; }
        public Systems.ReflectTowerSystem? ReflectTower { get; set; }
        public Systems.TowerMorphSystem? TowerMorph { get; set; }
        public Systems.TowerStealthSystem? TowerStealth { get; set; }
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
            if(TowerOvercharge!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.overcharge.update"),c=>TowerOvercharge?.Update(c.Delta));
            if(Heat!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.heat.update"),c=>Heat?.Update(c.Delta));
            if(Energy!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.energy.update"),c=>Energy?.Update(c.Delta));
            if(Demolish!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.demolish.update"),c=>Demolish?.Update());
            if(HitShield!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.hit-shield.update"),c=>HitShield?.Update(c.Delta));
            if(TowerSabotage!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.sabotage.update"),c=>TowerSabotage?.Update(c.Delta));
            if(TowerStealth!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.stealth.update"),c=>TowerStealth?.Update(c.Delta));
            if(TowerSynergy!=null){s.RegisterFrameBinding(FrameBindingFacts.Get("combat.synergy.resolve-buff-shares"),c=>TowerSynergy?.ResolveBuffShares());s.RegisterFrameBinding(FrameBindingFacts.Get("combat.synergy.update"),c=>TowerSynergy?.Update());}
            if(TowerAttack!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.tower-attack.update"),c=>TowerAttack?.Update(c.Delta));
            if(TowerLink!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.link.update"),c=>TowerLink?.Update());
            if(AuraTower!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.aura.resolve"),c=>AuraTower?.ResolveAuraBuffs());
            if(TowerShrine!=null){s.RegisterFrameBinding(FrameBindingFacts.Get("combat.shrine.prepare"),c=>TowerShrine?.SetTurn());s.RegisterFrameBinding(FrameBindingFacts.Get("combat.shrine.resolve"),c=>TowerShrine?.ResolveShrineBuffs());}
            if(TowerBeacon!=null){s.RegisterFrameBinding(FrameBindingFacts.Get("combat.beacon.prepare"),c=>TowerBeacon?.SetTurn());s.RegisterFrameBinding(FrameBindingFacts.Get("combat.beacon.resolve"),c=>TowerBeacon?.ResolveBeaconBuffs());}
            if(Curse!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.curse.resolve"),c=>Curse?.ResolveCurseDebuffs());
            if(PullTower!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.pull-tower.update"),c=>PullTower?.Update(c.Delta));
            if(TowerSilence!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.silence.update"),c=>TowerSilence?.Update(c.Delta));
            if(Dispel!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.dispel.update"),c=>Dispel?.Update(c.Delta));
            if(Projectile!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.projectile.update"),c=>Projectile?.Update(c.Delta));
            if(EnemyProjectile!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.enemy-projectile.update"),c=>EnemyProjectile?.Update(c.Delta));
            if(Pickup!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.pickup.update"),c=>Pickup?.Update(c.Delta));
            if(Mana!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.mana.update"),c=>Mana?.Update(c.Delta,false));
            if(ManaShield!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.mana-shield.update"),c=>ManaShield?.Update(c.Delta));
            if(GlobalSkill!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.global-skill.update"),c=>GlobalSkill?.Update(c.Delta,false));
            if(BeamTower!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.beam.update"),c=>BeamTower?.Update(c.Delta));
            if(Hero!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.hero.update"),c=>Hero?.Update(c.Delta));
            if(SuicideBomb!=null)s.RegisterFrameBinding(FrameBindingFacts.Get("combat.suicide-bomb.update"),c=>SuicideBomb?.Update());
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

        public void Execute(ComponentStore store, float deltaTime, int turn)
        {
            PlayerTowerAttack?.Update();
            TowerOvercharge?.Update(deltaTime);
            Heat?.Update(deltaTime);
            Energy?.Update(deltaTime);
            Demolish?.Update();
            HitShield?.Update(deltaTime);
            TowerSabotage?.Update(deltaTime);
            TowerStealth?.Update(deltaTime);
            // Round 103 — Buff Share: apply attack-speed sharing bonuses from sharing towers
            // onto nearby friendly towers. Must run BEFORE TowerAttack because TowerAttack
            // reads TowerAttackSpeed for cooldown / windup math.
            TowerSynergy?.ResolveBuffShares();
            TowerAttack?.Update(deltaTime);
            TowerSynergy?.Update();
            TowerLink?.Update();
            AuraTower?.ResolveAuraBuffs();
            // Round 173 Direction 1 — Shrine aura resolve. SetTurn/Resolve pair mirrors
            //   AuraTower's pattern: SetTurn collects shrine IDs, Resolve accumulates
            //   the per-frame cache for downstream consumers.
            TowerShrine?.SetTurn();
            TowerShrine?.ResolveShrineBuffs();
            // Round 177 Direction 2 — Beacon aura resolve. SetTurn/Resolve pair mirrors
            //   Shrine's pattern: SetTurn collects beacon IDs, Resolve accumulates the
            //   per-frame damage + atk-spd cache for downstream consumers. Same wiring
            //   cost as Shrine (one SetTurn + one Resolve per frame, sentinel-gated).
            TowerBeacon?.SetTurn();
            TowerBeacon?.ResolveBeaconBuffs();
            Curse?.ResolveCurseDebuffs();
            PullTower?.Update(deltaTime);
            TowerSilence?.Update(deltaTime);
            Dispel?.Update(deltaTime);
            Projectile?.Update(deltaTime);
            EnemyProjectile?.Update(deltaTime);
            Pickup?.Update(deltaTime);
            Mana?.Update(deltaTime, isBuildPhase: false);
            // Round 175 Direction 1 — Mana Shield per-frame tick. Runs after Mana
            //   regen so it can read the just-clamped PlayerMana. Per-player system,
            //   one instance per player slot, so each frame the Combat scheduler
            //   drives at most 4 Update() calls (MAX_PLAYERS).
            ManaShield?.Update(deltaTime);
            GlobalSkill?.Update(deltaTime, isBuildPhase: false);
            BeamTower?.Update(deltaTime);
            Hero?.Update(deltaTime);
            SuicideBomb?.Update();
            ReflectTower?.ResolveReflect();
            ReflectTower?.ApplyReflectDamage();
            TowerMorph?.Update(deltaTime);
            // Taunt tower: assign EnemyTauntedByTowerId for enemies in range of any
            // TowerIsTaunt tower. Runs after tower attacks (closest semantic — enemies are
            // already locked-on to the taunt tower for the *next* frame's targeting).
            Taunt?.ResolveTauntAssignments();
            // Round 138 — Per-tower active skill cooldown tick. Runs last in the
            //   combat phase so the cooldown we tick is the one the player sees
            //   in the HUD this frame (no half-frame drift). TriggerTowerActive()
            //   is event-driven by the player; it can be called from anywhere.
            TowerActiveSkill?.Update(deltaTime);
            // Round 142 方向5 — Aggro / Focus Fire per-frame duration tick. Runs
            //   after TowerActiveSkill (also a post-attack-phase tick) and after
            //   Taunt (which writes EnemyTauntedByTowerId for the next frame's
            //   targeting). Aggro's Update() is O(1) when no focus is active.
            Aggro?.Update(deltaTime);
            // Round 144 方向4 — Hero Active Skill per-frame cooldown tick. Runs
            //   last in the combat phase so the cooldown we tick is the one the
            //   player sees in the HUD this frame (no half-frame drift). HeroSkill
            //   is O(1) when no skill is configured (sentinel _anySkillConfigured).
            HeroSkill?.Update(deltaTime);
            // Round 201 Direction 8 — Echo Clone per-frame tick. Runs last in
            // the combat phase so the spawn roll sees parent's current damage
            // (aura caches resolved above). O(1) when no echo-capable parent
            // is on the field (sentinel _hasAnyEchoCapableParent).
            EchoClone?.Update(deltaTime);
            // Round 176 Direction 2 — Bloodlust per-frame tick. Runs last in
            // the combat phase so the cached damage / speed mults are visible
            // to TowerAttackSystem on the *next* frame (the same-frame kill
            // already produced its damage this turn, so the mult lift applies
            // to the *next* shot). O(activeTowers) when Enabled = true,
            // O(activeTowers) clear-pass when Enabled = false.
            Bloodlust?.Update(turn);
            // Round174+ Direction3 — Momentum per-frame tick. Runs last in
            // the combat phase (after Bloodlust) so the freshly-computed
            // tier-derived damage / speed bonuses are visible to
            // TowerAttackSystem on the *next* frame. O(MAX_PLAYERS +
            // activeTowers) per tick. Sentinel fast path: disabled or
            // degenerate config → single O(activeTowers) clear-pass.
            Momentum?.Update(deltaTime);
            // Round 207 Direction 2 — Adrenaline per-frame tick. Runs after
            // Momentum (and before Crest / Culling) so the freshly-computed
            // tier-derived attack-speed bonus and rush-frame count are visible
            // to PlayerTowerAttackSystem on the *next* frame. O(MAX_PLAYERS) per
            // tick. Sentinel fast path: disabled or degenerate thresholds →
            // single O(MAX_PLAYERS) clear-pass.
            Adrenaline?.Update(deltaTime);
            // Round 178+ Direction 5 — Crest per-frame tick. Event-driven
            // (no work done here), so Update() is a no-op. Exposed in
            // CombatGroup so the scheduler can call it uniformly. Real
            // work happens in HandleWaveStart / HandleWaveComplete.
            Crest?.Update(deltaTime);
            // Round 206 Direction 1 — Culling per-frame tick. No-op (event-driven;
            // the per-hit hot path is invoked from TowerAttackSystem via the
            // injected CullingSystem reference). Exposed in CombatGroup so the
            // scheduler can call it uniformly.
            Culling?.Update(deltaTime);
        }
    }
}
