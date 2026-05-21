using System;
using System.Collections.Generic;
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
        private List<(int enemyId, float damage)>[] _dotDamageQueue = new List<(int, float)>[2];
        private readonly object _dotDamageQueueLock = new object();
        private int _dotQueueIdx = 0;

        public BuffSystem(ComponentStore store, int playerId, IRenderer renderer = null)
        {
            this.store = store;
            this.playerId = playerId;
            this.renderer = renderer;
            _dotDamageQueue[0] = new List<(int, float)>(128);
            _dotDamageQueue[1] = new List<(int, float)>(128);
        }

        /// <summary>
        /// Main update: tick all active Periodic/Duration effects.
        /// Must be called after all attack systems have queued damage,
        /// but before ResolveDotDamage (which resolves enemy deaths).
        /// </summary>
        public void Update(float deltaTime)
        {
            ProcessPlayerEffects(deltaTime);
            ProcessEnemyEffects(deltaTime);
        }

        private void ProcessPlayerEffects(float deltaTime)
        {
            int count = store.GetEffectCount(playerId);
            for (int slot = 0; slot < count; slot++)
            {
                var eff = store.GetEffect(playerId, slot);
                if (eff.Definition.Type == EffectType.Instant || eff.Definition.Type == EffectType.Heal)
                {
                    // Instant and Heal effects are handled separately (Heal via SkillSystem casting, Instant already applied)
                    continue;
                }

                if (eff.Definition.Type == EffectType.Periodic)
                {
                    eff.TimeSinceLastTick += deltaTime;
                    while (eff.TimeSinceLastTick >= eff.Definition.TickInterval && eff.Definition.TicksRemaining > 0)
                    {
                        eff.TimeSinceLastTick -= eff.Definition.TickInterval;
                        eff.Definition.TicksRemaining--;
                        // Periodic DoT on enemies — find affected enemies in range
                        // For now, Poison Nova already applied initial hit; periodic tick goes through damage queue
                    }
                    eff.Definition.RemainingTime = Math.Max(0f, eff.Definition.RemainingTime - deltaTime);
                }
                else if (eff.Definition.Type == EffectType.Duration)
                {
                    eff.Definition.RemainingTime = Math.Max(0f, eff.Definition.RemainingTime - deltaTime);
                }

                store.SetEffect(playerId, slot, eff);

                // Expire expired effects
                if (eff.Definition.RemainingTime <= 0f)
                {
                    RemoveEffectAtSlot(playerId, slot);
                    count--;
                    slot--;
                }
            }
        }

        private void ProcessEnemyEffects(float deltaTime)
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
                    var eff = store.GetEffect(enemyId, slot);
                    if (eff.Definition.Type == EffectType.Instant) continue;

                    if (eff.Definition.Type == EffectType.Periodic)
                    {
                        eff.TimeSinceLastTick += deltaTime;
                        while (eff.TimeSinceLastTick >= eff.Definition.TickInterval && eff.Definition.TicksRemaining > 0)
                        {
                            eff.TimeSinceLastTick -= eff.Definition.TickInterval;
                            eff.Definition.TicksRemaining--;
                            // Queue DoT damage (multiplied by current stack count)
                            float stackedDamage = eff.Definition.Magnitude * eff.StackCount;
                            _dotDamageQueue[_dotQueueIdx].Add((enemyId, stackedDamage));
                        }
                        eff.Definition.RemainingTime = Math.Max(0f, eff.Definition.RemainingTime - deltaTime);
                    }
                    else if (eff.Definition.Type == EffectType.Duration)
                    {
                        eff.Definition.RemainingTime = Math.Max(0f, eff.Definition.RemainingTime - deltaTime);
                    }

                    store.SetEffect(enemyId, slot, eff);

                    if (eff.Definition.RemainingTime <= 0f)
                    {
                        RemoveEffectAtSlot(enemyId, slot);
                        count--;
                        slot--;
                    }
                }
            }
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

            foreach (var (enemyId, damage) in _dotDamageQueue[readIdx])
            {
                if (enemyId < 0 || enemyId >= ComponentStore.MAX_ENTITIES) continue;
                float currentHealth = store.EnemyHealth[enemyId];
                if (currentHealth <= 0f) continue;

                store.EnemyHealth[enemyId] -= damage;
                if (store.EnemyHealth[enemyId] <= 0f)
                    store.QueueEnemyDeath(enemyId, playerId);
            }
        }

        private void RemoveEffectAtSlot(int entityId, int slot)
        {
            // Shift remaining effects down to fill the gap
            int count = store.GetEffectCount(entityId);
            for (int i = slot; i < count - 1; i++)
            {
                var next = store.GetEffect(entityId, i + 1);
                store.SetEffect(entityId, i, next);
            }
            // Clear last slot and decrement count
            store.SetEffect(entityId, count - 1, default);
            // Decrement count via reflection-free approach: need a helper
            DecrementEffectCount(entityId);
        }

        private void DecrementEffectCount(int entityId)
        {
            // ActiveEffectCount lives in the store; expose a setter
            int newCount = store.GetEffectCount(entityId) - 1;
            if (newCount >= 0)
                store.SetEffectCount(entityId, newCount);
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
            // Fast path: StackingBehavior.None skips the search — same as old O(1) behavior
            if (dotDef.StackingBehavior == StackingBehavior.None)
            {
                store.AddEffect(targetId, new AppliedEffect(dotDef, playerId));
                return;
            }

            int count = store.GetEffectCount(targetId);
            for (int slot = 0; slot < count; slot++)
            {
                var existing = store.GetEffect(targetId, slot);
                if (existing.Definition.Name != dotDef.Name) continue;
                if (existing.Definition.Type != EffectType.Periodic) continue;

                switch (dotDef.StackingBehavior)
                {
                    case StackingBehavior.DurationRefresh:
                        // Refresh duration only, keep existing stacks
                        existing.Definition.RemainingTime = dotDef.Duration;
                        existing.Definition.TicksRemaining = dotDef.TotalTicks;
                        existing.TimeSinceLastTick = 0f;
                        store.SetEffect(targetId, slot, existing);
                        return;

                    case StackingBehavior.MaxStacks:
                        // Stack up to MaxStacks, no duration refresh
                        if (existing.StackCount < dotDef.MaxStacks)
                        {
                            existing.StackCount++;
                            store.SetEffect(targetId, slot, existing);
                        }
                        return;

                    case StackingBehavior.MaxStacksRefresh:
                        // Stack up to MaxStacks, refresh duration on each application
                        if (existing.StackCount < dotDef.MaxStacks)
                        {
                            existing.StackCount++;
                        }
                        existing.Definition.RemainingTime = dotDef.Duration;
                        existing.Definition.TicksRemaining = dotDef.TotalTicks;
                        existing.TimeSinceLastTick = 0f;
                        store.SetEffect(targetId, slot, existing);
                        return;
                }
                return;
            }
            // No existing effect found — add new one
            store.AddEffect(targetId, new AppliedEffect(dotDef, playerId));
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