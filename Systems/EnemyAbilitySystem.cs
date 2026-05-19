using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Components;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Enemy ability execution system.
    /// Handles enemy-cast abilities: self_heal, aoe_damage, buff_allies.
    /// Two-phase pattern: parallel collection → serial apply.
    /// </summary>
    public class EnemyAbilitySystem
    {
        private readonly ComponentStore store;
        private readonly IRenderer logger;
        private readonly int playerId;
        private readonly GameConfig gameConfig;
        private readonly Dictionary<string, EnemyAbilityDef> _abilityLookup;

        // Ping-pong double-buffer for ability events — collected parallel, applied serial.
        private ConcurrentBag<AbilityEvent>[] _abilityEvents = new ConcurrentBag<AbilityEvent>[2];
        private int _abilityEventsIdx = 0;

        // Per-ability cooldown tracking — keyed by enemyId * MAX_ABILITIES_PER_ENTITY + slot
        private readonly float[] _abilityCooldownTimers = new float[ComponentStore.MAX_ENTITIES * ComponentStore.MAX_ABILITIES_PER_ENTITY];

        public EnemyAbilitySystem(ComponentStore store, IRenderer logger, int playerId, GameConfig gameConfig)
        {
            this.store = store;
            this.logger = logger;
            this.playerId = playerId;
            this.gameConfig = gameConfig;

            // Build ability lookup from config
            _abilityLookup = new Dictionary<string, EnemyAbilityDef>();
            if (gameConfig.EnemyAbilities != null)
            {
                foreach (var ab in gameConfig.EnemyAbilities)
                {
                    if (!string.IsNullOrEmpty(ab.Id))
                        _abilityLookup[ab.Id] = ab;
                }
            }

            _abilityEvents[0] = new ConcurrentBag<AbilityEvent>();
            _abilityEvents[1] = new ConcurrentBag<AbilityEvent>();
        }

        /// <summary>
        /// Reset cooldowns for a new turn.
        /// </summary>
        public void SetTurn(int turn)
        {
        }

        /// <summary>
        /// Enqueue an enemy ability event from BT evaluation (called during EnemyAISystem serial phase).
        /// </summary>
        public void EnqueueAbility(int enemyId, string abilityId)
        {
            if (enemyId < 0 || enemyId >= ComponentStore.MAX_ENTITIES) return;
            if (!_abilityLookup.TryGetValue(abilityId, out var ability)) return;

            int timerIdx = enemyId * ComponentStore.MAX_ABILITIES_PER_ENTITY;
            if (_abilityCooldownTimers[timerIdx] > 0f) return;

            _abilityEvents[_abilityEventsIdx].Add(new AbilityEvent
            {
                EnemyId = enemyId,
                Ability = ability
            });
        }

        /// <summary>
        /// Decrement cooldown timers for active enemies with abilities. Called once per turn from GameManager.
        /// Each enemy uses slot 0 of _abilityCooldownTimers.
        /// </summary>
        public void UpdateCooldowns(float deltaTime)
        {
            var activeEnemyIds = store.GetCachedActiveEnemyIds();
            foreach (var enemyId in activeEnemyIds)
            {
                int idx = enemyId * ComponentStore.MAX_ABILITIES_PER_ENTITY; // slot 0
                if (_abilityCooldownTimers[idx] > 0f)
                    _abilityCooldownTimers[idx] -= deltaTime;
            }
        }

        /// <summary>
        /// Serial phase: execute all queued ability events in order.
        /// </summary>
        public void ExecuteAbilities()
        {
            int readIdx = _abilityEventsIdx;
            foreach (var evt in _abilityEvents[readIdx])
            {
                ExecuteAbility(evt.EnemyId, evt.Ability);
            }

            // Ping-pong swap
            int writeIdx = 1 - _abilityEventsIdx;
            _abilityEvents[writeIdx].Clear();
            _abilityEventsIdx = writeIdx;
        }

        private void ExecuteAbility(int enemyId, EnemyAbilityDef ability)
        {
            switch (ability.AbilityType)
            {
                case "self_heal":
                    ExecuteSelfHeal(enemyId, ability);
                    break;
                case "aoe_damage":
                    ExecuteAoeDamage(enemyId, ability);
                    break;
                case "buff_allies":
                    ExecuteBuffAllies(enemyId, ability);
                    break;
            }

            int timerIdx = enemyId * ComponentStore.MAX_ABILITIES_PER_ENTITY;
            _abilityCooldownTimers[timerIdx] = ability.Cooldown;
        }

        private void ExecuteSelfHeal(int enemyId, EnemyAbilityDef ability)
        {
            if (!store.EnemyActive[enemyId]) return;

            float maxHealth = store.EnemyMaxHealth[enemyId];
            float healAmount = maxHealth * ability.HealAmount;
            float newHealth = store.EnemyHealth[enemyId] + healAmount;
            store.EnemyHealth[enemyId] = Math.Min(newHealth, maxHealth);

            logger.Log($"[ABILITY] Enemy {enemyId} heals for {healAmount:F1} HP ({ability.Name})");
        }

        private void ExecuteAoeDamage(int enemyId, EnemyAbilityDef ability)
        {
            float enemyX = store.PositionX[enemyId];
            float enemyY = store.PositionY[enemyId];
            float playerX = store.PositionX[playerId];
            float playerY = store.PositionY[playerId];

            float dist = Math.Abs(enemyX - playerX) + Math.Abs(enemyY - playerY);
            bool inRange = ability.AoeRadius <= 0 || dist <= ability.AoeRadius;

            if (inRange)
            {
                float baseDamage = store.EnemyDamage[enemyId];
                float aoeDamage = baseDamage * ability.DamageMultiplier;

                store.DecreasePlayerHealth(playerId, aoeDamage);
                float remaining = store.GetPlayerCurrentHealth(playerId);

                EventBus.Instance.Publish(GameEvents.PlayerDamaged, new PlayerDamagedEvent
                {
                    Damage = aoeDamage,
                    RemainingHealth = remaining,
                    AttackerId = enemyId
                });

                logger.Log($"[ABILITY] Enemy {enemyId} AOE hits player for {aoeDamage:F1} damage ({ability.Name}). HP: {remaining:F1}");
            }
            else
            {
                logger.Log($"[ABILITY] Enemy {enemyId} AOE missed (player out of range, dist={dist:F1})");
            }
        }

        private void ExecuteBuffAllies(int enemyId, EnemyAbilityDef ability)
        {
            if (ability.AoeRadius <= 0) return;

            float enemyX = store.PositionX[enemyId];
            float enemyY = store.PositionY[enemyId];

            var activeEnemyIds = store.GetCachedActiveEnemyIds();
            int buffedCount = 0;

            foreach (var allyId in activeEnemyIds)
            {
                if (!store.EnemyActive[allyId]) continue;
                if (allyId == enemyId) continue;

                float allyX = store.PositionX[allyId];
                float allyY = store.PositionY[allyId];
                float dist = Math.Abs(enemyX - allyX) + Math.Abs(enemyY - allyY);

                if (dist <= ability.AoeRadius)
                {
                    float currentBuff = store.EnemyBuffDamageBonus[allyId];
                    float buffDamageBonus = store.EnemyDamage[allyId] * 0.3f;

                    if (currentBuff >= 0)
                    {
                        store.EnemyBuffDamageBonus[allyId] = buffDamageBonus;
                        store.EnemySpawnFrame[allyId] = ability.BuffDuration;
                        buffedCount++;
                    }
                }
            }

            if (buffedCount > 0)
            {
                logger.Log($"[ABILITY] Enemy {enemyId} buffs {buffedCount} allies with {ability.BuffStat} for {ability.BuffDuration} turns");
            }
        }

        private struct AbilityEvent
        {
            public int EnemyId;
            public EnemyAbilityDef Ability;
        }
    }
}
