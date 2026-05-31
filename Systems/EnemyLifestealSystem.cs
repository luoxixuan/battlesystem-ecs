using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Enemy Lifesteal System — enemies with lifesteal heal a fraction of damage dealt back to themselves.
    /// Runs in two phases:
    ///   Phase 1 (parallel): collect lifesteal events from enemies that have lifesteal active
    ///   Phase 2 (serial): apply health restoration
    ///
    /// This creates a resource-denial dynamic: enemies with lifesteal are harder to kill through
    /// direct damage as they recover health over the course of combat.
    /// </summary>
    public class EnemyLifestealSystem
    {
        private readonly ComponentStore store;

        // Ping-pong double-buffer for lifesteal events — eliminates per-frame GC allocation.
        private readonly ConcurrentBag<LifestealEvent>[] _lifestealEvents = new ConcurrentBag<LifestealEvent>[2];
        private int _lifestealEventsIdx = 0;

        private struct LifestealEvent
        {
            public int EnemyId;
            public float HealAmount;
        }

        public EnemyLifestealSystem(ComponentStore store)
        {
            this.store = store;
            _lifestealEvents[0] = new ConcurrentBag<LifestealEvent>();
            _lifestealEvents[1] = new ConcurrentBag<LifestealEvent>();
        }

        public void SetTurn(int turn)
        {
            // Nothing per-turn to cache
        }

        /// <summary>
        /// Check if any active enemy has lifesteal, collect events, apply healing.
        /// </summary>
        public void Update()
        {
            var activeEnemyIds = store.GetActiveEnemyIds();
            int count = activeEnemyIds.Count;

            if (count == 0) return;

            // Phase 1: parallel collection of lifesteal events
            // Only enemies with EnemyLifestealActive=true and ratio > 0 can lifesteal
            int batchSize = 256;
            int parallelThreshold = 500;

            if (count < parallelThreshold)
            {
                // Sequential path — avoid Parallel.For overhead for small counts
                for (int i = 0; i < count; i++)
                {
                    int enemyId = activeEnemyIds[i];
                    if (!store.EnemyActive[enemyId]) continue;

                    if (!store.EnemyLifestealActive[enemyId]) continue;
                    float ratio = store.EnemyLifestealRatio[enemyId];
                    if (ratio <= 0f) continue;

                    // Lifesteal is tracked per-attack in EnemyAISystem via _lifestealEvents.
                    // This system handles passive lifesteal auras (stored in EnemyLifestealRatio
                    // with active=true but not triggered per-attack — currently a no-op pass).
                    // The actual per-attack lifesteal is handled directly in EnemyAISystem's
                    // attack execution methods.
                }
            }
            else
            {
                // Parallel path — batch processing
                int numBatches = (count + batchSize - 1) / batchSize;
                Parallel.For(0, numBatches, new ParallelOptions { MaxDegreeOfParallelism = 4 }, batchIdx =>
                {
                    int start = batchIdx * batchSize;
                    int end = Math.Min(start + batchSize, count);
                    for (int i = start; i < end; i++)
                    {
                        int enemyId = activeEnemyIds[i];
                        if (!store.EnemyActive[enemyId]) continue;

                        if (!store.EnemyLifestealActive[enemyId]) continue;
                        float ratio = store.EnemyLifestealRatio[enemyId];
                        if (ratio <= 0f) continue;
                    }
                });
            }

            // Phase 2: serial execution — apply lifesteal heal from EnemyAISystem queue
            // (EnemyAISystem directly calls store.EnemyHealth[id] += heal in serial phase
            //  — this system is a placeholder for future aura-based passive lifesteal)
            int readIdx = _lifestealEventsIdx;
            foreach (var evt in _lifestealEvents[readIdx])
            {
                if (!store.EnemyActive[evt.EnemyId]) continue;
                float reduction = store.EnemyHealingReduction[evt.EnemyId];
                float effectiveHeal = reduction > 0f ? evt.HealAmount * (1f - reduction) : evt.HealAmount;
                store.EnemyHealth[evt.EnemyId] += effectiveHeal;
                if (store.EnemyHealth[evt.EnemyId] > store.EnemyMaxHealth[evt.EnemyId])
                    store.EnemyHealth[evt.EnemyId] = store.EnemyMaxHealth[evt.EnemyId];
            }

            // Ping-pong swap — clear write buffer
            int writeIdx = 1 - _lifestealEventsIdx;
            _lifestealEvents[writeIdx].Clear();
            _lifestealEventsIdx = writeIdx;
        }
    }
}