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

        // PathfindingSystem reference for waypoint-based movement
        private PathfindingSystem _pathfinding;
        // WeatherSystem reference for dynamic weather effects
        private WeatherSystem _weather;
        // DayNightSystem reference for day/night cycle effects
        private DayNightSystem _dayNight;

        public EnemyMovementSystem(Core.ComponentStore store, int playerId, int mapWidth = 10)
        {
            this.store = store;
            this.playerId = playerId;
            this.mapWidthMinusOne = mapWidth - 1f;
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
                // Apply weather move speed modifier (Rain/Fog/Storm slow)
                if (_weather != null)
                    moveSpeed *= _weather.GetEnemySpeedMultiplier(playerId);
                // Apply day/night cycle speed modifier
                if (_dayNight != null)
                    moveSpeed *= _dayNight.GetEnemySpeedMultiplier(playerId);

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
                    return; // waypoint movement replaces enum-based movement
                }

                // Default: move toward player (direction = -1, toward y=0)
                int direction = -1;

// Simplified switch: only Retreat needs special handling.
                // MoveToTarget, None, and default all fall through to direction = -1.
                switch (actionEnum)
                {
                    case EnemyActionType.Retreat:
                        direction = 1;
                        break;

                    case EnemyActionType.Dodge:
                        // X-axis lateral dodge is handled inline in EnemyAISystem (serial).
                        // Here we still apply forward Y movement toward player.
                        break;

                    case EnemyActionType.Fear:
                        // Fear: run away from player (direction = +1, toward y=max)
                        direction = 1;
                        break;

                    case EnemyActionType.Taunt:
                        // Taunt: attack the forced target instead of moving.
                        // Skip movement this frame. TowerAttackSystem handles the taunt target attack.
                        direction = 0; // zero movement
                        break;

                    case EnemyActionType.Charm:
                        // Charm: attack nearest enemy instead of moving.
                        // Skip movement this frame. Find nearest enemy and attack it.
                        direction = 0;
                        break;

                    default:
                        // Default: move toward player (direction = -1, toward y=0)
                        break;
                }

                store.PositionY[enemyId] = y + direction * moveSpeed;
            });
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
