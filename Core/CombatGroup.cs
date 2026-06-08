#nullable enable
namespace BattleSystemECS.Core
{
    /// <summary>Main combat phase: attacks, synergy, auras, curses, projectiles, mana, skills.</summary>
    public class CombatGroup : ISystemGroup
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
            //   the combat phase so the spawn roll sees parent's current damage
            //   (aura caches resolved above). O(1) when no echo-capable parent
            //   is on the field (sentinel _hasAnyEchoCapableParent).
            EchoClone?.Update(deltaTime);
        }
    }
}
