using System;
using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Buff/Effect tracking system.
    /// Updates Periodic (DoT) and Duration effects each frame:
    /// - Periodic: ticks damage on interval, decrements TicksRemaining
    /// - Duration: counts down RemainingTime, expires at zero
    /// 
    /// Uses two-phase pattern (parallel collect → serial apply) for enemy-targeted effects.
    /// </summary>
    public class BuffSystem
    {
        private ComponentStore store;
        private int playerId;
        private IRenderer renderer;

        // Ping-pong double-buffer for enemy DoT damage queue
        private List<(int enemyId, float damage, EntityHandle source, EffectId effect, long sequence)>[] _dotDamageQueue = new List<(int, float, EntityHandle, EffectId, long)>[2];
        private readonly object _dotDamageQueueLock = new object();
        private int _dotQueueIdx = 0;

        public BuffSystem(ComponentStore store, int playerId, IRenderer renderer = null)
        {
            this.store = store;
            this.playerId = playerId;
            this.renderer = renderer;
            _dotDamageQueue[0] = new List<(int, float, EntityHandle, EffectId, long)>(128);
            _dotDamageQueue[1] = new List<(int, float, EntityHandle, EffectId, long)>(128);
        }

        /// <summary>
        /// Main update: tick all active Periodic/Duration effects.
        /// Must be called after all attack systems have queued damage,
        /// but before ResolveDotDamage (which resolves enemy deaths).
        /// </summary>
        public void Update(float deltaTime)
        {
            Update(deltaTime, ClockId.Combat);
        }

        public void Update(float deltaTime, ClockId clock)
        {
            ProcessPlayerEffects(deltaTime, clock);
            ProcessEnemyEffects(deltaTime, clock);
        }

        private void ProcessPlayerEffects(float deltaTime, ClockId clock)
        {
            int count = store.GetEffectCount(playerId);
            for (int slot = 0; slot < count; slot++)
            {
                if (!ProcessEffectAt(playerId, slot, deltaTime, clock, false)) { count--; slot--; }
            }
        }

        private void ProcessEnemyEffects(float deltaTime, ClockId clock)
        {
            // Iterate all active enemies and tick their Periodic/Duration effects
            // Only tick enemies that have active effects — check ActiveEffectCount first
            var activeEnemyIds = store.GetCachedActiveEnemyIds();
            foreach (int enemyId in activeEnemyIds)
            {
                int count = store.GetEffectCount(enemyId);
                if (count == 0) continue;

                for (int slot = 0; slot < count; slot++)
                {
                    if (!ProcessEffectAt(enemyId, slot, deltaTime, clock, true)) { count--; slot--; }
                }
            }
        }

        private bool ProcessEffectAt(int entityId, int slot, float deltaTime, ClockId clock, bool queueDamage)
        {
            if (!store.TryGetActiveEffectAt(entityId, slot, out var active, out var definition, out _)) return true;
            // typed GameplayEffectRuntime 是 RuntimeOwned effect 的唯一 owner；legacy facade 只读 projection。
            if (active.RuntimeOwned) return true;
            if (active.SourceDeath == SourceDeathPolicy.Remove && !store.TryResolve(active.Source, out _, out _))
            {
                RemoveEffectAtSlot(entityId, slot);
                return false;
            }
            if (definition.Type == EffectType.Instant || definition.Type == EffectType.Heal || active.Clock != clock) return true;

            if (definition.Type == EffectType.Periodic)
            {
                int ticks = AdvancePeriodic(ref active, definition, deltaTime);
                if (queueDamage && ticks > 0)
                {
                    float damage = active.CapturedMagnitude * active.StackCount;
                    for (int i = 0; i < ticks; i++)
                        _dotDamageQueue[_dotQueueIdx].Add((entityId, damage, active.Source, active.DefinitionId,
                            store.AllocateGameplaySequence(entityId)));
                }
            }
            active.RemainingTime = Math.Max(0f, active.RemainingTime - deltaTime);

            if (active.RemainingTime <= 0f)
            {
                RemoveEffectAtSlot(entityId, slot);
                return false;
            }
            store.TryUpdateActiveEffect(store.GetEntityHandle(entityId), active);
            return true;
        }

        private static int AdvancePeriodic(ref ActiveGameplayEffect active, GameplayEffectDefinition definition, float deltaTime)
        {
            int available = active.TicksRemaining;
            if (available <= 0 || definition.Period <= 0f) return 0;
            active.TickAccumulator += deltaTime;
            int due = 0;
            if (active.FirstTickPending)
            {
                active.FirstTickPending = false;
                if (active.FirstTick == FirstTickPolicy.Immediate) due = 1;
            }
            if (active.CatchUp == CatchUpPolicy.CatchUpAll)
            {
                while (due < available && active.TickAccumulator >= definition.Period)
                {
                    active.TickAccumulator -= definition.Period;
                    due++;
                }
            }
            else if (due == 0 && active.TickAccumulator >= definition.Period)
            {
                due = 1;
                if (active.CatchUp == CatchUpPolicy.SkipMissed) active.TickAccumulator %= definition.Period;
                else active.TickAccumulator -= definition.Period;
            }
            if (due > available) due = available;
            active.TicksRemaining -= due;
            return due;
        }

        /// <summary>
        /// Resolve DoT damage queued this frame. Call after ProcessEnemyEffects, before ResolveEnemiesKilledThisFrame.
        /// </summary>
        public void ResolveDotDamage()
        {
            int readIdx = _dotQueueIdx;
            int writeIdx = 1 - _dotQueueIdx;
            _dotQueueIdx = writeIdx;
            _dotDamageQueue[writeIdx].Clear();

            foreach (var (enemyId, damage, source, effect, sequence) in _dotDamageQueue[readIdx])
            {
                if (enemyId < 0 || enemyId >= ComponentStore.MAX_ENTITIES) continue;
                float currentHealth = store.EnemyHealth[enemyId];
                if (currentHealth <= 0f) continue;
                // Invulnerability check: skip damage if enemy is invulnerable
                if (store.EnemyIsInvulnerable[enemyId]) continue;

                var handle = store.GetEntityHandle(enemyId);
                var damageSource = source.IsValid ? source : handle;
                var request = new DamageRequest(damageSource, handle, damage, DamageType.True,
                    ElementType.Poison, DamageFlags.None, DamageAmountStage.Raw,
                    DamageCommitBoundary.GameplayResolve,
                    sequence, effect: effect,
                    ownerPlayerId: playerId);
                store.DamageResolver.TryApply(request);
            }
        }

        private void RemoveEffectAtSlot(int entityId, int slot)
        {
            store.TryRemoveActiveEffectAt(entityId, slot, out _, out _, out _);
        }

        /// <summary>
        /// Convenience overload: apply a Firewall burn DoT with raw damage/duration params.
        /// Creates a StackingBehavior.None GameplayEffectDef internally.
        /// </summary>
        public void ApplyDot(int targetId, float damagePerTick, int duration)
        {
            if (targetId < 0 || targetId >= ComponentStore.MAX_ENTITIES) return;
            if (damagePerTick <= 0f || duration <= 0) return;
            // Firewall DoT: 1 tick per second, name encodes tower type
            var dotDef = new GameplayEffectDef(
                name: "Firewall_Burn",
                type: EffectType.Periodic,
                attrIdx: -1,
                op: AttributeModifierOp.Add,
                magnitude: damagePerTick,
                duration: duration
            );
            dotDef.TotalTicks = duration;
            dotDef.TickInterval = 1f;
            dotDef.StackingBehavior = StackingBehavior.None;
            ApplyDot(targetId, dotDef);
        }

        /// <summary>
        /// Add a Periodic DoT effect to an entity with stacking support.
        /// Implements stacking behaviors:
        /// - None: replaces any existing effect of the same name
        /// - DurationRefresh: refreshes duration only (no stacking)
        /// - MaxStacks: stacks up to MaxStacks, no duration refresh
        /// - MaxStacksRefresh: stacks up to MaxStacks, refreshes duration on each application
        /// </summary>
        public void ApplyDot(int targetId, GameplayEffectDef dotDef)
        {
            var target = store.GetEntityHandle(targetId);
            var source = store.GetEntityHandle(playerId);
            // Minimal unit worlds may not materialize a player entity; production worlds do.
            // Preserve a valid self-source only as a compatibility fallback for those worlds.
            if (!source.IsValid) return;
            var application = LegacyEffectAdapter.CreateApplication(dotDef, source, target);
            // Fast path: StackingBehavior.None skips the search — same as old O(1) behavior
            if (dotDef.StackingBehavior == StackingBehavior.None)
            {
                store.TryAddGameplayEffect(targetId, application, out _);
                return;
            }

            int count = store.GetEffectCount(targetId);
            for (int slot = 0; slot < count; slot++)
            {
                if (!store.TryGetActiveEffectAt(targetId, slot, out var existing, out var definition, out var snapshot)) continue;
                if (!string.Equals(snapshot.Name, dotDef.Name, StringComparison.Ordinal)) continue;
                if (definition.Type != EffectType.Periodic) continue;

                switch (dotDef.StackingBehavior)
                {
                    case StackingBehavior.DurationRefresh:
                        // Refresh duration only, keep existing stacks
                        existing.RemainingTime = application.Runtime.RemainingTime;
                        existing.TicksRemaining = application.Runtime.TicksRemaining;
                        existing.TickAccumulator = 0f;
                        existing.FirstTickPending = true;
                        store.TryUpdateActiveEffect(target, existing);
                        return;

                    case StackingBehavior.MaxStacks:
                        // Stack up to MaxStacks, no duration refresh
                        if (existing.StackCount < dotDef.MaxStacks)
                        {
                            existing.StackCount++;
                            store.TryUpdateActiveEffect(target, existing);
                        }
                        return;

                    case StackingBehavior.MaxStacksRefresh:
                        // Stack up to MaxStacks, refresh duration on each application
                        if (existing.StackCount < dotDef.MaxStacks)
                        {
                            existing.StackCount++;
                        }
                        existing.RemainingTime = application.Runtime.RemainingTime;
                        existing.TicksRemaining = application.Runtime.TicksRemaining;
                        existing.TickAccumulator = 0f;
                        existing.FirstTickPending = true;
                        store.TryUpdateActiveEffect(target, existing);
                        return;
                }
                return;
            }
            // No existing effect found — add new one
            store.TryAddGameplayEffect(targetId, application, out _);
        }

        /// <summary>
        /// Heal the player by a percent of max health. Caps at max health.
        /// </summary>
        public void HealPlayer(float healPercent)
        {
            if (playerId < 0 || playerId >= ComponentStore.MAX_PLAYERS) return;
            float maxHealth = store.GetPlayerMaxHealth(playerId);
            float currentHealth = store.GetPlayerCurrentHealth(playerId);
            float healAmount = maxHealth * healPercent;
            float newHealth = System.Math.Min(currentHealth + healAmount, maxHealth);
            store.SetPlayerCurrentHealth(playerId, newHealth);
            renderer?.Log($"[BUFF] HealPlayer: +{healAmount:F1} HP ({currentHealth:F1} -> {newHealth:F1})");
        }
    }
}
