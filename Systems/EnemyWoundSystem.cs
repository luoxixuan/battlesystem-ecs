using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Enemy Wound / Cripple System — HP-Threshold Slow.
    /// When an enemy's HP drops below WoundThreshold fraction, movement speed is reduced.
    /// Runs in WavePhase before EnemyMovement so the speed penalty is applied each frame.
    /// </summary>
    public class EnemyWoundSystem
    {
        private readonly ComponentStore store;
        private readonly int playerId;
        private List<int> _activeEnemyList;

        public EnemyWoundSystem(ComponentStore store, int playerId = 0)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.playerId = playerId;
        }

        public void SetTurn(int turn)
        {
            _activeEnemyList = store.GetCachedActiveEnemyIds();
        }

        public void Update()
        {
            if (_activeEnemyList == null)
                _activeEnemyList = store.GetCachedActiveEnemyIds();

            var activeEnemies = _activeEnemyList;

            Parallel.For(0, activeEnemies.Count, ParallelOptionsCache.HotPath, i =>
            {
                int enemyId = activeEnemies[i];
                if (!store.EnemyActive[enemyId])
                    return;

                float threshold = store.EnemyWoundThreshold[enemyId];
                if (threshold <= 0f)
                    return; // No wound mechanic for this enemy type

                float currentHealth = store.EnemyHealth[enemyId];
                float maxHealth = store.EnemyMaxHealth[enemyId];
                if (maxHealth <= 0f)
                    return;

                float hpRatio = currentHealth / maxHealth;
                bool shouldBeWounded = hpRatio < threshold;

                if (shouldBeWounded && !store.EnemyIsWounded[enemyId])
                {
                    // Transition: healthy → wounded — apply wound slow
                    store.EnemyIsWounded[enemyId] = true;
                    float slowRatio = store.EnemyWoundSlowRatio[enemyId];
                    if (slowRatio > 0f)
                    {
                        float baseSpeed = store.EnemyMoveSpeedBase[enemyId];
                        if (baseSpeed <= 0f) baseSpeed = store.EnemyMoveSpeed[enemyId];
                        store.EnemyMoveSpeed[enemyId] = baseSpeed * slowRatio;
                    }
                }
                else if (!shouldBeWounded && store.EnemyIsWounded[enemyId])
                {
                    // Transition: wounded → healthy — clear wound slow
                    store.ClearEnemyWound(enemyId);
                }
            });
        }
    }
}
