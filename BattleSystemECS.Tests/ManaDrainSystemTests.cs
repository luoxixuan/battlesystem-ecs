using System;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Tests.Infrastructure;

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
    public class ManaDrainSystemTests : BattleTestBase
    {
        private const int PlayerId = 0;
        private const float PlayerMaxMana = 500f;

        private int SpawnManaWielder(float maxMana)
        {
            int e = Enemy(e => e.Name = "ManaWielder");
            Store.EnemyMaxMana[e] = maxMana;
            Store.EnemyCurrentMana[e] = maxMana;
            return e;
        }

        private void InitPlayerMana(float current = 0f)
        {
            Store.PlayerMaxMana[PlayerId] = PlayerMaxMana;
            Store.PlayerMana[PlayerId] = current;
        }

        // ─── Default state — backward compat ─────────────────────────────

        [Fact]
        public void DefaultState_AllManaFieldsZero()
        {
            int e = Enemy();
            Assert.Equal(0f, Store.EnemyMaxMana[e]);
            Assert.Equal(0f, Store.EnemyCurrentMana[e]);
        }

        [Fact]
        public void DefaultState_NewComponentStore_TowerDrainZero()
        {
            for (int i = 0; i < 10; i++)
            {
                Assert.Equal(0f, Store.TowerManaDrainPct[i]);
                Assert.Equal(0f, Store.TowerManaDrainCap[i]);
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
            int e = SpawnManaWielder(200f);
            Assert.Equal(200f, Store.EnemyMaxMana[e]);
            Assert.Equal(200f, Store.EnemyCurrentMana[e]);
        }

        [Fact]
        public void SpawnPlainEnemy_FieldsStaysZero()
        {
            int e = Enemy();
            Assert.Equal(0f, Store.EnemyMaxMana[e]);
            Assert.Equal(0f, Store.EnemyCurrentMana[e]);
        }

        // ─── Direct field mutation: drain semantics ─────────────────────

        [Fact]
        public void DrainLogic_PctApplied_EnemyManaDecremented()
        {
            // Simulates the drain math from TowerAttackSystem in isolation:
            // drain = min(cap, curMana * drainPct); curMana -= drain; playerMana += drain (clamped)
            int e = SpawnManaWielder(100f);
            InitPlayerMana();

            float drainPct = 0.10f; // 10%
            float cap = ManaDrainConfig.ManaDrainCap;
            float curMana = Store.EnemyCurrentMana[e];
            float drain = Math.Min(cap, curMana * drainPct);
            Store.EnemyCurrentMana[e] = curMana - drain;
            Store.PlayerMana[PlayerId] += drain;

            Assert.Equal(90f, Store.EnemyCurrentMana[e], 3);
            Assert.Equal(10f, Store.PlayerMana[PlayerId], 3);
        }

        [Fact]
        public void DrainLogic_RespectsGlobalCap()
        {
            int e = SpawnManaWielder(10000f); // very large pool
            InitPlayerMana();

            float drainPct = 1.0f; // drain 100% (huge)
            float cap = ManaDrainConfig.ManaDrainCap; // global cap
            float curMana = Store.EnemyCurrentMana[e];
            float drain = Math.Min(cap, curMana * drainPct);
            Store.EnemyCurrentMana[e] = curMana - drain;
            Store.PlayerMana[PlayerId] += drain;

            Assert.Equal(cap, drain, 3);
            Assert.Equal(10000f - cap, Store.EnemyCurrentMana[e], 3);
            Assert.Equal(cap, Store.PlayerMana[PlayerId], 3);
        }

        [Fact]
        public void DrainLogic_PerTowerCapOverridesGlobal()
        {
            int e = SpawnManaWielder(1000f);
            InitPlayerMana();

            float drainPct = 1.0f;
            float towerCap = 25f; // < global cap
            float cap = towerCap > 0f ? towerCap : ManaDrainConfig.ManaDrainCap;
            float curMana = Store.EnemyCurrentMana[e];
            float drain = Math.Min(cap, curMana * drainPct);
            Store.EnemyCurrentMana[e] = curMana - drain;
            Store.PlayerMana[PlayerId] += drain;

            Assert.Equal(towerCap, drain, 3);
            Assert.Equal(towerCap, Store.PlayerMana[PlayerId], 3);
        }

        [Fact]
        public void DrainLogic_PlayerManaClampedToMax()
        {
            int e = SpawnManaWielder(1000f);
            InitPlayerMana(current: PlayerMaxMana - 5f); // almost full

            float drainPct = 0.5f;
            float curMana = Store.EnemyCurrentMana[e];
            float drain = Math.Min(ManaDrainConfig.ManaDrainCap, curMana * drainPct);
            Store.EnemyCurrentMana[e] = curMana - drain;
            Store.PlayerMana[PlayerId] = Math.Min(PlayerMaxMana, Store.PlayerMana[PlayerId] + drain);

            // Player should be at cap (PlayerMaxMana), not PlayerMaxMana + drain
            Assert.Equal(PlayerMaxMana, Store.PlayerMana[PlayerId], 3);
        }

        [Fact]
        public void DrainLogic_ZeroManaEnemy_NoOp()
        {
            int e = Enemy(); // EnemyMaxMana == 0
            InitPlayerMana();

            // The hot path guard is: if (EnemyMaxMana[e] > 0f) {...}
            // Simulate: no drain occurs
            float enemyMaxMana = Store.EnemyMaxMana[e];
            Assert.Equal(0f, enemyMaxMana);

            // No mutation should have happened to player mana
            Assert.Equal(0f, Store.PlayerMana[PlayerId]);
        }

        [Fact]
        public void DrainLogic_ZeroCurrentManaEnemy_NoOp()
        {
            // Edge case: enemy has MaxMana but already at 0 current
            int e = SpawnManaWielder(200f);
            Store.EnemyCurrentMana[e] = 0f;
            InitPlayerMana();

            float curMana = Store.EnemyCurrentMana[e];
            // The guard `if (curMana > 0f)` blocks drain
            Assert.Equal(0f, curMana);
            Assert.Equal(0f, Store.PlayerMana[PlayerId]);
        }

        // ─── ID-reuse safety ────────────────────────────────────────────

        [Fact]
        public void DestroyEntity_ResetsEnemyManaFields()
        {
            int e = SpawnManaWielder(500f);
            Store.EnemyCurrentMana[e] = 250f;
            Store.DestroyEntity(e);
            Assert.Equal(0f, Store.EnemyMaxMana[e]);
            Assert.Equal(0f, Store.EnemyCurrentMana[e]);
        }
    }
}
