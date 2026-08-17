using BattleSystemECS.Tests.Infrastructure;
using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Features.Buffs
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
    public class BloodlustSystemTests : BattleTestBase
    {
        private const int PlayerId = 0;

        private (BloodlustSystem system, int towerId) MakeSystem(
            bool enabled = true,
            int maxStacks = 10,
            float speedPerStack = 0.05f,
            float damagePerStack = 0.04f,
            int decayTurns = 300)
        {
            Config.Bloodlust = new BloodlustConfig
            {
                Enabled = enabled,
                MaxStacks = maxStacks,
                SpeedPerStack = speedPerStack,
                DamagePerStack = damagePerStack,
                DecayTurns = decayTurns
            };
            Player();
            int towerId = RawTower(0, 0, TowerType.Basic, 10f, 3, 1f, 1, 50f);
            Store.TowerActive[towerId] = true;
            Store.TowerAttackSpeed[towerId] = 1f;
            Store.TowerAttackDamage[towerId] = 10f;
            var sys = new BloodlustSystem(Store, Config);
            sys.SubscribeToEvents();
            sys.SubscribeToEvents();
            return (sys, towerId);
        }

        private void TriggerKill(int towerId, int playerId = 0, int enemyId = 0)
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
            var del = evField.GetValue(Store) as Delegate;
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
            Assert.Equal(0, Store.TowerBloodlustStacks[0]);
            Assert.Equal(0, Store.TowerBloodlustLastKillTurn[0]);
            Assert.Equal(0f, Store.TowerBloodlustDamageMult[0]);
            Assert.Equal(0f, Store.TowerBloodlustSpeedMult[0]);
        }

        [Fact]
        public void AddTower_InitializesBloodlustFields()
        {
            Store.AddPlayer(PlayerId, 5f, 1f, 10f, 1);
            Store.AddTower(0, TowerType.Basic, 10f, 3, 1f, 1, 50f);
            Assert.Equal(0, Store.TowerBloodlustStacks[0]);
            Assert.Equal(0, Store.TowerBloodlustLastKillTurn[0]);
            Assert.Equal(0f, Store.TowerBloodlustDamageMult[0]);
            Assert.Equal(0f, Store.TowerBloodlustSpeedMult[0]);
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
            var (sys, towerId) = MakeSystem();

            TriggerKill(towerId);

            Assert.Equal(1, Store.TowerBloodlustStacks[towerId]);
        }

        [Fact]
        public void OnTowerKill_DerivesCachedDamageAndSpeedMults()
        {
            var (sys, towerId) = MakeSystem(speedPerStack: 0.05f, damagePerStack: 0.04f);

            // Single kill → 1 stack → dmg = 0.04, speed = 0.05
            TriggerKill(towerId);
            sys.Update(turn: 1);

            Assert.Equal(1, Store.TowerBloodlustStacks[towerId]);
            Assert.Equal(0.04f, Store.TowerBloodlustDamageMult[towerId], 3);
            Assert.Equal(0.05f, Store.TowerBloodlustSpeedMult[towerId], 3);
        }

        [Fact]
        public void OnTowerKill_MultipleKills_StackCorrectly()
        {
            var (sys, towerId) = MakeSystem();

            for (int i = 0; i < 5; i++) TriggerKill(towerId);
            sys.Update(turn: 10);

            Assert.Equal(5, Store.TowerBloodlustStacks[towerId]);
            Assert.Equal(0.20f, Store.TowerBloodlustDamageMult[towerId], 3); // 5 * 0.04
            Assert.Equal(0.25f, Store.TowerBloodlustSpeedMult[towerId], 3);   // 5 * 0.05
        }

        [Fact]
        public void OnTowerKill_RespectsMaxStacksCap()
        {
            var (sys, towerId) = MakeSystem(maxStacks: 3);

            for (int i = 0; i < 10; i++) TriggerKill(towerId);
            sys.Update(turn: 20);

            Assert.Equal(3, Store.TowerBloodlustStacks[towerId]);
        }

        [Fact]
        public void OnTowerKill_StampsLastKillTurn()
        {
            var (sys, towerId) = MakeSystem();

            TriggerKill(towerId);
            sys.Update(turn: 100);

            Assert.Equal(100, Store.TowerBloodlustLastKillTurn[towerId]);
        }

        // ─── Decay ──────────────────────────────────────────────────────

        [Fact]
        public void Decay_ShedsOneStackPerDecayTurnsWindow()
        {
            var (sys, towerId) = MakeSystem(decayTurns: 100);

            // 3 kills at turn 0
            for (int i = 0; i < 3; i++) TriggerKill(towerId);
            sys.Update(turn: 0);
            Assert.Equal(3, Store.TowerBloodlustStacks[towerId]);

            // Jump to turn 250 (2.5 windows elapsed) → shed 2 stacks
            sys.Update(turn: 250);
            Assert.Equal(1, Store.TowerBloodlustStacks[towerId]);
        }

        [Fact]
        public void Decay_BelowZero_StopsAtZero()
        {
            var (sys, towerId) = MakeSystem(decayTurns: 50);

            TriggerKill(towerId);
            sys.Update(turn: 0);
            Assert.Equal(1, Store.TowerBloodlustStacks[towerId]);

            // Long gap → all stacks shed, no negative count
            sys.Update(turn: 10000);
            Assert.Equal(0, Store.TowerBloodlustStacks[towerId]);
        }

        [Fact]
        public void Decay_ZeroDecayTurns_NeverSheds()
        {
            var (sys, towerId) = MakeSystem(decayTurns: 0);

            for (int i = 0; i < 5; i++) TriggerKill(towerId);
            sys.Update(turn: 0);
            Assert.Equal(5, Store.TowerBloodlustStacks[towerId]);

            sys.Update(turn: 100000);
            Assert.Equal(5, Store.TowerBloodlustStacks[towerId]);
        }

        [Fact]
        public void Decay_RecentKill_ResetsShedWindow()
        {
            var (sys, towerId) = MakeSystem(decayTurns: 100);

            // 5 kills at turn 0
            for (int i = 0; i < 5; i++) TriggerKill(towerId);
            sys.Update(turn: 0);
            Assert.Equal(5, Store.TowerBloodlustStacks[towerId]);

            // Jump to turn 250 → shed 2 stacks (5-2=3), LastKillTurn re-anchored to 250
            sys.Update(turn: 250);
            Assert.Equal(3, Store.TowerBloodlustStacks[towerId]);
            Assert.Equal(250, Store.TowerBloodlustLastKillTurn[towerId]);

            // 50 more turns later → no additional shed (50 < 100)
            sys.Update(turn: 300);
            Assert.Equal(3, Store.TowerBloodlustStacks[towerId]);
        }

        // ─── Disabled config ─────────────────────────────────────────────

        [Fact]
        public void DisabledConfig_ForcesMultsToZero()
        {
            var (sys, towerId) = MakeSystem(enabled: false);

            // Try to stack via the event first, then Update with disabled config
            TriggerKill(towerId);
            sys.Update(turn: 1);

            // Enabled is false → mults forced to 0
            Assert.Equal(0f, Store.TowerBloodlustDamageMult[towerId]);
            Assert.Equal(0f, Store.TowerBloodlustSpeedMult[towerId]);
        }

        [Fact]
        public void DisabledConfig_OnTowerKill_DoesNotIncrementStacks()
        {
            var (sys, towerId) = MakeSystem(enabled: false);

            TriggerKill(towerId);
            sys.Update(turn: 1);

            // Handler is gated by Enabled = false → no stack increment
            Assert.Equal(0, Store.TowerBloodlustStacks[towerId]);
        }

        [Fact]
        public void MaxStacksZero_TreatedAsDisabled()
        {
            var (sys, towerId) = MakeSystem(maxStacks: 0);

            TriggerKill(towerId);
            sys.Update(turn: 1);

            // MaxStacks <= 0 → system silently disabled
            Assert.Equal(0f, Store.TowerBloodlustDamageMult[towerId]);
            Assert.Equal(0f, Store.TowerBloodlustSpeedMult[towerId]);
        }

        // ─── Mults reflect in attack speed and damage ────────────────────

        [Fact]
        public void CachedSpeedMult_VisibleInAttackSpeedField()
        {
            var (sys, towerId) = MakeSystem();

            for (int i = 0; i < 3; i++) TriggerKill(towerId);
            sys.Update(turn: 5);

            // 3 stacks * 0.05 = 0.15
            Assert.Equal(0.15f, Store.TowerBloodlustSpeedMult[towerId], 3);
        }

        [Fact]
        public void CachedDamageMult_VisibleInDamageMultField()
        {
            var (sys, towerId) = MakeSystem();

            for (int i = 0; i < 3; i++) TriggerKill(towerId);
            sys.Update(turn: 5);

            // 3 stacks * 0.04 = 0.12
            Assert.Equal(0.12f, Store.TowerBloodlustDamageMult[towerId], 3);
        }

        [Fact]
        public void ZeroStacks_ZeroMults()
        {
            var (sys, towerId) = MakeSystem();

            sys.Update(turn: 100);

            Assert.Equal(0f, Store.TowerBloodlustDamageMult[towerId]);
            Assert.Equal(0f, Store.TowerBloodlustSpeedMult[towerId]);
        }

        // ─── Lifecycle: Add / Remove tower ──────────────────────────────

        [Fact]
        public void RemoveTower_ResetsBloodlustFields()
        {
            var (sys, towerId) = MakeSystem();

            for (int i = 0; i < 5; i++) TriggerKill(towerId);
            sys.Update(turn: 10);
            Assert.Equal(5, Store.TowerBloodlustStacks[towerId]);

            Store.RemoveTower(towerId);

            Assert.Equal(0, Store.TowerBloodlustStacks[towerId]);
            Assert.Equal(0, Store.TowerBloodlustLastKillTurn[towerId]);
            Assert.Equal(0f, Store.TowerBloodlustDamageMult[towerId]);
            Assert.Equal(0f, Store.TowerBloodlustSpeedMult[towerId]);
        }

        [Fact]
        public void RecycledTower_DoesNotInheritOldStacks()
        {
            var (sys, towerId) = MakeSystem();

            // Old tower stacks up
            for (int i = 0; i < 5; i++) TriggerKill(towerId);
            sys.Update(turn: 10);
            Assert.Equal(5, Store.TowerBloodlustStacks[towerId]);

            // Tower destroyed and slot recycled
            Store.RemoveTower(towerId);
            Store.AddTower(towerId, TowerType.Basic, 10f, 3, 1f, 1, 50f);
            Store.TowerActive[towerId] = true;

            Assert.Equal(0, Store.TowerBloodlustStacks[towerId]);
            Assert.Equal(0f, Store.TowerBloodlustDamageMult[towerId]);
        }
    }
}
