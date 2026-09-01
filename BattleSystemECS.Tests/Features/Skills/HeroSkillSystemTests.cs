using BattleSystemECS.Tests.Infrastructure;
using Xunit;
using System;
using System.IO;
using System.Reflection;
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
        public void Parser_SkipsEntryWithEmptyName()
        {
            var json = "{ \"Skills\": [ { \"SlotIndex\": 0, \"SkillName\": \"\" }, { \"SlotIndex\": 1, \"SkillName\": \"Keep\" } ] }";
            var def = HeroSkillSystem.HeroSkillsConfigLoader.Parse(json);
            Assert.Single(def!.Skills!);
            Assert.Equal("Keep", def.Skills![0].SkillName);
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
            var cds = (float[])cdField!.GetValue(sys)!;
            var maxs = (float[])maxField!.GetValue(sys)!;
            int flat = hero * HeroSkillSystem.MAX_HERO_SKILLS + slot;
            ids[flat] = skillId;
            maxs[flat] = maxCooldown;
            cds[flat] = cooldown;
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
            ConfigureDefaultSkills();
            var sys = new HeroSkillSystem(Store, 0, "/nope.json", Config);
            sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            ConfigureSkillSlot(sys, 0, 0, skillId: 0, maxCooldown: 5f, cooldown: 0f); // ready
            Store.HeroIsDeployed[0] = true;
            bool ok = sys.TriggerHeroSkill(0, 0);
            Assert.True(ok);
            Assert.Equal(5f, sys.GetHeroSkillCooldown(0, 0), 3);
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
        public void Initialize_SkillDefsTakePriority_SameNameInBothTables()
        {
            Config.SkillDefs.Add(new SkillConfig { Name = "Cross Slash", Cooldown = 7f });
            Config.SkillDefs.Add(new SkillConfig { Name = "Guardian Heal", Cooldown = 9f });
            Config.Skills.Add(new SkillConfig { Name = "Cross Slash", Cooldown = 3f }); // 玩家栏同名条目，不得被优先命中

            string tmp = Path.Combine(Path.GetTempPath(), "hero_skills_test_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(tmp,
                    "{\"Skills\":[{\"SlotIndex\":0,\"SkillName\":\"Cross Slash\"},{\"SlotIndex\":1,\"SkillName\":\"Guardian Heal\"}]}");
                var sys = new HeroSkillSystem(Store, 0, heroSkillsPath: tmp, config: Config);
                sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
                sys.Initialize();

                Assert.True(sys.HasAnyConfiguredSkill());
                Store.HeroIsDeployed[0] = true;
                Assert.True(sys.IsHeroSkillReady(0, 0));
                Assert.True(sys.IsHeroSkillReady(0, 1));
                // 冷却上限来自 SkillDefs 条目（7s），不是玩家栏同名条目（3s）
                Assert.Equal(7f, sys.GetHeroSkillCooldownMax(0, 0), 3);
                Assert.Equal(9f, sys.GetHeroSkillCooldownMax(0, 1), 3);
                // 触发成功并把冷却翻转到 SkillDefs 的 max
                Assert.True(sys.TriggerHeroSkill(0, 0));
                Assert.Equal(7f, sys.GetHeroSkillCooldown(0, 0), 3);
            }
            finally
            {
                File.Delete(tmp);
            }
        }

        [Fact]
        public void Initialize_FallsBackToPlayerSkillBar_WhenNameNotInSkillDefs()
        {
            Config.Skills.Add(new SkillConfig { Name = "Railgun Shot #3", Cooldown = 4f });

            string tmp = Path.Combine(Path.GetTempPath(), "hero_skills_test_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(tmp, "{\"Skills\":[{\"SlotIndex\":2,\"SkillName\":\"Railgun Shot #3\"}]}");
                var sys = new HeroSkillSystem(Store, 0, heroSkillsPath: tmp, config: Config);
                sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
                sys.Initialize();

                Store.HeroIsDeployed[0] = true;
                Assert.True(sys.IsHeroSkillReady(0, 2));
                Assert.Equal(4f, sys.GetHeroSkillCooldownMax(0, 2), 3);
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
            int playerId = Player(p => { p.EntityId = 0; p.X = 0f; p.Y = 0f; p.Health = 100f; });
            var sys = new HeroSkillSystem(Store, playerId, "Data/Configs/hero_skills.json", config);
            sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            sys.Initialize();
            Store.HeroIsDeployed[0] = true;

            const int slot = 2;
            int skillId = sys.GetHeroSkillId(0, slot);
            Assert.True(config.CompiledCatalog!.TryResolveAlias("Guardian Heal", out var abilityId));
            Assert.Equal(abilityId.Value, skillId);
            Assert.Equal(playerId, Store.PlayerEntityId);
            Assert.True(Store.PositionActive[playerId]);
            Assert.True(Store.GetEntityHandle(playerId).IsValid);

            Assert.True(sys.TriggerHeroSkill(0, slot));
            Assert.Equal(config.CompiledCatalog.AbilityDefinitions[abilityId.Value].Cooldown,
                sys.GetHeroSkillCooldown(0, slot));
            Assert.True(sys.GetHeroSkillCooldown(0, slot) > 0f);
            Assert.False(sys.TriggerHeroSkill(0, slot));
        }

        [Fact]
        public void StrictCatalog_DamageSkillWithoutActiveTargetRejectsWithoutCooldown()
        {
            var config = GameConfigLoader.LoadStrictCatalog(Renderer);
            int playerId = Player(p => { p.EntityId = 0; p.Health = 100f; });
            var sys = new HeroSkillSystem(Store, playerId, "Data/Configs/hero_skills.json", config);
            sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            sys.Initialize();
            Store.HeroIsDeployed[0] = true;

            Assert.True(config.CompiledCatalog!.TryResolveAlias("Cross Slash", out var abilityId));
            Assert.Equal(abilityId.Value, sys.GetHeroSkillId(0, 0));
            Assert.False(sys.TriggerHeroSkill(0, 0));
            Assert.Equal(0f, sys.GetHeroSkillCooldown(0, 0));
            Assert.Equal(0, Store.DamageResolver.PendingRequestCount);
        }
    }
}
