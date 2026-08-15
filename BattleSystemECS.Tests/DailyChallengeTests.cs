using System;
using System.Collections.Generic;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Tests
{
    /// <summary>
    /// Tests for Round 105 Direction 9: Daily Challenge / Rotating Seed.
    /// Verifies that:
    ///   - Date seed is deterministic (same date → same hash)
    ///   - Different dates → different seeds
    ///   - SeedSelectIndices returns distinct indices, correct count
    ///   - ResolveForDate with empty pool / null pool returns empty result
    ///   - ApplyToConfig multiplies damage/gold/EnemyHp and adds StartingGoldBonus
    ///   - ApplyToConfig with empty result leaves config at neutral defaults
    ///   - GameConfig.DailyModifierPool is initialized to an empty list
    ///   - GameConfig.DailyDamageMult / GoldMult / EnemyHpMult default to 1.0
    ///   - GameConfig.DailyStartingGoldBonus defaults to 0
    /// </summary>
    public class DailyChallengeTests
    {
        private static List<DailyModifierDef> MakePool(int n)
        {
            var pool = new List<DailyModifierDef>(n);
            for (int i = 0; i < n; i++)
            {
                pool.Add(new DailyModifierDef
                {
                    Id = "m" + i,
                    Name = "Modifier " + i,
                    Description = "test " + i,
                    DamageMult = 1.0f + 0.01f * i,
                    GoldMult = 1.0f + 0.02f * i,
                    EnemyHpMult = 1.0f + 0.03f * i,
                    StartingGoldBonus = i * 5f
                });
            }
            return pool;
        }

        // ─── HashDateSeed determinism ─────────────────────────────────────

        [Fact]
        public void HashDateSeed_SameDate_SameHash()
        {
            // FNV-1a is deterministic — same input → same output, no randomness.
            int a = DailyChallengeSystem.HashDateSeed("2026-06-04");
            int b = DailyChallengeSystem.HashDateSeed("2026-06-04");
            Assert.Equal(a, b);
        }

        [Fact]
        public void HashDateSeed_DifferentDates_DifferentHashes()
        {
            // Two adjacent days should (with overwhelming probability) hash differently.
            int a = DailyChallengeSystem.HashDateSeed("2026-06-04");
            int b = DailyChallengeSystem.HashDateSeed("2026-06-05");
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void HashDateSeed_EmptyOrNull_ReturnsZero()
        {
            Assert.Equal(0, DailyChallengeSystem.HashDateSeed(""));
            Assert.Equal(0, DailyChallengeSystem.HashDateSeed(null));
        }

        // ─── SeedSelectIndices properties ─────────────────────────────────

        [Fact]
        public void SeedSelectIndices_ReturnsDistinctIndices()
        {
            // 20 trials × 5 picks from 30-pool — no duplicates allowed.
            for (int trial = 0; trial < 20; trial++)
            {
                var picks = DailyChallengeSystem.SeedSelectIndices(trial * 7919, 5, 30);
                Assert.Equal(5, picks.Count);
                var set = new HashSet<int>(picks);
                Assert.Equal(5, set.Count); // all 5 indices are distinct
                foreach (int idx in picks)
                {
                    Assert.True(idx >= 0 && idx < 30, "out-of-range index");
                }
            }
        }

        [Fact]
        public void SeedSelectIndices_ClampedToPoolSize()
        {
            // Requesting 20 from a 10-pool → exactly 10 returned (no repeat possible).
            var picks = DailyChallengeSystem.SeedSelectIndices(42, 20, 10);
            Assert.Equal(10, picks.Count);
            var set = new HashSet<int>(picks);
            Assert.Equal(10, set.Count);
        }

        [Fact]
        public void SeedSelectIndices_SameSeed_SameSelection()
        {
            int seed = 1234567;
            var a = DailyChallengeSystem.SeedSelectIndices(seed, 3, 16);
            var b = DailyChallengeSystem.SeedSelectIndices(seed, 3, 16);
            Assert.Equal(a, b);
        }

        [Fact]
        public void SeedSelectIndices_EmptyPool_ReturnsEmpty()
        {
            var picks = DailyChallengeSystem.SeedSelectIndices(1, 5, 0);
            Assert.Empty(picks);
        }

        [Fact]
        public void SeedSelectIndices_ZeroCount_ReturnsEmpty()
        {
            var picks = DailyChallengeSystem.SeedSelectIndices(1, 0, 16);
            Assert.Empty(picks);
        }

        // ─── ResolveForDate properties ────────────────────────────────────

        [Fact]
        public void ResolveForDate_SameDate_SameModifiers()
        {
            // The whole point of daily seeds: same calendar day → same picks.
            var pool = MakePool(20);
            var date = new DateTime(2026, 6, 4);
            var a = DailyChallengeSystem.ResolveForDate(pool, date, 3);
            var b = DailyChallengeSystem.ResolveForDate(pool, date, 3);
            Assert.Equal(a.Seed, b.Seed);
            Assert.Equal(a.Date, b.Date);
            Assert.Equal(a.Selected.Count, b.Selected.Count);
            for (int i = 0; i < a.Selected.Count; i++)
            {
                Assert.Equal(a.Selected[i].Id, b.Selected[i].Id);
            }
        }

        [Fact]
        public void ResolveForDate_DifferentDates_DifferentModifiers()
        {
            // Two different dates should produce different modifier sets with very
            // high probability. We try 3 dates and check that at least one pair
            // differs — guards against the (extremely unlikely) hash collision.
            var pool = MakePool(32);
            var a = DailyChallengeSystem.ResolveForDate(pool, new DateTime(2026, 6, 4), 3);
            var b = DailyChallengeSystem.ResolveForDate(pool, new DateTime(2026, 6, 5), 3);
            var c = DailyChallengeSystem.ResolveForDate(pool, new DateTime(2026, 6, 6), 3);
            // Seed values are the load-bearing difference — pick sets can
            // occasionally overlap by chance on small pools, but seeds differ.
            Assert.NotEqual(a.Seed, b.Seed);
            Assert.NotEqual(b.Seed, c.Seed);
            Assert.NotEqual(a.Seed, c.Seed);
        }

        [Fact]
        public void ResolveForDate_EmptyPool_ReturnsEmptyResult()
        {
            var r = DailyChallengeSystem.ResolveForDate(new List<DailyModifierDef>(), DateTime.Today, 3);
            Assert.Equal(0, r.Seed);
            Assert.Empty(r.Selected);
            Assert.Equal(DateTime.Today.ToString("yyyy-MM-dd"), r.Date);
        }

        [Fact]
        public void ResolveForDate_NullPool_ReturnsEmptyResult()
        {
            var r = DailyChallengeSystem.ResolveForDate(null, DateTime.Today, 3);
            Assert.Empty(r.Selected);
        }

        // ─── ApplyToConfig behavior ───────────────────────────────────────

        [Fact]
        public void ApplyToConfig_EmptyResult_LeavesNeutralValues()
        {
            // Default config: no daily, multipliers are 1.0 / bonus is 0.
            var cfg = new GameConfig();
            Assert.Equal(1.0f, cfg.DailyDamageMult);
            Assert.Equal(1.0f, cfg.DailyGoldMult);
            Assert.Equal(1.0f, cfg.DailyEnemyHpMult);
            Assert.Equal(0f, cfg.DailyStartingGoldBonus);

            // Apply empty result → still neutral.
            var r = new DailyChallengeResult { Date = "2026-06-04", Seed = 42 };
            DailyChallengeSystem.ApplyToConfig(cfg, r);
            Assert.Equal(1.0f, cfg.DailyDamageMult);
            Assert.Equal(1.0f, cfg.DailyGoldMult);
            Assert.Equal(1.0f, cfg.DailyEnemyHpMult);
            Assert.Equal(0f, cfg.DailyStartingGoldBonus);
        }

        [Fact]
        public void ApplyToConfig_ProductsAndSum_AreCorrect()
        {
            // Three modifiers: damageMult 1.2 × 1.3 × 1.0 = 1.56
            // goldMult 1.5 × 1.0 × 0.9 = 1.35
            // enemyHpMult 1.1 × 1.2 × 1.0 = 1.32
            // startingGoldBonus 50 + 0 + (-20) = 30
            var cfg = new GameConfig();
            var r = new DailyChallengeResult
            {
                Date = "2026-06-04",
                Seed = 1,
                Selected = new List<DailyModifierDef>
                {
                    new DailyModifierDef { DamageMult = 1.2f, GoldMult = 1.5f, EnemyHpMult = 1.1f, StartingGoldBonus = 50f },
                    new DailyModifierDef { DamageMult = 1.3f, GoldMult = 1.0f, EnemyHpMult = 1.2f, StartingGoldBonus = 0f },
                    new DailyModifierDef { DamageMult = 1.0f, GoldMult = 0.9f, EnemyHpMult = 1.0f, StartingGoldBonus = -20f }
                }
            };
            DailyChallengeSystem.ApplyToConfig(cfg, r);
            Assert.Equal(1.56f, cfg.DailyDamageMult, 4);
            Assert.Equal(1.35f, cfg.DailyGoldMult, 4);
            Assert.Equal(1.32f, cfg.DailyEnemyHpMult, 4);
            Assert.Equal(30f, cfg.DailyStartingGoldBonus, 4);
            Assert.Equal(r, cfg.DailyLastResult);
        }

        [Fact]
        public void ApplyToConfig_ZeroDamageMult_TreatedAsInert()
        {
            // Defensive: a modifier with DamageMult=0 is treated as inert
            // (multiplier set to 1) so a malformed JSON entry can't zero out
            // player damage. The product rule only multiplies by >0 values.
            var cfg = new GameConfig();
            var r = new DailyChallengeResult
            {
                Selected = new List<DailyModifierDef>
                {
                    new DailyModifierDef { DamageMult = 0f, GoldMult = 1.5f, EnemyHpMult = 1.0f, StartingGoldBonus = 0f }
                }
            };
            DailyChallengeSystem.ApplyToConfig(cfg, r);
            // DamageMult=0 is treated as inert → result stays at 1.0
            Assert.Equal(1.0f, cfg.DailyDamageMult, 4);
            // GoldMult=1.5 still applied
            Assert.Equal(1.5f, cfg.DailyGoldMult, 4);
        }

        [Fact]
        public void ApplyToConfig_NullSelected_TreatedAsEmpty()
        {
            var cfg = new GameConfig();
            var r = new DailyChallengeResult { Selected = null! };
            DailyChallengeSystem.ApplyToConfig(cfg, r);
            Assert.Equal(1.0f, cfg.DailyDamageMult);
            Assert.Equal(1.0f, cfg.DailyGoldMult);
            Assert.Equal(1.0f, cfg.DailyEnemyHpMult);
            Assert.Equal(0f, cfg.DailyStartingGoldBonus);
        }

        [Fact]
        public void ApplyToConfig_NullConfig_DoesNotThrow()
        {
            // Defensive: never crash on a null config (caller bug).
            var r = new DailyChallengeResult { Selected = new List<DailyModifierDef>() };
            var ex = Record.Exception(() => DailyChallengeSystem.ApplyToConfig(null, r));
            Assert.Null(ex);
        }

        // ─── GameConfig field defaults ────────────────────────────────────

        [Fact]
        public void GameConfig_Defaults_DailyFieldsNeutral()
        {
            // Fresh GameConfig: pool is empty, modifiers are inert.
            var cfg = new GameConfig();
            Assert.NotNull(cfg.DailyModifierPool);
            Assert.Empty(cfg.DailyModifierPool);
            Assert.Equal(3, cfg.DailyModifierCount);
            Assert.Equal(1.0f, cfg.DailyDamageMult);
            Assert.Equal(1.0f, cfg.DailyGoldMult);
            Assert.Equal(1.0f, cfg.DailyEnemyHpMult);
            Assert.Equal(0f, cfg.DailyStartingGoldBonus);
            Assert.Null(cfg.DailyLastResult);
        }

        [Fact]
        public void DailyModifierDef_Defaults_AreNeutral()
        {
            // Modifier with default values should multiply by 1.0 and add 0.
            var m = new DailyModifierDef();
            Assert.Equal("", m.Id);
            Assert.Equal("", m.Name);
            Assert.Equal("", m.Description);
            Assert.Equal(1.0f, m.DamageMult);
            Assert.Equal(1.0f, m.GoldMult);
            Assert.Equal(1.0f, m.EnemyHpMult);
            Assert.Equal(0f, m.StartingGoldBonus);
        }
    }
}
