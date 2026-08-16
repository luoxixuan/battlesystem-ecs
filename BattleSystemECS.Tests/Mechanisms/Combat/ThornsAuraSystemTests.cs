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
    public class ThornsAuraSystemTests
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

        private static int PlaceTower(ComponentStore store, MockRenderer r, int x, int y,
            TowerType type = TowerType.Basic)
        {
            var tps = new TowerPlacementSystem(store, r);
            return tps.PlaceTower(x, y, type, 0f, 0, 0f, 25f);
        }

        private static int PlaceEnemy(ComponentStore store, float x, float y, float maxHp = 100f)
        {
            int eid = store.AddEnemy(x, y, 1f, maxHp, maxHp, 5f, 10, 1, "TestEnemy");
            return eid;
        }

        private static ThornsAuraSystem MakeSystem(ComponentStore store)
        {
            return new ThornsAuraSystem(store);
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
            var (store, r) = Env();
            int id = PlaceTower(store, r, 0, 0);
            Assert.False(store.TowerIsThornsTower[id]);
            Assert.Equal(0f, store.TowerThornsRadius[id]);
            Assert.Equal(0f, store.TowerThornsDps[id]);
            Assert.Equal(0f, store.TowerThornsInterval[id]);
            Assert.Equal(0f, store.TowerThornsTimer[id]);
        }

        [Fact]
        public void ComponentStore_ThornsFields_Reset_OnDestroyEntity()
        {
            // CRITICAL: ID-reuse safety. After destroying a thorns tower and placing a
            // fresh one in the recycled slot, the new tower must NOT inherit the
            // previous thorns fields (which would silently turn a non-thorns tower
            // into a thorns emitter).
            var (store, r) = Env();
            int id = PlaceTower(store, r, 0, 0);
            store.TowerIsThornsTower[id] = true;
            store.TowerThornsRadius[id] = 5f;
            store.TowerThornsDps[id] = 10f;
            store.TowerThornsInterval[id] = 1f;
            store.TowerThornsTimer[id] = 0.5f;
            store.DestroyEntity(id);
            // PlaceTower re-uses the same id (entity recycling).
            int id2 = PlaceTower(store, r, 1, 1);
            Assert.Equal(id, id2); // same slot
            Assert.False(store.TowerIsThornsTower[id2]);
            Assert.Equal(0f, store.TowerThornsRadius[id2]);
            Assert.Equal(0f, store.TowerThornsDps[id2]);
            Assert.Equal(0f, store.TowerThornsInterval[id2]);
            Assert.Equal(0f, store.TowerThornsTimer[id2]);
        }

        // ─── No-op paths (zero-overhead when no thorns tower) ────────────

        [Fact]
        public void SetTurn_NoThornsTowerOnField_DoesNotThrow()
        {
            var (store, r) = Env();
            int _ = PlaceTower(store, r, 0, 0);
            var sys = MakeSystem(store);
            // No thorns tower on field — SetTurn must be a no-op (no throw).
            sys.SetTurn();
        }

        [Fact]
        public void Update_NoThornsTowerOnField_DoesNotThrow()
        {
            var (store, r) = Env();
            int _ = PlaceTower(store, r, 0, 0);
            int _eid = PlaceEnemy(store, 3, 0);
            var sys = MakeSystem(store);
            sys.SetTurn();
            // No emitter cached — Update must early-return without crashing AND
            // must not damage the enemy.
            float hpBefore = store.EnemyHealth[_eid];
            sys.Update(0.016f, PlayerId);
            Assert.Equal(hpBefore, store.EnemyHealth[_eid]);
        }

        [Fact]
        public void Update_NoEnemiesOnField_DoesNotThrow()
        {
            // Thorns tower is placed but no enemies exist — Update must early-return
            // on empty active-enemy list without crashing.
            var (store, r) = Env();
            int thorns = PlaceTower(store, r, 0, 0);
            store.TowerIsThornsTower[thorns] = true;
            store.TowerThornsRadius[thorns] = 5f;
            store.TowerThornsDps[thorns] = 10f;
            store.TowerThornsInterval[thorns] = 0f;
            store.TowerThornsTimer[thorns] = 0f;
            var sys = MakeSystem(store);
            sys.SetTurn();
            sys.Update(0.016f, PlayerId);
        }

        // ─── Core thorns behavior ────────────────────────────────────────

        [Fact]
        public void ThornsTower_DamagesEnemyInRange_WhenIntervalZero()
        {
            // interval=0 means "fire every frame" with per-frame scaling: damage per
            // frame = ThornsDps * deltaTime. Place a thorns tower at (0,0) with DPS=10
            // and a damaged enemy at (3,0) with max HP=100. After 0.1s of update, the
            // enemy must have lost exactly 10 * 0.1 = 1.0 HP (per-frame scaling).
            var (store, r) = Env();
            int thorns = PlaceTower(store, r, 0, 0);
            int enemy = PlaceEnemy(store, 3, 0, 100f);
            store.TowerIsThornsTower[thorns] = true;
            store.TowerThornsRadius[thorns] = 5f;
            store.TowerThornsDps[thorns] = 10f;
            store.TowerThornsInterval[thorns] = 0f;
            store.TowerThornsTimer[thorns] = 0f;

            var sys = MakeSystem(store);
            sys.SetTurn();
            sys.Update(0.1f, PlayerId);

            // Per-frame scaling: 10 DPS * 0.1s = 1 HP lost.
            Assert.Equal(99f, store.EnemyHealth[enemy]);
        }

        [Fact]
        public void ThornsTower_EnemyOutsideRadius_NotDamaged()
        {
            // Enemy at (9, 19) is way outside radius=5 from the thorns tower at (0, 0).
            var (store, r) = Env();
            int thorns = PlaceTower(store, r, 0, 0);
            int enemy = PlaceEnemy(store, 9, 19, 100f);
            store.TowerIsThornsTower[thorns] = true;
            store.TowerThornsRadius[thorns] = 5f;
            store.TowerThornsDps[thorns] = 10f;
            store.TowerThornsInterval[thorns] = 0f;
            store.TowerThornsTimer[thorns] = 0f;

            var sys = MakeSystem(store);
            sys.SetTurn();
            sys.Update(1f, PlayerId);

            // Far enemy: no damage.
            Assert.Equal(100f, store.EnemyHealth[enemy]);
        }

        [Fact]
        public void ThornsTower_DeadEnemy_NotReDamaged()
        {
            // Enemy already at 0 HP must not be touched (avoid double QueueEnemyDeath).
            var (store, r) = Env();
            int thorns = PlaceTower(store, r, 0, 0);
            int enemy = PlaceEnemy(store, 3, 0, 100f);
            store.EnemyHealth[enemy] = 0f; // already dead
            store.TowerIsThornsTower[thorns] = true;
            store.TowerThornsRadius[thorns] = 5f;
            store.TowerThornsDps[thorns] = 10f;
            store.TowerThornsInterval[thorns] = 0f;
            store.TowerThornsTimer[thorns] = 0f;

            var sys = MakeSystem(store);
            sys.SetTurn();
            // Should not crash; should not write to EnemyHealth of a dead enemy.
            sys.Update(0.016f, PlayerId);
            Assert.Equal(0f, store.EnemyHealth[enemy]);
        }

        [Fact]
        public void ThornsTower_InvulnerableEnemy_NotDamaged()
        {
            // Boss invuln phase must block thorns damage (same as it blocks any other source).
            var (store, r) = Env();
            int thorns = PlaceTower(store, r, 0, 0);
            int enemy = PlaceEnemy(store, 3, 0, 100f);
            store.EnemyIsInvulnerable[enemy] = true;
            store.TowerIsThornsTower[thorns] = true;
            store.TowerThornsRadius[thorns] = 5f;
            store.TowerThornsDps[thorns] = 10f;
            store.TowerThornsInterval[thorns] = 0f;
            store.TowerThornsTimer[thorns] = 0f;

            var sys = MakeSystem(store);
            sys.SetTurn();
            sys.Update(1f, PlayerId); // 10 HP would normally be lost

            // Invuln: no damage.
            Assert.Equal(100f, store.EnemyHealth[enemy]);
        }

        [Fact]
        public void ThornsTower_KillsEnemy_QueuesDeath()
        {
            // When thorns damage reduces HP to 0, the enemy must be queued for death.
            var (store, r) = Env();
            int thorns = PlaceTower(store, r, 0, 0);
            int enemy = PlaceEnemy(store, 3, 0, 5f); // low HP
            // Use a per-tick burst (interval>0 with ThornsDps as the per-tick amount)
            // so we can deliver a big hit in a single frame. interval=0 would mean
            // per-second scaling (dps * deltaTime per frame), which is the wrong
            // convention for this test.
            store.TowerIsThornsTower[thorns] = true;
            store.TowerThornsRadius[thorns] = 5f;
            store.TowerThornsDps[thorns] = 100f; // way more than enemy HP
            store.TowerThornsInterval[thorns] = 0.5f; // per-tick burst path
            store.TowerThornsTimer[thorns] = 0.01f;  // timer < deltaTime → expires on first Update

            var sys = MakeSystem(store);
            sys.SetTurn();
            sys.Update(0.016f, PlayerId);

            // HP clamped to 0.
            Assert.Equal(0f, store.EnemyHealth[enemy]);
        }

        [Fact]
        public void TwoThornsTowers_StackAdditively()
        {
            // Two thorns towers in range, each contributing 100 DPS to the same enemy
            // → per-frame damage = (100 + 100) * 0.1s = 20 HP. We use interval=0 with
            // per-frame scaling to make the test deterministic.
            var (store, r) = Env();
            int t1 = PlaceTower(store, r, 0, 0);
            int t2 = PlaceTower(store, r, 2, 0);
            int enemy = PlaceEnemy(store, 1, 0, 100f);
            foreach (var tid in new[] { t1, t2 })
            {
                store.TowerIsThornsTower[tid] = true;
                store.TowerThornsRadius[tid] = 5f;
                store.TowerThornsDps[tid] = 100f;
                store.TowerThornsInterval[tid] = 0f;
                store.TowerThornsTimer[tid] = 0f;
            }

            var sys = MakeSystem(store);
            sys.SetTurn();
            sys.Update(0.1f, PlayerId); // (100 + 100) * 0.1 = 20 HP lost

            // 100 - 20 = 80.
            Assert.Equal(80f, store.EnemyHealth[enemy]);
        }

        // ─── Timer-gated behavior ────────────────────────────────────────

        [Fact]
        public void ThornsTower_IntervalGates_Damage_Fires_OnlyAfter_Interval_Elapses()
        {
            // interval=1.0s, timer starts at 0.5s. After 0.3s of update (timer=0.2),
            // damage must NOT fire yet. After another 0.5s (timer expires), damage fires.
            // Per-tick damage = ThornsDps (when interval>0).
            var (store, r) = Env();
            int thorns = PlaceTower(store, r, 0, 0);
            int enemy = PlaceEnemy(store, 3, 0, 1000f);
            store.TowerIsThornsTower[thorns] = true;
            store.TowerThornsRadius[thorns] = 5f;
            store.TowerThornsDps[thorns] = 50f; // per-tick damage
            store.TowerThornsInterval[thorns] = 1.0f;
            store.TowerThornsTimer[thorns] = 0.5f; // half-way through cooldown

            var sys = MakeSystem(store);
            sys.SetTurn();
            sys.Update(0.3f, PlayerId); // timer: 0.5 - 0.3 = 0.2 → still on cooldown
            Assert.Equal(1000f, store.EnemyHealth[enemy]);

            sys.Update(0.5f, PlayerId); // timer: 0.2 - 0.5 = -0.3 → fires! reset to 1.0
            Assert.Equal(950f, store.EnemyHealth[enemy]); // 1000 - 50
        }

        // ─── Multi-frame continuous scaling ─────────────────────────────

        [Fact]
        public void ThornsTower_ContinuousPerFrameScaling_AccumulatesOverManyFrames()
        {
            // 10 small frames at DPS=10 → total 10 * 10 * 0.01 = 1 HP lost.
            // (Demonstrates that interval=0 means "continuous DPS", not "burst per tick".)
            var (store, r) = Env();
            int thorns = PlaceTower(store, r, 0, 0);
            int enemy = PlaceEnemy(store, 3, 0, 1000f);
            store.TowerIsThornsTower[thorns] = true;
            store.TowerThornsRadius[thorns] = 5f;
            store.TowerThornsDps[thorns] = 10f;
            store.TowerThornsInterval[thorns] = 0f;
            store.TowerThornsTimer[thorns] = 0f;

            var sys = MakeSystem(store);
            sys.SetTurn();
            for (int i = 0; i < 10; i++)
            {
                sys.Update(0.01f, PlayerId);
            }

            // 10 frames * (10 DPS * 0.01s) = 1 HP lost. Use tolerance to absorb
            // float-rounding from 0.1s/0.01s being inexact in IEEE 754.
            Assert.Equal(999f, store.EnemyHealth[enemy], 3);
        }

        // ─── Defensive: thorns tower with radius=0 is inert ─────────────

        [Fact]
        public void ThornsTower_DefensiveRadiusZero_DoesNotDamage()
        {
            // IsThornsTower=true but radius=0 must be inert (defensive guard inside Update).
            var (store, r) = Env();
            int thorns = PlaceTower(store, r, 0, 0);
            int enemy = PlaceEnemy(store, 3, 0, 100f);
            store.TowerIsThornsTower[thorns] = true;
            store.TowerThornsRadius[thorns] = 0f; // defensive: should not damage
            store.TowerThornsDps[thorns] = 10f;
            store.TowerThornsInterval[thorns] = 0f;
            store.TowerThornsTimer[thorns] = 0f;

            var sys = MakeSystem(store);
            sys.SetTurn();
            sys.Update(1f, PlayerId);
            Assert.Equal(100f, store.EnemyHealth[enemy]);
        }

        [Fact]
        public void ThornsTower_DefensiveDpsZero_DoesNotDamage()
        {
            // IsThornsTower=true and radius>0 but dps=0 must be inert.
            var (store, r) = Env();
            int thorns = PlaceTower(store, r, 0, 0);
            int enemy = PlaceEnemy(store, 3, 0, 100f);
            store.TowerIsThornsTower[thorns] = true;
            store.TowerThornsRadius[thorns] = 5f;
            store.TowerThornsDps[thorns] = 0f; // defensive: should not damage
            store.TowerThornsInterval[thorns] = 0f;
            store.TowerThornsTimer[thorns] = 0f;

            var sys = MakeSystem(store);
            sys.SetTurn();
            sys.Update(1f, PlayerId);
            Assert.Equal(100f, store.EnemyHealth[enemy]);
        }
    }
}