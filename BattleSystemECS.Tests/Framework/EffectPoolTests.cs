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
            Assert.Equal(1, pool.ActiveCount);
            Assert.Equal(1, pool.PeakActiveCount);
            Assert.True(pool.Release(oldHandle));
            Assert.Equal(0, pool.ActiveCount);
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
            Assert.Equal(1, pool.PeakActiveCount);
            pool.ResetDiagnostics();
            Assert.Equal(1, pool.PeakActiveCount);
            Assert.Equal(0, pool.AllocationFailures);
        }

        [Fact]
        public void CapacityOneCannotAllocatePastCapacityAfterRelease()
        {
            var pool = new EffectPool(1);
            Assert.True(pool.TryAllocate(out var first));
            Assert.True(pool.Release(first));
            Assert.True(pool.TryAllocate(out var second));
            Assert.False(pool.TryAllocate(out _));
            Assert.Equal(1, pool.ActiveCount);
            Assert.Equal(1, pool.Capacity);
        }

        [Fact]
        public void LargeLogicalCapacityAllocatesPagesOnlyWhenUsed()
        {
            var pool = new EffectPool(800000);
            Assert.Equal(0, pool.AllocatedPageCount);
            Assert.Equal(0, pool.AllocatedSlotCapacity);
            Assert.Equal(800000, pool.FreeCount);

            Assert.True(pool.TryAllocate(out _));

            Assert.Equal(1, pool.AllocatedPageCount);
            Assert.Equal(256, pool.AllocatedSlotCapacity);
            Assert.Equal(799999, pool.FreeCount);
        }

        [Fact]
        public void AllocationCrossesPageAndReusesReleasedIndexWithNewGeneration()
        {
            var pool = new EffectPool(257);
            var handles = new EffectHandle[257];
            for (int i = 0; i < handles.Length; i++)
                Assert.True(pool.TryAllocate(out handles[i]));
            Assert.Equal(2, pool.AllocatedPageCount);
            Assert.Equal(257, pool.AllocatedSlotCapacity);
            Assert.Equal(0, pool.FreeCount);
            Assert.False(pool.TryAllocate(out _));

            EffectHandle released = handles[128];
            Assert.True(pool.Release(released));
            Assert.True(pool.TryAllocate(out EffectHandle replacement));

            Assert.Equal(released.Index, replacement.Index);
            Assert.NotEqual(released.Generation, replacement.Generation);
            Assert.False(pool.TryResolve(released, out _, out HandleResolveFailure stale));
            Assert.Equal(HandleResolveFailure.StaleGeneration, stale);
            Assert.True(pool.TryResolve(replacement, out _));
        }
    }
}
