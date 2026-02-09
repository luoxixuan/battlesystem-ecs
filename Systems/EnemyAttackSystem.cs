using System;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 敌人攻击玩家系统 - SOA (Struct of Arrays) 优化
    /// 敌人攻击玩家，减少玩家生命值
    /// 性能提升：10-100 倍
    /// </summary>
    public class EnemyAttackSystem
    {
        private Core.ComponentStore store;
        private IRenderer logger;
        private int playerId;

        public EnemyAttackSystem(Core.ComponentStore store, IRenderer logger, int playerId)
        {
            this.store = store;
            this.logger = logger;
            this.playerId = playerId;
        }

        /// <summary>
        /// 更新敌人攻击玩家（每回合）
        /// </summary>
        public void Update()
        {
            // 获取所有活跃敌人
            var activeEnemyIds = store.GetAllActiveEnemyIds();
            if (activeEnemyIds.Count == 0) return;

            // 获取玩家位置和生命值
            if (!store.PositionActive[playerId]) return;

            float playerX = store.PositionX[playerId];
            float playerY = store.PositionY[playerId];
            float playerCurrentHealth = store.GetPlayerCurrentHealth(playerId);

            if (playerCurrentHealth <= 0f) return;

            int enemiesAttacked = 0;

            // 遍历所有敌人，检查是否攻击玩家
            foreach (int enemyId in activeEnemyIds)
            {
                if (enemyId == playerId) continue;

                // 检查敌人是否到达玩家位置（SOA 直接数组访问）
                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];
                float enemyHealth = store.GetEnemyHealth(enemyId);

                if (enemyHealth <= 0f)
                    continue;

                // 检查敌人是否与玩家相邻（直接相邻，即同一列）
                if (System.Math.Abs(enemyX - playerX) < 0.5f && System.Math.Abs(enemyY - playerY) < 1f)
                {
                    // 敌人攻击玩家
                    float enemyDamage = store.EnemyDamage[enemyId];
                    
                    // 减少玩家生命值
                    store.DecreasePlayerHealth(playerId, enemyDamage);
                    
                    float playerNewHealth = store.GetPlayerCurrentHealth(playerId);
                    
                    logger.Log($"[DAMAGE] Enemy {enemyId} attacked Player! Damage: {enemyDamage:F1}, Player Health: {playerNewHealth:F1} / 200");
                    
                    enemiesAttacked++;

                    // 检查玩家是否死亡
                    if (playerNewHealth <= 0f)
                    {
                        logger.Log($"[DEATH] Player died! Killed by Enemy {enemyId}.");
                        logger.Log("[INFO] Game Over! Player died.");
                        return;
                    }
                }
            }

            if (enemiesAttacked > 0)
            {
                logger.Log($"[COMBAT] {enemiesAttacked} enemies attacked Player this turn");
            }
        }

        /// <summary>
        /// 检查玩家是否存活
        /// </summary>
        public bool IsPlayerAlive()
        {
            return store.IsPlayerAlive(playerId);
        }
    }
}
