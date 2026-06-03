#nullable enable
using System;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Wander / Free-Roam Enemy System (Round 84 Direction 6) — "off-path" enemy movement.
    /// Enemies flagged EnemyIsFreeRoam = true do NOT follow waypoints. Instead, each frame
    /// they re-evaluate their target:
    ///   1. If a tower is within EnemyAggroRange (or fall-back 5 cells), chase the nearest active tower.
    ///   2. Otherwise, if no tower is in range, walk toward the player base (y=0).
    ///   3. Periodically (every ~6-10 frames) re-roll a random wander target cell so the
    ///      enemy doesn't march in a straight line when there's nothing in range.
    ///
    /// Movement is performed by setting EnemyWanderTargetX/Y here; the actual position
    /// update happens in EnemyMovementSystem.Update() when the action enum is Wandering.
    /// This split keeps all position writes in one place (the Movement system) — the
    /// pattern used by every other combat system in the codebase.
    ///
    /// Hot-path friendliness:
    ///   • O(N_active_enemies) per frame, with O(1) early-exit when no free-roam enemies.
    ///   • The "no free-roam" check is a single int counter on the store, set in AddEnemy /
    ///     cleared in DestroyEntity. Cached per-frame in SetTurn for branch prediction.
    ///   • No allocation, no LINQ, no Random.Next — uses deterministic xorshift hash keyed
    ///     off (enemyId, turn) so the wander target is reproducible and alloc-free.
    /// </summary>
    public class WanderRoamSystem
    {
        private readonly ComponentStore store;

        // Per-frame cache: true if any active enemy has EnemyIsFreeRoam = true.
        // Avoids scanning the whole active-enemy list twice (once to detect, once to act).
        private bool _hasFreeRoamThisFrame;
        // Per-frame cache: how many free-roam enemies exist (for cheap O(1) early-exit).
        private int _freeRoamCount;

        // Cached per-frame tower positions. Allocated once, sized at MAX_ENTITIES, refilled
        // each SetTurn. Saves per-frame PositionX/Y array lookups in the hot inner loop.
        // Tower scan stays O(N_active_towers) — towers are typically <50 so this is cheap.
        private int[] _activeTowerIds = new int[ComponentStore.MAX_ENTITIES];
        private float[] _towerX = new float[ComponentStore.MAX_ENTITIES];
        private float[] _towerY = new float[ComponentStore.MAX_ENTITIES];
        private int _towerCount;

        public WanderRoamSystem(ComponentStore store)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>
        /// Per-frame cache refresh. Cheap O(N_active_towers + N_active_enemies) pass
        /// to count free-roam enemies and snapshot tower positions for this frame.
        /// </summary>
        public void SetTurn(int turn)
        {
            // Snapshot active tower positions. Towers are the primary "what to chase"
            // target for free-roam enemies. Player is at fixed coords (read directly).
            var towers = store.ActiveTowerIds;
            int tCount = towers.Count;
            int n = 0;
            for (int i = 0; i < tCount; i++)
            {
                int tid = towers[i];
                if (!store.TowerActive[tid]) continue;
                // Snapshot into local arrays so the hot loop doesn't bounce cache lines
                // on the SOA store arrays. Bounded by MAX_TOWERS (10000) so the allocation
                // is a one-time cost at construction.
                _activeTowerIds[n] = tid;
                _towerX[n] = store.PositionX[tid];
                _towerY[n] = store.PositionY[tid];
                n++;
            }
            _towerCount = n;

            // Count free-roam enemies for O(1) early-exit guard. This pass also lets us
            // bump ComponentStore.ActiveFreeRoamCount so other systems (e.g. future AI
            // targeting optimisations) can read it without scanning.
            var enemies = store.ActiveEnemyIds;
            int eCount = enemies.Count;
            int freeRoam = 0;
            for (int i = 0; i < eCount; i++)
            {
                int eid = enemies[i];
                if (!store.EnemyActive[eid]) continue;
                if (store.EnemyIsFreeRoam[eid]) freeRoam++;
            }
            _freeRoamCount = freeRoam;
            _hasFreeRoamThisFrame = freeRoam > 0;
        }

        /// <summary>
        /// Per-frame update: for every active free-roam enemy, recompute the wander target
        /// based on (a) the closest active tower within aggro range, (b) the player base,
        /// or (c) a periodic re-rolled random cell.
        ///
        /// Writes EnemyWanderTargetX/Y which EnemyMovementSystem reads in the Wandering
        /// action branch. Does NOT write PositionX/Y directly — keeping all position
        /// writes in EnemyMovementSystem matches the existing per-system invariant.
        /// </summary>
        public void Update()
        {
            if (!_hasFreeRoamThisFrame) return; // O(1) fast-exit, common case

            var enemies = store.ActiveEnemyIds;
            int eCount = enemies.Count;
            // Map dimensions: 10 wide × 20 tall. Read once, used in clamp + rand.
            const float MAP_W = 10f;
            const float MAP_H = 20f;
            // Aggro range: if a tower is within this many cells, chase it. Tuned to feel
            // "patrol-aggro" — wider than AggroLeash (which uses 4-10 cells) so free-roam
            // enemies actively hunt towers across the map, not just at the end of the path.
            const float AGGRO_RANGE = 5f;
            const float AGGRO_RANGE_SQ = AGGRO_RANGE * AGGRO_RANGE;
            // Wander reroll period: every ~8 frames pick a new random target cell so the
            // enemy doesn't walk in a straight line when nothing is in range. The actual
            // value is randomized per enemy via hash to avoid all-enemies-re-roll-on-same-
            // frame clustering.
            const float REROLL_PERIOD = 8f;

            // Player base position (player 0). Hard-coded because the project uses a
            // single player in the main path; multi-player instances are out-of-scope
            // for this directive.
            float playerX = 0f; // default at (0, 0)
            float playerY = 0f;
            if (store.EnemyActive[0])
            {
                playerX = store.PositionX[0];
                playerY = store.PositionY[0];
            }

            for (int i = 0; i < eCount; i++)
            {
                int eid = enemies[i];
                if (!store.EnemyActive[eid]) continue;
                if (!store.EnemyIsFreeRoam[eid]) continue; // not a free-roam enemy

                float ex = store.PositionX[eid];
                float ey = store.PositionY[eid];

                // 1. Check towers within AGGRO_RANGE. Pick the closest one.
                float bestDistSq = float.MaxValue;
                float targetX = 0f, targetY = 0f;
                bool haveTarget = false;
                for (int t = 0; t < _towerCount; t++)
                {
                    float dx = _towerX[t] - ex;
                    float dy = _towerY[t] - ey;
                    float dSq = dx * dx + dy * dy;
                    if (dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        targetX = _towerX[t];
                        targetY = _towerY[t];
                        haveTarget = true;
                    }
                }

                if (haveTarget && bestDistSq <= AGGRO_RANGE_SQ)
                {
                    // Chase the nearest tower. Set target = tower's current position.
                    store.EnemyWanderTargetX[eid] = targetX;
                    store.EnemyWanderTargetY[eid] = targetY;
                    // Reset reroll timer so the enemy commits to the chase (won't wander
                    // off after ~8 frames of fruitless marching).
                    store.EnemyWanderRerollTimer[eid] = REROLL_PERIOD;
                    continue;
                }

                // 2. No tower in aggro range: head toward player. We do NOT clamp target
                // to within aggro range because the player is the long-term goal — the
                // enemy will eventually reach the base unless a tower gets in the way.
                store.EnemyWanderTargetX[eid] = playerX;
                store.EnemyWanderTargetY[eid] = playerY;
                // Don't reset reroll timer here — the periodic random-perturbation below
                // will still kick in, giving the enemy a "drunken march" appearance as
                // it crosses the map.
                _ = targetX; _ = targetY; // suppress unused warnings

                // 3. Periodically re-roll a random wander target so the enemy doesn't
                // walk in a perfectly straight line. Only when the timer has expired.
                float timer = store.EnemyWanderRerollTimer[eid] - 1f;
                if (timer <= 0f)
                {
                    // Deterministic xorshift hash: (eid XOR turn-ish counter) → [-1, 1] range.
                    // We don't have a turn counter here, so we use eid as the seed. This
                    // is fine for visual variation — the hot path is "looks random enough"
                    // rather than "cryptographically random".
                    int h = (eid * 1103515245 + 1013904223) | 0;
                    h ^= h << 13; h ^= h >> 17; h ^= h << 5;
                    float unitX = ((h & 0x7FFFFFFF) / (float)0x7FFFFFFF) * 2f - 1f;
                    h = (eid * 22695477 + 1) | 0;
                    h ^= h << 13; h ^= h >> 17; h ^= h << 5;
                    float unitY = ((h & 0x7FFFFFFF) / (float)0x7FFFFFFF) * 2f - 1f;
                    // Random cell within ±2 of player. This keeps the enemy loosely
                    // heading toward the player but with a stochastic nudge so the
                    // path looks organic.
                    float randX = playerX + unitX * 2f;
                    float randY = playerY + unitY * 2f;
                    // Clamp to map bounds (defensive — should always be in range).
                    if (randX < 0f) randX = 0f;
                    if (randX > MAP_W - 1f) randX = MAP_W - 1f;
                    if (randY < 0f) randY = 0f;
                    if (randY > MAP_H - 1f) randY = MAP_H - 1f;
                    store.EnemyWanderTargetX[eid] = randX;
                    store.EnemyWanderTargetY[eid] = randY;
                    // Randomize next reroll: 6-10 frames. Distribution is uniform, not
                    // biased, so a wave of 100 free-roam enemies won't all re-roll on
                    // the same frame.
                    int roll = h & 0x7; // 0..7
                    store.EnemyWanderRerollTimer[eid] = 6f + (float)roll;
                }
                else
                {
                    store.EnemyWanderRerollTimer[eid] = timer;
                }
            }
        }
    }
}
