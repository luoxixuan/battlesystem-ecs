using BattleSystemECS.Tests.Infrastructure;
using Xunit;
using BattleSystemECS.Core;

namespace BattleSystemECS.Tests.Features.Enemies
{
    /// <summary>
    /// Tests for the Phase-Through enemy (Round 181 Direction 9) — periodically immune
    /// to physical damage. Verifies:
    /// 1. Default AddEnemy produces a non-phaser (zero-overhead fast path)
    /// 2. SetEnemyPhaser configures the 6 phaser fields correctly
    /// 3. interval clamps to [0.1, 30.0], phaseDuration clamps to [0.1, 10.0]
    /// 4. DestroyEntity reset prevents leakage across slot reuse
    /// </summary>
    public class PhaserEnemyTests
    {
        private ComponentStore CreateStore()
        {
            return new ComponentStore();
        }

        [Fact]
        public void DefaultEnemy_PhaserFields_AreInert()
        {
            var store = CreateStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            // All 6 phaser fields should be in inert defaults — hot path fast-returns
            Assert.False(store.EnemyIsPhaser[eid]);
            Assert.Equal(0f, store.EnemyPhaserInterval[eid]);
            Assert.Equal(0f, store.EnemyPhaserDurationLeft[eid]);
            Assert.False(store.EnemyPhaserPhaseActive[eid]);
            Assert.Equal(0f, store.EnemyPhaserCycleTimer[eid]);
            Assert.Equal(0f, store.EnemyPhaserPhaseDuration[eid]);
        }

        [Fact]
        public void SetEnemyPhaser_ConfiguresFields()
        {
            var store = CreateStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            store.SetEnemyPhaser(eid, interval: 4.0f, phaseDuration: 1.5f);
            Assert.True(store.EnemyIsPhaser[eid]);
            Assert.Equal(4.0f, store.EnemyPhaserInterval[eid]);
            Assert.Equal(1.5f, store.EnemyPhaserPhaseDuration[eid]);
            // Starts in the "vulnerable gap": phase inactive, cycle timer at 0
            Assert.False(store.EnemyPhaserPhaseActive[eid]);
            Assert.Equal(0f, store.EnemyPhaserDurationLeft[eid]);
            Assert.Equal(0f, store.EnemyPhaserCycleTimer[eid]);
        }

        [Fact]
        public void SetEnemyPhaser_ClampsInterval_ToValidRange()
        {
            var store = CreateStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            // Clamp upper bound: 60.0f should clamp to 30.0f
            store.SetEnemyPhaser(eid, 60f, 1.5f);
            Assert.Equal(30.0f, store.EnemyPhaserInterval[eid]);
            // Clamp lower bound: 0.01f should clamp to 0.1f
            store.SetEnemyPhaser(eid, 0.01f, 1.5f);
            Assert.Equal(0.1f, store.EnemyPhaserInterval[eid]);
        }

        [Fact]
        public void SetEnemyPhaser_ClampsPhaseDuration_ToValidRange()
        {
            var store = CreateStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            // Clamp upper bound: 30.0f should clamp to 10.0f
            store.SetEnemyPhaser(eid, 4f, 30f);
            Assert.Equal(10.0f, store.EnemyPhaserPhaseDuration[eid]);
            // Clamp lower bound: 0.01f should clamp to 0.1f
            store.SetEnemyPhaser(eid, 4f, 0.01f);
            Assert.Equal(0.1f, store.EnemyPhaserPhaseDuration[eid]);
        }

        [Fact]
        public void SetEnemyPhaser_InvalidEntity_NoOp()
        {
            var store = CreateStore();
            // Negative entity id is invalid → silent no-op (no throw)
            store.SetEnemyPhaser(-1, 4f, 1.5f);
            // Out-of-range entity id is invalid → silent no-op
            store.SetEnemyPhaser(99999, 4f, 1.5f);
        }

        [Fact]
        public void RecycleEntity_PhaserFields_AreReset()
        {
            // Critical: when an entity id is recycled (DestroyEntity + AddEnemy),
            // the phaser state must NOT leak from the prior slot occupant. A
            // freshly-spawned enemy must start in the vulnerable gap, never inherit
            // an active phase window or advanced cycle timer from the prior slot
            // occupant.
            var store = CreateStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            store.SetEnemyPhaser(eid, 4f, 1.5f);
            // Simulate "in middle of phase window"
            store.EnemyPhaserPhaseActive[eid] = true;
            store.EnemyPhaserDurationLeft[eid] = 1.0f;
            store.EnemyPhaserCycleTimer[eid] = 2.5f;
            // Now recycle: destroy then re-add at same id (ComponentStore reuses ids)
            store.DestroyEntity(eid);
            int newEid = store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            // Slot must be reset to inert defaults — NOT carry over old phaser state
            Assert.False(store.EnemyIsPhaser[newEid]);
            Assert.Equal(0f, store.EnemyPhaserInterval[newEid]);
            Assert.Equal(0f, store.EnemyPhaserDurationLeft[newEid]);
            Assert.False(store.EnemyPhaserPhaseActive[newEid]);
            Assert.Equal(0f, store.EnemyPhaserCycleTimer[newEid]);
            Assert.Equal(0f, store.EnemyPhaserPhaseDuration[newEid]);
        }

        [Fact]
        public void Phaser_PhaseActiveFlag_IsReadable()
        {
            // Documents that the PhaseActive flag is the single source of truth for
            // the damage hot path. When false, damage flows normally; when true, all
            // physical damage branches in TowerAttackSystem and PlayerTowerAttackSystem
            // must zero the damage. The flag is set/cleared exclusively by
            // FrameScheduler.TickPhaserCycle (not by callers).
            var store = CreateStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            store.SetEnemyPhaser(eid, 4f, 1.5f);
            // Manually flip the phase flag to simulate "currently in phase"
            store.EnemyPhaserPhaseActive[eid] = true;
            Assert.True(store.EnemyPhaserPhaseActive[eid]);
            // Manually clear it
            store.EnemyPhaserPhaseActive[eid] = false;
            Assert.False(store.EnemyPhaserPhaseActive[eid]);
        }
    }
}