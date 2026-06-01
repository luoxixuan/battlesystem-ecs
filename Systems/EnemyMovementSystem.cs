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

        public void SetTurn(int turn)
        {
            _activeEnemyList = store.GetCachedActiveEnemyIds();  // zero allocation — frame cache
            _playerX = store.PositionX[playerId];
            _turn = turn;
            // NOTE: Do NOT clear EnemyStunFlag here.
            // Stun is now managed by EnemyStunDurationLeft (duration-based),
            // decremented in Update(). Clearing flags here broke tower stun
            // because TowerAttackSystem.ApplyEnemyStun() runs after SetTurn()
            // in the same frame.
        }

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
                // Apply weather move speed modifier (Rain/Fog/Storm slow)
                if (_weather != null)
                    moveSpeed *= _weather.GetEnemySpeedMultiplier(playerId);
                // Apply day/night cycle speed modifier
                if (_dayNight != null)
                    moveSpeed *= _dayNight.GetEnemySpeedMultiplier(playerId);
                // Apply tile-stacking penalty (crowding slow from previous frame's stack count).
                // 1.0 = no slow. < 1.0 = penalized. Defaults to 1.0 (no penalty) for first frame after spawn.
                moveSpeed *= store.EnemyStackSlowRatio[enemyId];
                if (moveSpeed < 0f) moveSpeed = 0f; // safety clamp

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
            });

            // ── Serial pass: tile-stacking penalty ──
            // Count how many enemies share each cell using the *just-moved* positions.
            // Apply per-enemy slow ratio = clamp(1 - stack * PenaltyPerStack, MaxStackSlow, 1.0).
            // This slow ratio will be applied to next frame's movement.
            // O(N) pass, no allocation (dictionary is reused and cleared at end).
            UpdateStackingPenalty();
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
    }
}
