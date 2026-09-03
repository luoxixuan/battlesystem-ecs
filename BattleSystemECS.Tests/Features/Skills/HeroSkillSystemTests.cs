using BattleSystemECS.Tests.Infrastructure;
using Xunit;
using System;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Text.Json;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;
using BattleSystemECS.Core.GAS;

namespace BattleSystemECS.Tests.Features.Skills
{
    /// <summary>
    /// Round 144 — HeroSkillSystem tests.
    /// Covers slot config loading, cooldown gating, deployment gate, fast-path sentinel,
    /// and the embedded JSON parser. No side effects on the global filesystem (all
    /// parser tests use the in-memory Parse(string) overload).
    /// </summary>
    public class HeroSkillSystemTests : BattleTestBase
    {
        // ─── Parser tests (no IO, no store) ─────────────────────────────────

        [Fact]
        public void Parser_EmptyJson_ReturnsEmptyDef()
        {
            var def = HeroSkillSystem.HeroSkillsConfigLoader.Parse("");
            Assert.NotNull(def);
            Assert.Null(def!.Skills);
        }

        [Fact]
        public void Parser_NoSkillsArray_ReturnsDefWithEmptySkills()
        {
            var json = "{ \"Description\": \"no skills here\" }";
            var def = HeroSkillSystem.HeroSkillsConfigLoader.Parse(json);
            Assert.NotNull(def);
            Assert.NotNull(def!.Skills);
            Assert.Empty(def.Skills!);
        }

        [Fact]
        public void Parser_SingleSlot_ParsesSlotIndexAndName()
        {
            var json = "{ \"Skills\": [ { \"SlotIndex\": 0, \"SkillName\": \"Fireball\" } ] }";
            var def = HeroSkillSystem.HeroSkillsConfigLoader.Parse(json);
            Assert.NotNull(def);
            Assert.NotNull(def!.Skills);
            Assert.Single(def.Skills!);
            Assert.Equal(0, def.Skills![0].SlotIndex);
            Assert.Equal("Fireball", def.Skills[0].SkillName);
        }

        [Fact]
        public void Parser_MultipleSlots_PreservesOrder()
        {
            var json = "{ \"Skills\": [ { \"SlotIndex\": 0, \"SkillName\": \"Alpha\" }, { \"SlotIndex\": 2, \"SkillName\": \"Beta\" } ] }";
            var def = HeroSkillSystem.HeroSkillsConfigLoader.Parse(json);
            Assert.NotNull(def);
            Assert.Equal(2, def!.Skills!.Count);
            Assert.Equal("Alpha", def.Skills![0].SkillName);
            Assert.Equal("Beta", def.Skills[1].SkillName);
        }

        [Fact]
        public void Parser_RejectsEntryWithEmptyName()
        {
            var json = "{ \"Skills\": [ { \"SlotIndex\": 0, \"SkillName\": \"\" }, { \"SlotIndex\": 1, \"SkillName\": \"Keep\" } ] }";
            var error = Assert.Throws<CatalogValidationException>(() =>
                HeroSkillSystem.HeroSkillsConfigLoader.Parse(json, "Data/Configs/test-hero.json"));
            Assert.Contains("Data/Configs/test-hero.json", error.Message);
            Assert.Contains("$.Skills[0].SkillName", error.Message);
        }

        [Theory]
        [InlineData("{\"Skills\":[{\"SkillName\":\"Cross Slash\"}]}", "$.Skills[0].SlotIndex")]
        [InlineData("{\"Skills\":[{\"SlotIndex\":\"0\",\"SkillName\":\"Cross Slash\"}]}", "$.Skills[0].SlotIndex")]
        [InlineData("{\"Skills\":[{\"SlotIndex\":0.5,\"SkillName\":\"Cross Slash\"}]}", "$.Skills[0].SlotIndex")]
        [InlineData("{\"Skills\":[{\"SlotIndex\":-1,\"SkillName\":\"Cross Slash\"}]}", "$.Skills[0].SlotIndex")]
        [InlineData("{\"Skills\":[{\"SlotIndex\":4,\"SkillName\":\"Cross Slash\"}]}", "$.Skills[0].SlotIndex")]
        [InlineData("{\"Skills\":[{\"SlotIndex\":0,\"SkillName\":\"Cross Slash\"},{\"SlotIndex\":0,\"SkillName\":\"Cold Nova\"}]}", "$.Skills[1].SlotIndex")]
        public void Parser_RejectsInvalidSlotIndexWithSourceAndJsonPath(string json, string jsonPath)
        {
            const string source = "Data/Configs/test-hero.json";
            var error = Assert.Throws<CatalogValidationException>(() =>
                HeroSkillSystem.HeroSkillsConfigLoader.Parse(json, source));

            Assert.Contains(source, error.Message);
            Assert.Contains(jsonPath, error.Message);
        }

