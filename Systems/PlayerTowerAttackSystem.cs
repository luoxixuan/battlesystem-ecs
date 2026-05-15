using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
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
        private Core.ComponentStore store;
        private IRenderer renderer;
        private int playerId;

        // BUG-1 fix: use System.Random.Shared (thread-safe in .NET 6+, eliminates Random corruption in Parallel.For)
        private static readonly Random critRandom = System.Random.Shared;

        // Cached per-turn to avoid per-frame store lookups
        private float _playerX, _playerY;
        private float _attackDamage, _attackRange;
        private List<int> _activeEnemyList;
        private bool _turnCached;
        private int _rangeSq;

        // Two-phase: damage collected in parallel (enemyId, damage), applied serially with -= to accumulate correctly
        private ConcurrentBag<(int enemyId, float damage)> _damageQueue = new ConcurrentBag<(int, float)>();

        public PlayerTowerAttackSystem(Core.ComponentStore store, IRenderer renderer, int playerId, GameConfig gameConfig)
        {
            this.store = store;
            this.renderer = renderer;
            this.playerId = playerId;
        }

        public void SetTurn(int turn)
        {
            _playerX = store.PositionX[playerId];
            _playerY = store.PositionY[playerId];
            _attackDamage = store.GetPlayerAttackDamage(playerId);
            _attackRange = store.GetPlayerAttackRange(playerId);
            _activeEnemyList = store.GetAllActiveEnemyIds();
            _turnCached = true;
            _rangeSq = (int)(_attackRange * _attackRange);
        }

        public int GetCachedEnemyCount() => _activeEnemyList != null ? _activeEnemyList.Count : 0;

        public void Update()
        {
            if (!_turnCached)
            {
                SetTurn(0);
            }

            // O(1) bit-flag check — no GC, no string comparison
            float baseDamage = _attackDamage * store.GetAttackBuffMultiplier(playerId);
            bool hasCritBuff = store.HasCritRateBuff(playerId);

            var activeEnemyIds = _activeEnemyList;

            // Phase 1 (parallel): collect damage events only — no structural mutations
            Parallel.For(0, activeEnemyIds.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
            {
                int enemyId = activeEnemyIds[i];
                if (enemyId == playerId) return;

                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];
                if (enemyY <= _playerY) return;

                float dx = enemyX - _playerX;
                if (dx * dx > _rangeSq) return;

                float enemyHealth = store.EnemyHealth[enemyId];
                if (enemyHealth <= 0f) return;

                // H-3 fix: crit rolled per-enemy inside parallel loop, not once per frame globally.
                float finalDamage = baseDamage;
                if (hasCritBuff && critRandom.NextDouble() < 0.05)
                {
                    finalDamage *= 2f;
                }

                _damageQueue.Add((enemyId, finalDamage));
            });

            // Phase 2 (serial): apply collected damage, then queue deaths
            foreach (var (enemyId, damage) in _damageQueue)
            {
                if (!store.EnemyActive[enemyId]) continue;
                store.EnemyHealth[enemyId] -= damage;
                if (store.EnemyHealth[enemyId] <= 0f)
                {
                    store.QueueEnemyDeath(enemyId, playerId);
                }
            }

            // Damage queue reset remains here to keep memory bounded per frame
            _damageQueue = new ConcurrentBag<(int, float)>();
        }
    }
}