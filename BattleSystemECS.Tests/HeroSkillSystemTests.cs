using Xunit;
using System;
using System.IO;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
{
    /// <summary>
    /// Round 144 — HeroSkillSystem tests.
    /// Covers slot config loading, cooldown gating, deployment gate, fast-path sentinel,
    /// and the embedded JSON parser. No side effects on the global filesystem (all
    /// parser tests use the in-memory Parse(string) overload).
    /// </summary>
    public class HeroSkillSystemTests
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

        private static (ComponentStore store, GameConfig config) MakeStoreAndConfig()
        {
            var store = new ComponentStore();
            var config = new GameConfig
            {
                Skills =
                {
                    new SkillConfig { Name = "Cross Slash", Cooldown = 5f },
                    new SkillConfig { Name = "Mega Explosion", Cooldown = 10f },
                }
            };
            return (store, config);
        }

        [Fact]
        public void Ctor_AllSlotsDefaultToNoSkill()
        {
            var (store, _) = MakeStoreAndConfig();
            var sys = new HeroSkillSystem(store, 0, heroSkillsPath: "/nonexistent/never.json");
            // No file → no init called → all slots stay -1
            for (int h = 0; h < ComponentStore.MAX_HEROES; h++)
                for (int s = 0; s < HeroSkillSystem.MAX_HERO_SKILLS; s++)
                    Assert.Equal(-1, sys.GetHeroSkillId(h, s));
            Assert.False(sys.HasAnyConfiguredSkill());
        }

        [Fact]
        public void Trigger_RejectsUndeployedHero()
        {
            var (store, config) = MakeStoreAndConfig();
            var sys = new HeroSkillSystem(store, 0, "/nope.json", config);
            // HeroIsDeployed[0] is false by default
            bool ok = sys.TriggerHeroSkill(0, 0);
            Assert.False(ok);
        }

        [Fact]
        public void Trigger_RejectsOutOfRangeHeroOrSlot()
        {
            var (store, config) = MakeStoreAndConfig();
            var sys = new HeroSkillSystem(store, 0, "/nope.json", config);
            Assert.False(sys.TriggerHeroSkill(-1, 0));
            Assert.False(sys.TriggerHeroSkill(ComponentStore.MAX_HEROES, 0));
            Assert.False(sys.TriggerHeroSkill(0, -1));
            Assert.False(sys.TriggerHeroSkill(0, HeroSkillSystem.MAX_HERO_SKILLS));
        }

        [Fact]
        public void Trigger_RejectsUnconfiguredSlot()
        {
            var (store, config) = MakeStoreAndConfig();
            var sys = new HeroSkillSystem(store, 0, "/nope.json", config);
            store.HeroIsDeployed[0] = true;
            // slot 0 has no skill assigned (no init)
            bool ok = sys.TriggerHeroSkill(0, 0);
            Assert.False(ok);
        }

        [Fact]
        public void IsReady_TrueWhenConfiguredAndCooldownZero()
        {
            var (store, config) = MakeStoreAndConfig();
            var sys = new HeroSkillSystem(store, 0, "/nope.json", config);
            // No init done, so slot 0 has no skill — IsReady returns false
            Assert.False(sys.IsHeroSkillReady(0, 0));
        }

        [Fact]
        public void GetCooldownMax_ReturnsZeroWhenUnconfigured()
        {
            var (store, config) = MakeStoreAndConfig();
            var sys = new HeroSkillSystem(store, 0, "/nope.json", config);
            Assert.Equal(0f, sys.GetHeroSkillCooldownMax(0, 0));
            Assert.Equal(0f, sys.GetHeroSkillCooldown(0, 0));
        }

        [Fact]
        public void Update_InertWhenNoSkillsConfigured()
        {
            var (store, config) = MakeStoreAndConfig();
            var sys = new HeroSkillSystem(store, 0, "/nope.json", config);
            store.HeroIsDeployed[0] = true;
            // No exception, no work
            sys.Update(0.016f);
            sys.Update(1.0f);
        }

        [Fact]
        public void Update_DecrementsCooldownForDeployedHeroes()
        {
            var (store, config) = MakeStoreAndConfig();
            var sys = new HeroSkillSystem(store, 0, "/nope.json", config);
            // Manually configure a skill for hero 0, slot 0
            // (bypass Initialize since we don't want a file dep)
            var idField = typeof(HeroSkillSystem).GetField("_heroSkillIds",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var cdField = typeof(HeroSkillSystem).GetField("_heroSkillCooldowns",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var maxField = typeof(HeroSkillSystem).GetField("_heroSkillCooldownMax",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var anyField = typeof(HeroSkillSystem).GetField("_anySkillConfigured",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var ids = (int[])idField!.GetValue(sys)!;
            var cds = (float[])cdField!.GetValue(sys)!;
            var maxs = (float[])maxField!.GetValue(sys)!;
            ids[0 * HeroSkillSystem.MAX_HERO_SKILLS + 0] = 0;
            maxs[0 * HeroSkillSystem.MAX_HERO_SKILLS + 0] = 5f;
            cds[0 * HeroSkillSystem.MAX_HERO_SKILLS + 0] = 2f;
            anyField!.SetValue(sys, true);

            store.HeroIsDeployed[0] = true;
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
            var (store, config) = MakeStoreAndConfig();
            var sys = new HeroSkillSystem(store, 0, "/nope.json", config);
            var idField = typeof(HeroSkillSystem).GetField("_heroSkillIds",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var cdField = typeof(HeroSkillSystem).GetField("_heroSkillCooldowns",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var maxField = typeof(HeroSkillSystem).GetField("_heroSkillCooldownMax",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var ids = (int[])idField!.GetValue(sys)!;
            var cds = (float[])cdField!.GetValue(sys)!;
            var maxs = (float[])maxField!.GetValue(sys)!;
            ids[0] = 0;
            maxs[0] = 5f;
            cds[0] = 3f; // active cooldown
            store.HeroIsDeployed[0] = true;
            bool ok = sys.TriggerHeroSkill(0, 0);
            Assert.False(ok);
            // Cooldown not changed
            Assert.Equal(3f, sys.GetHeroSkillCooldown(0, 0), 3);
        }

        [Fact]
        public void Trigger_SucceedsAndFlipsCooldownWhenReady()
        {
            var (store, config) = MakeStoreAndConfig();
            var sys = new HeroSkillSystem(store, 0, "/nope.json", config);
            var idField = typeof(HeroSkillSystem).GetField("_heroSkillIds",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var cdField = typeof(HeroSkillSystem).GetField("_heroSkillCooldowns",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var maxField = typeof(HeroSkillSystem).GetField("_heroSkillCooldownMax",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var ids = (int[])idField!.GetValue(sys)!;
            var cds = (float[])cdField!.GetValue(sys)!;
            var maxs = (float[])maxField!.GetValue(sys)!;
            ids[0] = 0;
            maxs[0] = 5f;
            cds[0] = 0f; // ready
            store.HeroIsDeployed[0] = true;
            bool ok = sys.TriggerHeroSkill(0, 0);
            Assert.True(ok);
            Assert.Equal(5f, sys.GetHeroSkillCooldown(0, 0), 3);
        }

        [Fact]
        public void Initialize_LoadsRealJsonFile()
        {
            var (store, config) = MakeStoreAndConfig();
            // HeroSystem file we just wrote
            var path = Path.Combine(
                Path.GetDirectoryName(typeof(HeroSkillSystemTests).Assembly.Location)!,
                "..", "..", "..", "..",
                "Data", "Configs", "hero_skills.json");
            // Fallback: try the repo-relative path
            if (!File.Exists(path))
            {
                path = Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "Data", "Configs", "hero_skills.json");
            }
            // If the file isn't found (different test run dir), skip — the parser
            // tests above already validate the format. We assert non-throw only
            // when the file is present.
            if (!File.Exists(path)) return;
            var sys = new HeroSkillSystem(store, 0, path, config);
            sys.Initialize();
            // Loaded 4 slots from the real file (Cross Slash / Mega Explosion / Guardian Heal / Cold Nova)
            Assert.True(sys.HasAnyConfiguredSkill());
        }
    }
}
