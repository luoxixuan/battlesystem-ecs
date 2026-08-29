using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Mana Burn System — enemies with mana-burn ability drain player mana.
    /// Runs in two phases:
    ///   Phase 1 (parallel): collect mana-burn events from enemies that have the ability
    ///   Phase 2 (serial): apply mana drain to player mana pool
    ///
    /// This creates a resource-denial dynamic: mana-burn enemies force players to
    /// manage their skill resource, balancing offensive casting vs defensive conservation.
    /// </summary>
    public class ManaBurnSystem
    {
        private readonly ComponentStore store;
        private readonly int playerId;

        // Ping-pong double-buffer for mana burn events — eliminates per-frame GC allocation.
        private readonly ConcurrentBag<ManaBurnEvent>[] _manaBurnEvents = new ConcurrentBag<ManaBurnEvent>[2];
        private int _manaBurnEventsIdx = 0;

        private struct ManaBurnEvent
        {
            public int EnemyId;
            public float BurnAmount;
        }

        public ManaBurnSystem(ComponentStore store, int playerId)
        {
            this.store = store;
            this.playerId = playerId;
            _manaBurnEvents[0] = new ConcurrentBag<ManaBurnEvent>();
            _manaBurnEvents[1] = new ConcurrentBag<ManaBurnEvent>();
        }

        public void SetTurn(int turn)
        {
            // Nothing per-turn to cache for mana burn
        }

        public void Update()
        {
            var activeEnemyIds = store.GetActiveEnemyIds();
            int count = activeEnemyIds.Count;

            if (count == 0) return;

            // Phase 1: parallel collection of mana burn events
            // Only enemies with EnemyManaBurnAmount > 0 can burn mana
            int batchSize = 256;
            int parallelThreshold = 500;

            if (count < parallelThreshold)
            {
                // Sequential path — avoid Parallel.For overhead for small counts
                for (int i = 0; i < count; i++)
                {
                    int enemyId = activeEnemyIds[i];
                    if (!store.EnemyActive[enemyId]) continue;

                    float burnAmount = store.EnemyManaBurnAmount[enemyId];
                    if (burnAmount <= 0f) continue;

                    _manaBurnEvents[_manaBurnEventsIdx].Add(new ManaBurnEvent
                    {
                        EnemyId = enemyId,
                        BurnAmount = burnAmount
                    });
                }
            }
            else
            {
                // Parallel path — batch processing
                int numBatches = (count + batchSize - 1) / batchSize;
                Parallel.For(0, numBatches, ParallelOptionsCache.Capped4, batchIdx =>
                {
                    int start = batchIdx * batchSize;
                    int end = Math.Min(start + batchSize, count);
                    for (int i = start; i < end; i++)
                    {
                        int enemyId = activeEnemyIds[i];
                        if (!store.EnemyActive[enemyId]) continue;

                        float burnAmount = store.EnemyManaBurnAmount[enemyId];
                        if (burnAmount <= 0f) continue;

                        _manaBurnEvents[_manaBurnEventsIdx].Add(new ManaBurnEvent
                        {
                            EnemyId = enemyId,
                            BurnAmount = burnAmount
                        });
                    }
                });
            }

            // Phase 2: serial execution — apply mana drain
            int readIdx = _manaBurnEventsIdx;
            foreach (var evt in _manaBurnEvents[readIdx])
            {
                ApplyManaBurn(evt.EnemyId, evt.BurnAmount);
            }

            // Ping-pong swap — clear write buffer
            int writeIdx = 1 - _manaBurnEventsIdx;
            _manaBurnEvents[writeIdx].Clear();
            _manaBurnEventsIdx = writeIdx;
        }

        private void ApplyManaBurn(int enemyId, float burnAmount)
        {
            if (!store.EnemyActive[enemyId]) return;
            if (!store.IsPlayerAlive(playerId)) return;

            float currentMana = store.GetPlayerMana(playerId);
            if (currentMana <= 0f) return; // No mana to burn

            int burnType = store.EnemyManaBurnType[enemyId];
            float drained;
            if (burnType == 1)
            {
                // Percent of current mana
                drained = currentMana * burnAmount;
            }
            else if (burnType == 2)
            {
                // Percent of max mana
                float maxMana = store.GetPlayerMaxMana(playerId);
                drained = maxMana * burnAmount;
            }
            else
            {
                // Flat amount (default)
                drained = Math.Min(currentMana, burnAmount);
            }

            store.DecreasePlayerMana(playerId, drained);
        }
    }
}