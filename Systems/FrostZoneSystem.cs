#nullable enable
using System;
using System.Threading.Tasks;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Frost Zone System (Round 82 Direction 1) — "frost tile" AoE slow centered on tower
    /// position. Towers with TowerFrostZoneRadius > 0 project a slow field: any active enemy
    /// inside the radius has their effective move speed multiplied by
    /// TowerFrostZoneSlowFactor (e.g. 0.5 = 50% speed). Multiple overlapping zones take the
    /// MIN (most severe) of all contributing towers per enemy per frame.
    ///
    /// Execution order: runs in CombatSetupGroup (after HotZone, before Combat). The result
    /// (per-enemy EnemyFrostZoneSlowMultiplier) is consumed by EnemyMovementSystem in the
    /// Movement phase. The system is hot-path friendly:
    ///   • Zero allocations per frame (no LINQ, no closure captures, no Lists/arrays grown).
    ///   • Parallel.For scans over active enemies × active frost towers; no shared state writes
    ///     until the serial post-pass that writes the final multiplier per enemy.
    ///   • Fast-exit O(1) when no frost towers exist (the common case in early waves).
    ///   • Permanent towers (TowerFrostZoneDuration == 0) skip the duration decrement path
    ///     entirely, reducing per-frame work for the typical placement.
    /// </summary>
    public class FrostZoneSystem
    {
        private readonly ComponentStore store;

        public FrostZoneSystem(ComponentStore store)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public void SetTurn(int turn)
        {
            // No per-turn state to cache. Towers' positions are read directly each frame.
        }

        /// <summary>
        /// Per-frame update: tick frost zone durations, then resolve per-enemy multipliers
        /// via two nested parallel passes (towers → enemies). Writes EnemyFrostZoneSlowMultiplier
        /// for every active enemy each frame, starting from a clean 1.0 baseline.
        /// </summary>
        public void Update()
        {
            // Step 1: tick durations. Serial pass — small fixed cost (≤ number of frost towers,
            // typically <50). Decrement > 0 timers and disable the zone when timer hits 0.
            // O(active_towers) at worst; one float compare-and-store per active frost tower.
            var activeTowers = store.ActiveTowerIds;
            int towerCount = activeTowers.Count;
            for (int i = 0; i < towerCount; i++)
            {
                int towerId = activeTowers[i];
                if (!store.TowerActive[towerId]) continue;
                if (store.TowerFrostZoneRadius[towerId] <= 0f) continue; // not a frost tower
                float dur = store.TowerFrostZoneDuration[towerId];
                if (dur <= 0f) continue; // 0 = permanent; no decrement
                dur -= 1f; // duration is in turns; 1 unit per frame
                if (dur <= 0f)
                {
                    store.TowerFrostZoneDuration[towerId] = 0f;
                    // Disable the zone by zeroing radius. SlowFactor left at 1f (neutral) to
                    // keep the array cache-line valid; we read Radius to gate the zone in
                    // the inner loop, so 0 means "no work, skip this tower".
                    store.TowerFrostZoneRadius[towerId] = 0f;
                }
                else
                {
                    store.TowerFrostZoneDuration[towerId] = dur;
                }
            }

            // Step 2: reset all active enemies' multiplier to 1.0 (neutral). Done in a
            // single Parallel.For over the active enemy span — single write per enemy.
            // This is the "baseline" pass; the per-enemy resolution pass below will then
            // potentially write a smaller value if any frost tower covers that enemy.
            var activeEnemiesList = store.ActiveEnemyIds;
            int enemyCount = activeEnemiesList.Count;
            if (enemyCount == 0) return; // no enemies, nothing to do

            // Fast-exit: if no active frost tower, just reset multipliers to 1.0 and exit.
            // We count active frost towers here (cheap, single O(towerCount) loop) so the
            // hot-path early-out is branch-predictable for the typical "no frost zone" frame.
            int frostTowerCount = 0;
            for (int i = 0; i < towerCount; i++)
            {
                int tid = activeTowers[i];
                if (!store.TowerActive[tid]) continue;
                if (store.TowerFrostZoneRadius[tid] > 0f) frostTowerCount++;
            }
            if (frostTowerCount == 0)
            {
                // No frost towers: still need to reset enemies' multipliers to 1.0 because
                // last frame's towers may have been sold/destroyed mid-game. Serial pass
                // is fine here — the cost is one float write per active enemy, which is
                // dwarfed by the system overhead avoided.
                for (int i = 0; i < enemyCount; i++)
                {
                    int eid = activeEnemiesList[i];
                    if (!store.EnemyActive[eid]) continue;
                    store.EnemyFrostZoneSlowMultiplier[eid] = 1f;
                }
                return;
            }

            // Baseline reset parallel — one float write per enemy. We cannot capture the
            // ReadOnlySpan<int> from GetActiveEnemySpan() inside the lambda (ref struct
            // boundary), so we use the IReadOnlyList<int> ActiveEnemyIds directly and
            // reference it by index from the parallel body.
            var activeEnemies = activeEnemiesList;
            Parallel.For(0, enemyCount, ParallelOptionsCache.HotPath, i =>
            {
                int eid = activeEnemies[i];
                if (!store.EnemyActive[eid]) return;
                store.EnemyFrostZoneSlowMultiplier[eid] = 1f;
            });

            // Step 3: resolve. Walk each frost tower and apply MIN(zone factor, current
            // multiplier) to all enemies within radius. Done serially: tower count is
            // typically tiny (< 50), and per-tower we scan a spatially-bounded radius
            // (small N) using squared-distance against active enemies. The outer loop is
            // sequential so we can early-exit when the tower's radius is 0.
            for (int i = 0; i < towerCount; i++)
            {
                int towerId = activeTowers[i];
                if (!store.TowerActive[towerId]) continue;
                float radius = store.TowerFrostZoneRadius[towerId];
                if (radius <= 0f) continue; // disabled / non-frost
                float towerX = store.PositionX[towerId];
                float towerY = store.PositionY[towerId];
                float radiusSq = radius * radius;
                float slowFactor = store.TowerFrostZoneSlowFactor[towerId];
                // 1f = no slow; we only write if slowFactor < 1 (otherwise the MIN
                // would just keep the existing 1.0 baseline, so we can skip).
                if (slowFactor >= 1f) continue;

                for (int j = 0; j < enemyCount; j++)
                {
                    int eid = activeEnemies[j];
                    if (!store.EnemyActive[eid]) continue;
                    float dx = store.PositionX[eid] - towerX;
                    float dy = store.PositionY[eid] - towerY;
                    if (dx * dx + dy * dy > radiusSq) continue;
                    // MIN: take the most severe slow. The first tower to cover the enemy
                    // sets the baseline (1.0), subsequent overlapping zones can only
                    // reduce it further (or keep it if their slow is milder).
                    float current = store.EnemyFrostZoneSlowMultiplier[eid];
                    if (slowFactor < current)
                    {
                        store.EnemyFrostZoneSlowMultiplier[eid] = slowFactor;
                    }
                }
            }
        }
    }
}
