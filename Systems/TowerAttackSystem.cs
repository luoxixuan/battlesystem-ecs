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

        // GC elimination: per-tower reusable candidate lists, pre-allocated in SetTurn
        private List<int>[] _towerCandidates = Array.Empty<List<int>>();

        // Two-phase: damage collected in parallel (enemyId, damage, playerId), applied serially with -= to accumulate
        private ConcurrentBag<(int enemyId, float damage, int playerId)> _damageQueue = new ConcurrentBag<(int, float, int)>();

        public TowerAttackSystem(ComponentStore store, IRenderer logger)
        {
            this.store = store;
            this.logger = logger;
        }

        public void SetTurn(int turn)
        {
            _activeEnemyList = store.GetCachedActiveEnemyIds();  // zero allocation — frame cache

            // Ensure _towerCandidates is large enough; each slot is a reusable List<int>
            var towerIds = store.ActiveTowerIds;
            if (_towerCandidates.Length < towerIds.Count)
            {
                var newArr = new List<int>[towerIds.Count];
                Array.Copy(_towerCandidates, newArr, _towerCandidates.Length);
                for (int i = _towerCandidates.Length; i < newArr.Length; i++)
                    newArr[i] = new List<int>(128);
                _towerCandidates = newArr;
            }
        }

        public void Update(float deltaTime)
        {
            var activeTowerIds = store.ActiveTowerIds;

            // Defensive: ensure _towerCandidates covers all towers before parallel loop.
            // Safe to call every frame — SetTurn also calls this; extra invocation is a no-op
            // when length is already sufficient.
            if (_towerCandidates.Length < activeTowerIds.Count)
            {
                var newArr = new List<int>[activeTowerIds.Count];
                Array.Copy(_towerCandidates, newArr, _towerCandidates.Length);
                for (int i = _towerCandidates.Length; i < newArr.Length; i++)
                    newArr[i] = new List<int>(128);
                _towerCandidates = newArr;
            }

            // Phase 0: rebuild spatial grid once per frame — O(enemies), called once outside Parallel.For
            store.RebuildSpatialGrid();

            // Phase 1 (parallel): collect damage events only — no structural mutations.
            // Capture current bag into local so threads keep writing to the same bag reference
            // even after we swap _damageQueue below. This prevents orphaned-bag damage drops.
            var bag = _damageQueue;

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

                // Spatial grid: query O(cells) instead of O(enemies) — reuse pre-allocated list
                var candidates = _towerCandidates[ti];
                candidates.Clear();
                store.SpatialGrid.GetEnemiesInRange(store, tx, ty, range, candidates);

                int bestTarget = -1;
                float minDistSq = float.MaxValue;

                for (int ci = 0; ci < candidates.Count; ci++)
                {
                    int enemyId = candidates[ci];
                    if (!store.EnemyActive[enemyId]) continue;

                    float ex = store.PositionX[enemyId];
                    float ey = store.PositionY[enemyId];

                    float dx = ex - tx;
                    float dy = ey - ty;

                    float distSq = dx * dx + dy * dy;
                    if (distSq < minDistSq)
                    {
                        minDistSq = distSq;
                        bestTarget = enemyId;
                    }
                }

                if (bestTarget != -1)
                {
                    store.TowerLastAttackTime[towerId] = 0f;
                    bag.Add((bestTarget, store.TowerAttackDamage[towerId], store.PlayerEntityId));
                }
            });

            // Phase 2 (serial): apply damage, queue deaths. Resolve happens at frame end in GameManager/Benchmark.
            foreach (var (enemyId, damage, playerId) in bag)
            {
                if (!store.EnemyActive[enemyId]) continue;
                store.EnemyHealth[enemyId] -= damage;
                if (store.EnemyHealth[enemyId] <= 0f)
                {
                    store.QueueEnemyDeath(enemyId, playerId);
                }
            }
            // Atomic swap: next frame's threads will capture the new bag via _damageQueue,
            // while this frame's threads (which captured 'bag' above) keep draining to 'bag'.
            System.Threading.Thread.MemoryBarrier(); // ensure bag drain completes before swap
            _damageQueue = new ConcurrentBag<(int, float, int)>();
        }
    }
}