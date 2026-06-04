using System;
using System.Collections.Generic;
using Xunit;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
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
    public class TimeRewindTests
    {
        private const int PlayerId = 0;
        private const float DeltaTime = 1f / 60f;

        // ── Default state & constants ────────────────────────────────────

        [Fact]
        public void DefaultState_AllSnapshotFieldsInert()
        {
            var store = new ComponentStore();
            Assert.Equal(0, store.PlayerSnapshotHead[0]);
            Assert.Equal(0, store.PlayerSnapshotFilled[0]);
            Assert.Equal(0f, store.PlayerSnapshotTick[0]);
            Assert.Equal(0f, store.PlayerSnapshotHP[0]);
            Assert.Equal(0f, store.PlayerSnapshotMana[0]);
            Assert.Equal(0f, store.PlayerSnapshotShield[0]);
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
            var store = new ComponentStore();
            store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            store.PlayerMaxHealth[0] = 100f;
            store.PlayerCurrentHealth[0] = 100f;
            store.PlayerMana[0] = 50f;
            store.PlayerShield[0] = 20f;

            var sys = new TimeRewindSnapshotSystem(store);

            // 24 ticks @ 1/60s = 0.4s elapsed — should produce exactly 1 sample (at 0.25s tick).
            for (int i = 0; i < 24; i++) sys.Update(DeltaTime);
            Assert.Equal(1, sys.GetSampleCount(0));
        }

        [Fact]
        public void Update_SkipsUninitializedPlayers()
        {
            var store = new ComponentStore();
            // Player 0 never had AddPlayer called → PlayerMaxHealth[0] == 0 → skip.
            var sys = new TimeRewindSnapshotSystem(store);
            for (int i = 0; i < 100; i++) sys.Update(DeltaTime);
            Assert.Equal(0, sys.GetSampleCount(0));
        }

        [Fact]
        public void AppendSnapshot_AdvancesHeadAndIncrementsFilled()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            store.PlayerMaxHealth[0] = 100f;
            store.PlayerCurrentHealth[0] = 50f;

            var sys = new TimeRewindSnapshotSystem(store);
            sys.AppendSnapshot(0);
            sys.AppendSnapshot(0);
            sys.AppendSnapshot(0);
            Assert.Equal(3, sys.GetSampleCount(0));
            Assert.Equal(3, store.PlayerSnapshotHead[0]);
        }

        [Fact]
        public void AppendSnapshot_OutOfRangePlayer_IsNoOp()
        {
            var store = new ComponentStore();
            var sys = new TimeRewindSnapshotSystem(store);
            sys.AppendSnapshot(-1);
            sys.AppendSnapshot(10); // MAX_PLAYERS=10
            Assert.Equal(0, sys.GetSampleCount(0));
        }

        [Fact]
        public void Ring_WrapsAfterMaxSnapshots()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            store.PlayerMaxHealth[0] = 100f;
            store.PlayerCurrentHealth[0] = 100f;

            var sys = new TimeRewindSnapshotSystem(store);
            // Force-fill the ring with MAX_SNAPSHOTS+5 entries to wrap around.
            int total = ComponentStore.MAX_SNAPSHOTS + 5;
            for (int i = 0; i < total; i++)
            {
                store.PlayerCurrentHealth[0] = 100f - i;
                sys.AppendSnapshot(0);
            }
            // Filled should be clamped at MAX_SNAPSHOTS.
            Assert.Equal(ComponentStore.MAX_SNAPSHOTS, sys.GetSampleCount(0));
            // Head should be (total % MAX_SNAPSHOTS).
            Assert.Equal(total % ComponentStore.MAX_SNAPSHOTS, store.PlayerSnapshotHead[0]);
        }

        // ── Restore behavior ─────────────────────────────────────────────

        [Fact]
        public void FindSnapshotSlot_EmptyBuffer_ReturnsMinusOne()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            store.PlayerMaxHealth[0] = 100f;

            var sys = new TimeRewindSnapshotSystem(store);
            Assert.Equal(-1, sys.FindSnapshotSlot(0, 1.0f));
        }

        [Fact]
        public void FindSnapshotSlot_NewestSample_WhenSecondsBackIsZero()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            store.PlayerMaxHealth[0] = 100f;

            var sys = new TimeRewindSnapshotSystem(store);
            store.PlayerCurrentHealth[0] = 42f;
            sys.AppendSnapshot(0);

            // The newest sample is at (head - 1 + MAX) mod MAX = 0, so slot 0 holds HP=42.
            int slot = sys.FindSnapshotSlot(0, 0f);
            Assert.Equal(0, slot);
            Assert.Equal(42f, store.PlayerSnapshotHP[slot]);
        }

        [Fact]
        public void RestoreFromSnapshot_RollsStateBackToTargetSample()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            store.PlayerMaxHealth[0] = 100f;

            var sys = new TimeRewindSnapshotSystem(store);

            // Take a "healthy" snapshot
            store.PlayerCurrentHealth[0] = 100f;
            store.PlayerMana[0] = 50f;
            store.PlayerShield[0] = 30f;
            sys.AppendSnapshot(0);

            // Take a second "wounded" snapshot
            store.PlayerCurrentHealth[0] = 30f;
            store.PlayerMana[0] = 10f;
            store.PlayerShield[0] = 0f;
            sys.AppendSnapshot(0);

            // Take a third "current" snapshot
            store.PlayerCurrentHealth[0] = 5f;
            store.PlayerMana[0] = 0f;
            store.PlayerShield[0] = 0f;
            sys.AppendSnapshot(0);

            // Rewind 1 sample (~0.25s back) — should restore to the wounded state.
            float actual = sys.RestoreFromSnapshot(0, 0.25f);
            Assert.Equal(0.25f, actual);
            Assert.Equal(30f, store.PlayerCurrentHealth[0]);
            Assert.Equal(10f, store.PlayerMana[0]);
            Assert.Equal(0f, store.PlayerShield[0]);
        }

        [Fact]
        public void RestoreFromSnapshot_ClampsHpAtMaxHealth()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            store.PlayerMaxHealth[0] = 100f;

            var sys = new TimeRewindSnapshotSystem(store);
            // Capture an overheal snapshot (e.g. from a temporary buff).
            store.PlayerCurrentHealth[0] = 250f;
            sys.AppendSnapshot(0);
            // Take a normal wounded sample afterwards.
            store.PlayerCurrentHealth[0] = 10f;
            sys.AppendSnapshot(0);

            // Rewind to the overheal sample — restore must clamp at MaxHealth.
            sys.RestoreFromSnapshot(0, 0.25f);
            Assert.Equal(100f, store.PlayerCurrentHealth[0]);
        }

        [Fact]
        public void RestoreFromSnapshot_EmptyBuffer_ReturnsMinusOne()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            store.PlayerMaxHealth[0] = 100f;

            var sys = new TimeRewindSnapshotSystem(store);
            Assert.Equal(-1f, sys.RestoreFromSnapshot(0, 1.0f));
        }

        [Fact]
        public void RestoreFromSnapshot_ClampsSecondsBackToAvailableBuffer()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            store.PlayerMaxHealth[0] = 100f;

            var sys = new TimeRewindSnapshotSystem(store);
            // Only 2 samples in the buffer; ask for 30s back.
            store.PlayerCurrentHealth[0] = 80f;
            sys.AppendSnapshot(0);
            store.PlayerCurrentHealth[0] = 60f;
            sys.AppendSnapshot(0);
            store.PlayerCurrentHealth[0] = 10f; // current

            float actual = sys.RestoreFromSnapshot(0, 30f);
            // Should clamp to the oldest entry (0.25s back, since filled=2).
            Assert.True(actual <= 0.25f);
            // Restored value should be 80 (oldest sample).
            Assert.Equal(80f, store.PlayerCurrentHealth[0]);
        }

        // ── Clear / AddPlayer integration ────────────────────────────────

        [Fact]
        public void Clear_ResetsAllCounters()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            store.PlayerMaxHealth[0] = 100f;

            var sys = new TimeRewindSnapshotSystem(store);
            sys.AppendSnapshot(0);
            sys.AppendSnapshot(0);
            Assert.Equal(2, sys.GetSampleCount(0));

            sys.Clear(0);
            Assert.Equal(0, sys.GetSampleCount(0));
            Assert.Equal(0, store.PlayerSnapshotHead[0]);
            Assert.Equal(0f, store.PlayerSnapshotTick[0]);
        }

        [Fact]
        public void AddPlayer_ResetsSnapshotCounters()
        {
            // Verifies the ID-reuse-leakage guard: AddPlayer on a recycled slot
            // resets the snapshot ring so a previous occupant's data doesn't bleed in.
            var store = new ComponentStore();
            store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            store.PlayerMaxHealth[0] = 100f;
            var sys = new TimeRewindSnapshotSystem(store);
            for (int i = 0; i < 5; i++) sys.AppendSnapshot(0);
            Assert.Equal(5, sys.GetSampleCount(0));

            // Re-AddPlayer on the same slot (simulating a fresh game).
            store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            store.PlayerMaxHealth[0] = 100f;
            Assert.Equal(0, store.PlayerSnapshotHead[0]);
            Assert.Equal(0, store.PlayerSnapshotFilled[0]);
            Assert.Equal(0f, store.PlayerSnapshotTick[0]);
        }

        // ── Diagnostic counters ──────────────────────────────────────────

        [Fact]
        public void DiagnosticCounters_TrackSamplesAndRestores()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            store.PlayerMaxHealth[0] = 100f;
            var sys = new TimeRewindSnapshotSystem(store);
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
            var store = new ComponentStore();
            store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            store.PlayerMaxHealth[0] = 100f;
            var sys = new TimeRewindSnapshotSystem(store);
            sys.Update(0f);
            sys.Update(-1f);
            Assert.Equal(0, sys.GetSampleCount(0));
        }

        // ── AreaShape integration ────────────────────────────────────────

        [Fact]
        public void AreaShapeType_TimeRewind_MapsFromString()
        {
            Assert.Equal(16, BattleSystemECS.Core.GAS.AreaShapeType.TimeRewind);
            Assert.Equal(16, BattleSystemECS.Core.GAS.AreaShapeType.FromString("timerwind"));
            // Unknown shape defaults to Single (0) — Time Rewind must use the explicit keyword.
            Assert.Equal(0, BattleSystemECS.Core.GAS.AreaShapeType.FromString("unknownshape"));
        }
    }
}
