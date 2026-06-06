using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Components;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Shrine Tower System — Round 173 Direction 1.
    /// A Shrine tower is a "tower-form totem": it has NO auto-attack, NO projectile,
    /// NO enemy targeting. Its only role is to passively apply a persistent radius-based
    /// buff to all friendly towers in range. Conceptually the opposite of an AuraTower
    /// (which buffs other towers) but with its own TowerType (Shrine) so the placement
    /// and "no attack" semantics are clean.
    ///
    /// Four aura types are supported (encoded in TowerShrineAuraType):
    ///   0 = None / inert
    ///   1 = Gold       — +X gold per kill dealt by towers in range
    ///   2 = Mana       — +X mana regen per second on towers in range
    ///   3 = Damage     — multiplier cached into _cachedShrineDmgBonus (consumed
    ///                     by the existing damage pipeline in TowerAttackSystem /
    ///                     PlayerTowerAttackSystem; v1 is "set bonus, take it now"
    ///                     approach to keep the wiring footprint small)
    ///   4 = AttackSpeed — multiplier cached into _cachedShrineAtkSpdBonus
    ///                     (consumed by TowerAttackSystem; same v1 wiring)
    ///
    /// The damage / attack-speed bonuses are written each frame to a per-tower
    /// additive cache, then RESET to 0 at the start of every frame in BeginFrame().
    /// This makes the value "what the shrine system wants the multiplier to be
    /// THIS frame" — the existing pipeline reads, applies, and the next frame
    /// the cache is wiped and re-populated. No accumulation drift.
    ///
    /// v1 scope: the GOLD (aura 1) and MANA (aura 2) effects are reported into
    /// the appropriate player-pool accumulators; the DAMAGE (aura 3) and
    /// ATTACK-SPEED (aura 4) effects are exposed via GetCachedDamageBonus /
    /// GetCachedAttackSpeedBonus so the combat pipeline can consume them.
    /// For the bench2/4/5 hot paths the damage / atk-spd caches are populated
    /// but not consumed (the attack systems that would consume them are in
    /// CombatGroup; v2 will wire consumption). The cost is one int per tower
    /// per frame (2 stores into 2 SOA arrays), which is the cheap fast-path
    /// overhead even when no Shrine is on the field.
    ///
    /// Per-frame cost when no Shrine is on the field: O(1) (sentinel-gated
    /// via _anyShrineOnField, refreshed lazily in SetTurn()).
    /// Per-frame cost when ≥1 Shrine is on the field: O(active shrines ×
    /// active towers). Active towers in a typical run ≤ 200, shrines ≤ a
    /// handful — well within the budget for the serial aura phase.
    /// </summary>
    public class TowerShrineSystem
    {
        private readonly ComponentStore store;

        // Active shrine tower IDs, refreshed in SetTurn().
        private readonly List<int> _shrineIds = new List<int>(16);

        // True iff at least one shrine was on the field during the most recent
        // SetTurn(). Used as the O(1) sentinel for the hot path.
        private bool _anyShrineOnField;

        // Per-tower-indexed cached damage / attack-speed bonus that the
        // existing damage pipeline reads after this system runs.
        // These are written each frame and RESET to 0 at the start of every
        // frame by ComponentStore.BeginFrame() (see Reset() callers below).
        // The store fields are TowerShrineCachedDmgBonus[] / TowerShrineCachedAtkSpdBonus[]
        // — declared on ComponentStore_Tower for unified access from any system.

        // Aura-type keyed player-resource accumulators. v1 keeps them as
        // per-tower-style "this frame" totals that other systems can sum.
        // For GOLD (aura 1) the value is "extra gold per kill". v1 simply
        // exposes the gold-bonus value per tower via GetCachedGoldBonus(towerId)
        // — GoldSystem can read it on the OnEnemyKilled path. To keep the
        // Round-173 footprint small, v1 sets the cached bonus but GoldSystem
        // is left to consume it in v2. The cost is the same (one SOA store).
        // For MANA (aura 2) the value is "extra mana per second". v1 stores
        // the total per-tower mana regen contribution in
        // TowerShrineCachedManaRegen[] so ManaSystem can sum it.

        public TowerShrineSystem(ComponentStore store)
        {
            this.store = store;
        }

        /// <summary>
        /// Called once per turn (e.g. from SetTurn in CombatGroup). Collects
        /// all shrine tower IDs into _shrineIds for the rest of the frame.
        /// O(activeTowers) one-time scan; the frame's ResolveShrineBuffs pass
        /// uses _shrineIds so we don't re-scan.
        /// </summary>
        public void SetTurn()
        {
            _shrineIds.Clear();
            var activeTowerIds = store.ActiveTowerIds;
            for (int i = 0; i < activeTowerIds.Count; i++)
            {
                int towerId = activeTowerIds[i];
                if (store.TowerIsShrine[towerId])
                    _shrineIds.Add(towerId);
            }
            _anyShrineOnField = _shrineIds.Count > 0;
        }

        /// <summary>
        /// Resolve all shrine buffs onto nearby friendly towers and write
        /// per-tower cache arrays for downstream consumers. Called in the
        /// serial aura phase of CombatGroup (mirrors AuraTowerSystem pattern).
        /// O(_shrineIds × activeTowers) worst case. Fast-returns when no
        /// shrine is on the field.
        /// </summary>
        public void ResolveShrineBuffs()
        {
            if (!_anyShrineOnField) return;

            var towerIds = store.ActiveTowerIds;
            int towerCount = towerIds.Count;

            // Phase 1: collect contributions from each shrine onto towers in range.
            for (int si = 0; si < _shrineIds.Count; si++)
            {
                int shrineId = _shrineIds[si];
                float radius = store.TowerShrineRadius[shrineId];
                if (radius <= 0f) continue;

                int auraType = store.TowerShrineAuraType[shrineId];
                if (auraType <= 0) continue; // 0 = None / inert

                float potency = store.TowerShrinePotency[shrineId];
                if (potency == 0f) continue; // 0 = inert (avoids float-noise work)

                float sx = store.PositionX[shrineId];
                float sy = store.PositionY[shrineId];
                float radiusSq = radius * radius;

                for (int ti = 0; ti < towerCount; ti++)
                {
                    int targetTowerId = towerIds[ti];
                    if (targetTowerId == shrineId) continue; // don't buff self
                    if (!store.TowerActive[targetTowerId]) continue;
                    // Skip towers that are dispelled (aura buffs cleared, cannot receive new ones).
                    if (store.TowerIsDispelled[targetTowerId]) continue;

                    float tx = store.PositionX[targetTowerId];
                    float ty = store.PositionY[targetTowerId];
                    float dx = tx - sx;
                    float dy = ty - sy;
                    float distSq = dx * dx + dy * dy;
                    if (distSq > radiusSq) continue;

                    // Apply the appropriate cache for this aura type.
                    switch (auraType)
                    {
                        case 1: // Gold — extra gold per kill
                            store.TowerShrineCachedGoldBonus[targetTowerId] += potency;
                            break;
                        case 2: // Mana — extra mana regen per second
                            store.TowerShrineCachedManaRegen[targetTowerId] += potency;
                            break;
                        case 3: // Damage — multiplier
                            store.TowerShrineCachedDmgBonus[targetTowerId] += potency;
                            break;
                        case 4: // AttackSpeed — multiplier
                            store.TowerShrineCachedAtkSpdBonus[targetTowerId] += potency;
                            break;
                        // Defensive default: unknown aura type → no effect.
                        // Adding new types here is a deliberate code change.
                    }
                }
            }
        }

        // ── Read helpers (consumed by GoldSystem / ManaSystem in v2) ────────
        // Bounds-checked: returns 0f for invalid (negative or out-of-range) towerId
        // so the helper is safe to call from any system without a precondition.
        public float GetCachedGoldBonus(int towerId) =>
            (uint)towerId < (uint)store.TowerShrineCachedGoldBonus.Length
                ? store.TowerShrineCachedGoldBonus[towerId] : 0f;
        public float GetCachedManaRegen(int towerId) =>
            (uint)towerId < (uint)store.TowerShrineCachedManaRegen.Length
                ? store.TowerShrineCachedManaRegen[towerId] : 0f;
        public float GetCachedDamageBonus(int towerId) =>
            (uint)towerId < (uint)store.TowerShrineCachedDmgBonus.Length
                ? store.TowerShrineCachedDmgBonus[towerId] : 0f;
        public float GetCachedAttackSpeedBonus(int towerId) =>
            (uint)towerId < (uint)store.TowerShrineCachedAtkSpdBonus.Length
                ? store.TowerShrineCachedAtkSpdBonus[towerId] : 0f;

        /// <summary>True iff at least one shrine was active last SetTurn.</summary>
        public bool AnyShrineOnField => _anyShrineOnField;
    }
}
