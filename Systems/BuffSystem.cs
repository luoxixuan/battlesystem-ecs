using System;
using System.Collections.Concurrent;
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

        // Ping-pong double-buffer for enemy DoT damage queue
        private ConcurrentBag<(int enemyId, float damage)>[] _dotDamageQueue = new ConcurrentBag<(int, float)>[2];
        private int _dotQueueIdx = 0;

        public BuffSystem(ComponentStore store, int playerId)
        {
            this.store = store;
            this.playerId = playerId;
            _dotDamageQueue[0] = new ConcurrentBag<(int, float)>();
            _dotDamageQueue[1] = new ConcurrentBag<(int, float)>();
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
                if (eff.Definition.Type == EffectType.Instant) continue;

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
                            // Queue DoT damage
                            _dotDamageQueue[_dotQueueIdx].Add((enemyId, eff.Definition.Magnitude));
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
        /// Add a Periodic DoT effect to an entity.
        /// </summary>
        public void ApplyDot(int targetId, GameplayEffectDef dotDef)
        {
            store.AddEffect(targetId, new AppliedEffect(dotDef, playerId));
        }
    }
}