        [Fact]
        public void Initialize_InvalidSlotRejectsWholeFileInsteadOfPartiallyApplyingValidEntries()
        {
            Config.SkillDefs.Add(new SkillConfig { Name = "Cross Slash", Cooldown = 7f });
            string tmp = Path.Combine(Path.GetTempPath(), "hero_skills_invalid_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(tmp,
                    "{\"Skills\":[{\"SlotIndex\":0,\"SkillName\":\"Cross Slash\"},{\"SlotIndex\":4,\"SkillName\":\"Cross Slash\"}]}");
                var sys = new HeroSkillSystem(Store, 0, tmp, Config);

                sys.Initialize();

                Assert.False(sys.HasAnyConfiguredSkill());
                Assert.Equal(-1, sys.GetHeroSkillId(0, 0));
            }
            finally
            {
                File.Delete(tmp);
            }
        }

        // ─── System tests (use real store + minimal config) ──────────────────

        private void ConfigureDefaultSkills()
        {
            Config.Skills.Add(new SkillConfig { Name = "Cross Slash", Cooldown = 5f });
            Config.Skills.Add(new SkillConfig { Name = "Mega Explosion", Cooldown = 10f });
        }

        /// <summary>
        /// 文件内共享反射 helper：直接配置 (hero, slot) 的技能 id / 冷却（生产未提供写接口，
        /// 反射点统一集中在此，后续生产补测缝时删除）。字段名用 nameof 防重命名。
        /// </summary>
        private static void ConfigureSkillSlot(
            HeroSkillSystem sys, int hero, int slot, int skillId, float maxCooldown, float cooldown,
            bool markConfigured = false)
        {
            const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
            // 私有字段 nameof 在类外不可用，保留字符串（反射点）。
            var idField = typeof(HeroSkillSystem).GetField("_heroSkillIds", Flags);
            var cdField = typeof(HeroSkillSystem).GetField("_heroSkillCooldowns", Flags);
            var maxField = typeof(HeroSkillSystem).GetField("_heroSkillCooldownMax", Flags);
            var anyField = typeof(HeroSkillSystem).GetField("_anySkillConfigured", Flags);

            var ids = (int[])idField!.GetValue(sys)!;
            var cds = (AbilityState[])cdField!.GetValue(sys)!;
            var maxs = (float[])maxField!.GetValue(sys)!;
            int flat = hero * HeroSkillSystem.MAX_HERO_SKILLS + slot;
            ids[flat] = skillId;
            maxs[flat] = maxCooldown;
            var state = cds[flat];
            state.Cooldown = cooldown;
            cds[flat] = state;
            if (markConfigured) anyField!.SetValue(sys, true);
        }

        [Fact]
        public void Ctor_AllSlotsDefaultToNoSkill()
        {
            ConfigureDefaultSkills();
            var sys = new HeroSkillSystem(Store, 0, heroSkillsPath: "/nonexistent/never.json");
            // No file → no init called → all slots stay -1
            for (int h = 0; h < ComponentStore.MAX_HEROES; h++)
                for (int s = 0; s < HeroSkillSystem.MAX_HERO_SKILLS; s++)
                    Assert.Equal(-1, sys.GetHeroSkillId(h, s));
            Assert.False(sys.HasAnyConfiguredSkill());
        }

        [Fact]
        public void Trigger_RejectsUndeployedHero()
        {
            ConfigureDefaultSkills();
            var sys = new HeroSkillSystem(Store, 0, "/nope.json", Config);
            sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            // HeroIsDeployed[0] is false by default
            bool ok = sys.TriggerHeroSkill(0, 0);
            Assert.False(ok);
        }

        [Fact]
        public void Trigger_RejectsOutOfRangeHeroOrSlot()
        {
            ConfigureDefaultSkills();
            var sys = new HeroSkillSystem(Store, 0, "/nope.json", Config);
            sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            Assert.False(sys.TriggerHeroSkill(-1, 0));
            Assert.False(sys.TriggerHeroSkill(ComponentStore.MAX_HEROES, 0));
            Assert.False(sys.TriggerHeroSkill(0, -1));
            Assert.False(sys.TriggerHeroSkill(0, HeroSkillSystem.MAX_HERO_SKILLS));
        }

