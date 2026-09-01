using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Components;
using BattleSystemECS.Core.GAS;

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
        private readonly EventBus _eventBus;
        private readonly Dictionary<string, EnemyAbilityDef> _abilityLookup;
        private TelegraphSystem _telegraphSystem;

        // Ping-pong double-buffer for ability events — collected parallel, applied serial.
        private readonly List<AbilityEvent>[] _abilityEvents = { new List<AbilityEvent>(64), new List<AbilityEvent>(64) };
        private int _abilityEventsIdx = 0;

        // EnemyAbilityCooldownOwner: enemy abilities have a separate domain-owned
        // timer bank; activation still crosses the shared typed runtime seam.
        private readonly float[] _abilityCooldownTimers = new float[ComponentStore.MAX_ENTITIES * ComponentStore.MAX_ABILITIES_PER_ENTITY];

        // Sparse list of currently-channeling enemy ids. Avoids iterating all active enemies
        // per frame in TickCastTimers (10K enemies × 500 frames would be wasted work when
        // only a handful are channeling at any time). Swap-and-pop on resolve to keep the
        // list compact. Synchronized implicitly because TickCastTimers + EnqueueAbility +
        // InterruptCast all run on the main game thread (no parallel writes).
        private readonly List<int> _activeChannelers = new List<int>(64);

        public EnemyAbilitySystem(ComponentStore store, IRenderer logger, int playerId, GameConfig gameConfig, EventBus eventBus = null)
        {
            this.store = store;
            this.logger = logger;
            this.playerId = playerId;
            this.gameConfig = gameConfig;
            this._eventBus = eventBus ?? new EventBus();

            // Build ability lookup from config
            _abilityLookup = new Dictionary<string, EnemyAbilityDef>();
            if (gameConfig.EnemyAbilities != null)
            {
                foreach (var ab in gameConfig.EnemyAbilities)
                {
                    if (!string.IsNullOrEmpty(ab.Id))
                    {
                        _abilityLookup[ab.Id] = ab;
                    }
                }
            }

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
        /// If the ability has CastTime > 0, the enemy enters the channeling state instead of executing
        /// immediately. The ability will resolve after CastTime turns (via TickCastTimers), or be
        /// interrupted by InterruptCast() (silence/stun/damage).
        /// </summary>
        public void EnqueueAbility(int enemyId, string abilityId)
        {
            if (enemyId < 0 || enemyId >= ComponentStore.MAX_ENTITIES) return;
            if (!_abilityLookup.TryGetValue(abilityId, out var ability)) return;

            int timerIdx = enemyId * ComponentStore.MAX_ABILITIES_PER_ENTITY;
            var activation = new AbilityActivationRequest(enemyId, 0, ability.Cooldown);
            if (!GameplayAbilityRuntime.TryActivate(_abilityCooldownTimers, activation).Accepted) return;

            // If enemy is already channeling, ignore new ability requests (channel is locked).
            if (store.EnemyIsChanneling[enemyId]) return;

            // Disarm CC (Round 124): a disarmed enemy cannot queue or cast any ability.
            // Distinct from Stun (which blocks movement). Disarm preserves mobility + basic attack
            // but silences all abilities (AOE heal, summon, buff, stun_aoe, etc.).
            // Skip if the enemy is dead or invalid; otherwise check the per-enemy disarm duration.
            if (!store.EnemyActive[enemyId]) return;
            if (store.EnemyDisarmDurationLeft[enemyId] > 0f)
            {
                logger.Log($"[ABILITY] Enemy {enemyId} is DISARMED, skipping ability '{ability.Name}'");
                return;
            }

            // Channeling path: if ability has CastTime > 0, start a channel timer instead of
            // executing immediately. The string ability id is stored so TickCastTimers can
            // resolve the EnemyAbilityDef via the existing _abilityLookup (avoids hash
            // collision risk that string.GetHashCode() would introduce in .NET Core).
            if (ability.CastTime > 0f)
            {
                store.EnemyIsChanneling[enemyId] = true;
                store.EnemyChannelTimer[enemyId] = ability.CastTime;
                store.EnemyChannelAbilityId[enemyId] = abilityId;
                store.EnemyChannelInterruptible[enemyId] = ability.Interruptible;
                _activeChannelers.Add(enemyId);
                logger.Log($"[ABILITY] Enemy {enemyId} begins channeling '{ability.Name}' for {ability.CastTime:F0} turns (interruptible={ability.Interruptible})");
                return;
            }

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
                    GameplayAbilityRuntime.TickCooldown(_abilityCooldownTimers, idx, deltaTime);

                // Round 124: tick down per-enemy disarm duration (independent of ability cooldowns)
                float disarmLeft = store.EnemyDisarmDurationLeft[enemyId];
                if (disarmLeft > 0f)
                {
                    disarmLeft -= deltaTime;
                    store.EnemyDisarmDurationLeft[enemyId] = disarmLeft > 0f ? disarmLeft : 0f;
                }
            }
        }

        /// <summary>
        /// Decrement cast timers for enemies currently channeling. When a timer reaches 0,
        /// resolve the cast by enqueuing the ability as if it had been instant. Must be called
        /// once per turn (typically from GameManager right after ExecuteAbilities or before
        /// the next frame) so that channeling is independent of frame rate. Also called
        /// before any system checks EnemyIsCasting so Movement/AI know the cast is active.
        /// </summary>
        public void TickCastTimers(float deltaTime)
        {
            // Iterate the sparse list of currently-channeling enemies (not all active enemies).
            // This keeps the per-frame work O(active channelers) instead of O(active enemies).
            for (int i = _activeChannelers.Count - 1; i >= 0; i--)
            {
                int enemyId = _activeChannelers[i];
                if (!store.EnemyIsChanneling[enemyId])
                {
                    // Stale entry (e.g. enemy was destroyed mid-channel without going through
                    // InterruptCast). Remove via swap-pop and continue.
                    int lastIdx = _activeChannelers.Count - 1;
                    if (i != lastIdx) _activeChannelers[i] = _activeChannelers[lastIdx];
                    _activeChannelers.RemoveAt(lastIdx);
                    continue;
                }

                store.EnemyChannelTimer[enemyId] -= 1f;
                if (store.EnemyChannelTimer[enemyId] > 0f)
                {
                    // Round 124: disarm during a channel interrupts it (no cooldown refund —
                    // consistent with AddStaggerDamage which also overrides Interruptible).
                    if (store.EnemyDisarmDurationLeft[enemyId] > 0f)
                    {
                        store.EnemyIsChanneling[enemyId] = false;
                        store.EnemyChannelTimer[enemyId] = 0f;
                        store.EnemyChannelAbilityId[enemyId] = null;
                        store.EnemyChannelInterruptible[enemyId] = true;
                        int popIdx2 = _activeChannelers.Count - 1;
                        if (i != popIdx2) _activeChannelers[i] = _activeChannelers[popIdx2];
                        _activeChannelers.RemoveAt(popIdx2);
                        logger.Log($"[ABILITY] Enemy {enemyId} channel interrupted by DISARM");
                        continue;
                    }
                    continue;
                }

                // Channel complete: resolve the ability
                string abilityId = store.EnemyChannelAbilityId[enemyId];
                store.EnemyIsChanneling[enemyId] = false;
                store.EnemyChannelTimer[enemyId] = 0f;
                store.EnemyChannelAbilityId[enemyId] = null;

                // Remove from sparse list
                int popIdx = _activeChannelers.Count - 1;
                if (i != popIdx) _activeChannelers[i] = _activeChannelers[popIdx];
                _activeChannelers.RemoveAt(popIdx);

                if (!string.IsNullOrEmpty(abilityId) && _abilityLookup.TryGetValue(abilityId, out var ability))
                {
                    _abilityEvents[_abilityEventsIdx].Add(new AbilityEvent
                    {
                        EnemyId = enemyId,
                        Ability = ability
                    });
                    logger.Log($"[ABILITY] Enemy {enemyId} channel resolved: '{ability.Name}'");
                }
            }
        }

        /// <summary>
        /// Interrupt a currently channeling enemy's cast. If the channel is not interruptible
        /// (e.g. boss ultimate), the call is a no-op. On successful interrupt, the cooldown
        /// for that ability slot is set to 50% of the original cooldown (refund half) so
        /// the enemy cannot perma-stun itself by being interrupted. Returns true if the
        /// channel was actually interrupted, false otherwise (not channeling or
        /// non-interruptible). Public so external systems (silence tower, damage threshold,
        /// Stagger meter) can call it without knowing the ability's internal cooldown state.
        /// </summary>
        public bool InterruptCast(int enemyId)
        {
            if (enemyId < 0 || enemyId >= ComponentStore.MAX_ENTITIES) return false;
            if (!store.EnemyIsChanneling[enemyId]) return false;
            if (!store.EnemyChannelInterruptible[enemyId]) return false;

            string abilityId = store.EnemyChannelAbilityId[enemyId];
            string abilityName = null;
            float halfCooldown = 0f;
            if (!string.IsNullOrEmpty(abilityId) && _abilityLookup.TryGetValue(abilityId, out var ability))
            {
                abilityName = ability.Name;
                halfCooldown = ability.Cooldown * 0.5f;
            }

            store.EnemyIsChanneling[enemyId] = false;
            store.EnemyChannelTimer[enemyId] = 0f;
            store.EnemyChannelAbilityId[enemyId] = null;
            store.EnemyChannelInterruptible[enemyId] = true;

            // Remove from sparse channelers list (swap-pop, O(1))
            int idx = _activeChannelers.IndexOf(enemyId);
            if (idx >= 0)
            {
                int lastIdx = _activeChannelers.Count - 1;
                if (idx != lastIdx) _activeChannelers[idx] = _activeChannelers[lastIdx];
                _activeChannelers.RemoveAt(lastIdx);
            }

            // Refund half cooldown so the enemy can try again later
            int timerIdx = enemyId * ComponentStore.MAX_ABILITIES_PER_ENTITY;
            if (halfCooldown > 0f)
            {
                _abilityCooldownTimers[timerIdx] = halfCooldown;
            }

            logger.Log($"[ABILITY] Enemy {enemyId} channel INTERRUPTED: '{abilityName ?? "<unknown>"}' (refund {halfCooldown:F1} turn CD)");
            return true;
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
                case "silence_tower":
                    ExecuteSilenceTower(enemyId, ability);
                    break;
                case "dispel_tower":
                    ExecuteDispelTower(enemyId, ability);
                    break;
                default:
                    // Unknown ability type — log and set cooldown to prevent infinite retry
                    logger.Log($"[ABILITY] Unknown ability type '{ability.AbilityType}' on enemy {enemyId}, ignoring");
                    break;
            }

            int timerIdx = enemyId * ComponentStore.MAX_ABILITIES_PER_ENTITY;
            GameplayAbilityRuntime.AbilityCommit(_abilityCooldownTimers,
                new AbilityActivationRequest(enemyId, 0, ability.Cooldown));
        }

        private void ExecuteSelfHeal(int enemyId, EnemyAbilityDef ability)
        {
            if (!store.EnemyActive[enemyId]) return;

            float maxHealth = store.EnemyMaxHealth[enemyId];
            float healAmount = maxHealth * ability.HealAmount;
            store.ApplyEnemyResourceAuthority(enemyId, enemyId, new Core.GAS.AttributeKey(3), healAmount);

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

                    _eventBus.PlayerDamaged.Publish(new PlayerDamagedEvent
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
            store.ApplyEnemyResourceAuthority(enemyId, allyId, new Core.GAS.AttributeKey(3), healAmount);
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

        private void ExecuteSilenceTower(int enemyId, EnemyAbilityDef ability)
        {
            if (ability.SilenceRadius <= 0 || ability.SilenceDuration <= 0) return;

            float enemyX = store.PositionX[enemyId];
            float enemyY = store.PositionY[enemyId];

            // Silence all towers within the specified radius
            var activeTowerIds = store.ActiveTowerIds;
            int silencedCount = 0;
            for (int i = 0; i < activeTowerIds.Count; i++)
            {
                int towerId = activeTowerIds[i];
                float tx = store.PositionX[towerId];
                float ty = store.PositionY[towerId];
                float dx = tx - enemyX;
                float dy = ty - enemyY;
                float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                if (dist <= ability.SilenceRadius)
                {
                    // Apply silence to this tower
                    store.TowerIsSilenced[towerId] = true;
                    store.TowerSilenceTimer[towerId] = ability.SilenceDuration;
                    store.TowerSilenceSourceId[towerId] = enemyId;
                    silencedCount++;
                }
            }

            logger.Log($"[ABILITY] Enemy {enemyId} silences {silencedCount} towers for {ability.SilenceDuration:F0} turns (radius={ability.SilenceRadius:F1}) ({ability.Name})");
        }

        private void ExecuteDispelTower(int enemyId, EnemyAbilityDef ability)
        {
            if (ability.DispelRadius <= 0f || ability.DispelDuration <= 0f) return;

            float enemyX = store.PositionX[enemyId];
            float enemyY = store.PositionY[enemyId];

            // Dispel all towers within the specified radius
            var activeTowerIds = store.ActiveTowerIds;
            int dispelledCount = 0;
            for (int i = 0; i < activeTowerIds.Count; i++)
            {
                int towerId = activeTowerIds[i];
                // Skip towers that are immune (in immunity period after dispel expired)
                if (store.TowerDispelImmunityTimer[towerId] > 0f) continue;

                float tx = store.PositionX[towerId];
                float ty = store.PositionY[towerId];
                float dx = tx - enemyX;
                float dy = ty - enemyY;
                float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                if (dist <= ability.DispelRadius)
                {
                    // Apply dispel to this tower
                    store.TowerIsDispelled[towerId] = true;
                    store.TowerDispelTimer[towerId] = ability.DispelDuration;
                    // Clear immunity timer when dispel is applied (immunity starts after dispel expires)
                    store.TowerDispelImmunityTimer[towerId] = 0f;
                    dispelledCount++;
                }
            }

            logger.Log($"[ABILITY] Enemy {enemyId} dispels {dispelledCount} tower buffs for {ability.DispelDuration:F0} turns (radius={ability.DispelRadius:F1}) ({ability.Name})");
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
