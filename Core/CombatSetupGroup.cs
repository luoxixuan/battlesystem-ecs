#nullable enable
namespace BattleSystemECS.Core
{
    /// <summary>Pre-combat SetTurn calls for all combat systems.</summary>
    public class CombatSetupGroup : ISystemGroup
    {
        public Systems.PlayerTowerAttackSystem? PlayerTowerAttack { get; set; }
        public Systems.HeroSystem? Hero { get; set; }
        public Systems.TowerAttackSystem? TowerAttack { get; set; }
        public Systems.TowerOverchargeSystem? TowerOvercharge { get; set; }
        public Systems.HeatSystem? Heat { get; set; }
        public Systems.TowerSynergySystem? TowerSynergy { get; set; }
        public Systems.TowerLinkSystem? TowerLink { get; set; }
        public Systems.SkillSystem? Skill { get; set; }
        public Systems.AuraTowerSystem? AuraTower { get; set; }
        public Systems.CurseAuraSystem? Curse { get; set; }
        public Systems.PullTowerSystem? PullTower { get; set; }
        public Systems.ManaSystem? Mana { get; set; }
        public Systems.GlobalSkillSystem? GlobalSkill { get; set; }
        public Systems.HitShieldSystem? HitShield { get; set; }
        public Systems.HotZoneSystem? HotZone { get; set; }

        public void Execute(ComponentStore store, float deltaTime, int turn)
        {
            PlayerTowerAttack?.SetTurn(turn);
            Hero?.SetTurn(turn);
            TowerAttack?.SetTurn(turn);
            TowerOvercharge?.SetTurn(turn);
            Heat?.SetTurn(turn);
            TowerSynergy?.SetTurn();
            TowerLink?.SetTurn();
            Skill?.SetTurn(turn);
            AuraTower?.SetTurn();
            Curse?.SetTurn();
            PullTower?.SetTurn();
            Mana?.SetTurn();
            GlobalSkill?.SetTurn(turn);
            HitShield?.SetTurn(turn);
            HotZone?.SetTurn(turn);
        }
    }
}
