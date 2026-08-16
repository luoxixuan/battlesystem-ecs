using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Tests.Infrastructure;

namespace BattleSystemECS.Tests.Mechanisms.Control
{
    /// <summary>
    /// Tests for Round 141 Direction 8 — Debuff Decay Resistance (EnemyDebuffResistMult):
    /// a per-enemy uniform duration scaler (0-1) applied to all debuff durations
    /// (root / disarm / polymorph / slow). Boss enemies get 0.5 → debuffs last 50% of base.
    /// Verifies:
    ///   - Default: EnemyDebuffResistMult = 0, no duration reduction
    ///   - ApplyDebuffResistToDuration: pure helper, no state mutation, returns scaled int
    ///   - Edge cases: resist=0 (no change), resist=1 (full immune → 0), resist=0.5 (50% reduction)
    ///   - ApplyEnemyDisarm applies the multiplier on top of EnemyDisarmResistance
    ///   - ApplyEnemyRoot applies the multiplier
    ///   - ApplyPolymorph applies the multiplier
    ///   - ApplySlow applies the multiplier (only duration, not factor)
    ///   - Higher resist (0.99) reduces 1-turn debuff to 0 (early-out safety)
    /// </summary>
    public class DebuffResistTests : BattleTestBase
    {
        // ─── Default (no resist configured) — backward compat ───────────────

        [Fact]
        public void DefaultResist_ZeroOnAllEnemies()
        {
            int e = Enemy();
            Assert.Equal(0f, Store.EnemyDebuffResistMult[e]);
        }

        [Fact]
        public void DefaultResist_DisarmDurationUnchanged()
        {
            int e = Enemy();
            Store.ApplyEnemyDisarm(e, 5);
            Assert.Equal(5f, Store.EnemyDisarmDurationLeft[e]);
        }

        [Fact]
        public void DefaultResist_RootDurationUnchanged()
        {
            int e = Enemy();
            Store.ApplyEnemyRoot(e, 4);
            Assert.Equal(4f, Store.EnemyRootDurationLeft[e]);
        }

        [Fact]
        public void DefaultResist_PolymorphDurationUnchanged()
        {
            int e = Enemy();
            Store.ApplyPolymorph(e, 6, 1.5f);
            Assert.Equal(6f, Store.EnemyPolymorphDurationLeft[e]);
        }

        [Fact]
        public void DefaultResist_SlowDurationUnchanged()
        {
            int e = Enemy();
            Store.ApplySlow(e, 0.5f, 3);
            Assert.Equal(3f, Store.EnemySlowDurationLeft[e]);
        }

        // ─── Pure helper: ApplyDebuffResistToDuration ───────────────────────

        [Fact]
        public void Helper_ZeroResist_ReturnsUnchanged()
        {
            int e = Enemy();
            Store.EnemyDebuffResistMult[e] = 0f;
            Assert.Equal(10, Store.ApplyDebuffResistToDuration(e, 10));
            Assert.Equal(1, Store.ApplyDebuffResistToDuration(e, 1));
        }

        [Fact]
        public void Helper_HalfResist_HalvesDuration()
        {
            int e = Enemy();
            Store.EnemyDebuffResistMult[e] = 0.5f;
            Assert.Equal(5, Store.ApplyDebuffResistToDuration(e, 10));
            Assert.Equal(2, Store.ApplyDebuffResistToDuration(e, 4)); // 4*0.5 = 2.0
        }

        [Fact]
        public void Helper_FullResist_ReturnsZero()
        {
            int e = Enemy();
            Store.EnemyDebuffResistMult[e] = 1f;
            Assert.Equal(0, Store.ApplyDebuffResistToDuration(e, 10));
            Assert.Equal(0, Store.ApplyDebuffResistToDuration(e, 1));
        }

        [Fact]
        public void Helper_HighResist_ReducesShortDebuffToZero()
        {
            int e = Enemy();
            Store.EnemyDebuffResistMult[e] = 0.99f;
            // duration=1 * 0.01 = 0.01 → cast to int = 0
            Assert.Equal(0, Store.ApplyDebuffResistToDuration(e, 1));
        }

