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
        private TechTreeSystem techTreeSystem;
        private List<int> _activeEnemyList;

        // GC elimination: per-tower reusable candidate lists, pre-allocated in SetTurn
        private List<int>[] _towerCandidates = Array.Empty<List<int>>();

        // Ping-pong double-buffer: eliminates per-frame new ConcurrentBag<>() allocation
        private ConcurrentBag<(int enemyId, float damage, int playerId)>[] _damageQueue = new ConcurrentBag<(int, float, int)>[2];
        private int _damageQueueIdx = 0;

        // Cached player armor stats (updated each SetTurn)
        private float _armorPenetration = 0f;  // from TechTreeSystem
        private float _damageTakenMult = 1f;   // from TechTreeSystem

        // Cached wave-based difficulty multiplier (updated each SetTurn)
        private float _waveDifficultyMult = 1f;

        public TowerAttackSystem(ComponentStore store, IRenderer logger, TechTreeSystem techTreeSystem = null)
        {
            this.store = store;
            this.logger = logger;
            this.techTreeSystem = techTreeSystem;
            _damageQueue[0] = new ConcurrentBag<(int, float, int)>();
            _damageQueue[1] = new ConcurrentBag<(int, float, int)>();
        }

        public void SetTurn(int turn)
        {
            _activeEnemyList = store.GetCachedActiveEnemyIds();  // zero allocation — frame cache

            // Cache armor stats from tech tree
            _armorPenetration = techTreeSystem != null ? techTreeSystem.GetArmorPenetration() : 0f;
            _damageTakenMult = techTreeSystem != null ? techTreeSystem.GetDamageTakenMult() : 1f;

            // Cache wave-based difficulty multiplier (default wave 1)
            _waveDifficultyMult = techTreeSystem != null ? techTreeSystem.GetWaveDifficultyMultiplier(1) : 1f;

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

        /// <summary>
        /// Update the cached wave difficulty multiplier when wave number changes.
        /// Call this when a new wave starts.
        /// </summary>
        public void SetWaveNumber(int waveNumber)
        {
            _waveDifficultyMult = techTreeSystem != null ? techTreeSystem.GetWaveDifficultyMultiplier(waveNumber) : 1f;
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

            // Phase 0: Spatial grid already rebuilt by GameManager before system chain.
            // Reuse instead of rebuilding — avoids O(enemies) waste per frame.

            // Phase 1 (parallel): collect damage events only — no structural mutations.
            // Capture current bag into local so threads keep writing to the same bag reference
            // even after we swap _damageQueue below. This prevents orphaned-bag damage drops.
            var bag = _damageQueue[_damageQueueIdx];

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
                    float baseDmg = store.TowerAttackDamage[towerId];
                    // Apply enemy armor reduction + tech tree damage taken multiplier + wave scaling
                    // Inlined: avoids branch + Math.Max call in hot path
                    baseDmg *= Math.Max(0.01f, 1f - store.EnemyArmor[bestTarget] * (1f - _armorPenetration)) * _damageTakenMult;
                    if (_waveDifficultyMult != 1.0f) baseDmg *= _waveDifficultyMult;
                    bag.Add((bestTarget, baseDmg, store.PlayerEntityId));
                }
            });

            // Phase 2 (serial): ping-pong swap — read from current bag, clear alternate for next frame
            int readIdx = _damageQueueIdx;
            int writeIdx = 1 - _damageQueueIdx;
            _damageQueueIdx = writeIdx;
            _damageQueue[writeIdx].Clear();
            foreach (var (enemyId, damage, playerId) in _damageQueue[readIdx])
            {
                if (!store.EnemyActive[enemyId]) continue;
                store.EnemyHealth[enemyId] -= damage;
                if (store.EnemyHealth[enemyId] <= 0f)
                {
                    store.QueueEnemyDeath(enemyId, playerId);
                }
            }
            System.Threading.Thread.MemoryBarrier(); // ensure drain completes
        }
    }
}