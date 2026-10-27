using System;
using System.Reflection;
using System.Threading;
using BattleSystemECS.Core.GAS;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    /// <summary>
    /// GetSerialRef 串行前提：Debug 构建下与持有队列锁的并发发布者重叠时抛出，无并发时正常按引用读取。
    /// </summary>
    public sealed class GameplayEventQueueSerialReadTests
    {
        [Fact]
        public void SerialReadReturnsPublishedEventWithoutConcurrentPublisher()
        {
            var queue = new GameplayEventQueue(4);
            Assert.True(queue.TryPublish(new GameplayEvent(GameplayEventType.DamageApplied, default, default, 7L)));
            Assert.Equal(GameplayEventType.DamageApplied, queue.GetSerialRef(0).Type);
            Assert.Equal(7L, queue.GetSerialRef(0).Sequence);
        }

#if DEBUG
        [Fact]
        public void SerialReadThrowsWhileAnotherThreadHoldsQueueLock()
        {
            var queue = new GameplayEventQueue(4);
            Assert.True(queue.TryPublish(new GameplayEvent(GameplayEventType.HitConfirmed, default, default, 1L)));
            // 模拟正在发布的另一线程：持有队列内部锁（测试经反射取得私有锁对象）
            object sync = typeof(GameplayEventQueue).GetField("_sync", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(queue)!;
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var publisher = new Thread(() => { lock (sync) { held.Set(); release.Wait(); } });
            publisher.Start();
            held.Wait();
            try
            {
                Assert.Throws<InvalidOperationException>(() => { var e = queue.GetSerialRef(0); _ = e.Type; });
            }
            finally
            {
                release.Set();
                publisher.Join();
            }
            Assert.Equal(GameplayEventType.HitConfirmed, queue.GetSerialRef(0).Type);
        }
#endif
    }
}
