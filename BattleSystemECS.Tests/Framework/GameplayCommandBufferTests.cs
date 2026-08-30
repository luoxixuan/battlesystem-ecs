using BattleSystemECS.Core.GAS;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class GameplayCommandBufferTests
    {
        [Fact]
        public void ProducerSequenceIsMonotonicPerProducer()
        {
            var sequence = new ProducerSequence(3);
            long first = sequence.Next();
            long second = sequence.Next();
            Assert.Equal(3, ProducerSequence.Producer(first));
            Assert.Equal(ProducerSequence.Local(first) + 1, ProducerSequence.Local(second));
        }

        [Fact]
        public void MergeSortIsIndependentOfSubmissionOrder()
        {
            var left = new CommandBuffer<GameplayEvent>(4);
            var right = new CommandBuffer<GameplayEvent>(4);
            var a = new GameplayEvent(GameplayEventType.DamageApplied, default(EntityHandle), new EntityHandle(2, 1), 2);
            var b = new GameplayEvent(GameplayEventType.HitConfirmed, default(EntityHandle), new EntityHandle(1, 1), 1);
            Assert.True(left.TryAdd(a));
            Assert.True(right.TryAdd(b));
            Assert.True(left.TryMerge(right, GameplayEventOrdering.Compare));
            Assert.Equal(b.Sequence, left.Get(0).Sequence);
            Assert.Equal(a.Sequence, left.Get(1).Sequence);
        }

        [Fact]
        public void CriticalCapacityIsDistinctAndDiagnosticsCanBeReset()
        {
            var sink = new CommandSink<GameplayEvent>(1, 0);
            var value = new GameplayEvent(GameplayEventType.HitConfirmed, default(EntityHandle), default(EntityHandle), 0);
            Assert.True(sink.Submit(value));
            Assert.False(sink.Submit(value, true));
            Assert.Equal(CommandRejection.CriticalCapacity, sink.LastRejection);
            Assert.Equal(1, sink.OverflowCount);
            sink.Clear();
            Assert.Equal(1, sink.OverflowCount);
            sink.ResetDiagnostics();
            Assert.Equal(0, sink.OverflowCount);
        }

        [Fact]
        public void MergeCapacityFailureIsAtomic()
        {
            var destination = new CommandBuffer<GameplayEvent>(2);
            var source = new CommandBuffer<GameplayEvent>(2);
            var value = new GameplayEvent(GameplayEventType.HitConfirmed, default(EntityHandle), default(EntityHandle), 0);
            Assert.True(destination.TryAdd(value));
            Assert.True(source.TryAdd(value));
            Assert.True(source.TryAdd(value));
            Assert.False(destination.TryMerge(source, GameplayEventOrdering.Compare));
            Assert.Equal(1, destination.Count);
            Assert.Equal(CommandRejection.Capacity, destination.LastRejection);
        }

        [Fact]
        public void EventOrderingUsesTypeEffectTargetAndSourceAsTieBreakers()
        {
            var queue = new GameplayEventQueue(4);
            var target = new EntityHandle(4, 1);
            var source = new EntityHandle(3, 1);
            Assert.True(queue.TryPublish(new GameplayEvent(GameplayEventType.DamageApplied, source, target, 7)));
            Assert.True(queue.TryPublish(new GameplayEvent(GameplayEventType.HitConfirmed, source, target, 7)));
            queue.Sort(GameplayEventOrdering.Compare);
            Assert.Equal(GameplayEventType.HitConfirmed, queue.Get(0).Type);
        }
    }
}
