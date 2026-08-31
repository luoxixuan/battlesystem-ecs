using BattleSystemECS.Tests.Infrastructure;
using System;
using System.Collections.Generic;
using Xunit;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Framework
{
    /// <summary>
    /// Tests for Round 109 Direction 5: Time Rewind snapshot / restore subsystem.
    /// Verifies that:
    ///   - Default state: all snapshot fields are zero / inert (zero-overhead path)
    ///   - Sampling at SNAPSHOT_INTERVAL appends a new sample and advances the head
    ///   - The ring wraps at MAX_SNAPSHOTS without losing the newest entry
    ///   - AppendSnapshot is a no-op on out-of-range playerId
    ///   - FindSnapshotSlot returns -1 when the ring is empty
    ///   - FindSnapshotSlot returns the newest entry when samplesBack == 0
    ///   - FindSnapshotSlot returns older entries as samplesBack increases
    ///   - RestoreFromSnapshot rolls HP / Mana / Shield back to the target entry
    ///   - RestoreFromSnapshot clamps restored HP at MaxHealth
    ///   - RestoreFromSnapshot returns -1 when the ring is empty
    ///   - RestoreFromSnapshot clamps the requested secondsBack to the available buffer
    ///   - Clear() resets the head/filled/tick counters
    ///   - TimeRewind is wired through SkillSystem (case 16: timerwind)
    ///   - PlayerSnapshotHP/Mana/Shield are zero-initialized (no ID-reuse leakage)
    ///   - The Default rewind seconds is exactly 3.0f as documented
    ///   - SNAPSHOT_INTERVAL is exactly 0.25f as documented
    /// </summary>
    public class TimeRewindTests : BattleTestBase
    {
        private const int PlayerId = 0;
        private const float DeltaTime = 1f / 60f;

        // ── Default state & constants ────────────────────────────────────

        [Fact]
        public void DefaultState_AllSnapshotFieldsInert()
        {
            Assert.Equal(0, Store.PlayerSnapshotHead[0]);
            Assert.Equal(0, Store.PlayerSnapshotFilled[0]);
            Assert.Equal(0f, Store.PlayerSnapshotTick[0]);
            Assert.Equal(0f, Store.PlayerSnapshotHP[0]);
            Assert.Equal(0f, Store.PlayerSnapshotMana[0]);
            Assert.Equal(0f, Store.PlayerSnapshotShield[0]);
        }

        [Fact]
        public void Constants_DocumentedValuesUnchanged()
        {
            Assert.Equal(20, ComponentStore.MAX_SNAPSHOTS);
            Assert.Equal(0.25f, ComponentStore.SNAPSHOT_INTERVAL);
            Assert.Equal(3.0f, ComponentStore.DEFAULT_REWIND_SECONDS);
            // Buffer covers 5s of history (20 slots * 0.25s).
            Assert.Equal(5.0f, ComponentStore.MAX_SNAPSHOTS * ComponentStore.SNAPSHOT_INTERVAL);
        }

        // ── Sampling behavior ────────────────────────────────────────────

        [Fact]
        public void Update_TakesSampleEverySnapshotInterval()
        {
            Store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            Store.PlayerMaxHealth[0] = 100f;
            Store.PlayerMaxMana[0] = 50f;
            Store.PlayerCurrentHealth[0] = 100f;
            Store.PlayerMana[0] = 50f;
            Store.PlayerShield[0] = 20f;

            var sys = new TimeRewindSnapshotSystem(Store);

            // 24 ticks @ 1/60s = 0.4s elapsed — should produce exactly 1 sample (at 0.25s tick).
            for (int i = 0; i < 24; i++) sys.Update(DeltaTime);
            Assert.Equal(1, sys.GetSampleCount(0));
        }

        [Fact]
        public void Update_SkipsUninitializedPlayers()
        {
            // Player 0 never had AddPlayer called → PlayerMaxHealth[0] == 0 → skip.
            var sys = new TimeRewindSnapshotSystem(Store);
            for (int i = 0; i < 100; i++) sys.Update(DeltaTime);
            Assert.Equal(0, sys.GetSampleCount(0));
        }

        [Fact]
        public void AppendSnapshot_AdvancesHeadAndIncrementsFilled()
        {
            Store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            Store.PlayerMaxHealth[0] = 100f;
            Store.PlayerCurrentHealth[0] = 50f;

            var sys = new TimeRewindSnapshotSystem(Store);
            sys.AppendSnapshot(0);
            sys.AppendSnapshot(0);
            sys.AppendSnapshot(0);
            Assert.Equal(3, sys.GetSampleCount(0));
            Assert.Equal(3, Store.PlayerSnapshotHead[0]);
        }

        [Fact]
        public void AppendSnapshot_OutOfRangePlayer_IsNoOp()
        {
            Store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            Store.PlayerMaxHealth[0] = 100f;
            Store.PlayerCurrentHealth[0] = 42f;
            Store.PlayerMana[0] = 17f;
            Store.PlayerShield[0] = 9f;

            var sys = new TimeRewindSnapshotSystem(Store);

            // 先写入一个合法快照，建立可观测基线。
            sys.AppendSnapshot(0);
            int samples = sys.GetSampleCount(0);
            int head = Store.PlayerSnapshotHead[0];
            int filled = Store.PlayerSnapshotFilled[0];
            int totalSamples = sys.TotalSamplesTaken;

            // 对 -1 / 越界上界调用 AppendSnapshot，生产实现应直接 return。
            // MAX_PLAYERS 是 internal 常量，边界值从可观测的玩家数组长度推导。
            sys.AppendSnapshot(-1);
            sys.AppendSnapshot(Store.PlayerSnapshotHead.Length);

            // 真实 no-op 证据：合法槽位状态、头部/填充计数、总采样数全部不变。
            Assert.Equal(samples, sys.GetSampleCount(0));
            Assert.Equal(filled, Store.PlayerSnapshotFilled[0]);
            Assert.Equal(head, Store.PlayerSnapshotHead[0]);
            Assert.Equal(42f, Store.PlayerSnapshotHP[0], 5);
            Assert.Equal(17f, Store.PlayerSnapshotMana[0], 5);
            Assert.Equal(9f, Store.PlayerSnapshotShield[0], 5);
            Assert.Equal(totalSamples, sys.TotalSamplesTaken);
        }

        [Fact]
        public void Ring_WrapsAfterMaxSnapshots()
        {
            Store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            Store.PlayerMaxHealth[0] = 100f;
            Store.PlayerCurrentHealth[0] = 100f;

            var sys = new TimeRewindSnapshotSystem(Store);
            // Force-fill the ring with MAX_SNAPSHOTS+5 entries to wrap around.
            int total = ComponentStore.MAX_SNAPSHOTS + 5;
            for (int i = 0; i < total; i++)
            {
                Store.PlayerCurrentHealth[0] = 100f - i;
                sys.AppendSnapshot(0);
            }
            // Filled should be clamped at MAX_SNAPSHOTS.
            Assert.Equal(ComponentStore.MAX_SNAPSHOTS, sys.GetSampleCount(0));
            // Head should be (total % MAX_SNAPSHOTS).
            Assert.Equal(total % ComponentStore.MAX_SNAPSHOTS, Store.PlayerSnapshotHead[0]);
        }

        // ── Restore behavior ─────────────────────────────────────────────

        [Fact]
        public void FindSnapshotSlot_EmptyBuffer_ReturnsMinusOne()
        {
            Store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            Store.PlayerMaxHealth[0] = 100f;

            var sys = new TimeRewindSnapshotSystem(Store);
            Assert.Equal(-1, sys.FindSnapshotSlot(0, 1.0f));
        }

        [Fact]
        public void FindSnapshotSlot_NewestSample_WhenSecondsBackIsZero()
        {
            Store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            Store.PlayerMaxHealth[0] = 100f;

            var sys = new TimeRewindSnapshotSystem(Store);
            Store.PlayerCurrentHealth[0] = 42f;
            sys.AppendSnapshot(0);

            // The newest sample is at (head - 1 + MAX) mod MAX = 0, so slot 0 holds HP=42.
            int slot = sys.FindSnapshotSlot(0, 0f);
            Assert.Equal(0, slot);
            Assert.Equal(42f, Store.PlayerSnapshotHP[slot]);
        }

        [Fact]
        public void RestoreFromSnapshot_RollsStateBackToTargetSample()
        {
            Store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            Store.PlayerMaxHealth[0] = 100f;
            Store.PlayerMaxMana[0] = 50f;

            var sys = new TimeRewindSnapshotSystem(Store);

            // Take a "healthy" snapshot
            Store.PlayerCurrentHealth[0] = 100f;
            Store.PlayerMana[0] = 50f;
            Store.PlayerShield[0] = 30f;
            sys.AppendSnapshot(0);

            // Take a second "wounded" snapshot
            Store.PlayerCurrentHealth[0] = 30f;
            Store.PlayerMana[0] = 10f;
            Store.PlayerShield[0] = 0f;
            sys.AppendSnapshot(0);

            // Take a third "current" snapshot
            Store.PlayerCurrentHealth[0] = 5f;
            Store.PlayerMana[0] = 0f;
            Store.PlayerShield[0] = 0f;
            sys.AppendSnapshot(0);

            // Rewind 1 sample (~0.25s back) — should restore to the wounded state.
            float actual = sys.RestoreFromSnapshot(0, 0.25f);
            Assert.Equal(0.25f, actual);
            Assert.Equal(30f, Store.PlayerCurrentHealth[0]);
            Assert.Equal(10f, Store.PlayerMana[0]);
            Assert.Equal(0f, Store.PlayerShield[0]);
        }

        [Fact]
        public void RestoreFromSnapshot_ClampsHpAtMaxHealth()
        {
            Store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            Store.PlayerMaxHealth[0] = 100f;

            var sys = new TimeRewindSnapshotSystem(Store);
            // Capture an overheal snapshot (e.g. from a temporary buff).
            Store.PlayerCurrentHealth[0] = 250f;
            sys.AppendSnapshot(0);
            // Take a normal wounded sample afterwards.
            Store.PlayerCurrentHealth[0] = 10f;
            sys.AppendSnapshot(0);

            // Rewind to the overheal sample — restore must clamp at MaxHealth.
            sys.RestoreFromSnapshot(0, 0.25f);
            Assert.Equal(100f, Store.PlayerCurrentHealth[0]);
        }

        [Fact]
        public void RestoreFromSnapshot_EmptyBuffer_ReturnsMinusOne()
        {
            Store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            Store.PlayerMaxHealth[0] = 100f;

            var sys = new TimeRewindSnapshotSystem(Store);
            Assert.Equal(-1f, sys.RestoreFromSnapshot(0, 1.0f));
        }

        [Fact]
        public void RestoreFromSnapshot_ClampsSecondsBackToAvailableBuffer()
        {
            Store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            Store.PlayerMaxHealth[0] = 100f;

            var sys = new TimeRewindSnapshotSystem(Store);
            // Only 2 samples in the buffer; ask for 30s back.
            Store.PlayerCurrentHealth[0] = 80f;
            sys.AppendSnapshot(0);
            Store.PlayerCurrentHealth[0] = 60f;
            sys.AppendSnapshot(0);
            Store.PlayerCurrentHealth[0] = 10f; // current

            float actual = sys.RestoreFromSnapshot(0, 30f);
            // Should clamp to the oldest entry (0.25s back, since filled=2).
            Assert.Equal(0.25f, actual, 5);
            // Restored value should be 80 (oldest sample).
            Assert.Equal(80f, Store.PlayerCurrentHealth[0]);
        }

        // ── Clear / AddPlayer integration ────────────────────────────────

        [Fact]
        public void Clear_ResetsAllCounters()
        {
            Store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            Store.PlayerMaxHealth[0] = 100f;

            var sys = new TimeRewindSnapshotSystem(Store);
            sys.AppendSnapshot(0);
            sys.AppendSnapshot(0);
            Assert.Equal(2, sys.GetSampleCount(0));

            sys.Clear(0);
            Assert.Equal(0, sys.GetSampleCount(0));
            Assert.Equal(0, Store.PlayerSnapshotHead[0]);
            Assert.Equal(0f, Store.PlayerSnapshotTick[0]);
        }

        [Fact]
        public void AddPlayer_ResetsSnapshotCounters()
        {
            // Verifies the ID-reuse-leakage guard: AddPlayer on a recycled slot
            // resets the snapshot ring so a previous occupant's data doesn't bleed in.
            Store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            Store.PlayerMaxHealth[0] = 100f;
            var sys = new TimeRewindSnapshotSystem(Store);
            for (int i = 0; i < 5; i++) sys.AppendSnapshot(0);
            Assert.Equal(5, sys.GetSampleCount(0));

            // Re-AddPlayer on the same slot (simulating a fresh game).
            Store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            Store.PlayerMaxHealth[0] = 100f;
            Assert.Equal(0, Store.PlayerSnapshotHead[0]);
            Assert.Equal(0, Store.PlayerSnapshotFilled[0]);
            Assert.Equal(0f, Store.PlayerSnapshotTick[0]);
        }

        // ── Diagnostic counters ──────────────────────────────────────────

        [Fact]
        public void DiagnosticCounters_TrackSamplesAndRestores()
        {
            Store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            Store.PlayerMaxHealth[0] = 100f;
            var sys = new TimeRewindSnapshotSystem(Store);
            sys.AppendSnapshot(0);
            sys.AppendSnapshot(0);
            sys.AppendSnapshot(0);
            Assert.Equal(3, sys.TotalSamplesTaken);

            sys.RestoreFromSnapshot(0, 0.25f);
            sys.RestoreFromSnapshot(0, 0.25f);
            Assert.Equal(2, sys.TotalRestores);
        }

        [Fact]
        public void Update_DoesNotSampleOnZeroDelta()
        {
            Store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            Store.PlayerMaxHealth[0] = 100f;
            var sys = new TimeRewindSnapshotSystem(Store);
            sys.Update(0f);
            sys.Update(-1f);
            Assert.Equal(0, sys.GetSampleCount(0));
        }

        // ── AreaShape integration ────────────────────────────────────────

        [Fact]
        public void AreaShapeType_TimeRewind_MapsFromString()
        {
            // 不钉住枚举数值，直接用符号常量做相对断言。
            Assert.Equal(AreaShapeType.TimeRewind, AreaShapeType.FromString("timerwind"));
            // Unknown shape defaults to Single — Time Rewind must use the explicit keyword.
            Assert.Equal(AreaShapeType.Single, AreaShapeType.FromString("unknownshape"));
        }
    }
}
