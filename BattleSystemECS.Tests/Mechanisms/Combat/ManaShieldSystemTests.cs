using BattleSystemECS.Tests.Infrastructure;
using System;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Mechanisms.Combat
{
    /// <summary>
    /// Tests for Round 175 Direction 1: Mana Shield (mana → damage-absorption shield).
    /// Verifies that:
    ///   - Default state: all mana-shield fields are 0/false (backward compat)
    ///   - Mana above trigger threshold converts into shield up to the cap
    ///   - Mana below threshold leaks shield at DecayPerSecond
    ///   - Damage hot-path: shield absorbs damage before PlayerShield / PlayerCurrentHealth
    ///   - Disabled config: absorb ratio forced to 0 (cheap damage-path)
    ///   - AbsorbRatio > 1.0 doubles shield efficiency
    ///   - Cap is recomputed from PlayerMaxMana each frame
    ///   - Shield is clamped to [0, cap] after gain+decay
    ///   - AddPlayer resets all mana-shield fields
    /// </summary>
    public class ManaShieldSystemTests : BattleTestBase
    {
        private const int PlayerId = 0;
        private const float PlayerMaxManaDefault = 100f;

        private GameConfig MakeConfig(bool enabled = true, float ratio = 1f, float maxPct = 0.5f, float decay = 5f, float triggerPct = 0.7f)
        {
            Config.ManaShield = new ManaShieldConfig
            {
                Enabled = enabled,
                ConversionRatio = ratio,
                MaxShieldPercent = maxPct,
                DecayPerSecond = decay,
                TriggerThresholdPercent = triggerPct
            };
            Config.Mana = new ManaConfig
            {
                BaseMana = 0f,
                MaxManaBase = PlayerMaxManaDefault,
                ManaRegenPerSec = 0f, // Tests control mana directly
                ManaRegenBuildPhase = 0f,
                ManaCostMultiplier = 1f
            };
            return Config;
        }

        private void MakeStoreWithPlayer(GameConfig cfg, float currentMana = 0f, float currentShield = 0f)
        {
            Store.AddPlayer(PlayerId, 5f, 1f, 10f, 1);
            Store.PlayerMaxMana[PlayerId] = cfg.Mana.MaxManaBase;
            Store.PlayerMana[PlayerId] = currentMana;
            Store.PlayerCurrentHealth[PlayerId] = 1000f;
            Store.PlayerMaxHealth[PlayerId] = 1000f;
            Store.PlayerManaShield[PlayerId] = currentShield;
        }

        // ─── Default state (backward compat) ──────────────────────────────

        [Fact]
        public void DefaultState_NewComponentStore_AllManaShieldFieldsZero()
        {
            Assert.Equal(0f, Store.PlayerManaShield[PlayerId]);
            Assert.Equal(0f, Store.PlayerManaShieldCap[PlayerId]);
            Assert.Equal(0f, Store.PlayerManaShieldAbsorbRatio[PlayerId]);
            Assert.False(Store.PlayerManaShieldTriggered[PlayerId]);
        }

        [Fact]
        public void AddPlayer_InitializesManaShieldFields()
        {
            Store.AddPlayer(PlayerId, 5f, 1f, 10f, 1);
            Assert.Equal(0f, Store.PlayerManaShield[PlayerId]);
            Assert.Equal(0f, Store.PlayerManaShieldCap[PlayerId]);
            Assert.Equal(1f, Store.PlayerManaShieldAbsorbRatio[PlayerId]); // baseline
            Assert.False(Store.PlayerManaShieldTriggered[PlayerId]);
        }

        [Fact]
        public void ManaShieldConfig_HasSensibleDefaults()
        {
            var cfg = new ManaShieldConfig();
            Assert.True(cfg.Enabled);
            Assert.True(cfg.ConversionRatio > 0f);
            Assert.True(cfg.MaxShieldPercent > 0f && cfg.MaxShieldPercent <= 1f);
            Assert.True(cfg.DecayPerSecond >= 0f);
            Assert.True(cfg.TriggerThresholdPercent >= 0f && cfg.TriggerThresholdPercent <= 1f);
        }

        // ─── Mana → shield conversion ────────────────────────────────────

        [Fact]
        public void ManaAboveThreshold_ConvertsIntoShield()
        {
            var cfg = MakeConfig();
            MakeStoreWithPlayer(cfg, currentMana: 100f); // 100% > 70% threshold
            var sys = new ManaShieldSystem(Store, cfg, PlayerId);
            sys.Initialize();

            sys.Update(0.1f);

            // Cap = 100 * 0.5 = 50；首帧转换 excess=30（ratio=1），精确写入 30 并消耗 30 蓝。
            Assert.Equal(50f, Store.PlayerManaShieldCap[PlayerId], 2);
            Assert.Equal(30f, Store.PlayerManaShield[PlayerId], 2);
            Assert.Equal(70f, Store.PlayerMana[PlayerId], 2);
        }

        [Fact]
        public void ManaAboveThreshold_ShieldClampedToCap()
        {
            var cfg = MakeConfig();
            MakeStoreWithPlayer(cfg, currentMana: 100f);
            var sys = new ManaShieldSystem(Store, cfg, PlayerId);
            sys.Initialize();

            // 首帧转 30 后蓝量落到阈值 70，之后 9 帧每帧衰减 5*0.5=2.5。
            for (int i = 0; i < 10; i++) sys.Update(0.5f);

            float cap = Store.PlayerManaShieldCap[PlayerId];
            Assert.Equal(50f, cap, 2);
            Assert.Equal(7.5f, Store.PlayerManaShield[PlayerId], 2);
        }

        [Fact]
        public void ManaBelowThreshold_DecaysShield()
        {
            var cfg = MakeConfig(decay: 10f);
            MakeStoreWithPlayer(cfg, currentMana: 10f, currentShield: 30f); // below 70% threshold
            var sys = new ManaShieldSystem(Store, cfg, PlayerId);
            Store.PlayerManaShieldCap[PlayerId] = 50f;

            sys.Update(1f);

            // 30 - 10*1 = 20
            Assert.Equal(20f, Store.PlayerManaShield[PlayerId], 1);
        }

        [Fact]
        public void ManaBelowThreshold_DecayStopsAtZero()
        {
            var cfg = MakeConfig(decay: 100f);
            MakeStoreWithPlayer(cfg, currentMana: 0f, currentShield: 5f);
            var sys = new ManaShieldSystem(Store, cfg, PlayerId);
            Store.PlayerManaShieldCap[PlayerId] = 50f;

            sys.Update(1f);

            Assert.Equal(0f, Store.PlayerManaShield[PlayerId]);
        }

        [Fact]
        public void Cap_RecomputedFromMaxManaEachFrame()
        {
            var cfg = MakeConfig(maxPct: 0.5f);
            MakeStoreWithPlayer(cfg, currentMana: 100f);
            var sys = new ManaShieldSystem(Store, cfg, PlayerId);
            sys.Initialize();

            sys.Update(0.01f);
            float cap1 = Store.PlayerManaShieldCap[PlayerId];
            Assert.Equal(50f, cap1, 1);

            // Bump max-mana mid-game (tech tree upgrade simulation)
            Store.PlayerMaxMana[PlayerId] = 200f;
            sys.Update(0.01f);
            float cap2 = Store.PlayerManaShieldCap[PlayerId];
            Assert.Equal(100f, cap2, 1);
        }

        // ─── Damage hot-path: shield absorbs before health ────────────────

        [Fact]
        public void Damage_AbsorbedByManaShieldFirst()
        {
            var cfg = MakeConfig();
            MakeStoreWithPlayer(cfg, currentMana: 0f);
            var sys = new ManaShieldSystem(Store, cfg, PlayerId);
            sys.Initialize();
            Store.PlayerManaShield[PlayerId] = 30f;
            Store.PlayerManaShieldAbsorbRatio[PlayerId] = 1f;
            Store.PlayerCurrentHealth[PlayerId] = 100f;

            Store.DecreasePlayerHealth(PlayerId, 20f);

            // Shield should drop by 20, health should be untouched
            Assert.Equal(10f, Store.PlayerManaShield[PlayerId], 2);
            Assert.Equal(100f, Store.PlayerCurrentHealth[PlayerId], 2);
            Assert.True(Store.PlayerManaShieldTriggered[PlayerId]);
        }

        [Fact]
        public void Damage_OverflowsToNormalShield()
        {
            var cfg = MakeConfig();
            MakeStoreWithPlayer(cfg, currentMana: 0f);
            Store.PlayerManaShield[PlayerId] = 10f;
            Store.PlayerManaShieldAbsorbRatio[PlayerId] = 1f;
            Store.PlayerShield[PlayerId] = 20f;
            Store.PlayerCurrentHealth[PlayerId] = 100f;

            // 50 damage: 10 mana shield + 20 player shield + 20 health loss
            Store.DecreasePlayerHealth(PlayerId, 50f);

            Assert.Equal(0f, Store.PlayerManaShield[PlayerId], 2);
            Assert.Equal(0f, Store.PlayerShield[PlayerId], 2);
            Assert.Equal(80f, Store.PlayerCurrentHealth[PlayerId], 2);
        }

        [Fact]
        public void Damage_AbsorbRatioAboveOne_DoublesShieldEfficiency()
        {
            var cfg = MakeConfig();
            MakeStoreWithPlayer(cfg, currentMana: 0f);
            Store.PlayerManaShield[PlayerId] = 10f;
            Store.PlayerManaShieldAbsorbRatio[PlayerId] = 2f; // 1 shield = 2 HP absorbed
            Store.PlayerCurrentHealth[PlayerId] = 100f;

            // 20 damage with ratio=2: pool drain = 20/2 = 10, shield fully drained
            Store.DecreasePlayerHealth(PlayerId, 20f);

            Assert.Equal(0f, Store.PlayerManaShield[PlayerId], 2);
            Assert.Equal(100f, Store.PlayerCurrentHealth[PlayerId], 2);
        }

        [Fact]
        public void Damage_AbsorbRatioZero_TakesOriginalPath()
        {
            var cfg = MakeConfig();
            MakeStoreWithPlayer(cfg, currentMana: 0f);
            Store.PlayerManaShield[PlayerId] = 50f; // pool full
            Store.PlayerManaShieldAbsorbRatio[PlayerId] = 0f; // disabled sentinel
            Store.PlayerCurrentHealth[PlayerId] = 100f;

            Store.DecreasePlayerHealth(PlayerId, 30f);

            // Damage goes straight to health; mana shield untouched
            Assert.Equal(50f, Store.PlayerManaShield[PlayerId], 2);
            Assert.Equal(70f, Store.PlayerCurrentHealth[PlayerId], 2);
            Assert.False(Store.PlayerManaShieldTriggered[PlayerId]);
        }

        [Fact]
        public void Damage_ZeroShield_DoesNotTriggerLatch()
        {
            var cfg = MakeConfig();
            MakeStoreWithPlayer(cfg, currentMana: 0f);
            Store.PlayerManaShield[PlayerId] = 0f;
            Store.PlayerCurrentHealth[PlayerId] = 100f;

            Store.DecreasePlayerHealth(PlayerId, 30f);

            Assert.Equal(70f, Store.PlayerCurrentHealth[PlayerId], 2);
            Assert.False(Store.PlayerManaShieldTriggered[PlayerId]);
        }

        // ─── Disabled config: system forces ratio to 0 ────────────────────

        [Fact]
        public void DisabledConfig_ForcesAbsorbRatioToZero()
        {
            var cfg = MakeConfig(enabled: false);
            MakeStoreWithPlayer(cfg, currentMana: 100f);
            var sys = new ManaShieldSystem(Store, cfg, PlayerId);
            sys.Initialize();

            sys.Update(0.1f);

            // No shield should be gained
            Assert.Equal(0f, Store.PlayerManaShield[PlayerId]);
            // Absorb ratio should be 0 so the damage path takes the cheap branch
            Assert.Equal(0f, Store.PlayerManaShieldAbsorbRatio[PlayerId]);
        }

        [Fact]
        public void EnabledConfig_ReassertsAbsorbRatioOnUpdate()
        {
            var cfg = MakeConfig(enabled: true);
            MakeStoreWithPlayer(cfg, currentMana: 100f);
            var sys = new ManaShieldSystem(Store, cfg, PlayerId);
            sys.Initialize();

            // Simulate a frame where the ratio was forced to 0 by a prior disabled config
            Store.PlayerManaShieldAbsorbRatio[PlayerId] = 0f;

            sys.Update(0.1f);

            Assert.Equal(1f, Store.PlayerManaShieldAbsorbRatio[PlayerId]);
        }

        [Fact]
        public void ZeroMaxMana_InertPath()
        {
            var cfg = MakeConfig();
            MakeStoreWithPlayer(cfg, currentMana: 0f);
            Store.PlayerMaxMana[PlayerId] = 0f; // no mana pool
            var sys = new ManaShieldSystem(Store, cfg, PlayerId);
            sys.Initialize();

            sys.Update(1f);

            // Cap stays at 0; no shield built
            Assert.Equal(0f, Store.PlayerManaShieldCap[PlayerId]);
            Assert.Equal(0f, Store.PlayerManaShield[PlayerId]);
        }
    }
}