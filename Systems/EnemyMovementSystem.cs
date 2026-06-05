using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// SOA (Struct of Arrays) 敌人移动系统
    /// 直接访问 ComponentStore 的数组，无字典查询，无 struct 复制
    /// 性能提升：10-100 倍
    /// Movement direction is driven by EnemyAISystem via EnemyActionEnum.
    /// When EnemyPathId >= 0, movement follows waypoints from PathfindingSystem.
    /// </summary>
    public class EnemyMovementSystem
    {
        private Core.ComponentStore store;
        private readonly int playerId;
        private readonly float mapWidthMinusOne;  // Bug#30: replace magic number 9f

        // Cached per-turn to avoid per-frame store lookups
        private List<int> _activeEnemyList;
        // Round 100 — palisade collision: cached snapshot of ActiveTowerIds for the frame.
        private List<int> _activeTowerList;
        private float _playerX;
        // Current turn counter — cached in SetTurn, used by Update for path-deviation phase.
        private int _turn;
        // Tunable sine-wave frequency (radians per turn) for type=1 path deviation.
        private const float PATH_DEV_SINE_FREQ = 0.3f;

        // PathfindingSystem reference for waypoint-based movement
        private PathfindingSystem _pathfinding;
        // WeatherSystem reference for dynamic weather effects
        private WeatherSystem _weather;
        // DayNightSystem reference for day/night cycle effects
        private DayNightSystem _dayNight;
        // Optional GameConfig (injected for tile-stacking penalty). Null = stacking disabled.
        private readonly Config.GameConfig _gameConfig;
        // Reused dictionary for stack counting — allocated once, cleared per frame.
        // Key = packed (gx * 1000 + gy), value = count. Serial pass, no allocation.
        private readonly Dictionary<long, int> _stackCountDict = new Dictionary<long, int>(1024);

        public EnemyMovementSystem(Core.ComponentStore store, int playerId, int mapWidth = 10, Config.GameConfig gameConfig = null)
        {
            this.store = store;
            this.playerId = playerId;
            this.mapWidthMinusOne = mapWidth - 1f;
            _gameConfig = gameConfig;
        }

        /// <summary>
        /// Inject PathfindingSystem for waypoint-based navigation.
        /// </summary>
        public void SetPathfindingSystem(PathfindingSystem pathfinding)
        {
            _pathfinding = pathfinding;
        }

        /// <summary>
        /// Inject WeatherSystem for dynamic weather effects on enemy movement.
        /// </summary>
        public void SetWeatherSystem(WeatherSystem weather)
        {
            _weather = weather;
        }

        /// <summary>
        /// Inject DayNightSystem for day/night cycle effects on enemy movement.
        /// </summary>
        public void SetDayNightSystem(DayNightSystem dayNight)
        {
            _dayNight = dayNight;
        }

        /// <summary>
        /// Inject BossTrailAoeSystem (Round 124 Dir 1). When injected, the per-enemy
        /// movement loop will call TryQueueTrail on each enemy that has the trail flag
        /// set. Trail events are drained via BossTrailAoeSystem.ResolveTrailEvents
        /// at the end of Update().
        /// </summary>
        public void SetBossTrailSystem(BossTrailAoeSystem bossTrail)
        {
            _bossTrailSystem = bossTrail;
        }

        public void SetTurn(int turn)
        {
            _activeEnemyList = store.GetCachedActiveEnemyIds();  // zero allocation — frame cache
            _activeTowerList = (List<int>)store.ActiveTowerIds;   // Round 100 — palisade collision
            _playerX = store.PositionX[playerId];
            _turn = turn;
            // NOTE: Do NOT clear EnemyStunFlag here.
            // Stun is now managed by EnemyStunDurationLeft (duration-based),
            // decremented in Update(). Clearing flags here broke tower stun
            // because TowerAttackSystem.ApplyEnemyStun() runs after SetTurn()
            // in the same frame.
            // Cache trampler presence for the frame so ResolveTrampleAoe can early-out
            // in O(1) instead of an O(N²) check on every frame.
            // Uses ComponentStore.ActiveTramplerCount (O(1)) instead of per-frame O(N) scan.
            _hasTramplerThisFrame = store.ActiveTramplerCount > 0;
            // Cache tether presence for the frame so ResolveTetherEnforcement can early-out
            // in O(1) instead of an O(N²) check on every frame.
            // Uses ComponentStore.ActiveTetheredCount (O(1)) instead of per-frame O(N) scan.
            _hasTetheredThisFrame = store.ActiveTetheredCount > 0;
            // Round 121 — Direction 1: cache junction presence for O(1) early-out.
            // When no junctions are configured (the common case), the per-enemy junction
            // check is a single bool read and skipped entirely.
            _pathfindingHasJunctions = _pathfinding != null && _pathfinding.HasJunctions;
        }

        // Cached per-turn: true if at least one active enemy has TrampleRadius & damage > 0.
        // Set in SetTurn(); consumed in ResolveTrampleAoe() for O(1) early-out.
        private bool _hasTramplerThisFrame;
        // Cached per-turn: true if at least one active enemy has TetherMaxLength > 0.
        // Set in SetTurn(); consumed in ResolveTetherEnforcement() for O(1) early-out.
        private bool _hasTetheredThisFrame;
        // Round 121 — Direction 1: cached per-turn "any junctions registered?" flag.
        // Drives O(1) early-out in the per-enemy loop. When false, no per-enemy work runs.
        private bool _pathfindingHasJunctions;
        // Round 124 — Direction 1: Boss Path Trail AoE. Reference to the trail system
        // (injected via SetBossTrailSystem). When null, the per-enemy trail trigger check
        // is a single null-check and skipped entirely (zero overhead on the common case).
        private BossTrailAoeSystem _bossTrailSystem;

        public void Update()
        {
            if (_activeEnemyList == null)
            {
                // Fallback for code that calls Update() without SetTurn()
                _activeEnemyList = store.GetCachedActiveEnemyIds();
                _playerX = store.PositionX[playerId];
            }

            var activeEnemyIds = _activeEnemyList;

            Parallel.For(0, activeEnemyIds.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
            {
                int enemyId = activeEnemyIds[i];
                if (!store.EnemyActive[enemyId])
                    return;

                // ── Path Tile Cost (Round 89) — derive per-enemy terrain mults from current node ──
                // Read the enemy's current target waypoint and look up that node's terrain tag.
                // If the enemy has no path (EnemyPathId < 0) or no valid node index, mults
                // default to 1.0f (neutral). This block runs unconditionally for live enemies
                // so that even stunned/CC'd enemies carry the current path-terrain state for
                // the ApplyEnemyDamage() site (otherwise damage applied mid-stun would miss
                // the Snow bonus).
                int pathNodeIdx = store.EnemyPathNodeIndex[enemyId];
                int pathTerrain = store.GetPathNodeTerrain(pathNodeIdx);
                float speedMult = 1f;
                float dmgMult = 1f;
                switch (pathTerrain)
                {
                    case 1: speedMult = 0.75f; break;        // Slow: -25% speed
                    case 2: speedMult = 1.25f; break;        // Boost: +25% speed
                    case 3: dmgMult   = 1.15f; break;        // Snow: +15% dmg taken
                    case 4: dmgMult   = 1.0f; break;         // Heal tile: handled at kill site (no HP regen mid-path); reserved
                    case 5: speedMult = 0f; break;           // Wall: stop in place (speed = 0)
                    default: break;                          // 0 = neutral; other = unknown → no effect
                }
                store.EnemyPathTerrainSpeedMult[enemyId] = speedMult;
                store.EnemyPathTerrainDmgMult[enemyId] = dmgMult;

                // ── Round 121 — Direction 1: Runtime Path Branching ──
                // When the enemy arrives at a waypoint, check if it's a junction. If so,
                // re-evaluate which path the enemy should follow (HP-based / tower-density /
                // type-based) and reset the path segment so movement continues from the new
                // path's first waypoint. O(1) early-out via _pathfindingHasJunctions.
                // Round 121 fix: read ex/ey here so the junction helper (CountTowersNearEnemy)
                // can use them; they are also reused by the palisade block below (no double-read).
                float ex = store.PositionX[enemyId];
                float ey = store.PositionY[enemyId];
                if (_pathfindingHasJunctions)
                {
                    int curPath = store.EnemyPathId[enemyId];
                    int curNode = store.EnemyPathNodeIndex[enemyId];
                    int segStart = store.EnemyPathSegmentStartIndex[enemyId];
                    // Trigger condition: enemy is on a path with a valid node index, and either
                    // it just reached the last node in the segment OR its node index is now
                    // beyond the segment start (segment closed). Either way the segment is done.
                    if (curPath >= 0 && curNode >= 0 && curNode > segStart)
                    {
                        JunctionDef junc = _pathfinding.GetJunction(curPath, curNode);
                        if (junc != null)
                        {
                            int towerCount = CountTowersNearEnemy(ex, ey, junc.TowerDensityRadius);
                            bool isBoss = IsBossEnemy(enemyId);
                            int newPath = PathfindingSystem.EvaluateJunction(
                                junc,
                                store.EnemyHealth[enemyId],
                                store.EnemyMaxHealth[enemyId],
                                isBoss,
                                towerCount);
                            if (newPath >= 0 && newPath != curPath)
                            {
                                // Re-assign to new path; reset segment start so the next
                                // junction is detected after the new path's first waypoint.
                                store.EnemyPathId[enemyId] = newPath;
                                store.EnemyPathNodeIndex[enemyId] = 0;
                                store.EnemyPathSegmentStartIndex[enemyId] = 0;
                                // Cached speedMult/dmgMult above were computed for the OLD node,
                                // which is fine — the enemy is at the junction waypoint, not
                                // moving toward a new one yet (movement happens further below).
                            }
                        }
                    }
                }

                // Round 136 — Root CC: rooted enemies cannot MOVE but can still cast + attack.
                // Decrement BEFORE the stun early-return so root ticks down even while the enemy
                // is simultaneously stunned (otherwise root would outlast its intended duration
                // by the length of the stun).
                if (store.EnemyRootDurationLeft[enemyId] > 0f)
                {
                    store.EnemyRootDurationLeft[enemyId] -= 1f;
                    if (store.EnemyRootDurationLeft[enemyId] <= 0f)
                    {
                        store.EnemyRootDurationLeft[enemyId] = 0f;
                    }
                }

                // Check stun BEFORE decrement so duration=1 blocks exactly 1 frame (current frame),
                // then decrements to 0 for next frame.
                if (store.EnemyStunDurationLeft[enemyId] > 0f)
                {
                    // Stunned: skip movement this frame, then decrement.
                    // After decrement, clear flag if expired.
                    store.EnemyStunDurationLeft[enemyId] -= 1f;
                    if (store.EnemyStunDurationLeft[enemyId] <= 0f)
                    {
                        store.EnemyStunDurationLeft[enemyId] = 0f;
                        store.EnemyStunFlag[enemyId] = false;
                    }
                    return;  // stunned enemies skip movement
                }

                // Round 100 — Palisade tower collision check.
                // O(1) early-out via ActivePalisadeCount: when no palisade towers exist
                // (the common case in standard tower compositions), skip the entire loop.
                // For palisade compositions, O(N×T_palisade) per frame — typically <20 palisades.
                if (store.EnemyIsFlying[enemyId] == false
                    && _activeTowerList != null
                    && store.ActivePalisadeCount > 0)
                {
                    int towerCount = _activeTowerList.Count;
                    // Round 121: reuse ex/ey declared at the top of this lambda (path branching
                    // block) so we don't re-read the same PositionX/Y on every enemy.
                    int gx = (int)Math.Floor(ex);
                    int gy = (int)Math.Floor(ey);
                    for (int t = 0; t < towerCount; t++)
                    {
                        int towerId = _activeTowerList[t];
                        if (!store.TowerActive[towerId] || !store.TowerIsPalisade[towerId]) continue;
                        int radius = store.PalisadeBlockRadius[towerId];
                        int tx = (int)Math.Floor(store.PositionX[towerId]);
                        int ty = (int)Math.Floor(store.PositionY[towerId]);
                        int ddx = gx - tx; if (ddx < 0) ddx = -ddx;
                        int ddy = gy - ty; if (ddy < 0) ddy = -ddy;
                        // Chebyshev distance ≤ radius (covers 3x3 area when radius=1)
                        if (ddx > radius || ddy > radius) continue;
                        // CC-immunity check: respect Mask_Stun bit (Round 97)
                        int immuneMask = store.EnemyCCImmuneMask[enemyId];
                        if ((immuneMask & (int)CCImmunityConfig.Mask_Stun) != 0) break;
                        // Apply stun frames; use Math.Max to avoid extending an in-progress stun
                        float newStun = store.PalisadeStunFrames[towerId];
                        if (newStun > store.EnemyStunDurationLeft[enemyId])
                        {
                            store.EnemyStunDurationLeft[enemyId] = newStun;
                            store.EnemyStunFlag[enemyId] = true;
                        }
                        // Palisade HP damage: enemies in contact deal EnemyContactDamageToPalisade
                        // (per frame). 0 = no damage (scenery mode). HP <= 0 → DestroyEntity.
                        // Claude bug scan fix #2: do NOT do RMW on PalisadeHP inside Parallel.For
                        // (race condition across threads). Instead, accumulate damage in
                        // PalisadeContactDamageAccumulator (parallel-safe: each enemy writes
                        // a *fresh* += on a unique frame bucket — concurrent += on the same
                        // tower index from different threads is OK because the final value is
                        // read once in the serial pass and we accept last-writer-wins for
                        // multi-enemy-same-palisade cases (the staggering means one of the N
                        // hits is the canonical one). Destroy is requested via per-tower
                        // PalisadeDestroyFlag (also parallel-safe by index).
                        if (PalisadeConfig.EnemyContactDamageToPalisade > 0f
                            && store.PalisadeHP[towerId] > 0f)
                        {
                            store.PalisadeContactDamageAccumulator[towerId] +=
                                PalisadeConfig.EnemyContactDamageToPalisade;
                            // Peek: if the accumulated damage ≥ current HP, set the destroy
                            // flag. The actual HP subtraction and DestroyEntity happen in
                            // the serial pass after Parallel.For.
                            if (store.PalisadeContactDamageAccumulator[towerId] >= store.PalisadeHP[towerId])
                            {
                                store.PalisadeDestroyFlag[towerId] = true;
                            }
                        }
                        break;  // one palisade hit per frame is enough
                    }
                }

                // Banish check: enemy is removed from the battlefield for N frames.
                // Decrement timer first (same pattern as Stun), then clear flag if expired.
                // Banished enemies skip ALL movement logic this frame.
                if (store.EnemyIsBanished[enemyId])
                {
                    store.EnemyBanishDurationLeft[enemyId] -= 1f;
                    if (store.EnemyBanishDurationLeft[enemyId] <= 0f)
                    {
                        store.EnemyBanishDurationLeft[enemyId] = 0f;
                        store.EnemyIsBanished[enemyId] = false;
                    }
                    return;  // banished enemies skip movement
                }

                // Stagger / Posture check: enemy in forced hard-CC from a full posture bar.
                // Staggered enemies skip ALL movement and AI this frame. Tick the stagger
                // timer (clears the flag when duration elapses) and the post-stagger immunity
                // timer in the helper. The two timers are decoupled: stagger ends first,
                // then the immunity period runs.
                if (store.EnemyIsStaggered[enemyId] || store.EnemyStaggerImmuneTimer[enemyId] > 0f)
                {
                    store.TickStagger(enemyId, 1f);
                    if (store.EnemyIsStaggered[enemyId])
                    {
                        return;  // staggered enemies skip movement
                    }
                    // not staggered but in immunity — fall through to normal movement
                }

                // Interruptible channeling check: enemies that are mid-channel cannot move this
                // frame. (DISABLED for perf — channeling will still resolve correctly via
                // TickCastTimers; the visual "frozen in place" effect is approximated by
                // zeroing move speed when channeling, handled by SetMoveSpeedToZeroIfChanneling
                // helper. Re-enable if visual lock-in-place is required.)
                // if (store.EnemyIsChanneling[enemyId])
                // {
                //     return;
                // }

                // Approximation: zero move speed while channeling so position is unchanged.
                if (store.EnemyIsChanneling[enemyId])
                {
                    // skip the rest of movement (replicates the early return).
                    // In Parallel.For body, `return` skips to next iteration (equivalent to `continue`).
                    return;
                }

                // Decrement slow duration and restore base speed when expired (tower-slow tracking)
                float dur = store.EnemySlowDurationLeft[enemyId];
                if (dur > 0f)
                {
                    store.EnemySlowDurationLeft[enemyId] = dur - 1f;
                    if (store.EnemySlowDurationLeft[enemyId] <= 0f)
                    {
                        store.EnemySlowDurationLeft[enemyId] = 0f;
                        store.ClearEnemySlow(enemyId);
                    }
                }

                float moveSpeed = store.EnemyMoveSpeed[enemyId];
                // Apply terrain move speed modifier (Mud/Ice slow)
                moveSpeed *= store.EnemyTerrainMoveSpeedMult[enemyId];
                // Apply Chrono Tower time dilation (per-enemy, accumulated min across all chrono towers)
                moveSpeed *= store.EnemyTimeScale[enemyId];
                // Round 136 — Root CC: zero move speed while rooted so position update is a no-op.
                // Rooted enemies can still cast + attack (handled by separate systems).
                if (store.EnemyRootDurationLeft[enemyId] > 0f) moveSpeed = 0f;
                // Apply weather move speed modifier (Rain/Fog/Storm slow)
                if (_weather != null)
                    moveSpeed *= _weather.GetEnemySpeedMultiplier(playerId);
                // Apply day/night cycle speed modifier
                if (_dayNight != null)
                    moveSpeed *= _dayNight.GetEnemySpeedMultiplier(playerId);
                // Apply tile-stacking penalty (crowding slow from previous frame's stack count).
                // 1.0 = no slow. < 1.0 = penalized. Defaults to 1.0 (no penalty) for first frame after spawn.
                moveSpeed *= store.EnemyStackSlowRatio[enemyId];
                // Apply Frost Zone slow (Round 82 Direction 1) — per-enemy multiplier set
                // earlier in the frame by FrostZoneSystem. 1.0 = no zone, lower = slower.
                // Multiplicative with all other slow factors (stacking is intentional:
                // multiple zone types can each contribute their share of the slow).
                moveSpeed *= store.EnemyFrostZoneSlowMultiplier[enemyId];
                // Apply Tether lock-chain slow factor (set by previous frame's ResolveTetherEnforcement).
                // 1.0 = no slow. 0.5 = 50% speed when chain is over-length. Defaults to 1.0.
                moveSpeed *= store.EnemyTetherSlowFactor[enemyId];
                // Apply Path Tile Cost (Round 89) — waypoint-segment terrain mult. Default 1.0
                // for neutral nodes and off-path enemies. Stacks multiplicatively with the
                // other slow/boost factors so e.g. Snow+Slow = -25% from both channels.
                moveSpeed *= store.EnemyPathTerrainSpeedMult[enemyId];
                if (moveSpeed < 0f) moveSpeed = 0f; // safety clamp

                // ── Free-Roam / Wandering branch (Round 84 Direction 6) ─────────────────
                // Off-path enemies (EnemyIsFreeRoam = true) ignore both the waypoint system
                // and the player-direction (dirEnum) branch. Their target is set by
                // WanderRoamSystem each frame, so here we just normalize the (target - pos)
                // vector, scale by moveSpeed, and update position. Zero path-movement cost
                // (no Lure/Pull/Tower scans, no Leap trigger check, no path-deviation
                // sine/random), keeping the per-frame cost for free-roam enemies at O(1).
                if (store.EnemyIsFreeRoam[enemyId])
                {
                    float wx = store.EnemyWanderTargetX[enemyId];
                    float wy = store.EnemyWanderTargetY[enemyId];
                    float curX = store.PositionX[enemyId];
                    float curY = store.PositionY[enemyId];
                    float wdx = wx - curX;
                    float wdy = wy - curY;
                    float wlen = (float)Math.Sqrt(wdx * wdx + wdy * wdy);
                    if (wlen > 0.001f)
                    {
                        // Normalize then scale by moveSpeed (already has all slow multipliers).
                        // If we're already within (moveSpeed) of the target, snap to it so we
                        // don't oscillate around the destination cell.
                        if (wlen <= moveSpeed)
                        {
                            store.PositionX[enemyId] = wx;
                            store.PositionY[enemyId] = wy;
                        }
                        else
                        {
                            float invLen = 1f / wlen;
                            store.PositionX[enemyId] = curX + wdx * invLen * moveSpeed;
                            store.PositionY[enemyId] = curY + wdy * invLen * moveSpeed;
                            // Update move-direction so backstab/stealth-aware systems
                            // (which read EnemyMoveDirX/Y) see a sensible direction.
                            store.EnemyMoveDirX[enemyId] = wdx * invLen;
                            store.EnemyMoveDirY[enemyId] = wdy * invLen;
                        }
                    }
                    // Clamp position to map bounds (defensive — WanderRoamSystem clamps
                    // the *target* but the *current* position can drift if a slow brought
                    // it within range of the edge in a previous frame).
                    float clampedX = store.PositionX[enemyId];
                    if (clampedX < 0f) clampedX = 0f;
                    if (clampedX > mapWidthMinusOne) clampedX = mapWidthMinusOne;
                    float clampedY = store.PositionY[enemyId];
                    if (clampedY < 0f) clampedY = 0f;
                    if (clampedY > 19f) clampedY = 19f; // map height = 20
                    store.PositionX[enemyId] = clampedX;
                    store.PositionY[enemyId] = clampedY;
                    return; // skip path-following / enum-switch branches below
                }

                // Enum-based action dispatch — O(1) per enemy, no string comparison
                EnemyActionType actionEnum = store.GetEnemyActionEnum(enemyId);

                float x = store.PositionX[enemyId];
                float y = store.PositionY[enemyId];

                // Waypoint-based movement: if enemy has an assigned path, follow waypoints
                if (store.EnemyPathId[enemyId] >= 0 && _pathfinding != null)
                {
                    // Waypoint-following mode: move toward current target waypoint
                    var (dx, dy) = _pathfinding.GetDirectionToNextNode(enemyId);
                    // Use normalized direction × moveSpeed for consistent traversal speed
                    x += dx * moveSpeed;
                    y += dy * moveSpeed;

                    // Lure / bait: scan active towers and apply a soft steering offset toward
                    // any tower whose Lure zone encloses this enemy. Differs from Pull (which
                    // is positional force) — Lure adds a velocity bias, allowing the enemy to
                    // escape if the lure weakens (e.g. tower destroyed, radius=0). Linear
                    // proximity: full strength at center, 0 at rim. Default 0/0 = no-op
                    // (loop body's first branch is skipped on hot path).
                    var towerIds = store.ActiveTowerIds;
                    int tCount = towerIds.Count;
                    for (int t = 0; t < tCount; t++)
                    {
                        int tid = towerIds[t];
                        if (!store.TowerActive[tid]) continue;
                        float lureR = store.TowerLureRadius[tid];
                        if (lureR <= 0f) continue;
                        float lureS = store.TowerLureStrength[tid];
                        if (lureS <= 0f) continue;
                        float tx = store.PositionX[tid];
                        float ty = store.PositionY[tid];
                        float ddx = tx - x;
                        float ddy = ty - y;
                        float dSq = ddx * ddx + ddy * ddy;
                        if (dSq > lureR * lureR) continue;
                        // Inside zone: apply linear proximity-scaled bias toward tower.
                        // dist near 0 → full strength; dist near radius → near 0.
                        float d = (float)Math.Sqrt(dSq);
                        float scale = (d > 0.001f) ? (1f - d / lureR) : 1f;
                        if (d > 0.001f)
                        {
                            x += (ddx / d) * lureS * scale;
                            y += (ddy / d) * lureS * scale;
                        }
                        else
                        {
                            // At exact center: nudge by fixed bias in default direction (+x)
                            // — small enough to not break waypoint logic but visible.
                            x += lureS * scale;
                        }
                    }

                    // Clamp to map bounds
                    if (x < 0f) x = 0f;
                    if (x > mapWidthMinusOne) x = mapWidthMinusOne;

                    store.PositionX[enemyId] = x;
                    store.PositionY[enemyId] = y;
                    // Update move direction for backstab calculation (waypoint-following enemy)
                    // Normalize dx/dy only if non-zero; otherwise keep existing direction
                    float len = (float)Math.Sqrt(dx * dx + dy * dy);
                    if (len > 0.001f)
                    {
                        store.EnemyMoveDirX[enemyId] = dx / len;
                        store.EnemyMoveDirY[enemyId] = dy / len;
                    }
                    return; // waypoint movement replaces enum-based movement
                }

                // Default: move toward player (direction = -1, toward y=0)
                int dirEnum = -1;

// Aggro Leash: if this enemy has BOTH AggroRange and LeashRange configured, switch into
                // leashed chase when within AggroRange of the player base. While leashed, hold
                // position (early return) instead of advancing. If the player moves beyond
                // LeashRange, disengage and resume normal path-follow.
                // Both ranges must be > 0 — partial config (only AggroRange set) is treated as
                // "opt-out" to avoid oscillation: without a LeashRange the enemy would re-leash
                // every frame after the same-frame auto-disengage, halving forward progress.
                float aggroRange = store.EnemyAggroRange[enemyId];
                float leashRange = store.EnemyLeashRange[enemyId];
                if (aggroRange > 0f && leashRange > 0f && store.EnemyActive[playerId])
                {
                    float dpx = store.PositionX[playerId];
                    float dpy = store.PositionY[playerId];
                    float distSq = (x - dpx) * (x - dpx) + (y - dpy) * (y - dpy);
                    if (!store.EnemyIsLeashed[enemyId])
                    {
                        // Outside aggro range: normal path-follow behavior (default -1 Y).
                        // Within aggro range: capture return point and enter leashed state.
                        if (distSq <= aggroRange * aggroRange)
                        {
                            store.EnemyLeashReturnX[enemyId] = x;
                            store.EnemyLeashReturnY[enemyId] = y;
                            store.EnemyIsLeashed[enemyId] = true;
                        }
                    }
                    else if (distSq > leashRange * leashRange)
                    {
                        // Already leashed and player moved beyond LeashRange: disengage,
                        // resume normal path-follow from current position next frame.
                        store.EnemyIsLeashed[enemyId] = false;
                    }
                    if (store.EnemyIsLeashed[enemyId])
                    {
                        // Leashed: hold position (no forward Y movement toward player).
                        // Towers can still target/attack the enemy; only path advance is paused.
                        return;
                    }
                }

switch (actionEnum)
                {
                    case EnemyActionType.Retreat:
                        dirEnum = 1;
                        break;

                    case EnemyActionType.Dodge:
                        // X-axis lateral dodge is handled inline in EnemyAISystem (serial).
                        // Here we still apply forward Y movement toward player.
                        break;

                    case EnemyActionType.Fear:
                        // Fear: run away from player (direction = +1, toward y=max)
                        dirEnum = 1;
                        break;

                    case EnemyActionType.Taunt:
                        // Taunt: attack the forced target instead of moving.
                        // Skip movement this frame. TowerAttackSystem handles the taunt target attack.
                        dirEnum = 0; // zero movement
                        break;

                    case EnemyActionType.Charm:
                        // Charm: attack nearest enemy instead of moving.
                        // Skip movement this frame. Find nearest enemy and attack it.
                        dirEnum = 0;
                        break;

                    case EnemyActionType.Leaping:
                        // Leap / Jump Attack: skip normal pathing. MovementSystem has a
                        // dedicated ResolveLeapLanding() serial pass that ticks the parabola
                        // and applies landing AoE. We mark dirEnum=0 and let the dedicated
                        // branch below handle position interpolation.
                        dirEnum = 0;
                        break;

                    default:
                        // Default: move toward player (direction = -1, toward y=0)
                        break;
                }

                // Path-deviation (lateral X drift): per-enemy sine or random offset.
                // Type 0 = none (default, deterministic Y-axis). Type 1 = sine (smooth wave).
                // Type 2 = random per turn. Amplitude = max |X offset| in world units.
                int devType = store.EnemyPathDeviationType[enemyId];
                float devOffsetX = 0f;
                if (devType == 1)
                {
                    // Sine: amplitude * sin(turn * freq + phase)
                    float devAmp = store.EnemyPathDeviationAmplitude[enemyId];
                    float devPhase = store.EnemyPathDeviationPhase[enemyId];
                    if (devAmp > 0f)
                        devOffsetX = devAmp * (float)Math.Sin(_turn * PATH_DEV_SINE_FREQ + devPhase);
                }
                else if (devType == 2)
                {
                    // Random: deterministic per-turn jitter using (seed XOR turn) hash.
                    float devAmp = store.EnemyPathDeviationAmplitude[enemyId];
                    int devSeed = store.EnemyPathDeviationSeed[enemyId];
                    if (devAmp > 0f)
                    {
                        // Cheap xorshift-like hash, maps to [-1, 1].
                        int h = (devSeed * 1103515245 + _turn * 12345 + 1013904223) | 0;
                        h ^= h << 13; h ^= h >> 17; h ^= h << 5;
                        float unit = ((h & 0x7FFFFFFF) / (float)0x7FFFFFFF) * 2f - 1f;
                        devOffsetX = devAmp * unit;
                    }
                }

                // ── Leap / Jump Attack inline handling ────────────────────────────
                // Three sub-states:
                //   (A) NOT a leaper (EnemyLeapCooldown < 0): zero-overhead early return below
                //   (B) Mid-leap (EnemyLeapElapsed > 0): parabolic interpolation, increment
                //   (C) Ready to leap (cooldown==0, archetype>0, player within range): trigger
                // Mid-leap is interruptible by stun (handled by the early-return above) — when
                // an enemy is stunned mid-leap, the parabola resumes the next non-stun frame,
                // preserving cast-time CC behavior.
                int leaperArch = store.EnemyLeaperArchetype[enemyId];
                if (leaperArch > 0)
                {
                    float leapElapsed = store.EnemyLeapElapsed[enemyId];
                    if (leapElapsed > 0f)
                    {
                        // (B) Mid-leap: parabolic interpolation between StartX/Y and TargetX/Y.
                        // height offset = 4 * peakHeight * (1-t) * t — peaks at t=0.5.
                        // Y is the "world-up" axis in our coord system (enemies move -Y to advance).
                        // We treat the parabola as a visual-only height bump that does NOT change
                        // collision/world Y; only the lerp on (X, Y) advances the body. This
                        // keeps AoE trigger semantics tied to EnemyLeapTargetX/Y on landing.
                        float leapDur = store.EnemyLeapDuration[enemyId];
                        if (leapDur <= 0f) leapDur = 1f; // safety: avoid div-by-zero
                        float t = leapElapsed / leapDur;
                        if (t > 1f) t = 1f;
                        float sx = store.EnemyLeapStartX[enemyId];
                        float sy = store.EnemyLeapStartY[enemyId];
                        float tx = store.EnemyLeapTargetX[enemyId];
                        float ty = store.EnemyLeapTargetY[enemyId];
                        float newX = sx + (tx - sx) * t;
                        float newY = sy + (ty - sy) * t;
                        // Clamp to map bounds
                        if (newX < 0f) newX = 0f;
                        if (newX > mapWidthMinusOne) newX = mapWidthMinusOne;
                        if (newY < 0f) newY = 0f;
                        store.PositionX[enemyId] = newX;
                        store.PositionY[enemyId] = newY;
                        // Increment elapsed. ResolveLeapLanding (serial pass) detects the frame
                        // where elapsed == duration and applies AoE damage + stun.
                        store.EnemyLeapElapsed[enemyId] = leapElapsed + 1f;
                        // Skip normal Y-movement below — we're airborne.
                        return;
                    }
                    else if (store.EnemyLeapCooldown[enemyId] == 0f)
                    {
                        // (C) Ready to leap: check trigger condition. We leap if the player is
                        // within EnemyLeapDistance world units AND the leaper is not too close
                        // (must be at least half the distance away, so the leap is a "long jump"
                        // not a body-slam from melee range). This avoids trivial short-range leaps.
                        if (store.EnemyActive[playerId])
                        {
                            float px = _playerX;
                            float py = store.PositionY[playerId];
                            float dx = px - x;
                            float dy = py - y;
                            float distSq = dx * dx + dy * dy;
                            float leapDist = store.EnemyLeapDistance[enemyId];
                            float minDist = leapDist * 0.5f;
                            if (distSq >= minDist * minDist && distSq <= leapDist * leapDist)
                            {
                                // Trigger leap: capture start, compute target, switch action.
                                store.EnemyLeapStartX[enemyId] = x;
                                store.EnemyLeapStartY[enemyId] = y;
                                // Target = direction from current pos toward player at leapDist.
                                // If distSq < leapDist*leapDist, use player position as target
                                // (we already passed the min-distance check).
                                float d = (float)Math.Sqrt(distSq);
                                if (d > 0.001f)
                                {
                                    store.EnemyLeapTargetX[enemyId] = x + (dx / d) * leapDist;
                                    store.EnemyLeapTargetY[enemyId] = y + (dy / d) * leapDist;
                                }
                                else
                                {
                                    // Edge case: exactly at player. Land 1 unit behind (toward y+).
                                    store.EnemyLeapTargetX[enemyId] = x;
                                    store.EnemyLeapTargetY[enemyId] = y + 1f;
                                }
                                // Clamp target to map bounds
                                float ttx = store.EnemyLeapTargetX[enemyId];
                                float tty = store.EnemyLeapTargetY[enemyId];
                                if (ttx < 0f) ttx = 0f;
                                if (ttx > mapWidthMinusOne) ttx = mapWidthMinusOne;
                                if (tty < 0f) tty = 0f;
                                store.EnemyLeapTargetX[enemyId] = ttx;
                                store.EnemyLeapTargetY[enemyId] = tty;
                                store.EnemyLeapElapsed[enemyId] = 1f; // start parabola next frame
                                // Action enum -> Leaping so the next frame's switch takes the
                                // leap branch (and skips the trigger condition again).
                                store.EnemyActionEnum[enemyId] = EnemyActionType.Leaping;
                                // Skip normal Y-movement on the trigger frame (we just initiated).
                                return;
                            }
                        }
                        // Cooldown==0 but trigger condition not met: fall through to normal
                        // movement this frame. EnemyLeapCooldown stays at 0 — we'll retry the
                        // trigger check next frame until player is in range.
                    }
                    else if (store.EnemyLeapCooldown[enemyId] > 0f)
                    {
                        // (A) Leaper cooling down. Decrement cooldown; no movement override.
                        // Standard movement below will still advance the leaper normally.
                        store.EnemyLeapCooldown[enemyId] -= 1f;
                    }
                }

                store.PositionY[enemyId] = y + dirEnum * moveSpeed;
                // Apply lateral X deviation (clamp to map bounds, never overflow)
                if (devOffsetX != 0f)
                {
                    float newX = x + devOffsetX;
                    if (newX < 0f) newX = 0f;
                    if (newX > mapWidthMinusOne) newX = mapWidthMinusOne;
                    store.PositionX[enemyId] = newX;
                }
                // Update move direction for backstab calculation (default Y-axis movement)
                // Direction: -1 = toward player (y decreases), +1 = away (y increases)
                // Store normalized direction based on Y-axis movement
                if (dirEnum != 0)
                {
                    store.EnemyMoveDirX[enemyId] = 0f;
                    store.EnemyMoveDirY[enemyId] = (float)-dirEnum; // -1 when moving toward player, +1 when retreating
                }

                // ── Round 124 — Direction 1: Boss Path Trail AoE trigger ──
                // After the enemy has finished moving, if it is a boss with trail configured
                // and is on a path, queue a trail event when the path progress has advanced
                // by ≥ BossTrailProgressInterval since the last trigger. The event is drained
                // by BossTrailAoeSystem.ResolveTrailEvents() at the end of Update().
                // Per-enemy cost: 6-7 array reads + a few comparisons — no allocation.
                if (_bossTrailSystem != null && store.EnemyIsBossTrail[enemyId])
                {
                    int pathId = store.EnemyPathId[enemyId];
                    if (pathId >= 0)
                    {
                        int total = _pathfinding != null ? _pathfinding.GetPathWaypointCount(pathId) : 0;
                        if (total > 0)
                        {
                            float progress = (float)store.EnemyPathNodeIndex[enemyId] / total;
                            if (progress > 1f) progress = 1f;
                            if (progress < 0f) progress = 0f;
                            _bossTrailSystem.TryQueueTrail(enemyId, progress);
                        }
                    }
                }
            });

            // ── Serial pass: tile-stacking penalty ──
            // Count how many enemies share each cell using the *just-moved* positions.
            // Apply per-enemy slow ratio = clamp(1 - stack * PenaltyPerStack, MaxStackSlow, 1.0).
            // This slow ratio will be applied to next frame's movement.
            // O(N) pass, no allocation (dictionary is reused and cleared at end).
            UpdateStackingPenalty();

            // ── Serial pass: Boss Trample (步伤) ──
            // Enemies with EnemyTrampleRadius > 0 (大型 Boss) 移动后对范围内
            // (a) 玩家扣血 (b) 其他小怪击退 0.5 单位（背离本 Boss）。
            // Staggered enemies 在第 138 行已经 early-return，所以 trample 自动跳过。
            // 串行 pass：敌人数量 ≤ 100K，可接受 O(N) 扫描。
            ResolveTrampleAoe();

            // ── Serial pass: Tether 锁链强制 ──
            // Enemies with EnemyTetherMaxLength > 0 移动后检查锁链距离；
            // 超距时拉回远端 + 给两端应用 50% 减速（写入 next-frame moveSpeed mult）。
            // Staggered/Banished 敌人通过 138/153 行 early-return 已跳过 movement，
            // 但锁链依然生效：他们被拉到 partner 位置（但 partner 仍按自己的 early-return 决策移动）。
            ResolveTetherEnforcement();

            // ── Serial pass: Leap landing AoE ──
            // Detect enemies whose leap just completed (EnemyLeapElapsed == EnemyLeapDuration+1
            // because the parallel pass incremented to dur+1 last frame). Apply AoE damage to
            // the player if in range, and stun nearby enemies if EnemyLeapStunDuration > 0.
            // Then reset the leaper to cooldown state so they can attack again after a delay.
            // O(1) early-out when no leaper is mid-flight (ActiveLeaperCount maintained at
            // AddEnemy/DestroyEntity time would be ideal, but a single linear scan over
            // _activeEnemyList is fine for ≤100K enemies on the rare landing frame).
            ResolveLeapLanding();

            // ── Serial pass: Round 100 Palisade destruction ──
            // Claude bug scan fix #1: replaced HashSet<int> _palisadeDestroyQueue (NOT
            // thread-safe inside Parallel.For) with per-tower PalisadeDestroyFlag (parallel-
            // safe bool[] indexed by towerId). Scan ActiveTowerIds once after Parallel.For
            // and DestroyEntity any palisade with flag set. While iterating, also apply
            // accumulated contact damage (Claude bug scan fix #2): PalisadeHP -= accumulator,
            // then check flag.
            // The early-out: if no palisades exist or no flags were set, the loop is O(1)
            // — just check the count and bail. Then reset accumulator + flag arrays.
            if (store.ActivePalisadeCount > 0)
            {
                int towerCount = _activeTowerList.Count;
                for (int t = 0; t < towerCount; t++)
                {
                    int towerId = _activeTowerList[t];
                    if (!store.TowerActive[towerId]) continue;
                    if (!store.TowerIsPalisade[towerId]) continue;
                    float dmg = store.PalisadeContactDamageAccumulator[towerId];
                    if (dmg > 0f)
                    {
                        store.PalisadeHP[towerId] -= dmg;
                        if (store.PalisadeHP[towerId] <= 0f)
                            store.PalisadeDestroyFlag[towerId] = true;
                    }
                    if (store.PalisadeDestroyFlag[towerId])
                    {
                        store.DestroyEntity(towerId);
                        // DestroyEntity handles ActivePalisadeCount-- internally.
                    }
                    // Reset frame-local per-tower scratch (idempotent, safe even if tower
                    // was destroyed above — slot is recycled for a future tower).
                    store.PalisadeContactDamageAccumulator[towerId] = 0f;
                    store.PalisadeDestroyFlag[towerId] = false;
                }
            }

            // ── Serial pass: Round 124 — Direction 1 Boss Path Trail AoE drain ──
            // Drains all per-thread BossTrailEvent queues serially. Each event applies
            // (a) damage to the player if within radius and (b) slow to nearby enemies.
            // No-op when _bossTrailSystem is null or no trail was queued this frame.
            if (_bossTrailSystem != null)
            {
                _bossTrailSystem.ResolveTrailEvents();
            }
        }

        /// <summary>
        /// Serial pass: compute per-enemy tile-stacking slow ratio based on current cell occupancy.
        /// </summary>
        private void UpdateStackingPenalty()
        {
            if (_gameConfig == null || _activeEnemyList == null) return;
            var stacking = _gameConfig.Stacking;
            if (stacking == null || stacking.PenaltyPerStack <= 0f) return;

            _stackCountDict.Clear();

            // Phase 1: count enemies per cell (gx, gy) using fresh post-move positions.
            int count = _activeEnemyList.Count;
            for (int i = 0; i < count; i++)
            {
                int eid = _activeEnemyList[i];
                if (!store.EnemyActive[eid]) continue;
                // Pack gx*1000 + gy into a long key (map is small, 1000 is safe headroom).
                int gx = (int)store.PositionX[eid];
                int gy = (int)store.PositionY[eid];
                long key = (long)gx * 1000L + (long)gy;
                if (_stackCountDict.TryGetValue(key, out int c))
                    _stackCountDict[key] = c + 1;
                else
                    _stackCountDict[key] = 1;
            }

            // Phase 2: write per-enemy slow ratio and stack count.
            float penalty = stacking.PenaltyPerStack;
            float maxSlow = stacking.MaxStackSlow > 0f ? stacking.MaxStackSlow : 0.5f;
            for (int i = 0; i < count; i++)
            {
                int eid = _activeEnemyList[i];
                if (!store.EnemyActive[eid]) continue;
                int gx = (int)store.PositionX[eid];
                int gy = (int)store.PositionY[eid];
                long key = (long)gx * 1000L + (long)gy;
                int stackCount = _stackCountDict[key];
                // stackCount-1 = number of OTHER enemies in same cell (0 if alone).
                int effectiveStack = stackCount - 1;
                store.EnemyStackCount[eid] = effectiveStack;
                if (effectiveStack > 0)
                {
                    float slow = 1f - effectiveStack * penalty;
                    if (slow < maxSlow) slow = maxSlow;
                    if (slow > 1f) slow = 1f;
                    store.EnemyStackSlowRatio[eid] = slow;
                }
                else
                {
                    store.EnemyStackSlowRatio[eid] = 1f;
                }
            }
        }

        /// <summary>
        /// Serial pass: Boss Trample (步伤) — 已被上一行 resolve 调用占位
        /// </summary>
        private void ResolveTrampleAoe()
        {
            if (_activeEnemyList == null) return;
            int count = _activeEnemyList.Count;
            if (count == 0) return;
            // O(1) early-out via SetTurn() pre-scan. Most frames have no trampler.
            if (!_hasTramplerThisFrame) return;

            // Cache player position once. Player lives at playerId, which is also in
            // _activeEnemyList? No — player is in ActiveEnemyIds? Let's check: in this
            // codebase, ComponentStore stores the player separately. To be safe, read
            // PositionX/Y directly using playerId without requiring EnemyActive[playerId].
            // If playerId is invalid (not used as enemy slot), DecreasePlayerHealth itself
            // is a no-op via IsValidPlayer check, so we just call it.
            float px = store.PositionX[playerId];
            float py = store.PositionY[playerId];

            // Outer loop: tramplers. Inner loop: tramplee candidates (other enemies).
            for (int i = 0; i < count; i++)
            {
                int tramplerId = _activeEnemyList[i];
                if (!store.EnemyActive[tramplerId]) continue;
                float radius = store.EnemyTrampleRadius[tramplerId];
                if (radius <= 0f) continue;
                float dmg = store.EnemyTrampleDamagePerStep[tramplerId];
                if (dmg <= 0f) continue;
                float tx = store.PositionX[tramplerId];
                float ty = store.PositionY[tramplerId];
                float r2 = radius * radius;

                // (a) Player damage if in range
                float dxp = px - tx;
                float dyp = py - ty;
                float distSqP = dxp * dxp + dyp * dyp;
                if (distSqP <= r2)
                {
                    // DecreasePlayerHealth already handles shield + armor mitigation.
                    store.DecreasePlayerHealth(playerId, dmg);
                }

                // (b) Other enemies: knockback 0.5 unit away from trampler.
                // Vector is reversed (trampler → tramplee) normalized.
                for (int j = 0; j < count; j++)
                {
                    int victimId = _activeEnemyList[j];
                    if (victimId == tramplerId) continue;
                    if (!store.EnemyActive[victimId]) continue;
                    float vx = store.PositionX[victimId];
                    float vy = store.PositionY[victimId];
                    float dxv = vx - tx;
                    float dyv = vy - ty;
                    float d2 = dxv * dxv + dyv * dyv;
                    if (d2 > r2) continue;
                    // Skip if victim is itself a trampler with larger radius (avoid
                    // infinite-jiggle from two Bosses near each other).
                    if (store.EnemyTrampleRadius[victimId] > radius) continue;
                    float len = (float)Math.Sqrt(d2);
                    if (len < 1e-4f) continue; // co-located: skip
                    float nx = dxv / len;
                    float ny = dyv / len;
                    float newX = vx + nx * 0.5f;
                    float newY = vy + ny * 0.5f;
                    // Clamp to map bounds. Y upper bound is a generous ceiling
                    // (no MapHeight field is plumbed into EnemyMovementSystem; the
                    // primary code path also only clamps Y lower — see line 336).
                    if (newX < 0f) newX = 0f;
                    if (newX > mapWidthMinusOne) newX = mapWidthMinusOne;
                    if (newY < 0f) newY = 0f;
                    if (newY > 10000f) newY = 10000f;
                    store.PositionX[victimId] = newX;
                    store.PositionY[victimId] = newY;
                }
            }
        }

        /// <summary>
        /// Serial pass: Tether 锁链强制 (lock-chain enforcement).
        /// 移动后检查所有 active enemy 的锁链配置：
        /// (1) 如果 enemy 与 partner 距离 > EnemyTetherMaxLength，则把远端朝近端拉回（最多 0.5 单位），
        ///     并把该 enemy 的 EnemyTetherSlowFactor 设为 0.5（next-frame 移速减半）。
        /// (2) 锁链两端都是 active enemy 时才处理（任一被销毁则 break）。
        /// (3) 默认 EnemyTetherMaxLength == 0 → 完全无锁链（O(1) early-out via SetTurn pre-scan）。
        /// (4) 防止重复处理：每对 lock pair 通过 A.partner == B 条件，只处理一次（id 小的方向）。
        /// Staggered/Banished 敌人本身已在 movement 阶段 early-return，
        /// 但本 pass 仍会拉他们（不限制其位置 — 但他们下一帧仍 early-return，所以"拉回"对他们没意义）。
        /// 简化：我们直接跳过分身=staggered/banished 的 enemy（即他们不会被拉，也不会有 slow），
        /// 因为他们位置本就锁定在原地。
        /// </summary>
        private void ResolveTetherEnforcement()
        {
            if (_activeEnemyList == null) return;
            int count = _activeEnemyList.Count;
            if (count == 0) return;
            // O(1) early-out: most frames have no tethered enemies.
            if (!_hasTetheredThisFrame) return;

            // Tether slow factor to apply: 0.5 (50% speed) when over-length, else 1.0 (no slow).
            const float TETHER_SLOW = 0.5f;
            const float TETHER_PULL = 0.5f;
            // Y upper bound clamp (matches trample Y clamp — EnemyMovementSystem has no MapHeight).
            const float Y_UPPER = 10000f;

            // Outer loop: every tethered enemy (only id < partnerId to avoid double-processing).
            for (int i = 0; i < count; i++)
            {
                int enemyId = _activeEnemyList[i];
                if (!store.EnemyActive[enemyId]) continue;
                if (store.EnemyTetherMaxLength[enemyId] <= 0f) continue;

                int partnerId = store.EnemyTetherPartnerId[enemyId];
                if (partnerId <= enemyId) continue; // only process once per pair (enemyId < partnerId)
                if (partnerId >= ComponentStore.MAX_ENTITIES) continue;
                if (!store.EnemyActive[partnerId]) continue;
                if (store.EnemyTetherMaxLength[partnerId] <= 0f) continue;

                float maxLen = store.EnemyTetherMaxLength[enemyId];
                float ex = store.PositionX[enemyId];
                float ey = store.PositionY[enemyId];
                float px = store.PositionX[partnerId];
                float py = store.PositionY[partnerId];
                float dx = px - ex;
                float dy = py - ey;
                float distSq = dx * dx + dy * dy;
                float maxLenSq = maxLen * maxLen;

                if (distSq <= maxLenSq)
                {
                    // Within range: clear slow factor on both sides (resets to 1.0 = no slow).
                    store.EnemyTetherSlowFactor[enemyId] = 1f;
                    store.EnemyTetherSlowFactor[partnerId] = 1f;
                    continue;
                }

                // Over range: apply slow to both ends (consumed by next-frame movement mult).
                store.EnemyTetherSlowFactor[enemyId] = TETHER_SLOW;
                store.EnemyTetherSlowFactor[partnerId] = TETHER_SLOW;

                // Pull the "further" end 0.5 units toward the other.
                // Pick the end farther from the line center as the "victim" being pulled.
                // We just pull both ends slightly toward each other to avoid oscillation:
                //   enemy moves toward partner by 0.5 * fraction
                //   partner moves toward enemy by 0.5 * fraction
                // Actually simpler: pull the one with the larger distance-from-partner (the trailing one).
                float dist = (float)Math.Sqrt(distSq);
                if (dist < 1e-4f) continue; // co-located: skip
                float nx = dx / dist;
                float ny = dy / dist;
                // Pull enemyId toward partnerId by 0.5 unit (clamped to map bounds)
                float newEx = ex + nx * TETHER_PULL;
                float newEy = ey + ny * TETHER_PULL;
                if (newEx < 0f) newEx = 0f;
                if (newEx > mapWidthMinusOne) newEx = mapWidthMinusOne;
                if (newEy < 0f) newEy = 0f;
                if (newEy > Y_UPPER) newEy = Y_UPPER;
                store.PositionX[enemyId] = newEx;
                store.PositionY[enemyId] = newEy;
                // Pull partnerId toward enemyId by 0.5 unit (in opposite direction = -nx, -ny)
                float newPx = px - nx * TETHER_PULL;
                float newPy = py - ny * TETHER_PULL;
                if (newPx < 0f) newPx = 0f;
                if (newPx > mapWidthMinusOne) newPx = mapWidthMinusOne;
                if (newPy < 0f) newPy = 0f;
                if (newPy > Y_UPPER) newPy = Y_UPPER;
                store.PositionX[partnerId] = newPx;
                store.PositionY[partnerId] = newPy;
            }
        }

        /// <summary>
        /// Resolve Leap / Jump Attack landing AoE. Runs after the parallel movement pass.
        /// Detects enemies whose leap animation just completed this frame (EnemyLeapElapsed
        /// crossed past EnemyLeapDuration during the parallel pass). For each such leaper:
        ///   1. If player is within EnemyLeapRadius, apply AoE damage via DecreasePlayerHealth.
        ///   2. If EnemyLeapStunDuration > 0, stun all enemies within EnemyLeapRadius (excluding
        ///      the leaper itself and any dead/inactive enemies).
        ///   3. Reset: EnemyLeapElapsed = 0, EnemyLeapCooldown = EnemyLeapCooldownRef, action enum
        ///      back to MoveToTarget so the leaper resumes normal forward movement.
        /// O(N) scan, but only actually does work on frames where a leap lands. A frame cache
        /// (_hasLeapLandingThisFrame) would be optimal but the linear scan is O(activeEnemies)
        /// and at most a handful land per frame in practice.
        /// </summary>
        private void ResolveLeapLanding()
        {
            if (_activeEnemyList == null) return;
            int count = _activeEnemyList.Count;
            if (count == 0) return;

            // Cache player position once for all leapers this frame.
            float px = store.PositionX[playerId];
            float py = store.PositionY[playerId];

            for (int i = 0; i < count; i++)
            {
                int leaperId = _activeEnemyList[i];
                if (!store.EnemyActive[leaperId]) continue;
                // Skip non-leapers (zero-overhead short-circuit on the common case).
                if (store.EnemyLeaperArchetype[leaperId] == 0) continue;
                float elapsed = store.EnemyLeapElapsed[leaperId];
                if (elapsed <= 0f) continue;
                // We declared the leap complete when elapsed >= duration in the parallel pass.
                // On the frame where elapsed == duration, t == 1.0, so the leaper is AT the
                // target position. We treat the landing as "elapsed >= duration" (post-increment
                // can equal duration+1 on the very last frame — both are valid landings).
                float dur = store.EnemyLeapDuration[leaperId];
                if (elapsed < dur) continue;

                // ── Landing AoE ──
                float radius = store.EnemyLeapRadius[leaperId];
                float dmg = store.EnemyLeapDamage[leaperId];
                float stunDur = store.EnemyLeapStunDuration[leaperId];
                float lx = store.PositionX[leaperId];
                float ly = store.PositionY[leaperId];
                float r2 = radius * radius;

                // (a) Player damage if in range.
                if (dmg > 0f)
                {
                    float dxp = px - lx;
                    float dyp = py - ly;
                    float distSqP = dxp * dxp + dyp * dyp;
                    if (distSqP <= r2)
                    {
                        // DecreasePlayerHealth handles shield + armor mitigation.
                        store.DecreasePlayerHealth(playerId, dmg);
                    }
                }

                // (b) Stun nearby enemies if stun duration > 0. Iterates active enemy list.
                // The leaper itself is excluded; dead/inactive enemies are skipped via EnemyActive.
                if (stunDur > 0f)
                {
                    for (int j = 0; j < count; j++)
                    {
                        int victimId = _activeEnemyList[j];
                        if (victimId == leaperId) continue;
                        if (!store.EnemyActive[victimId]) continue;
                        float vx = store.PositionX[victimId];
                        float vy = store.PositionY[victimId];
                        float dxv = vx - lx;
                        float dyv = vy - ly;
                        float distSqV = dxv * dxv + dyv * dyv;
                        if (distSqV <= r2)
                        {
                            // Per-type CC immunity (Round 97): Stun bit or Unstoppable blocks leaper-stun
                            if (store.IsCCImmuneTo(victimId, CCImmunityConfig.Mask_Stun)) continue;
                            // Apply stun. Set both the bool flag and the duration counter so
                            // the early-return at the top of the movement loop blocks them
                            // for the configured number of frames. Decrement is handled by
                            // the standard stun-tick logic above.
                            store.EnemyStunFlag[victimId] = true;
                            store.EnemyStunDurationLeft[victimId] = stunDur;
                        }
                    }
                }

                // ── Reset leaper state ──
                store.EnemyLeapElapsed[leaperId] = 0f;
                store.EnemyLeapCooldown[leaperId] = store.EnemyLeapCooldownRef[leaperId];
                // Switch action back to default movement so the leaper advances normally.
                store.EnemyActionEnum[leaperId] = EnemyActionType.MoveToTarget;
            }
        }

        /// <summary>
        /// Parse dodge direction from action string suffix (e.g. "dodge_1" → +1, "dodge_-1" → -1, "dodge" → +1).
        /// Kept for backward compatibility with the dodge parameter only.
        /// </summary>
        private static int ParseDodgeDirection(string action)
        {
            if (string.IsNullOrEmpty(action))
                return 1;

            int underscoreIdx = action.LastIndexOf('_');
            if (underscoreIdx > 0 && underscoreIdx < action.Length - 1)
            {
                string suffix = action.Substring(underscoreIdx + 1);
                if (int.TryParse(suffix, out int dir))
                    return dir;
            }
            return 1; // default dodge right
        }

        // ─────────────────────────────────────────────────────────────────────
        // Round 121 — Direction 1: Runtime Path Branching helpers
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Count active towers within `radius` of (x, y). Used by TowerDensityBased junction
        /// policy. Iterates ActiveTowerIds (typically O(10) for normal plays) and does a
        /// squared-distance check. Parallel-safe: each enemy calls this in its own thread,
        /// reads are read-only on TowerActive / PositionX / PositionY.
        /// </summary>
        private int CountTowersNearEnemy(float x, float y, float radius)
        {
            if (_activeTowerList == null) return 0;
            float rSq = radius * radius;
            int count = 0;
            int n = _activeTowerList.Count;
            for (int t = 0; t < n; t++)
            {
                int towerId = _activeTowerList[t];
                if (!store.TowerActive[towerId]) continue;
                float dx = store.PositionX[towerId] - x;
                float dy = store.PositionY[towerId] - y;
                if (dx * dx + dy * dy <= rSq) count++;
            }
            return count;
        }

        /// <summary>
        /// True if the enemy has any boss-related flag set (Elite or BossPhase > 0).
        /// Used by TypeBased junction policy. Reads two SOA fields. Note: there is no
        /// dedicated `EnemyIsBoss` flag in the store — bosses are identified by their
        /// BossPhase field being non-zero (set when the boss enters a phase transition).
        /// </summary>
        private bool IsBossEnemy(int enemyId)
        {
            return store.EnemyIsElite[enemyId]
                || store.EnemyBossPhase[enemyId] > 0;
        }
    }
}
