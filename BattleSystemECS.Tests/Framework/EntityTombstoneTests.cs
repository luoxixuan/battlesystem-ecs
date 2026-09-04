using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class EntityTombstoneTests
    {
        [Fact]
        public void QueryEntityTombstoneDistinguishesNeverExistedDeadAliveAndPending()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 1f, 1f, 1f, 1);
            var neverAllocated = new EntityHandle(50_000, 1);
            Assert.Equal(EntityTombstone.NeverExisted, store.QueryEntityTombstone(neverAllocated));
            Assert.Equal(EntityTombstone.NeverExisted, store.QueryEntityTombstone(default));

            int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            var live = store.GetEntityHandle(enemy);
            Assert.Equal(EntityTombstone.Alive, store.QueryEntityTombstone(live));

            store.QueueEnemyDeath(enemy, 0);
            Assert.Equal(EntityTombstone.PendingDeath, store.QueryEntityTombstone(live));
            Assert.False(store.TryResolve(live, out _, out var pendingReason));
            Assert.Equal(HandleResolveFailure.Inactive, pendingReason);

            store.DestroyEntity(enemy);
            Assert.Equal(EntityTombstone.Dead, store.QueryEntityTombstone(live));

            int recycled = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            Assert.Equal(enemy, recycled);
            var fresh = store.GetEntityHandle(recycled);
            Assert.Equal(EntityTombstone.Dead, store.QueryEntityTombstone(live));
            Assert.Equal(EntityTombstone.Alive, store.QueryEntityTombstone(fresh));
            Assert.False(store.TryResolve(live, out _, out var staleReason));
            Assert.Equal(HandleResolveFailure.StaleGeneration, staleReason);
        }

        [Fact]
        public void StaleGenerationDamageIsDiscardedAndDiagnosed()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 3f, 1f, 1f, 1);
            int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            var stale = store.GetEntityHandle(enemy);
            store.DestroyEntity(enemy);
            int recycled = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            Assert.Equal(enemy, recycled);
            long before = store.DamageResolver.StaleHandleRejectedCount;
            var result = store.DamageResolver.TryApplyValidated(
                new DamageRequest(store.GetEntityHandle(0), stale, 1f, DamageType.True, 12L, ownerPlayerId: 0));
            Assert.False(result.Accepted);
            Assert.Equal(DamageRejectionReason.InvalidTarget, result.Reason);
            Assert.Equal(10f, store.EnemyHealth[recycled]);
            Assert.Equal(before + 1, store.DamageResolver.StaleHandleRejectedCount);
        }
    }
}
