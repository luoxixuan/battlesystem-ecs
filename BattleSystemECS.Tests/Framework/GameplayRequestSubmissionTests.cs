using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class GameplayRequestSubmissionTests
    {
        [Fact]
        public void ValidRequestCommitsOneFactWithSameSequence()
        {
            var store = new ComponentStore();
            int source = store.AddEnemy(1, 1, 1, 10, 10, 1, 1, 1);
            int target = store.AddEnemy(2, 1, 1, 10, 10, 1, 1, 1);
            var queue = new GameplayEventQueue(4, 1);
            var session = new GameplayRequestSubmissionSession();
            var request = new DamageRequest(store.GetEntityHandle(source), store.GetEntityHandle(target), 2, DamageType.Physical, 42);
            Assert.True(session.TrySubmit(request, store, queue, out var fact));
            Assert.Equal(1, queue.Count);
            Assert.Equal(request.Sequence, fact.Sequence);
            Assert.Equal(GameplayEventType.DamageApplied, fact.Type);
        }

        [Fact]
        public void StaleRequestProducesOneRejectionWithoutStateMutation()
        {
            var store = new ComponentStore();
            int source = store.AddEnemy(1, 1, 1, 10, 10, 1, 1, 1);
            int target = store.AddEnemy(2, 1, 1, 10, 10, 1, 1, 1);
            var stale = store.GetEntityHandle(target);
            store.DestroyEntity(target);
            var queue = new GameplayEventQueue(4, 1);
            var session = new GameplayRequestSubmissionSession();
            var request = new DamageRequest(store.GetEntityHandle(source), stale, 2, DamageType.Physical, 77);
            Assert.False(session.TrySubmit(request, store, queue, out _));
            Assert.False(session.TrySubmit(request, store, queue, out _));
            Assert.Equal(1, queue.Count);
            Assert.Equal(GameplayEventType.DamageBlocked, queue.Get(0).Type);
            Assert.Equal(0, store.GetEffectCount(source));
        }

        [Fact]
        public void RejectionDeduplicationIsSessionScopedAndDifferentSequencesRemainObservable()
        {
            var store = new ComponentStore();
            int source = store.AddEnemy(1, 1, 1, 10, 10, 1, 1, 1);
            var queue = new GameplayEventQueue(8, 2);
            var first = new GameplayRequestSubmissionSession();
            var second = new GameplayRequestSubmissionSession();
            var invalid = new DamageRequest(store.GetEntityHandle(source), new EntityHandle(-1, 1), 1, DamageType.Physical, 9);
            Assert.False(first.TrySubmit(invalid, store, queue, out var a));
            Assert.False(first.TrySubmit(invalid, store, queue, out _));
            Assert.False(second.TrySubmit(invalid, store, queue, out var b));
            Assert.Equal(2, queue.Count);
            Assert.Equal(a.Sequence, b.Sequence);
        }
    }
}
