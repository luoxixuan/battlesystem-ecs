using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Components;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 塔攻击系统 - 负责处理塔寻找目标并攻击敌人的逻辑
    /// </summary>
    public class TowerAttackSystem
    {
        private ComponentStore store;
        private IRenderer logger;
        private List<int> _activeEnemyList;

        public TowerAttackSystem(ComponentStore store, IRenderer logger)
        {
            this.store = store;
            this.logger = logger;
        }

        public void SetTurn(int turn)
        {
            _activeEnemyList = store.GetAllActiveEnemyIds();
        }

        public void Update(float deltaTime)
        {
            // 遍历所有塔
            for (int i = 0; i < store.NextEntityId; i++)
            {
                if (!store.TowerActive[i]) continue;

                store.TowerLastAttackTime[i] += deltaTime;

                // 检查是否满足攻击速度
                float attackInterval = 1.0f / Math.Max(0.1f, store.TowerAttackSpeed[i]);
                if (store.TowerLastAttackTime[i] >= attackInterval)
                {
                    // 寻找最近敌人
                    int targetId = FindNearestEnemy(store.PositionX[i], store.PositionY[i], store.TowerRange[i]);

                    if (targetId != -1)
                    {
                        // 执行攻击
                        float damage = store.TowerAttackDamage[i];
                        store.EnemyHealth[targetId] -= damage;
                        store.TowerLastAttackTime[i] = 0f;

                        // 检查敌人死亡
                        if (store.EnemyHealth[targetId] <= 0)
                        {
                            store.EnemyActive[targetId] = false;
                        }
                    }
                }
            }
        }

        private int FindNearestEnemy(float tx, float ty, int range)
        {
            int bestTarget = -1;
            float minDistSq = float.MaxValue;
            int rangeSq = range * range;

            var activeEnemies = _activeEnemyList;
            for (int i = 0; i < activeEnemies.Count; i++)
            {
                int enemyId = activeEnemies[i];
                if (!store.EnemyActive[enemyId]) continue;

                float ex = store.PositionX[enemyId];
                float ey = store.PositionY[enemyId];

                float dx = ex - tx;
                float dy = ey - ty;
                float distSq = dx * dx + dy * dy;

                if (distSq <= rangeSq && distSq < minDistSq)
                {
                    minDistSq = distSq;
                    bestTarget = enemyId;
                }
            }

            return bestTarget;
        }
    }
}