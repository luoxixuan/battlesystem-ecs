using System;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// SOA (Struct of Arrays) 敌人移动系统
    /// 直接访问 ComponentStore 的数组，无字典查询，无 struct 复制
    /// 性能提升：10-100 倍
    /// Movement direction is driven by EnemyAISystem via EnemyAIAction.
    /// </summary>
    public class EnemyMovementSystem
    {
        private Core.ComponentStore store;
        private readonly int playerId;

        public EnemyMovementSystem(Core.ComponentStore store, int playerId)
        {
            this.store = store;
            this.playerId = playerId;
        }

        public void Update()
        {
            var activeEnemyIds = store.GetAllActiveEnemyIds();
            int enemiesMoved = 0;

            foreach (int enemyId in activeEnemyIds)
            {
                if (!store.EnemyActive[enemyId])
                    continue;

                float moveSpeed = store.EnemyMoveSpeed[enemyId];
                string action = store.GetEnemyAIAction(enemyId);

                // Default: move toward player (direction = -1, toward y=0)
                int direction = -1;
                float x = store.PositionX[enemyId];
                float y = store.PositionY[enemyId];
                float playerX = store.PositionX[playerId];

                if (!string.IsNullOrEmpty(action))
                {
                    if (action == "move_to_target")
                    {
                        // Move toward player on Y axis
                        direction = -1;
                    }
                    else if (action == "retreat")
                    {
                        // Move away from player (opposite direction)
                        direction = 1;
                    }
                    else if (action == "dodge_1")
                    {
                        // Dodge perpendicular: move +1 on X (right), reset Y move
                        store.PositionX[enemyId] = Math.Clamp(x + 1f, 0f, 9f);
                        store.PositionY[enemyId] = y - moveSpeed * 0.5f;
                        enemiesMoved++;
                        continue;
                    }
                    else if (action == "dodge_-1")
                    {
                        // Dodge perpendicular: move -1 on X (left), reset Y move
                        store.PositionX[enemyId] = Math.Clamp(x - 1f, 0f, 9f);
                        store.PositionY[enemyId] = y - moveSpeed * 0.5f;
                        enemiesMoved++;
                        continue;
                    }
                    else if (action == "dodge")
                    {
                        // Default dodge right
                        store.PositionX[enemyId] = Math.Clamp(x + 1f, 0f, 9f);
                        store.PositionY[enemyId] = y - moveSpeed * 0.5f;
                        enemiesMoved++;
                        continue;
                    }
                    else
                    {
                        // Any other action (attack_melee, ranged_attack, charge_attack): hold position
                        continue;
                    }
                }
                else
                {
                    // Fallback: no action set → move toward player
                    direction = -1;
                }

                // Apply Y movement
                store.PositionY[enemyId] = y + direction * moveSpeed;
                enemiesMoved++;
            }

            if (enemiesMoved > 0)
            {
                Console.WriteLine($"[MOVE] {enemiesMoved} enemies moved");
            }
        }
    }
}
