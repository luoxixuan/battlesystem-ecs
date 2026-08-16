using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Tests.Infrastructure;

namespace BattleSystemECS.Tests.Mechanisms.Control
{
    /// <summary>
    /// Tests for Round 124 — Disarm CC: prevents enemies from casting abilities while
    /// preserving movement and basic attacks. Verifies:
    ///   - Default behavior: EnemyDisarmDurationLeft = 0, IsEnemyDisarmed() = false
    ///   - ApplyEnemyDisarm sets duration; IsEnemyDisarmed() = true while > 0
    ///   - Refresh semantics: longer duration wins, shorter duration does not shorten
    ///   - EnemyIsUnstoppable blocks disarm (no-op)
    ///   - Per-type CC immunity (CCImmunityConfig.Mask_Disarm) blocks disarm
    ///   - EnemyDisarmResistance reduces duration (and shortens to 0 → no-op)
    ///   - Disarm is orthogonal to stun / slow (independent bit fields)
    ///   - ComponentStore can be constructed, the disarm field allocated to MAX_ENTITIES,
    ///     and disposed without crash
    /// </summary>
    public class DisarmTests : BattleTestBase
    {
        // ─── Default (no disarm applied) — backward compat ───────────────

        [Fact]
        public void DefaultDisarm_NoDisarmApplied()
        {
            int e = Enemy();
            Assert.Equal(0f, Store.EnemyDisarmDurationLeft[e]);
            Assert.False(Store.IsEnemyDisarmed(e));
        }

        // ─── ApplyDisarm sets duration and flips IsEnemyDisarmed ──────────

        [Fact]
        public void ApplyDisarm_SetsDuration_AndFlipsFlag()
        {
            int e = Enemy();

            Store.ApplyEnemyDisarm(e, 3);

            Assert.True(Store.IsEnemyDisarmed(e));
            Assert.Equal(3f, Store.EnemyDisarmDurationLeft[e]);
        }

        // ─── Refresh: longer duration wins ────────────────────────────────

        [Fact]
        public void ApplyDisarm_RefreshTakesLongerDuration()
        {
            int e = Enemy();

            Store.ApplyEnemyDisarm(e, 5);
            Store.ApplyEnemyDisarm(e, 2); // shorter, should be ignored

            Assert.Equal(5f, Store.EnemyDisarmDurationLeft[e]);
        }

        // ─── Unstoppable enemies ignore disarm ────────────────────────────

        [Fact]
        public void UnstoppableEnemy_IgnoresDisarm()
        {
            int e = Enemy();
            Store.EnemyIsUnstoppable[e] = true;

            Store.ApplyEnemyDisarm(e, 5);

            Assert.False(Store.IsEnemyDisarmed(e));
            Assert.Equal(0f, Store.EnemyDisarmDurationLeft[e]);
        }

        // ─── Mask_Disarm blocks disarm ────────────────────────────────────

        [Fact]
        public void MaskDisarm_BlocksDisarm()
        {
            int e = Enemy();
            Store.SetCCImmuneBit(e, CCImmunityConfig.Mask_Disarm);

            Store.ApplyEnemyDisarm(e, 5);

            Assert.False(Store.IsEnemyDisarmed(e));
            Assert.Equal(0f, Store.EnemyDisarmDurationLeft[e]);
        }

        // ─── Disarm resistance reduces duration ───────────────────────────

        [Fact]
        public void DisarmResistance_ReducesDuration()
        {
            int e = Enemy();
            Store.EnemyDisarmResistance[e] = 0.5f; // 50% reduction

            Store.ApplyEnemyDisarm(e, 4);

            // 4 * (1 - 0.5) = 2 turns
            Assert.Equal(2f, Store.EnemyDisarmDurationLeft[e]);
            Assert.True(Store.IsEnemyDisarmed(e));
        }

        // ─── Full disarm resistance is a no-op ────────────────────────────

        [Fact]
        public void FullDisarmResistance_NoOp()
        {
            int e = Enemy();
            Store.EnemyDisarmResistance[e] = 1.0f;

            Store.ApplyEnemyDisarm(e, 4);

            Assert.False(Store.IsEnemyDisarmed(e));
            Assert.Equal(0f, Store.EnemyDisarmDurationLeft[e]);
        }

        // ─── Zero duration is a no-op ─────────────────────────────────────

        [Fact]
        public void ZeroDuration_IsNoOp()
        {
            int e = Enemy();

            Store.ApplyEnemyDisarm(e, 0);

            Assert.False(Store.IsEnemyDisarmed(e));
            Assert.Equal(0f, Store.EnemyDisarmDurationLeft[e]);
        }

        // ─── Invalid enemy id is a no-op ──────────────────────────────────

        [Fact]
        public void InvalidEnemyId_IsNoOp()
        {
            // Negative id should be silently ignored (no exception)
            Store.ApplyEnemyDisarm(-1, 5);

            // After invalid id, no crash, no field touched on real entities
            int e = Enemy();
            Assert.False(Store.IsEnemyDisarmed(e));
        }

        // ─── Disarm does not affect stun/slow (orthogonal CC) ─────────────

        [Fact]
        public void Disarm_DoesNotInterfereWithStunOrSlow()
        {
            int e = Enemy();

            Store.ApplyEnemyDisarm(e, 3);
            Store.ApplyEnemyStun(e, 2);
            Store.ApplyEnemySlow(e, 0.5f, 5);

            // All three CCs are independent
            Assert.True(Store.IsEnemyDisarmed(e));
            Assert.True(Store.IsEnemyStunned(e));
            Assert.Equal(0.5f, Store.EnemySlowFactor[e]);
        }

        // ─── ComponentStore with disarm fields can be constructed and disposed ─

        [Fact]
        public void ComponentStore_DisarmFields_ConstructAndDispose()
        {
            int e = Enemy();

            // Verify the array exists and is the right size (ComponentStore.MAX_ENTITIES)
            Assert.NotNull(Store.EnemyDisarmDurationLeft);
            Assert.Equal(ComponentStore.MAX_ENTITIES, Store.EnemyDisarmDurationLeft.Length);

            Store.ApplyEnemyDisarm(e, 2);
            Assert.True(Store.IsEnemyDisarmed(e));

            Store.Dispose();
        }
    }
}
