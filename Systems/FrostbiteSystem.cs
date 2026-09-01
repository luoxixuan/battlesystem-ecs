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

        private (int enemyId, float damage)[] _frostEvents = Array.Empty<(int, float)>();
        private bool[] _hasFrostEvent = Array.Empty<bool>();
        private int _frostCollectCount;

        public FrostbiteSystem(ComponentStore store, int playerId)
        {
            this.store = store;
            this.playerId = playerId;
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
            int count=activeEnemyIds.Count;
            EnsureCollectCapacity(count);
            Array.Clear(_hasFrostEvent,0,count);
            _frostCollectCount=count;

            Parallel.For(0, count, ParallelOptionsCache.HotPath, i =>
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

                    _frostEvents[i]=(enemyId,totalFrostDmg);
                    _hasFrostEvent[i]=true;

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
            for (int i = 0; i < _frostCollectCount; i++)
            {
                if(!_hasFrostEvent[i])continue;
                var (enemyId, damage) = _frostEvents[i];
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
            _frostCollectCount = 0;
        }

        private void EnsureCollectCapacity(int count)
        {
            if(_frostEvents.Length>=count)return;
            int capacity=Math.Max(count,Math.Max(512,_frostEvents.Length*2));
            Array.Resize(ref _frostEvents,capacity);
            Array.Resize(ref _hasFrostEvent,capacity);
        }
    }
}
