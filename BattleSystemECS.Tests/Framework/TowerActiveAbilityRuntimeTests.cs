using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Systems;
using Xunit;
using System.IO;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class TowerActiveAbilityRuntimeTests
    {
        [Fact]
        public void TowerActivationCommitsThroughRuntimeAndDamagesTarget()
        {
            var store = new ComponentStore();
            int tower = store.CreateEntity();
            store.AddTower(tower, TowerType.Basic, 20f, 10, 1f, 1, 20f);
            int enemy = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 1f, 1, 1);
            store.PositionX[tower] = 0f;
            store.PositionY[tower] = 0f;
            store.SetTowerActiveSkill(tower, 0, 3f);
            var config = new GameConfig();
            config.SkillDefs.Add(new SkillConfig { Name = "burst", DamageMultiplier = 2f });
            var system = new TowerActiveSkillSystem(store, config);
            system.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));

            float before = store.EnemyHealth[enemy];
            var result = system.ActivateTower(tower);

            Assert.True(result.Accepted);
            Assert.Equal(AbilityActivationRejectReason.None, result.Reason);
            Assert.True(store.EnemyHealth[enemy] < before);
            Assert.Equal(3f, store.TowerActiveCooldown[tower]);
        }

        [Fact]
        public void TowerActivationRejectsCooldownThroughRuntime()
        {
            var store = new ComponentStore();
            int tower = store.CreateEntity();
            store.AddTower(tower, TowerType.Basic, 20f, 10, 1f, 1, 20f);
            store.AddEnemy(0f, 0f, 1f, 100f, 100f, 1f, 1, 1);
            store.SetTowerActiveSkill(tower, 0, 3f);
            var system = new TowerActiveSkillSystem(store, new GameConfig());
            system.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));

            Assert.True(system.TriggerTowerActive(tower));
            var rejected = system.ActivateTower(tower);

            Assert.False(rejected.Accepted);
            Assert.Equal(AbilityActivationRejectReason.Cooldown, rejected.Reason);
        }

        [Fact]
        public void TowerActiveSystemUsesRuntimeAsCooldownWriter()
        {
            string path = Path.Combine("..", "..", "..", "..", "Systems", "TowerActiveSkillSystem.cs");
            string source = File.ReadAllText(path);
            Assert.Contains("GameplayAbilityRuntime.AbilityCommit", source);
            Assert.Contains("GameplayAbilityRuntime.TickCooldown", source);
            Assert.DoesNotContain("SetTowerActiveOnCooldown", source);
            Assert.DoesNotContain("TowerActiveCooldown[towerId] =", source);
        }
    }
}
