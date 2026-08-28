using System;
using System.Threading.Tasks;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Bleed / Hemorrhage system — stacking physical DoT.
    /// 
    /// Bleed is a stacking DoT that deals percentage-of-maxHP damage per stack per tick.
    /// Distinct from Poison (fixed damage) — bleed scales with target's HP pool.
    /// Applied by Slash/Pierce tower types on hit. Each stack independently ticks and expires.
    /// 
    /// Two-phase pattern: collect bleed damage in parallel, apply in serial.
    /// </summary>
    public class BleedSystem
    {
        private ComponentStore store;
        private int playerId;

        // Ping-pong double-buffer for bleed damage (parallel collect → serial apply)
        private (int enemyId, float damage)[] _bleedQueue0 = Array.Empty<(int, float)>();
        private (int enemyId, float damage)[] _bleedQueue1 = Array.Empty<(int, float)>();
        private int _bleedQueueIdx = 0;
        private int _bleedQueueCount = 0;
        private readonly object _bleedQueueLock = new object();

        public BleedSystem(ComponentStore store, int playerId)
        {
            this.store = store;
            this.playerId = playerId;
            // Pre-allocate queues for 512 bleed events per frame (avoid per-frame allocation)
            _bleedQueue0 = new (int, float)[512];
            _bleedQueue1 = new (int, float)[512];
        }

        /// <summary>
        /// Apply bleed stacks to a target enemy from a tower hit.
        /// Called from TowerAttackSystem during the debuff phase (after damage is queued).
        /// </summary>
        public void ApplyBleedFromTower(int towerId, int targetId, float stacksToApply, float dmgPerStack, float duration)
        {
            if (targetId < 0 || targetId >= ComponentStore.MAX_ENTITIES) return;
            if (!store.EnemyActive[targetId]) return;
            if (stacksToApply <= 0f || dmgPerStack <= 0f || duration <= 0f) return;

            // Boss immunity: Boss = enemy with no max stacks cap (maxStacks = 0)
            // Actually the inverse: enemies with maxStacks == 0 are immune
            float maxStacks = store.EnemyBleedMaxStacks[targetId];
            if (maxStacks == 0f) return;  // Boss / elite resistance

            // Apply resistance (some enemies have partial bleed resistance)
            float resist = store.EnemyBleedResistance[targetId];
            float effectiveStacks = stacksToApply * (1f - resist);
            if (effectiveStacks <= 0f) return;

            // Cap at max stacks (0 = no cap, treated as infinity)
            if (maxStacks > 0f)
            {
                float currentStacks = store.EnemyBleedStacks[targetId];
                if (currentStacks >= maxStacks)
                {
                    // Already at max — just refresh duration if longer
                    if (duration > store.EnemyBleedDurationLeft[targetId])
                    {
                        store.EnemyBleedDurationLeft[targetId] = duration;
                    }
                    return;
                }
                effectiveStacks = Math.Min(effectiveStacks, maxStacks - currentStacks);
            }

            // Add stacks
            store.EnemyBleedStacks[targetId] += effectiveStacks;
            store.EnemyBleedDamagePerStack[targetId] = dmgPerStack;
            // Refresh duration (bleed doesn't stack duration, just resets to max)
            store.EnemyBleedDurationLeft[targetId] = duration;
            // Reset tick timer so next tick is full interval away
            if (store.EnemyBleedTimer[targetId] <= 0f)
            {
                store.EnemyBleedTimer[targetId] = 1f;  // next tick in ~1 sec
            }
        }

        /// <summary>
        /// Update all bleed effects — called in WavePhase after enemy movement.
        /// Ticks bleed damage and decays bleed state.
        /// </summary>
        public void Update(float deltaTime)
        {
            var activeEnemyIds = store.GetCachedActiveEnemyIds();

            // Phase 1 (parallel): collect bleed damage events — no structural mutations
            _bleedQueueCount = 0;
            var queue = _bleedQueueIdx == 0 ? _bleedQueue0 : _bleedQueue1;

            Parallel.For(0, activeEnemyIds.Count, ParallelOptionsCache.HotPath, i =>
            {
                int enemyId = activeEnemyIds[i];
                if (!store.EnemyActive[enemyId]) return;

                float stacks = store.EnemyBleedStacks[enemyId];
                if (stacks <= 0f) return;

                // Tick bleed timer
                float timer = store.EnemyBleedTimer[enemyId];
                timer -= deltaTime;

                if (timer <= 0f)
                {
                    // Trigger bleed tick — queue damage
                    float dmgPerStack = store.EnemyBleedDamagePerStack[enemyId];
                    float maxHealth = store.EnemyMaxHealth[enemyId];
                    // Damage = stacks * dmgPerStack * maxHealth
                    float totalBleedDmg = stacks * dmgPerStack * maxHealth;

                    lock (_bleedQueueLock)
                    {
                        if (_bleedQueueCount < queue.Length)
                        {
                            queue[_bleedQueueCount++] = (enemyId, totalBleedDmg);
                        }
                    }

                    // Reset timer for next tick (default 1 sec interval)
                    store.EnemyBleedTimer[enemyId] = 1f;
                }
                else
                {
                    store.EnemyBleedTimer[enemyId] = timer;
                }

                // Decay bleed duration
                float durLeft = store.EnemyBleedDurationLeft[enemyId];
                durLeft -= deltaTime;
                if (durLeft <= 0f)
                {
                    // Bleed expired — clear stacks
                    store.EnemyBleedStacks[enemyId] = 0f;
                    store.EnemyBleedTimer[enemyId] = 0f;
                }
                else
                {
                    store.EnemyBleedDurationLeft[enemyId] = durLeft;
                }
            });
        }

        /// <summary>
        /// Resolve queued bleed damage — called after Update, before ResolveEnemiesKilledThisFrame.
        /// Two-phase: swap buffer, then serial apply.
        /// </summary>
        public void ResolveBleedDamage()
        {
            int readIdx = _bleedQueueIdx;
            int writeIdx = 1 - _bleedQueueIdx;
            _bleedQueueIdx = writeIdx;
            // Clear write buffer (set count to 0)
            if (writeIdx == 0) Array.Clear(_bleedQueue0, 0, _bleedQueue0.Length);
            else Array.Clear(_bleedQueue1, 0, _bleedQueue1.Length);

            var readQueue = readIdx == 0 ? _bleedQueue0 : _bleedQueue1;
            for (int i = 0; i < _bleedQueueCount; i++)
            {
                var (enemyId, damage) = readQueue[i];
                if (enemyId < 0 || enemyId >= ComponentStore.MAX_ENTITIES) continue;
                if (!store.EnemyActive[enemyId]) continue;
                float currentHealth = store.EnemyHealth[enemyId];
                if (currentHealth <= 0f) continue;
                // Invulnerability check
                if (store.EnemyIsInvulnerable[enemyId]) continue;

                store.EnemyHealth[enemyId] -= damage;
                if (store.EnemyHealth[enemyId] <= 0f)
                {
                    store.QueueEnemyDeath(enemyId, playerId);
                }
            }
            _bleedQueueCount = 0;
        }
    }
}