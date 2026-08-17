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
    public class PhaserEnemyTests : BattleTestBase
    {
        [Fact]
        public void DefaultEnemy_PhaserFields_AreInert()
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            // All 6 phaser fields should be in inert defaults — hot path fast-returns
            Assert.False(Store.EnemyIsPhaser[eid]);
            Assert.Equal(0f, Store.EnemyPhaserInterval[eid]);
            Assert.Equal(0f, Store.EnemyPhaserDurationLeft[eid]);
            Assert.False(Store.EnemyPhaserPhaseActive[eid]);
            Assert.Equal(0f, Store.EnemyPhaserCycleTimer[eid]);
            Assert.Equal(0f, Store.EnemyPhaserPhaseDuration[eid]);
        }

        [Fact]
        public void SetEnemyPhaser_ConfiguresFields()
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            Store.SetEnemyPhaser(eid, interval: 4.0f, phaseDuration: 1.5f);
            Assert.True(Store.EnemyIsPhaser[eid]);
            Assert.Equal(4.0f, Store.EnemyPhaserInterval[eid]);
            Assert.Equal(1.5f, Store.EnemyPhaserPhaseDuration[eid]);
            // Starts in the "vulnerable gap": phase inactive, cycle timer at 0
            Assert.False(Store.EnemyPhaserPhaseActive[eid]);
            Assert.Equal(0f, Store.EnemyPhaserDurationLeft[eid]);
            Assert.Equal(0f, Store.EnemyPhaserCycleTimer[eid]);
        }

        [Fact]
        public void SetEnemyPhaser_ClampsInterval_ToValidRange()
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            // Clamp upper bound: 60.0f should clamp to 30.0f
            Store.SetEnemyPhaser(eid, 60f, 1.5f);
            Assert.Equal(30.0f, Store.EnemyPhaserInterval[eid]);
            // Clamp lower bound: 0.01f should clamp to 0.1f
            Store.SetEnemyPhaser(eid, 0.01f, 1.5f);
            Assert.Equal(0.1f, Store.EnemyPhaserInterval[eid]);
        }

        [Fact]
        public void SetEnemyPhaser_ClampsPhaseDuration_ToValidRange()
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            // Clamp upper bound: 30.0f should clamp to 10.0f
            Store.SetEnemyPhaser(eid, 4f, 30f);
            Assert.Equal(10.0f, Store.EnemyPhaserPhaseDuration[eid]);
            // Clamp lower bound: 0.01f should clamp to 0.1f
            Store.SetEnemyPhaser(eid, 4f, 0.01f);
            Assert.Equal(0.1f, Store.EnemyPhaserPhaseDuration[eid]);
        }

        [Fact]
        public void SetEnemyPhaser_InvalidEntity_NoOp()
        {
            // 先写入合法实体的已知值作为对照，再用无效 id 调用并断言合法槽位不变。
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            Store.SetEnemyPhaser(eid, 4f, 1.5f);
            Assert.True(Store.EnemyIsPhaser[eid]);
            Assert.Equal(4f, Store.EnemyPhaserInterval[eid]);
            Assert.Equal(1.5f, Store.EnemyPhaserPhaseDuration[eid]);

            Store.SetEnemyPhaser(-1, 9f, 8f);
            Store.SetEnemyPhaser(99999, 9f, 8f);

            Assert.True(Store.EnemyIsPhaser[eid]);
            Assert.Equal(4f, Store.EnemyPhaserInterval[eid]);
            Assert.Equal(1.5f, Store.EnemyPhaserPhaseDuration[eid]);
        }

        [Fact]
        public void RecycleEntity_PhaserFields_AreReset()
        {
            // Critical: when an entity id is recycled (DestroyEntity + AddEnemy),
            // the phaser state must NOT leak from the prior slot occupant. A
            // freshly-spawned enemy must start in the vulnerable gap, never inherit
            // an active phase window or advanced cycle timer from the prior slot
            // occupant.
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            Store.SetEnemyPhaser(eid, 4f, 1.5f);
            // Simulate "in middle of phase window"
            Store.EnemyPhaserPhaseActive[eid] = true;
            Store.EnemyPhaserDurationLeft[eid] = 1.0f;
            Store.EnemyPhaserCycleTimer[eid] = 2.5f;
            // Now recycle: destroy then re-add at same id (ComponentStore reuses ids)
            Store.DestroyEntity(eid);
            int newEid = Store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            // Slot must be reset to inert defaults — NOT carry over old phaser state
            Assert.False(Store.EnemyIsPhaser[newEid]);
            Assert.Equal(0f, Store.EnemyPhaserInterval[newEid]);
            Assert.Equal(0f, Store.EnemyPhaserDurationLeft[newEid]);
            Assert.False(Store.EnemyPhaserPhaseActive[newEid]);
            Assert.Equal(0f, Store.EnemyPhaserCycleTimer[newEid]);
            Assert.Equal(0f, Store.EnemyPhaserPhaseDuration[newEid]);
        }

        [Fact]
        public void Phaser_PhaseActiveFlag_IsReadable()
        {
            // Documents that the PhaseActive flag is the single source of truth for
            // the damage hot path. When false, damage flows normally; when true, all
            // physical damage branches in TowerAttackSystem and PlayerTowerAttackSystem
            // must zero the damage. The flag is set/cleared exclusively by
            // FrameScheduler.TickPhaserCycle (not by callers).
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            Store.SetEnemyPhaser(eid, 4f, 1.5f);
            // Manually flip the phase flag to simulate "currently in phase"
            Store.EnemyPhaserPhaseActive[eid] = true;
            Assert.True(Store.EnemyPhaserPhaseActive[eid]);
            // Manually clear it
            Store.EnemyPhaserPhaseActive[eid] = false;
            Assert.False(Store.EnemyPhaserPhaseActive[eid]);
        }
    }
}