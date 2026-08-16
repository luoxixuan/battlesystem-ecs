using BattleSystemECS.Tests.Infrastructure;
using System;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Mechanisms.Combat
{
    /// <summary>
    /// Tests for Round 200 Direction 5: Death Mark System.
    /// Stack-based execute counter applied by tower/player attacks.
    /// Verifies:
    ///   1. Default state: all Death Mark fields are 0 (zero-overhead, opt-out sentinel)
    ///   2. AddDeathMark: increments stacks, resets timer
    ///   3. AddDeathMark: opt-out (MaxStacks == 0) is no-op
    ///   4. AddDeathMark: ExecuteImmune enemies cannot be Death Marked
    ///   5. AddDeathMark: stacks clamped at hard cap
    ///   6. AddDeathMark: at full stacks, OnDeathMarkFull fires and enemy queued for death
    ///   7. AddDeathMark: invalid enemyId / stacksToAdd no-op
    ///   8. GetDamageMultiplier: unmarked = 1.0
    ///   9. GetDamageMultiplier: stacks * bonusPerStack adds multiplicatively
    ///  10. Update: timer decrements; on expiry drops 1 stack + re-arms
    ///  11. Update: zero-stack enemies skip work (fast path)
    ///  12. Update: at stacks==0, timer stops
    ///  13. ClearDeathMark: resets stacks + timer + latch
    ///  14. IsMarked helper reflects stack count
    ///  15. _fullFired latch: re-fires when stacks drop back below cap
    ///  16. Invulnerable enemies are NOT auto-executed (HP stays > 0)
    ///  17. DeathMarkConfig defaults
    ///  18. LoadConfig override replaces config
    /// </summary>
    public class DeathMarkSystemTests
    {
        private const int PlayerId = 0;
        private const float DeltaTime = 1f / 60f;

        // ── Test helpers ────────────────────────────────────────────────

        private static (DeathMarkSystem system, ComponentStore store) MakeSystem(DeathMarkConfig? config = null)
        {
            var store = new ComponentStore();
            store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            var system = new DeathMarkSystem(store, PlayerId);
            if (config != null) system.LoadConfig(config);
            return (system, store);
        }

        /// <summary>Spawn a Death-Mark-eligible enemy with the given maxStacks / bonusPerStack.</summary>
        private static int MakeMarkableEnemy(ComponentStore store, int maxStacks = 10, float bonusPerStack = 0.05f)
        {
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 5f, 10, 1, "TestEnemy");
            store.EnemyDeathMarkMaxStacks[eid] = maxStacks;
            store.EnemyDeathMarkBonusPerStack[eid] = bonusPerStack;
            return eid;
        }

        // ── 1. Default state ────────────────────────────────────────────
        [Fact]
        public void DefaultState_AllFieldsZero()
        {
            var store = new ComponentStore();
            Assert.Equal(0, store.EnemyDeathMarkStacks[0]);
            Assert.Equal(0f, store.EnemyDeathMarkTimer[0]);
            Assert.Equal(0, store.EnemyDeathMarkMaxStacks[0]);
            Assert.Equal(0f, store.EnemyDeathMarkBonusPerStack[0]);
        }

        // ── 2. AddDeathMark increments stacks ───────────────────────────
        [Fact]
        public void AddDeathMark_IncrementsStacks()
        {
            var (sys, store) = MakeSystem();
            int eid = MakeMarkableEnemy(store, maxStacks: 10);
            int newStacks = sys.AddDeathMark(eid, 1);
            Assert.Equal(1, newStacks);
            Assert.Equal(1, store.EnemyDeathMarkStacks[eid]);
            Assert.True(store.EnemyDeathMarkTimer[eid] > 0f);
        }

        [Fact]
        public void AddDeathMark_StacksAccumulate()
        {
            var (sys, store) = MakeSystem();
            int eid = MakeMarkableEnemy(store, maxStacks: 10);
            sys.AddDeathMark(eid, 3);
            sys.AddDeathMark(eid, 2);
            Assert.Equal(5, store.EnemyDeathMarkStacks[eid]);
        }

        // ── 3. Opt-out sentinel ─────────────────────────────────────────
        [Fact]
        public void AddDeathMark_OptOutEnemyIsNoOp()
        {
            var (sys, store) = MakeSystem();
            // MakeMarkableEnemy sets MaxStacks; this one stays at default 0
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 5f, 10, 1, "E");
            int result = sys.AddDeathMark(eid, 5);
            Assert.Equal(0, result);
            Assert.Equal(0, store.EnemyDeathMarkStacks[eid]);
        }

        // ── 4. ExecuteImmune cannot be Death Marked ─────────────────────
        [Fact]
        public void AddDeathMark_ExecuteImmuneIsNoOp()
        {
            var (sys, store) = MakeSystem();
            int eid = MakeMarkableEnemy(store, maxStacks: 10);
            store.EnemyExecuteImmune[eid] = true;
            int result = sys.AddDeathMark(eid, 5);
            Assert.Equal(0, result);
            Assert.Equal(0, store.EnemyDeathMarkStacks[eid]);
        }

        // ── 5. Hard cap clamping ────────────────────────────────────────
        [Fact]
        public void AddDeathMark_HardCappedAtConfigMaxStackCap()
        {
            var (sys, store) = MakeSystem(new DeathMarkConfig { DecayInterval = 1f, MaxStackCap = 5 });
            int eid = MakeMarkableEnemy(store, maxStacks: 20); // per-enemy cap 20, hard cap 5
            int result = sys.AddDeathMark(eid, 100);
            Assert.Equal(5, result);
            Assert.Equal(5, store.EnemyDeathMarkStacks[eid]);
        }

        // ── 6. Full stacks → auto-execute + event ───────────────────────
        [Fact]
        public void AddDeathMark_AtCapFiresEventAndQueuesDeath()
        {
            var (sys, store) = MakeSystem();
            int eid = MakeMarkableEnemy(store, maxStacks: 3);

            int firedEnemy = -1;
            int firedPlayer = -1;
            int firedStacks = -1;
            sys.OnDeathMarkFull += (en, pl, st) => { firedEnemy = en; firedPlayer = pl; firedStacks = st; };

            sys.AddDeathMark(eid, 3);
            Assert.Equal(3, store.EnemyDeathMarkStacks[eid]);
            Assert.Equal(eid, firedEnemy);
            Assert.Equal(PlayerId, firedPlayer);
            Assert.Equal(3, firedStacks);

            // HP zeroed and death queued
            Assert.Equal(0f, store.EnemyHealth[eid]);

            // Resolve death so the enemy is no longer active
            store.ResolveEnemiesKilledThisFrame();
            Assert.False(store.EnemyActive[eid]);
        }

        // ── 7. Invalid inputs ───────────────────────────────────────────
        [Fact]
        public void AddDeathMark_InvalidInputsNoOp()
        {
            var (sys, store) = MakeSystem();
            int eid = MakeMarkableEnemy(store);

            // negative enemyId
            Assert.Equal(0, sys.AddDeathMark(-1, 1));
            // out-of-range enemyId
            Assert.Equal(0, sys.AddDeathMark(ComponentStore.MAX_ENTITIES + 5, 1));
            // inactive enemy
            store.EnemyActive[eid] = false;
            Assert.Equal(0, sys.AddDeathMark(eid, 1));
            // zero stacks
            store.EnemyActive[eid] = true;
            Assert.Equal(0, sys.AddDeathMark(eid, 0));
            // negative stacks
            Assert.Equal(0, sys.AddDeathMark(eid, -1));
        }

        // ── 8. GetDamageMultiplier: unmarked ────────────────────────────
        [Fact]
        public void GetDamageMultiplier_UnmarkedIsOne()
        {
            var (sys, store) = MakeSystem();
            int eid = MakeMarkableEnemy(store);
            Assert.Equal(1.0f, sys.GetDamageMultiplier(eid));
            // Also covers invalid id
            Assert.Equal(1.0f, sys.GetDamageMultiplier(-1));
        }

        // ── 9. GetDamageMultiplier: stacks * bonusPerStack ──────────────
        [Fact]
        public void GetDamageMultiplier_ScalesWithStacks()
        {
            var (sys, store) = MakeSystem();
            int eid = MakeMarkableEnemy(store, maxStacks: 10, bonusPerStack: 0.05f);
            sys.AddDeathMark(eid, 4);
            // 1.0 + 4 * 0.05 = 1.20
            Assert.Equal(1.20f, sys.GetDamageMultiplier(eid), 3);
        }

        // ── 10. Update: decay timer + drop stack on expiry ──────────────
        [Fact]
        public void Update_TimerExpiryDropsOneStack()
        {
            var (sys, store) = MakeSystem(new DeathMarkConfig { DecayInterval = 1.0f, MaxStackCap = 50 });
            int eid = MakeMarkableEnemy(store, maxStacks: 10);
            sys.AddDeathMark(eid, 3);
            Assert.Equal(3, store.EnemyDeathMarkStacks[eid]);

            // Run update past the decay interval
            sys.Update(1.5f);
            Assert.Equal(2, store.EnemyDeathMarkStacks[eid]);
            Assert.True(store.EnemyDeathMarkTimer[eid] > 0f); // re-armed
        }

        // ── 11. Update fast-path ────────────────────────────────────────
        [Fact]
        public void Update_UnmarkedEnemySkipsWork()
        {
            var (sys, store) = MakeSystem();
            int eid = MakeMarkableEnemy(store);
            // Stacks == 0, Timer == 0 — fast path should not touch fields
            store.EnemyDeathMarkStacks[eid] = 0;
            store.EnemyDeathMarkTimer[eid] = 0f;
            sys.Update(5f);
            Assert.Equal(0, store.EnemyDeathMarkStacks[eid]);
            Assert.Equal(0f, store.EnemyDeathMarkTimer[eid]);
        }

        // ── 12. Update: stacks→0 stops timer ────────────────────────────
        [Fact]
        public void Update_StacksReachZeroStopsTimer()
        {
            var (sys, store) = MakeSystem(new DeathMarkConfig { DecayInterval = 1.0f, MaxStackCap = 50 });
            int eid = MakeMarkableEnemy(store, maxStacks: 10);
            sys.AddDeathMark(eid, 1);
            // Run past decay
            sys.Update(1.5f);
            Assert.Equal(0, store.EnemyDeathMarkStacks[eid]);
            Assert.Equal(0f, store.EnemyDeathMarkTimer[eid]);
        }

        // ── 13. ClearDeathMark ──────────────────────────────────────────
        [Fact]
        public void ClearDeathMark_ResetsEverything()
        {
            var (sys, store) = MakeSystem();
            int eid = MakeMarkableEnemy(store, maxStacks: 3);
            sys.AddDeathMark(eid, 3); // full → latch fired
            Assert.True(sys.IsFullFired(eid));

            sys.ClearDeathMark(eid);
            Assert.Equal(0, store.EnemyDeathMarkStacks[eid]);
            Assert.Equal(0f, store.EnemyDeathMarkTimer[eid]);
            Assert.False(sys.IsFullFired(eid));
        }

        // ── 14. IsMarked helper ─────────────────────────────────────────
        [Fact]
        public void IsMarked_ReflectsStackCount()
        {
            var (sys, store) = MakeSystem();
            int eid = MakeMarkableEnemy(store);
            Assert.False(sys.IsMarked(eid));
            sys.AddDeathMark(eid, 1);
            Assert.True(sys.IsMarked(eid));
        }

        // ── 15. Full-stack latch re-arms after decay ────────────────────
        [Fact]
        public void FullFiredLatch_ReArmsAfterDecayBelowCap()
        {
            var (sys, store) = MakeSystem(new DeathMarkConfig { DecayInterval = 0.5f, MaxStackCap = 50 });
            int eid = MakeMarkableEnemy(store, maxStacks: 3);

            int firedCount = 0;
            sys.OnDeathMarkFull += (_, _, _) => firedCount++;

            sys.AddDeathMark(eid, 3); // first firing
            Assert.Equal(1, firedCount);

            // Force a decay to drop below cap
            sys.Update(1.0f);
            Assert.True(store.EnemyDeathMarkStacks[eid] < 3);

            // Bring back to full
            sys.AddDeathMark(eid, 3);
            Assert.Equal(2, firedCount);
        }

        // ── 16. Invulnerable enemies NOT auto-executed ──────────────────
        [Fact]
        public void AddDeathMark_InvulnerableDoesNotAutoExecute()
        {
            var (sys, store) = MakeSystem();
            int eid = MakeMarkableEnemy(store, maxStacks: 3);
            store.EnemyIsInvulnerable[eid] = true;
            sys.AddDeathMark(eid, 3);

            // HP must stay positive (queue death is skipped)
            Assert.True(store.EnemyHealth[eid] > 0f);
            // Stacks still at cap, but enemy not queued
            Assert.Equal(3, store.EnemyDeathMarkStacks[eid]);
            Assert.True(store.EnemyActive[eid]);
        }

        // ── 17. DeathMarkConfig defaults ────────────────────────────────
        [Fact]
        public void DeathMarkConfig_Defaults()
        {
            var d = DeathMarkConfig.Default;
            Assert.Equal(DeathMarkSubsystemConfig.DefaultDecayInterval, d.DecayInterval);
            Assert.Equal(DeathMarkSubsystemConfig.DefaultMaxStackCap, d.MaxStackCap);
        }

        // ── 18. LoadConfig override ─────────────────────────────────────
        [Fact]
        public void LoadConfig_OverridesConfig()
        {
            var (sys, _) = MakeSystem();
            var custom = new DeathMarkConfig { DecayInterval = 7.5f, MaxStackCap = 25 };
            sys.LoadConfig(custom);
            Assert.Equal(7.5f, sys.Config.DecayInterval);
            Assert.Equal(25, sys.Config.MaxStackCap);
        }

        // ── 19. Negative deltaTime is no-op ─────────────────────────────
        [Fact]
        public void Update_NegativeDeltaIsNoOp()
        {
            var (sys, store) = MakeSystem();
            int eid = MakeMarkableEnemy(store);
            sys.AddDeathMark(eid, 2);
            float originalTimer = store.EnemyDeathMarkTimer[eid];
            sys.Update(-1f);
            Assert.Equal(2, store.EnemyDeathMarkStacks[eid]);
            Assert.Equal(originalTimer, store.EnemyDeathMarkTimer[eid]);
        }

        // ── 21. Regression: hardCap is the event threshold (Claude bug scan fix) ───
        // When config.MaxStackCap < per-enemy EnemyDeathMarkMaxStacks, the event
        // should still fire when stacks cross the hardCap (effective ceiling), not
        // only when they cross the unreachable per-enemy cap.
        [Fact]
        public void AddDeathMark_HardCapIsEventThreshold()
        {
            var (sys, store) = MakeSystem(new DeathMarkConfig { DecayInterval = 1f, MaxStackCap = 3 });
            int eid = MakeMarkableEnemy(store, maxStacks: 20); // per-enemy 20, hard cap 3

            int firedCount = 0;
            sys.OnDeathMarkFull += (_, _, _) => firedCount++;

            sys.AddDeathMark(eid, 3);
            Assert.Equal(3, store.EnemyDeathMarkStacks[eid]);
            Assert.Equal(1, firedCount); // event fires at hardCap, not per-enemy cap
            Assert.Equal(0f, store.EnemyHealth[eid]); // auto-executed
        }
        [Fact]
        public void OnEnemyDestroyed_ClearsLatch()
        {
            var (sys, store) = MakeSystem();
            int eid = MakeMarkableEnemy(store, maxStacks: 3);
            sys.AddDeathMark(eid, 3);
            Assert.True(sys.IsFullFired(eid));

            sys.OnEnemyDestroyed(eid);
            Assert.False(sys.IsFullFired(eid));
        }
    }
}