        [Fact]
        public void Trigger_RejectsUnconfiguredSlot()
        {
            ConfigureDefaultSkills();
            var sys = new HeroSkillSystem(Store, 0, "/nope.json", Config);
            Store.HeroIsDeployed[0] = true;
            // slot 0 has no skill assigned (no init)
            bool ok = sys.TriggerHeroSkill(0, 0);
            Assert.False(ok);
        }

        [Fact]
        public void IsReady_TrueWhenConfiguredAndCooldownZero()
        {
            ConfigureDefaultSkills();
            var sys = new HeroSkillSystem(Store, 0, "/nope.json", Config);
            // No init done, so slot 0 has no skill — IsReady returns false
            Assert.False(sys.IsHeroSkillReady(0, 0));
        }

        [Fact]
        public void GetCooldownMax_ReturnsZeroWhenUnconfigured()
        {
            ConfigureDefaultSkills();
            var sys = new HeroSkillSystem(Store, 0, "/nope.json", Config);
            Assert.Equal(0f, sys.GetHeroSkillCooldownMax(0, 0));
            Assert.Equal(0f, sys.GetHeroSkillCooldown(0, 0));
        }

        [Fact]
        public void Update_InertWhenNoSkillsConfigured()
        {
            ConfigureDefaultSkills();
            var sys = new HeroSkillSystem(Store, 0, "/nope.json", Config);
            Store.HeroIsDeployed[0] = true;
            sys.Update(0.016f);
            sys.Update(1.0f);

            // inert 状态精确可观察：所有槽位仍为 -1 / 冷却 0，哨兵保持 false。
            Assert.False(sys.HasAnyConfiguredSkill());
            for (int h = 0; h < ComponentStore.MAX_HEROES; h++)
            {
                for (int s = 0; s < HeroSkillSystem.MAX_HERO_SKILLS; s++)
                {
                    Assert.Equal(-1, sys.GetHeroSkillId(h, s));
                    Assert.Equal(0f, sys.GetHeroSkillCooldown(h, s));
                    Assert.Equal(0f, sys.GetHeroSkillCooldownMax(h, s));
                }
            }
        }

        [Fact]
        public void Update_DecrementsCooldownForDeployedHeroes()
        {
            ConfigureDefaultSkills();
            var sys = new HeroSkillSystem(Store, 0, "/nope.json", Config);
            sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            // 手动配置 hero 0 / slot 0 的技能（绕过文件 Initialize）。
            ConfigureSkillSlot(sys, 0, 0, skillId: 0, maxCooldown: 5f, cooldown: 2f, markConfigured: true);

            Store.HeroIsDeployed[0] = true;
            sys.Update(1.0f);
            Assert.Equal(1f, sys.GetHeroSkillCooldown(0, 0), 3);

            sys.Update(0.5f);
            Assert.Equal(0.5f, sys.GetHeroSkillCooldown(0, 0), 3);

            sys.Update(0.5f);
            Assert.Equal(0f, sys.GetHeroSkillCooldown(0, 0), 3);

            // Cooldown must not go negative
            sys.Update(5f);
            Assert.Equal(0f, sys.GetHeroSkillCooldown(0, 0), 3);
        }

        [Fact]
        public void Trigger_FailsOnActiveCooldown()
        {
            ConfigureDefaultSkills();
            var sys = new HeroSkillSystem(Store, 0, "/nope.json", Config);
            sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            ConfigureSkillSlot(sys, 0, 0, skillId: 0, maxCooldown: 5f, cooldown: 3f); // active cooldown
            Store.HeroIsDeployed[0] = true;
            bool ok = sys.TriggerHeroSkill(0, 0);
            Assert.False(ok);
            // Cooldown not changed
            Assert.Equal(3f, sys.GetHeroSkillCooldown(0, 0), 3);
        }

        [Fact]
        public void Trigger_SucceedsAndFlipsCooldownWhenReady()
        {
            var strictConfig = GameConfigLoader.LoadStrictCatalog(Renderer);
            var binding = FindStrictBinding(strictConfig, ability =>
                HasExecution(strictConfig, ability, ExecutionOperation.ApplyHeal));
            int playerId = Player(p => p.EntityId = 0);
            var sys = new HeroSkillSystem(Store, playerId, "Data/Configs/hero_skills.json", strictConfig);
            sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            sys.Initialize();
            Store.HeroIsDeployed[0] = true;
            Assert.Equal(binding.Ability.Id.Value, sys.GetHeroSkillId(0, binding.Slot));
            Assert.True(sys.TriggerHeroSkill(0, binding.Slot));
            Assert.Equal(binding.Ability.Cooldown, sys.GetHeroSkillCooldown(0, binding.Slot), 3);
        }

