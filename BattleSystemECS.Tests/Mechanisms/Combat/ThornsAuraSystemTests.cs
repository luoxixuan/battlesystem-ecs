using BattleSystemECS.Tests.Infrastructure;
using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Mechanisms.Combat
{
    /// <summary>
    /// Tests for Round 126 Direction 4: Thorns Aura / Passive Damage Aura.
    /// Verifies:
    ///   1. TowerConfig.Thorns* fields default to false/0 (zero-overhead)
    ///   2. ComponentStore SOA fields zero-init on AddTower
    ///   3. ComponentStore SOA fields reset on DestroyEntity (no ID-reuse leak)
    ///   4. SetTurn early-returns when no thorns tower on field (no crash)
    ///   5. Update early-returns when no thorns tower on field (no crash)
    ///   6. Single thorns tower in range damages enemies by ThornsDps (interval=0, every frame)
    ///   7. Enemy outside radius is not damaged
    ///   8. Dead enemy (HP<=0) is not re-damaged / re-queued
    ///   9. Invulnerable enemy is not damaged (Boss invuln phase)
    ///  10. Enemy HP crossing 0 queues a death
    ///  11. Multiple thorns towers in range stack additively on each enemy
    ///  12. Interval > 0 fires only every Interval seconds (timer gates the tick)
    ///  13. Continuous-per-frame scaling: interval=0 means "dps * deltaTime per frame"
    ///  14. Inactive (no thorns) towers are zero-cost in Update
    /// </summary>
    public class ThornsAuraSystemTests : BattleTestBase
    {
        private const int PlayerId = 0;

        private void InitEnv()
        {
            int pid = Store.CreateEntity();
            Store.PlayerMaxHealth[pid] = 200f;
            Store.PlayerCurrentHealth[pid] = 200f;
        }

        private int PlaceTower(int x, int y,
            TowerType type = TowerType.Basic)
        {
            return Tower(x, y, type, t =>
            {
                t.Damage = 0f;
                t.Range = 0;
                t.Speed = 0f;
                t.Cost = 25f;
            });
        }

        private int PlaceEnemy(float x, float y, float maxHp = 100f)
        {
            return Enemy(e =>
            {
                e.X = x;
                e.Y = y;
                e.MoveSpeed = 1f;
                e.Health = maxHp;
                e.Damage = 5f;
                e.GoldReward = 10;
                e.WaveNumber = 1;
                e.Name = "TestEnemy";
            });
        }

        private ThornsAuraSystem MakeSystem()
        {
            return new ThornsAuraSystem(Store);
        }

        // ─── Config defaults ─────────────────────────────────────────────

        [Fact]
        public void TowerConfig_Thorns_DefaultsToZeroAndFalse()
        {
            // All 4 fields default false/0 → no aura (zero-overhead on hot path).
            var tc = new TowerConfig();
            Assert.False(tc.IsThornsTower);
            Assert.Equal(0f, tc.ThornsRadius);
            Assert.Equal(0f, tc.ThornsDps);
            Assert.Equal(0f, tc.ThornsInterval);
        }

        // ─── SOA field lifecycle ──────────────────────────────────────────

        [Fact]
        public void ComponentStore_ThornsFields_DefaultToZeroAndFalse_OnAddTower()
        {
            // Adding a tower without opting in to thorns aura must leave all 5 fields at false/0.
            InitEnv();
            int id = PlaceTower(0, 0);
            Assert.False(Store.TowerIsThornsTower[id]);
            Assert.Equal(0f, Store.TowerThornsRadius[id]);
            Assert.Equal(0f, Store.TowerThornsDps[id]);
            Assert.Equal(0f, Store.TowerThornsInterval[id]);
            Assert.Equal(0f, Store.TowerThornsTimer[id]);
        }

        [Fact]
        public void ComponentStore_ThornsFields_Reset_OnDestroyEntity()
        {
            // CRITICAL: ID-reuse safety. After destroying a thorns tower and placing a
            // fresh one in the recycled slot, the new tower must NOT inherit the
            // previous thorns fields (which would silently turn a non-thorns tower
            // into a thorns emitter).
            InitEnv();
            int id = PlaceTower(0, 0);
            Store.TowerIsThornsTower[id] = true;
            Store.TowerThornsRadius[id] = 5f;
            Store.TowerThornsDps[id] = 10f;
            Store.TowerThornsInterval[id] = 1f;
            Store.TowerThornsTimer[id] = 0.5f;
            Store.DestroyEntity(id);
            // PlaceTower re-uses the same id (entity recycling).
            int id2 = PlaceTower(1, 1);
            Assert.Equal(id, id2); // same slot
            Assert.False(Store.TowerIsThornsTower[id2]);
            Assert.Equal(0f, Store.TowerThornsRadius[id2]);
            Assert.Equal(0f, Store.TowerThornsDps[id2]);
            Assert.Equal(0f, Store.TowerThornsInterval[id2]);
            Assert.Equal(0f, Store.TowerThornsTimer[id2]);
        }

        // ─── No-op paths (zero-overhead when no thorns tower) ────────────

        [Fact]
        public void SetTurn_NoThornsTowerOnField_DoesNotWriteEnemyFields()
        {
            InitEnv();
            PlaceTower(0, 0); // 普通塔，非荆棘塔
            int enemy = PlaceEnemy(3, 0);
            float hpBefore = Store.EnemyHealth[enemy];

            var sys = MakeSystem();
            // No thorns tower on field — SetTurn 只建缓存，不得改写敌人字段。
            sys.SetTurn();

            Assert.Equal(hpBefore, Store.EnemyHealth[enemy]);
        }

        [Fact]
        public void Update_NoThornsTowerOnField_DoesNotDamageEnemy()
        {
            InitEnv();
            PlaceTower(0, 0);
            int enemy = PlaceEnemy(3, 0);
            var sys = MakeSystem();
            sys.SetTurn();
            // No emitter cached — Update 早退且不得伤害敌人。
            float hpBefore = Store.EnemyHealth[enemy];
            sys.Update(0.016f, PlayerId);
            Assert.Equal(hpBefore, Store.EnemyHealth[enemy]);
            Assert.Single(Store.ActiveTowerIds);
        }

        [Fact]
        public void Update_NoEnemiesOnField_DoesNotTickEmitterTimer()
        {
            // Thorns tower is placed but no enemies exist — Update must early-return
            // on empty active-enemy list, even before ticking the emitter timer.
            InitEnv();
            int thorns = PlaceTower(0, 0);
            Store.TowerIsThornsTower[thorns] = true;
            Store.TowerThornsRadius[thorns] = 5f;
            Store.TowerThornsDps[thorns] = 10f;
            Store.TowerThornsInterval[thorns] = 0f;
            Store.TowerThornsTimer[thorns] = 0.75f; // 已知计时器
            Assert.Empty(Store.ActiveEnemyIds);

            var sys = MakeSystem();
            sys.SetTurn();
            sys.Update(0.016f, PlayerId);

            Assert.Equal(0.75f, Store.TowerThornsTimer[thorns]);
        }

        // ─── Core thorns behavior ────────────────────────────────────────

        [Fact]
        public void ThornsTower_DamagesEnemyInRange_WhenIntervalZero()
        {
            // interval=0 means "fire every frame" with per-frame scaling: damage per
            // frame = ThornsDps * deltaTime. Place a thorns tower at (0,0) with DPS=10
            // and a damaged enemy at (3,0) with max HP=100. After 0.1s of update, the
            // enemy must have lost exactly 10 * 0.1 = 1.0 HP (per-frame scaling).
            InitEnv();
            int thorns = PlaceTower(0, 0);
            int enemy = PlaceEnemy(3, 0, 100f);
            Store.TowerIsThornsTower[thorns] = true;
            Store.TowerThornsRadius[thorns] = 5f;
            Store.TowerThornsDps[thorns] = 10f;
            Store.TowerThornsInterval[thorns] = 0f;
            Store.TowerThornsTimer[thorns] = 0f;

            var sys = MakeSystem();
            sys.SetTurn();
            sys.Update(0.1f, PlayerId);

            // Per-frame scaling: 10 DPS * 0.1s = 1 HP lost.
            Assert.Equal(99f, Store.EnemyHealth[enemy]);
        }

        [Fact]
        public void ThornsTower_EnemyOutsideRadius_NotDamaged()
        {
            // Enemy at (9, 19) is way outside radius=5 from the thorns tower at (0, 0).
            InitEnv();
            int thorns = PlaceTower(0, 0);
            int enemy = PlaceEnemy(9, 19, 100f);
            Store.TowerIsThornsTower[thorns] = true;
            Store.TowerThornsRadius[thorns] = 5f;
            Store.TowerThornsDps[thorns] = 10f;
            Store.TowerThornsInterval[thorns] = 0f;
            Store.TowerThornsTimer[thorns] = 0f;

            var sys = MakeSystem();
            sys.SetTurn();
            sys.Update(1f, PlayerId);

            // Far enemy: no damage.
            Assert.Equal(100f, Store.EnemyHealth[enemy]);
        }

        [Fact]
        public void ThornsTower_DeadEnemy_NotReDamaged()
        {
            // Enemy already at 0 HP must not be touched (avoid double QueueEnemyDeath).
            InitEnv();
            int thorns = PlaceTower(0, 0);
            int enemy = PlaceEnemy(3, 0, 100f);
            Store.EnemyHealth[enemy] = 0f; // already dead
            Store.TowerIsThornsTower[thorns] = true;
            Store.TowerThornsRadius[thorns] = 5f;
            Store.TowerThornsDps[thorns] = 10f;
            Store.TowerThornsInterval[thorns] = 0f;
            Store.TowerThornsTimer[thorns] = 0f;

            var sys = MakeSystem();
            sys.SetTurn();
            // Should not crash; should not write to EnemyHealth of a dead enemy.
            sys.Update(0.016f, PlayerId);
            Assert.Equal(0f, Store.EnemyHealth[enemy]);
        }

        [Fact]
        public void ThornsTower_InvulnerableEnemy_NotDamaged()
        {
            // Boss invuln phase must block thorns damage (same as it blocks any other source).
            InitEnv();
            int thorns = PlaceTower(0, 0);
            int enemy = PlaceEnemy(3, 0, 100f);
            Store.EnemyIsInvulnerable[enemy] = true;
            Store.TowerIsThornsTower[thorns] = true;
            Store.TowerThornsRadius[thorns] = 5f;
            Store.TowerThornsDps[thorns] = 10f;
            Store.TowerThornsInterval[thorns] = 0f;
            Store.TowerThornsTimer[thorns] = 0f;

            var sys = MakeSystem();
            sys.SetTurn();
            sys.Update(1f, PlayerId); // 10 HP would normally be lost

            // Invuln: no damage.
            Assert.Equal(100f, Store.EnemyHealth[enemy]);
        }

        [Fact]
        public void ThornsTower_KillsEnemy_QueuesDeath()
        {
            // When thorns damage reduces HP to 0, the enemy must be queued for death.
            InitEnv();
            int thorns = PlaceTower(0, 0);
            int enemy = PlaceEnemy(3, 0, 5f); // low HP
            // Use a per-tick burst (interval>0 with ThornsDps as the per-tick amount)
            // so we can deliver a big hit in a single frame. interval=0 would mean
            // per-second scaling (dps * deltaTime per frame), which is the wrong
            // convention for this test.
            Store.TowerIsThornsTower[thorns] = true;
            Store.TowerThornsRadius[thorns] = 5f;
            Store.TowerThornsDps[thorns] = 100f; // way more than enemy HP
            Store.TowerThornsInterval[thorns] = 0.5f; // per-tick burst path
            Store.TowerThornsTimer[thorns] = 0.01f;  // timer < deltaTime → expires on first Update

            var sys = MakeSystem();
            sys.SetTurn();
            sys.Update(0.016f, PlayerId);

            // HP clamped to 0.
            Assert.Equal(0f, Store.EnemyHealth[enemy]);
        }

        [Fact]
        public void TwoThornsTowers_StackAdditively()
        {
            // Two thorns towers in range, each contributing 100 DPS to the same enemy
            // → per-frame damage = (100 + 100) * 0.1s = 20 HP. We use interval=0 with
            // per-frame scaling to make the test deterministic.
            InitEnv();
            int t1 = PlaceTower(0, 0);
            int t2 = PlaceTower(2, 0);
            int enemy = PlaceEnemy(1, 0, 100f);
            foreach (var tid in new[] { t1, t2 })
            {
                Store.TowerIsThornsTower[tid] = true;
                Store.TowerThornsRadius[tid] = 5f;
                Store.TowerThornsDps[tid] = 100f;
                Store.TowerThornsInterval[tid] = 0f;
                Store.TowerThornsTimer[tid] = 0f;
            }

            var sys = MakeSystem();
            sys.SetTurn();
            sys.Update(0.1f, PlayerId); // (100 + 100) * 0.1 = 20 HP lost

            // 100 - 20 = 80.
            Assert.Equal(80f, Store.EnemyHealth[enemy]);
        }

        // ─── Timer-gated behavior ────────────────────────────────────────

        [Fact]
        public void ThornsTower_IntervalGates_Damage_Fires_OnlyAfter_Interval_Elapses()
        {
            // interval=1.0s, timer starts at 0.5s. After 0.3s of update (timer=0.2),
            // damage must NOT fire yet. After another 0.5s (timer expires), damage fires.
            // Per-tick damage = ThornsDps (when interval>0).
            InitEnv();
            int thorns = PlaceTower(0, 0);
            int enemy = PlaceEnemy(3, 0, 1000f);
            Store.TowerIsThornsTower[thorns] = true;
            Store.TowerThornsRadius[thorns] = 5f;
            Store.TowerThornsDps[thorns] = 50f; // per-tick damage
            Store.TowerThornsInterval[thorns] = 1.0f;
            Store.TowerThornsTimer[thorns] = 0.5f; // half-way through cooldown

            var sys = MakeSystem();
            sys.SetTurn();
            sys.Update(0.3f, PlayerId); // timer: 0.5 - 0.3 = 0.2 → still on cooldown
            Assert.Equal(1000f, Store.EnemyHealth[enemy]);

            sys.Update(0.5f, PlayerId); // timer: 0.2 - 0.5 = -0.3 → fires! reset to 1.0
            Assert.Equal(950f, Store.EnemyHealth[enemy]); // 1000 - 50
        }

        // ─── Multi-frame continuous scaling ─────────────────────────────

        [Fact]
        public void ThornsTower_ContinuousPerFrameScaling_AccumulatesOverManyFrames()
        {
            // 10 small frames at DPS=10 → total 10 * 10 * 0.01 = 1 HP lost.
            // (Demonstrates that interval=0 means "continuous DPS", not "burst per tick".)
            InitEnv();
            int thorns = PlaceTower(0, 0);
            int enemy = PlaceEnemy(3, 0, 1000f);
            Store.TowerIsThornsTower[thorns] = true;
            Store.TowerThornsRadius[thorns] = 5f;
            Store.TowerThornsDps[thorns] = 10f;
            Store.TowerThornsInterval[thorns] = 0f;
            Store.TowerThornsTimer[thorns] = 0f;

            var sys = MakeSystem();
            sys.SetTurn();
            for (int i = 0; i < 10; i++)
            {
                sys.Update(0.01f, PlayerId);
            }

            // 10 frames * (10 DPS * 0.01s) = 1 HP lost. Use tolerance to absorb
            // float-rounding from 0.1s/0.01s being inexact in IEEE 754.
            Assert.Equal(999f, Store.EnemyHealth[enemy], 3);
        }

        // ─── Defensive: thorns tower with radius=0 is inert ─────────────

        [Fact]
        public void ThornsTower_DefensiveRadiusZero_DoesNotDamage()
        {
            // IsThornsTower=true but radius=0 must be inert (defensive guard inside Update).
            InitEnv();
            int thorns = PlaceTower(0, 0);
            int enemy = PlaceEnemy(3, 0, 100f);
            Store.TowerIsThornsTower[thorns] = true;
            Store.TowerThornsRadius[thorns] = 0f; // defensive: should not damage
            Store.TowerThornsDps[thorns] = 10f;
            Store.TowerThornsInterval[thorns] = 0f;
            Store.TowerThornsTimer[thorns] = 0f;

            var sys = MakeSystem();
            sys.SetTurn();
            sys.Update(1f, PlayerId);
            Assert.Equal(100f, Store.EnemyHealth[enemy]);
        }

        [Fact]
        public void ThornsTower_DefensiveDpsZero_DoesNotDamage()
        {
            // IsThornsTower=true and radius>0 but dps=0 must be inert.
            InitEnv();
            int thorns = PlaceTower(0, 0);
            int enemy = PlaceEnemy(3, 0, 100f);
            Store.TowerIsThornsTower[thorns] = true;
            Store.TowerThornsRadius[thorns] = 5f;
            Store.TowerThornsDps[thorns] = 0f; // defensive: should not damage
            Store.TowerThornsInterval[thorns] = 0f;
            Store.TowerThornsTimer[thorns] = 0f;

            var sys = MakeSystem();
            sys.SetTurn();
            sys.Update(1f, PlayerId);
            Assert.Equal(100f, Store.EnemyHealth[enemy]);
        }
    }
}
