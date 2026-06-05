using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Tests
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
    public class DisarmTests
    {
        private static int SpawnPlainEnemy(ComponentStore store)
        {
            return store.AddEnemy(0, 0, 5f, 100f, 100f, 5f, 10, 1, "TestEnemy");
        }

        // ─── Default (no disarm applied) — backward compat ───────────────

        [Fact]
        public void DefaultDisarm_NoDisarmApplied()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            Assert.Equal(0f, store.EnemyDisarmDurationLeft[e]);
            Assert.False(store.IsEnemyDisarmed(e));
        }

        // ─── ApplyDisarm sets duration and flips IsEnemyDisarmed ──────────

        [Fact]
        public void ApplyDisarm_SetsDuration_AndFlipsFlag()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);

            store.ApplyEnemyDisarm(e, 3);

            Assert.True(store.IsEnemyDisarmed(e));
            Assert.Equal(3f, store.EnemyDisarmDurationLeft[e]);
        }

        // ─── Refresh: longer duration wins ────────────────────────────────

        [Fact]
        public void ApplyDisarm_RefreshTakesLongerDuration()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);

            store.ApplyEnemyDisarm(e, 5);
            store.ApplyEnemyDisarm(e, 2); // shorter, should be ignored

            Assert.Equal(5f, store.EnemyDisarmDurationLeft[e]);
        }

        // ─── Unstoppable enemies ignore disarm ────────────────────────────

        [Fact]
        public void UnstoppableEnemy_IgnoresDisarm()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            store.EnemyIsUnstoppable[e] = true;

            store.ApplyEnemyDisarm(e, 5);

            Assert.False(store.IsEnemyDisarmed(e));
            Assert.Equal(0f, store.EnemyDisarmDurationLeft[e]);
        }

        // ─── Mask_Disarm blocks disarm ────────────────────────────────────

        [Fact]
        public void MaskDisarm_BlocksDisarm()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            store.SetCCImmuneBit(e, CCImmunityConfig.Mask_Disarm);

            store.ApplyEnemyDisarm(e, 5);

            Assert.False(store.IsEnemyDisarmed(e));
            Assert.Equal(0f, store.EnemyDisarmDurationLeft[e]);
        }

        // ─── Disarm resistance reduces duration ───────────────────────────

        [Fact]
        public void DisarmResistance_ReducesDuration()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            store.EnemyDisarmResistance[e] = 0.5f; // 50% reduction

            store.ApplyEnemyDisarm(e, 4);

            // 4 * (1 - 0.5) = 2 turns
            Assert.Equal(2f, store.EnemyDisarmDurationLeft[e]);
            Assert.True(store.IsEnemyDisarmed(e));
        }

        // ─── Full disarm resistance is a no-op ────────────────────────────

        [Fact]
        public void FullDisarmResistance_NoOp()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            store.EnemyDisarmResistance[e] = 1.0f;

            store.ApplyEnemyDisarm(e, 4);

            Assert.False(store.IsEnemyDisarmed(e));
            Assert.Equal(0f, store.EnemyDisarmDurationLeft[e]);
        }

        // ─── Zero duration is a no-op ─────────────────────────────────────

        [Fact]
        public void ZeroDuration_IsNoOp()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);

            store.ApplyEnemyDisarm(e, 0);

            Assert.False(store.IsEnemyDisarmed(e));
            Assert.Equal(0f, store.EnemyDisarmDurationLeft[e]);
        }

        // ─── Invalid enemy id is a no-op ──────────────────────────────────

        [Fact]
        public void InvalidEnemyId_IsNoOp()
        {
            var store = new ComponentStore();

            // Negative id should be silently ignored (no exception)
            store.ApplyEnemyDisarm(-1, 5);

            // After invalid id, no crash, no field touched on real entities
            int e = SpawnPlainEnemy(store);
            Assert.False(store.IsEnemyDisarmed(e));
        }

        // ─── Disarm does not affect stun/slow (orthogonal CC) ─────────────

        [Fact]
        public void Disarm_DoesNotInterfereWithStunOrSlow()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);

            store.ApplyEnemyDisarm(e, 3);
            store.ApplyEnemyStun(e, 2);
            store.ApplyEnemySlow(e, 0.5f, 5);

            // All three CCs are independent
            Assert.True(store.IsEnemyDisarmed(e));
            Assert.True(store.IsEnemyStunned(e));
            Assert.Equal(0.5f, store.EnemySlowFactor[e]);
        }

        // ─── ComponentStore with disarm fields can be constructed and disposed ─

        [Fact]
        public void ComponentStore_DisarmFields_ConstructAndDispose()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);

            // Verify the array exists and is the right size (ComponentStore.MAX_ENTITIES)
            Assert.NotNull(store.EnemyDisarmDurationLeft);
            Assert.Equal(ComponentStore.MAX_ENTITIES, store.EnemyDisarmDurationLeft.Length);

            store.ApplyEnemyDisarm(e, 2);
            Assert.True(store.IsEnemyDisarmed(e));

            store.Dispose();
        }
    }
}
