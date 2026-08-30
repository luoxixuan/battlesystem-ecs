using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class EffectPoolTests
    {
        [Fact]
        public void RejectsStaleHandleAfterReleaseAndReallocation()
        {
            var pool = new EffectPool(1);
            Assert.True(pool.TryAllocate(out var oldHandle));
            Assert.True(pool.Release(oldHandle));
            Assert.True(pool.TryAllocate(out var newHandle));
            Assert.False(pool.TryResolve(oldHandle, out _));
            Assert.True(pool.TryResolve(newHandle, out _));
        }

        [Fact]
        public void ResolveReportsInvalidInactiveAndStaleReasons()
        {
            var pool = new EffectPool(1);
            Assert.False(pool.TryResolve(new EffectHandle(-1, 1), out _, out var invalid));
            Assert.Equal(HandleResolveFailure.InvalidIndex, invalid);
            Assert.True(pool.TryAllocate(out var handle));
            Assert.True(pool.Release(handle));
            Assert.False(pool.TryResolve(handle, out _, out var inactive));
            Assert.Equal(HandleResolveFailure.Inactive, inactive);
            Assert.True(pool.TryAllocate(out var replacement));
            Assert.False(pool.TryResolve(handle, out _, out var stale));
            Assert.Equal(HandleResolveFailure.StaleGeneration, stale);
            Assert.True(pool.TryResolve(replacement, out _));
        }

        [Fact]
        public void AllocationFailureReportsCapacityRatherThanInactiveHandle()
        {
            var pool = new EffectPool(1);
            Assert.True(pool.TryAllocate(out _));
            Assert.False(pool.TryAllocate(out _));
            Assert.Equal(HandleResolveFailure.Capacity, pool.LastFailure);
            Assert.Equal(EffectPoolFailure.Capacity, pool.LastPoolFailure);
        }
    }
}
