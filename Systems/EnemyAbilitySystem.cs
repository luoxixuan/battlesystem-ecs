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
        private TelegraphSystem _telegraphSystem;

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
        /// Inject TelegraphSystem reference for warning zone queuing.
        /// </summary>
        public void SetTelegraphSystem(TelegraphSystem telegraphSystem)
        {
            _telegraphSystem = telegraphSystem;
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
                case "stun_aoe":
                    ExecuteStunAoe(enemyId, ability);
                    break;
                case "slow_aoe":
                    ExecuteSlowAoe(enemyId, ability);
                    break;
                case "heal_allies":
                    ExecuteHealAllies(enemyId, ability);
                    break;
                case "stealth_attack":
                    ExecuteStealthAttack(enemyId, ability);
                    break;
                case "summon_minion":
                    ExecuteSummonMinion(enemyId, ability);
                    break;
                default:
                    // Unknown ability type — log and set cooldown to prevent infinite retry
                    logger.Log($"[ABILITY] Unknown ability type '{ability.AbilityType}' on enemy {enemyId}, ignoring");
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

                // Queue as telegraph zone if telegraph duration > 0, otherwise instant damage
                if (_telegraphSystem != null && ability.TelegraphDuration > 0f)
                {
                    _telegraphSystem.QueueTelegraphZone(
                        enemyId,
                        playerX, playerY,
                        ability.AoeRadius,
                        ability.TelegraphDuration,
                        aoeDamage,
                        playerId,
                        TelegraphSystem.SHAPE_CIRCLE,
                        60f, 0f,
                        ability.TelegraphColor);
                    logger.Log($"[ABILITY] Enemy {enemyId} AOE telegraph zone queued for {ability.TelegraphDuration:F0} turns, damage={aoeDamage:F1} ({ability.Name})");
                }
                else
                {
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
                    float buffDamageBonus = store.EnemyDamage[allyId] * ability.DamageMultiplier;

                    if (currentBuff >= 0)
                    {
                        store.EnemyBuffDamageBonus[allyId] = buffDamageBonus;
                        store.EnemyBuffDurationLeft[allyId] = ability.BuffDuration;
                        buffedCount++;
                    }
                }
            }

            if (buffedCount > 0)
            {
                logger.Log($"[ABILITY] Enemy {enemyId} buffs {buffedCount} allies with {ability.BuffStat} for {ability.BuffDuration} turns");
            }
        }

        private void ExecuteStunAoe(int enemyId, EnemyAbilityDef ability)
        {
            if (ability.AoeRadius <= 0 || ability.StunDuration <= 0) return;

            float enemyX = store.PositionX[enemyId];
            float enemyY = store.PositionY[enemyId];
            float playerX = store.PositionX[playerId];
            float playerY = store.PositionY[playerId];

            float dist = Math.Abs(enemyX - playerX) + Math.Abs(enemyY - playerY);
            if (dist > ability.AoeRadius) return;

            store.ApplyPlayerStun(playerId, ability.StunDuration);
            logger.Log($"[ABILITY] Enemy {enemyId} stuns player for {ability.StunDuration} turn(s) ({ability.Name})");
        }

        private void ExecuteSlowAoe(int enemyId, EnemyAbilityDef ability)
        {
            if (ability.AoeRadius <= 0 || ability.SlowFactor <= 0f || ability.SlowDuration <= 0) return;

            float enemyX = store.PositionX[enemyId];
            float enemyY = store.PositionY[enemyId];
            float playerX = store.PositionX[playerId];
            float playerY = store.PositionY[playerId];

            float dist = Math.Abs(enemyX - playerX) + Math.Abs(enemyY - playerY);
            if (dist > ability.AoeRadius) return;

            store.ApplyPlayerSlow(playerId, ability.SlowFactor, ability.SlowDuration);
            logger.Log($"[ABILITY] Enemy {enemyId} slows player by {((1f - ability.SlowFactor) * 100):F0}% for {ability.SlowDuration} turn(s) ({ability.Name})");
        }

        private void ExecuteHealAllies(int enemyId, EnemyAbilityDef ability)
        {
            if (ability.AoeRadius <= 0) return;

            float enemyX = store.PositionX[enemyId];
            float enemyY = store.PositionY[enemyId];

            var activeEnemyIds = store.GetCachedActiveEnemyIds();
            int healedCount = 0;

            foreach (var allyId in activeEnemyIds)
            {
                if (!store.EnemyActive[allyId]) continue;
                if (allyId == enemyId) continue;

                float allyX = store.PositionX[allyId];
                float allyY = store.PositionY[allyId];
                float dist = Math.Abs(enemyX - allyX) + Math.Abs(enemyY - allyY);

                if (dist <= ability.AoeRadius)
                {
                    float maxHealth = store.EnemyMaxHealth[allyId];
                    float healAmount = maxHealth * ability.HealAmount;
                    float newHealth = store.EnemyHealth[allyId] + healAmount;
                    store.EnemyHealth[allyId] = Math.Min(newHealth, maxHealth);
                    healedCount++;
                }
            }

            if (healedCount > 0)
            {
                logger.Log($"[ABILITY] Enemy {enemyId} heals {healedCount} allies for {ability.HealAmount * 100:F0}% max HP each ({ability.Name})");
            }
        }

        private void ExecuteStealthAttack(int enemyId, EnemyAbilityDef ability)
        {
            // Stealth attack: enhanced damage when attacking from stealth.
            // Set the EnemyStealthMultiplier so the next attack in EnemyAISystem applies extra damage.
            // EnemyStealthMultiplier is a dedicated field (not shared with EnemyBuffDamageBonus).
            if (ability.DamageMultiplier <= 0f) return;

            // Use Math.Max to preserve the strongest stealth bonus if multiple stealth_attack
            // abilities fire in quick succession.
            float existingMult = store.EnemyStealthMultiplier[enemyId];
            store.EnemyStealthMultiplier[enemyId] = Math.Max(existingMult, ability.DamageMultiplier);
            logger.Log($"[ABILITY] Enemy {enemyId} prepares stealth attack with {store.EnemyStealthMultiplier[enemyId]:F1}x damage multiplier ({ability.Name})");
        }

        private void ExecuteSummonMinion(int enemyId, EnemyAbilityDef ability)
        {
            // Summon a weak minion at the enemy's position.
            // Note: Creates a minimal entity with Normal type so it participates in active enemy iteration.
            // The minion will use default stats (0) and will be killed quickly.
            // Full implementation would require proper entity initialization through WaveSpawningSystem.
            float enemyX = store.PositionX[enemyId];
            float enemyY = store.PositionY[enemyId];

            int minionId = store.CreateEntity();
            if (minionId < 0) return;

            // Set minion properties (30% of summoner's stats by default)
            float healthMult = ability.MinionHealthMult > 0 ? ability.MinionHealthMult : 0.3f;
            float damageMult = ability.MinionDamageMult > 0 ? ability.MinionDamageMult : 0.3f;
            float baseHealth = store.EnemyMaxHealth[enemyId];
            float baseDamage = store.EnemyDamage[enemyId];

            store.EnemyHealth[minionId] = baseHealth * healthMult;
            store.EnemyMaxHealth[minionId] = baseHealth * healthMult;
            store.EnemyDamage[minionId] = baseDamage * damageMult;
            store.EnemyMoveSpeed[minionId] = store.EnemyMoveSpeed[enemyId];
            store.EnemyGoldReward[minionId] = Math.Max(1, store.EnemyGoldReward[enemyId] / 3);
            store.EnemyWaveNumber[minionId] = store.EnemyWaveNumber[enemyId];
            store.EnemyActive[minionId] = true;
            store.EnemyTypeName[minionId] = "Normal";
            store.PositionX[minionId] = enemyX;
            store.PositionY[minionId] = enemyY;
            store.PositionActive[minionId] = true;
            store.SetEntityName(minionId, $"Minion_{minionId}");
            // Add to active enemy list so minion is visible to TowerAttackSystem, EnemyMovementSystem, etc.
            store.AddActiveEnemyId(minionId);

            logger.Log($"[ABILITY] Enemy {enemyId} summons minion {minionId} (HP: {baseHealth * healthMult:F0}, DMG: {baseDamage * damageMult:F0}) ({ability.Name})");
        }

        /// <summary>
        /// Called once per turn from GameManager.Run(). Decrements buff_allies durations and clears expired buffs.
        /// Does NOT touch EnemySlowDurationLeft — that is managed by ComponentStore.DecrementEnemySlowDurations().
        /// </summary>
        public void Update()
        {
            var activeEnemyIds = store.GetCachedActiveEnemyIds();
            foreach (var enemyId in activeEnemyIds)
            {
                if (!store.EnemyActive[enemyId]) continue;
                float remaining = store.EnemyBuffDurationLeft[enemyId];
                if (remaining <= 0f) continue;

                store.EnemyBuffDurationLeft[enemyId] = remaining - 1f;
                if (store.EnemyBuffDurationLeft[enemyId] <= 0f)
                {
                    store.EnemyBuffDamageBonus[enemyId] = 0f;
                    store.EnemyBuffDurationLeft[enemyId] = 0f;
                    // NOTE: do NOT clear slow here — EnemySlowDurationLeft is tracked separately
                }
            }
        }

        private struct AbilityEvent
        {
            public int EnemyId;
            public EnemyAbilityDef Ability;
        }
    }
}
