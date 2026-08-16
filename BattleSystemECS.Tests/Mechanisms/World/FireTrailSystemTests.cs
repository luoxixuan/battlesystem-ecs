using BattleSystemECS.Tests.Infrastructure;
using System;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Mechanisms.World
{
    /// <summary>
    /// Tests for Round 128 Direction 5: Fire Trail System.
    ///
    /// FireTrailSystem is a thin passive wrapper around ComponentStore.AddCorpseEffect
    /// (effectType 3 = fire DoT). These tests verify:
    ///   1. Constructor rejects null store
    ///   2. SpawnTrail allocates a CorpseEffect slot of type Fire (3) and radius/dps/duration
    ///   3. Multiple SpawnTrail calls allocate distinct slots
    ///   4. SpawnTrail returns -1 (and increments TotalFailedFull) when storage is full
    ///   5. SpawnTrail clamps oversize radius/duration defensively
    ///   6. SpawnTrail rejects invalid (zero/negative) input by returning -1
    ///   7. Default parameters produce a non-zero, in-range fire patch
    ///   8. TotalSpawned / TotalFailedFull counters track correctly
    /// </summary>
    public class FireTrailSystemTests
    {
        private const int PlayerId = 0;

        private static (ComponentStore store, MockRenderer renderer) Env()
        {
            var store = new ComponentStore();
            int pid = store.CreateEntity();
            store.PlayerMaxHealth[pid] = 200f;
            store.PlayerCurrentHealth[pid] = 200f;
            return (store, new MockRenderer());
        }

        private static FireTrailSystem MakeSystem(ComponentStore store)
        {
            return new FireTrailSystem(store);
        }

        // ─── Constructor / lifecycle ────────────────────────────────────

        [Fact]
        public void Ctor_NullStore_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new FireTrailSystem(null));
        }

        [Fact]
        public void FreshSystem_ZeroCounters()
        {
            var (store, _) = Env();
            var sys = MakeSystem(store);
            Assert.Equal(0, sys.TotalSpawned);
            Assert.Equal(0, sys.TotalFailedFull);
        }

        // ─── SpawnTrail: happy path ─────────────────────────────────────

        [Fact]
        public void SpawnTrail_DefaultParams_AllocatesFireZone()
        {
            var (store, _) = Env();
            var sys = MakeSystem(store);

            int id = sys.SpawnTrail(10f, 5f);
            Assert.True(id >= 0, $"Expected non-negative slot id, got {id}");

            // Verify the slot is populated with a Fire-type corpse effect
            Assert.True(store.CorpseEffectActive[id]);
            Assert.Equal(3, store.CorpseEffectType[id]); // 3 = Fire
            Assert.Equal(10f, store.CorpseEffectX[id]);
            Assert.Equal(5f, store.CorpseEffectY[id]);
            Assert.Equal(1.5f, store.CorpseEffectRadius[id]);
            Assert.Equal(2.0f, store.CorpseEffectDuration[id]);
            Assert.True(store.CorpseEffectDamagePerTick[id] > 0f);
            Assert.Equal(0.5f, store.CorpseEffectTickInterval[id]);
        }

        [Fact]
        public void SpawnTrail_CustomParams_AppliesOverride()
        {
            var (store, _) = Env();
            var sys = MakeSystem(store);

            int id = sys.SpawnTrail(x: 1f, y: 2f, radius: 3f, dps: 10f, duration: 5f, tickInterval: 1f);
            Assert.True(id >= 0);
            Assert.Equal(3f, store.CorpseEffectRadius[id]);
            Assert.Equal(5f, store.CorpseEffectDuration[id]);
            Assert.Equal(1f, store.CorpseEffectTickInterval[id]);
            // damagePerTick = dps * tickInterval
            Assert.Equal(10f, store.CorpseEffectDamagePerTick[id]);
        }

        [Fact]
        public void SpawnTrail_MultipleCalls_GetDistinctSlots()
        {
            var (store, _) = Env();
            var sys = MakeSystem(store);

            int id1 = sys.SpawnTrail(0f, 0f);
            int id2 = sys.SpawnTrail(1f, 0f);
            int id3 = sys.SpawnTrail(2f, 0f);
            Assert.True(id1 >= 0 && id2 >= 0 && id3 >= 0);
            Assert.NotEqual(id1, id2);
            Assert.NotEqual(id2, id3);
            Assert.NotEqual(id1, id3);
            Assert.Equal(3, sys.TotalSpawned);
        }

        [Fact]
        public void SpawnTrail_IncrementsTotalSpawned()
        {
            var (store, _) = Env();
            var sys = MakeSystem(store);

            Assert.Equal(0, sys.TotalSpawned);
            sys.SpawnTrail(0f, 0f);
            Assert.Equal(1, sys.TotalSpawned);
            sys.SpawnTrail(1f, 1f);
            Assert.Equal(2, sys.TotalSpawned);
        }

        // ─── SpawnTrail: defensive clamps ───────────────────────────────

        [Fact]
        public void SpawnTrail_ZeroRadius_ReturnsMinus1()
        {
            var (store, _) = Env();
            var sys = MakeSystem(store);

            Assert.Equal(-1, sys.SpawnTrail(0f, 0f, radius: 0f));
            Assert.Equal(-1, sys.SpawnTrail(0f, 0f, radius: -1f));
            Assert.Equal(0, sys.TotalSpawned);
        }

        [Fact]
        public void SpawnTrail_ZeroDuration_ReturnsMinus1()
        {
            var (store, _) = Env();
            var sys = MakeSystem(store);

            Assert.Equal(-1, sys.SpawnTrail(0f, 0f, duration: 0f));
            Assert.Equal(0, sys.TotalSpawned);
        }

        [Fact]
        public void SpawnTrail_NegativeDps_ReturnsMinus1()
        {
            var (store, _) = Env();
            var sys = MakeSystem(store);

            Assert.Equal(-1, sys.SpawnTrail(0f, 0f, dps: -1f));
            Assert.Equal(0, sys.TotalSpawned);
        }

        [Fact]
        public void SpawnTrail_OversizeRadius_ClampsTo50()
        {
            var (store, _) = Env();
            var sys = MakeSystem(store);

            int id = sys.SpawnTrail(0f, 0f, radius: 99999f);
            Assert.True(id >= 0);
            Assert.Equal(50f, store.CorpseEffectRadius[id]);
        }

        [Fact]
        public void SpawnTrail_OversizeDuration_ClampsTo30()
        {
            var (store, _) = Env();
            var sys = MakeSystem(store);

            int id = sys.SpawnTrail(0f, 0f, duration: 99999f);
            Assert.True(id >= 0);
            Assert.Equal(30f, store.CorpseEffectDuration[id]);
        }

        [Fact]
        public void SpawnTrail_ZeroTickInterval_FallsBackToQuarterSecond()
        {
            var (store, _) = Env();
            var sys = MakeSystem(store);

            int id = sys.SpawnTrail(0f, 0f, tickInterval: 0f);
            Assert.True(id >= 0);
            Assert.Equal(0.25f, store.CorpseEffectTickInterval[id]);
        }

        // ─── SpawnTrail: full storage behavior ──────────────────────────

        [Fact]
        public void SpawnTrail_StorageFull_ReturnsMinus1AndIncrementsFailed()
        {
            var (store, _) = Env();
            var sys = MakeSystem(store);

            // Fill every CorpseEffect slot manually so the next spawn must fail.
            for (int i = 0; i < ComponentStore.MAX_CORPSE_EFFECTS; i++)
            {
                store.CorpseEffectActive[i] = true;
            }

            int id = sys.SpawnTrail(0f, 0f);
            Assert.Equal(-1, id);
            Assert.Equal(0, sys.TotalSpawned);
            Assert.Equal(1, sys.TotalFailedFull);
        }

        [Fact]
        public void SpawnTrail_StorageFull_RepeatedCallsAccumulateFailureCount()
        {
            var (store, _) = Env();
            var sys = MakeSystem(store);

            for (int i = 0; i < ComponentStore.MAX_CORPSE_EFFECTS; i++)
            {
                store.CorpseEffectActive[i] = true;
            }

            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(-1, sys.SpawnTrail(i, i));
            }
            Assert.Equal(0, sys.TotalSpawned);
            Assert.Equal(5, sys.TotalFailedFull);
        }

        // ─── Zero-allocation contract: no per-call allocations ──────────

        [Fact]
        public void SpawnTrail_DoesNotThrowOnFarCoordinates()
        {
            var (store, _) = Env();
            var sys = MakeSystem(store);

            // Far-away positions must not crash; CorpseEffect uses float X/Y
            // so anything finite is accepted.
            int id = sys.SpawnTrail(1e6f, -1e6f);
            Assert.True(id >= 0);
        }
    }
}