using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Components;

namespace BattleSystemECS.Core.GAS
{
    public enum AbilityActivationRejectReason { None, InvalidRequest, Cooldown, NoTarget, PhaseNotAllowed, TagRequirementsNotMet, Cost, UnsupportedDefinition }

    public readonly struct AbilityActivationRequest
    {
        public readonly int OwnerId;
        public readonly int Slot;
        public readonly float Cooldown;
        public readonly int TargetId;
        public readonly AbilityId Ability;
        public readonly EffectId Effect;
        public readonly TriggerId Trigger;
        public readonly float Cost;
        public readonly float MagnitudeOverride;
        public readonly int OwnerPlayerId;
        public AbilityActivationRequest(int ownerId, int slot, float cooldown, int targetId = -1,
            AbilityId ability = default(AbilityId), EffectId effect = default(EffectId), TriggerId trigger = default(TriggerId), float cost = 0f)
            : this(ownerId, slot, cooldown, targetId, ability, effect, trigger, cost, float.NaN, ownerId) { }
        public AbilityActivationRequest(int ownerId, int slot, float cooldown, int targetId,
            AbilityId ability, EffectId effect, TriggerId trigger, float cost, float magnitudeOverride, int ownerPlayerId = -1)
        { OwnerId = ownerId; Slot = slot; Cooldown = cooldown; TargetId = targetId; Ability = ability; Effect = effect; Trigger = trigger; Cost = cost; MagnitudeOverride = magnitudeOverride; OwnerPlayerId = ownerPlayerId < 0 ? ownerId : ownerPlayerId; }
        public AbilityActivationRequest(int ownerId, int slot, float cooldown, int targetId,
            AbilityId ability, float magnitudeOverride)
            : this(ownerId, slot, cooldown, targetId, ability, default(EffectId), default(TriggerId), 0f, magnitudeOverride, ownerId) { }
        public AbilityActivationRequest(int ownerId, int slot, float cooldown, int targetId,
            AbilityId ability, float magnitudeOverride, int ownerPlayerId)
            : this(ownerId, slot, cooldown, targetId, ability, default(EffectId), default(TriggerId), 0f, magnitudeOverride, ownerPlayerId) { }
    }

    public readonly struct AbilityActivationResult
    {
        public readonly bool Accepted;
        public readonly int OwnerId;
        public readonly int Slot;
        public readonly AbilityActivationRejectReason Reason;
        public readonly int AppliedEffects;
        public AbilityActivationResult(bool accepted, int ownerId, int slot, AbilityActivationRejectReason reason = AbilityActivationRejectReason.None, int appliedEffects = 0)
        { Accepted = accepted; OwnerId = ownerId; Slot = slot; Reason = reason; AppliedEffects = appliedEffects; }
    }

    public readonly struct AbilityPayloadContext
    {
        public readonly ComponentStore Store;
        public readonly AbilityDefinition Ability;
        public readonly ExecutionDefinition Execution;
        public readonly AbilityActivationRequest Request;
        public readonly EntityHandle Source;
        public readonly EntityHandle Target;
        public readonly float Magnitude;
        public AbilityPayloadContext(ComponentStore store, AbilityDefinition ability, ExecutionDefinition execution,
            AbilityActivationRequest request, EntityHandle source, EntityHandle target, float magnitude)
        { Store = store; Ability = ability; Execution = execution; Request = request; Source = source; Target = target; Magnitude = magnitude; }
    }

    /// <summary>
    /// 供需要 GAS 存储外部服务的载荷扩展。
    /// CanCommit 是只读规划；返回 true 后 Commit 不得拒绝。
    /// </summary>
    public interface IAbilityPayloadHandler
    {
        bool Supports(ExecutionDefinition execution);
        bool CanCommit(AbilityPayloadContext context);
        int Commit(AbilityPayloadContext context);
    }

    /// <summary>
    /// 能力槽激活状态的唯一写入者。旧系统可读取定义，但冷却归 ECS 能力存储所有。
    /// </summary>
    public static class GameplayAbilityRuntime
    {
        private interface IAbilityActivationState
        {
            bool IsValid { get; }
            bool IsReady { get; }
            void Commit(float cooldown);
        }

        private readonly struct CooldownArrayActivationState : IAbilityActivationState
        {
            private readonly float[] _cooldowns;
            private readonly int _slot;
            public CooldownArrayActivationState(float[] cooldowns, int slot)
            { _cooldowns = cooldowns; _slot = slot; }
            public bool IsValid => _cooldowns != null && _slot >= 0 && _slot < _cooldowns.Length;
            public bool IsReady => IsValid && _cooldowns[_slot] <= 0f;
            public void Commit(float cooldown) => _cooldowns[_slot] = Math.Max(0f, cooldown);
        }

