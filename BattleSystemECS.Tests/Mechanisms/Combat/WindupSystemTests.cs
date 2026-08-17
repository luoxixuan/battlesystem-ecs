using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;
using BattleSystemECS.Tests.Infrastructure;

namespace BattleSystemECS.Tests.Mechanisms.Combat
{
    /// <summary>
    /// Tests for the Tower Windup / Pre-cast mechanism (Round 98, Direction 9).
    /// When TowerWindupFrames[id] > 0, the tower enters a "charging" state between
    /// cooldown end and actual fire. WindupCountdown counts down to 0, then the tower
    /// fires. Tower CC (silence / sabotage) cancels in-flight windup.
    /// </summary>
    public class WindupSystemTests : BattleTestBase
    {
        private int CreateTower()
        {
            int tid = Store.CreateEntity();
            Store.AddTower(tid, TowerType.Basic, 10f, 5, 1f, 1, 50f);
            return tid;
        }

        // ─── Field defaults ────────────────────────────────────────────────────────

        [Fact]
        public void AddTower_DefaultWindup_IsZero()
        {
            int tid = CreateTower();
            Assert.Equal(0, Store.TowerWindupFrames[tid]);
            Assert.Equal(0, Store.TowerWindupCountdown[tid]);
        }

        // ─── WindupConfig constants ────────────────────────────────────────────────

        [Fact]
        public void WindupConfig_DefaultsAreSane()
        {
            Assert.Equal(0, WindupConfig.DefaultWindupFrames);
            Assert.Equal(1, WindupConfig.MinWindupFrames);
            Assert.Equal(30, WindupConfig.MaxWindupFrames);
            Assert.True(WindupConfig.WindupInterruptOnCC);
        }

        // ─── Recycle safety (RemoveTower + DestroyEntity) ──────────────────────────

        [Fact]
        public void RemoveTower_ResetsWindupFields()
        {
            int tid = CreateTower();
            Store.TowerWindupFrames[tid] = 5;
            Store.TowerWindupCountdown[tid] = 3;
            Store.RemoveTower(tid);
            Assert.Equal(0, Store.TowerWindupFrames[tid]);
            Assert.Equal(0, Store.TowerWindupCountdown[tid]);
        }

        [Fact]
        public void DestroyEntity_ResetsWindupFields_AndRecycledSlotIsClean()
        {
            int tid = Store.CreateEntity();
            Store.AddTower(tid, TowerType.Basic, 10f, 5, 1f, 1, 50f);
            Store.TowerWindupFrames[tid] = 8;
            Store.TowerWindupCountdown[tid] = 4;
            Store.DestroyEntity(tid);
            Assert.Equal(0, Store.TowerWindupFrames[tid]);
            Assert.Equal(0, Store.TowerWindupCountdown[tid]);

            // 回收语义：销毁后在同一槽位重新 AddTower，windup 字段必须已被重置。
            int reused = Store.CreateEntity();
            Assert.Equal(tid, reused); // swap-and-pop 复用同一实体槽位
            Store.AddTower(reused, TowerType.Basic, 10f, 5, 1f, 1, 50f);
            Assert.Equal(0, Store.TowerWindupFrames[reused]);
            Assert.Equal(0, Store.TowerWindupCountdown[reused]);
        }
    }
}
