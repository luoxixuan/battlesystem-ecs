using BattleSystemECS.Tests.Infrastructure;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Features.Towers
{
    /// <summary>
    /// Tests for Round 203 Direction 4: Tower Form / Stance Switch.
    /// Verifies that:
    ///   1. Default state: all form fields are 0/-1/0f (zero-overhead, opt-out sentinel)
    ///   2. SetTowerForms(configures a tower with N forms and a cooldown snapshot)
    ///   3. RequestFormSwitch on a tower with no forms configured returns false (opt-out sentinel respected)
    ///   4. RequestFormSwitch to a valid form succeeds and sets the active form
    ///   5. RequestFormSwitch during cooldown returns false (gated)
    ///   6. RequestFormSwitch to the same form is a no-op (success, no cooldown tick)
    ///   7. RequestFormSwitch to an out-of-range form index returns false
    ///   8. RequestFormSwitch on an inactive tower returns false
    ///   9. Update: form count = 0 → fast path is zero-overhead (no cooldown advance)
    ///  10. Update: form count > 0 → cooldown drains by dt seconds toward 0 (floor at 0)
    ///  11. Update: with cooldown = 0, the tower can immediately re-switch after the drain
    ///  12. GetActiveForm returns the active index (or 0 when no forms configured)
    /// </summary>
    public class TowerFormSwitchSystemTests
    {
        private const float DeltaTime = 1f / 60f;

        // ── Test helpers ────────────────────────────────────────────────

        private static (TowerFormSwitchSystem system, ComponentStore store) MakeSystem()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            var system = new TowerFormSwitchSystem(store);
            return (system, store);
        }

        /// <summary>Spawn a basic tower with the given form configuration.</summary>
        private static int MakeFormTower(
            ComponentStore store,
            int formCount = 3,
            float cooldown = 5f)
        {
            int tid = 1;
            store.AddTower(tid, Components.TowerType.Basic, 10f, 5, 1f, 1, 50f);
            store.SetTowerForms(tid, formCount, cooldown, currentTurn: 0);
            return tid;
        }

        // ── 1. Default state ────────────────────────────────────────────
        [Fact]
        public void DefaultState_AllFormFields_InertSentinels()
        {
            var store = new ComponentStore();
            // Raw C# array defaults — AddTower/DestroyEntity reset paths set the
            // -1 sentinel for TowerFormLastSwitchTurn and 0 for everything else.
            Assert.Equal(0, store.TowerActiveForm[0]);
            Assert.Equal(0f, store.TowerFormSwitchCooldown[0]);
            Assert.Equal(0, store.TowerFormLastSwitchTurn[0]); // raw default, before reset
            Assert.Equal(0, store.TowerFormCount[0]);
            Assert.Equal(0f, store.TowerFormSwitchCooldownRemaining[0]);

            // After AddTower → DestroyEntity, the recycle hook sets the -1 sentinel.
            int tid = 1;
            store.AddTower(tid, Components.TowerType.Basic, 10f, 5, 1f, 1, 50f);
            store.DestroyEntity(tid);
            Assert.Equal(0, store.TowerActiveForm[tid]);
            Assert.Equal(0f, store.TowerFormSwitchCooldown[tid]);
            Assert.Equal(-1, store.TowerFormLastSwitchTurn[tid]);
            Assert.Equal(0, store.TowerFormCount[tid]);
            Assert.Equal(0f, store.TowerFormSwitchCooldownRemaining[tid]);
        }

        // ── 2. SetTowerForms configures a tower ──────────────────────────
        [Fact]
        public void SetTowerForms_ConfiguresValidSnapshot()
        {
            var store = new ComponentStore();
            int tid = 1;
            store.AddTower(tid, Components.TowerType.Basic, 10f, 5, 1f, 1, 50f);
            store.SetTowerForms(tid, 3, 5f, currentTurn: 7);

            Assert.Equal(3, store.TowerFormCount[tid]);
            Assert.Equal(5f, store.TowerFormSwitchCooldown[tid]);
            Assert.Equal(0f, store.TowerFormSwitchCooldownRemaining[tid]);
            Assert.Equal(0, store.TowerActiveForm[tid]); // default to first form
            Assert.Equal(7, store.TowerFormLastSwitchTurn[tid]);
        }

        // ── 3. No forms configured → request always rejected ────────────
        [Fact]
        public void RequestFormSwitch_NoFormsConfigured_Rejected()
        {
            var (system, store) = MakeSystem();
            int tid = MakeFormTower(store, formCount: 0, cooldown: 5f);
            // Opt-out sentinel: FormCount=0 → fast path always returns false
            Assert.False(system.RequestFormSwitch(tid, 0));
            Assert.False(system.RequestFormSwitch(tid, 1));
            Assert.False(system.RequestFormSwitch(tid, -1));
        }

        // ── 4. Valid switch succeeds ────────────────────────────────────
        [Fact]
        public void RequestFormSwitch_ValidTarget_Succeeds()
        {
            var (system, store) = MakeSystem();
            int tid = MakeFormTower(store, formCount: 3, cooldown: 5f);

            Assert.True(system.RequestFormSwitch(tid, 1));
            Assert.Equal(1, store.GetTowerActiveForm(tid));
            // Cooldown was reset to the configured value (5f)
            Assert.True(store.TowerFormSwitchCooldownRemaining[tid] > 0f);
        }

        // ── 5. Cooldown gates rapid switching ───────────────────────────
        [Fact]
        public void RequestFormSwitch_DuringCooldown_Rejected()
        {
            var (system, store) = MakeSystem();
            int tid = MakeFormTower(store, formCount: 3, cooldown: 5f);

            // First switch is allowed (cooldown = 0 → ready)
            Assert.True(system.RequestFormSwitch(tid, 1));
            // Immediately request a different form → cooldown gates this
            Assert.False(system.RequestFormSwitch(tid, 2));
            // Active form is still 1
            Assert.Equal(1, store.GetTowerActiveForm(tid));
        }

        // ── 6. Same-form is a no-op (success, no cooldown tick) ─────────
        [Fact]
        public void RequestFormSwitch_SameForm_NoOpSuccess()
        {
            var (system, store) = MakeSystem();
            int tid = MakeFormTower(store, formCount: 3, cooldown: 5f);

            // First switch to form 1
            Assert.True(system.RequestFormSwitch(tid, 1));
            float cooldownAfterFirst = store.TowerFormSwitchCooldownRemaining[tid];
            Assert.True(cooldownAfterFirst > 0f);

            // Switching to the same form (1) is allowed (no-op), does NOT trigger cooldown again
            Assert.True(system.RequestFormSwitch(tid, 1));
            // Cooldown should not have grown (still equal to the configured value, not stacked)
            Assert.Equal(cooldownAfterFirst, store.TowerFormSwitchCooldownRemaining[tid]);
        }

        // ── 7. Out-of-range form index rejected ─────────────────────────
        [Fact]
        public void RequestFormSwitch_OutOfRange_Rejected()
        {
            var (system, store) = MakeSystem();
            int tid = MakeFormTower(store, formCount: 3, cooldown: 5f);

            Assert.False(system.RequestFormSwitch(tid, -1));
            Assert.False(system.RequestFormSwitch(tid, 3));
            Assert.False(system.RequestFormSwitch(tid, 99));
            // Active form is still 0 (default)
            Assert.Equal(0, store.GetTowerActiveForm(tid));
        }

        // ── 8. Inactive tower rejected ──────────────────────────────────
        [Fact]
        public void RequestFormSwitch_InactiveTower_Rejected()
        {
            var (system, store) = MakeSystem();
            int tid = MakeFormTower(store, formCount: 3, cooldown: 5f);
            store.TowerActive[tid] = false;

            Assert.False(system.RequestFormSwitch(tid, 1));
            Assert.Equal(0, store.GetTowerActiveForm(tid));
        }

        // ── 9. Update: FormCount = 0 → fast path is zero-overhead ───────
        [Fact]
        public void Update_NoForms_FastPathNoCooldownAdvance()
        {
            var (system, store) = MakeSystem();
            int tid = MakeFormTower(store, formCount: 0, cooldown: 5f);

            // Even with non-zero "cooldown" on the slot (legacy recycled value),
            // the fast path skips the drain (FormCount == 0)
            store.TowerFormSwitchCooldownRemaining[tid] = 5f;
            system.Update(1f);
            // Fast path means: no change. The field is NOT drained because the
            // system skips the tick entirely (the cooldown never engages for an
            // inert sentinel-zero FormCount).
            Assert.Equal(5f, store.TowerFormSwitchCooldownRemaining[tid]);
        }

        // ── 10. Update drains cooldown toward 0 (floor at 0) ────────────
        [Fact]
        public void Update_DrainsCooldownTowardZero()
        {
            var (system, store) = MakeSystem();
            int tid = MakeFormTower(store, formCount: 3, cooldown: 5f);

            // Force a partial cooldown
            store.TowerFormSwitchCooldownRemaining[tid] = 1.0f;
            system.Update(0.5f);
            Assert.Equal(0.5f, store.TowerFormSwitchCooldownRemaining[tid], 3);

            // Continue draining until it floors at 0
            system.Update(0.6f);
            Assert.Equal(0f, store.TowerFormSwitchCooldownRemaining[tid]);

            // Further updates stay at 0 (no drift negative)
            system.Update(10f);
            Assert.Equal(0f, store.TowerFormSwitchCooldownRemaining[tid]);
        }

        // ── 11. Cooldown = 0 lets tower re-switch immediately after drain ─
        [Fact]
        public void Update_CooldownZero_AllowsImmediateReSwitch()
        {
            var (system, store) = MakeSystem();
            int tid = MakeFormTower(store, formCount: 3, cooldown: 0f);

            // Switch with no cooldown
            Assert.True(system.RequestFormSwitch(tid, 1));
            Assert.Equal(1, store.GetTowerActiveForm(tid));
            Assert.Equal(0f, store.TowerFormSwitchCooldownRemaining[tid]); // 0 → no reset

            // Switch again immediately (still no cooldown)
            Assert.True(system.RequestFormSwitch(tid, 2));
            Assert.Equal(2, store.GetTowerActiveForm(tid));
        }

        // ── 12. GetActiveForm returns the right index ───────────────────
        [Fact]
        public void GetActiveForm_NoForms_ReturnsZero()
        {
            var (system, store) = MakeSystem();
            int tid = MakeFormTower(store, formCount: 0, cooldown: 0f);
            Assert.Equal(0, system.GetActiveForm(tid));
        }

        [Fact]
        public void GetActiveForm_WithForms_ReturnsConfiguredIndex()
        {
            var (system, store) = MakeSystem();
            int tid = MakeFormTower(store, formCount: 3, cooldown: 0f);
            Assert.Equal(0, system.GetActiveForm(tid));
            system.RequestFormSwitch(tid, 2);
            Assert.Equal(2, system.GetActiveForm(tid));
        }
    }
}