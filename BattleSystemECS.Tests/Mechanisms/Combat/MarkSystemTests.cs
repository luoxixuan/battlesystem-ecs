using BattleSystemECS.Tests.Infrastructure;
using System;
using System.Collections.Generic;
using Xunit;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Mechanisms.Combat
{
    /// <summary>
    /// Tests for Round 107 Direction 6: Target Mark Stack / Decay subsystem.
    /// Verifies that:
    ///   - Default state: all mark fields are zero / inert (zero-overhead path)
    ///   - MarkConfig exposes sensible defaults
    ///   - AddMark() increments stacks, resets decay timer, caps at threshold
    ///   - AddMark() is a no-op when EnemyMarkMaxThreshold == 0 (opt-out)
    ///   - AddMark() is a no-op on invalid / inactive enemies
    ///   - Decay: timer ticks down, one stack consumed per interval
    ///   - Decay: timer stops when stacks reach 0
    ///   - Threshold event fires once on transition from &lt; threshold to >= threshold
    ///   - Threshold event re-fires after stacks drop back below threshold
    ///   - ClearMark() resets stacks + timer + latch (but keeps MaxThreshold = opt-in)
    ///   - DestroyEntity resets all mark fields (no ID-reuse leakage)
    ///   - OnEnemyDestroyed() resets the threshold-fired latch
    ///   - Custom MarkConfig (DecayInterval, MaxStackCap) takes effect
    ///   - MaxStackCap overrides threshold when cap &lt; threshold
    /// </summary>
    public class MarkSystemTests : BattleTestBase
    {
        private const int PlayerId = 0;
        private const float DeltaTime = 1f / 60f;

        // ── Default state & config ───────────────────────────────────────

        [Fact]
        public void DefaultState_AllMarkFieldsInert()
        {
            Assert.Equal(0, Store.EnemyMarkStacks[0]);
            Assert.Equal(0f, Store.EnemyMarkDecayTimer[0]);
            Assert.Equal(0, Store.EnemyMarkMaxThreshold[0]);
        }

        [Fact]
        public void MarkConfig_ExposesExpectedDefaults()
        {
            Assert.Equal(1.0f, MarkConfig.Default.DecayInterval);
            Assert.Equal(100, MarkConfig.Default.MaxStackCap);
        }

        [Fact]
        public void MarkSubsystemConfig_ExposesRecommendedThresholds()
        {
            // Sanity: thresholds follow a ladder (low/med/high) for the 3 default mark types.
            Assert.True(MarkSubsystemConfig.RecommendedFrostThreshold > 0);
            Assert.True(MarkSubsystemConfig.RecommendedScorchThreshold > MarkSubsystemConfig.RecommendedFrostThreshold);
            Assert.True(MarkSubsystemConfig.RecommendedVoltThreshold < MarkSubsystemConfig.RecommendedFrostThreshold);
        }

        // ── Opt-in / opt-out behavior ────────────────────────────────────

        [Fact]
        public void AddMark_NoOptIn_NoOp()
        {
            var system = MakeSystem();
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "E"; });
            // EnemyMarkMaxThreshold defaults to 0 (opt-out)
            int result = system.AddMark(eid, 1);
            Assert.Equal(0, result);
            Assert.Equal(0, Store.EnemyMarkStacks[eid]);
            Assert.Equal(0f, Store.EnemyMarkDecayTimer[eid]);
        }

        [Fact]
        public void AddMark_InvalidEnemy_NoOp()
        {
            var system = MakeSystem();
            // Out-of-range
            Assert.Equal(0, system.AddMark(-1, 1));
            Assert.Equal(0, system.AddMark(ComponentStore.MAX_ENTITIES, 1));
            Assert.Equal(0, system.AddMark(ComponentStore.MAX_ENTITIES + 100, 1));
        }

        [Fact]
        public void AddMark_InactiveEnemy_NoOp()
        {
            var system = MakeSystem();
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "E"; });
            Store.EnemyMarkMaxThreshold[eid] = 5;
            // Manually mark inactive
            Store.EnemyActive[eid] = false;
            int result = system.AddMark(eid, 1);
            Assert.Equal(0, result);
        }

        [Fact]
        public void AddMark_NonPositiveStacks_NoOp()
        {
            var system = MakeSystem();
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "E"; });
            Store.EnemyMarkMaxThreshold[eid] = 5;
            Assert.Equal(0, system.AddMark(eid, 0));
            Assert.Equal(0, system.AddMark(eid, -1));
            Assert.Equal(0, Store.EnemyMarkStacks[eid]);
        }

        // ── Basic increment + cap behavior ────────────────────────────────

        [Fact]
        public void AddMark_IncrementsAndResetsTimer()
        {
            var system = MakeSystem();
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "E"; });
            Store.EnemyMarkMaxThreshold[eid] = 5;
            int s1 = system.AddMark(eid);
            Assert.Equal(1, s1);
            Assert.Equal(1, Store.EnemyMarkStacks[eid]);
            Assert.Equal(1.0f, Store.EnemyMarkDecayTimer[eid]);
            // Add more
            int s2 = system.AddMark(eid, 2);
            Assert.Equal(3, s2);
            Assert.Equal(3, Store.EnemyMarkStacks[eid]);
            // Timer reset to 1.0
            Assert.Equal(1.0f, Store.EnemyMarkDecayTimer[eid]);
        }

        [Fact]
        public void AddMark_CapsAtThreshold()
        {
            var system = MakeSystem();
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "E"; });
            Store.EnemyMarkMaxThreshold[eid] = 5;
            // Add 10 in one go → cap at 5
            int s = system.AddMark(eid, 10);
            Assert.Equal(5, s);
            Assert.Equal(5, Store.EnemyMarkStacks[eid]);
        }

        [Fact]
        public void AddMark_CapsAtMaxStackCapWhenSmaller()
        {
            var system = MakeSystem();
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "E"; });
            Store.EnemyMarkMaxThreshold[eid] = 50;
            system.LoadConfig(new MarkConfig { DecayInterval = 1f, MaxStackCap = 3 });
            // Add 10 in one go → cap at 3 (MaxStackCap < threshold)
            int s = system.AddMark(eid, 10);
            Assert.Equal(3, s);
        }

        [Fact]
        public void AddMark_MaxStackCapZero_NotApplied()
        {
            // MaxStackCap = 0 means "no cap" (use threshold only)
            var system = MakeSystem();
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "E"; });
            Store.EnemyMarkMaxThreshold[eid] = 50;
            system.LoadConfig(new MarkConfig { DecayInterval = 1f, MaxStackCap = 0 });
            int s = system.AddMark(eid, 30);
            Assert.Equal(30, s);
        }

        // ── Threshold event firing ───────────────────────────────────────

        [Fact]
        public void AddMark_ThresholdEvent_FiresOnCrossing()
        {
            var system = MakeSystem();
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "E"; });
            Store.EnemyMarkMaxThreshold[eid] = 5;
            int firedCount = 0;
            int firedEnemyId = -1;
            int firedStacks = 0;
            system.OnMarkThreshold += (eidArg, pid, stacks) =>
            {
                firedCount++;
                firedEnemyId = eidArg;
                firedStacks = stacks;
            };
            // Add 3 stacks (below threshold 5) → no event
            system.AddMark(eid, 3);
            Assert.Equal(0, firedCount);
            // Add 2 more (now at 5, crossing threshold) → event fires
            system.AddMark(eid, 2);
            Assert.Equal(1, firedCount);
            Assert.Equal(eid, firedEnemyId);
            Assert.Equal(5, firedStacks);
        }

        [Fact]
        public void AddMark_ThresholdEvent_DoesNotRefireWhileStaysAbove()
        {
            var system = MakeSystem();
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "E"; });
            Store.EnemyMarkMaxThreshold[eid] = 5;
            int firedCount = 0;
            system.OnMarkThreshold += (eidArg, pid, stacks) => firedCount++;
            // Cross threshold
            system.AddMark(eid, 5);
            Assert.Equal(1, firedCount);
            // Keep adding — still at/above threshold
            system.AddMark(eid, 3); // capped at 5
            Assert.Equal(1, firedCount);
            system.AddMark(eid, 1); // still capped
            Assert.Equal(1, firedCount);
        }

        [Fact]
        public void AddMark_ThresholdEvent_RefiresAfterDecayBelow()
        {
            var system = MakeSystem();
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "E"; });
            Store.EnemyMarkMaxThreshold[eid] = 5;
            int firedCount = 0;
            system.OnMarkThreshold += (eidArg, pid, stacks) => firedCount++;
            // Cross threshold
            system.AddMark(eid, 5);
            Assert.Equal(1, firedCount);
            // Decay stacks below threshold (5 frames of decay)
            for (int i = 0; i < 5; i++) system.Update(1.0f);
            Assert.Equal(0, Store.EnemyMarkStacks[eid]);
            // Re-add to cross again
            system.AddMark(eid, 5);
            Assert.Equal(2, firedCount);
        }

        // ── Decay behavior ───────────────────────────────────────────────

        [Fact]
        public void Update_DecayTimer_DecrementsByDeltaTime()
        {
            var system = MakeSystem();
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "E"; });
            Store.EnemyMarkMaxThreshold[eid] = 5;
            system.AddMark(eid, 3);
            float t0 = Store.EnemyMarkDecayTimer[eid];
            system.Update(0.1f);
            Assert.Equal(t0 - 0.1f, Store.EnemyMarkDecayTimer[eid], 5);
            // Stacks unchanged
            Assert.Equal(3, Store.EnemyMarkStacks[eid]);
        }

        [Fact]
        public void Update_DecayTimerExpires_ConsumesOneStack()
        {
            var system = MakeSystem();
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "E"; });
            Store.EnemyMarkMaxThreshold[eid] = 5;
            system.AddMark(eid, 3);
            // Run exactly 1 second (decay interval) — timer should hit 0 and consume 1 stack
            system.Update(1.0f);
            Assert.Equal(2, Store.EnemyMarkStacks[eid]);
            // Timer re-armed for next stack's decay
            Assert.Equal(1.0f, Store.EnemyMarkDecayTimer[eid], 5);
        }

        [Fact]
        public void Update_DecayTimerExpires_StopsAtZeroStacks()
        {
            var system = MakeSystem();
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "E"; });
            Store.EnemyMarkMaxThreshold[eid] = 5;
            system.AddMark(eid, 1);
            // Run 2 seconds (1 to consume, 1 more to verify timer stops)
            system.Update(1.0f);
            Assert.Equal(0, Store.EnemyMarkStacks[eid]);
            system.Update(1.0f);
            Assert.Equal(0, Store.EnemyMarkStacks[eid]);
            // Timer should be 0 (no ticking when no stacks)
            Assert.Equal(0f, Store.EnemyMarkDecayTimer[eid]);
        }

        [Fact]
        public void Update_FastPath_SkipsUnmarkedEnemies()
        {
            var system = MakeSystem();
            // Spawn 100 unmarked enemies — no work should be done.
            for (int i = 0; i < 100; i++)
                Enemy(e => { e.MoveSpeed = 1f; e.Name = "E"; });
            // Just verify no exception and stacks remain 0
            for (int f = 0; f < 5; f++) system.Update(0.1f);
            foreach (int eid in Store.ActiveEnemyIds)
                Assert.Equal(0, Store.EnemyMarkStacks[eid]);
        }

        [Fact]
        public void Update_NonPositiveDeltaTime_NoOp()
        {
            var system = MakeSystem();
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "E"; });
            Store.EnemyMarkMaxThreshold[eid] = 5;
            system.AddMark(eid, 3);
            float t = Store.EnemyMarkDecayTimer[eid];
            system.Update(0f);
            Assert.Equal(t, Store.EnemyMarkDecayTimer[eid]);
            system.Update(-0.1f);
            Assert.Equal(t, Store.EnemyMarkDecayTimer[eid]);
        }

        [Fact]
        public void Update_CustomDecayInterval_TakesEffect()
        {
            var system = MakeSystem();
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "E"; });
            Store.EnemyMarkMaxThreshold[eid] = 5;
            system.LoadConfig(new MarkConfig { DecayInterval = 0.5f, MaxStackCap = 100 });
            system.AddMark(eid, 3);
            // After add: timer = 0.5
            Assert.Equal(0.5f, Store.EnemyMarkDecayTimer[eid], 5);
            // Run 0.5s → stack consumed
            system.Update(0.5f);
            Assert.Equal(2, Store.EnemyMarkStacks[eid]);
        }

        // ── ClearMark / destroy cleanup ──────────────────────────────────

        [Fact]
        public void ClearMark_ResetsStacksAndTimer()
        {
            var system = MakeSystem();
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "E"; });
            Store.EnemyMarkMaxThreshold[eid] = 5;
            system.AddMark(eid, 3);
            system.ClearMark(eid);
            Assert.Equal(0, Store.EnemyMarkStacks[eid]);
            Assert.Equal(0f, Store.EnemyMarkDecayTimer[eid]);
        }

        [Fact]
        public void ClearMark_DoesNotResetMaxThreshold()
        {
            var system = MakeSystem();
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "E"; });
            Store.EnemyMarkMaxThreshold[eid] = 5;
            system.AddMark(eid, 3);
            system.ClearMark(eid);
            // MaxThreshold is a static opt-in field, should persist
            Assert.Equal(5, Store.EnemyMarkMaxThreshold[eid]);
        }

        [Fact]
        public void ClearMark_AllowsRefiringOfThresholdEvent()
        {
            var system = MakeSystem();
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "E"; });
            Store.EnemyMarkMaxThreshold[eid] = 5;
            int firedCount = 0;
            system.OnMarkThreshold += (eidArg, pid, stacks) => firedCount++;
            system.AddMark(eid, 5); // fires
            Assert.Equal(1, firedCount);
            system.ClearMark(eid);
            system.AddMark(eid, 5); // should re-fire (latch reset)
            Assert.Equal(2, firedCount);
        }

        [Fact]
        public void DestroyEntity_ResetsAllMarkFields()
        {
            var system = MakeSystem();
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "E"; });
            Store.EnemyMarkMaxThreshold[eid] = 5;
            system.AddMark(eid, 3);
            Store.DestroyEntity(eid);
            // All mark fields reset to prevent ID-reuse leakage
            Assert.Equal(0, Store.EnemyMarkStacks[eid]);
            Assert.Equal(0f, Store.EnemyMarkDecayTimer[eid]);
            Assert.Equal(0, Store.EnemyMarkMaxThreshold[eid]);
        }

        [Fact]
        public void OnEnemyDestroyed_ResetsThresholdLatch()
        {
            var system = MakeSystem();
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "E"; });
            Store.EnemyMarkMaxThreshold[eid] = 5;
            system.AddMark(eid, 5); // fires latch
            Assert.True(system.IsThresholdFired(eid));
            system.OnEnemyDestroyed(eid);
            Assert.False(system.IsThresholdFired(eid));
        }

        [Fact]
        public void OnEnemyDestroyed_OutOfRange_DoesNotTouchValidLatch()
        {
            var system = MakeSystem();
            // 先建立一个合法敌人的 mark 状态与阈值锁存。
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "E"; });
            Store.EnemyMarkMaxThreshold[eid] = 5;
            system.AddMark(eid, 5);
            Assert.True(system.IsThresholdFired(eid));

            // 越界 id 必须 no-op，不能破坏合法槽位的锁存状态。
            system.OnEnemyDestroyed(-1);
            system.OnEnemyDestroyed(ComponentStore.MAX_ENTITIES);

            Assert.True(system.IsThresholdFired(eid));
            Assert.Equal(5, Store.EnemyMarkStacks[eid]);
        }

        // ── Update skips inactive enemies ────────────────────────────────

        [Fact]
        public void Update_SkipsInactiveEnemies()
        {
            var system = MakeSystem();
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "E"; });
            Store.EnemyMarkMaxThreshold[eid] = 5;
            system.AddMark(eid, 3);
            Store.EnemyActive[eid] = false; // simulate kill
            // No exception, stack count not relevant since inactive
            system.Update(5.0f);
            // Stack field itself is not modified by Update for inactive enemies
            Assert.Equal(3, Store.EnemyMarkStacks[eid]);
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private MarkSystem MakeSystem()
        {
            return new MarkSystem(Store, PlayerId);
        }
    }
}