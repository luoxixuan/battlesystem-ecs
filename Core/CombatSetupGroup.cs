#nullable enable
namespace BattleSystemECS.Core
{
    /// <summary>Pre-combat SetTurn calls for all combat systems.</summary>
    internal sealed class CombatSetupGroup : ISystemGroup
    {
        public Systems.PlayerTowerAttackSystem? PlayerTowerAttack { get; set; }
        public Systems.HeroSystem? Hero { get; set; }
        public Systems.TowerAttackSystem? TowerAttack { get; set; }
        public Systems.TowerOverchargeSystem? TowerOvercharge { get; set; }
        public Systems.HeatSystem? Heat { get; set; }
        public Systems.TowerSynergySystem? TowerSynergy { get; set; }
        // Round 180 Direction 5 — Fortress Aura. SetTurn runs the O(N²) cluster scan
        //   and writes cached dmg/atk-spd bonuses. Must be set up in this group so
        //   the cache is fresh for TowerAttackSystem reads in the same frame.
        public Systems.TowerFortressSystem? TowerFortress { get; set; }
        public Systems.TowerLinkSystem? TowerLink { get; set; }
        public Systems.SkillSystem? Skill { get; set; }
        public Systems.AuraTowerSystem? AuraTower { get; set; }
        public Systems.CurseAuraSystem? Curse { get; set; }
        public Systems.PullTowerSystem? PullTower { get; set; }
        public Systems.ManaSystem? Mana { get; set; }
        public Systems.GlobalSkillSystem? GlobalSkill { get; set; }
        public Systems.HitShieldSystem? HitShield { get; set; }
        public Systems.HotZoneSystem? HotZone { get; set; }
        public Systems.FrostZoneSystem? FrostZone { get; set; }
        // Round 200 / Direction 2 — Elemental Terrain Zone (player-spawned per-element ground effects).
        public Systems.TerrainZoneSystem? TerrainZone { get; set; }
        public Systems.WanderRoamSystem? WanderRoam { get; set; }
        public Systems.TauntSystem? Taunt { get; set; }

        internal void RegisterFrameBindings(FrameScheduler scheduler)
        {
            if (PlayerTowerAttack != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("combat-setup.player-attack.prepare"), c => PlayerTowerAttack?.SetTurn(c.Turn));
            if (Hero != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("combat-setup.hero.prepare"), c => Hero?.SetTurn(c.Turn));
            if (TowerAttack != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("combat-setup.tower-attack.prepare"), c => TowerAttack?.SetTurn(c.Turn));
            if (TowerOvercharge != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("combat-setup.overcharge.prepare"), c => TowerOvercharge?.SetTurn(c.Turn));
            if (Heat != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("combat-setup.heat.prepare"), c => Heat?.SetTurn(c.Turn));
            if (TowerSynergy != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("combat-setup.synergy.prepare"), c => TowerSynergy?.SetTurn());
            if (TowerFortress != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("combat-setup.fortress.prepare"), c => TowerFortress?.SetTurn());
            if (TowerLink != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("combat-setup.link.prepare"), c => TowerLink?.SetTurn());
            if (Skill != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("combat-setup.skill.prepare"), c => Skill?.SetTurn(c.Turn));
            if (AuraTower != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("combat-setup.aura.prepare"), c => AuraTower?.SetTurn());
            if (Curse != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("combat-setup.curse.prepare"), c => Curse?.SetTurn());
            if (PullTower != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("combat-setup.pull-tower.prepare"), c => PullTower?.SetTurn());
            if (Mana != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("combat-setup.mana.prepare"), c => Mana?.SetTurn());
            if (GlobalSkill != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("combat-setup.global-skill.prepare"), c => GlobalSkill?.SetTurn(c.Turn));
            if (HitShield != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("combat-setup.hit-shield.prepare"), c => HitShield?.SetTurn(c.Turn));
            if (HotZone != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("combat-setup.hot-zone.prepare"), c => HotZone?.SetTurn(c.Turn));
            if (FrostZone != null) { scheduler.RegisterFrameBinding(FrameBindingFacts.Get("combat-setup.frost-zone.prepare"), c => FrostZone?.SetTurn(c.Turn)); scheduler.RegisterFrameBinding(FrameBindingFacts.Get("combat-setup.frost-zone.update"), c => FrostZone?.Update()); }
            if (TerrainZone != null) { scheduler.RegisterFrameBinding(FrameBindingFacts.Get("combat-setup.terrain-zone.prepare"), c => TerrainZone?.SetTurn(c.Turn)); scheduler.RegisterFrameBinding(FrameBindingFacts.Get("combat-setup.terrain-zone.update"), c => TerrainZone?.Update(c.Delta)); }
            if (WanderRoam != null) { scheduler.RegisterFrameBinding(FrameBindingFacts.Get("combat-setup.wander.prepare"), c => WanderRoam?.SetTurn(c.Turn)); scheduler.RegisterFrameBinding(FrameBindingFacts.Get("combat-setup.wander.update"), c => WanderRoam?.Update()); }
            if (Taunt != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("combat-setup.taunt.prepare"), c => Taunt?.SetTurn());
        }

        public void Execute(ComponentStore store, float deltaTime, int turn)
        {
            PlayerTowerAttack?.SetTurn(turn);
            Hero?.SetTurn(turn);
            TowerAttack?.SetTurn(turn);
            TowerOvercharge?.SetTurn(turn);
            Heat?.SetTurn(turn);
            TowerSynergy?.SetTurn();
            // Round 180 Direction 5 — Fortress cluster scan. Runs after Synergy
            //   (independent; both only read ActiveTowerIds) so the cached bonuses
            //   are visible to TowerAttackSystem the same frame.
            TowerFortress?.SetTurn();
            TowerLink?.SetTurn();
            Skill?.SetTurn(turn);
            AuraTower?.SetTurn();
            Curse?.SetTurn();
            PullTower?.SetTurn();
            Mana?.SetTurn();
            GlobalSkill?.SetTurn(turn);
            HitShield?.SetTurn(turn);
            HotZone?.SetTurn(turn);
            // Frost Zone: writes EnemyFrostZoneSlowMultiplier per enemy for this frame.
            // Runs after HotZone (placement pre-computed) and before Taunt (independent).
            FrostZone?.SetTurn(turn);
            FrostZone?.Update();
            // Round 200 Direction 2 — Elemental Terrain Zone: per-frame tick (lifetime decay +
            // radius expansion + DoT/slow application). Sentinel-gated fast path when no zone is
            // active. Runs after Frost Zone (independent — both write per-enemy slow arrays).
            TerrainZone?.SetTurn(turn);
            TerrainZone?.Update(deltaTime);
            // Wander Roam (Round 84 Direction 6): resolves per-free-roam-enemy target
            // cell. Writes EnemyWanderTargetX/Y which EnemyMovementSystem reads in its
            // Wandering branch. Must run BEFORE Movement (which happens in a later phase),
            // not before Combat. Cost: O(N_active_enemies) with O(1) fast-exit when
            // no free-roam enemies exist.
            WanderRoam?.SetTurn(turn);
            WanderRoam?.Update();
            // Taunt tower: pre-compute taunt tower list (O(n_active_towers)) before Combat
            // assigns EnemyTauntedByTowerId. Cheap when no taunt towers exist.
            Taunt?.SetTurn();
        }
    }
}
