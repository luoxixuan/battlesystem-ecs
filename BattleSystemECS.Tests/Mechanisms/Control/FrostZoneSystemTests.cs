using BattleSystemECS.Tests.Infrastructure;
using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Mechanisms.Control
{
    /// <summary>
    /// Tests for the Frost Zone system (Round 82 Direction 1) — per-tower "frost tile"
    /// AoE slow. FrostZoneSystem writes EnemyFrostZoneSlowMultiplier each frame; the
    /// default 1.0 means "no zone", and a value < 1.0 means the enemy's move speed
    /// is reduced by that fraction.
    /// </summary>
    public class FrostZoneSystemTests
    {
        private (ComponentStore store, int playerId, FrostZoneSystem sys) CreateEnv()
        {
            var store = new ComponentStore();
            int playerId = store.CreateEntity();
            // 3 active enemies, 1 active frost tower (radius 5, factor 0.5) within range,
            // 1 active enemy outside range.
            var sys = new FrostZoneSystem(store);
            return (store, playerId, sys);
        }

        [Fact]
        public void DefaultEnemyMultiplier_IsOne()
        {
            var (store, _, _) = CreateEnv();
            int eid = store.AddEnemy(0f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            Assert.Equal(1f, store.EnemyFrostZoneSlowMultiplier[eid]);
        }

        [Fact]
        public void AddTower_DefaultFrostZone_IsDisabled()
        {
            var (store, _, _) = CreateEnv();
            int tid = store.CreateEntity();
            store.AddTower(tid, TowerType.Basic, 5f, 3, 1f, 1, 50f);
            Assert.Equal(0f, store.TowerFrostZoneRadius[tid]);
            Assert.Equal(1f, store.TowerFrostZoneSlowFactor[tid]);
            Assert.Equal(0f, store.TowerFrostZoneDuration[tid]);
        }

        [Fact]
        public void Update_NoFrostTowers_LeavesMultiplierAtOne()
        {
            var (store, _, sys) = CreateEnv();
            int eid = store.AddEnemy(0f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            // Place a normal (non-frost) tower far away.
            int tid = store.CreateEntity();
            store.AddTower(tid, TowerType.Basic, 5f, 3, 1f, 1, 50f);
            store.PositionX[tid] = 50f; store.PositionY[tid] = 50f;
            // Even with a far tower, no frost zone should apply.
            store.TowerFrostZoneRadius[tid] = 0f;
            sys.Update();
            Assert.Equal(1f, store.EnemyFrostZoneSlowMultiplier[eid]);
        }

        [Fact]
        public void Update_FrostTower_AppliesSlowToEnemyInRadius()
        {
            var (store, _, sys) = CreateEnv();
            // Tower at (5,5) with radius 3 (covers 25..49 dist²), factor 0.5.
            int tid = store.CreateEntity();
            store.AddTower(tid, TowerType.Basic, 5f, 3, 1f, 1, 50f);
            store.PositionX[tid] = 5f; store.PositionY[tid] = 5f;
            store.TowerFrostZoneRadius[tid] = 3f;
            store.TowerFrostZoneSlowFactor[tid] = 0.5f;
            // Enemy at (6,6): dist²=2 → in radius.
            int eid = store.AddEnemy(6f, 6f, 1f, 10f, 10f, 1f, 1, 1);
            sys.Update();
            Assert.Equal(0.5f, store.EnemyFrostZoneSlowMultiplier[eid]);
        }

        [Fact]
        public void Update_EnemyOutsideRadius_StaysAtOne()
        {
            var (store, _, sys) = CreateEnv();
            int tid = store.CreateEntity();
            store.AddTower(tid, TowerType.Basic, 5f, 3, 1f, 1, 50f);
            store.PositionX[tid] = 0f; store.PositionY[tid] = 0f;
            store.TowerFrostZoneRadius[tid] = 2f;
            store.TowerFrostZoneSlowFactor[tid] = 0.5f;
            // Enemy at (10,10): dist²=200 > 4 (radius²).
            int eid = store.AddEnemy(10f, 10f, 1f, 10f, 10f, 1f, 1, 1);
            sys.Update();
            Assert.Equal(1f, store.EnemyFrostZoneSlowMultiplier[eid]);
        }

        [Fact]
        public void Update_OverlappingZones_TakeMinFactor()
        {
            var (store, _, sys) = CreateEnv();
            // Two frost towers, both covering enemy.
            int t1 = store.CreateEntity();
            store.AddTower(t1, TowerType.Basic, 5f, 3, 1f, 1, 50f);
            store.PositionX[t1] = 5f; store.PositionY[t1] = 5f;
            store.TowerFrostZoneRadius[t1] = 5f;
            store.TowerFrostZoneSlowFactor[t1] = 0.7f; // milder

            int t2 = store.CreateEntity();
            store.AddTower(t2, TowerType.Basic, 5f, 3, 1f, 1, 50f);
            store.PositionX[t2] = 7f; store.PositionY[t2] = 5f;
            store.TowerFrostZoneRadius[t2] = 5f;
            store.TowerFrostZoneSlowFactor[t2] = 0.4f; // stronger

            int eid = store.AddEnemy(6f, 5f, 1f, 10f, 10f, 1f, 1, 1);
            sys.Update();
            // MIN(0.7, 0.4) = 0.4 → enemy takes the more severe slow.
            Assert.Equal(0.4f, store.EnemyFrostZoneSlowMultiplier[eid]);
        }

        [Fact]
        public void Update_DecrementsDuration_AndDisablesZoneAtZero()
        {
            var (store, _, sys) = CreateEnv();
            int tid = store.CreateEntity();
            store.AddTower(tid, TowerType.Basic, 5f, 3, 1f, 1, 50f);
            store.PositionX[tid] = 5f; store.PositionY[tid] = 5f;
            store.TowerFrostZoneRadius[tid] = 3f;
            store.TowerFrostZoneSlowFactor[tid] = 0.5f;
            store.TowerFrostZoneDuration[tid] = 2f; // 2-turn zone

            int eid = store.AddEnemy(5f, 5f, 1f, 10f, 10f, 1f, 1, 1);
            sys.Update();
            Assert.Equal(0.5f, store.EnemyFrostZoneSlowMultiplier[eid]);
            Assert.Equal(1f, store.TowerFrostZoneDuration[tid]);

            sys.Update();
            // After second tick: duration=0 → radius should be 0 and enemy back to 1.0.
            Assert.Equal(0f, store.TowerFrostZoneRadius[tid]);
            Assert.Equal(1f, store.EnemyFrostZoneSlowMultiplier[eid]);
        }

        [Fact]
        public void Update_PermanentZone_NeverExpires()
        {
            var (store, _, sys) = CreateEnv();
            int tid = store.CreateEntity();
            store.AddTower(tid, TowerType.Basic, 5f, 3, 1f, 1, 50f);
            store.PositionX[tid] = 5f; store.PositionY[tid] = 5f;
            store.TowerFrostZoneRadius[tid] = 3f;
            store.TowerFrostZoneSlowFactor[tid] = 0.6f;
            store.TowerFrostZoneDuration[tid] = 0f; // 0 = permanent
            int eid = store.AddEnemy(5f, 5f, 1f, 10f, 10f, 1f, 1, 1);
            for (int i = 0; i < 100; i++) sys.Update();
            Assert.Equal(3f, store.TowerFrostZoneRadius[tid]);
            Assert.Equal(0.6f, store.EnemyFrostZoneSlowMultiplier[eid]);
        }

        [Fact]
        public void RemoveTower_ResetsFrostZoneFields()
        {
            var (store, _, _) = CreateEnv();
            int tid = store.CreateEntity();
            store.AddTower(tid, TowerType.Basic, 5f, 3, 1f, 1, 50f);
            store.TowerFrostZoneRadius[tid] = 4f;
            store.TowerFrostZoneSlowFactor[tid] = 0.5f;
            store.TowerFrostZoneDuration[tid] = 10f;
            store.RemoveTower(tid);
            Assert.Equal(0f, store.TowerFrostZoneRadius[tid]);
            Assert.Equal(1f, store.TowerFrostZoneSlowFactor[tid]);
            Assert.Equal(0f, store.TowerFrostZoneDuration[tid]);
        }

        [Fact]
        public void DestroyEntity_ResetsEnemyFrostMultiplier()
        {
            var (store, _, _) = CreateEnv();
            int eid = store.AddEnemy(0f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            store.EnemyFrostZoneSlowMultiplier[eid] = 0.3f; // simulate being in a zone
            store.DestroyEntity(eid);
            Assert.Equal(1f, store.EnemyFrostZoneSlowMultiplier[eid]);
        }
    }
}