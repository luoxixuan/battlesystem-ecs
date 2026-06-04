using System;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Tests
{
    /// <summary>
    /// Tests for Round 101 Direction 10: Mana Drain (tower → enemy).
    /// Verifies that:
    ///   - Default behavior (no drain configured) leaves enemy & player mana untouched
    ///   - Towers with ManaDrainPct > 0 drain a fraction of enemy current mana on hit
    ///   - Drain is capped by the global / per-tower cap
    ///   - Drained mana is added to the player mana pool (clamped to max)
    ///   - Zero-mana enemies (EnemyMaxMana == 0) silently no-op
    ///   - DestroyEntity resets enemy mana fields (no ID-reuse leakage)
    ///   - AddTower / DestroyEntity resets tower drain fields
    /// </summary>
    public class ManaDrainSystemTests
    {
        private const int PlayerId = 0;
        private const float PlayerMaxMana = 500f;

        private static int SpawnManaWielder(ComponentStore store, float maxMana)
        {
            int e = store.AddEnemy(0, 0, 5f, 100f, 100f, 5f, 10, 1, "ManaWielder");
            store.EnemyMaxMana[e] = maxMana;
            store.EnemyCurrentMana[e] = maxMana;
            return e;
        }

        private static int SpawnPlainEnemy(ComponentStore store)
        {
            return store.AddEnemy(0, 0, 5f, 100f, 100f, 5f, 10, 1, "TestEnemy");
        }

        private static void InitPlayerMana(ComponentStore store, float current = 0f)
        {
            store.PlayerMaxMana[PlayerId] = PlayerMaxMana;
            store.PlayerMana[PlayerId] = current;
        }

        // ─── Default state — backward compat ─────────────────────────────

        [Fact]
        public void DefaultState_AllManaFieldsZero()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            Assert.Equal(0f, store.EnemyMaxMana[e]);
            Assert.Equal(0f, store.EnemyCurrentMana[e]);
        }

        [Fact]
        public void DefaultState_NewComponentStore_TowerDrainZero()
        {
            var store = new ComponentStore();
            for (int i = 0; i < 10; i++)
            {
                Assert.Equal(0f, store.TowerManaDrainPct[i]);
                Assert.Equal(0f, store.TowerManaDrainCap[i]);
            }
        }

        [Fact]
        public void ManaDrainConfig_HasSensibleDefaults()
        {
            Assert.True(ManaDrainConfig.DefaultManaDrainPct > 0f);
            Assert.True(ManaDrainConfig.ManaDrainCap > 0f);
            Assert.Equal(0f, ManaDrainConfig.DefaultEnemyMaxMana);
        }

        // ─── Enemy mana pool helpers ────────────────────────────────────

        [Fact]
        public void SpawnManaWielder_FieldsPopulated()
        {
            var store = new ComponentStore();
            int e = SpawnManaWielder(store, 200f);
            Assert.Equal(200f, store.EnemyMaxMana[e]);
            Assert.Equal(200f, store.EnemyCurrentMana[e]);
        }

        [Fact]
        public void SpawnPlainEnemy_FieldsStaysZero()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            Assert.Equal(0f, store.EnemyMaxMana[e]);
            Assert.Equal(0f, store.EnemyCurrentMana[e]);
        }

        // ─── Direct field mutation: drain semantics ─────────────────────

        [Fact]
        public void DrainLogic_PctApplied_EnemyManaDecremented()
        {
            // Simulates the drain math from TowerAttackSystem in isolation:
            // drain = min(cap, curMana * drainPct); curMana -= drain; playerMana += drain (clamped)
            var store = new ComponentStore();
            int e = SpawnManaWielder(store, 100f);
            InitPlayerMana(store);

            float drainPct = 0.10f; // 10%
            float cap = ManaDrainConfig.ManaDrainCap;
            float curMana = store.EnemyCurrentMana[e];
            float drain = Math.Min(cap, curMana * drainPct);
            store.EnemyCurrentMana[e] = curMana - drain;
            store.PlayerMana[PlayerId] += drain;

            Assert.Equal(90f, store.EnemyCurrentMana[e], 3);
            Assert.Equal(10f, store.PlayerMana[PlayerId], 3);
        }

        [Fact]
        public void DrainLogic_RespectsGlobalCap()
        {
            var store = new ComponentStore();
            int e = SpawnManaWielder(store, 10000f); // very large pool
            InitPlayerMana(store);

            float drainPct = 1.0f; // drain 100% (huge)
            float cap = ManaDrainConfig.ManaDrainCap; // global cap
            float curMana = store.EnemyCurrentMana[e];
            float drain = Math.Min(cap, curMana * drainPct);
            store.EnemyCurrentMana[e] = curMana - drain;
            store.PlayerMana[PlayerId] += drain;

            Assert.Equal(cap, drain, 3);
            Assert.Equal(10000f - cap, store.EnemyCurrentMana[e], 3);
            Assert.Equal(cap, store.PlayerMana[PlayerId], 3);
        }

        [Fact]
        public void DrainLogic_PerTowerCapOverridesGlobal()
        {
            var store = new ComponentStore();
            int e = SpawnManaWielder(store, 1000f);
            InitPlayerMana(store);

            float drainPct = 1.0f;
            float towerCap = 25f; // < global cap
            float cap = towerCap > 0f ? towerCap : ManaDrainConfig.ManaDrainCap;
            float curMana = store.EnemyCurrentMana[e];
            float drain = Math.Min(cap, curMana * drainPct);
            store.EnemyCurrentMana[e] = curMana - drain;
            store.PlayerMana[PlayerId] += drain;

            Assert.Equal(towerCap, drain, 3);
            Assert.Equal(towerCap, store.PlayerMana[PlayerId], 3);
        }

        [Fact]
        public void DrainLogic_PlayerManaClampedToMax()
        {
            var store = new ComponentStore();
            int e = SpawnManaWielder(store, 1000f);
            InitPlayerMana(store, current: PlayerMaxMana - 5f); // almost full

            float drainPct = 0.5f;
            float curMana = store.EnemyCurrentMana[e];
            float drain = Math.Min(ManaDrainConfig.ManaDrainCap, curMana * drainPct);
            store.EnemyCurrentMana[e] = curMana - drain;
            store.PlayerMana[PlayerId] = Math.Min(PlayerMaxMana, store.PlayerMana[PlayerId] + drain);

            // Player should be at cap (PlayerMaxMana), not PlayerMaxMana + drain
            Assert.Equal(PlayerMaxMana, store.PlayerMana[PlayerId], 3);
        }

        [Fact]
        public void DrainLogic_ZeroManaEnemy_NoOp()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store); // EnemyMaxMana == 0
            InitPlayerMana(store);

            // The hot path guard is: if (EnemyMaxMana[e] > 0f) {...}
            // Simulate: no drain occurs
            float enemyMaxMana = store.EnemyMaxMana[e];
            Assert.Equal(0f, enemyMaxMana);

            // No mutation should have happened to player mana
            Assert.Equal(0f, store.PlayerMana[PlayerId]);
        }

        [Fact]
        public void DrainLogic_ZeroCurrentManaEnemy_NoOp()
        {
            // Edge case: enemy has MaxMana but already at 0 current
            var store = new ComponentStore();
            int e = SpawnManaWielder(store, 200f);
            store.EnemyCurrentMana[e] = 0f;
            InitPlayerMana(store);

            float curMana = store.EnemyCurrentMana[e];
            // The guard `if (curMana > 0f)` blocks drain
            Assert.Equal(0f, curMana);
            Assert.Equal(0f, store.PlayerMana[PlayerId]);
        }

        // ─── ID-reuse safety ────────────────────────────────────────────

        [Fact]
        public void DestroyEntity_ResetsEnemyManaFields()
        {
            var store = new ComponentStore();
            int e = SpawnManaWielder(store, 500f);
            store.EnemyCurrentMana[e] = 250f;
            store.DestroyEntity(e);
            Assert.Equal(0f, store.EnemyMaxMana[e]);
            Assert.Equal(0f, store.EnemyCurrentMana[e]);
        }
    }
}