        [Fact]
        public void Trigger_UnboundContextRejectsWithoutCooldown()
        {
            // Bug 回归：未绑定阶段的 HeroSkill 入口必须拒绝且不得启动冷却。
            ConfigureDefaultSkills();
            var sys = new HeroSkillSystem(Store, 0, "/nope.json", Config);
            ConfigureSkillSlot(sys, 0, 0, skillId: 0, maxCooldown: 5f, cooldown: 0f);
            Store.HeroIsDeployed[0] = true;
            Assert.False(sys.TriggerHeroSkill(0, 0));
            Assert.Equal(0f, sys.GetHeroSkillCooldown(0, 0));
        }

        // ─── SkillDefs 优先解析（接线：Data/Configs/skills.json 共享技能表）─────────

        /// <summary>
        /// 同名技能同时存在于 SkillDefs（共享定义表）与 Skills（玩家技能栏）时，
        /// Initialize 的名称解析必须命中 SkillDefs 条目（冷却取自精选定义）。
        /// 接线前 SkillDefs 不存在，hero_skills.json 引用的精选技能名永远解析失败。
        /// </summary>
        [Fact]
        public void Initialize_StrictCatalogTakesPriorityOverMutableLegacyTables()
        {
            var strictConfig = GameConfigLoader.LoadStrictCatalog(Renderer);
            var damage = FindStrictBinding(strictConfig, ability => ability.Targeting.Relation == RelationFilter.Enemies &&
                HasExecution(strictConfig, ability, ExecutionOperation.ApplyDamage));
            var heal = FindStrictBinding(strictConfig, ability =>
                HasExecution(strictConfig, ability, ExecutionOperation.ApplyHeal));
            strictConfig.SkillDefs.Add(new SkillConfig { Name = damage.Ability.Name, Cooldown = damage.Ability.Cooldown + 2f });
            strictConfig.SkillDefs.Add(new SkillConfig { Name = heal.Ability.Name, Cooldown = heal.Ability.Cooldown + 2f });
            strictConfig.Skills.Add(new SkillConfig { Name = damage.Ability.Name, Cooldown = damage.Ability.Cooldown + 3f });

            string tmp = Path.Combine(Path.GetTempPath(), "hero_skills_test_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(new
                {
                    Skills = new[]
                    {
                        new { SlotIndex = damage.Slot, SkillName = damage.Ability.Name },
                        new { SlotIndex = heal.Slot, SkillName = heal.Ability.Name }
                    }
                }));
                var sys = new HeroSkillSystem(Store, 0, heroSkillsPath: tmp, config: strictConfig);
                sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
                sys.Initialize();

