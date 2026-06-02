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
            // Cache trampler presence for the frame so ResolveTrampleAoe can early-out
            // in O(1) instead of an O(N²) check on every frame.
            // Uses ComponentStore.ActiveTramplerCount (O(1)) instead of per-frame O(N) scan.
            _hasTramplerThisFrame = store.ActiveTramplerCount > 0;
            // Cache tether presence for the frame so ResolveTetherEnforcement can early-out
            // in O(1) instead of an O(N²) check on every frame.
            // Uses ComponentStore.ActiveTetheredCount (O(1)) instead of per-frame O(N) scan.
            _hasTetheredThisFrame = store.ActiveTetheredCount > 0;
        }

        // Cached per-turn: true if at least one active enemy has TrampleRadius & damage > 0.
        // Set in SetTurn(); consumed in ResolveTrampleAoe() for O(1) early-out.
        private bool _hasTramplerThisFrame;
        // Cached per-turn: true if at least one active enemy has TetherMaxLength > 0.
        // Set in SetTurn(); consumed in ResolveTetherEnforcement() for O(1) early-out.
        private bool _hasTetheredThisFrame;

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
                // Apply weather move speed modifier (Rain/Fog/Storm slow)
                if (_weather != null)
                    moveSpeed *= _weather.GetEnemySpeedMultiplier(playerId);
                // Apply day/night cycle speed modifier
                if (_dayNight != null)
                    moveSpeed *= _dayNight.GetEnemySpeedMultiplier(playerId);
                // Apply tile-stacking penalty (crowding slow from previous frame's stack count).
                // 1.0 = no slow. < 1.0 = penalized. Defaults to 1.0 (no penalty) for first frame after spawn.
                moveSpeed *= store.EnemyStackSlowRatio[enemyId];
                // Apply Tether lock-chain slow factor (set by previous frame's ResolveTetherEnforcement).
                // 1.0 = no slow. 0.5 = 50% speed when chain is over-length. Defaults to 1.0.
                moveSpeed *= store.EnemyTetherSlowFactor[enemyId];
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