        private readonly struct StoredAbilityActivationState : IAbilityActivationState
        {
            private readonly ComponentStore _store;
            private readonly int _entityId;
            private readonly int _slot;
            public StoredAbilityActivationState(ComponentStore store, int entityId, int slot)
            { _store = store; _entityId = entityId; _slot = slot; }
            public bool IsValid => _store != null && _entityId >= 0 && _entityId < ComponentStore.MAX_ENTITIES &&
                _slot >= 0 && _slot < _store.AbilityCount[_entityId];
            public bool IsReady => IsValid && _store.GetAbility(_entityId, _slot).CanActivate();
            public void Commit(float cooldown)
            {
                var instance = _store.GetAbility(_entityId, _slot);
                instance.Activate();
                _store.SetAbility(_entityId, _slot, instance);
            }
        }

        private enum TargetMagnitudeMode { Scale, Override }

        private readonly struct ActivationTargetSet
        {
            private readonly int _singleTargetId;
            private readonly IReadOnlyList<int> _targetIds;
            private readonly IReadOnlyList<float> _magnitudes;
            private readonly TargetMagnitudeMode _magnitudeMode;
            private readonly bool _isSingle;
            public readonly bool RequireEnemy;
            public readonly bool RequireHealExecutions;
            public readonly bool ForbidEffects;
            private readonly bool _requireMagnitudes;

            private ActivationTargetSet(int singleTargetId, IReadOnlyList<int> targetIds,
                IReadOnlyList<float> magnitudes, TargetMagnitudeMode magnitudeMode,
                bool isSingle, bool requireEnemy, bool requireHealExecutions, bool forbidEffects)
            {
                _singleTargetId = singleTargetId;
                _targetIds = targetIds;
                _magnitudes = magnitudes;
                _magnitudeMode = magnitudeMode;
                _isSingle = isSingle;
                RequireEnemy = requireEnemy;
                RequireHealExecutions = requireHealExecutions;
                ForbidEffects = forbidEffects;
                _requireMagnitudes = magnitudeMode == TargetMagnitudeMode.Override;
            }

            public static ActivationTargetSet Single(int targetId) =>
                new ActivationTargetSet(targetId, null, null, TargetMagnitudeMode.Scale, true, false, false, false);

            public static ActivationTargetSet Scaled(IReadOnlyList<int> targetIds, IReadOnlyList<float> scales) =>
                new ActivationTargetSet(-1, targetIds, scales, TargetMagnitudeMode.Scale, false, false, false, false);

            public static ActivationTargetSet Heals(IReadOnlyList<int> targetIds, IReadOnlyList<float> magnitudes) =>
                new ActivationTargetSet(-1, targetIds, magnitudes, TargetMagnitudeMode.Override, false, true, true, true);

            public bool IsSingle => _isSingle;
            public int Count => IsSingle ? 1 : _targetIds == null ? 0 : _targetIds.Count;
            public IReadOnlyList<int> TargetIds => _targetIds;
            public int TargetIdAt(int index) => IsSingle ? _singleTargetId : _targetIds[index];

            public AbilityActivationRejectReason ValidateShape()
            {
                if (IsSingle) return AbilityActivationRejectReason.None;
                if (_requireMagnitudes && (_targetIds == null || _magnitudes == null))
                    return AbilityActivationRejectReason.InvalidRequest;
                if (_targetIds == null || _targetIds.Count == 0) return AbilityActivationRejectReason.NoTarget;
                if (_magnitudes != null && _magnitudes.Count != _targetIds.Count)
                    return AbilityActivationRejectReason.InvalidRequest;
                return HasDuplicateTargets(_targetIds)
                    ? AbilityActivationRejectReason.InvalidRequest
                    : AbilityActivationRejectReason.None;
            }

            public bool TryRequestAt(AbilityActivationRequest request, int index,
                out AbilityActivationRequest targetRequest)
            {
                int targetId = TargetIdAt(index);
                if (IsSingle)
                {
                    targetRequest = request.ForTarget(targetId, request.MagnitudeScale);
                    return true;
                }
                float value = _magnitudes == null ? 1f : _magnitudes[index];
                if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0f)
                {
                    targetRequest = default(AbilityActivationRequest);
                    return false;
                }
                targetRequest = _magnitudeMode == TargetMagnitudeMode.Override
                    ? new AbilityActivationRequest(request.OwnerId, request.Slot, request.Cooldown, targetId,
                        request.Ability, request.Effect, request.Trigger, request.Cost, value,
                        request.OwnerPlayerId, 1f)
                    : request.ForTarget(targetId, value);
                return true;
            }
        }

