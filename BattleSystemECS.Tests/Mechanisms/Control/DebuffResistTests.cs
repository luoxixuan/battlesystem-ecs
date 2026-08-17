using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Tests.Infrastructure;

namespace BattleSystemECS.Tests.Mechanisms.Control
{
    /// <summary>
    /// Tests for Round 141 Direction 8 — Debuff Decay Resistance (EnemyDebuffResistMult):
    /// a per-enemy uniform duration scaler (0-1) applied to all debuff durations
    /// (root / disarm / polymorph / slow). Boss enemies get 0.5 → debuffs last 50% of base.
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

        [Theory(DisplayName = "默认 0 抗性时四类 debuff 持续时间均不缩减")]
        [InlineData(0)] // Disarm
        [InlineData(1)] // Root
        [InlineData(2)] // Polymorph
        [InlineData(3)] // Slow
        public void DefaultResist_AllDebuffDurationsUnchanged(int debuffKind)
        {
            int e = Enemy();
            switch (debuffKind)
            {
                case 0:
                    Store.ApplyEnemyDisarm(e, 5);
                    Assert.Equal(5f, Store.EnemyDisarmDurationLeft[e]);
                    break;
                case 1:
                    Store.ApplyEnemyRoot(e, 4);
                    Assert.Equal(4f, Store.EnemyRootDurationLeft[e]);
                    break;
                case 2:
                    Store.ApplyPolymorph(e, 6, 1.5f);
                    Assert.Equal(6f, Store.EnemyPolymorphDurationLeft[e]);
                    break;
                default:
                    Store.ApplySlow(e, 0.5f, 3);
                    Assert.Equal(3f, Store.EnemySlowDurationLeft[e]);
                    break;
            }
        }

        // ─── Pure helper: ApplyDebuffResistToDuration ───────────────────────

        [Theory(DisplayName = "ApplyDebuffResistToDuration 缩放语义")]
        [InlineData(0f, 10, 10)]   // 0 抗性 → 不变
        [InlineData(0f, 1, 1)]     // 0 抗性 → 不变
        [InlineData(0.5f, 10, 5)]  // 50% → 减半
        [InlineData(0.5f, 4, 2)]   // 4 * 0.5 = 2
        [InlineData(1f, 10, 0)]    // 100% → 免疫
        [InlineData(1f, 1, 0)]     // 100% → 免疫
        [InlineData(0.99f, 1, 0)]  // 1 * 0.01 = 0.01 → int 截断为 0
        [InlineData(0.5f, 0, 0)]   // 非正时长 → 0
        [InlineData(0.5f, -5, 0)]  // 负时长 → 0
        public void Helper_ScalesDurationWithoutMutatingFields(
            float resist, int duration, int expected)
        {
            int e = Enemy();
            Store.EnemyDebuffResistMult[e] = resist;

            Assert.Equal(expected, Store.ApplyDebuffResistToDuration(e, duration));

            // Pure helper 不得改写任何 debuff 持续时间字段。
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
