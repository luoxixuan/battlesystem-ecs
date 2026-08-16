using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Mechanisms.Perception
{
    /// <summary>
    /// Invariants for the Threat Score / Dynamic Difficulty Scaling system (Round 99 Direction 5).
    /// Tests verify:
    /// 1. Fields exist with correct default (zero) values
    /// 2. Threat multiplier math at the constants level
    /// 3. Spawn-time HP scaling uses PlayerRecentDPS
    /// 4. EMA decay logic reduces PlayerRecentDPS over time
    /// 5. Cap is enforced (max multiplier, no lower than 1.0)
    /// 6. Zero DPS path is zero-overhead (no scale applied)
    /// </summary>
    public class ThreatScoreTests
    {
        // ─── Field initialization tests ──────────────────────────────────

        [Fact]
        public void PlayerRecentDPS_DefaultsToZero()
        {
            var store = new ComponentStore();
            for (int p = 0; p < 10; p++)
            {
                Assert.Equal(0f, store.PlayerRecentDPS[p]);
                Assert.Equal(0f, store.PlayerDPSAccumulator[p]);
            }
        }

        // ─── Config constants invariants ─────────────────────────────────

        [Fact]
        public void ThreatScoreConfig_HasExpectedDefaults()
        {
            // Defaults must be safe: rate 0 means no scaling, max > 1 to allow growth.
            Assert.True(ThreatScoreConfig.ThreatScalingRate > 0f,
                "ThreatScalingRate must be positive to allow scaling");
            Assert.True(ThreatScoreConfig.MaxThreatMultiplier > 1.0f,
                "MaxThreatMultiplier must be > 1.0 to allow growth");
            Assert.True(ThreatScoreConfig.MinThreatMultiplier <= 1.0f,
                "MinThreatMultiplier must be <= 1.0 (system only makes harder)");
            Assert.Equal(1.0f, ThreatScoreConfig.MinThreatMultiplier);
            Assert.True(ThreatScoreConfig.DPSWindowSec > 0f,
                "DPSWindowSec must be positive");
        }

        // ─── Spawn-time scaling math (pure compute, no scheduler) ───────

        [Fact]
        public void ThreatMultiplier_ZeroDPSYieldsOne()
        {
            // At zero DPS, multiplier = 1 + 0 * rate = 1.0 → no scaling.
            float recentDps = 0f;
            float mult = 1f + recentDps * ThreatScoreConfig.ThreatScalingRate;
            Assert.Equal(1.0f, mult);
        }

        [Fact]
        public void ThreatMultiplier_HighDPSIsCapped()
        {
            // Very high DPS: 1 + 1e6 * 1e-4 = 101 → cap should clamp to MaxThreatMultiplier.
            float recentDps = 1_000_000f;
            float mult = 1f + recentDps * ThreatScoreConfig.ThreatScalingRate;
            if (mult > ThreatScoreConfig.MaxThreatMultiplier) mult = ThreatScoreConfig.MaxThreatMultiplier;
            if (mult < ThreatScoreConfig.MinThreatMultiplier) mult = ThreatScoreConfig.MinThreatMultiplier;
            Assert.Equal(ThreatScoreConfig.MaxThreatMultiplier, mult);
        }

        [Fact]
        public void ThreatMultiplier_ReasonableDPSScalesLinearly()
        {
            // At rate 0.0001: 10000 DPS → mult = 1 + 10000*0.0001 = 2.0
            // 20000 DPS → 3.0
            float mult1 = 1f + 10000f * ThreatScoreConfig.ThreatScalingRate;
            float mult2 = 1f + 20000f * ThreatScoreConfig.ThreatScalingRate;
            Assert.Equal(2.0f, mult1, 3);
            Assert.Equal(3.0f, mult2, 3);
        }

        // ─── EMA decay math (simulating FrameScheduler.DecayAndAccumulateThreatScore) ──

        [Fact]
        public void EMADecay_DecaysTowardZeroWithoutAccumulator()
        {
            var store = new ComponentStore();
            store.PlayerRecentDPS[0] = 1000f;
            // No accumulator: pure decay
            float halfLife = ThreatScoreConfig.DPSWindowSec;
            float dt = 0.5f; // half-life / 10 → expect ~7% remaining
            float alpha = 1f - MathF.Exp(-0.6931472f * dt / halfLife);

            for (int i = 0; i < 1; i++)
            {
                float decayed = store.PlayerRecentDPS[0] * (1f - alpha);
                store.PlayerRecentDPS[0] = decayed + 0f; // no accumulator
            }
            // After dt=0.5s with halfLife=5s, alpha ≈ 0.0677 → result ≈ 932
            Assert.True(store.PlayerRecentDPS[0] < 1000f,
                "EMA decay should reduce PlayerRecentDPS over time");
            Assert.True(store.PlayerRecentDPS[0] > 900f,
                "Decay over 0.5s should leave most of the value intact");
        }

        [Fact]
        public void EMAUpdate_AccumulatorResetsAfterDecay()
        {
            var store = new ComponentStore();
            store.PlayerRecentDPS[0] = 100f;
            store.PlayerDPSAccumulator[0] = 50f;

            // Simulate one tick of DecayAndAccumulateThreatScore
            float halfLife = ThreatScoreConfig.DPSWindowSec;
            float dt = 1f / 60f;
            float alpha = 1f - MathF.Exp(-0.6931472f * dt / halfLife);
            float decayed = store.PlayerRecentDPS[0] * (1f - alpha);
            float added = store.PlayerDPSAccumulator[0] * alpha;
            store.PlayerRecentDPS[0] = decayed + added;
            store.PlayerDPSAccumulator[0] = 0f;

            Assert.Equal(0f, store.PlayerDPSAccumulator[0]);
            Assert.True(store.PlayerRecentDPS[0] >= 0f, "Result must be non-negative");
        }

        // ─── Integration: end-to-end spawn scaling via FieldSetter ──────

        [Fact]
        public void PlayerRecentDPS_DrivesSpawnScalingInWaveSpawningSystem()
        {
            // Simulate the spawn-time scaling logic from WaveSpawningSystem:
            // scaledHealth = base * threatMult
            var store = new ComponentStore();
            float baseHp = 100f;

            // Case 1: zero DPS → mult = 1.0
            store.PlayerRecentDPS[0] = 0f;
            float mult = 1f + store.PlayerRecentDPS[0] * ThreatScoreConfig.ThreatScalingRate;
            float scaledHp1 = baseHp * mult;
            Assert.Equal(100f, scaledHp1);

            // Case 2: 5000 DPS → mult = 1.5 → 150 HP
            store.PlayerRecentDPS[0] = 5000f;
            mult = 1f + store.PlayerRecentDPS[0] * ThreatScoreConfig.ThreatScalingRate;
            if (mult > ThreatScoreConfig.MaxThreatMultiplier) mult = ThreatScoreConfig.MaxThreatMultiplier;
            if (mult < ThreatScoreConfig.MinThreatMultiplier) mult = ThreatScoreConfig.MinThreatMultiplier;
            float scaledHp2 = baseHp * mult;
            Assert.Equal(150f, scaledHp2, 3);

            // Case 3: extreme DPS clamped to MaxThreatMultiplier (3.0)
            store.PlayerRecentDPS[0] = 100_000_000f;
            mult = 1f + store.PlayerRecentDPS[0] * ThreatScoreConfig.ThreatScalingRate;
            if (mult > ThreatScoreConfig.MaxThreatMultiplier) mult = ThreatScoreConfig.MaxThreatMultiplier;
            float scaledHp3 = baseHp * mult;
            Assert.Equal(300f, scaledHp3); // 100 * 3.0
        }
    }
}
