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
        public Systems.TowerLinkSystem? TowerLink { get; set; }
        public Systems.AuraTowerSystem? AuraTower { get; set; }
        public Systems.CurseAuraSystem? Curse { get; set; }
        public Systems.PullTowerSystem? PullTower { get; set; }
        public Systems.TowerSilenceSystem? TowerSilence { get; set; }
        public Systems.TowerDispelSystem? Dispel { get; set; }
        public Systems.ProjectileSystem? Projectile { get; set; }
        public Systems.EnemyProjectileSystem? EnemyProjectile { get; set; }
        public Systems.PickupSystem? Pickup { get; set; }
        public Systems.ManaSystem? Mana { get; set; }
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
            Curse?.ResolveCurseDebuffs();
            PullTower?.Update(deltaTime);
            TowerSilence?.Update(deltaTime);
            Dispel?.Update(deltaTime);
            Projectile?.Update(deltaTime);
            EnemyProjectile?.Update(deltaTime);
            Pickup?.Update(deltaTime);
            Mana?.Update(deltaTime, isBuildPhase: false);
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
        }
    }
}
