using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
{
    /// <summary>
    /// Tests for Round 176 Direction 2: Bloodlust (per-tower kill-stacking attack-speed / damage buff).
    /// Verifies that:
    ///   - Default state: all Bloodlust fields are 0 (backward compat)
    ///   - OnTowerKill increments stacks and re-derives the cached damage / speed mults
    ///   - MaxStacks cap is enforced
    ///   - DecayTurns elapsing sheds one stack per window
    ///   - Disabled config: stacks / mults forced to 0 fast path
    ///   - Cached damage / speed mults are visible in the TowerAttack hot path
    ///   - AddTower / RemoveTower reset Bloodlust fields
    ///   - BloodlustConfig has sensible defaults
    /// </summary>
    public class BloodlustSystemTests
    {
        private const int TowerId = 0;
        private const int PlayerId = 0;

        private static GameConfig MakeConfig(
            bool enabled = true,
            int maxStacks = 10,
            float speedPerStack = 0.05f,
            float damagePerStack = 0.04f,
            int decayTurns = 300)
        {
            return new GameConfig
            {
                Bloodlust = new BloodlustConfig
                {
                    Enabled = enabled,
                    MaxStacks = maxStacks,
                    SpeedPerStack = speedPerStack,
                    DamagePerStack = damagePerStack,
                    DecayTurns = decayTurns
                }
            };
        }

        private static ComponentStore MakeStoreWithTower(GameConfig cfg)
        {
            var store = new ComponentStore();
            store.AddPlayer(PlayerId, 5f, 1f, 10f, 1);
            store.AddTower(TowerId, TowerType.Basic, 10f, 3, 1f, 1, 50f);
            store.TowerActive[TowerId] = true;
            store.TowerAttackSpeed[TowerId] = 1f;
            store.TowerAttackDamage[TowerId] = 10f;
            return store;
        }

        private static void TriggerKill(ComponentStore store, int towerId, int playerId =0, int enemyId =0)
 {
 // The kill pipeline is: enqueue → ResolveTowerKillsThisFrame drains
 // and invokes OnTowerKill?.Invoke(enemyId, playerId, towerId).
 // ResolveTowerKillsThisFrame is private; the cleanest test surface
 // is to invoke the OnTowerKill event directly via reflection. The
 // production code path is identical: same handler, same delegate.
 var evField = typeof(ComponentStore).GetField(
 "OnTowerKill",
 System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
 Assert.NotNull(evField);
 var del = evField.GetValue(store) as Delegate;
 if (del != null)
 {
 // Each subscriber takes (enemyId, playerId, towerId).
 foreach (var subscriber in del.GetInvocationList())
 {
 subscriber.DynamicInvoke(enemyId, playerId, towerId);
 }
 }
 }

        // ─── Default state (backward compat) ──────────────────────────────

        [Fact]
        public void DefaultState_NewComponentStore_AllBloodlustFieldsZero()
        {
            var store = new ComponentStore();
            Assert.Equal(0, store.TowerBloodlustStacks[TowerId]);
            Assert.Equal(0, store.TowerBloodlustLastKillTurn[TowerId]);
            Assert.Equal(0f, store.TowerBloodlustDamageMult[TowerId]);
            Assert.Equal(0f, store.TowerBloodlustSpeedMult[TowerId]);
        }

        [Fact]
        public void AddTower_InitializesBloodlustFields()
        {
            var store = new ComponentStore();
            store.AddPlayer(PlayerId, 5f, 1f, 10f, 1);
            store.AddTower(TowerId, TowerType.Basic, 10f, 3, 1f, 1, 50f);
            Assert.Equal(0, store.TowerBloodlustStacks[TowerId]);
            Assert.Equal(0, store.TowerBloodlustLastKillTurn[TowerId]);
            Assert.Equal(0f, store.TowerBloodlustDamageMult[TowerId]);
            Assert.Equal(0f, store.TowerBloodlustSpeedMult[TowerId]);
        }

        [Fact]
        public void BloodlustConfig_HasSensibleDefaults()
        {
            var cfg = new BloodlustConfig();
            Assert.True(cfg.Enabled);
            Assert.True(cfg.MaxStacks > 0);
            Assert.True(cfg.SpeedPerStack > 0f && cfg.SpeedPerStack <= 1f);
            Assert.True(cfg.DamagePerStack > 0f && cfg.DamagePerStack <= 1f);
            Assert.True(cfg.DecayTurns >= 0);
        }

        // ─── OnTowerKill → stack increment + cached mults ────────────────

        [Fact]
        public void OnTowerKill_IncrementsStacks()
        {
            var cfg = MakeConfig();
            var store = MakeStoreWithTower(cfg);
            var sys = new BloodlustSystem(store, cfg);
            sys.SubscribeToEvents();
            sys.SubscribeToEvents();

            TriggerKill(store, TowerId);

            Assert.Equal(1, store.TowerBloodlustStacks[TowerId]);
        }

        [Fact]
        public void OnTowerKill_DerivesCachedDamageAndSpeedMults()
        {
            var cfg = MakeConfig(speedPerStack: 0.05f, damagePerStack: 0.04f);
            var store = MakeStoreWithTower(cfg);
            var sys = new BloodlustSystem(store, cfg);
            sys.SubscribeToEvents();
            sys.SubscribeToEvents();

            // Single kill → 1 stack → dmg = 0.04, speed = 0.05
            TriggerKill(store, TowerId);
            sys.Update(turn: 1);

            Assert.Equal(1, store.TowerBloodlustStacks[TowerId]);
            Assert.Equal(0.04f, store.TowerBloodlustDamageMult[TowerId], 3);
            Assert.Equal(0.05f, store.TowerBloodlustSpeedMult[TowerId], 3);
        }

        [Fact]
        public void OnTowerKill_MultipleKills_StackCorrectly()
        {
            var cfg = MakeConfig();
            var store = MakeStoreWithTower(cfg);
            var sys = new BloodlustSystem(store, cfg);
            sys.SubscribeToEvents();
            sys.SubscribeToEvents();

            for (int i = 0; i < 5; i++) TriggerKill(store, TowerId);
            sys.Update(turn: 10);

            Assert.Equal(5, store.TowerBloodlustStacks[TowerId]);
            Assert.Equal(0.20f, store.TowerBloodlustDamageMult[TowerId], 3); // 5 * 0.04
            Assert.Equal(0.25f, store.TowerBloodlustSpeedMult[TowerId], 3);   // 5 * 0.05
        }

        [Fact]
        public void OnTowerKill_RespectsMaxStacksCap()
        {
            var cfg = MakeConfig(maxStacks: 3);
            var store = MakeStoreWithTower(cfg);
            var sys = new BloodlustSystem(store, cfg);
            sys.SubscribeToEvents();
            sys.SubscribeToEvents();

            for (int i = 0; i < 10; i++) TriggerKill(store, TowerId);
            sys.Update(turn: 20);

            Assert.Equal(3, store.TowerBloodlustStacks[TowerId]);
        }

        [Fact]
        public void OnTowerKill_StampsLastKillTurn()
        {
            var cfg = MakeConfig();
            var store = MakeStoreWithTower(cfg);
            var sys = new BloodlustSystem(store, cfg);
            sys.SubscribeToEvents();
            sys.SubscribeToEvents();

            TriggerKill(store, TowerId);
            sys.Update(turn: 100);

            Assert.Equal(100, store.TowerBloodlustLastKillTurn[TowerId]);
        }

        // ─── Decay ──────────────────────────────────────────────────────

        [Fact]
        public void Decay_ShedsOneStackPerDecayTurnsWindow()
        {
            var cfg = MakeConfig(decayTurns: 100);
            var store = MakeStoreWithTower(cfg);
            var sys = new BloodlustSystem(store, cfg);
            sys.SubscribeToEvents();
            sys.SubscribeToEvents();

            // 3 kills at turn 0
            for (int i = 0; i < 3; i++) TriggerKill(store, TowerId);
            sys.Update(turn: 0);
            Assert.Equal(3, store.TowerBloodlustStacks[TowerId]);

            // Jump to turn 250 (2.5 windows elapsed) → shed 2 stacks
            sys.Update(turn: 250);
            Assert.Equal(1, store.TowerBloodlustStacks[TowerId]);
        }

        [Fact]
        public void Decay_BelowZero_StopsAtZero()
        {
            var cfg = MakeConfig(decayTurns: 50);
            var store = MakeStoreWithTower(cfg);
            var sys = new BloodlustSystem(store, cfg);
            sys.SubscribeToEvents();
            sys.SubscribeToEvents();

            TriggerKill(store, TowerId);
            sys.Update(turn: 0);
            Assert.Equal(1, store.TowerBloodlustStacks[TowerId]);

            // Long gap → all stacks shed, no negative count
            sys.Update(turn: 10000);
            Assert.Equal(0, store.TowerBloodlustStacks[TowerId]);
        }

        [Fact]
        public void Decay_ZeroDecayTurns_NeverSheds()
        {
            var cfg = MakeConfig(decayTurns: 0);
            var store = MakeStoreWithTower(cfg);
            var sys = new BloodlustSystem(store, cfg);
            sys.SubscribeToEvents();
            sys.SubscribeToEvents();

            for (int i = 0; i < 5; i++) TriggerKill(store, TowerId);
            sys.Update(turn: 0);
            Assert.Equal(5, store.TowerBloodlustStacks[TowerId]);

            sys.Update(turn: 100000);
            Assert.Equal(5, store.TowerBloodlustStacks[TowerId]);
        }

        [Fact]
        public void Decay_RecentKill_ResetsShedWindow()
        {
            var cfg = MakeConfig(decayTurns: 100);
            var store = MakeStoreWithTower(cfg);
            var sys = new BloodlustSystem(store, cfg);
            sys.SubscribeToEvents();
            sys.SubscribeToEvents();

            // 5 kills at turn 0
            for (int i = 0; i < 5; i++) TriggerKill(store, TowerId);
            sys.Update(turn: 0);
            Assert.Equal(5, store.TowerBloodlustStacks[TowerId]);

            // Jump to turn 250 → shed 2 stacks (5-2=3), LastKillTurn re-anchored to 250
            sys.Update(turn: 250);
            Assert.Equal(3, store.TowerBloodlustStacks[TowerId]);
            Assert.Equal(250, store.TowerBloodlustLastKillTurn[TowerId]);

            // 50 more turns later → no additional shed (50 < 100)
            sys.Update(turn: 300);
            Assert.Equal(3, store.TowerBloodlustStacks[TowerId]);
        }

        // ─── Disabled config ─────────────────────────────────────────────

        [Fact]
        public void DisabledConfig_ForcesMultsToZero()
        {
            var cfg = MakeConfig(enabled: false);
            var store = MakeStoreWithTower(cfg);
            var sys = new BloodlustSystem(store, cfg);
            sys.SubscribeToEvents();
            sys.SubscribeToEvents();

            // Try to stack via the event first, then Update with disabled config
            TriggerKill(store, TowerId);
            sys.Update(turn: 1);

            // Enabled is false → mults forced to 0
            Assert.Equal(0f, store.TowerBloodlustDamageMult[TowerId]);
            Assert.Equal(0f, store.TowerBloodlustSpeedMult[TowerId]);
        }

        [Fact]
        public void DisabledConfig_OnTowerKill_DoesNotIncrementStacks()
        {
            var cfg = MakeConfig(enabled: false);
            var store = MakeStoreWithTower(cfg);
            var sys = new BloodlustSystem(store, cfg);
            sys.SubscribeToEvents();
            sys.SubscribeToEvents();

            TriggerKill(store, TowerId);
            sys.Update(turn: 1);

            // Handler is gated by Enabled = false → no stack increment
            Assert.Equal(0, store.TowerBloodlustStacks[TowerId]);
        }

        [Fact]
        public void MaxStacksZero_TreatedAsDisabled()
        {
            var cfg = MakeConfig(maxStacks: 0);
            var store = MakeStoreWithTower(cfg);
            var sys = new BloodlustSystem(store, cfg);
            sys.SubscribeToEvents();
            sys.SubscribeToEvents();

            TriggerKill(store, TowerId);
            sys.Update(turn: 1);

            // MaxStacks <= 0 → system silently disabled
            Assert.Equal(0f, store.TowerBloodlustDamageMult[TowerId]);
            Assert.Equal(0f, store.TowerBloodlustSpeedMult[TowerId]);
        }

        // ─── Mults reflect in attack speed and damage ────────────────────

        [Fact]
        public void CachedSpeedMult_VisibleInAttackSpeedField()
        {
            var cfg = MakeConfig();
            var store = MakeStoreWithTower(cfg);
            var sys = new BloodlustSystem(store, cfg);
            sys.SubscribeToEvents();
            sys.SubscribeToEvents();

            for (int i = 0; i < 3; i++) TriggerKill(store, TowerId);
            sys.Update(turn: 5);

            // 3 stacks * 0.05 = 0.15
            Assert.Equal(0.15f, store.TowerBloodlustSpeedMult[TowerId], 3);
        }

        [Fact]
        public void CachedDamageMult_VisibleInDamageMultField()
        {
            var cfg = MakeConfig();
            var store = MakeStoreWithTower(cfg);
            var sys = new BloodlustSystem(store, cfg);
            sys.SubscribeToEvents();
            sys.SubscribeToEvents();

            for (int i = 0; i < 3; i++) TriggerKill(store, TowerId);
            sys.Update(turn: 5);

            // 3 stacks * 0.04 = 0.12
            Assert.Equal(0.12f, store.TowerBloodlustDamageMult[TowerId], 3);
        }

        [Fact]
        public void ZeroStacks_ZeroMults()
        {
            var cfg = MakeConfig();
            var store = MakeStoreWithTower(cfg);
            var sys = new BloodlustSystem(store, cfg);
            sys.SubscribeToEvents();
            sys.SubscribeToEvents();

            sys.Update(turn: 100);

            Assert.Equal(0f, store.TowerBloodlustDamageMult[TowerId]);
            Assert.Equal(0f, store.TowerBloodlustSpeedMult[TowerId]);
        }

        // ─── Lifecycle: Add / Remove tower ──────────────────────────────

        [Fact]
        public void RemoveTower_ResetsBloodlustFields()
        {
            var cfg = MakeConfig();
            var store = MakeStoreWithTower(cfg);
            var sys = new BloodlustSystem(store, cfg);
            sys.SubscribeToEvents();
            sys.SubscribeToEvents();

            for (int i = 0; i < 5; i++) TriggerKill(store, TowerId);
            sys.Update(turn: 10);
            Assert.Equal(5, store.TowerBloodlustStacks[TowerId]);

            store.RemoveTower(TowerId);

            Assert.Equal(0, store.TowerBloodlustStacks[TowerId]);
            Assert.Equal(0, store.TowerBloodlustLastKillTurn[TowerId]);
            Assert.Equal(0f, store.TowerBloodlustDamageMult[TowerId]);
            Assert.Equal(0f, store.TowerBloodlustSpeedMult[TowerId]);
        }

        [Fact]
        public void RecycledTower_DoesNotInheritOldStacks()
        {
            var cfg = MakeConfig();
            var store = MakeStoreWithTower(cfg);
            var sys = new BloodlustSystem(store, cfg);
            sys.SubscribeToEvents();
            sys.SubscribeToEvents();

            // Old tower stacks up
            for (int i = 0; i < 5; i++) TriggerKill(store, TowerId);
            sys.Update(turn: 10);
            Assert.Equal(5, store.TowerBloodlustStacks[TowerId]);

            // Tower destroyed and slot recycled
            store.RemoveTower(TowerId);
            store.AddTower(TowerId, TowerType.Basic, 10f, 3, 1f, 1, 50f);
            store.TowerActive[TowerId] = true;

            Assert.Equal(0, store.TowerBloodlustStacks[TowerId]);
            Assert.Equal(0f, store.TowerBloodlustDamageMult[TowerId]);
        }
    }
}
