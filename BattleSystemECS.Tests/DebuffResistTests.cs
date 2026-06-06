using Xunit;
using BattleSystemECS.Core;

namespace BattleSystemECS.Tests
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
    public class DebuffResistTests
    {
        private static int SpawnPlainEnemy(ComponentStore store)
        {
            return store.AddEnemy(0, 0, 5f, 100f, 100f, 5f, 10, 1, "TestEnemy");
        }

        // ─── Default (no resist configured) — backward compat ───────────────

        [Fact]
        public void DefaultResist_ZeroOnAllEnemies()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            Assert.Equal(0f, store.EnemyDebuffResistMult[e]);
        }

        [Fact]
        public void DefaultResist_DisarmDurationUnchanged()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            store.ApplyEnemyDisarm(e, 5);
            Assert.Equal(5f, store.EnemyDisarmDurationLeft[e]);
        }

        [Fact]
        public void DefaultResist_RootDurationUnchanged()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            store.ApplyEnemyRoot(e, 4);
            Assert.Equal(4f, store.EnemyRootDurationLeft[e]);
        }

        [Fact]
        public void DefaultResist_PolymorphDurationUnchanged()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            store.ApplyPolymorph(e, 6, 1.5f);
            Assert.Equal(6f, store.EnemyPolymorphDurationLeft[e]);
        }

        [Fact]
        public void DefaultResist_SlowDurationUnchanged()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            store.ApplySlow(e, 0.5f, 3);
            Assert.Equal(3f, store.EnemySlowDurationLeft[e]);
        }

        // ─── Pure helper: ApplyDebuffResistToDuration ───────────────────────

        [Fact]
        public void Helper_ZeroResist_ReturnsUnchanged()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            store.EnemyDebuffResistMult[e] = 0f;
            Assert.Equal(10, store.ApplyDebuffResistToDuration(e, 10));
            Assert.Equal(1, store.ApplyDebuffResistToDuration(e, 1));
        }

        [Fact]
        public void Helper_HalfResist_HalvesDuration()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            store.EnemyDebuffResistMult[e] = 0.5f;
            Assert.Equal(5, store.ApplyDebuffResistToDuration(e, 10));
            Assert.Equal(2, store.ApplyDebuffResistToDuration(e, 4)); // 4*0.5 = 2.0
        }

        [Fact]
        public void Helper_FullResist_ReturnsZero()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            store.EnemyDebuffResistMult[e] = 1f;
            Assert.Equal(0, store.ApplyDebuffResistToDuration(e, 10));
            Assert.Equal(0, store.ApplyDebuffResistToDuration(e, 1));
        }

        [Fact]
        public void Helper_HighResist_ReducesShortDebuffToZero()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            store.EnemyDebuffResistMult[e] = 0.99f;
            // duration=1 * 0.01 = 0.01 → cast to int = 0
            Assert.Equal(0, store.ApplyDebuffResistToDuration(e, 1));
        }

        [Fact]
        public void Helper_NegativeDuration_ReturnsZero()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            store.EnemyDebuffResistMult[e] = 0.5f;
            Assert.Equal(0, store.ApplyDebuffResistToDuration(e, 0));
            Assert.Equal(0, store.ApplyDebuffResistToDuration(e, -5));
        }

        [Fact]
        public void Helper_DoesNotMutateDurationLeftFields()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            store.EnemyDebuffResistMult[e] = 0.5f;
            store.ApplyDebuffResistToDuration(e, 10);
            // None of the debuff duration fields should be touched by the pure helper
            Assert.Equal(0f, store.EnemyDisarmDurationLeft[e]);
            Assert.Equal(0f, store.EnemyRootDurationLeft[e]);
            Assert.Equal(0f, store.EnemyPolymorphDurationLeft[e]);
            Assert.Equal(0f, store.EnemySlowDurationLeft[e]);
        }

        // ─── ApplyEnemyDisarm with resist ──────────────────────────────────

        [Fact]
        public void Disarm_HalfResist_HalvesDuration()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            store.EnemyDebuffResistMult[e] = 0.5f;
            store.ApplyEnemyDisarm(e, 10);
            Assert.Equal(5f, store.EnemyDisarmDurationLeft[e]);
        }

        [Fact]
        public void Disarm_FullResist_ZeroDurationNoOp()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            store.EnemyDebuffResistMult[e] = 1f;
            store.ApplyEnemyDisarm(e, 5);
            // Fully resisted → duration becomes 0 → early-out → field stays 0
            Assert.Equal(0f, store.EnemyDisarmDurationLeft[e]);
            Assert.False(store.IsEnemyDisarmed(e));
        }

        [Fact]
        public void Disarm_ResistCombinesWithDisarmResistance()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            store.EnemyDisarmResistance[e] = 0.5f;  // first reduce by 50% (10 → 5)
            store.EnemyDebuffResistMult[e] = 0.5f;   // then reduce by 50% (5 → 2)
            store.ApplyEnemyDisarm(e, 10);
            Assert.Equal(2f, store.EnemyDisarmDurationLeft[e]);
        }

        // ─── ApplyEnemyRoot with resist ────────────────────────────────────

        [Fact]
        public void Root_HalfResist_HalvesDuration()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            store.EnemyDebuffResistMult[e] = 0.5f;
            store.ApplyEnemyRoot(e, 8);
            Assert.Equal(4f, store.EnemyRootDurationLeft[e]);
        }

        // ─── ApplyPolymorph with resist ────────────────────────────────────

        [Fact]
        public void Polymorph_HalfResist_HalvesDuration()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            store.EnemyDebuffResistMult[e] = 0.5f;
            store.ApplyPolymorph(e, 10, 1.5f);
            Assert.Equal(5f, store.EnemyPolymorphDurationLeft[e]);
            // The damageTakenMultiplier should NOT be affected by resist
            Assert.Equal(1.5f, store.EnemyPolymorphDamageTakenMultiplier[e]);
        }

        // ─── ApplySlow with resist (duration only, factor unaffected) ──────

        [Fact]
        public void Slow_HalfResist_HalvesDuration_FactorUnaffected()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            store.EnemyDebuffResistMult[e] = 0.5f;
            store.ApplySlow(e, 0.4f, 10);
            // Duration halved by resist
            Assert.Equal(5f, store.EnemySlowDurationLeft[e]);
            // Factor (slow severity) NOT changed by debuff resist
            Assert.Equal(0.4f, store.EnemySlowFactor[e]);
        }
    }
}
