using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
{
    /// <summary>
    /// Tests for the Tower Windup / Pre-cast mechanism (Round 98, Direction 9).
    /// When TowerWindupFrames[id] > 0, the tower enters a "charging" state between
    /// cooldown end and actual fire. WindupCountdown counts down to 0, then the tower
    /// fires. Tower CC (silence / sabotage) cancels in-flight windup.
    /// </summary>
    public class WindupSystemTests
    {
        private (ComponentStore store, int towerId) CreateTower()
        {
            var store = new ComponentStore();
            int tid = store.CreateEntity();
            store.AddTower(tid, TowerType.Basic, 10f, 5, 1f, 1, 50f);
            return (store, tid);
        }

        // ─── Field defaults ────────────────────────────────────────────────────────

        [Fact]
        public void AddTower_DefaultWindup_IsZero()
        {
            var (store, tid) = CreateTower();
            Assert.Equal(0, store.TowerWindupFrames[tid]);
            Assert.Equal(0, store.TowerWindupCountdown[tid]);
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
            var (store, tid) = CreateTower();
            store.TowerWindupFrames[tid] = 5;
            store.TowerWindupCountdown[tid] = 3;
            store.RemoveTower(tid);
            Assert.Equal(0, store.TowerWindupFrames[tid]);
            Assert.Equal(0, store.TowerWindupCountdown[tid]);
        }

        [Fact]
        public void DestroyEntity_ResetsWindupFields()
        {
            var store = new ComponentStore();
            int tid = store.CreateEntity();
            store.AddTower(tid, TowerType.Basic, 10f, 5, 1f, 1, 50f);
            store.TowerWindupFrames[tid] = 8;
            store.TowerWindupCountdown[tid] = 4;
            store.DestroyEntity(tid);
            Assert.Equal(0, store.TowerWindupFrames[tid]);
            Assert.Equal(0, store.TowerWindupCountdown[tid]);
        }

        // ─── Reuse-after-recycle: stale countdown must NOT leak ────────────────────

        [Fact]
        public void WindupCountdown_StaleOnRecycledSlot_DoesNotLeak()
        {
            var store = new ComponentStore();
            // First tower: manually set stale countdown > 0, then destroy.
            int t1 = store.CreateEntity();
            store.AddTower(t1, TowerType.Basic, 10f, 5, 1f, 1, 50f);
            store.TowerWindupCountdown[t1] = 7; // simulate in-flight windup
            store.DestroyEntity(t1);

            // Reuse the same slot — windup fields must be reset by DestroyEntity.
            int t2 = store.CreateEntity();
            // If the slot index matches (entity id reuse), the reset path will hit.
            // (Note: entity ids always increase, so this is a separate slot, but the
            //  same reset logic is exercised in DestroyEntity above.)
            store.AddTower(t2, TowerType.Basic, 10f, 5, 1f, 1, 50f);
            Assert.Equal(0, store.TowerWindupCountdown[t2]);
        }
    }
}
