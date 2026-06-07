using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Components;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Beacon Tower System — Round 177 Direction 2.
    /// A Beacon tower is an active "command post": it has NO auto-attack, NO projectile,
    /// NO enemy targeting. Its only role is to broadcast persistent radius-based attack
    /// buffs (damage + attack-speed) to ALL friendly towers in range. Conceptually the
    /// complement of Shrine (which has a single typed aura: gold/mana/dmg/atk-spd only):
    ///   - Shrine:    one typed aura, all 4 typed buffs are mutually exclusive (auraType enum)
    ///   - Beacon:    two simultaneous typed buffs (dmg + atk-spd), always broadcast together
    ///   - AuraTower: legacy SOACopy pattern with two TowerAura* fields, shares wire name
    ///
    /// Buff semantics:
    ///   - The damage / attack-speed bonuses are written each frame to a per-tower additive
    ///     cache (TowerBeaconCachedDmgBonus / TowerBeaconCachedAtkSpdBonus), then RESET to 0
    ///     at the start of every frame in ComponentStore.BeginFrame(). This makes the value
    ///     "what the beacon system wants the multiplier to be THIS frame" — the existing
    ///     damage / attack-speed pipeline can read, apply, and the next frame the cache is
    ///     wiped and re-populated. No accumulation drift.
    ///   - Multiple overlapping beacons STACK additively. Example: 3 beacons at 0.10 dmg
    ///     = +0.30 damage cache for every tower in range of all 3.
    ///   - Beacons do NOT buff themselves (matches Shrine/AuraTower pattern).
    ///   - Beacons do NOT buff dispelled towers (matches AuraTower pattern).
    ///   - Beacons DO buff other beacons (so a cluster of beacons amplifies its own
    ///     internal aura output... but since beacons do not attack, this has no practical
    ///     effect — listed for future use, e.g. beacon-buff that grants mana regen).
    ///
    /// Per-frame cost when no Beacon is on the field: O(1) (sentinel-gated via
    /// _anyBeaconOnField, refreshed lazily in SetTurn()).
    /// Per-frame cost when ≥1 Beacon is on the field: O(activeBeacons × activeTowers).
    /// Active towers in a typical run ≤ 200, beacons ≤ a handful — well within budget.
    /// </summary>
    public class TowerBeaconSystem
    {
        private readonly ComponentStore store;

        // Active beacon tower IDs, refreshed in SetTurn().
        private readonly List<int> _beaconIds = new List<int>(16);

        // True iff at least one beacon was on the field during the most recent
        // SetTurn(). Used as the O(1) sentinel for the hot path.
        private bool _anyBeaconOnField;

        public TowerBeaconSystem(ComponentStore store)
        {
            this.store = store;
        }

        /// <summary>
        /// Called once per turn (e.g. from SetTurn in CombatGroup). Collects
        /// all beacon tower IDs into _beaconIds for the rest of the frame.
        /// O(activeTowers) one-time scan; the frame's ResolveBeaconBuffs pass
        /// uses _beaconIds so we don't re-scan.
        /// </summary>
        public void SetTurn()
        {
            _beaconIds.Clear();
            var activeTowerIds = store.ActiveTowerIds;
            for (int i = 0; i < activeTowerIds.Count; i++)
            {
                int towerId = activeTowerIds[i];
                if (store.TowerIsBeacon[towerId])
                    _beaconIds.Add(towerId);
            }
            _anyBeaconOnField = _beaconIds.Count > 0;
        }

        /// <summary>
        /// Resolve all beacon buffs onto nearby friendly towers and write
        /// per-tower cache arrays for downstream consumers. Called in the
        /// serial aura phase of CombatGroup (mirrors Shrine/AuraTower pattern).
        /// O(_beaconIds × activeTowers) worst case. Fast-returns when no
        /// beacon is on the field.
        /// </summary>
        public void ResolveBeaconBuffs()
        {
            if (!_anyBeaconOnField) return;

            var towerIds = store.ActiveTowerIds;
            int towerCount = towerIds.Count;

            // Phase 1: collect contributions from each beacon onto towers in range.
            for (int bi = 0; bi < _beaconIds.Count; bi++)
            {
                int beaconId = _beaconIds[bi];
                float radius = store.TowerBeaconRadius[beaconId];
                if (radius <= 0f) continue;

                float dmgBonus = store.TowerBeaconDmgBonus[beaconId];
                float spdBonus = store.TowerBeaconAtkSpdBonus[beaconId];
                // 0/0 (or 0+X) beacon still counts as a valid beacon if radius > 0 — designers
                // can use 0-bonus beacons as "structural" placeholders (rare). Both fields must
                // be 0 to inert-fast-path this beacon.
                if (dmgBonus == 0f && spdBonus == 0f) continue;

                float bx = store.PositionX[beaconId];
                float by = store.PositionY[beaconId];
                float radiusSq = radius * radius;

                for (int ti = 0; ti < towerCount; ti++)
                {
                    int targetTowerId = towerIds[ti];
                    if (targetTowerId == beaconId) continue; // don't buff self
                    if (!store.TowerActive[targetTowerId]) continue;
                    // Skip towers that are dispelled (aura buffs cleared, cannot receive new ones).
                    if (store.TowerIsDispelled[targetTowerId]) continue;

                    float tx = store.PositionX[targetTowerId];
                    float ty = store.PositionY[targetTowerId];
                    float dx = tx - bx;
                    float dy = ty - by;
                    float distSq = dx * dx + dy * dy;
                    if (distSq > radiusSq) continue;

                    // Both bonuses apply additively. Defensive: zero-bonus fields still cost
                    // a +=0 (no-op) but we already checked for the inert case above.
                    store.TowerBeaconCachedDmgBonus[targetTowerId] += dmgBonus;
                    store.TowerBeaconCachedAtkSpdBonus[targetTowerId] += spdBonus;
                }
            }
        }

        // ── Read helpers (consumed by TowerAttackSystem / TowerSynergySystem in v2) ─
        // Bounds-checked: returns 0f for invalid (negative or out-of-range) towerId
        // so the helper is safe to call from any system without a precondition.
        public float GetCachedDamageBonus(int towerId) =>
            (uint)towerId < (uint)store.TowerBeaconCachedDmgBonus.Length
                ? store.TowerBeaconCachedDmgBonus[towerId] : 0f;
        public float GetCachedAttackSpeedBonus(int towerId) =>
            (uint)towerId < (uint)store.TowerBeaconCachedAtkSpdBonus.Length
                ? store.TowerBeaconCachedAtkSpdBonus[towerId] : 0f;

        /// <summary>True iff at least one beacon was active last SetTurn.</summary>
        public bool AnyBeaconOnField => _anyBeaconOnField;
    }
}
