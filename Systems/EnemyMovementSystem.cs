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
    /// </summary>
    public class EnemyMovementSystem
    {
        private Core.ComponentStore store;
        private readonly int playerId;
        private readonly float mapWidthMinusOne;  // Bug#30: replace magic number 9f

        // Cached per-turn to avoid per-frame store lookups
        private List<int> _activeEnemyList;
        private float _playerX;

        public EnemyMovementSystem(Core.ComponentStore store, int playerId, int mapWidth = 10)
        {
            this.store = store;
            this.playerId = playerId;
            this.mapWidthMinusOne = mapWidth - 1f;
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

                // Decrement stun duration (duration-based stun, survives SetTurn clear)
                float stunDur = store.EnemyStunDurationLeft[enemyId];
                if (stunDur > 0f)
                {
                    store.EnemyStunDurationLeft[enemyId] = stunDur - 1f;
                    if (store.EnemyStunDurationLeft[enemyId] <= 0f)
                    {
                        store.EnemyStunDurationLeft[enemyId] = 0f;
                        store.EnemyStunFlag[enemyId] = false;
                    }
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

                // Enum-based action dispatch — O(1) per enemy, no string comparison
                EnemyActionType actionEnum = store.GetEnemyActionEnum(enemyId);

                // Stun check: stunned enemies skip all movement this frame
                if (store.IsEnemyStunned(enemyId))
                    return;

                // Default: move toward player (direction = -1, toward y=0)
                int direction = -1;
                float x = store.PositionX[enemyId];
                float y = store.PositionY[enemyId];

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