        /// <summary>领域适配器使用的目录激活边界。</summary>
        public static AbilityActivationResult Activate(ComponentStore store, GameplayCatalog catalog, float[] cooldowns,
            AbilityActivationRequest request, IAbilityPayloadHandler payloadHandler = null)
            => ActivateCore(store, catalog, new CooldownArrayActivationState(cooldowns, request.Slot),
                request, ActivationTargetSet.Single(request.TargetId >= 0 ? request.TargetId : request.OwnerId), payloadHandler);

        /// <summary>ECS 能力槽的目录激活入口。</summary>
        public static AbilityActivationResult Activate(ComponentStore store, GameplayCatalog catalog, int entityId, int slot,
            AbilityActivationRequest request, IAbilityPayloadHandler payloadHandler = null)
            => ActivateCore(store, catalog, new StoredAbilityActivationState(store, entityId, slot),
                request, ActivationTargetSet.Single(request.TargetId >= 0 ? request.TargetId : request.OwnerId), payloadHandler);

        /// <summary>
        /// 在确定性目标集上激活目录能力。校验、消耗、冷却和激活发布各执行一次，
        /// 载荷对每个目标执行一次。
        /// </summary>
        public static AbilityActivationResult ActivateTargets(ComponentStore store, GameplayCatalog catalog,
            float[] cooldowns, AbilityActivationRequest request, IReadOnlyList<int> targetIds,
            IReadOnlyList<float> magnitudeOverrides = null, IAbilityPayloadHandler payloadHandler = null)
            => ActivateCore(store, catalog, new CooldownArrayActivationState(cooldowns, request.Slot),
                request, ActivationTargetSet.Scaled(targetIds, magnitudeOverrides), payloadHandler);

        public static AbilityActivationResult ActivateTargets(ComponentStore store, GameplayCatalog catalog,
            int entityId, int slot, AbilityActivationRequest request, IReadOnlyList<int> targetIds,
            IReadOnlyList<float> magnitudeScales = null, IAbilityPayloadHandler payloadHandler = null)
            => ActivateCore(store, catalog, new StoredAbilityActivationState(store, entityId, slot),
                request, ActivationTargetSet.Scaled(targetIds, magnitudeScales), payloadHandler);

