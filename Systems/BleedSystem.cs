using System;
using System.Threading.Tasks;
using BattleSystemECS.Core;
using BattleSystemECS.Components;
using BattleSystemECS.Core.GAS;

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

        private (int enemyId, float damage)[] _bleedEvents = Array.Empty<(int, float)>();
        private bool[] _hasBleedEvent = Array.Empty<bool>();
        private int _bleedCollectCount;

        public BleedSystem(ComponentStore store, int playerId)
        {
            this.store = store;
            this.playerId = playerId;
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
            int count=activeEnemyIds.Count;
            EnsureCollectCapacity(count);
            Array.Clear(_hasBleedEvent,0,count);
            _bleedCollectCount=count;

            Parallel.For(0, count, ParallelOptionsCache.HotPath, i =>
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

                    _bleedEvents[i]=(enemyId,totalBleedDmg);
                    _hasBleedEvent[i]=true;

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
            for (int i = 0; i < _bleedCollectCount; i++)
            {
                if(!_hasBleedEvent[i])continue;
                var (enemyId, damage) = _bleedEvents[i];
                if (enemyId < 0 || enemyId >= ComponentStore.MAX_ENTITIES) continue;
                if (!store.EnemyActive[enemyId]) continue;
                float currentHealth = store.EnemyHealth[enemyId];
                if (currentHealth <= 0f) continue;
                // Invulnerability check
                if (store.EnemyIsInvulnerable[enemyId]) continue;

                var source = store.GetEntityHandle(store.PlayerEntityId);
                var target = store.GetEntityHandle(enemyId);
                if (!source.IsValid || !target.IsValid) continue;
                store.DamageResolver.TryApply(new DamageRequest(source, target, damage, DamageType.True, ElementType.None, DamageFlags.None, DamageAmountStage.Raw, DamageCommitBoundary.GameplayResolve, store.AllocateGameplaySequence(enemyId), ownerPlayerId: playerId));
            }
            _bleedCollectCount = 0;
        }

        private void EnsureCollectCapacity(int count)
        {
            if(_bleedEvents.Length>=count)return;
            int capacity=Math.Max(count,Math.Max(512,_bleedEvents.Length*2));
            Array.Resize(ref _bleedEvents,capacity);
            Array.Resize(ref _hasBleedEvent,capacity);
        }
    }
}
