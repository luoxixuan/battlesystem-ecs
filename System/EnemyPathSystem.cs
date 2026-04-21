using System;
using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 敌人路径系统 - SOA (Struct of Arrays) 优化
    /// 管理敌人的移动路径
    /// </summary>
    public class EnemyPathSystem
    {
        private Core.ComponentStore store;
        private IRenderer renderer;

        public EnemyPathSystem(Core.ComponentStore store, IRenderer renderer)
        {
            this.store = store;
            this.renderer = renderer;
        }

        /// <summary>
        /// 更新敌人路径
        /// </summary>
        public void Update()
        {
            var activeEnemyIds = store.GetAllActiveEnemyIds();
            int enemiesMoved = 0;

            foreach (int enemyId in activeEnemyIds)
            {
                if (!store.EnemyActive[enemyId]) continue;

                // 获取敌人属性
                float moveSpeed = store.EnemyMoveSpeed[enemyId];
                float currentY = store.PositionY[enemyId];

                // 敌人向下移动
                store.PositionY[enemyId] = currentY - moveSpeed;
                enemiesMoved++;

                // 检查是否到达底部
                if (store.PositionY[enemyId] <= 0f)
                {
                    // 敌人到达底部，玩家受到伤害
                    DealDamageToPlayer(enemyId);
                    store.EnemyActive[enemyId] = false;
                    renderer.Log($"[ENEMY] 敌人 {enemyId} 到达底部，玩家受到伤害！");
                }
            }

            if (enemiesMoved > 0)
            {
                renderer.Log($"[MOVE] {enemiesMoved} 个敌人向下移动");
            }
        }

        /// <summary>
        /// 对玩家造成伤害
        /// </summary>
        private void DealDamageToPlayer(int enemyId)
        {
            float enemyDamage = store.EnemyDamage[enemyId];
            store.DecreasePlayerHealth(store.PlayerEntityId, enemyDamage);
            
            float playerNewHealth = store.GetPlayerCurrentHealth(store.PlayerEntityId);
            renderer.Log($"[DAMAGE] 敌人 {enemyId} 攻击玩家！造成 {enemyDamage:F1} 点伤害，玩家生命值: {playerNewHealth:F1} / {store.GetPlayerMaxHealth(store.PlayerEntityId)}");
            
            if (playerNewHealth <= 0f)
            {
                renderer.Log("[DEATH] 玩家死亡！游戏结束！");
                store.SetGameStateIsGameRunning(store.PlayerEntityId, false);
            }
        }
    }
}