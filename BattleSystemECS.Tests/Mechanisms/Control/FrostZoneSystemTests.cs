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
    public class FrostZoneSystemTests : BattleTestBase
    {
        private FrostZoneSystem CreateEnv()
        {
            // 原样保留玩家实体分配，保证敌人槽位与迁移前一致。
            _ = Store.CreateEntity();
            return new FrostZoneSystem(Store);
        }

        private int SpawnEnemy(float x, float y)
            => Enemy(e =>
            {
                e.X = x;
                e.Y = y;
                e.MoveSpeed = 1f;
                e.Health = 10f;
                e.MaxHealth = 10f;
                e.Damage = 1f;
                e.GoldReward = 1;
            });

        [Fact]
        public void DefaultEnemyMultiplier_IsOne()
        {
            _ = CreateEnv();
            int eid = SpawnEnemy(0f, 0f);
            Assert.Equal(1f, Store.EnemyFrostZoneSlowMultiplier[eid]);
        }

        [Fact]
        public void AddTower_DefaultFrostZone_IsDisabled()
        {
            _ = CreateEnv();
            int tid = RawTower(0, 0);
            Assert.Equal(0f, Store.TowerFrostZoneRadius[tid]);
            Assert.Equal(1f, Store.TowerFrostZoneSlowFactor[tid]);
            Assert.Equal(0f, Store.TowerFrostZoneDuration[tid]);
        }

        [Fact]
        public void Update_NoFrostTowers_LeavesMultiplierAtOne()
        {
            var sys = CreateEnv();
            int eid = SpawnEnemy(0f, 0f);
            // Place a normal (non-frost) tower far away.
            int tid = RawTower(50, 50);
            // Even with a far tower, no frost zone should apply.
            Store.TowerFrostZoneRadius[tid] = 0f;
            sys.Update();
            Assert.Equal(1f, Store.EnemyFrostZoneSlowMultiplier[eid]);
        }

        [Fact]
        public void Update_FrostTower_AppliesSlowToEnemyInRadius()
        {
            var sys = CreateEnv();
            // Tower at (5,5) with radius 3 (covers 25..49 dist²), factor 0.5.
            int tid = RawTower(5, 5);
            Store.TowerFrostZoneRadius[tid] = 3f;
            Store.TowerFrostZoneSlowFactor[tid] = 0.5f;
            // Enemy at (6,6): dist²=2 → in radius.
            int eid = SpawnEnemy(6f, 6f);
            sys.Update();
            Assert.Equal(0.5f, Store.EnemyFrostZoneSlowMultiplier[eid]);
        }

        [Fact]
        public void Update_EnemyOutsideRadius_StaysAtOne()
        {
            var sys = CreateEnv();
            int tid = RawTower(0, 0);
            Store.TowerFrostZoneRadius[tid] = 2f;
            Store.TowerFrostZoneSlowFactor[tid] = 0.5f;
            // Enemy at (10,10): dist²=200 > 4 (radius²).
            int eid = SpawnEnemy(10f, 10f);
            sys.Update();
            Assert.Equal(1f, Store.EnemyFrostZoneSlowMultiplier[eid]);
        }

        [Fact]
        public void Update_OverlappingZones_TakeMinFactor()
        {
            var sys = CreateEnv();
            // Two frost towers, both covering enemy.
            int t1 = RawTower(5, 5);
            Store.TowerFrostZoneRadius[t1] = 5f;
            Store.TowerFrostZoneSlowFactor[t1] = 0.7f; // milder

            int t2 = RawTower(7, 5);
            Store.TowerFrostZoneRadius[t2] = 5f;
            Store.TowerFrostZoneSlowFactor[t2] = 0.4f; // stronger

            int eid = SpawnEnemy(6f, 5f);
            sys.Update();
            // MIN(0.7, 0.4) = 0.4 → enemy takes the more severe slow.
            Assert.Equal(0.4f, Store.EnemyFrostZoneSlowMultiplier[eid]);
        }

        [Fact]
        public void Update_DecrementsDuration_AndDisablesZoneAtZero()
        {
            var sys = CreateEnv();
            int tid = RawTower(5, 5);
            Store.TowerFrostZoneRadius[tid] = 3f;
            Store.TowerFrostZoneSlowFactor[tid] = 0.5f;
            Store.TowerFrostZoneDuration[tid] = 2f; // 2-turn zone

            int eid = SpawnEnemy(5f, 5f);
            sys.Update();
            Assert.Equal(0.5f, Store.EnemyFrostZoneSlowMultiplier[eid]);
            Assert.Equal(1f, Store.TowerFrostZoneDuration[tid]);

            sys.Update();
            // After second tick: duration=0 → radius should be 0 and enemy back to 1.0.
            Assert.Equal(0f, Store.TowerFrostZoneRadius[tid]);
            Assert.Equal(1f, Store.EnemyFrostZoneSlowMultiplier[eid]);
        }

        [Fact]
        public void Update_PermanentZone_NeverExpires()
        {
            var sys = CreateEnv();
            int tid = RawTower(5, 5);
            Store.TowerFrostZoneRadius[tid] = 3f;
            Store.TowerFrostZoneSlowFactor[tid] = 0.6f;
            Store.TowerFrostZoneDuration[tid] = 0f; // 0 = permanent
            int eid = SpawnEnemy(5f, 5f);
            for (int i = 0; i < 100; i++) sys.Update();
            Assert.Equal(3f, Store.TowerFrostZoneRadius[tid]);
            Assert.Equal(0.6f, Store.EnemyFrostZoneSlowMultiplier[eid]);
        }

        [Fact]
        public void RemoveTower_ResetsFrostZoneFields()
        {
            _ = CreateEnv();
            int tid = RawTower(0, 0);
            Store.TowerFrostZoneRadius[tid] = 4f;
            Store.TowerFrostZoneSlowFactor[tid] = 0.5f;
            Store.TowerFrostZoneDuration[tid] = 10f;
            Store.RemoveTower(tid);
            Assert.Equal(0f, Store.TowerFrostZoneRadius[tid]);
            Assert.Equal(1f, Store.TowerFrostZoneSlowFactor[tid]);
            Assert.Equal(0f, Store.TowerFrostZoneDuration[tid]);
        }

        [Fact]
        public void DestroyEntity_ResetsEnemyFrostMultiplier()
        {
            _ = CreateEnv();
            int eid = SpawnEnemy(0f, 0f);
            Store.EnemyFrostZoneSlowMultiplier[eid] = 0.3f; // simulate being in a zone
            Store.DestroyEntity(eid);
            Assert.Equal(1f, Store.EnemyFrostZoneSlowMultiplier[eid]);
        }
    }
}
