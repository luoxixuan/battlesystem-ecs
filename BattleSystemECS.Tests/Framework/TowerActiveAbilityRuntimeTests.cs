using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Systems;
using Xunit;
using System.IO;
using System.Reflection;

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

        [Fact]
        public void NonStrictCompatibilityCatalogIsReusedAfterWarmup()
        {
            var store = new ComponentStore();
            int tower = store.CreateEntity();
            store.AddTower(tower, TowerType.Basic, 20f, 10, 1f, 1, 20f);
            store.AddEnemy(0f, 0f, 1f, 1000f, 1000f, 1f, 1, 1);
            store.SetTowerActiveSkill(tower, 0, 3f);
            var system = new TowerActiveSkillSystem(store, new GameConfig());
            system.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            var field = typeof(TowerActiveSkillSystem).GetField("_compatibilityCatalogs",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            Assert.True(system.TriggerTowerActive(tower));
            var cache = (System.Array)field.GetValue(system)!;
            object first = cache.GetValue(tower)!;
            store.TowerActiveCooldown[tower] = 0f;
            Assert.True(system.TriggerTowerActive(tower));
            object second = cache.GetValue(tower)!;

            Assert.Same(first, second);
            store.TowerAttackDamage[tower] += 1f;
            store.TowerActiveCooldown[tower] = 0f;
            Assert.True(system.TriggerTowerActive(tower));
            object changed = cache.GetValue(tower)!;
            Assert.NotSame(second, changed);
        }

        [Fact]
        public void EnemyGroupHealUsesReusableBuffers()
        {
            string path = Path.Combine("..", "..", "..", "..", "Systems", "EnemyAbilitySystem.cs");
            string source = File.ReadAllText(path);
            Assert.Contains("readonly List<int> _healTargets", source);
            Assert.Contains("readonly List<float> _healMagnitudes", source);
            Assert.Contains("_healTargets.Clear()", source);
            Assert.DoesNotContain("var targets = new List<int>()", source);
            Assert.DoesNotContain("var magnitudes = new List<float>()", source);
        }

        [Fact]
        public void NonStrictCompatibilitySupportsNonZeroSkillId()
        {
            var store = new ComponentStore();
            int tower = store.CreateEntity();
            store.AddTower(tower, TowerType.Basic, 20f, 10, 1f, 1, 20f);
            store.AddEnemy(0f, 0f, 1f, 100f, 100f, 1f, 1, 1);
            store.SetTowerActiveSkill(tower, 7, 3f);
            var system = new TowerActiveSkillSystem(store, new GameConfig());
            system.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));

            Assert.True(system.TriggerTowerActive(tower));
            Assert.Equal(3f, store.TowerActiveCooldown[tower]);
        }
    }
}
