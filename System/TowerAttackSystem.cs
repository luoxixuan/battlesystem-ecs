using System;
using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 塔攻击系统 - SOA (Struct of Arrays) 优化
    /// 管理塔的攻击逻辑
    /// </summary>
    public class TowerAttackSystem
    {
        private Core.ComponentStore store;
        private IRenderer renderer;
        private GameConfig gameConfig;

        public TowerAttackSystem(Core.ComponentStore store, IRenderer renderer, GameConfig gameConfig)
        {
            this.store = store;
            this.renderer = renderer;
            this.gameConfig = gameConfig;
        }

        /// <summary>
        /// 更新塔攻击
        /// </summary>
        public void Update()
        {
            var activeTowerIds = store.GetAllActiveTowerIds();
            int towersAttacked = 0;

            foreach (int towerId in activeTowerIds)
            {
                if (!store.TowerActive[towerId]) continue;

                // 获取塔属性
                float attackDamage = store.GetTowerAttackDamage(towerId);
                int range = store.GetTowerRange(towerId);
                float attackSpeed = store.GetTowerAttackSpeed(towerId);
                float lastAttackTime = store.GetTowerLastAttackTime(towerId);
                float currentTime = (float)DateTime.Now.TimeOfDay.TotalSeconds;

                // 检查攻击冷却
                if (currentTime - lastAttackTime < 1f / attackSpeed)
                {
                    continue;
                }

                // 寻找范围内的敌人
                var enemiesInRange = FindEnemiesInRange(towerId, range);
                if (enemiesInRange.Count == 0)
                {
                    continue;
                }

                // 攻击最近的敌人
                int targetEnemyId = FindNearestEnemy(towerId, enemiesInRange);
                if (targetEnemyId == -1) continue;

                // 造成伤害
                float enemyHealth = store.GetEnemyHealth(targetEnemyId);
                enemyHealth = Math.Max(0f, enemyHealth - attackDamage);
                store.SetEnemyHealth(targetEnemyId, enemyHealth);

                // 更新最后攻击时间
                store.SetTowerLastAttackTime(towerId, currentTime);

                towersAttacked++;

                renderer.Log($"[TOWER] 塔在 ({store.PositionX[towerId]:F0}, {store.PositionY[towerId]:F0}) 攻击敌人 {targetEnemyId}，造成 {attackDamage:F1} 点伤害");
            }

            if (towersAttacked > 0)
            {
                renderer.Log($"[COMBAT] {towersAttacked} 个塔进行了攻击");
            }
        }

        /// <summary>
        /// 寻找范围内的敌人
        /// </summary>
        private List<int> FindEnemiesInRange(int towerId, int range)
        {
            var enemiesInRange = new List<int>();
            var activeEnemyIds = store.GetAllActiveEnemyIds();

            float towerX = store.PositionX[towerId];
            float towerY = store.PositionY[towerId];

            foreach (int enemyId in activeEnemyIds)
            {
                if (enemyId == towerId) continue;

                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];
                float distance = Math.Abs(enemyX - towerX) + Math.Abs(enemyY - towerY);

                if (distance <= range)
                {
                    enemiesInRange.Add(enemyId);
                }
            }

            return enemiesInRange;
        }

        /// <summary>
        /// 寻找最近的敌人
        /// </summary>
        private int FindNearestEnemy(int towerId, List<int> enemies)
        {
            if (enemies.Count == 0) return -1;

            float towerX = store.PositionX[towerId];
            float towerY = store.PositionY[towerId];
            int nearestEnemy = -1;
            float minDistance = float.MaxValue;

            foreach (int enemyId in enemies)
            {
                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];
                float distance = Math.Abs(enemyX - towerX) + Math.Abs(enemyY - towerY);

                if (distance < minDistance)
                {
                    minDistance = distance;
                    nearestEnemy = enemyId;
                }
            }

            return nearestEnemy;
        }
    }
}