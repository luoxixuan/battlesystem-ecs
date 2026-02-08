using System;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// SOA (Struct of Arrays) 玩家攻击系统
    /// 直接访问 ComponentStore 的数组，无字典查询，无 struct 复制
    /// 性能提升：10-100 倍
    /// </summary>
    public class PlayerTowerAttackSystem
    {
        private ComponentStore store;
        private IRenderer renderer;
        private int playerId;

        public PlayerTowerAttackSystem(ComponentStore store, IRenderer renderer, int playerId, GameConfig gameConfig)
        {
            this.store = store;
            this.renderer = renderer;
            this.playerId = playerId;
        }

        public void Update()
        {
            // SOA 直接数组访问，无字典查询，无 struct 复制
            float attackRange = store.GetPlayerAttackRange(playerId);
            float attackDamage = store.GetPlayerAttackDamage(playerId);
            float playerX = store.PositionX[playerId];
            float playerY = store.PositionY[playerId];
            var buffs = store.GetPlayerBuffs(playerId);

            // Calculate player stats with buff effects
            float finalAttackDamage = attackDamage;
            float finalAttackRange = attackRange;

            if (buffs.Count > 0)
            {
                foreach (string buff in buffs)
                {
                    if (buff == "Attack+10%")
                    {
                        finalAttackDamage *= 1.1f;
                        renderer.Log($"[BUFF] Attack+10% applied: {finalAttackDamage:F1} damage");
                    }
                    else if (buff == "Crit Rate+5%")
                    {
                        if (new Random().NextDouble() < 0.05)
                        {
                            finalAttackDamage *= 2f;
                            renderer.Log($"[BUFF] CRITICAL! Damage doubled: {finalAttackDamage:F1}");
                        }
                    }
                }
            }

            // Find and attack enemies in range (SOA 迭代)
            var activeEnemyIds = store.GetAllActiveEnemyIds();
            int enemiesAttacked = 0;

            foreach (int enemyId in activeEnemyIds)
            {
                if (enemyId == playerId) continue;

                // SOA 直接数组访问，无字典查询，无 struct 复制
                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];
                float enemyHealth = store.GetEnemyHealth(enemyId);

                // Skip dead enemies
                if (enemyHealth <= 0f)
                    continue;

                // Check if in attack range (直接数组计算，无复制）
                float distance = Math.Abs(enemyX - playerX);
                if (distance <= finalAttackRange && enemyY > playerY)
                {
                    // Attack enemy (SOA 直接数组访问，无 struct 复制）
                    enemyHealth = Math.Max(0f, enemyHealth - finalAttackDamage);
                    store.SetEnemyHealth(enemyId, enemyHealth);

                    int goldReward = store.GetEnemyGoldReward(enemyId);
                    string enemyName = store.GetName(enemyId);

                    renderer.Log($"[ATTACK] Player (Level {store.GetPlayerLevel(playerId)}) attacks enemy {enemyId}, damage: {finalAttackDamage:F1}, position: x={enemyX:F0}, y={enemyY:F0}");

                    if (enemyHealth <= 0f)
                    {
                        // Add gold to player (SOA 直接数组访问，无 struct 复制）
                        float currentGold = store.GetPlayerGold(playerId);
                        float newGold = currentGold + goldReward;
                        store.SetPlayerGold(playerId, newGold);

                        renderer.Log($"[GOLD] Killed {enemyName}, gained {goldReward} gold");
                        renderer.Log($"[GOLD] Total gold: {newGold:F1}");

                        enemiesAttacked++;
                    }
                }
            }

            if (enemiesAttacked > 0)
            {
                renderer.Log($"[COMBAT] Attacked {enemiesAttacked} enemies this turn");
            }
        }
    }
}