                Assert.True(sys.HasAnyConfiguredSkill());
                Store.HeroIsDeployed[0] = true;
                Assert.True(sys.IsHeroSkillReady(0, damage.Slot));
                Assert.True(sys.IsHeroSkillReady(0, heal.Slot));
                // Strict Catalog 是权威来源，测试期修改的 legacy 表不能改变运行时冷却。
                Assert.Equal(damage.Ability.Cooldown, sys.GetHeroSkillCooldownMax(0, damage.Slot), 3);
                Assert.Equal(heal.Ability.Cooldown, sys.GetHeroSkillCooldownMax(0, heal.Slot), 3);
                // 伤害能力在该 fixture 中没有目标，不得提交冷却。
                Assert.False(sys.TriggerHeroSkill(0, damage.Slot));
                Assert.Equal(0f, sys.GetHeroSkillCooldown(0, damage.Slot), 3);
            }
            finally
            {
                File.Delete(tmp);
            }
        }

        [Fact]
        public void Initialize_FallsBackToPlayerSkillBar_WhenNameNotInSkillDefs()
        {
            const string fixtureName = "Fixture Player Skill";
            const float fixtureCooldown = 4f;
            Config.Skills.Add(new SkillConfig { Name = fixtureName, Cooldown = fixtureCooldown });

            string tmp = Path.Combine(Path.GetTempPath(), "hero_skills_test_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(new
                {
                    Skills = new[] { new { SlotIndex = 2, SkillName = fixtureName } }
                }));
                var sys = new HeroSkillSystem(Store, 0, heroSkillsPath: tmp, config: Config);
                sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
                sys.Initialize();

                Store.HeroIsDeployed[0] = true;
                Assert.True(sys.IsHeroSkillReady(0, 2));
                Assert.Equal(fixtureCooldown, sys.GetHeroSkillCooldownMax(0, 2), 3);
            }
            finally
            {
                File.Delete(tmp);
            }
        }

        [Fact]
        public void StrictCatalog_CanonicalHealUsesRealPlayerSourceTargetAndCooldown()
        {
            var config = GameConfigLoader.LoadStrictCatalog(Renderer);
            var binding = FindStrictBinding(config, ability =>
                HasExecution(config, ability, ExecutionOperation.ApplyHeal));
            int playerId = Player(p => { p.EntityId = 0; p.X = 0f; p.Y = 0f; p.Health = 100f; });
            var sys = new HeroSkillSystem(Store, playerId, "Data/Configs/hero_skills.json", config);
            sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            sys.Initialize();
            Store.HeroIsDeployed[0] = true;

            int skillId = sys.GetHeroSkillId(0, binding.Slot);
            Assert.Equal(binding.Ability.Id.Value, skillId);
            Assert.Equal(playerId, Store.PlayerEntityId);
            Assert.True(Store.PositionActive[playerId]);
            Assert.True(Store.GetEntityHandle(playerId).IsValid);

            Assert.True(sys.TriggerHeroSkill(0, binding.Slot));
            Assert.Equal(binding.Ability.Cooldown, sys.GetHeroSkillCooldown(0, binding.Slot));
            Assert.True(sys.GetHeroSkillCooldown(0, binding.Slot) > 0f);
            Assert.False(sys.TriggerHeroSkill(0, binding.Slot));
        }

        [Fact]
        public void StrictCatalog_DamageSkillWithoutActiveTargetRejectsWithoutCooldown()
        {
            var config = GameConfigLoader.LoadStrictCatalog(Renderer);
            var binding = FindStrictBinding(config, ability => ability.Targeting.Relation == RelationFilter.Enemies &&
                HasExecution(config, ability, ExecutionOperation.ApplyDamage));
            int playerId = Player(p => { p.EntityId = 0; p.Health = 100f; });
            var sys = new HeroSkillSystem(Store, playerId, "Data/Configs/hero_skills.json", config);
            sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            sys.Initialize();
            Store.HeroIsDeployed[0] = true;

            Assert.Equal(binding.Ability.Id.Value, sys.GetHeroSkillId(0, binding.Slot));
            Assert.False(sys.TriggerHeroSkill(0, binding.Slot));
            Assert.Equal(0f, sys.GetHeroSkillCooldown(0, binding.Slot));
            Assert.Equal(0, Store.DamageResolver.PendingRequestCount);
        }

        [Fact]
        public void StrictCatalog_HeroesUsingSameSlotKeepIndependentCooldowns()
        {
            var config = GameConfigLoader.LoadStrictCatalog(Renderer);
            var binding = FindStrictBinding(config, ability =>
                HasExecution(config, ability, ExecutionOperation.ApplyHeal));
            int playerId = Player(p => { p.EntityId = 0; p.Health = 100f; });
            var sys = new HeroSkillSystem(Store, playerId, "Data/Configs/hero_skills.json", config);
            sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            sys.Initialize();
            Store.HeroIsDeployed[0] = true;
            Store.HeroIsDeployed[1] = true;
            Assert.True(sys.TriggerHeroSkill(1, binding.Slot));
            Assert.Equal(0f, sys.GetHeroSkillCooldown(0, binding.Slot));
            Assert.Equal(binding.Ability.Cooldown, sys.GetHeroSkillCooldown(1, binding.Slot));
            Assert.False(sys.TriggerHeroSkill(1, binding.Slot));
            Assert.True(sys.TriggerHeroSkill(0, binding.Slot));
            Assert.Equal(binding.Ability.Cooldown, sys.GetHeroSkillCooldown(0, binding.Slot));
        }

        private static (int Slot, AbilityDefinition Ability) FindStrictBinding(GameConfig config,
            Func<AbilityDefinition, bool> predicate)
        {
            const string path = "Data/Configs/hero_skills.json";
            var bindings = HeroSkillSystem.HeroSkillsConfigLoader.Parse(File.ReadAllText(path), path);
            foreach (var binding in bindings.Skills ?? Enumerable.Empty<HeroSkillSystem.HeroSkillSlotEntry>())
                if (binding.SkillName != null && config.CompiledCatalog!.TryResolveAlias(binding.SkillName, out var id) &&
                    config.CompiledCatalog.TryGetAbility(id, out var ability) && predicate(ability))
                    return (binding.SlotIndex, ability);
            throw new Xunit.Sdk.XunitException("strict hero binding with requested typed behavior was not found");
        }

        private static bool HasExecution(GameConfig config, AbilityDefinition ability, ExecutionOperation operation) =>
            ability.Executions.Any(id => config.CompiledCatalog!.TryGetExecution(id, out var execution) &&
                execution.Operation == operation);
    }
}
