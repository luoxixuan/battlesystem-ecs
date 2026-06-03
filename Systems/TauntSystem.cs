using System.Collections.Generic;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Taunt Tower System — forces nearby enemies to retarget a taunt tower (instead of the
    /// path/player base). The dual of the Aggro/Leash system in EnemyMovementSystem:
    ///   • Aggro: enemy actively chases player when within range (EnemyIsLeashed).
    ///   • Taunt: tower actively forces enemy to attack itself (EnemyTauntedByTowerId).
    ///
    /// Algorithm (per frame, runs early in CombatSetupGroup — before TowerAttackSystem targets):
    ///   1. Reset all EnemyTauntedByTowerId[enemyId] = -1 (default, no taunt).
    ///   2. For each active tower with TowerIsTaunt=true and TowerTauntRadius>0:
    ///      a. For each active enemy within radius, set EnemyTauntedByTowerId = towerId,
    ///         using a "closest taunt tower wins" rule: if the enemy is already assigned to
    ///         a closer taunt tower this frame, keep the closer one.
    ///   3. Stale-field cleanup: if an enemy's taunt target was destroyed/sold/removed
    ///      (no longer in ActiveTowerIds), the reset in step 1 zeroes it out automatically.
    ///
    /// Performance characteristics:
    ///   • Default path: all TowerIsTaunt=false → step 2's loop body executes 0 times; total
    ///     work is O(n_enemies) for the reset + O(0) for the scan. Negligible overhead.
    ///   • Active path: O(n_taunt_towers * n_enemies) — acceptable since taunt towers are
    ///     a support role and the inner loop is a cheap distance check.
    ///
    /// Effect on gameplay (out-of-scope for this round's 5-file budget):
    ///   The EnemyMovementSystem / EnemyAISystem can read EnemyTauntedByTowerId to override
    ///   the enemy's BT path/attack target. This round only sets the field; the override
    ///   logic is left as a follow-up to keep this direction within 5 source files.
    /// </summary>
    public class TauntSystem
    {
        private readonly ComponentStore store;
        // Per-frame: active taunt tower IDs (filtered subset of ActiveTowerIds where
        // TowerIsTaunt=true && TowerTauntRadius>0). Rebuilt each frame in SetTurn() so that
        // towers added/removed/destroyed mid-wave are reflected without manual bookkeeping.
        private readonly List<int> _tauntTowerIds;

        public TauntSystem(ComponentStore store)
        {
            this.store = store;
            _tauntTowerIds = new List<int>(8);
        }

        /// <summary>
        /// Phase 0 of CombatSetupGroup: collect all currently-active taunt tower IDs into the
        /// internal list. Called once per frame; cost is O(n_active_towers) with a single
        /// bool+float check per tower (zero allocations, branch-predictor friendly).
        /// </summary>
        public void SetTurn()
        {
            _tauntTowerIds.Clear();
            var activeTowerIds = store.ActiveTowerIds;
            for (int i = 0; i < activeTowerIds.Count; i++)
            {
                int towerId = activeTowerIds[i];
                // O(1) zero-overhead early-exit: most towers aren't taunt towers, so this
                // branch is the hot path. radius>0 guards against inert IsTaunt=true entries.
                if (store.TowerIsTaunt[towerId] && store.TowerTauntRadius[towerId] > 0f)
                {
                    _tauntTowerIds.Add(towerId);
                }
            }
        }

        /// <summary>
        /// Phase 1 of CombatSetupGroup: reset all enemy taunt assignments, then re-assign
        /// enemies that are within any active taunt tower's radius. "Closest taunt tower
        /// wins" rule: when two taunt towers overlap, the enemy picks the nearest one (more
        /// intuitive than "first in scan order" — symmetric and deterministic regardless of
        /// ActiveTowerIds ordering).
        /// </summary>
        public void ResolveTauntAssignments()
        {
            if (_tauntTowerIds.Count == 0)
            {
                // No taunt towers this frame → still need to clear stale assignments from
                // last frame (e.g. the taunt tower was sold/destroyed). Cheap O(n_enemies)
                // reset; the inner check is a single int comparison.
                var enemyIds = store.ActiveEnemyIds;
                int enemyCount = enemyIds.Count;
                for (int ei = 0; ei < enemyCount; ei++)
                {
                    int enemyId = enemyIds[ei];
                    if (store.EnemyActive[enemyId])
                        store.EnemyTauntedByTowerId[enemyId] = -1;
                }
                return;
            }

            // Cache taunt tower centers + radii (squared) once — reused in the inner loop.
            int tauntCount = _tauntTowerIds.Count;
            var tx = new float[tauntCount];
            var ty = new float[tauntCount];
            var trSq = new int[tauntCount];
            for (int ti = 0; ti < tauntCount; ti++)
            {
                int towerId = _tauntTowerIds[ti];
                tx[ti] = store.PositionX[towerId];
                ty[ti] = store.PositionY[towerId];
                float r = store.TowerTauntRadius[towerId];
                trSq[ti] = (int)(r * r);
            }

            var enemyIdsAll = store.ActiveEnemyIds;
            int enemyCountAll = enemyIdsAll.Count;
            for (int ei = 0; ei < enemyCountAll; ei++)
            {
                int enemyId = enemyIdsAll[ei];
                if (!store.EnemyActive[enemyId]) continue;

                float ex = store.PositionX[enemyId];
                float ey = store.PositionY[enemyId];

                // Find the closest taunt tower in range. -1 = no taunt assigned.
                int closestTowerId = -1;
                int closestDistSq = int.MaxValue;
                for (int ti = 0; ti < tauntCount; ti++)
                {
                    float dx = ex - tx[ti];
                    float dy = ey - ty[ti];
                    int distSq = (int)(dx * dx + dy * dy);
                    if (distSq <= trSq[ti] && distSq < closestDistSq)
                    {
                        closestDistSq = distSq;
                        closestTowerId = _tauntTowerIds[ti];
                    }
                }
                store.EnemyTauntedByTowerId[enemyId] = closestTowerId;
            }
        }
    }
}
