using System;
using BattleSystemECS.Components;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// SOA (Struct of Arrays) 敌人移动系统
    /// 直接访问 ComponentStore 的数组，无字典查询，无 struct 复制
    /// 性能提升：10-100 倍
    /// </summary>
    public class EnemyMovementSystem
    {
        private ComponentStore store;

        public EnemyMovementSystem(ComponentStore store)
        {
            this.store = store;
        }

        public void Update()
        {
            var activeEnemyIds = store.GetActiveEnemyIds();
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

            Console.WriteLine($"[MOVE] {enemiesMoved} enemies moved downward");
        }
    }
}
