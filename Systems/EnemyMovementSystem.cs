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
    /// </summary>
    public class EnemyMovementSystem
    {
        private Core.ComponentStore store;

        public EnemyMovementSystem(Core.ComponentStore store)
        {
            this.store = store;
        }

        public void Update()
        {
            var activeEnemyIds = store.GetAllActiveEnemyIds();
            int enemiesMoved = 0;

            foreach (int enemyId in activeEnemyIds)
            {
                // SOA 直接数组访问，无字典查询，无 struct 复制
                float moveSpeed = store.EnemyMoveSpeed[enemyId];
                float y = store.PositionY[enemyId];

                if (store.EnemyActive[enemyId])
                {
                    // 敌人向下移动
                    store.PositionY[enemyId] = y - moveSpeed;
                    enemiesMoved++;
                }
            }

            if (enemiesMoved > 0)
            {
                Console.WriteLine($"[MOVE] {enemiesMoved} enemies moved downward");
            }
        }
    }
}
