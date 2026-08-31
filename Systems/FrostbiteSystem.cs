using System;
using System.Threading.Tasks;
using BattleSystemECS.Core;
using BattleSystemECS.Components;
using BattleSystemECS.Core.GAS;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Frostbite system — non-stacking percentage-of-maxHP DoT.
    ///
    /// Frostbite deals a flat fraction of the target's EnemyMaxHealth per tick.
    /// Distinct from Bleed (stacking, fixed-per-stack damage) — Frostbite's
    /// percent-based damage scales naturally with Boss HP pools, making it a
    /// viable anti-Boss tool that ignores armor/resistance/HP-floor ceilings.
    ///
    /// Applied by Ice-type towers and skills (e.g. "Frostbite" cast).
    /// Each enemy can have at most one Frostbite effect; re-applying refreshes
    /// duration and updates the percentage (max-of-old-new for stacking safety).
    ///
    /// Two-phase pattern: collect frostbite damage in parallel, apply in serial.
    /// </summary>
    public class FrostbiteSystem
    {
        private ComponentStore store;
        private int playerId;

        // Ping-pong double-buffer for frostbite damage (parallel collect → serial apply)
        private (int enemyId, float damage)[] _frostQueue0 = Array.Empty<(int, float)>();
        private (int enemyId, float damage)[] _frostQueue1 = Array.Empty<(int, float)>();
        private int _frostQueueIdx = 0;
        private int _frostQueueCount = 0;
        private readonly object _frostQueueLock = new object();
        // Overflow guard: tracks total dropped tick events (queue full). BleedSystem
        // has the same silent-drop behavior; we expose a counter for future observability.
        private int _overflowDrops = 0;

        public FrostbiteSystem(ComponentStore store, int playerId)
        {
            this.store = store;
            this.playerId = playerId;
            // Pre-allocate queues for 512 frostbite events per frame (avoid per-frame allocation)
            _frostQueue0 = new (int, float)[512];
            _frostQueue1 = new (int, float)[512];
        }

        /// <summary>
        /// Apply frostbite to a target enemy. Re-apply refreshes duration.
        /// If the target is already frostbitten, the new percentage wins if higher
        /// (max-of-old-new for stack-safety).
        /// </summary>
        public void ApplyFrostbite(int targetId, float maxHpPctPerTick, float duration)
        {
            if (targetId < 0 || targetId >= ComponentStore.MAX_ENTITIES) return;
            if (!store.EnemyActive[targetId]) return;
            if (maxHpPctPerTick <= 0f || duration <= 0f) return;

            // Apply resistance (some enemies have partial frostbite resistance)
            float resist = store.EnemyFrostbiteResistance[targetId];
            float effectivePct = maxHpPctPerTick * (1f - resist);
            if (effectivePct <= 0f) return;

            // Max-of-old-new for stack-safety (refresh stronger)
            float currentPct = store.EnemyFrostbiteMaxHpPct[targetId];
            if (effectivePct > currentPct)
            {
                store.EnemyFrostbiteMaxHpPct[targetId] = effectivePct;
            }

            // Refresh duration (frostbite doesn't stack duration, just resets to max)
            store.EnemyFrostbiteDurationLeft[targetId] = duration;

            // Reset tick timer so next tick is full interval away
            if (store.EnemyFrostbiteTimer[targetId] <= 0f)
            {
                store.EnemyFrostbiteTimer[targetId] = 1f;  // next tick in ~1 sec
            }
        }

        /// <summary>
        /// Update all frostbite effects — called in WavePhase after enemy movement.
        /// Ticks frostbite damage and decays frostbite state.
        /// </summary>
        public void Update(float deltaTime)
        {
            var activeEnemyIds = store.GetCachedActiveEnemyIds();

            // Phase 1 (parallel): collect frostbite damage events — no structural mutations
            _frostQueueCount = 0;
            var queue = _frostQueueIdx == 0 ? _frostQueue0 : _frostQueue1;

            Parallel.For(0, activeEnemyIds.Count, ParallelOptionsCache.HotPath, i =>
            {
                int enemyId = activeEnemyIds[i];
                if (!store.EnemyActive[enemyId]) return;

                float pct = store.EnemyFrostbiteMaxHpPct[enemyId];
                if (pct <= 0f) return;

                // Tick frostbite timer
                float timer = store.EnemyFrostbiteTimer[enemyId];
                timer -= deltaTime;

                if (timer <= 0f)
                {
                    // Trigger frostbite tick — queue damage
                    float maxHealth = store.EnemyMaxHealth[enemyId];
                    // Damage = pct * maxHealth (e.g. 0.02 * 1000 = 20)
                    float totalFrostDmg = pct * maxHealth;

                    lock (_frostQueueLock)
                    {
                        if (_frostQueueCount < queue.Length)
                        {
                            queue[_frostQueueCount++] = (enemyId, totalFrostDmg);
                        }
                        else
                        {
                            // Queue full — record overflow. Drop this tick to avoid blocking.
                            // BleedSystem has identical behavior; we expose counters for observability.
                            _overflowDrops++;
                        }
                    }

                    // Note: timer reset is deferred to the "no expiry" branch below to
                    // avoid a redundant 1f → 0f overwrite when the same frame also expires
                    // the frostbite (the expiry branch will zero the timer).
                    timer = 1f;  // local copy: next tick in 1 sec
                }

                // Decay frostbite duration
                float durLeft = store.EnemyFrostbiteDurationLeft[enemyId];
                durLeft -= deltaTime;
                if (durLeft <= 0f)
                {
                    // Frostbite expired — clear all state
                    store.EnemyFrostbiteMaxHpPct[enemyId] = 0f;
                    store.EnemyFrostbiteTimer[enemyId] = 0f;
                    store.EnemyFrostbiteDurationLeft[enemyId] = 0f;
                }
                else
                {
                    store.EnemyFrostbiteDurationLeft[enemyId] = durLeft;
                    if (timer > 0f) store.EnemyFrostbiteTimer[enemyId] = timer;
                }
            });
        }

        /// <summary>
        /// Resolve queued frostbite damage — called after Update, before ResolveEnemiesKilledThisFrame.
        /// Two-phase: swap buffer, then serial apply.
        /// </summary>
        public void ResolveFrostbiteDamage()
        {
            int readIdx = _frostQueueIdx;
            int writeIdx = 1 - _frostQueueIdx;
            _frostQueueIdx = writeIdx;
            // Queue stores value tuples (no GC refs), so no clear needed — the next
            // Update() resets _frostQueueCount = 0 and overwrites indices 0..count-1.

            var readQueue = readIdx == 0 ? _frostQueue0 : _frostQueue1;
            for (int i = 0; i < _frostQueueCount; i++)
            {
                var (enemyId, damage) = readQueue[i];
                if (enemyId < 0 || enemyId >= ComponentStore.MAX_ENTITIES) continue;
                if (!store.EnemyActive[enemyId]) continue;
                float currentHealth = store.EnemyHealth[enemyId];
                if (currentHealth <= 0f) continue;
                // Invulnerability check
                if (store.EnemyIsInvulnerable[enemyId]) continue;

                var source = store.GetEntityHandle(store.PlayerEntityId);
                var target = store.GetEntityHandle(enemyId);
                if (!source.IsValid || !target.IsValid) continue;
                store.DamageResolver.TryApply(new DamageRequest(source, target, damage, DamageType.True, ElementType.Ice, DamageFlags.None, DamageAmountStage.Raw, DamageCommitBoundary.GameplayResolve, store.AllocateGameplaySequence(enemyId), ownerPlayerId: playerId));
            }
            _frostQueueCount = 0;
        }
    }
}
