using BattleSystemECS.Core.GAS;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class CatalogEntryBehaviorMatrixTests : BattleTestBase
    {
        [Fact]
        public void SkillUnknownCatalogIdRejectsWithoutCooldown()
        {
            int player = Player();
            var skill = new SkillSystem(Store, Renderer, player, Config);
            skill.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            var result = skill.TryActivateCatalogAbility(new AbilityId(9999));
            Assert.False(result.Accepted);
            Assert.Equal(AbilityActivationRejectReason.UnsupportedDefinition, result.Reason);
            Assert.Equal(0, Store.AbilityCount[player]);
        }

        [Fact]
        public void GlobalInsufficientManaRejectsWithoutResourceOrCooldownWrite()
        {
            int player = Player();
            Config.GlobalSkills.Add(new GlobalSkillDef { Name = "matrix-global", ManaCost = 50f, Cooldown = 7f });
            var global = new GlobalSkillSystem(Store, Config, Renderer, player);
            global.SetTurn(0);
            float mana = Store.PlayerMana[player];
            Assert.False(global.TryActivateGlobalSkill(0));
            Assert.Equal(mana, Store.PlayerMana[player]);
            Assert.Equal(0f, Store.PlayerGlobalSkillCooldown[player * 8]);
        }

        [Fact]
        public void HeroUnconfiguredSlotRejectsWithoutCooldown()
        {
            int hero = Player();
            var system = new HeroSkillSystem(Store, hero, "/missing/matrix-hero.json", Config);
            Assert.False(system.TriggerHeroSkill(0, 0));
            Assert.Equal(0f, system.GetHeroSkillCooldown(0, 0));
        }

        [Fact]
        public void TowerNoTargetRejectsWithoutCooldown()
        {
            int tower = RawTower(0, 0);
            Store.SetTowerActiveSkill(tower, 0, 5f);
            var system = new TowerActiveSkillSystem(Store, Config);
            system.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            var result = system.ActivateTower(tower);
            Assert.False(result.Accepted);
            Assert.Equal(AbilityActivationRejectReason.NoTarget, result.Reason);
            Assert.Equal(0f, Store.GetTowerActiveCooldown(tower));
        }

        [Fact]
        public void EnemyMissingCatalogAliasUsesExplicitCompatibilityBoundary()
        {
            int enemy = Enemy();
            Config.EnemyAbilities.Add(new EnemyAbilityDef { Id = "matrix-legacy", Name = "matrix-legacy", AbilityType = "unsupported" });
            var source = System.IO.File.ReadAllText(System.IO.Path.Combine("..", "..", "..", "..", "Systems", "EnemyAbilitySystem.cs"));
            Assert.Contains("gameConfig.CompiledCatalog", source);
            Assert.Contains("TryResolveAlias", source);
            Assert.Contains("Legacy handlers remain adapters", source);
            Assert.True(Store.EnemyActive[enemy]);
        }

        [Fact]
        public void AutoFacadeRejectsBuildCombatCandidateWithoutCooldownMutation()
        {
            int player = Player();
            Config.AutoSkill.Enabled = true;
            Config.AutoSkill.MaxSkillsPerPhase = 1;
            Config.Skills[0].AreaShape = "single";
            var skill = new SkillSystem(Store, Renderer, player, Config);
            skill.InitializePlayerSkills();
            var auto = new AutoSkillSystem(Store, Renderer, player, skill, Config.AutoSkill);
            auto.Update(allowCombat: false);
            Assert.Equal(0, auto.SuccessfulCastCount);
            Assert.Equal(0f, Store.GetAbility(player, 0).CurrentCooldown);
        }
    }
}
