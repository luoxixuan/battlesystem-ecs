using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using BattleSystemECS.Core;
using BattleSystemECS.Components;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 塔攻击系统 - 负责处理塔寻找目标并攻击敌人的逻辑
    /// Two-phase: parallel collect, serial resolve (Bug#2 thread-safety fix)
    /// </summary>
    public class TowerAttackSystem
    {
        private ComponentStore store;
        private IRenderer logger;
        private List<int> _activeEnemyList;

        // Two-phase: damage collected in parallel (enemyId, damage, playerId), applied serially with -= to accumulate
        private ConcurrentBag<(int enemyId, float damage, int playerId)> _damageQueue = new ConcurrentBag<(int, float, int)>();

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
            var activeTowerIds = store.ActiveTowerIds;

            // Phase 1 (parallel): collect damage events only — no structural mutations
            Parallel.For(0, activeTowerIds.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, ti =>
            {
                int towerId = activeTowerIds[ti];

                store.TowerLastAttackTime[towerId] += deltaTime;

                float attackInterval = 1.0f / Math.Max(0.1f, store.TowerAttackSpeed[towerId]);
                if (store.TowerLastAttackTime[towerId] < attackInterval) return;

                float tx = store.PositionX[towerId];
                float ty = store.PositionY[towerId];
                int range = store.TowerRange[towerId];
                float damage = store.TowerAttackDamage[towerId];
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

                store.TowerLastAttackTime[towerId] = 0f;

                if (bestTarget != -1)
                {
                    _damageQueue.Add((bestTarget, damage, store.PlayerEntityId));
                }
            });

            // Phase 2 (serial): apply damage, queue deaths. Resolve happens at frame end in GameManager/Benchmark.
            foreach (var (enemyId, damage, playerId) in _damageQueue)
            {
                if (!store.EnemyActive[enemyId]) continue;
                store.EnemyHealth[enemyId] -= damage;
                if (store.EnemyHealth[enemyId] <= 0f)
                {
                    store.QueueEnemyDeath(enemyId, playerId);
                }
            }
            // Damage queue reset remains here to keep memory bounded per frame
            _damageQueue = new ConcurrentBag<(int, float, int)>();
        }
    }
}