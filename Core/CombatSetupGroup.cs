#nullable enable
namespace BattleSystemECS.Core
{
    /// <summary>Pre-combat SetTurn calls for all combat systems.</summary>
    internal sealed class CombatSetupGroup
    {
        public Systems.PlayerTowerAttackSystem? PlayerTowerAttack { get; set; }
        public Systems.HeroSystem? Hero { get; set; }
        public Systems.TowerAttackSystem? TowerAttack { get; set; }
        public Systems.TowerSynergySystem? TowerSynergy { get; set; }
        // Round 180 Direction 5 — Fortress Aura. SetTurn runs the O(N²) cluster scan
        //   and writes cached dmg/atk-spd bonuses. Must be set up in this group so
        //   the cache is fresh for TowerAttackSystem reads in the same frame.
        public Systems.TowerFortressSystem? TowerFortress { get; set; }
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
            if (TowerSynergy != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("combat-setup.synergy.prepare"), c => TowerSynergy?.SetTurn());
            if (TowerFortress != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("combat-setup.fortress.prepare"), c => TowerFortress?.SetTurn());
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

    }
}
