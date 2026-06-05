using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
{
    /// <summary>
    /// Invariants for the Adaptive Spawn Count / Rubber-band Spawn Pacing system
    /// (Round 120 Direction 3). Tests verify:
    ///  1. WaveConfig.ExpectedKillCount defaults to 0 (backward-compatible)
    ///  2. AdaptiveSpawnConfig constants are safe (sensitivity > 0, min < max)
    ///  3. PerformanceSpawnMultiplier defaults to 1.0 on a fresh WaveSpawningSystem
    ///  4. SetPerformanceSpawnMultiplier clamps to [Min, Max]
    ///  5. SetPerformanceSpawnMultiplier snaps near-1 values to exactly 1.0
    ///  6. Over-kill (kills > expected) → multiplier > 1 → more spawns next wave
    ///  7. Under-kill (kills < expected) → multiplier < 1 → fewer spawns next wave
    ///  8. Clamping at upper bound (extreme over-kill) — multiplier never > MaxSpawnMultiplier
    ///  9. Clamping at lower bound (zero kills vs huge expected) — multiplier never < MinSpawnMultiplier
    /// 10. expectedKills=0 (designer opted out) — multiplier stays at 1.0 even with kills data
    /// 11. SetLevel resets the multiplier back to 1.0
    /// 12. InjectExtraEnemies honors multiplier (2x) — doubled count
    /// 13. InjectExtraEnemies at multiplier=1.0 is unchanged (zero-overhead)
    /// 14. InjectExtraEnemies at min multiplier (0.5) — halved count
    /// </summary>
    public class AdaptiveSpawnCountTests
    {
        private (ComponentStore store, GameConfig config) Env()
        {
            var store = new ComponentStore();
            int pid = store.CreateEntity();
            store.PlayerMaxHealth[pid] = 200f;
            store.PlayerCurrentHealth[pid] = 200f;
            return (store, new GameConfig());
        }

        // ─── Config invariants ─────────────────────────────────────────────

        [Fact]
        public void AdaptiveSpawnConfig_HasSafeDefaults()
        {
            // sensitivity > 0 enables the system by default; designers can set 0 to disable.
            Assert.True(AdaptiveSpawnConfig.DefaultSpawnSensitivity > 0f,
                "DefaultSpawnSensitivity must be > 0 to enable rubber-band by default");
            // Bounds: min < 1.0 < max so the system can both reduce and increase spawns.
            Assert.True(AdaptiveSpawnConfig.MinSpawnMultiplier < 1.0f,
                "MinSpawnMultiplier must be < 1.0 to allow catch-up reduction");
            Assert.True(AdaptiveSpawnConfig.MaxSpawnMultiplier > 1.0f,
                "MaxSpawnMultiplier must be > 1.0 to allow challenge ramp-up");
            Assert.True(AdaptiveSpawnConfig.MinSpawnMultiplier > 0f,
                "MinSpawnMultiplier must be > 0 to avoid zero-spawn waves");
        }

        [Fact]
        public void WaveConfig_ExpectedKillCount_DefaultsToZero()
        {
            // 0 means "no scaling" — backward-compatible with old JSON files.
            var wc = new WaveConfig();
            Assert.Equal(0, wc.ExpectedKillCount);
        }

        // ─── WaveSpawningSystem: state + clamping ─────────────────────────

        [Fact]
        public void WaveSpawningSystem_PerformanceSpawnMultiplier_DefaultsToOne()
        {
            var (store, config) = Env();
            var sys = new WaveSpawningSystem(store, new MockRenderer(), config);
            Assert.Equal(1.0f, sys.PerformanceSpawnMultiplier);
        }

        [Fact]
        public void SetPerformanceSpawnMultiplier_ClampsToMax()
        {
            var (store, config) = Env();
            var sys = new WaveSpawningSystem(store, new MockRenderer(), config);
            sys.SetPerformanceSpawnMultiplier(99f);
            Assert.Equal(AdaptiveSpawnConfig.MaxSpawnMultiplier, sys.PerformanceSpawnMultiplier);
        }

        [Fact]
        public void SetPerformanceSpawnMultiplier_ClampsToMin()
        {
            var (store, config) = Env();
            var sys = new WaveSpawningSystem(store, new MockRenderer(), config);
            sys.SetPerformanceSpawnMultiplier(-5f);
            Assert.Equal(AdaptiveSpawnConfig.MinSpawnMultiplier, sys.PerformanceSpawnMultiplier);
        }

        [Fact]
        public void SetPerformanceSpawnMultiplier_SnapsNearOneToExactlyOne()
        {
            // Sub-1e-4 deviations snap to exactly 1.0 so the hot-path branch stays cheap
            // and test comparisons are exact. 1e-5 is well below the snap threshold.
            var (store, config) = Env();
            var sys = new WaveSpawningSystem(store, new MockRenderer(), config);
            sys.SetPerformanceSpawnMultiplier(1.0f + 1e-5f);
            Assert.Equal(1.0f, sys.PerformanceSpawnMultiplier);
            sys.SetPerformanceSpawnMultiplier(1.0f - 1e-5f);
            Assert.Equal(1.0f, sys.PerformanceSpawnMultiplier);
        }

        // ─── AdaptiveDifficultySystem end-to-end → multiplier write ──────

        [Fact]
        public void OverKill_RaisesMultiplierAboveOne()
        {
            // Player killed 30 out of an expected 20 → +50% over-kill. With sensitivity
            // 0.5 (default), rawDelta=0.5, multiplier = 1.0 + 0.5*0.5 = 1.25. After
            // clamp, max 2.0. After near-1 snap, still 1.25 (well above threshold).
            var (store, config) = Env();
            var sys = new WaveSpawningSystem(store, new MockRenderer(), config);
            var ads = new AdaptiveDifficultySystem(store, config);
            ads.SetWaveSpawningSystem(sys);
            // Simulate 30 kills recorded this wave
            for (int i = 0; i < 30; i++) ads.RecordKill(0);
            ads.OnWaveComplete(0, expectedKills: 20);
            Assert.Equal(1.25f, sys.PerformanceSpawnMultiplier, 3);
        }

        [Fact]
        public void UnderKill_LowersMultiplierBelowOne()
        {
            // Player killed 5 out of an expected 20 → -75% under-kill. With sensitivity
            // 0.5, rawDelta = -0.75, multiplier = 1.0 + (-0.75)*0.5 = 0.625. Clamp
            // to MinSpawnMultiplier (0.5) — 0.625 is above 0.5 so stays 0.625.
            var (store, config) = Env();
            var sys = new WaveSpawningSystem(store, new MockRenderer(), config);
            var ads = new AdaptiveDifficultySystem(store, config);
            ads.SetWaveSpawningSystem(sys);
            for (int i = 0; i < 5; i++) ads.RecordKill(0);
            ads.OnWaveComplete(0, expectedKills: 20);
            Assert.Equal(0.625f, sys.PerformanceSpawnMultiplier, 3);
        }

        [Fact]
        public void ZeroKills_ClampsToMinSpawnMultiplier()
        {
            // Player killed 0 of expected 100 → rawDelta = -1.0 → mult = 0.5 → clamp to 0.5.
            var (store, config) = Env();
            var sys = new WaveSpawningSystem(store, new MockRenderer(), config);
            var ads = new AdaptiveDifficultySystem(store, config);
            ads.SetWaveSpawningSystem(sys);
            // No kills recorded
            ads.OnWaveComplete(0, expectedKills: 100);
            Assert.Equal(AdaptiveSpawnConfig.MinSpawnMultiplier, sys.PerformanceSpawnMultiplier);
        }

        [Fact]
        public void MassiveOverKill_ClampsToMaxSpawnMultiplier()
        {
            // Player killed 1000 of expected 1 → rawDelta = 999 → mult = 1 + 499.5 = 500.5 → clamp 2.0.
            var (store, config) = Env();
            var sys = new WaveSpawningSystem(store, new MockRenderer(), config);
            var ads = new AdaptiveDifficultySystem(store, config);
            ads.SetWaveSpawningSystem(sys);
            for (int i = 0; i < 1000; i++) ads.RecordKill(0);
            ads.OnWaveComplete(0, expectedKills: 1);
            Assert.Equal(AdaptiveSpawnConfig.MaxSpawnMultiplier, sys.PerformanceSpawnMultiplier);
        }

        [Fact]
        public void ExpectedKillsZero_MultiplierStaysAtOne()
        {
            // Designer opted out (no ExpectedKillCount in JSON) — multiplier untouched.
            var (store, config) = Env();
            var sys = new WaveSpawningSystem(store, new MockRenderer(), config);
            var ads = new AdaptiveDifficultySystem(store, config);
            ads.SetWaveSpawningSystem(sys);
            for (int i = 0; i < 50; i++) ads.RecordKill(0);
            // expectedKills = 0 → the rubber-band block is skipped.
            ads.OnWaveComplete(0, expectedKills: 0);
            Assert.Equal(1.0f, sys.PerformanceSpawnMultiplier);
        }

        // ─── SetLevel resets multiplier ───────────────────────────────────

        [Fact]
        public void SetLevel_ResetsMultiplierToOne()
        {
            var (store, config) = Env();
            var sys = new WaveSpawningSystem(store, new MockRenderer(), config);
            sys.SetPerformanceSpawnMultiplier(1.5f);
            Assert.Equal(1.5f, sys.PerformanceSpawnMultiplier);
            sys.SetLevel(2);
            Assert.Equal(1.0f, sys.PerformanceSpawnMultiplier);
        }

        // ─── Mid-wave spawn sites honor multiplier (scaled count returned) ─

        [Fact]
        public void InjectExtraEnemies_HonorsMultiplier()
        {
            // ApplyToMidWaveSpawns defaults to true, so a 2x multiplier should double
            // the inject count. We verify by counting enemies in the store.
            var (store, config) = Env();
            var sys = new WaveSpawningSystem(store, new MockRenderer(), config);
            sys.SetPerformanceSpawnMultiplier(2.0f);
            int baseline = store.GetActiveEnemyCount();
            sys.InjectExtraEnemies(3);
            int spawnedDelta = store.GetActiveEnemyCount() - baseline;
            Assert.Equal(6, spawnedDelta); // 3 * 2.0
        }

        [Fact]
        public void InjectExtraEnemies_MultiplierOne_NoChange()
        {
            // Zero-overhead path: multiplier = 1.0 → no scaling applied.
            var (store, config) = Env();
            var sys = new WaveSpawningSystem(store, new MockRenderer(), config);
            int baseline = store.GetActiveEnemyCount();
            sys.InjectExtraEnemies(3);
            int spawnedDelta = store.GetActiveEnemyCount() - baseline;
            Assert.Equal(3, spawnedDelta);
        }

        [Fact]
        public void InjectExtraEnemies_ClampedMultiplierHonorsMin()
        {
            // 0.5x (MinSpawnMultiplier) on a 4-count request → 2 enemies.
            var (store, config) = Env();
            var sys = new WaveSpawningSystem(store, new MockRenderer(), config);
            sys.SetPerformanceSpawnMultiplier(0.5f);
            int baseline = store.GetActiveEnemyCount();
            sys.InjectExtraEnemies(4);
            int spawnedDelta = store.GetActiveEnemyCount() - baseline;
            Assert.Equal(2, spawnedDelta); // 4 * 0.5
        }
    }
}
