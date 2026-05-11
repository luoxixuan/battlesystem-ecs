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
            var activeEnemies = _activeEnemyList ?? store.GetAllActiveEnemyIds();

            for (int ti = 0; ti < store.NextEntityId; ti++)
            {
                if (!store.TowerActive[ti]) continue;

                store.TowerLastAttackTime[ti] += deltaTime;

                float attackInterval = 1.0f / Math.Max(0.1f, store.TowerAttackSpeed[ti]);
                if (store.TowerLastAttackTime[ti] < attackInterval) continue;

                float tx = store.PositionX[ti];
                float ty = store.PositionY[ti];
                int range = store.TowerRange[ti];
                float damage = store.TowerAttackDamage[ti];
                int rangeSq = range * range;

                int bestTarget = -1;
                float minDistSq = float.MaxValue;

                // For-index loop over cached enemy list (no enumerator allocation)
                for (int ei = 0; ei < activeEnemies.Count; ei++)
                {
                    int enemyId = activeEnemies[ei];
                    if (!store.EnemyActive[enemyId]) continue;

                    float ex = store.PositionX[enemyId];
                    float ey = store.PositionY[enemyId];

                    float dx = ex - tx;
                    float dy = ey - ty;

                    float distSq = dx * dx + dy * dy;
                    if (distSq > rangeSq) continue;
                    if (distSq < minDistSq)
                    {
                        minDistSq = distSq;
                        bestTarget = enemyId;
                    }
                }

                store.TowerLastAttackTime[ti] = 0f;

                if (bestTarget != -1)
                {
                    store.EnemyHealth[bestTarget] -= damage;
                    if (store.EnemyHealth[bestTarget] <= 0)
                    {
                        store.EnemyActive[bestTarget] = false;
                    }
                }
            }
        }
    }
}