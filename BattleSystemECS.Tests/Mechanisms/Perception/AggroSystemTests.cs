using System.Collections.Generic;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Mechanisms.Perception
{
    /// <summary>
    /// Tests for Round 142 Direction 5: Aggro / Focus Fire System.
    /// Verifies that:
    ///   - Default state: all focus fields are -1 / 0f (zero-overhead path)
    ///   - MarkFocusTower() assigns focus; returns false on invalid enemy / tower / duration
    ///   - MarkFocusTower() refreshes duration (max-old vs new) so re-mark doesn't shorten
    ///   - MarkFocusTowerBulk() marks N enemies in one call
    ///   - ClearFocus() resets the assignment (and is a no-op when already clear)
    ///   - Update() decrements duration each frame, clears at zero
    ///   - Update() fast-path: when no enemy is focused, returns O(1) — sentinel-gated
    ///   - OnEnemyDestroyed() clears the per-enemy focus state
    ///   - ComponentStore.DestroyEntity() (tower) clears all focus assignments pointing
    ///     at that tower (eager-clear prevents stale IDs)
    ///   - HasFocus / GetFocusTowerId read helpers behave correctly across the lifecycle
    ///   - Default duration of 0 is treated as "no focus" by read helpers
    /// </summary>
    public class AggroSystemTests
    {
        private const float DeltaTime = 1f / 60f;

        // ── Default state ───────────────────────────────────────────────

        [Fact]
        public void DefaultState_AllFocusFieldsInert()
        {
            var store = new ComponentStore();
            // C# array default = 0; the -1 sentinel is applied per-entity via
            // ResetEnemy (factory path) and DestroyEntity (recycle path). A
            // fresh ComponentStore with no entities ever spawned has all-zero
            // arrays — same convention as EnemyTauntedByTowerId.
            Assert.Equal(0, store.EnemyFocusTowerId[0]);
            Assert.Equal(0f, store.EnemyFocusDurationLeft[0]);

            // After an enemy is created + reset, the per-entity defaults kick in
            // (sentinel = -1 / 0f). This is the real "inert" state at runtime.
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 5f, 10, 1, "E");
            Assert.Equal(-1, store.EnemyFocusTowerId[eid]);
            Assert.Equal(0f, store.EnemyFocusDurationLeft[eid]);
        }

        [Fact]
        public void MarkFocusTower_InvalidEnemy_NoOp()
        {
            var (system, store) = MakeSystem();
            int tid = MakeTower(store, 0);
            Assert.False(system.MarkFocusTower(-1, tid, 5f));
            Assert.False(system.MarkFocusTower(ComponentStore.MAX_ENTITIES, tid, 5f));
            Assert.False(system.MarkFocusTower(ComponentStore.MAX_ENTITIES + 100, tid, 5f));
        }

        [Fact]
        public void MarkFocusTower_InactiveEnemy_NoOp()
        {
            var (system, store) = MakeSystem();
            int tid = MakeTower(store, 0);
            // Slot 0 is not occupied by an enemy — IsValidEnemy returns false.
            Assert.False(system.MarkFocusTower(0, tid, 5f));
        }

        [Fact]
        public void MarkFocusTower_InvalidTower_NoOp()
        {
            var (system, store) = MakeSystem();
            int eid = MakeEnemy(store);
            Assert.False(system.MarkFocusTower(eid, -1, 5f));
            Assert.False(system.MarkFocusTower(eid, ComponentStore.MAX_ENTITIES, 5f));
        }

        [Fact]
        public void MarkFocusTower_ZeroDuration_NoOp()
        {
            var (system, store) = MakeSystem();
            int eid = MakeEnemy(store);
            int tid = MakeTower(store, 1);
            Assert.False(system.MarkFocusTower(eid, tid, 0f));
            Assert.False(system.MarkFocusTower(eid, tid, -1f));
        }

        // ── Mark + read ──────────────────────────────────────────────────

        [Fact]
        public void MarkFocusTower_AssignsTowerAndDuration()
        {
            var (system, store) = MakeSystem();
            int eid = MakeEnemy(store);
            int tid = MakeTower(store, 1);

            Assert.True(system.MarkFocusTower(eid, tid, 5f));
            Assert.Equal(tid, store.EnemyFocusTowerId[eid]);
            Assert.Equal(5f, store.EnemyFocusDurationLeft[eid]);
            Assert.True(system.HasFocus(eid));
            Assert.Equal(tid, system.GetFocusTowerId(eid));
        }

        [Fact]
        public void MarkFocusTower_RefreshDuration_TakesMax()
        {
            var (system, store) = MakeSystem();
            int eid = MakeEnemy(store);
            int tid = MakeTower(store, 1);

            system.MarkFocusTower(eid, tid, 10f);
            // Re-mark with shorter duration: must NOT shorten the existing focus
            // (refresh should be additive, not destructive).
            system.MarkFocusTower(eid, tid, 3f);
            Assert.Equal(10f, store.EnemyFocusDurationLeft[eid]);

            // Re-mark with longer duration: must extend.
            system.MarkFocusTower(eid, tid, 20f);
            Assert.Equal(20f, store.EnemyFocusDurationLeft[eid]);
        }

        [Fact]
        public void MarkFocusTower_SwitchesTargetTower()
        {
            var (system, store) = MakeSystem();
            int eid = MakeEnemy(store);
            int t1 = MakeTower(store, 1);
            int t2 = MakeTower(store, 2);

            system.MarkFocusTower(eid, t1, 5f);
            Assert.Equal(t1, store.EnemyFocusTowerId[eid]);

            system.MarkFocusTower(eid, t2, 3f);
            // Target switched; duration takes the max (3 < 5 → keep 5).
            Assert.Equal(t2, store.EnemyFocusTowerId[eid]);
            Assert.Equal(5f, store.EnemyFocusDurationLeft[eid]);
        }

        // ── Bulk mark ────────────────────────────────────────────────────

        [Fact]
        public void MarkFocusTowerBulk_AssignsAllValidEnemies()
        {
            var (system, store) = MakeSystem();
            int e1 = MakeEnemy(store);
            int e2 = MakeEnemy(store);
            int e3 = MakeEnemy(store);
            int tid = MakeTower(store, 3);

            var ids = new List<int> { e1, e2, e3 };
            int marked = system.MarkFocusTowerBulk(ids, tid, 4f);
            Assert.Equal(3, marked);

            Assert.Equal(tid, store.EnemyFocusTowerId[e1]);
            Assert.Equal(tid, store.EnemyFocusTowerId[e2]);
            Assert.Equal(tid, store.EnemyFocusTowerId[e3]);
            Assert.Equal(4f, store.EnemyFocusDurationLeft[e1]);
        }

        [Fact]
        public void MarkFocusTowerBulk_SkipsInvalidEnemies()
        {
            var (system, store) = MakeSystem();
            int e1 = MakeEnemy(store);
            int tid = MakeTower(store, 1);

            var ids = new List<int> { e1, -1, 999, ComponentStore.MAX_ENTITIES };
            int marked = system.MarkFocusTowerBulk(ids, tid, 4f);
            Assert.Equal(1, marked);
            Assert.Equal(tid, store.EnemyFocusTowerId[e1]);
        }

        [Fact]
        public void MarkFocusTowerBulk_NullList_ReturnsZero()
        {
            var (system, store) = MakeSystem();
            int tid = MakeTower(store, 0);
            Assert.Equal(0, system.MarkFocusTowerBulk(null, tid, 4f));
        }

        // ── Clear ────────────────────────────────────────────────────────

        [Fact]
        public void ClearFocus_ResetsAssignment()
        {
            var (system, store) = MakeSystem();
            int eid = MakeEnemy(store);
            int tid = MakeTower(store, 1);

            system.MarkFocusTower(eid, tid, 5f);
            Assert.True(system.HasFocus(eid));

            system.ClearFocus(eid);
            Assert.False(system.HasFocus(eid));
            Assert.Equal(-1, store.EnemyFocusTowerId[eid]);
            Assert.Equal(0f, store.EnemyFocusDurationLeft[eid]);
        }

        [Fact]
        public void ClearFocus_NoOpWhenAlreadyClear()
        {
            var (system, store) = MakeSystem();
            int eid = MakeEnemy(store);
            // Default state — ClearFocus should be a no-op (no exception).
            system.ClearFocus(eid);
            Assert.Equal(-1, store.EnemyFocusTowerId[eid]);
            Assert.Equal(0f, store.EnemyFocusDurationLeft[eid]);
        }

        // ── Update tick (decay) ──────────────────────────────────────────

        [Fact]
        public void Update_DecrementsDuration()
        {
            var (system, store) = MakeSystem();
            int eid = MakeEnemy(store);
            int tid = MakeTower(store, 1);
            system.MarkFocusTower(eid, tid, 1f);

            system.Update(0.25f);
            Assert.Equal(0.75f, store.EnemyFocusDurationLeft[eid]);
            Assert.True(system.HasFocus(eid));

            system.Update(0.5f);
            Assert.Equal(0.25f, store.EnemyFocusDurationLeft[eid]);

            system.Update(0.3f);
            // Tick past zero → cleared.
            Assert.Equal(0f, store.EnemyFocusDurationLeft[eid]);
            Assert.Equal(-1, store.EnemyFocusTowerId[eid]);
            Assert.False(system.HasFocus(eid));
        }

        [Fact]
        public void Update_NegativeDt_NoOp()
        {
            var (system, store) = MakeSystem();
            int eid = MakeEnemy(store);
            int tid = MakeTower(store, 1);
            system.MarkFocusTower(eid, tid, 5f);

            system.Update(-1f);
            Assert.Equal(5f, store.EnemyFocusDurationLeft[eid]);
        }

        [Fact]
        public void Update_NoActiveFocus_FastPathNoOp()
        {
            var (system, store) = MakeSystem();
            // Add some enemies that have NO focus — Update should be a no-op
            // (sentinel-gated). Verify no field changes after a tick.
            int e1 = MakeEnemy(store);
            int e2 = MakeEnemy(store);

            system.Update(1f);
            Assert.Equal(-1, store.EnemyFocusTowerId[e1]);
            Assert.Equal(0f, store.EnemyFocusDurationLeft[e1]);
            Assert.Equal(-1, store.EnemyFocusTowerId[e2]);
            Assert.Equal(0f, store.EnemyFocusDurationLeft[e2]);
        }

        [Fact]
        public void Update_AllFocusExpired_StillWorksAfterExpiry()
        {
            var (system, store) = MakeSystem();
            int e1 = MakeEnemy(store);
            int e2 = MakeEnemy(store);
            int tid = MakeTower(store, 2);
            system.MarkFocusTower(e1, tid, 0.5f);
            system.MarkFocusTower(e2, tid, 0.5f);

            // Tick past both durations.
            system.Update(0.6f);
            Assert.False(system.HasFocus(e1));
            Assert.False(system.HasFocus(e2));

            // After all focus expired, sentinel should be dropped. Subsequent Update
            // is O(1) no-op (verify nothing breaks when no focus is active).
            system.Update(0.6f);
            Assert.False(system.HasFocus(e1));
        }

        // ── Tower destruction (eager clear) ─────────────────────────────

        [Fact]
        public void DestroyTower_ClearsFocusOnPointingEnemies()
        {
            var (system, store) = MakeSystem();
            int e1 = MakeEnemy(store);
            int e2 = MakeEnemy(store);
            int t1 = MakeTower(store, 2);
            int t2 = MakeTower(store, 3);

            system.MarkFocusTower(e1, t1, 5f);
            system.MarkFocusTower(e2, t2, 5f);

            // Destroy t1 — only e1's focus should be cleared; e2's focus is intact.
            store.DestroyEntity(t1);
            Assert.False(system.HasFocus(e1));
            Assert.Equal(-1, store.EnemyFocusTowerId[e1]);
            Assert.Equal(0f, store.EnemyFocusDurationLeft[e1]);

            Assert.True(system.HasFocus(e2));
            Assert.Equal(t2, store.EnemyFocusTowerId[e2]);
        }

        // ── Enemy destroy lifecycle hook ────────────────────────────────

        [Fact]
        public void OnEnemyDestroyed_ClearsFocus()
        {
            var (system, store) = MakeSystem();
            int eid = MakeEnemy(store);
            int tid = MakeTower(store, 1);
            system.MarkFocusTower(eid, tid, 5f);

            system.OnEnemyDestroyed(eid);
            Assert.Equal(-1, store.EnemyFocusTowerId[eid]);
            Assert.Equal(0f, store.EnemyFocusDurationLeft[eid]);
        }

        [Fact]
        public void OnEnemyDestroyed_InvalidId_NoOp()
        {
            var (system, store) = MakeSystem();
            // No exception expected.
            system.OnEnemyDestroyed(-1);
            system.OnEnemyDestroyed(ComponentStore.MAX_ENTITIES);
        }

        // ── Read helpers edge cases ─────────────────────────────────────

        [Fact]
        public void GetFocusTowerId_DefaultsTo_MinusOne()
        {
            var (system, store) = MakeSystem();
            int eid = MakeEnemy(store);
            Assert.Equal(-1, system.GetFocusTowerId(eid));
        }

        [Fact]
        public void HasFocus_InvalidEnemy_False()
        {
            var (system, store) = MakeSystem();
            Assert.False(system.HasFocus(-1));
            Assert.False(system.HasFocus(ComponentStore.MAX_ENTITIES));
        }

        [Fact]
        public void ReadHelpers_AfterUpdateExpiry_ReturnInert()
        {
            var (system, store) = MakeSystem();
            int eid = MakeEnemy(store);
            int tid = MakeTower(store, 1);
            system.MarkFocusTower(eid, tid, 0.1f);

            system.Update(0.2f); // expire it
            Assert.False(system.HasFocus(eid));
            Assert.Equal(-1, system.GetFocusTowerId(eid));
        }

        // ── Test helpers ────────────────────────────────────────────────

        private static (AggroSystem, ComponentStore) MakeSystem()
        {
            var store = new ComponentStore();
            return (new AggroSystem(store), store);
        }

        // AddEnemy auto-allocates and returns the entity id. We don't pre-allocate
        // because the test helpers below use sequential calls and rely on the
        // allocator handing back distinct ids in the order they're requested.
        private static int MakeEnemy(ComponentStore store)
        {
            return store.AddEnemy(0f, 0f, 1f, 100f, 100f, 5f, 10, 1, "E");
        }

        // AddTower is void; the caller picks the entityId. We track the slot
        // via the call-site's explicit argument so tests can read intent
        // (e.g. "the tower at slot 1 is the second tower").
        private static int MakeTower(ComponentStore store, int entityId)
        {
            store.AddTower(entityId, TowerType.Basic, 5f, 5, 1f, 1, 50f);
            return entityId;
        }
    }
}