        [Fact]
        public void Helper_NegativeDuration_ReturnsZero()
        {
            int e = Enemy();
            Store.EnemyDebuffResistMult[e] = 0.5f;
            Assert.Equal(0, Store.ApplyDebuffResistToDuration(e, 0));
            Assert.Equal(0, Store.ApplyDebuffResistToDuration(e, -5));
        }

        [Fact]
        public void Helper_DoesNotMutateDurationLeftFields()
        {
            int e = Enemy();
            Store.EnemyDebuffResistMult[e] = 0.5f;
            Store.ApplyDebuffResistToDuration(e, 10);
            // None of the debuff duration fields should be touched by the pure helper
            Assert.Equal(0f, Store.EnemyDisarmDurationLeft[e]);
            Assert.Equal(0f, Store.EnemyRootDurationLeft[e]);
            Assert.Equal(0f, Store.EnemyPolymorphDurationLeft[e]);
            Assert.Equal(0f, Store.EnemySlowDurationLeft[e]);
        }

        // ─── ApplyEnemyDisarm with resist ──────────────────────────────────

        [Fact]
        public void Disarm_HalfResist_HalvesDuration()
        {
            int e = Enemy();
            Store.EnemyDebuffResistMult[e] = 0.5f;
            Store.ApplyEnemyDisarm(e, 10);
            Assert.Equal(5f, Store.EnemyDisarmDurationLeft[e]);
        }

        [Fact]
        public void Disarm_FullResist_ZeroDurationNoOp()
        {
            int e = Enemy();
            Store.EnemyDebuffResistMult[e] = 1f;
            Store.ApplyEnemyDisarm(e, 5);
            // Fully resisted → duration becomes 0 → early-out → field stays 0
            Assert.Equal(0f, Store.EnemyDisarmDurationLeft[e]);
            Assert.False(Store.IsEnemyDisarmed(e));
        }

        [Fact]
        public void Disarm_ResistCombinesWithDisarmResistance()
        {
            int e = Enemy();
            Store.EnemyDisarmResistance[e] = 0.5f;  // first reduce by 50% (10 → 5)
            Store.EnemyDebuffResistMult[e] = 0.5f;   // then reduce by 50% (5 → 2)
            Store.ApplyEnemyDisarm(e, 10);
            Assert.Equal(2f, Store.EnemyDisarmDurationLeft[e]);
        }

        // ─── ApplyEnemyRoot with resist ────────────────────────────────────

        [Fact]
        public void Root_HalfResist_HalvesDuration()
        {
            int e = Enemy();
            Store.EnemyDebuffResistMult[e] = 0.5f;
            Store.ApplyEnemyRoot(e, 8);
            Assert.Equal(4f, Store.EnemyRootDurationLeft[e]);
        }

        // ─── ApplyPolymorph with resist ────────────────────────────────────

        [Fact]
        public void Polymorph_HalfResist_HalvesDuration()
        {
            int e = Enemy();
            Store.EnemyDebuffResistMult[e] = 0.5f;
            Store.ApplyPolymorph(e, 10, 1.5f);
            Assert.Equal(5f, Store.EnemyPolymorphDurationLeft[e]);
            // The damageTakenMultiplier should NOT be affected by resist
            Assert.Equal(1.5f, Store.EnemyPolymorphDamageTakenMultiplier[e]);
        }

        // ─── ApplySlow with resist (duration only, factor unaffected) ──────

        [Fact]
        public void Slow_HalfResist_HalvesDuration_FactorUnaffected()
        {
            int e = Enemy();
            Store.EnemyDebuffResistMult[e] = 0.5f;
            Store.ApplySlow(e, 0.4f, 10);
            // Duration halved by resist
            Assert.Equal(5f, Store.EnemySlowDurationLeft[e]);
            // Factor (slow severity) NOT changed by debuff resist
            Assert.Equal(0.4f, Store.EnemySlowFactor[e]);
        }
    }
}
