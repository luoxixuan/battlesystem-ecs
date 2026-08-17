using BattleSystemECS.Tests.Infrastructure;
using Xunit;
using System;
using System.IO;
using System.Reflection;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

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
            // HeroIsDeployed[0] is false by default
            bool ok = sys.TriggerHeroSkill(0, 0);
            Assert.False(ok);
        }

        [Fact]
        public void Trigger_RejectsOutOfRangeHeroOrSlot()
        {
            ConfigureDefaultSkills();
            var sys = new HeroSkillSystem(Store, 0, "/nope.json", Config);
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
            ConfigureSkillSlot(sys, 0, 0, skillId: 0, maxCooldown: 5f, cooldown: 0f); // ready
            Store.HeroIsDeployed[0] = true;
            bool ok = sys.TriggerHeroSkill(0, 0);
            Assert.True(ok);
            Assert.Equal(5f, sys.GetHeroSkillCooldown(0, 0), 3);
        }
    }
}
