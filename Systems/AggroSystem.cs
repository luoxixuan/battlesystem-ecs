using System;
using System.Collections.Generic;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Aggro / Focus Fire System — Round 142 Direction 5.
    ///
    /// Player-driven "mark a target" tool. When the player (or a tower / skill)
    /// calls MarkFocusTower(enemyId, towerId, duration), the target enemy will
    /// prioritize the marked tower as its attack target for the next N seconds.
    /// This is the inverse of the existing TauntSystem (which forces enemies to
    /// attack a tower automatically based on proximity) — AggroSystem is an
    /// *opt-in* strategic tool the player triggers, not a passive aura.
    ///
    /// Design rationale:
    ///   - Mark focus is an EVENT, not a per-frame aura. We don't scan the map
    ///     for enemies; the caller knows which enemies to mark.
    ///   - Per-frame Update() only decrements the focus duration and clears
    ///     expired assignments. Fast-path: skip the loop when no enemy has an
    ///     active focus (a single bool sentinel — set on first mark, cleared
    ///     when the last focus expires).
    ///   - Stale tower IDs (target tower sold / destroyed) are sanitized
    ///     eagerly in ComponentStore.DestroyEntity(), not lazily in this
    ///     system. This keeps the per-frame Update() a single float cmp +
    ///     decrement, no TowerActive[] read.
    ///   - The actual "AI prefers the focus tower" behavior is intentionally
    ///     out of scope for this round — EnemyAISystem can read EnemyFocusTowerId
    ///     in a follow-up. This round establishes the data + API + lifecycle.
    ///
    /// Distinction from related systems:
    ///   - TauntSystem: tower aura that auto-marks all enemies in radius.
    ///   - MarkSystem: stack-based hit counter for vulnerability payoff.
    ///   - AggroSystem: per-enemy "I am being told to focus this tower" flag.
    ///
    /// Per-frame cost: O(n_enemies) ONLY when at least one enemy has an active
    /// focus assignment. When the system is dormant (no marks this wave), the
    /// sentinel is false and Update() returns in O(1). Benchmarked against
    /// the existing TauntSystem fast-path (which has a similar "skip when
    /// empty" gate) for parity.
    /// </summary>
    public class AggroSystem
    {
        private readonly ComponentStore store;

        // ── Sentinel: true when at least one enemy currently has an active focus.
        //    Set on MarkFocusTower, cleared when the per-frame Update finds the
        //    last assignment expired. Avoids a full ActiveEnemyIds sweep on the
        //    common "no aggro commands" path (most frames in most waves).
        private bool _hasActiveFocus;

        public AggroSystem(ComponentStore store)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
        }

        // ─── Public API ────────────────────────────────────────────────────

        /// <summary>
        /// Assign a focus mark on a single enemy: "attack this tower for the
        /// next `duration` seconds (or until the tower is destroyed)".
        /// No-op if:
        ///   - enemyId is invalid / inactive
        ///   - towerId is invalid / inactive
        ///   - duration &lt;= 0
        /// Returns true if the mark was applied, false if no-op.
        /// </summary>
        public bool MarkFocusTower(int enemyId, int towerId, float duration)
        {
            if (!IsValidEnemy(enemyId)) return false;
            if (!IsValidTower(towerId)) return false;
            if (duration <= 0f) return false;

            store.EnemyFocusTowerId[enemyId] = towerId;
            // Take max of (current remaining, new duration) so a second mark
            // doesn't *shorten* an existing long focus (typical RTS/ARPG semantics
            // — refreshing a buff extends it). This matches MarkSystem.AddMark's
            // "stack-add" semantics, though focus is binary (not stackable).
            if (store.EnemyFocusDurationLeft[enemyId] < duration)
                store.EnemyFocusDurationLeft[enemyId] = duration;

            _hasActiveFocus = true;
            return true;
        }

        /// <summary>
        /// Bulk version: mark N enemies at once (e.g., a "focus fire" AoE skill
        /// or a wave-1 "all enemies focus this tower" dev command). Returns the
        /// count of enemies actually marked (excludes invalid/inactive).
        /// </summary>
        public int MarkFocusTowerBulk(IList<int> enemyIds, int towerId, float duration)
        {
            if (enemyIds == null) return 0;
            if (!IsValidTower(towerId)) return 0;
            if (duration <= 0f) return 0;

            int marked = 0;
            for (int i = 0; i < enemyIds.Count; i++)
            {
                int eid = enemyIds[i];
                if (!IsValidEnemy(eid)) continue;
                store.EnemyFocusTowerId[eid] = towerId;
                if (store.EnemyFocusDurationLeft[eid] < duration)
                    store.EnemyFocusDurationLeft[eid] = duration;
                marked++;
            }
            if (marked > 0) _hasActiveFocus = true;
            return marked;
        }

        /// <summary>
        /// Clear any active focus on a single enemy (e.g., dispel / banish / wave
        /// end). Resets both the tower id (-1) and the duration (0f).
        /// </summary>
        public void ClearFocus(int enemyId)
        {
            if (!IsValidEnemy(enemyId)) return;
            if (store.EnemyFocusTowerId[enemyId] == -1 &&
                store.EnemyFocusDurationLeft[enemyId] <= 0f)
                return; // already clear
            store.EnemyFocusTowerId[enemyId] = -1;
            store.EnemyFocusDurationLeft[enemyId] = 0f;
            // Do NOT clear _hasActiveFocus here — other enemies may still have
            // active focus. The Update() loop will detect "no enemies left with
            // focus" and clear the sentinel naturally.
        }

        /// <summary>Read-only check used by HUD / AI: does this enemy have a
        /// live focus assignment right now?</summary>
        public bool HasFocus(int enemyId)
        {
            if (!IsValidEnemy(enemyId)) return false;
            return store.EnemyFocusTowerId[enemyId] != -1 &&
                   store.EnemyFocusDurationLeft[enemyId] > 0f;
        }

        /// <summary>Read-only access to the focus tower id (returns -1 if no
        /// active focus). Used by AI/Movement follow-up rounds.</summary>
        public int GetFocusTowerId(int enemyId)
        {
            if (!IsValidEnemy(enemyId)) return -1;
            if (store.EnemyFocusDurationLeft[enemyId] <= 0f) return -1;
            return store.EnemyFocusTowerId[enemyId];
        }

        // ─── Per-frame lifecycle ───────────────────────────────────────────

        /// <summary>
        /// Tick all active focus durations down by <paramref name="deltaTime"/>.
        /// Clears assignments that have expired (tower id → -1, duration → 0f).
        /// Fast-path: returns immediately when no enemy is currently focused
        /// (zero overhead in the common case where the player has not issued
        /// any aggro commands this wave).
        /// Called from CombatGroup at the end (after all attacks resolve, so
        /// the focus duration spans a full tick of "this enemy is focusing
        /// that tower" before it might be cleared).
        /// </summary>
        public void Update(float deltaTime)
        {
            if (deltaTime <= 0f) return;
            if (!_hasActiveFocus) return; // common case: no marks → O(1)

            var activeEnemies = store.ActiveEnemyIds;
            int count = activeEnemies.Count;
            bool stillAnyActive = false;

            for (int i = 0; i < count; i++)
            {
                int enemyId = activeEnemies[i];
                if (!store.EnemyActive[enemyId]) continue;
                if (store.EnemyFocusTowerId[enemyId] == -1) continue; // no focus

                float left = store.EnemyFocusDurationLeft[enemyId] - deltaTime;
                if (left <= 0f)
                {
                    // Expired — clear
                    store.EnemyFocusTowerId[enemyId] = -1;
                    store.EnemyFocusDurationLeft[enemyId] = 0f;
                }
                else
                {
                    store.EnemyFocusDurationLeft[enemyId] = left;
                    stillAnyActive = true;
                }
            }

            // If no enemy has a live focus after this tick, drop the sentinel
            // so subsequent frames take the O(1) fast path.
            if (!stillAnyActive) _hasActiveFocus = false;
        }

        /// <summary>
        /// Called by SystemRegistry / GameManager when an enemy dies. Resets
        /// the per-enemy focus state (defense in depth — DestroyEntity already
        /// does this, but exposing a public hook makes the contract explicit
        /// and lets tests verify it without going through DestroyEntity).
        /// No-op if the enemy is not currently active (defensive: caller
        /// might invoke after the ID was already recycled; we don't want to
        /// corrupt the new entity's focus state).
        /// </summary>
        public void OnEnemyDestroyed(int enemyId)
        {
            if (enemyId < 0 || enemyId >= ComponentStore.MAX_ENTITIES) return;
            if (enemyId < store.EnemyActive.Length && !store.EnemyActive[enemyId]) return;
            store.EnemyFocusTowerId[enemyId] = -1;
            store.EnemyFocusDurationLeft[enemyId] = 0f;
            // Do NOT touch _hasActiveFocus here — other enemies may still hold focus.
        }

        // ─── Internal helpers ──────────────────────────────────────────────

        private bool IsValidEnemy(int enemyId)
        {
            if (enemyId < 0 || enemyId >= ComponentStore.MAX_ENTITIES) return false;
            return store.EnemyActive[enemyId];
        }

        private bool IsValidTower(int towerId)
        {
            if (towerId < 0 || towerId >= ComponentStore.MAX_ENTITIES) return false;
            return store.TowerActive[towerId];
        }
    }
}
