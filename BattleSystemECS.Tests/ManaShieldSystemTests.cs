using System;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
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
    public class ManaShieldSystemTests
    {
        private const int PlayerId = 0;
        private const float PlayerMaxManaDefault = 100f;

        private static GameConfig MakeConfig(bool enabled = true, float ratio = 1f, float maxPct = 0.5f, float decay = 5f, float triggerPct = 0.7f)
        {
            return new GameConfig
            {
                ManaShield = new ManaShieldConfig
                {
                    Enabled = enabled,
                    ConversionRatio = ratio,
                    MaxShieldPercent = maxPct,
                    DecayPerSecond = decay,
                    TriggerThresholdPercent = triggerPct
                },
                Mana = new ManaConfig
                {
                    BaseMana = 0f,
                    MaxManaBase = PlayerMaxManaDefault,
                    ManaRegenPerSec = 0f, // Tests control mana directly
                    ManaRegenBuildPhase = 0f,
                    ManaCostMultiplier = 1f
                }
            };
        }

        private static ComponentStore MakeStoreWithPlayer(GameConfig cfg, float currentMana = 0f, float currentShield = 0f)
        {
            var store = new ComponentStore();
            store.AddPlayer(PlayerId, 5f, 1f, 10f, 1);
            store.PlayerMaxMana[PlayerId] = cfg.Mana.MaxManaBase;
            store.PlayerMana[PlayerId] = currentMana;
            store.PlayerCurrentHealth[PlayerId] = 1000f;
            store.PlayerMaxHealth[PlayerId] = 1000f;
            store.PlayerManaShield[PlayerId] = currentShield;
            return store;
        }

        // ─── Default state (backward compat) ──────────────────────────────

        [Fact]
        public void DefaultState_NewComponentStore_AllManaShieldFieldsZero()
        {
            var store = new ComponentStore();
            Assert.Equal(0f, store.PlayerManaShield[PlayerId]);
            Assert.Equal(0f, store.PlayerManaShieldCap[PlayerId]);
            Assert.Equal(0f, store.PlayerManaShieldAbsorbRatio[PlayerId]);
            Assert.False(store.PlayerManaShieldTriggered[PlayerId]);
        }

        [Fact]
        public void AddPlayer_InitializesManaShieldFields()
        {
            var store = new ComponentStore();
            store.AddPlayer(PlayerId, 5f, 1f, 10f, 1);
            Assert.Equal(0f, store.PlayerManaShield[PlayerId]);
            Assert.Equal(0f, store.PlayerManaShieldCap[PlayerId]);
            Assert.Equal(1f, store.PlayerManaShieldAbsorbRatio[PlayerId]); // baseline
            Assert.False(store.PlayerManaShieldTriggered[PlayerId]);
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
            var store = MakeStoreWithPlayer(cfg, currentMana: 100f); // 100% > 70% threshold
            var sys = new ManaShieldSystem(store, cfg, PlayerId);
            sys.Initialize();

            sys.Update(0.1f);

            // Cap = 100 * 0.5 = 50. Excess mana = 100 - 70 = 30. Should fill to cap.
            Assert.True(store.PlayerManaShield[PlayerId] > 0f, "shield should be > 0");
            Assert.True(store.PlayerManaShield[PlayerId] <= 50f + 0.01f, "shield must not exceed cap");
        }

        [Fact]
        public void ManaAboveThreshold_ShieldClampedToCap()
        {
            var cfg = MakeConfig();
            var store = MakeStoreWithPlayer(cfg, currentMana: 100f);
            var sys = new ManaShieldSystem(store, cfg, PlayerId);
            sys.Initialize();

            // Run multiple frames — shield must never exceed cap
            for (int i = 0; i < 10; i++) sys.Update(0.5f);

            float cap = store.PlayerManaShieldCap[PlayerId];
            Assert.True(store.PlayerManaShield[PlayerId] <= cap + 0.01f);
        }

        [Fact]
        public void ManaBelowThreshold_DecaysShield()
        {
            var cfg = MakeConfig(decay: 10f);
            var store = MakeStoreWithPlayer(cfg, currentMana: 10f, currentShield: 30f); // below 70% threshold
            var sys = new ManaShieldSystem(store, cfg, PlayerId);
            store.PlayerManaShieldCap[PlayerId] = 50f;

            sys.Update(1f);

            // 30 - 10*1 = 20
            Assert.Equal(20f, store.PlayerManaShield[PlayerId], 1);
        }

        [Fact]
        public void ManaBelowThreshold_DecayStopsAtZero()
        {
            var cfg = MakeConfig(decay: 100f);
            var store = MakeStoreWithPlayer(cfg, currentMana: 0f, currentShield: 5f);
            var sys = new ManaShieldSystem(store, cfg, PlayerId);
            store.PlayerManaShieldCap[PlayerId] = 50f;

            sys.Update(1f);

            Assert.Equal(0f, store.PlayerManaShield[PlayerId]);
        }

        [Fact]
        public void Cap_RecomputedFromMaxManaEachFrame()
        {
            var cfg = MakeConfig(maxPct: 0.5f);
            var store = MakeStoreWithPlayer(cfg, currentMana: 100f);
            var sys = new ManaShieldSystem(store, cfg, PlayerId);
            sys.Initialize();

            sys.Update(0.01f);
            float cap1 = store.PlayerManaShieldCap[PlayerId];
            Assert.Equal(50f, cap1, 1);

            // Bump max-mana mid-game (tech tree upgrade simulation)
            store.PlayerMaxMana[PlayerId] = 200f;
            sys.Update(0.01f);
            float cap2 = store.PlayerManaShieldCap[PlayerId];
            Assert.Equal(100f, cap2, 1);
        }

        // ─── Damage hot-path: shield absorbs before health ────────────────

        [Fact]
        public void Damage_AbsorbedByManaShieldFirst()
        {
            var cfg = MakeConfig();
            var store = MakeStoreWithPlayer(cfg, currentMana: 0f);
            var sys = new ManaShieldSystem(store, cfg, PlayerId);
            sys.Initialize();
            store.PlayerManaShield[PlayerId] = 30f;
            store.PlayerManaShieldAbsorbRatio[PlayerId] = 1f;
            store.PlayerCurrentHealth[PlayerId] = 100f;

            store.DecreasePlayerHealth(PlayerId, 20f);

            // Shield should drop by 20, health should be untouched
            Assert.Equal(10f, store.PlayerManaShield[PlayerId], 2);
            Assert.Equal(100f, store.PlayerCurrentHealth[PlayerId], 2);
            Assert.True(store.PlayerManaShieldTriggered[PlayerId]);
        }

        [Fact]
        public void Damage_OverflowsToNormalShield()
        {
            var cfg = MakeConfig();
            var store = MakeStoreWithPlayer(cfg, currentMana: 0f);
            store.PlayerManaShield[PlayerId] = 10f;
            store.PlayerManaShieldAbsorbRatio[PlayerId] = 1f;
            store.PlayerShield[PlayerId] = 20f;
            store.PlayerCurrentHealth[PlayerId] = 100f;

            // 50 damage: 10 mana shield + 20 player shield + 20 health loss
            store.DecreasePlayerHealth(PlayerId, 50f);

            Assert.Equal(0f, store.PlayerManaShield[PlayerId], 2);
            Assert.Equal(0f, store.PlayerShield[PlayerId], 2);
            Assert.Equal(80f, store.PlayerCurrentHealth[PlayerId], 2);
        }

        [Fact]
        public void Damage_AbsorbRatioAboveOne_DoublesShieldEfficiency()
        {
            var cfg = MakeConfig();
            var store = MakeStoreWithPlayer(cfg, currentMana: 0f);
            store.PlayerManaShield[PlayerId] = 10f;
            store.PlayerManaShieldAbsorbRatio[PlayerId] = 2f; // 1 shield = 2 HP absorbed
            store.PlayerCurrentHealth[PlayerId] = 100f;

            // 20 damage with ratio=2: pool drain = 20/2 = 10, shield fully drained
            store.DecreasePlayerHealth(PlayerId, 20f);

            Assert.Equal(0f, store.PlayerManaShield[PlayerId], 2);
            Assert.Equal(100f, store.PlayerCurrentHealth[PlayerId], 2);
        }

        [Fact]
        public void Damage_AbsorbRatioZero_TakesOriginalPath()
        {
            var cfg = MakeConfig();
            var store = MakeStoreWithPlayer(cfg, currentMana: 0f);
            store.PlayerManaShield[PlayerId] = 50f; // pool full
            store.PlayerManaShieldAbsorbRatio[PlayerId] = 0f; // disabled sentinel
            store.PlayerCurrentHealth[PlayerId] = 100f;

            store.DecreasePlayerHealth(PlayerId, 30f);

            // Damage goes straight to health; mana shield untouched
            Assert.Equal(50f, store.PlayerManaShield[PlayerId], 2);
            Assert.Equal(70f, store.PlayerCurrentHealth[PlayerId], 2);
            Assert.False(store.PlayerManaShieldTriggered[PlayerId]);
        }

        [Fact]
        public void Damage_ZeroShield_DoesNotTriggerLatch()
        {
            var cfg = MakeConfig();
            var store = MakeStoreWithPlayer(cfg, currentMana: 0f);
            store.PlayerManaShield[PlayerId] = 0f;
            store.PlayerCurrentHealth[PlayerId] = 100f;

            store.DecreasePlayerHealth(PlayerId, 30f);

            Assert.Equal(70f, store.PlayerCurrentHealth[PlayerId], 2);
            Assert.False(store.PlayerManaShieldTriggered[PlayerId]);
        }

        // ─── Disabled config: system forces ratio to 0 ────────────────────

        [Fact]
        public void DisabledConfig_ForcesAbsorbRatioToZero()
        {
            var cfg = MakeConfig(enabled: false);
            var store = MakeStoreWithPlayer(cfg, currentMana: 100f);
            var sys = new ManaShieldSystem(store, cfg, PlayerId);
            sys.Initialize();

            sys.Update(0.1f);

            // No shield should be gained
            Assert.Equal(0f, store.PlayerManaShield[PlayerId]);
            // Absorb ratio should be 0 so the damage path takes the cheap branch
            Assert.Equal(0f, store.PlayerManaShieldAbsorbRatio[PlayerId]);
        }

        [Fact]
        public void EnabledConfig_ReassertsAbsorbRatioOnUpdate()
        {
            var cfg = MakeConfig(enabled: true);
            var store = MakeStoreWithPlayer(cfg, currentMana: 100f);
            var sys = new ManaShieldSystem(store, cfg, PlayerId);
            sys.Initialize();

            // Simulate a frame where the ratio was forced to 0 by a prior disabled config
            store.PlayerManaShieldAbsorbRatio[PlayerId] = 0f;

            sys.Update(0.1f);

            Assert.Equal(1f, store.PlayerManaShieldAbsorbRatio[PlayerId]);
        }

        [Fact]
        public void ZeroMaxMana_InertPath()
        {
            var cfg = MakeConfig();
            var store = MakeStoreWithPlayer(cfg, currentMana: 0f);
            store.PlayerMaxMana[PlayerId] = 0f; // no mana pool
            var sys = new ManaShieldSystem(store, cfg, PlayerId);
            sys.Initialize();

            sys.Update(1f);

            // Cap stays at 0; no shield built
            Assert.Equal(0f, store.PlayerManaShieldCap[PlayerId]);
            Assert.Equal(0f, store.PlayerManaShield[PlayerId]);
        }
    }
}
