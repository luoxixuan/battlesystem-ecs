using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class GameplayStorageProfileTests
    {
        private readonly ITestOutputHelper _output;

        public GameplayStorageProfileTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void DenseSoaInventoryAndActiveListProfileAreReproducible()
        {
            long allocationStart = GC.GetAllocatedBytesForCurrentThread();
            using var store = new ComponentStore();
            long constructorAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
            var arrays = CaptureArrays(store);

            Assert.Equal((long)ComponentStore.MAX_ENTITIES * ComponentStore.MAX_ABILITIES_PER_ENTITY,
                Assert.Single(arrays, item => item.Name == "AbilityInstances").Elements);
            Assert.Equal((long)ComponentStore.MAX_ENTITIES * ComponentStore.MAX_ACTIVE_EFFECTS_PER_ENTITY,
                Assert.Single(arrays, item => item.Name == "ActiveEffects").Elements);
            Assert.Equal((long)ComponentStore.MAX_ENTITIES * ComponentStore.MAX_ACTIVE_EFFECTS_PER_ENTITY,
                Assert.Single(arrays, item => item.Name == "_activeEffectHandles").Elements);
            Assert.Equal(0, store.GameplayEffects.AllocatedPageCount);

            int effectCapacity = store.GameplayEffectPool.Capacity;
            long denseStart = GC.GetAllocatedBytesForCurrentThread();
            var denseGenerations = new int[effectCapacity];
            var denseActive = new bool[effectCapacity];
            var denseNextFree = new int[effectCapacity];
            long denseEffectHandleBytes = GC.GetAllocatedBytesForCurrentThread() - denseStart;
            long pagedStart = GC.GetAllocatedBytesForCurrentThread();
            var pagedProbe = new BattleSystemECS.Core.GAS.EffectPool(effectCapacity);
            long pagedEffectHandleBytes = GC.GetAllocatedBytesForCurrentThread() - pagedStart;
            Assert.True(denseEffectHandleBytes > pagedEffectHandleBytes);
            Assert.Equal(0, pagedProbe.AllocatedPageCount);

            const int population = 10000;
            for (int i = 0; i < population; i++)
            {
                int id = store.AddEnemy(i % 100, i / 100, 0f, 100f, 100f, 0f, 0, 1);
                Assert.True(id >= 0);
            }
            GameplayObservation.EnableDigests(store);
            (long activeTicks, double activeSum) = MeasureActiveList(store, 64);
            (long scanTicks, double scanSum) = MeasureDenseScan(store, 64);
            Assert.Equal(activeSum, scanSum, 3);
            var observation = GameplayObservation.Capture(store);
            // Storage profile is an inventory/state observation only: it must not
            // manufacture gameplay facts merely by capturing the real store.
            Assert.Equal(0L, observation.GameplayEventPublishedCount);
            Assert.NotEqual(0UL, observation.StateDigest);
            Assert.NotEqual(0UL, observation.GameplayEventSequenceDigest);

            var categories = arrays.GroupBy(item => item.Category, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new
                {
                    category = group.Key,
                    arrayCount = group.Count(),
                    elements = group.Sum(item => item.Elements),
                    estimatedPayloadBytes = group.Sum(item => item.EstimatedPayloadBytes)
                }).ToArray();
            var report = new
            {
                schemaVersion = 1,
                scenario = "component-store-dense-soa-inventory",
                maxEntities = ComponentStore.MAX_ENTITIES,
                population,
                constructorAllocatedBytes,
                effectHandleAllocationComparison = new
                {
                    logicalCapacity = effectCapacity,
                    denseBytes = denseEffectHandleBytes,
                    pagedBytes = pagedEffectHandleBytes,
                    avoidedInitialBytes = denseEffectHandleBytes - pagedEffectHandleBytes
                },
                effectPool = new
                {
                    capacity = store.GameplayEffectPool.Capacity,
                    active = store.GameplayEffectPool.ActiveCount,
                    handleAllocatedPages = store.GameplayEffectPool.AllocatedPageCount,
                    handleAllocatedSlots = store.GameplayEffectPool.AllocatedSlotCapacity,
                    runtimeAllocatedPages = store.GameplayEffects.AllocatedPageCount,
                    runtimeAllocatedSlots = store.GameplayEffects.AllocatedSlotCapacity
                },
                iteration = new
                {
                    repetitions = 64,
                    activeListStopwatchTicks = activeTicks,
                    denseScanStopwatchTicks = scanTicks,
                    sumsEqual = activeSum == scanSum
                },
                stateDigest = observation.StateDigest,
                gameplayEventSequenceDigest = observation.GameplayEventSequenceDigest,
                gameplayEventPublishedCount = observation.GameplayEventPublishedCount,
                observation,
                categories,
                arrays
            };
            _output.WriteLine("STORAGE_PROFILE=" +
                $"arrays:{arrays.Count};allocated:{constructorAllocatedBytes};" +
                $"activeTicks:{activeTicks};scanTicks:{scanTicks}");
            EvidenceWriter.WriteJsonIfRequested("BATTLESYSTEM_STORAGE_REPORT", report);
            GC.KeepAlive(denseGenerations);
            GC.KeepAlive(denseActive);
            GC.KeepAlive(denseNextFree);
            GC.KeepAlive(pagedProbe);
        }

        private static List<ArrayProfile> CaptureArrays(ComponentStore store)
        {
            var profiles = new List<ArrayProfile>();
            FieldInfo[] fields = typeof(ComponentStore).GetFields(BindingFlags.Instance |
                BindingFlags.Public | BindingFlags.NonPublic);
            foreach (FieldInfo field in fields)
            {
                if (!(field.GetValue(store) is Array array) || array.LongLength < ComponentStore.MAX_ENTITIES)
                    continue;
                Type elementType = field.FieldType.GetElementType() ?? typeof(object);
                int elementSize = EstimateElementSize(elementType);
                profiles.Add(new ArrayProfile(
                    field.Name,
                    elementType.FullName ?? elementType.Name,
                    array.Rank,
                    array.LongLength,
                    elementSize <= 0 ? 0L : array.LongLength * elementSize,
                    Classify(field.Name)));
            }
            profiles.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
            return profiles;
        }

        private static string Classify(string name)
        {
            if (name == "AbilityInstances" || name == "AbilityCount") return "ability-capped-candidate";
            if (name == "ActiveEffects" || name == "ActiveEffectCount" || name == "_activeEffectHandles")
                return "legacy-effect-projection";
            if (name.StartsWith("EnemyPhase", StringComparison.Ordinal)) return "boss-phase-candidate";
            return "dense-existing";
        }

        private static int EstimateElementSize(Type type)
        {
            if (!type.IsValueType) return IntPtr.Size;
            if (type.IsEnum) type = Enum.GetUnderlyingType(type);
            if (type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte)) return 1;
            if (type == typeof(char) || type == typeof(short) || type == typeof(ushort)) return 2;
            if (type == typeof(int) || type == typeof(uint) || type == typeof(float)) return 4;
            if (type == typeof(long) || type == typeof(ulong) || type == typeof(double)) return 8;
            try { return Marshal.SizeOf(type); }
            catch (ArgumentException) { return 0; }
        }

        private static (long ticks, double sum) MeasureActiveList(ComponentStore store, int repetitions)
        {
            double sum = 0d;
            var stopwatch = Stopwatch.StartNew();
            for (int repeat = 0; repeat < repetitions; repeat++)
            {
                ReadOnlySpan<int> ids = store.GetActiveEnemySpan();
                for (int i = 0; i < ids.Length; i++) sum += store.EnemyHealth[ids[i]];
            }
            stopwatch.Stop();
            return (stopwatch.ElapsedTicks, sum);
        }

        private static (long ticks, double sum) MeasureDenseScan(ComponentStore store, int repetitions)
        {
            double sum = 0d;
            var stopwatch = Stopwatch.StartNew();
            for (int repeat = 0; repeat < repetitions; repeat++)
            {
                for (int i = 0; i < ComponentStore.MAX_ENTITIES; i++)
                    if (store.EnemyActive[i]) sum += store.EnemyHealth[i];
            }
            stopwatch.Stop();
            return (stopwatch.ElapsedTicks, sum);
        }

        public sealed class ArrayProfile
        {
            public string Name { get; }
            public string ElementType { get; }
            public int Rank { get; }
            public long Elements { get; }
            public long EstimatedPayloadBytes { get; }
            public string Category { get; }

            public ArrayProfile(string name, string elementType, int rank, long elements,
                long estimatedPayloadBytes, string category)
            {
                Name = name;
                ElementType = elementType;
                Rank = rank;
                Elements = elements;
                EstimatedPayloadBytes = estimatedPayloadBytes;
                Category = category;
            }
        }
    }
}
