using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
        private static readonly Random critRandom = new Random();

        // Cached per-turn to avoid per-frame store lookups
        private float _playerX, _playerY;
        private float _attackDamage, _attackRange;
        private List<int> _activeEnemyList;
        private bool _turnCached;
        private long _frameGoldAcc;
        private int _rangeSq;

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
            _frameGoldAcc = 0;
            _rangeSq = (int)(_attackRange * _attackRange);
        }

        public int GetCachedEnemyCount() => _activeEnemyList != null ? _activeEnemyList.Count : 0;

        public void Update()
        {
            if (!_turnCached)
            {
                SetTurn(0);
            }

            var buffs = store.GetPlayerBuffs(playerId);

            float finalAttackDamage = _attackDamage;

            if (buffs.Count > 0)
            {
                foreach (string buff in buffs)
                {
                    if (buff == "Attack+10%")
                    {
                        finalAttackDamage *= 1.1f;
                    }
                    else if (buff == "Crit Rate+5%")
                    {
                        if (critRandom.NextDouble() < 0.05)
                        {
                            finalAttackDamage *= 2f;
                        }
                    }
                }
            }

            _frameGoldAcc = 0;
            var activeEnemyIds = _activeEnemyList;

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

                enemyHealth -= finalAttackDamage;
                store.EnemyHealth[enemyId] = enemyHealth;

                if (enemyHealth <= 0f)
                {
                    Interlocked.Add(ref _frameGoldAcc, (long)store.EnemyGoldReward[enemyId]);
                    // Use DestroyEntity instead of directly setting EnemyActive=false
                    // so ActiveEnemyIds list stays consistent (Bug#9 / stale enemy list fix)
                    store.DestroyEntity(enemyId);
                }
            });

            if (_frameGoldAcc > 0)
            {
                store.PlayerGold[playerId] += (int)_frameGoldAcc;
            }
        }
    }
}