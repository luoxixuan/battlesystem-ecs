using BattleSystemECS.Core.GAS;
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
            Assert.False(queue.TryPublish(value));
            Assert.True(queue.TryPublish(value, true));
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
    }
}
