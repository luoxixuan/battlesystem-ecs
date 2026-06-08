#nullable enable
using System;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Round 203 Direction 4 — Tower Form / Stance Switch System.
    ///
    /// Allows towers to switch between 1..8 forms (e.g. Frost → Frostbite / Blizzard)
    /// during combat, with a per-tower cooldown that prevents spam. Distinct from
    /// TowerMorphSystem (which is build-time path swap) and TowerUpgradeSystem (which
    /// is path-driven stat growth). Form Switch is the player's *runtime* choice
    /// between stances for a single tower.
    ///
    /// Design:
    /// - Each tower has a per-tower form array (1..8 entries) snapshot from TowerConfig.Forms.
    /// - Active form index is stored in TowerActiveForm[].
    /// - Switching costs nothing but has a configurable cooldown (0 = no cooldown).
    /// - Sentinel-gated: if TowerFormCount[towerId] == 0 the tower is single-form
    ///   and the entire Update path is a zero-overhead fast path (one bound check).
    /// - The system ticks the cooldown each frame (TickTowerFormSwitchCooldown) so
    ///   the cooldown drains toward 0 and the tower may switch again.
    /// - Active skill systems (TowerActiveSkillSystem) and tower attack systems
    ///   should call store.GetTowerActiveForm(towerId) when reading form-specific
    ///   overrides (damage / range / attackSpeed / multi-strike count etc.).
    ///
    /// Usage: call RequestFormSwitch(towerId, formIndex) from player input
    /// (hotkey, UI button, or controller). Returns true on a successful switch.
    /// </summary>
    public class TowerFormSwitchSystem
    {
        private readonly ComponentStore store;

        public TowerFormSwitchSystem(ComponentStore store)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>
        /// Per-frame tick: drain every active tower's form switch cooldown by <paramref name="dt"/> seconds.
        /// Towers with no forms configured (FormCount == 0) are skipped (fast path).
        /// </summary>
        public void Update(float dt)
        {
            if (dt <= 0f) return;
            // Scan active towers — bound is small (active tower count, ≤ a few dozen).
            // ActiveTowerIds is the canonical source of truth for live towers.
            var ids = store.ActiveTowerIds;
            for (int i = 0; i < ids.Count; i++)
            {
                int towerId = ids[i];
                if (!ComponentStore.IsValidEntity(towerId)) continue;
                if (!store.TowerActive[towerId]) continue;
                // Fast path: skip towers with no forms configured
                if (store.TowerFormCount[towerId] <= 0) continue;
                store.TickTowerFormSwitchCooldown(towerId, dt);
            }
        }

        /// <summary>
        /// Request a form switch for the given tower. Returns true if the switch succeeded
        /// (cooldown ready, target index valid, tower is active). Failure modes are silent
        /// — the caller is expected to query <see cref="ComponentStore.GetTowerFormSwitchCooldownRemaining"/>
        /// to display "On Cooldown" UI when the request fails.
        /// </summary>
        public bool RequestFormSwitch(int towerId, int targetForm)
        {
            if (!ComponentStore.IsValidEntity(towerId)) return false;
            if (!store.TowerActive[towerId]) return false;
            // Fast path: no forms configured → request always rejected (and there's
            // nothing to switch between)
            if (store.TowerFormCount[towerId] <= 0) return false;
            return store.TrySwitchTowerForm(towerId, targetForm);
        }

        /// <summary>
        /// Returns the currently-active form index for the tower, or 0 if no forms
        /// are configured. UI may use this to highlight the active stance.
        /// </summary>
        public int GetActiveForm(int towerId)
        {
            return store.GetTowerActiveForm(towerId);
        }

        /// <summary>
        /// Returns true if the tower can be switched to a different form right now.
        /// I.e. has any forms configured and is not on cooldown.
        /// </summary>
        public bool CanSwitch(int towerId)
        {
            return store.CanTowerSwitchForm(towerId);
        }

        /// <summary>
        /// Returns the configured form count (0 = no forms).
        /// </summary>
        public int GetFormCount(int towerId)
        {
            return store.GetTowerFormCount(towerId);
        }

        /// <summary>
        /// Returns the remaining cooldown in seconds until the tower may switch forms again.
        /// 0 means the tower is ready to switch.
        /// </summary>
        public float GetCooldownRemaining(int towerId)
        {
            return store.GetTowerFormSwitchCooldownRemaining(towerId);
        }
    }
}