        private static AbilityActivationResult ActivateCore<TState>(ComponentStore store, GameplayCatalog catalog,
            TState activationState, AbilityActivationRequest request, ActivationTargetSet targets,
            IAbilityPayloadHandler payloadHandler) where TState : struct, IAbilityActivationState
        {
            if (store == null || catalog == null || !activationState.IsValid)
                return Reject(request, AbilityActivationRejectReason.InvalidRequest);
            var targetShape = targets.ValidateShape();
            if (targetShape != AbilityActivationRejectReason.None) return Reject(request, targetShape);
            if (!catalog.TryGetAbility(request.Ability, out var ability) ||
                request.Effect.HasValue && !Contains(ability.Effects, request.Effect.Value) ||
                request.Trigger.HasValue && !Contains(ability.TriggerRefs, request.Trigger.Value))
                return Reject(request, AbilityActivationRejectReason.InvalidRequest);
            if (targets.ForbidEffects && ability.Effects.Count != 0)
                return Reject(request, AbilityActivationRejectReason.UnsupportedDefinition);
            if (targets.RequireHealExecutions)
                for (int i = 0; i < ability.Executions.Count; i++)
                    if (!catalog.TryGetExecution(ability.Executions[i], out var execution) ||
                        execution.Payload != EffectPayloadKind.Heal)
                        return Reject(request, AbilityActivationRejectReason.UnsupportedDefinition);
            var source = store.GetEntityHandle(request.OwnerId);
            if (!source.IsValid) return Reject(request, AbilityActivationRejectReason.InvalidRequest);
            if (!activationState.IsReady)
                return Reject(request, AbilityActivationRejectReason.Cooldown);

            var validation = BuildActivationPlan(store, catalog, ability, request, source, targets, payloadHandler);
            if (validation != AbilityActivationRejectReason.None) return Reject(request, validation);

            int applied = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                targets.TryRequestAt(request, i, out var targetRequest);
                int targetId = targets.TargetIdAt(i);
                int targetApplied = CommitPlan(store, catalog, ability, targetRequest, source,
                    store.GetEntityHandle(targetId), payloadHandler);
                if (targetApplied < 0)
                    throw new InvalidOperationException("prevalidated ability plan was rejected during commit");
                applied += targetApplied;
            }
            if (!CommitCosts(store, ability, request, source))
                throw new InvalidOperationException("prevalidated ability cost was rejected during commit");
            activationState.Commit(ability.Cooldown);
            int firstTargetId = targets.TargetIdAt(0);
            PublishActivation(store, request, source, store.GetEntityHandle(firstTargetId), firstTargetId);
            return new AbilityActivationResult(true, request.OwnerId, request.Slot, appliedEffects: applied);
        }

        private static AbilityActivationRejectReason BuildActivationPlan(ComponentStore store,
            GameplayCatalog catalog, AbilityDefinition ability, AbilityActivationRequest request,
            EntityHandle source, ActivationTargetSet targets, IAbilityPayloadHandler payloadHandler)
        {
            if (!ValidateCosts(store, ability, request, source)) return AbilityActivationRejectReason.Cost;
            for (int i = 0; i < targets.Count; i++)
            {
                if (!targets.TryRequestAt(request, i, out var targetRequest))
                    return targets.RequireHealExecutions ? AbilityActivationRejectReason.NoTarget
                        : AbilityActivationRejectReason.InvalidRequest;
                int targetId = targets.TargetIdAt(i);
                var target = store.GetEntityHandle(targetId);
                if (!target.IsValid || targets.RequireEnemy && !store.EnemyActive[targetId])
                    return AbilityActivationRejectReason.NoTarget;
                var validation = ValidatePlan(store, catalog, ability, targetRequest, source, target,
                    payloadHandler);
                if (validation != AbilityActivationRejectReason.None) return validation;
            }
            return ValidateCapacityPlan(store, catalog, ability, request, source, targets, payloadHandler)
                ? AbilityActivationRejectReason.None
                : AbilityActivationRejectReason.InvalidRequest;
        }

        private static AbilityActivationRejectReason ValidatePlan(ComponentStore store, GameplayCatalog catalog,
            AbilityDefinition ability, AbilityActivationRequest request, EntityHandle source, EntityHandle target,
            IAbilityPayloadHandler payloadHandler)
        {
            GameplayPhaseMask phase = PhaseMask(store.GameplayPhaseContext.Kind);
            if (phase == GameplayPhaseMask.None || (ability.AllowedPhases & phase) == 0)
                return AbilityActivationRejectReason.PhaseNotAllowed;
            if (!GameplayTagRuntime.Matches(store, source.Index, ability.RequiredTags, ability.BlockedTags) ||
                !GameplayTagRuntime.Matches(store, target.Index,
                    ability.Targeting.RequiredTags, ability.Targeting.BlockedTags))
                return AbilityActivationRejectReason.TagRequirementsNotMet;
            for (int i = 0; i < ability.Effects.Count; i++)
                if (!catalog.TryGetEffect(ability.Effects[i], out var effect) ||
                    !store.GameplayEffectsRuntime.CanApplyDefinition(effect, target.Index))
                    return AbilityActivationRejectReason.InvalidRequest;
            for (int i = 0; i < ability.Executions.Count; i++)
            {
                if (!catalog.TryGetExecution(ability.Executions[i], out var execution))
                    return AbilityActivationRejectReason.UnsupportedDefinition;
                float magnitude = ResolveMagnitude(store, execution, request.MagnitudeOverride, source.Index);
                var context = new AbilityPayloadContext(store, ability, execution, request, source, target, magnitude);
                if (payloadHandler != null && payloadHandler.Supports(execution))
                {
                    if (!payloadHandler.CanCommit(context)) return AbilityActivationRejectReason.InvalidRequest;
                    continue;
                }
                if (!TryBuildBuiltInPayload(context, out _, out bool supported))
                    return supported ? AbilityActivationRejectReason.InvalidRequest
                        : AbilityActivationRejectReason.UnsupportedDefinition;
            }
            return AbilityActivationRejectReason.None;
        }

        private enum BuiltInPayloadKind { Damage, Heal, Shield, Slow, CrowdControl, GameplayEvent }

        private readonly struct BuiltInPayloadPlan
        {
            public readonly BuiltInPayloadKind Kind;
            public BuiltInPayloadPlan(BuiltInPayloadKind kind) { Kind = kind; }
            public int DamageRequests => Kind == BuiltInPayloadKind.Damage ? 1 : 0;
            public int DamageEvents => Kind == BuiltInPayloadKind.Damage ? 3 :
                Kind == BuiltInPayloadKind.GameplayEvent ? 1 : 0;
            public int ResourceRequests => Kind == BuiltInPayloadKind.Heal || Kind == BuiltInPayloadKind.Shield ? 1 : 0;
            public int ResourceEvents => ResourceRequests;
        }

        private static bool ValidateCapacityPlan(ComponentStore store, GameplayCatalog catalog,
            AbilityDefinition ability, AbilityActivationRequest request, EntityHandle source,
            ActivationTargetSet targets, IAbilityPayloadHandler payloadHandler)
        {
            long runtimeSlots = 0;
            long modifiers = 0;
            for (int i = 0; i < ability.Effects.Count; i++)
            {
                catalog.TryGetEffect(ability.Effects[i], out var effect);
                if (effect.Type != EffectType.Instant) runtimeSlots++;
                modifiers += effect.Modifiers.Count;
            }
            long damageRequests = 0;
            long damageEvents = 1;
            long resourceRequests = 0;
            long resourceEvents = 0;
            for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++)
            {
                targets.TryRequestAt(request, targetIndex, out var targetRequest);
                var target = store.GetEntityHandle(targets.TargetIdAt(targetIndex));
                for (int i = 0; i < ability.Executions.Count; i++)
                {
                    catalog.TryGetExecution(ability.Executions[i], out var execution);
                    float magnitude = ResolveMagnitude(store, execution, targetRequest, source.Index);
                    var context = new AbilityPayloadContext(store, ability, execution, targetRequest,
                        source, target, magnitude);
                    if (payloadHandler != null && payloadHandler.Supports(execution)) continue;
                    TryBuildBuiltInPayload(context, out var payload, out _);
                    damageRequests += payload.DamageRequests;
                    damageEvents += payload.DamageEvents;
                    resourceRequests += payload.ResourceRequests;
                    resourceEvents += payload.ResourceEvents;
                }
            }
            for (int i = 0; i < ability.Costs.Count; i++)
                if (EffectiveCost(ability, request, i) != 0f) { resourceRequests++; resourceEvents++; }
            long effectEvents = (long)ability.Effects.Count * targets.Count;
            if (runtimeSlots > int.MaxValue || modifiers > int.MaxValue || effectEvents > int.MaxValue ||
                damageRequests > int.MaxValue || damageEvents > int.MaxValue ||
                resourceRequests > int.MaxValue || resourceEvents > int.MaxValue) return false;
            bool effectsOk = targets.IsSingle
                ? store.GameplayEffectsRuntime.CanApplyPlan(targets.TargetIdAt(0), (int)runtimeSlots,
                    (int)modifiers, (int)effectEvents)
                : store.GameplayEffectsRuntime.CanApplyPlan(targets.TargetIds, (int)runtimeSlots,
                    (int)modifiers, (int)effectEvents);
            return effectsOk &&
                   store.DamageResolver.CanAccept((int)damageRequests, (int)damageEvents) &&
                   store.ResourceResolver.CanAccept((int)resourceRequests, (int)resourceEvents);
        }

        private static int CommitPlan(ComponentStore store, GameplayCatalog catalog, AbilityDefinition ability,
            AbilityActivationRequest request, EntityHandle source, EntityHandle target,
            IAbilityPayloadHandler payloadHandler)
        {
            int applied = 0;
            bool damageInThisPlanQueuedTargetDeath = false;
            for (int i = 0; i < ability.Effects.Count; i++)
            {
                catalog.TryGetEffect(ability.Effects[i], out var effect);
                if (!store.GameplayEffectsRuntime.TryApply(effect.Id, effect, source, target, out _, ownerPlayerId: request.OwnerPlayerId))
                    return -1;
                applied++;
            }
            for (int i = 0; i < ability.Executions.Count; i++)
            {
                catalog.TryGetExecution(ability.Executions[i], out var execution);
                float magnitude = ResolveMagnitude(store, execution, request.MagnitudeOverride, source.Index);
                var context = new AbilityPayloadContext(store, ability, execution, request, source, target, magnitude);
                if (payloadHandler != null && payloadHandler.Supports(execution))
                    applied += Math.Max(0, payloadHandler.Commit(context));
                else if (damageInThisPlanQueuedTargetDeath && execution.Payload == EffectPayloadKind.Damage &&
                         store.IsEnemyPendingDeath(target.Index)) applied++;
                else if (!TryBuildBuiltInPayload(context, out var payload, out _) || !CommitBuiltIn(payload, context)) return -1;
                else
                {
                    applied++;
                    if (execution.Payload == EffectPayloadKind.Damage && store.IsEnemyPendingDeath(target.Index))
                        damageInThisPlanQueuedTargetDeath = true;
                }
            }
            return applied;
        }

        private static bool TryBuildBuiltInPayload(AbilityPayloadContext context,
            out BuiltInPayloadPlan plan, out bool supported)
        {
            var execution = context.Execution;
            float magnitude = context.Magnitude;
            plan = default(BuiltInPayloadPlan);
            supported = true;
            BuiltInPayloadKind kind;
            switch (execution.Payload)
            {
                case EffectPayloadKind.Damage:
                    if (!Matches(execution.Operation, ExecutionOperation.ApplyDamage)) { supported = false; return false; }
                    kind = BuiltInPayloadKind.Damage; break;
                case EffectPayloadKind.Heal:
                    if (!Matches(execution.Operation, ExecutionOperation.ApplyHeal)) { supported = false; return false; }
                    kind = BuiltInPayloadKind.Heal; break;
                case EffectPayloadKind.Shield:
                    if (!Matches(execution.Operation, ExecutionOperation.ApplyShield)) { supported = false; return false; }
                    kind = BuiltInPayloadKind.Shield; break;
                case EffectPayloadKind.Slow:
                    if (!Matches(execution.Operation, ExecutionOperation.ApplySlow)) { supported = false; return false; }
                    kind = BuiltInPayloadKind.Slow; break;
                case EffectPayloadKind.CrowdControl:
                    if (!Matches(execution.Operation, ExecutionOperation.ApplyCrowdControl)) { supported = false; return false; }
                    kind = BuiltInPayloadKind.CrowdControl; break;
                case EffectPayloadKind.GameplayEvent:
                    if (execution.Operation != ExecutionOperation.Default) { supported = false; return false; }
                    kind = BuiltInPayloadKind.GameplayEvent; break;
                default:
                    supported = false;
                    return false;
            }
            plan = new BuiltInPayloadPlan(kind);
            if (float.IsNaN(magnitude) || float.IsInfinity(magnitude)) return false;
            int targetId = context.Target.Index;
            bool player = (uint)targetId < ComponentStore.MAX_PLAYERS && context.Store.PositionActive[targetId];
            bool enemy = ComponentStore.IsValidEntity(targetId) && context.Store.EnemyActive[targetId] &&
                context.Store.EnemyHealth[targetId] > 0f && !context.Store.IsEnemyPendingDeath(targetId);
            switch (kind)
            {
                case BuiltInPayloadKind.Damage:
                    return magnitude > 0f && enemy &&
                        context.Request.OwnerPlayerId >= 0 && context.Request.OwnerPlayerId < ComponentStore.MAX_PLAYERS;
                case BuiltInPayloadKind.Heal: return magnitude > 0f && (player || enemy);
                case BuiltInPayloadKind.Shield:
                    return magnitude > 0f &&
                        execution.Duration >= 0f && context.Ability.Clock == ClockId.Combat && player;
                case BuiltInPayloadKind.Slow:
                    return magnitude > 0f && magnitude < 1f &&
                        execution.Duration > 0f && (player || enemy);
                case BuiltInPayloadKind.CrowdControl: return magnitude > 0f && (player || enemy);
                case BuiltInPayloadKind.GameplayEvent: return true;
                default:
                    return false;
            }
        }

        private static bool CommitBuiltIn(BuiltInPayloadPlan plan, AbilityPayloadContext context)
        {
            var store = context.Store;
            int targetId = context.Target.Index;
            long sequence = store.AllocateGameplaySequence(targetId);
            switch (plan.Kind)
            {
                case BuiltInPayloadKind.Damage:
                    DamageAmountStage stage = context.Execution.Stage == DamageAmountStage.LegacyMultiplier
                        ? DamageAmountStage.Raw : context.Execution.Stage;
                    var damage = store.DamageResolver.TryApply(new DamageRequest(context.Source, context.Target, context.Magnitude,
                        DamageType.True, ElementType.None, DamageFlags.None, stage,
                        DamageCommitBoundary.GameplayResolve, sequence, ability: context.Ability.Id,
                        effect: context.Request.Effect.GetValueOrDefault(), ownerPlayerId: context.Request.OwnerPlayerId));
                    return damage.Accepted;
                case BuiltInPayloadKind.Heal:
                    return store.ResourceResolver.TryApply(new HealRequest(context.Source, context.Target,
                        context.Magnitude, sequence, context.Request.OwnerPlayerId)).Accepted;
                case BuiltInPayloadKind.Shield:
                    return store.ResourceResolver.TryApply(new ShieldRequest(context.Source, context.Target,
                        context.Magnitude, context.Execution.Duration, context.Ability.Clock, sequence),
                        context.Request.OwnerPlayerId).Accepted;
                case BuiltInPayloadKind.Slow:
                    int slowDuration = Math.Max(1, (int)Math.Ceiling(context.Execution.Duration));
                    if (store.EnemyActive[targetId]) store.ApplyEnemySlow(targetId, context.Magnitude, slowDuration);
                    else store.ApplyPlayerSlow(targetId, context.Magnitude, slowDuration);
                    return true;
                case BuiltInPayloadKind.CrowdControl:
                    int duration = Math.Max(1, (int)Math.Ceiling(context.Magnitude));
                    if (store.EnemyActive[targetId])
                    {
                        if (context.Ability.Targeting.Shape == TargetingShape.AoeRoot) store.ApplyEnemyRoot(targetId, duration);
                        else if (context.Ability.Targeting.Shape == TargetingShape.AoeKnockback) store.ApplyEnemyKnockback(targetId, context.Magnitude);
                        else store.ApplyEnemyStun(targetId, duration);
                    }
                    else store.ApplyPlayerStun(targetId, duration);
                    return true;
                case BuiltInPayloadKind.GameplayEvent:
                    return store.DamageResolver.Events.TryPublish(new GameplayEvent(GameplayEventType.EffectApplied,
                        context.Source, context.Target, sequence, ownerPlayerId: context.Request.OwnerPlayerId), true);
                default:
                    return false;
            }
        }

        private static bool ValidateCosts(ComponentStore store, AbilityDefinition ability,
            AbilityActivationRequest request, EntityHandle source)
        {
            for (int i = 0; i < ability.Costs.Count; i++)
            {
                float amount = EffectiveCost(ability, request, i);
                if (amount < 0f || float.IsNaN(amount) || float.IsInfinity(amount) ||
                    !TryGetResource(store, source.Index, ability.Costs[i].Resource, out float available)) return false;
                float sameResource = 0f;
                for (int j = 0; j <= i; j++)
                    if (ability.Costs[j].Resource.Equals(ability.Costs[i].Resource)) sameResource += EffectiveCost(ability, request, j);
                if (available < sameResource) return false;
            }
            return true;
        }

        private static bool CommitCosts(ComponentStore store, AbilityDefinition ability,
            AbilityActivationRequest request, EntityHandle source)
        {
            for (int i = 0; i < ability.Costs.Count; i++)
            {
                float amount = EffectiveCost(ability, request, i);
                if (amount == 0f) continue;
                if (!store.ResourceResolver.TryApply(new ResourceRequest(source, source, ability.Costs[i].Resource,
                    -amount, store.AllocateGameplaySequence(source.Index), request.OwnerPlayerId)).Accepted) return false;
            }
            return true;
        }

        private static float EffectiveCost(AbilityDefinition ability, AbilityActivationRequest request, int index) =>
            request.Cost > 0f && ability.Costs.Count == 1 && index == 0 ? request.Cost : ability.Costs[index].Amount;

        private static bool TryGetResource(ComponentStore store, int entityId, AttributeKey key, out float value)
        {
            value = 0f;
            bool player = (uint)entityId < ComponentStore.MAX_PLAYERS && store.PositionActive[entityId];
            bool enemy = ComponentStore.IsValidEntity(entityId) && store.EnemyActive[entityId];
            if (!player && !enemy) return false;
            switch (key.Value)
            {
                case 2: value = player ? store.PlayerMaxHealth[entityId] : store.EnemyMaxHealth[entityId]; return true;
                case 3: value = player ? store.PlayerCurrentHealth[entityId] : store.EnemyHealth[entityId]; return true;
                case 4: if (!player) return false; value = store.PlayerGold[entityId]; return true;
                case 7: value = player ? store.PlayerMana[entityId] : store.EnemyCurrentMana[entityId]; return true;
                case 9: value = player ? store.PlayerShield[entityId] : store.EnemyShield[entityId]; return true;
                default: return false;
            }
        }

        private static bool Matches(ExecutionOperation actual, ExecutionOperation expected) =>
            actual == ExecutionOperation.Default || actual == expected;

        private static void PublishActivation(ComponentStore store, AbilityActivationRequest request,
            EntityHandle source, EntityHandle target, int targetId) =>
            store.DamageResolver.Events.TryPublish(new GameplayEvent(GameplayEventType.AbilityActivated, source, target,
                store.AllocateGameplaySequence(targetId), ownerPlayerId: request.OwnerPlayerId), true);
        public static AbilityActivationResult ActivateHealTargets(ComponentStore store, GameplayCatalog catalog,
            float[] cooldowns, AbilityActivationRequest request, IReadOnlyList<int> targetIds,
            IReadOnlyList<float> magnitudes)
            => ActivateCore(store, catalog, new CooldownArrayActivationState(cooldowns, request.Slot),
                request, ActivationTargetSet.Heals(targetIds, magnitudes), null);

        private static bool Contains(IReadOnlyList<EffectId> ids, EffectId id) { for (int i = 0; i < ids.Count; i++) if (ids[i].Value == id.Value) return true; return false; }
        private static bool Contains(IReadOnlyList<TriggerId> ids, TriggerId id) { for (int i = 0; i < ids.Count; i++) if (ids[i].Value == id.Value) return true; return false; }
        private static bool HasDuplicateTargets(IReadOnlyList<int> targetIds)
        {
            for (int i = 0; i < targetIds.Count; i++)
                for (int j = i + 1; j < targetIds.Count; j++)
                    if (targetIds[i] == targetIds[j]) return true;
            return false;
        }
        private static GameplayPhaseMask PhaseMask(PhaseContextKind phase)
        {
            switch (phase)
            {
                case PhaseContextKind.Build: return GameplayPhaseMask.Build;
                case PhaseContextKind.Wave: return GameplayPhaseMask.Wave;
                case PhaseContextKind.Intermission: return GameplayPhaseMask.Intermission;
                default: return GameplayPhaseMask.None;
            }
        }
        private static float ResolveMagnitude(ComponentStore store, ExecutionDefinition execution,
            AbilityActivationRequest request, int sourceId)
        {
            float magnitude;
            if (!float.IsNaN(request.MagnitudeOverride)) magnitude = request.MagnitudeOverride;
            else if (execution.MagnitudeSource != MagnitudeSource.Multiplier) magnitude = execution.Magnitude;
            else
            {
                float basis = store.EnemyActive[sourceId] ? store.EnemyDamage[sourceId]
                    : store.TowerActive[sourceId] ? store.TowerAttackDamage[sourceId]
                    : sourceId == store.PlayerEntityId ? store.GetPlayerAttackDamageProjection(sourceId)
                    : 0f;
                magnitude = Math.Max(0f, basis * execution.Magnitude);
            }
            return magnitude * request.MagnitudeScale;
        }
        private static AbilityActivationResult Reject(AbilityActivationRequest request, AbilityActivationRejectReason reason) => new AbilityActivationResult(false, request.OwnerId, request.Slot, reason);

        public static AbilityActivationResult TryActivate(float[] cooldowns, AbilityActivationRequest request)
        {
            var reason = cooldowns == null || request.Slot < 0 || request.Slot >= (cooldowns?.Length ?? 0)
                ? AbilityActivationRejectReason.InvalidRequest
                : cooldowns[request.Slot] > 0f ? AbilityActivationRejectReason.Cooldown : AbilityActivationRejectReason.None;
            return new AbilityActivationResult(reason == AbilityActivationRejectReason.None, request.OwnerId, request.Slot, reason);
        }

        public static AbilityActivationResult AbilityCommit(float[] cooldowns, AbilityActivationRequest request)
        {
            var ready = TryActivate(cooldowns, request);
            if (!ready.Accepted) return ready;
            bool accepted = AbilityCommit(cooldowns, request.Slot, request.Cooldown);
            return new AbilityActivationResult(accepted, request.OwnerId, request.Slot,
                accepted ? AbilityActivationRejectReason.None : AbilityActivationRejectReason.Cooldown);
        }

        public static bool TryActivate(float[] cooldowns, int index)
        {
            return cooldowns != null && index >= 0 && index < cooldowns.Length && cooldowns[index] <= 0f;
        }

        public static bool AbilityCommit(float[] cooldowns, int index, float cooldown)
        {
            if (!TryActivate(cooldowns, index)) return false;
            cooldowns[index] = System.Math.Max(0f, cooldown);
            return true;
        }

        public static bool TickCooldown(float[] cooldowns, int index, float deltaSeconds)
        {
            if (cooldowns == null || deltaSeconds <= 0f || index < 0 || index >= cooldowns.Length) return false;
            cooldowns[index] = System.Math.Max(0f, cooldowns[index] - deltaSeconds);
            return true;
        }

        public static bool TryActivate(ComponentStore store, int entityId, int slot, out AbilityInstance ability)
        {
            ability = default(AbilityInstance);
            if (store == null || entityId < 0 || entityId >= ComponentStore.MAX_ENTITIES ||
                slot < 0 || slot >= store.AbilityCount[entityId]) return false;
            ability = store.GetAbility(entityId, slot);
            return ability.CanActivate();
        }

        public static bool AbilityCommit(ComponentStore store, int entityId, int slot)
        {
            if (!TryActivate(store, entityId, slot, out var ability)) return false;
            ability.Activate();
            store.SetAbility(entityId, slot, ability);
            return true;
        }

        public static bool TickCooldown(ComponentStore store, int entityId, int slot, float deltaSeconds)
        {
            if (store == null || deltaSeconds <= 0f || entityId < 0 || entityId >= ComponentStore.MAX_ENTITIES ||
                slot < 0 || slot >= store.AbilityCount[entityId]) return false;
            var ability = store.GetAbility(entityId, slot);
            if (ability.CurrentCooldown <= 0f) return true;
            ability.CurrentCooldown = System.Math.Max(0f, ability.CurrentCooldown - deltaSeconds);
            store.SetAbility(entityId, slot, ability);
            return true;
        }
    }
}
