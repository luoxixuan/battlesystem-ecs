using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Components;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Heal Aura System — passive tower-to-tower healing. (Round 122 Direction 2)
    ///
    /// Towers with TowerHealAuraRadius > 0 + TowerHealAuraAmount > 0 are "healers". Each
    /// TowerHealAuraInterval seconds, the healer restores TowerHealAuraAmount HP to every
    /// friendly Palisade tower (the only tower archetype with a HP pool) within
    /// TowerHealAuraRadius world-units, clamped to PalisadeMaxHP so overheal is wasted.
    ///
    /// Design notes:
    ///  - Designers opt-in via TowerConfig.HealAuraRadius > 0; default 0 = no overhead in
    ///    the hot path.
    ///  - This system targets PalisadeHP only. Non-Palisade towers are not healed (they
    ///    have no HP pool — they go straight from alive to TowerActive=false on destroy).
    ///  - Multiple healers in range stack additively (each contributes its own amount per
    ///    tick). No "leader-only" arbitration — designers can balance via small per-healer
    ///    amounts.
    ///  - The system is serial (no Parallel.For). Heal-aura towers are rare (support role),
    ///    and the inner loop scans ActiveTowerIds which is bounded by ~towers-on-field
    ///    (a few dozen at most). Serial keeps the patch simple and side-effect-free.
    ///  - Independent of TowerRegen (the per-tower passive HP regen) and TowerHealOnKillAmount
    ///    (player heal on tower kill). Both can stack with this system.
    /// </summary>
    public class HealAuraSystem
    {
        private ComponentStore store;
        // Cached list of healer tower IDs (heal-aura towers that have a non-zero radius).
        // Rebuilt each frame by SetTurn() to avoid scanning the full ActiveTowerIds every
        // Update. The cache is bounded by the number of heal-aura towers (typically 0-3).
        private List<int> _healerTowerIds;

        public HealAuraSystem(ComponentStore store)
        {
            this.store = store;
            _healerTowerIds = new List<int>(16);
        }

        /// <summary>
        /// Cache all heal-aura tower IDs for the upcoming Update. Called once per frame
        /// (typically right after SpatialGrid rebuild so the healer list is fresh). The
        /// "radius > 0" guard means towers that were never opt-in (default state) are
        /// skipped, keeping the working set tiny.
        /// </summary>
        public void SetTurn()
        {
            _healerTowerIds.Clear();
            var activeTowerIds = store.ActiveTowerIds;
            for (int i = 0; i < activeTowerIds.Count; i++)
            {
                int towerId = activeTowerIds[i];
                // Early-out: radius<=0 means no aura configured. This is the dominant
                // case (no heal-aura towers on field), and the bool check is essentially
                // free — no string compares, no allocations.
                if (store.TowerHealAuraRadius[towerId] > 0f && store.TowerHealAuraAmount[towerId] > 0f)
                    _healerTowerIds.Add(towerId);
            }
        }

        /// <summary>
        /// Apply heal ticks to all friendly Palisade towers in range of any healer. Called
        /// once per frame in the WavePhase serial segment (after damage resolution so the
        /// heal doesn't accidentally cancel a kill-this-frame by bringing HP up after the
        /// kill check). Internally ticks the per-healer cooldown and resets it on fire.
        /// </summary>
        /// <param name="deltaTime">frame delta in seconds (used to decrement per-healer timer).</param>
        public void Update(float deltaTime)
        {
            if (_healerTowerIds.Count == 0) return;

            // Per-healer single-pass loop: tick cooldown, decide fire, then reset.
            // We MUST do tick + fire + reset in one pass — splitting them (e.g. a
            // pre-pass to tick timers and a second pass to fire) is broken because
            // resetting timer=interval on expiry makes the fire-pass's
            // "timer > 0 ? skip" check always true, so the heal never fires.
            for (int hi = 0; hi < _healerTowerIds.Count; hi++)
            {
                int healerId = _healerTowerIds[hi];
                float interval = store.TowerHealAuraInterval[healerId];
                float timer = store.TowerHealAuraTimer[healerId];

                if (interval <= 0f)
                {
                    // interval=0 means "fire every frame" — keep timer at 0 so the
                    // fire branch below triggers every frame.
                    store.TowerHealAuraTimer[healerId] = 0f;
                }
                else
                {
                    // Decrement; if it expired, fire (after the if-block) and reset.
                    timer -= deltaTime;
                    if (timer > 0f)
                    {
                        // Still on cooldown — write back and skip fire.
                        store.TowerHealAuraTimer[healerId] = timer;
                        continue;
                    }
                    // Expired: reset to interval for the next cycle. (Falls through
                    // to the fire block below.)
                    store.TowerHealAuraTimer[healerId] = interval;
                }

                float radius = store.TowerHealAuraRadius[healerId];
                if (radius <= 0f) continue; // defensive: should not happen after SetTurn filter
                float amount = store.TowerHealAuraAmount[healerId];
                if (amount <= 0f) continue;  // defensive: should not happen after SetTurn filter

                float healerX = store.PositionX[healerId];
                float healerY = store.PositionY[healerId];
                float radiusSq = radius * radius;

                // Scan all active towers for friendly Palisade targets. We do an O(N)
                // scan over ActiveTowerIds. This is intentional: heal-aura towers are
                // rare and the active-tower count is bounded (a few dozen). No need
                // for a SpatialGrid on the tower set.
                var activeTowerIds = store.ActiveTowerIds;
                int activeCount = activeTowerIds.Count;
                for (int ti = 0; ti < activeCount; ti++)
                {
                    int targetId = activeTowerIds[ti];
                    if (targetId == healerId) continue; // don't self-heal
                    // Only Palisade towers have a HP pool. Skip the rest to avoid
                    // writing to non-existent fields.
                    if (!store.TowerIsPalisade[targetId]) continue;
                    // Skip dead palisades (HP <= 0 means the destroy flag is set; the
                    // entity will be reaped this frame anyway).
                    if (store.PalisadeHP[targetId] <= 0f) continue;
                    // Skip already-at-max palisades (heal would be wasted, but we still
                    // pay the distance check cost — that's fine for the common case).
                    if (store.PalisadeHP[targetId] >= store.PalisadeMaxHP[targetId]) continue;

                    float dx = store.PositionX[targetId] - healerX;
                    float dy = store.PositionY[targetId] - healerY;
                    if (dx * dx + dy * dy > radiusSq) continue;

                    // Apply heal, clamped to max HP. We do not apply overheal — any
                    // excess amount is silently dropped (designer-side over-tuning is
                    // safe; no overflow into next frame, no double-heal on subsequent
                    // ticks because we re-check the cap on the next pass).
                    float newHp = store.PalisadeHP[targetId] + amount;
                    float maxHp = store.PalisadeMaxHP[targetId];
                    if (newHp > maxHp) newHp = maxHp;
                    store.PalisadeHP[targetId] = newHp;
                }
            }
        }
    }
}
