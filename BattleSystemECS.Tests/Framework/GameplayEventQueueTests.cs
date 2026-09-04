using BattleSystemECS.Core.GAS;
using BattleSystemECS.Core;
using BattleSystemECS.Components;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class GameplayEventQueueTests
    {
        [Fact]
        public void ReservesCriticalCapacityAndClearsForRecovery()
        {
            var queue = new GameplayEventQueue(2, 1);
            var value = new GameplayEvent(GameplayEventType.HitConfirmed, default(EntityHandle), default(EntityHandle), 1);
            Assert.True(queue.TryPublish(value));
            Assert.Equal(1, queue.PublishedCount);
            Assert.False(queue.TryPublish(value));
            Assert.True(queue.TryPublish(value, true));
            Assert.Equal(2, queue.PublishedCount);
            queue.Clear();
            Assert.Equal(0, queue.Count);
            Assert.True(queue.TryPublish(value));
        }

        [Fact]
        public void MergeRejectsNullAndClearRestoresCapacity()
        {
            var queue = new GameplayEventQueue(1);
            var value = new GameplayEvent(GameplayEventType.HitConfirmed, default(EntityHandle), default(EntityHandle), 1);
            Assert.Throws<System.ArgumentNullException>(() => queue.TryMerge(null, GameplayEventOrdering.Compare));
            Assert.True(queue.TryPublish(value));
            Assert.False(queue.TryPublish(value));
            queue.Clear();
            Assert.True(queue.TryPublish(value));
            Assert.Equal(1, queue.OverflowCount);
        }

        [Fact]
        public void DigestIncludesSemanticPayloadAndStableMergeOrder()
        {
            var first = new GameplayEvent(GameplayEventType.DamageApplied, default(EntityHandle),
                default(EntityHandle), default(EffectHandle), default(EffectId), 1L,
                producerIndex: 2, flags: DamageFlags.IgnoreArmor, tag: new TagId(3));
            var second = new GameplayEvent(GameplayEventType.HitConfirmed, default(EntityHandle),
                default(EntityHandle), default(EffectHandle), default(EffectId), 2L,
                producerIndex: 1, flags: DamageFlags.None, tag: new TagId(1));
            var reverseSource = new GameplayEventQueue(4);
            var sortedSource = new GameplayEventQueue(4);
            Assert.True(reverseSource.TryPublish(second));
            Assert.True(reverseSource.TryPublish(first));
            Assert.True(sortedSource.TryPublish(first));
            Assert.True(sortedSource.TryPublish(second));

            var reverseDestination = new GameplayEventQueue(4);
            var sortedDestination = new GameplayEventQueue(4);
            reverseDestination.DigestEnabled = true;
            sortedDestination.DigestEnabled = true;
            Assert.True(reverseDestination.TryMerge(reverseSource, GameplayEventOrdering.Compare));
            Assert.True(sortedDestination.TryMerge(sortedSource, GameplayEventOrdering.Compare));
            // 源队列先按 sequence/entity 归并再累计 digest，不依赖发布运气。
            Assert.Equal(sortedDestination.SequenceDigest, reverseDestination.SequenceDigest);
            Assert.Equal(2, reverseDestination.PublishedCount);

            var preexisting = new GameplayEventQueue(4);
            var preexistingExpected = new GameplayEventQueue(4);
            preexisting.DigestEnabled = true;
            preexistingExpected.DigestEnabled = true;
            Assert.True(preexisting.TryPublish(second));
            Assert.True(preexistingExpected.TryPublish(second));
            Assert.True(preexistingExpected.TryPublish(first));
            var oneItemSource = new GameplayEventQueue(4);
            Assert.True(oneItemSource.TryPublish(first));
            Assert.True(preexisting.TryMerge(oneItemSource, GameplayEventOrdering.Compare));
            Assert.Equal(preexistingExpected.SequenceDigest, preexisting.SequenceDigest);
            Assert.Equal(2, preexisting.PublishedCount);

            var payloadSource = new GameplayEventQueue(4);
            Assert.True(payloadSource.TryPublish(new GameplayEvent(GameplayEventType.DamageApplied,
                default(EntityHandle), default(EntityHandle), default(EffectHandle), default(EffectId),
                1L, producerIndex: 2, flags: DamageFlags.IgnoreShield, tag: new TagId(3))));
            Assert.True(payloadSource.TryPublish(second));
            var payloadDestination = new GameplayEventQueue(4);
            payloadDestination.DigestEnabled = true;
            Assert.True(payloadDestination.TryMerge(payloadSource, GameplayEventOrdering.Compare));
            Assert.NotEqual(sortedDestination.SequenceDigest, payloadDestination.SequenceDigest);
        }

        [Fact]
        public void PublicationCountIsIndependentFromDigestOptIn()
        {
            var queue = new GameplayEventQueue(2);
            var value = new GameplayEvent(GameplayEventType.HitConfirmed, default(EntityHandle), default(EntityHandle), 7L);
            ulong initialDigest = queue.SequenceDigest;
            Assert.True(queue.TryPublish(value));
            Assert.Equal(1, queue.PublishedCount);
            Assert.Equal(initialDigest, queue.SequenceDigest);
            queue.DigestEnabled = true;
            Assert.True(queue.TryPublish(value));
            Assert.Equal(2, queue.PublishedCount);
            Assert.NotEqual(initialDigest, queue.SequenceDigest);
        }

        [Fact]
        public void DigestCountsOnlyMutationNotInitialOrReplay()
        {
            var mutation = new GameplayEvent(GameplayEventType.EffectApplied, default(EntityHandle),
                default(EntityHandle), 1L);
            var initial = mutation.WithCause(GameplayEventCause.Initial);
            var replay = mutation.WithCause(GameplayEventCause.Replay);
            Assert.Equal(0, GameplayEventOrdering.Compare(mutation, initial));
            Assert.Equal(0, GameplayEventOrdering.Compare(mutation, replay));

            var mutationsOnly = new GameplayEventQueue(8) { DigestEnabled = true };
            Assert.True(mutationsOnly.TryPublish(mutation));
            ulong afterMutation = mutationsOnly.SequenceDigest;

            var withSnapshots = new GameplayEventQueue(8) { DigestEnabled = true };
            Assert.True(withSnapshots.TryPublish(mutation));
            Assert.True(withSnapshots.TryPublish(initial));
            Assert.True(withSnapshots.TryPublish(replay));
            Assert.Equal(afterMutation, withSnapshots.SequenceDigest);
            Assert.Equal(3, withSnapshots.PublishedCount);

            var doubleMutation = new GameplayEventQueue(8) { DigestEnabled = true };
            Assert.True(doubleMutation.TryPublish(mutation));
            Assert.True(doubleMutation.TryPublish(mutation));
            Assert.NotEqual(afterMutation, doubleMutation.SequenceDigest);
        }

        [Fact]
        public async Task ConcurrentSinglePublicationKeepsCountAndDigestAccountingAtomic()
        {
            var queue = new GameplayEventQueue(512);
            var start = new Barrier(5);
            var tasks = new Task[4];
            for (int worker = 0; worker < tasks.Length; worker++)
            {
                int producer = worker;
                tasks[worker] = Task.Run(() =>
                {
                    start.SignalAndWait();
                    for (int i = 0; i < 100; i++)
                        Assert.True(queue.TryPublish(new GameplayEvent(GameplayEventType.HitConfirmed,
                            default(EntityHandle), default(EntityHandle), producer * 100L + i)));
                });
            }
            start.SignalAndWait();
            await Task.WhenAll(tasks);
            Assert.Equal(400, queue.Count);
            Assert.Equal(400, queue.PublishedCount);
            Assert.Equal(0, queue.OverflowCount);
        }

        [Fact]
        public void SuccessfulDamageAndResourceCommitsAllocateNoManagedBytesAfterWarmup()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 100f, 1f, 1f, 1);
                store.PlayerMaxMana[0] = 100000f;
                int enemy = store.AddEnemy(0, 0, 1f, 100000f, 100000f, 1f, 1, 1);
                var source = store.GetEntityHandle(0);
                var target = store.GetEntityHandle(enemy);
                var damage = new DamageRequest(source, target, 1f, DamageType.True, 1L, ownerPlayerId: 0);
                var resource = new ResourceRequest(source, source, new AttributeKey(7), 1f, 2L, ownerPlayerId: 0);
                var shield = new ShieldRequest(source, source, 1f, 1f, ClockId.Combat, 3L);

                for (int i = 0; i < 32; i++)
                {
                    Assert.True(store.DamageResolver.TryApply(damage).Accepted);
                    Assert.True(store.ResourceResolver.TryApply(resource).Accepted);
                    Assert.True(store.ResourceResolver.TryApply(shield, 0).Accepted);
                    store.DamageResolver.Events.Clear();
                    store.ResourceResolver.Events.Clear();
                }

                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 256; i++)
                {
                    store.DamageResolver.TryApply(damage);
                    store.ResourceResolver.TryApply(resource);
                    store.ResourceResolver.TryApply(shield, 0);
                    store.DamageResolver.Events.Clear();
                    store.ResourceResolver.Events.Clear();
                }
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                Assert.Equal(0, allocated);
            }
        }

        [Fact]
        public void AtomicReservationRentFailureReturnsFirstBufferAndLeaksNoCapacity()
        {
            var firstPool = new TrackingBufferPool();
            var secondPool = new TrackingBufferPool(throwOnRent: true);
            var first = new GameplayEventQueue(1, 0, firstPool);
            var second = new GameplayEventQueue(1, 0, secondPool);

            Assert.Throws<InvalidOperationException>(() => GameplayEventQueue.TryReserveAtomic(first, 1, second, 1));

            Assert.Equal(1, firstPool.RentCount);
            Assert.Equal(1, firstPool.ReturnCount);
            Assert.True(firstPool.LastClearRequested);
            Assert.True(first.TryPublish(default(GameplayEvent), true));
            Assert.True(second.TryPublish(default(GameplayEvent), true));
        }

        [Fact]
        public void AtomicReservationCommitAndDisposeAreIdempotent()
        {
            var firstPool = new TrackingBufferPool();
            var secondPool = new TrackingBufferPool();
            var first = new GameplayEventQueue(2, 0, firstPool);
            var second = new GameplayEventQueue(2, 0, secondPool);
            var reservation = GameplayEventQueue.TryReserveAtomic(first, 1, second, 1);
            Assert.NotNull(reservation);
            reservation!.StageFirst(default(GameplayEvent));
            reservation.StageSecond(default(GameplayEvent));

            reservation.Commit();
            reservation.Commit();
            reservation.Dispose();

            Assert.Equal(1, first.Count);
            Assert.Equal(1, second.Count);
            Assert.Equal(1, firstPool.ReturnCount);
            Assert.Equal(1, secondPool.ReturnCount);
            Assert.True(firstPool.LastClearRequested);
            Assert.True(secondPool.LastClearRequested);
            Assert.True(first.TryPublish(default(GameplayEvent), true));
            Assert.True(second.TryPublish(default(GameplayEvent), true));
        }

        [Fact]
        public void LockOrderIsStableForReverseAndCollisionPaths()
        {
            var first = new GameplayEventQueue(1);
            var second = new GameplayEventQueue(1);
            GameplayEventQueue.GetLockOrder(first, 1, second, 2,
                out object forwardFirst, out object forwardSecond, out bool forwardTie);
            GameplayEventQueue.GetLockOrder(second, 2, first, 1,
                out object reverseFirst, out object reverseSecond, out bool reverseTie);
            GameplayEventQueue.GetLockOrder(first, 7, second, 7,
                out object tieFirst, out object tieSecond, out bool tie);

            Assert.False(forwardTie);
            Assert.False(reverseTie);
            Assert.Same(forwardFirst, reverseFirst);
            Assert.Same(forwardSecond, reverseSecond);
            Assert.True(tie);
            Assert.NotSame(tieFirst, tieSecond);
        }

        [Fact]
        public async Task OppositeDirectionMergesUseOneLockOrderAndSameQueueIsRejected()
        {
            var first = new GameplayEventQueue(8);
            var second = new GameplayEventQueue(8);
            Assert.True(first.TryPublish(default(GameplayEvent)));
            Assert.True(second.TryPublish(default(GameplayEvent)));
            Assert.Throws<ArgumentException>(() => first.TryMerge(first, GameplayEventOrdering.Compare));

            var start = new Barrier(3);
            Task left = Task.Run(() => { start.SignalAndWait(); first.TryMerge(second, GameplayEventOrdering.Compare); });
            Task right = Task.Run(() => { start.SignalAndWait(); second.TryMerge(first, GameplayEventOrdering.Compare); });
            start.SignalAndWait();

            await Task.WhenAll(left, right).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(first.Count > 0);
            Assert.True(second.Count > 0);
        }

        [Fact]
        public void FixedArityBatchesPreserveOrderCountAndDigestForOneTwoAndThreeFacts()
        {
            var actual = new GameplayEventQueue(6) { DigestEnabled = true };
            var expected = new GameplayEventQueue(6) { DigestEnabled = true };
            var facts = new GameplayEvent[6];
            for (int i = 0; i < facts.Length; i++)
            {
                facts[i] = new GameplayEvent(GameplayEventType.HitConfirmed,
                    default(EntityHandle), default(EntityHandle), i + 1L);
                Assert.True(expected.TryPublish(facts[i], true));
            }

            Assert.True(actual.TryPublishBatch(facts[0], true));
            Assert.True(actual.TryPublishBatch(facts[1], facts[2], true));
            Assert.True(actual.TryPublishBatch(facts[3], facts[4], facts[5], true));

            Assert.Equal(6, actual.Count);
            Assert.Equal(6, actual.PublishedCount);
            Assert.Equal(expected.SequenceDigest, actual.SequenceDigest);
            for (int i = 0; i < facts.Length; i++) Assert.Equal(i + 1L, actual.Get(i).Sequence);
        }

        private sealed class TrackingBufferPool : GameplayEventQueue.IBufferPool
        {
            private readonly bool _throwOnRent;
            internal TrackingBufferPool(bool throwOnRent = false) { _throwOnRent = throwOnRent; }
            internal int RentCount { get; private set; }
            internal int ReturnCount { get; private set; }
            internal bool LastClearRequested { get; private set; }
            public GameplayEvent[] Rent(int minimumLength)
            {
                RentCount++;
                if (_throwOnRent) throw new InvalidOperationException("controlled rent failure");
                return new GameplayEvent[minimumLength];
            }
            public void Return(GameplayEvent[] buffer, bool clearArray)
            {
                ReturnCount++;
                LastClearRequested = clearArray;
                if (clearArray) Array.Clear(buffer, 0, buffer.Length);
            }
        }
    }
}
