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
    public class EnemyAbilitySystem : IAbilityPayloadHandler
    {
        private readonly ComponentStore store;
        private readonly IRenderer logger;
        private readonly int playerId;
        private readonly GameConfig gameConfig;
        private readonly EventBus _eventBus;
        private readonly Dictionary<string, EnemyAbilityDef> _abilityLookup;
        private readonly Dictionary<int, EnemyAbilityDef> _payloadDefinitions = new Dictionary<int, EnemyAbilityDef>();
        private TelegraphSystem _telegraphSystem;

        // Ping-pong double-buffer for ability events — collected parallel, applied serial.
        private readonly List<AbilityEvent>[] _abilityEvents = { new List<AbilityEvent>(64), new List<AbilityEvent>(64) };
        private int _abilityEventsIdx = 0;

        // EnemyAbilityCooldownOwner：敌人能力使用领域自有计时器，激活仍经过共享类型化运行时边界。
        private readonly float[] _abilityCooldownTimers = new float[ComponentStore.MAX_ENTITIES * ComponentStore.MAX_ABILITIES_PER_ENTITY];

        // Sparse list of currently-channeling enemy ids. Avoids iterating all active enemies
        // per frame in TickCastTimers (10K enemies × 500 frames would be wasted work when
        // only a handful are channeling at any time). Swap-and-pop on resolve to keep the
        // list compact. Synchronized implicitly because TickCastTimers + EnqueueAbility +
        // InterruptCast all run on the main game thread (no parallel writes).
        private readonly List<int> _activeChannelers = new List<int>(64);
        private readonly List<int> _healTargets = new List<int>(256);
        private readonly List<float> _healMagnitudes = new List<float>(256);
        private IAbilityPayloadHandler _payloadHandler;
        private readonly List<int> _typedTargets = new List<int>(256);
        private readonly List<float> _typedMagnitudes = new List<float>(256);
        private bool _dispelCapacityReserved;

        public EnemyAbilitySystem(ComponentStore store, IRenderer logger, int playerId, GameConfig gameConfig, EventBus eventBus = null)
        {
            this.store = store;
            this.logger = logger;
            this.playerId = playerId;
            this.gameConfig = gameConfig;
            this._eventBus = eventBus ?? new EventBus();
            _payloadHandler = this;

            // Build ability lookup from config
            _abilityLookup = new Dictionary<string, EnemyAbilityDef>();
            if (gameConfig.EnemyAbilities != null)
            {
                foreach (var ab in gameConfig.EnemyAbilities)
                {
                    string id = ab.Id;
                    if (!string.IsNullOrEmpty(id))
                    {
                        _abilityLookup[id] = ab;
                    }
                }
            }

            var catalog = gameConfig.CompiledCatalog;
            if (catalog != null)
            {
                foreach (var ability in _abilityLookup.Values)
                    if (catalog.TryResolveAlias(ability.Id, out var abilityId))
                        _payloadDefinitions[abilityId.Value] = ability;
            }

        }

        /// <summary>
        /// Inject TelegraphSystem reference for warning zone queuing.
        /// </summary>
        public void SetTelegraphSystem(TelegraphSystem telegraphSystem)
        {
            _telegraphSystem = telegraphSystem;
        }

        internal void SetPhaseContext(PhaseContext context)
        {
            store.GameplayPhaseContext = context;
            if (context.AllowsCombat) return;
            _abilityEvents[0].Clear();
            _abilityEvents[1].Clear();
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

            if (gameConfig.StrictCatalogReferences && !CanDispatchStrict(ability))
            {
                logger.Log($"[ABILITY_REJECTED] UnsupportedDefinition enemyAbility={ability.Id}");
                return;
            }

            int timerIdx = CooldownSlot(enemyId);
            var activation = new AbilityActivationRequest(enemyId, timerIdx, ability.Cooldown);
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
            if (gameConfig.StrictCatalogReferences)
            {
                if (EnemyAbilityTypeRegistry.TryResolve(ability.AbilityType, out var type) &&
                    type.DispatchMode == EnemyAbilityDispatchMode.RuntimeAdapter)
                {
                    var result = TryExecuteTypedSpecialAbility(enemyId, ability, type);
                    if (!result.Accepted)
                        logger.Log($"[ABILITY_REJECTED] {result.Reason} enemyAbility={ability.Id}");
                    return;
                }

                var typed = TryExecuteTypedBasicAbility(enemyId, ability);
                if (!typed.Accepted)
                    logger.Log($"[ABILITY_REJECTED] {typed.Reason} enemyAbility={ability.Id}");
                return;
            }

            // 仅供有意绕过严格启动的测试夹具兼容投影。
            if (TryExecuteTypedBasicAbility(enemyId, ability).Accepted) return;

            if (!EnemyAbilityTypeRegistry.TryResolve(ability.AbilityType, out var compatibilityType))
            {
                logger.Log($"[ABILITY] Unknown ability type '{ability.AbilityType}' on enemy {enemyId}, ignoring");
            }
            else switch (compatibilityType.Kind)
            {
                case EnemyAbilityKind.SelfHeal:
                    ExecuteSelfHeal(enemyId, ability);
                    break;
                case EnemyAbilityKind.AoeDamage:
                    ExecuteAoeDamage(enemyId, ability);
                    break;
                case EnemyAbilityKind.BuffAllies:
                    ExecuteBuffAllies(enemyId, ability);
                    break;
                case EnemyAbilityKind.StunAoe:
                    ExecuteStunAoe(enemyId, ability);
                    break;
                case EnemyAbilityKind.SlowAoe:
                    ExecuteSlowAoe(enemyId, ability);
                    break;
                case EnemyAbilityKind.HealAllies:
                    ExecuteHealAllies(enemyId, ability);
                    break;
                case EnemyAbilityKind.StealthAttack:
                    ExecuteStealthAttack(enemyId, ability);
                    break;
                case EnemyAbilityKind.SummonMinion:
                    ExecuteSummonMinion(enemyId, ability);
                    break;
                case EnemyAbilityKind.SilenceTower:
                    ExecuteSilenceTower(enemyId, ability);
                    break;
                case EnemyAbilityKind.DispelTower:
                    ExecuteDispelTower(enemyId, ability);
                    break;
                default:
                    logger.Log($"[ABILITY] Unknown ability type '{ability.AbilityType}' on enemy {enemyId}, ignoring");
                    break;
            }

            int timerIdx = enemyId * ComponentStore.MAX_ABILITIES_PER_ENTITY;
            GameplayAbilityRuntime.AbilityCommit(_abilityCooldownTimers,
                new AbilityActivationRequest(enemyId, CooldownSlot(enemyId), ability.Cooldown));
        }

        private bool CanDispatchStrict(EnemyAbilityDef ability)
        {
            if (!EnemyAbilityTypeRegistry.TryResolve(ability, out var type, out var payload,
                out var operation)) return false;
            if (type.DispatchMode == EnemyAbilityDispatchMode.CompatibilityOnly) return false;
            var catalog = gameConfig.CompiledCatalog;
            if (catalog == null || !catalog.TryResolveAlias(ability.Id, out var id) ||
                !catalog.TryGetAbility(id, out var typed)) return false;
            for (int i = 0; i < typed.Executions.Count; i++)
                if (catalog.TryGetExecution(typed.Executions[i], out var execution) &&
                    execution.Payload == payload && execution.Operation == operation)
                    return true;
            return false;
        }

        private AbilityActivationResult TryExecuteTypedSpecialAbility(int enemyId, EnemyAbilityDef ability,
            EnemyAbilityTypeDescriptor type)
        {
            var catalog = gameConfig.CompiledCatalog;
            if (catalog == null || !catalog.TryResolveAlias(ability.Id, out var abilityId) ||
                !catalog.TryGetAbility(abilityId, out var typed))
                return new AbilityActivationResult(false, enemyId, CooldownSlot(enemyId),
                    AbilityActivationRejectReason.UnsupportedDefinition);
            bool matched = false;
            for (int i = 0; i < typed.Executions.Count; i++)
                if (catalog.TryGetExecution(typed.Executions[i], out var execution) &&
                    execution.Payload == EffectPayloadKind.WorldAction && execution.Operation == type.Operation)
                    matched = true;
            if (!matched) return new AbilityActivationResult(false, enemyId, CooldownSlot(enemyId),
                AbilityActivationRejectReason.UnsupportedDefinition);
            var request = new AbilityActivationRequest(enemyId, CooldownSlot(enemyId), ability.Cooldown,
                enemyId, abilityId, ownerPlayerId: playerId);
            return GameplayAbilityRuntime.Activate(store, catalog, _abilityCooldownTimers, request, _payloadHandler);
        }

        bool IAbilityPayloadHandler.Supports(ExecutionDefinition execution) =>
            execution.Payload == EffectPayloadKind.Damage &&
                (execution.Operation == ExecutionOperation.Default || execution.Operation == ExecutionOperation.ApplyDamage) ||
            execution.Payload == EffectPayloadKind.WorldAction &&
                (execution.Operation == ExecutionOperation.SummonEnemy || execution.Operation == ExecutionOperation.PrepareStealth) ||
            execution.Payload == EffectPayloadKind.Status &&
                (execution.Operation == ExecutionOperation.ApplyEnemyBuff || execution.Operation == ExecutionOperation.ApplyTowerSilence) ||
            execution.Payload == EffectPayloadKind.Dispel &&
                execution.Operation == ExecutionOperation.RemoveDispellableEffects ||
            execution.Payload == EffectPayloadKind.Telegraph &&
                execution.Operation == ExecutionOperation.QueueTelegraph;

        bool IAbilityPayloadHandler.CanCommit(AbilityPayloadContext context)
        {
            if (context.Execution.Payload == EffectPayloadKind.Damage &&
                (uint)context.Target.Index < ComponentStore.MAX_PLAYERS)
                return store.ResourceResolver.CanApplyPlayerDamage(new PlayerDamageRequest(context.Source,
                    context.Target, context.Magnitude, 0L,
                    context.Ability.Id, context.Target.Index));
            if (!_payloadDefinitions.TryGetValue(context.Ability.Id.Value, out var ability) ||
                !store.EnemyActive[context.Source.Index]) return false;
            if (context.Execution.Operation == ExecutionOperation.ApplyEnemyBuff)
                return context.Execution.Payload == EffectPayloadKind.Status &&
                       store.EnemyActive[context.Target.Index] && ability.DamageMultiplier > 0f && ability.BuffDuration > 0f;
            if (context.Execution.Operation == ExecutionOperation.ApplyTowerSilence)
                return context.Execution.Payload == EffectPayloadKind.Status && IsActiveTower(context.Target.Index) &&
                       ability.SilenceDuration > 0f;
            if (context.Execution.Operation == ExecutionOperation.RemoveDispellableEffects)
                return context.Execution.Payload == EffectPayloadKind.Dispel && IsActiveTower(context.Target.Index) &&
                       _dispelCapacityReserved && CountDispellableEffects(context.Target.Index) > 0;
            if (context.Execution.Operation == ExecutionOperation.QueueTelegraph)
                return context.Execution.Payload == EffectPayloadKind.Telegraph && _telegraphSystem != null &&
                       (uint)context.Target.Index < ComponentStore.MAX_PLAYERS &&
                       context.Execution.Parameter >= 0 && context.Execution.Parameter <= 2 &&
                       context.Magnitude > 0f && _telegraphSystem.CanQueueTelegraphZone(context.Execution.Duration);
            if (context.Execution.Payload != EffectPayloadKind.WorldAction) return false;
            if (context.Execution.Operation == ExecutionOperation.SummonEnemy)
                return store.HasEntityCapacity && HasValidSummonContract(ability, context.Execution);
            return context.Execution.Operation == ExecutionOperation.PrepareStealth &&
                   ability.DamageMultiplier > 0f;
        }

        int IAbilityPayloadHandler.Commit(AbilityPayloadContext context)
        {
            if (context.Execution.Payload == EffectPayloadKind.Damage &&
                (uint)context.Target.Index < ComponentStore.MAX_PLAYERS)
            {
                var result = store.ResourceResolver.TryApply(new PlayerDamageRequest(context.Source, context.Target,
                    context.Magnitude, store.AllocateGameplaySequence(context.Target.Index), context.Ability.Id,
                    context.Target.Index));
                if (!result.Accepted) throw new InvalidOperationException("prevalidated player damage was rejected during commit");
                _eventBus.PlayerDamaged.Publish(new PlayerDamagedEvent
                {
                    Damage = result.Applied,
                    RemainingHealth = store.PlayerCurrentHealth[context.Target.Index],
                    AttackerId = context.Source.Index
                });
                return 1;
            }
            if (!_payloadDefinitions.TryGetValue(context.Ability.Id.Value, out var ability)) return 0;
            if (context.Execution.Operation == ExecutionOperation.ApplyEnemyBuff) return 1;
            if (context.Execution.Operation == ExecutionOperation.ApplyTowerSilence)
            {
                store.ApplyTowerSilence(context.Target.Index, ability.SilenceDuration, context.Source.Index);
                return 1;
            }
            if (context.Execution.Operation == ExecutionOperation.RemoveDispellableEffects)
                return RemoveDispellableEffects(context.Target.Index);
            if (context.Execution.Operation == ExecutionOperation.QueueTelegraph)
            {
                float radius = context.Ability.Targeting.Radius > 0f
                    ? context.Ability.Targeting.Radius : context.Ability.Targeting.Range;
                bool queued = _telegraphSystem.TryQueueTelegraphZone(context.Source, context.Target,
                    store.PositionX[context.Target.Index], store.PositionY[context.Target.Index], radius,
                    context.Execution.Duration, context.Magnitude, context.Ability.Id,
                    context.Request.OwnerPlayerId, TelegraphSystem.SHAPE_CIRCLE,
                    colorHint: context.Execution.Parameter);
                if (!queued) throw new InvalidOperationException("prevalidated telegraph capacity was unavailable during commit");
                return 1;
            }
            if (context.Execution.Operation == ExecutionOperation.SummonEnemy)
            {
                if (!HasValidSummonContract(ability, context.Execution))
                    throw new InvalidOperationException("summon multipliers must be validated before commit");
                return EnemyWorldActionAdapter.Summon(store, logger, context.Source.Index, ability,
                    context.Execution.Magnitude, context.Execution.Duration);
            }
            if (context.Execution.Operation == ExecutionOperation.PrepareStealth)
                return EnemyWorldActionAdapter.PrepareStealth(store, logger, context.Source.Index, ability);
            return 0;
        }

        internal void SetPayloadHandler(IAbilityPayloadHandler sharedHandler)
        {
            if (sharedHandler == null) throw new ArgumentNullException(nameof(sharedHandler));
            _payloadHandler = new AbilityPayloadHandlerChain(this, sharedHandler);
        }

        private static bool HasValidSummonContract(EnemyAbilityDef ability, ExecutionDefinition execution) =>
            ability.MinionHealthMult > 0f && ability.MinionDamageMult > 0f &&
            execution.Magnitude > 0f && execution.Duration > 0f &&
            Math.Abs(ability.MinionHealthMult - execution.Magnitude) <= 0.0001f &&
            Math.Abs(ability.MinionDamageMult - execution.Duration) <= 0.0001f;

        private bool IsActiveTower(int towerId)
        {
            if (!store.GetEntityHandle(towerId).IsValid || !store.PositionActive[towerId]) return false;
            var towers = store.ActiveTowerIds;
            for (int i = 0; i < towers.Count; i++) if (towers[i] == towerId) return true;
            return false;
        }

        private int CountDispellableEffects(int towerId)
        {
            int result = 0;
            for (int slot = 0; slot < store.GetEffectCount(towerId); slot++)
                if (store.TryGetActiveEffectAt(towerId, slot, out _, out var definition, out _) &&
                    GrantsTag(definition, CatalogRegistries.DispellableTag)) result++;
            return result;
        }

        private int RemoveDispellableEffects(int towerId)
        {
            int removed = 0;
            var target = store.GetEntityHandle(towerId);
            for (int slot = store.GetEffectCount(towerId) - 1; slot >= 0; slot--)
            {
                if (!store.TryGetActiveEffectAt(towerId, slot, out var runtime, out var definition, out _) ||
                    !GrantsTag(definition, CatalogRegistries.DispellableTag)) continue;
                if (!store.GameplayEffectsRuntime.Remove(target, runtime.Handle))
                    throw new InvalidOperationException("prevalidated dispel removal failed during commit");
                removed++;
            }
            return removed;
        }

        private static bool GrantsTag(GameplayEffectDefinition definition, TagId tag)
        {
            for (int i = 0; i < definition.GrantedTags.Count; i++)
                if (definition.GrantedTags[i].Equals(tag)) return true;
            return false;
        }

        private AbilityActivationResult TryExecuteTypedBasicAbility(int enemyId, EnemyAbilityDef ability)
        {
            if (!EnemyAbilityTypeRegistry.TryResolve(ability, out var type, out var payload,
                out var operation) || type.DispatchMode != EnemyAbilityDispatchMode.TypedCatalog)
                return new AbilityActivationResult(false, enemyId, 0, AbilityActivationRejectReason.UnsupportedDefinition);
            bool heal = payload == EffectPayloadKind.Heal;
            bool damage = payload == EffectPayloadKind.Damage || payload == EffectPayloadKind.Telegraph;
            var catalog = gameConfig.CompiledCatalog;
            if (catalog == null) return new AbilityActivationResult(false, enemyId, 0, AbilityActivationRejectReason.UnsupportedDefinition);
            string alias = ability.Id;
            if (string.IsNullOrWhiteSpace(alias) || !catalog.TryResolveAlias(alias, out var typedId) ||
                !catalog.TryGetAbility(typedId, out var typed))
                return new AbilityActivationResult(false, enemyId, 0, AbilityActivationRejectReason.UnsupportedDefinition);
            bool groupHeal = type.Kind == EnemyAbilityKind.HealAllies;
            int targetId = heal ? enemyId : playerId;
            if (targetId < 0 || !store.GetEntityHandle(targetId).IsValid)
                return new AbilityActivationResult(false, enemyId, 0, AbilityActivationRejectReason.NoTarget);
            bool payloadMatches = false;
            for (int i = 0; i < typed.Executions.Count; i++)
            {
                if (!catalog.TryGetExecution(typed.Executions[i], out var execution))
                    return new AbilityActivationResult(false, enemyId, 0, AbilityActivationRejectReason.UnsupportedDefinition);
                if (execution.Payload == payload && execution.Operation == operation) payloadMatches = true;
            }
            if (!payloadMatches) return new AbilityActivationResult(false, enemyId, 0, AbilityActivationRejectReason.UnsupportedDefinition);
            if (groupHeal)
            {
                _healTargets.Clear();
                _healMagnitudes.Clear();
                CollectHealTargets(enemyId, ability, _healTargets, _healMagnitudes);
                var groupRequest = new AbilityActivationRequest(enemyId, CooldownSlot(enemyId), ability.Cooldown, -1, typedId,
                    null, null, 0f, float.NaN, playerId);
                var groupResult = GameplayAbilityRuntime.ActivateHealTargets(store, catalog, _abilityCooldownTimers,
                    groupRequest, _healTargets, _healMagnitudes);
                if (groupResult.Accepted)
                    logger.Log($"[ABILITY] Enemy {enemyId} typed '{ability.Name}' healed {groupResult.AppliedEffects} allies");
                return groupResult;
            }
            if (type.Kind == EnemyAbilityKind.BuffAllies || type.Kind == EnemyAbilityKind.SilenceTower ||
                type.Kind == EnemyAbilityKind.DispelTower)
            {
                _typedTargets.Clear();
                _typedMagnitudes.Clear();
                bool collected = type.Kind == EnemyAbilityKind.BuffAllies
                    ? TargetingRuntime.TryCollectEnemyAllies(store, enemyId, typed.Targeting, _typedTargets, _typedMagnitudes)
                    : TargetingRuntime.TryCollectTowerTargets(store, enemyId, typed.Targeting, _typedTargets, _typedMagnitudes);
                if (!collected)
                    return new AbilityActivationResult(false, enemyId, CooldownSlot(enemyId), AbilityActivationRejectReason.UnsupportedDefinition);
                if (type.Kind == EnemyAbilityKind.DispelTower)
                {
                    int requiredEvents = 0;
                    for (int i = _typedTargets.Count - 1; i >= 0; i--)
                    {
                        int count = CountDispellableEffects(_typedTargets[i]);
                        if (count == 0) { _typedTargets.RemoveAt(i); _typedMagnitudes.RemoveAt(i); }
                        else requiredEvents += count;
                    }
                    _dispelCapacityReserved = requiredEvents > 0 &&
                        store.GameplayEffectsRuntime.Events.CanPublish(requiredEvents, true);
                    if (!_dispelCapacityReserved)
                        return new AbilityActivationResult(false, enemyId, CooldownSlot(enemyId),
                            _typedTargets.Count == 0 ? AbilityActivationRejectReason.NoTarget : AbilityActivationRejectReason.InvalidRequest);
                }
                var groupRequest = new AbilityActivationRequest(enemyId, CooldownSlot(enemyId), ability.Cooldown,
                    -1, typedId, ownerPlayerId: playerId);
                try
                {
                    var groupResult = GameplayAbilityRuntime.ActivateTargets(store, catalog, _abilityCooldownTimers,
                        groupRequest, _typedTargets, _typedMagnitudes, _payloadHandler);
                    if (groupResult.Accepted)
                        logger.Log($"[ABILITY] Enemy {enemyId} typed '{ability.Name}' affected {groupResult.AppliedEffects} target payload(s)");
                    return groupResult;
                }
                finally { _dispelCapacityReserved = false; }
            }
            float magnitude = heal
                ? store.EnemyMaxHealth[targetId] * Math.Max(0f, ability.HealAmount)
                : damage ? store.EnemyDamage[enemyId] * Math.Max(0f, ability.DamageMultiplier)
                : float.NaN;
             var request = new AbilityActivationRequest(enemyId, CooldownSlot(enemyId), ability.Cooldown, targetId, typedId,
                 magnitudeOverride: magnitude, ownerPlayerId: playerId);
            var result = GameplayAbilityRuntime.Activate(store, catalog, _abilityCooldownTimers, request, _payloadHandler);
            if (result.Accepted)
                logger.Log($"[ABILITY] Enemy {enemyId} typed '{ability.Name}' applied {result.AppliedEffects} effect(s)");
            return result;
        }

        private void CollectHealTargets(int sourceId, EnemyAbilityDef ability, List<int> targets, List<float> magnitudes)
        {
            float sourceX = store.PositionX[sourceId];
            float sourceY = store.PositionY[sourceId];
            var enemies = store.GetCachedActiveEnemyIds();
            for (int i = 0; i < enemies.Count; i++)
            {
                int target = enemies[i];
                if (target == sourceId || !store.EnemyActive[target] || store.EnemyHealth[target] >= store.EnemyMaxHealth[target]) continue;
                float distance = Math.Abs(store.PositionX[target] - sourceX) + Math.Abs(store.PositionY[target] - sourceY);
                if (ability.AoeRadius > 0f && distance > ability.AoeRadius) continue;
                float magnitude = store.EnemyMaxHealth[target] * Math.Max(0f, ability.HealAmount);
                if (magnitude <= 0f) continue;
                targets.Add(target);
                magnitudes.Add(magnitude);
            }
        }

        private static int CooldownSlot(int enemyId) => enemyId * ComponentStore.MAX_ABILITIES_PER_ENTITY;

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
                if (!store.EnemyActive[allyId] || allyId == enemyId) continue;
                float dist = Math.Abs(enemyX - store.PositionX[allyId]) +
                             Math.Abs(enemyY - store.PositionY[allyId]);
                if (dist > ability.AoeRadius) continue;
                store.EnemyBuffDamageBonus[allyId] = store.EnemyDamage[allyId] * ability.DamageMultiplier;
                store.EnemyBuffDurationLeft[allyId] = ability.BuffDuration;
                buffedCount++;
            }
            if (buffedCount > 0)
                logger.Log($"[ABILITY] Enemy {enemyId} buffs {buffedCount} allies with {ability.BuffStat} for {ability.BuffDuration} turns");
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
            EnemyWorldActionAdapter.PrepareStealth(store, logger, enemyId, ability);
        }

        private void ExecuteSummonMinion(int enemyId, EnemyAbilityDef ability)
        {
            EnemyWorldActionAdapter.Summon(store, logger, enemyId, ability,
                ability.MinionHealthMult, ability.MinionDamageMult);
        }

        private static class EnemyWorldActionAdapter
        {
            internal static int PrepareStealth(ComponentStore store, IRenderer logger, int enemyId,
                EnemyAbilityDef ability)
            {
                if (ability.DamageMultiplier <= 0f) return 0;
                float existing = store.EnemyStealthMultiplier[enemyId];
                store.EnemyStealthMultiplier[enemyId] = Math.Max(existing, ability.DamageMultiplier);
                logger.Log($"[ABILITY] Enemy {enemyId} prepares stealth attack with {store.EnemyStealthMultiplier[enemyId]:F1}x damage multiplier ({ability.Name})");
                return 1;
            }

            internal static int Summon(ComponentStore store, IRenderer logger, int enemyId,
                EnemyAbilityDef ability, float healthMult, float damageMult)
            {
                if (healthMult <= 0f || damageMult <= 0f)
                    throw new InvalidOperationException("summon multipliers must be validated before commit");
                int minionId = store.CreateEntity();
                if (minionId < 0)
                    throw new InvalidOperationException("prevalidated summon capacity was unavailable during commit");
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
                store.PositionX[minionId] = store.PositionX[enemyId];
                store.PositionY[minionId] = store.PositionY[enemyId];
                store.PositionActive[minionId] = true;
                store.SetEntityName(minionId, $"Minion_{minionId}");
                store.AddActiveEnemyId(minionId);
                logger.Log($"[ABILITY] Enemy {enemyId} summons minion {minionId} (HP: {baseHealth * healthMult:F0}, DMG: {baseDamage * damageMult:F0}) ({ability.Name})");
                return 1;
            }
        }

        private void ExecuteSilenceTower(int enemyId, EnemyAbilityDef ability)
        {
            if (ability.SilenceRadius <= 0 || ability.SilenceDuration <= 0) return;
            float enemyX = store.PositionX[enemyId];
            float enemyY = store.PositionY[enemyId];
            var activeTowerIds = store.ActiveTowerIds;
            for (int i = 0; i < activeTowerIds.Count; i++)
            {
                int towerId = activeTowerIds[i];
                float dx = store.PositionX[towerId] - enemyX;
                float dy = store.PositionY[towerId] - enemyY;
                if ((float)Math.Sqrt(dx * dx + dy * dy) > ability.SilenceRadius) continue;
                store.TowerIsSilenced[towerId] = true;
                store.TowerSilenceTimer[towerId] = ability.SilenceDuration;
                store.TowerSilenceSourceId[towerId] = enemyId;
            }
        }

        private void ExecuteDispelTower(int enemyId, EnemyAbilityDef ability)
        {
            if (ability.DispelRadius <= 0f || ability.DispelDuration <= 0f) return;
            float enemyX = store.PositionX[enemyId];
            float enemyY = store.PositionY[enemyId];
            var activeTowerIds = store.ActiveTowerIds;
            for (int i = 0; i < activeTowerIds.Count; i++)
            {
                int towerId = activeTowerIds[i];
                if (store.TowerDispelImmunityTimer[towerId] > 0f) continue;
                float dx = store.PositionX[towerId] - enemyX;
                float dy = store.PositionY[towerId] - enemyY;
                if ((float)Math.Sqrt(dx * dx + dy * dy) > ability.DispelRadius) continue;
                store.TowerIsDispelled[towerId] = true;
                store.TowerDispelTimer[towerId] = ability.DispelDuration;
                store.TowerDispelImmunityTimer[towerId] = 0f;
            }
        }

        /// <summary>
        /// 从规范沉默标签同步旧塔攻击门控。
        /// </summary>
        public void Update()
        {
            var towers = store.ActiveTowerIds;
            for (int i = 0; i < towers.Count; i++)
            {
                int towerId = towers[i];
                bool silenced = TryGetTagRemaining(towerId, CatalogRegistries.TowerSilencedTag, out float remaining);
                store.TowerIsSilenced[towerId] = silenced;
                if (silenced) store.TowerSilenceTimer[towerId] = remaining;
                else
                {
                    store.TowerSilenceTimer[towerId] = 0f;
                    store.TowerSilenceSourceId[towerId] = -1;
                }
            }
        }

        private bool TryGetTagRemaining(int entityId, TagId tag, out float remaining)
        {
            remaining = 0f;
            for (int slot = 0; slot < store.GetEffectCount(entityId); slot++)
            {
                if (!store.TryGetActiveEffectAt(entityId, slot, out var runtime, out var definition, out _) ||
                    !GrantsTag(definition, tag)) continue;
                remaining = runtime.RemainingTime;
                return true;
            }
            return false;
        }

        private struct AbilityEvent
        {
            public int EnemyId;
            public EnemyAbilityDef Ability;
        }
    }
}
