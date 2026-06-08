#nullable enable
namespace BattleSystemECS.Core
{
    /// <summary>BuildPhase systems: economy, upgrades, auto-skills.</summary>
    public class BuildGroup : IBuildPhaseGroup
    {
        public Systems.GoldSystem? Gold { get; set; }
        public Systems.TowerIncomeSystem? TowerIncome { get; set; }
        public Systems.UpgradeSystem? Upgrade { get; set; }
        public Systems.SkillSystem? Skill { get; set; }
        public Systems.AutoSkillSystem? AutoSkill { get; set; }
        public Systems.TowerRelocateSystem? TowerRelocate { get; set; }
        public Systems.InterestSystem? Interest { get; set; }
        public Systems.ManaSystem? Mana { get; set; }
        // Round 175 Direction 1 — Mana Shield: also runs in BuildPhase so the
        //   shield can fill up while the player is preparing between waves.
        //   Per-player system (one instance per slot).
        public Systems.ManaShieldSystem? ManaShield { get; set; }
        public Systems.ObjectiveSystem? Objective { get; set; }
        public Systems.ResourceNodeSystem? ResourceNode { get; set; }
        public Systems.GlobalSkillSystem? GlobalSkill { get; set; }
        public Systems.DesperationSystem? Desperation { get; set; }
        public Systems.ShopRerollSystem? ShopReroll { get; set; }

        public void Execute(ComponentStore store, float deltaTime)
        {
            Gold?.Update();
            TowerIncome?.Update(deltaTime);
            Upgrade?.Update();
            Skill?.Update(deltaTime);
            AutoSkill?.Update();
            TowerRelocate?.Update();
            Interest?.Update();
            Mana?.Update(deltaTime, isBuildPhase: true);
            // Round 175 Direction 1 — Mana Shield BuildPhase tick (per-player).
            ManaShield?.Update(deltaTime);
            ResourceNode?.Update(deltaTime, GameState.BuildPhase);
            Objective?.Update(deltaTime, GameState.BuildPhase);
            GlobalSkill?.Update(deltaTime, isBuildPhase: true);
            Desperation?.Update();
            ShopReroll?.Update();
        }
    }
}
