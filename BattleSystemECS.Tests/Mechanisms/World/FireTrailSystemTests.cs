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
    public class FireTrailSystemTests : BattleTestBase
    {
        private const int PlayerId = 0;

        private FireTrailSystem Env()
        {
            int pid = Store.CreateEntity();
            Store.PlayerMaxHealth[pid] = 200f;
            Store.PlayerCurrentHealth[pid] = 200f;
            return new FireTrailSystem(Store);
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
            var sys = Env();
            Assert.Equal(0, sys.TotalSpawned);
            Assert.Equal(0, sys.TotalFailedFull);
        }

        // ─── SpawnTrail: happy path ─────────────────────────────────────

        [Fact]
        public void SpawnTrail_DefaultParams_AllocatesFireZone()
        {
            var sys = Env();

            int id = sys.SpawnTrail(10f, 5f);
            Assert.True(id >= 0, $"Expected non-negative slot id, got {id}");

            // Verify the slot is populated with a Fire-type corpse effect
            Assert.True(Store.CorpseEffectActive[id]);
            Assert.Equal(3, Store.CorpseEffectType[id]); // 3 = Fire
            Assert.Equal(10f, Store.CorpseEffectX[id]);
            Assert.Equal(5f, Store.CorpseEffectY[id]);
            Assert.Equal(1.5f, Store.CorpseEffectRadius[id]);
            Assert.Equal(2.0f, Store.CorpseEffectDuration[id]);
            // 生产默认 dps=8、tickInterval=0.5 → damagePerTick = 8 * 0.5 = 4。
            Assert.Equal(4f, Store.CorpseEffectDamagePerTick[id]);
            Assert.Equal(0.5f, Store.CorpseEffectTickInterval[id]);
        }

        [Fact]
        public void SpawnTrail_CustomParams_AppliesOverride()
        {
            var sys = Env();

            int id = sys.SpawnTrail(x: 1f, y: 2f, radius: 3f, dps: 10f, duration: 5f, tickInterval: 1f);
            Assert.True(id >= 0);
            Assert.Equal(3f, Store.CorpseEffectRadius[id]);
            Assert.Equal(5f, Store.CorpseEffectDuration[id]);
            Assert.Equal(1f, Store.CorpseEffectTickInterval[id]);
            // damagePerTick = dps * tickInterval
            Assert.Equal(10f, Store.CorpseEffectDamagePerTick[id]);
        }

        [Fact]
        public void SpawnTrail_MultipleCalls_GetDistinctSlots()
        {
            var sys = Env();

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
            var sys = Env();

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
            var sys = Env();

            Assert.Equal(-1, sys.SpawnTrail(0f, 0f, radius: 0f));
            Assert.Equal(-1, sys.SpawnTrail(0f, 0f, radius: -1f));
            Assert.Equal(0, sys.TotalSpawned);
        }

        [Fact]
        public void SpawnTrail_ZeroDuration_ReturnsMinus1()
        {
            var sys = Env();

            Assert.Equal(-1, sys.SpawnTrail(0f, 0f, duration: 0f));
            Assert.Equal(0, sys.TotalSpawned);
        }

        [Fact]
        public void SpawnTrail_NegativeDps_ReturnsMinus1()
        {
            var sys = Env();

            Assert.Equal(-1, sys.SpawnTrail(0f, 0f, dps: -1f));
            Assert.Equal(0, sys.TotalSpawned);
        }

        [Fact]
        public void SpawnTrail_OversizeRadius_ClampsTo50()
        {
            var sys = Env();

            int id = sys.SpawnTrail(0f, 0f, radius: 99999f);
            Assert.True(id >= 0);
            Assert.Equal(50f, Store.CorpseEffectRadius[id]);
        }

        [Fact]
        public void SpawnTrail_OversizeDuration_ClampsTo30()
        {
            var sys = Env();

            int id = sys.SpawnTrail(0f, 0f, duration: 99999f);
            Assert.True(id >= 0);
            Assert.Equal(30f, Store.CorpseEffectDuration[id]);
        }

        [Fact]
        public void SpawnTrail_ZeroTickInterval_FallsBackToQuarterSecond()
        {
            var sys = Env();

            int id = sys.SpawnTrail(0f, 0f, tickInterval: 0f);
            Assert.True(id >= 0);
            Assert.Equal(0.25f, Store.CorpseEffectTickInterval[id]);
        }

        // ─── SpawnTrail: full storage behavior ──────────────────────────

        [Fact]
        public void SpawnTrail_StorageFull_ReturnsMinus1AndIncrementsFailed()
        {
            var sys = Env();

            // Fill every CorpseEffect slot manually so the next spawn must fail.
            for (int i = 0; i < ComponentStore.MAX_CORPSE_EFFECTS; i++)
            {
                Store.CorpseEffectActive[i] = true;
            }

            int id = sys.SpawnTrail(0f, 0f);
            Assert.Equal(-1, id);
            Assert.Equal(0, sys.TotalSpawned);
            Assert.Equal(1, sys.TotalFailedFull);
        }

        [Fact]
        public void SpawnTrail_StorageFull_RepeatedCallsAccumulateFailureCount()
        {
            var sys = Env();

            for (int i = 0; i < ComponentStore.MAX_CORPSE_EFFECTS; i++)
            {
                Store.CorpseEffectActive[i] = true;
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
        public void SpawnTrail_FarCoordinates_AreStoredExactly()
        {
            var sys = Env();

            // 远端坐标必须正常生成，且 X/Y 精确写入（CorpseEffect 用 float 存储）。
            int id = sys.SpawnTrail(1e6f, -1e6f);
            Assert.True(id >= 0);
            Assert.True(Store.CorpseEffectActive[id]);
            Assert.Equal(1e6f, Store.CorpseEffectX[id]);
            Assert.Equal(-1e6f, Store.CorpseEffectY[id]);
        }
    }
}