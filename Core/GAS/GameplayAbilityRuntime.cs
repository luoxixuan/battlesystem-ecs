using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Components;

namespace BattleSystemECS.Core.GAS
{
    public enum AbilityActivationRejectReason { None, InvalidRequest, Cooldown, NoTarget, PhaseNotAllowed, TagRequirementsNotMet, Cost, UnsupportedDefinition, QueueOverflow }

    public readonly struct AbilityActivationRequest
    {
        public readonly int SourceEntityId;
        public int OwnerId => SourceEntityId;
        public readonly int Slot;
        public readonly float Cooldown;
        public readonly int TargetId;
        public readonly AbilityId Ability;
        public readonly EffectId? Effect;
        public readonly TriggerId? Trigger;
        public readonly float Cost;
        public readonly float MagnitudeOverride;
        public readonly float MagnitudeScale;
        public readonly int OwnerPlayerId;
        public AbilityActivationRequest(int ownerId, int slot, float cooldown, int targetId = -1,
            AbilityId ability = default(AbilityId), EffectId? effect = null, TriggerId? trigger = null,
            float cost = 0f, int ownerPlayerId = -1)
            : this(ownerId, slot, cooldown, targetId, ability, effect, trigger, cost, float.NaN, ownerPlayerId, 1f) { }
        public AbilityActivationRequest(int ownerId, int slot, float cooldown, int targetId,
            AbilityId ability, EffectId? effect, TriggerId? trigger, float cost, float magnitudeOverride,
            int ownerPlayerId = -1, float magnitudeScale = 1f)
        { SourceEntityId = ownerId; Slot = slot; Cooldown = cooldown; TargetId = targetId; Ability = ability; Effect = effect; Trigger = trigger; Cost = cost; MagnitudeOverride = magnitudeOverride; MagnitudeScale = magnitudeScale; OwnerPlayerId = ownerPlayerId < 0 ? ownerId : ownerPlayerId; }
        public AbilityActivationRequest(int ownerId, int slot, float cooldown, int targetId,
            AbilityId ability, float magnitudeOverride, int ownerPlayerId = -1)
            : this(ownerId, slot, cooldown, targetId, ability, null, null, 0f, magnitudeOverride, ownerPlayerId) { }

        internal AbilityActivationRequest ForTarget(int targetId, float magnitudeScale) =>
            new AbilityActivationRequest(OwnerId, Slot, Cooldown, targetId, Ability, Effect, Trigger,
                Cost, MagnitudeOverride, OwnerPlayerId, magnitudeScale);
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
    /// CanCommit 是只读规划；返回 true 后 Commit 不得 throw；资源/容量竞争走 AbilityCancelled。
    /// </summary>
    public interface IAbilityPayloadHandler
    {
        bool Supports(ExecutionDefinition execution);
        bool CanCommit(AbilityPayloadContext context);
        int Commit(AbilityPayloadContext context);
        /// <summary>载荷占用的 Resolver 容量。默认 0；handler 独占的 execution 不会再走内建 payload 计数。</summary>
        void ContributeCommitCapacity(AbilityPayloadContext context,
            ref int resourceRequests, ref int resourceEvents, ref int damageRequests, ref int damageEvents)
        { }
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
            AbilityQueueCooldownKind QueueKind { get; }
            float[] FloatCooldowns { get; }
            AbilityState[] StateCooldowns { get; }
        }

        internal enum AbilityQueueCooldownKind : byte
        {
            Stored = 0,
            FloatArray = 1,
            StateArray = 2,
            TowerActive = 3,
            PlayerGlobal = 4,
            Hero = 5
        }

        private const byte QueueFlagRequireEnemy = 1;
        private const byte QueueFlagRequireHeal = 2;
        private const byte QueueFlagForbidEffects = 4;
        private const byte QueueFlagIsSingle = 8;
        private const byte QueueFlagMagnitudeOverride = 16;

        private readonly struct CooldownArrayActivationState : IAbilityActivationState
        {
            private readonly float[] _cooldowns;
            private readonly int _slot;
            public CooldownArrayActivationState(float[] cooldowns, int slot)
            { _cooldowns = cooldowns; _slot = slot; }
            public bool IsValid => _cooldowns != null && _slot >= 0 && _slot < _cooldowns.Length;
            public bool IsReady => IsValid && _cooldowns[_slot] <= 0f;
            public void Commit(float cooldown) => _cooldowns[_slot] = Math.Max(0f, cooldown);
            public AbilityQueueCooldownKind QueueKind => AbilityQueueCooldownKind.FloatArray;
            public float[] FloatCooldowns => _cooldowns;
            public AbilityState[] StateCooldowns => null;
        }

        private readonly struct AbilityStateArrayActivationState : IAbilityActivationState
        {
            private readonly AbilityState[] _states;
            private readonly int _slot;
            private readonly AbilityQueueCooldownKind _kind;
            public AbilityStateArrayActivationState(AbilityState[] states, int slot, AbilityQueueCooldownKind kind)
            { _states = states; _slot = slot; _kind = kind; }
            public bool IsValid => _states != null && _slot >= 0 && _slot < _states.Length;
            public bool IsReady => IsValid && _states[_slot].CanActivate();
            public void Commit(float cooldown)
            {
                var state = _states[_slot];
                state.Cooldown = Math.Max(0f, cooldown);
                if (state.MaxCharges > 1 && state.Charges > 0) state.Charges--;
                _states[_slot] = state;
            }
            public AbilityQueueCooldownKind QueueKind => _kind;
            public float[] FloatCooldowns => null;
            public AbilityState[] StateCooldowns => _states;
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
                instance.CurrentCooldown = Math.Max(0f, cooldown);
                if (instance.State.MaxCharges > 1 && instance.State.Charges > 0)
                {
                    var state = instance.State;
                    state.Charges--;
                    instance.State = state;
                }
                _store.SetAbility(_entityId, _slot, instance);
            }
            public AbilityQueueCooldownKind QueueKind => AbilityQueueCooldownKind.Stored;
            public float[] FloatCooldowns => null;
            public AbilityState[] StateCooldowns => null;
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

            public static ActivationTargetSet FromQueue(ComponentStore store, int index)
            {
                byte flags = store.AbilityQueuedFlags[index];
                int count = store.AbilityQueuedTargetCounts[index];
                int start = store.AbilityQueuedTargetStarts[index];
                if ((flags & QueueFlagIsSingle) != 0)
                    return Single(store.AbilityQueuedTargetIds[start]);
                var ids = new int[count];
                var mags = new float[count];
                for (int i = 0; i < count; i++)
                {
                    ids[i] = store.AbilityQueuedTargetIds[start + i];
                    mags[i] = store.AbilityQueuedMagnitudes[start + i];
                }
                var mode = (flags & QueueFlagMagnitudeOverride) != 0
                    ? TargetMagnitudeMode.Override : TargetMagnitudeMode.Scale;
                return new ActivationTargetSet(-1, ids, mags, mode, false,
                    (flags & QueueFlagRequireEnemy) != 0,
                    (flags & QueueFlagRequireHeal) != 0,
                    (flags & QueueFlagForbidEffects) != 0);
            }

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

        public static AbilityActivationResult Activate(ComponentStore store, GameplayCatalog catalog, AbilityState[] cooldowns,
            AbilityActivationRequest request, IAbilityPayloadHandler payloadHandler = null)
            => ActivateCore(store, catalog, new AbilityStateArrayActivationState(cooldowns, request.Slot, IdentifyStateArray(store, cooldowns)),
                request, ActivationTargetSet.Single(request.TargetId >= 0 ? request.TargetId : request.OwnerId), payloadHandler);

        public static AbilityActivationResult Activate(ComponentStore store, GameplayCatalog catalog, AbilityCooldownColumn cooldowns,
            AbilityActivationRequest request, IAbilityPayloadHandler payloadHandler = null)
            => Activate(store, catalog, cooldowns.States, request, payloadHandler);

        /// <summary>
        /// AbilityRequest 主入口：按 Source 句柄与 AbilityId 解析槽位后入队。
        /// Seal 后只写入 AbilityRequests，由 <c>ability.commit</c> drain；未 Seal 则立刻
        /// <see cref="CommitQueuedAbilities"/>。granted effect 经 EffectRequests 入队，
        /// 由 <c>effect.commit</c> <c>TryApply</c>；执行项（伤害/治疗/CC）仍在 ability.commit 当场提交。
        /// </summary>
        public static AbilityActivationResult Activate(ComponentStore store, GameplayCatalog catalog, AbilityRequest request,
            IAbilityPayloadHandler payloadHandler = null)
        {
            if (store == null || catalog == null || !request.Source.IsValid)
                return new AbilityActivationResult(false, request.Source.Index, -1, AbilityActivationRejectReason.InvalidRequest);
            if (!catalog.TryGetAbility(request.Ability, out var resolved))
                return new AbilityActivationResult(false, request.Source.Index, -1, AbilityActivationRejectReason.InvalidRequest);
            int slot = FindStoredSlot(store, request.Source.Index, request.Ability, resolved.Name);
            if (slot < 0)
                return new AbilityActivationResult(false, request.Source.Index, -1, AbilityActivationRejectReason.InvalidRequest);
            var activation = new AbilityActivationRequest(request.Source.Index, slot, resolved.Cooldown,
                request.Target.IsValid ? request.Target.Index : request.Source.Index, request.Ability,
                ownerPlayerId: request.Source.Index);
            return Activate(store, catalog, request.Source.Index, slot, activation, payloadHandler);
        }

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
            AbilityState[] cooldowns, AbilityActivationRequest request, IReadOnlyList<int> targetIds,
            IReadOnlyList<float> magnitudeOverrides = null, IAbilityPayloadHandler payloadHandler = null)
            => ActivateCore(store, catalog, new AbilityStateArrayActivationState(cooldowns, request.Slot, IdentifyStateArray(store, cooldowns)),
                request, ActivationTargetSet.Scaled(targetIds, magnitudeOverrides), payloadHandler);

        public static AbilityActivationResult ActivateTargets(ComponentStore store, GameplayCatalog catalog,
            AbilityCooldownColumn cooldowns, AbilityActivationRequest request, IReadOnlyList<int> targetIds,
            IReadOnlyList<float> magnitudeOverrides = null, IAbilityPayloadHandler payloadHandler = null)
            => ActivateTargets(store, catalog, cooldowns.States, request, targetIds, magnitudeOverrides, payloadHandler);

        public static AbilityActivationResult ActivateTargets(ComponentStore store, GameplayCatalog catalog,
            int entityId, int slot, AbilityActivationRequest request, IReadOnlyList<int> targetIds,
            IReadOnlyList<float> magnitudeScales = null, IAbilityPayloadHandler payloadHandler = null)
            => ActivateCore(store, catalog, new StoredAbilityActivationState(store, entityId, slot),
                request, ActivationTargetSet.Scaled(targetIds, magnitudeScales), payloadHandler);

        /// <summary>
        /// 冻结准入序：第一段 InvalidRequest / 形状 NoTarget / ForbidEffects·heal-only UnsupportedDefinition；
        /// 第二段 PhaseNotAllowed → Cooldown → Cost → 实体 NoTarget → Tag → 时长/CanCommit/未知 execution；
        /// 第三段 QueueOverflow。形状类检查不得挪到 Cost 之后。
        /// </summary>
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
            // 第二段：PhaseNotAllowed → Cooldown → Cost；容量仍在末尾 QueueOverflow。
            var phaseReject = ValidatePhase(store, ability);
            if (phaseReject != AbilityActivationRejectReason.None) return Reject(request, phaseReject);
            if (!activationState.IsReady)
                return Reject(request, AbilityActivationRejectReason.Cooldown);

            var validation = BuildActivationPlan(store, catalog, ability, request, source, targets, payloadHandler);
            if (validation != AbilityActivationRejectReason.None) return Reject(request, validation);

            if (!TryEnqueueActivation(store, catalog, activationState, request, source, targets, payloadHandler))
                return Reject(request, AbilityActivationRejectReason.QueueOverflow);
            if (store.DeferAbilityAndEffectCommit)
                return new AbilityActivationResult(true, request.OwnerId, request.Slot, appliedEffects: 0);
            return CommitQueuedAbilities(store);
        }

        private static AbilityQueueCooldownKind IdentifyStateArray(ComponentStore store, AbilityState[] states)
        {
            if (store == null || states == null) return AbilityQueueCooldownKind.StateArray;
            if (ReferenceEquals(states, store.TowerActiveCooldown.States)) return AbilityQueueCooldownKind.TowerActive;
            if (ReferenceEquals(states, store.PlayerGlobalSkillCooldown.States)) return AbilityQueueCooldownKind.PlayerGlobal;
            if (ReferenceEquals(states, store.HeroSkillCooldown.States)) return AbilityQueueCooldownKind.Hero;
            return AbilityQueueCooldownKind.StateArray;
        }

        private static bool TryEnqueueActivation<TState>(ComponentStore store, GameplayCatalog catalog,
            TState activationState, AbilityActivationRequest request, EntityHandle source,
            ActivationTargetSet targets, IAbilityPayloadHandler payloadHandler)
            where TState : struct, IAbilityActivationState
        {
            int packedStart = store.AbilityQueuedTargetFill;
            if (targets.Count < 0 || packedStart > ComponentStore.MAX_ABILITY_QUEUE_TARGET_SLOTS - targets.Count)
                return false;
            if (!store.AbilityRequests.CanAdd(1))
            {
                store.AbilityRequests.RecordCapacityRejection(false);
                return false;
            }
            int index = store.AbilityRequests.Count;
            byte flags = 0;
            if (targets.RequireEnemy) flags |= QueueFlagRequireEnemy;
            if (targets.RequireHealExecutions) flags |= QueueFlagRequireHeal;
            if (targets.ForbidEffects) flags |= QueueFlagForbidEffects;
            if (targets.IsSingle) flags |= QueueFlagIsSingle;
            store.AbilityQueuedCatalogs[index] = catalog;
            store.AbilityQueuedHandlers[index] = payloadHandler;
            store.AbilityQueuedActivations[index] = request;
            store.AbilityQueuedTargetStarts[index] = packedStart;
            store.AbilityQueuedTargetCounts[index] = targets.Count;
            store.AbilityQueuedFlags[index] = flags;
            store.AbilityQueuedCooldownKinds[index] = (byte)activationState.QueueKind;
            store.AbilityQueuedFloatArrays[index] = activationState.FloatCooldowns;
            store.AbilityQueuedStateArrays[index] = activationState.StateCooldowns;
            for (int i = 0; i < targets.Count; i++)
            {
                int slot = packedStart + i;
                store.AbilityQueuedTargetIds[slot] = targets.TargetIdAt(i);
                store.AbilityQueuedMagnitudes[slot] = 1f;
                if (!targets.IsSingle && targets.TryRequestAt(request, i, out var mapped))
                {
                    if (!float.IsNaN(mapped.MagnitudeOverride))
                    {
                        store.AbilityQueuedMagnitudes[slot] = mapped.MagnitudeOverride;
                        store.AbilityQueuedFlags[index] |= QueueFlagMagnitudeOverride;
                    }
                    else
                        store.AbilityQueuedMagnitudes[slot] = mapped.MagnitudeScale;
                }
            }
            store.AbilityQueuedTargetFill = packedStart + targets.Count;
            int firstTarget = targets.TargetIdAt(0);
            if (!TryReserveActivation(store, catalog, request, source, targets, payloadHandler, index))
            {
                store.AbilityQueuedTargetFill = packedStart;
                return false;
            }
            if (!store.AbilityRequests.TryAdd(new AbilityRequest(source, request.Ability,
                store.GetEntityHandle(firstTarget), store.AllocateGameplaySequence(request.OwnerId))))
            {
                store.AbilityCommitReservation.Release(index);
                store.AbilityQueuedTargetFill = packedStart;
                return false;
            }
            return true;
        }

        public static AbilityActivationResult CommitQueuedAbilities(ComponentStore store)
        {
            if (store == null) return new AbilityActivationResult(false, -1, -1, AbilityActivationRejectReason.InvalidRequest);
            int applied = 0;
            int lastOwner = -1;
            int lastSlot = -1;
            bool anyAccepted = false;
            AbilityActivationRejectReason lastCancel = AbilityActivationRejectReason.None;
            int count = store.AbilityRequests.Count;
            for (int i = 0; i < count; i++)
            {
                var catalog = store.AbilityQueuedCatalogs[i];
                var request = store.AbilityQueuedActivations[i];
                lastOwner = request.OwnerId;
                lastSlot = request.Slot;
                if (catalog == null || !catalog.TryGetAbility(request.Ability, out var ability))
                    throw new InvalidOperationException("queued ability is missing from catalog");
                var targets = ActivationTargetSet.FromQueue(store, i);
                var source = store.GetEntityHandle(request.OwnerId);
                var handler = store.AbilityQueuedHandlers[i];
                // 先从预留表摘掉本条，复查只看到更晚请求的预留 + 当前真实值。
                store.AbilityCommitReservation.Release(i);
                var recheck = RecheckQueuedActivation(store, catalog, ability, request, source, targets, handler, i);
                if (recheck != AbilityActivationRejectReason.None)
                {
                    PublishCancelled(store, request, source, targets, recheck);
                    lastCancel = recheck;
                    continue;
                }
                // 先 Spend 再 CommitPlan：Plan 失败时退款，避免效果已落、费用未扣。
                if (!CommitCosts(store, ability, request, source))
                {
                    PublishCancelled(store, request, source, targets, AbilityActivationRejectReason.Cost);
                    lastCancel = AbilityActivationRejectReason.Cost;
                    continue;
                }
                int requestApplied = 0;
                bool planOk = true;
                AbilityActivationRejectReason planFail = AbilityActivationRejectReason.None;
                for (int t = 0; t < targets.Count; t++)
                {
                    targets.TryRequestAt(request, t, out var targetRequest);
                    int targetId = targets.TargetIdAt(t);
                    int targetApplied = CommitPlan(store, catalog, ability, targetRequest, source,
                        store.GetEntityHandle(targetId), handler, out planFail);
                    if (targetApplied < 0)
                    {
                        planOk = false;
                        break;
                    }
                    requestApplied += targetApplied;
                }
                if (!planOk)
                {
                    RefundCosts(store, ability, request, source);
                    PublishCancelled(store, request, source, targets, planFail);
                    lastCancel = planFail;
                    continue;
                }
                CommitQueuedCooldown(store, i, request, ability.Cooldown);
                int firstTargetId = targets.TargetIdAt(0);
                PublishActivation(store, request, source, store.GetEntityHandle(firstTargetId), firstTargetId);
                applied += requestApplied;
                anyAccepted = true;
            }
            store.ClearAbilityQueue();
            if (count == 0)
                return new AbilityActivationResult(true, lastOwner, lastSlot, appliedEffects: 0);
            if (anyAccepted)
                return new AbilityActivationResult(true, lastOwner, lastSlot, appliedEffects: applied);
            return new AbilityActivationResult(false, lastOwner, lastSlot,
                lastCancel == AbilityActivationRejectReason.None ? AbilityActivationRejectReason.Cost : lastCancel);
        }

        public static void RejectQueuedAbilities(ComponentStore store)
        {
            if (store == null) return;
            int n = store.AbilityRequests.Count;
            if (n > 0) store.UnconsumedAbilityRequests += n;
            store.ClearAbilityQueue();
        }

        private static void CommitQueuedCooldown(ComponentStore store, int index, AbilityActivationRequest request,
            float cooldown)
        {
            var kind = (AbilityQueueCooldownKind)store.AbilityQueuedCooldownKinds[index];
            switch (kind)
            {
                case AbilityQueueCooldownKind.FloatArray:
                    new CooldownArrayActivationState(store.AbilityQueuedFloatArrays[index], request.Slot).Commit(cooldown);
                    break;
                case AbilityQueueCooldownKind.StateArray:
                case AbilityQueueCooldownKind.TowerActive:
                case AbilityQueueCooldownKind.PlayerGlobal:
                case AbilityQueueCooldownKind.Hero:
                    new AbilityStateArrayActivationState(ResolveQueuedStates(store, index, kind), request.Slot, kind)
                        .Commit(cooldown);
                    break;
                default:
                    new StoredAbilityActivationState(store, request.OwnerId, request.Slot).Commit(cooldown);
                    break;
            }
        }

        private static AbilityState[] ResolveQueuedStates(ComponentStore store, int index, AbilityQueueCooldownKind kind)
        {
            switch (kind)
            {
                case AbilityQueueCooldownKind.TowerActive: return store.TowerActiveCooldown.States;
                case AbilityQueueCooldownKind.PlayerGlobal: return store.PlayerGlobalSkillCooldown.States;
                case AbilityQueueCooldownKind.Hero: return store.HeroSkillCooldown.States;
                default: return store.AbilityQueuedStateArrays[index];
            }
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
                : AbilityActivationRejectReason.QueueOverflow;
        }

        private static AbilityActivationRejectReason ValidatePhase(ComponentStore store, AbilityDefinition ability)
        {
            GameplayPhaseMask phase = PhaseMask(store.GameplayPhaseContext.Kind);
            if (phase == GameplayPhaseMask.None || (ability.AllowedPhases & phase) == 0)
                return AbilityActivationRejectReason.PhaseNotAllowed;
            return AbilityActivationRejectReason.None;
        }

        private static AbilityActivationRejectReason ValidatePlan(ComponentStore store, GameplayCatalog catalog,
            AbilityDefinition ability, AbilityActivationRequest request, EntityHandle source, EntityHandle target,
            IAbilityPayloadHandler payloadHandler)
        {
            if (!GameplayTagRuntime.Matches(store, source.Index, ability.RequiredTags, ability.BlockedTags) ||
                !GameplayTagRuntime.Matches(store, target.Index,
                    ability.Targeting.RequiredTags, ability.Targeting.BlockedTags))
                return AbilityActivationRejectReason.TagRequirementsNotMet;
            for (int i = 0; i < ability.Effects.Count; i++)
            {
                if (!catalog.TryGetEffect(ability.Effects[i], out var effect))
                    return AbilityActivationRejectReason.InvalidRequest;
                if (!GameplayEffectRuntime.IsDurationContractValid(effect))
                    return AbilityActivationRejectReason.UnsupportedDefinition;
            }
            for (int i = 0; i < ability.Executions.Count; i++)
            {
                if (!catalog.TryGetExecution(ability.Executions[i], out var execution))
                    return AbilityActivationRejectReason.UnsupportedDefinition;
                float magnitude = ResolveMagnitude(store, execution, request, source.Index);
                var context = new AbilityPayloadContext(store, ability, execution, request, source, target, magnitude);
                if (payloadHandler != null && payloadHandler.Supports(execution))
                {
                    if (!payloadHandler.CanCommit(context)) return AbilityActivationRejectReason.UnsupportedDefinition;
                    continue;
                }
                if (!TryBuildBuiltInPayload(context, out _, out bool supported))
                    return supported ? AbilityActivationRejectReason.InvalidRequest
                        : AbilityActivationRejectReason.UnsupportedDefinition;
            }
            return AbilityActivationRejectReason.None;
        }

        private enum BuiltInPayloadKind { Damage, Heal, Shield, Slow, CrowdControl, Freeze, GameplayEvent }

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

        private static void AccumulateExecutionCapacity(IAbilityPayloadHandler payloadHandler,
            AbilityPayloadContext context, ref long damageRequests, ref long damageEvents,
            ref long resourceRequests, ref long resourceEvents)
        {
            if (payloadHandler != null && payloadHandler.Supports(context.Execution))
            {
                int requests = 0, events = 0, damageReq = 0, damageEvt = 0;
                payloadHandler.ContributeCommitCapacity(context, ref requests, ref events, ref damageReq, ref damageEvt);
                resourceRequests += requests;
                resourceEvents += events;
                damageRequests += damageReq;
                damageEvents += damageEvt;
                return;
            }
            TryBuildBuiltInPayload(context, out var payload, out _);
            damageRequests += payload.DamageRequests;
            damageEvents += payload.DamageEvents;
            resourceRequests += payload.ResourceRequests;
            resourceEvents += payload.ResourceEvents;
        }

        private static bool ValidateCapacityPlan(ComponentStore store, GameplayCatalog catalog,
            AbilityDefinition ability, AbilityActivationRequest request, EntityHandle source,
            ActivationTargetSet targets, IAbilityPayloadHandler payloadHandler)
        {
            long runtimeSlots = 0;
            long modifiers = 0;
            bool single = targets.IsSingle;
            int occupancyTarget = targets.Count > 0 ? targets.TargetIdAt(0) : -1;
            for (int i = 0; i < ability.Effects.Count; i++)
            {
                catalog.TryGetEffect(ability.Effects[i], out var effect);
                if (single)
                {
                    store.GameplayEffectsRuntime.CountPlanOccupancy(occupancyTarget, effect,
                        out int occupancyRuntime, out int occupancyModifiers);
                    runtimeSlots += occupancyRuntime;
                    modifiers += occupancyModifiers;
                }
                else
                {
                    if (effect.Type != EffectType.Instant) runtimeSlots++;
                    modifiers += effect.Modifiers.Count;
                }
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
                    AccumulateExecutionCapacity(payloadHandler, context,
                        ref damageRequests, ref damageEvents, ref resourceRequests, ref resourceEvents);
                }
            }
            for (int i = 0; i < ability.Costs.Count; i++)
                if (EffectiveCost(ability, request, i) != 0f) { resourceRequests++; resourceEvents++; }
            long effectEvents = (long)ability.Effects.Count * targets.Count;
            if (runtimeSlots > int.MaxValue || modifiers > int.MaxValue || effectEvents > int.MaxValue ||
                damageRequests > int.MaxValue || damageEvents > int.MaxValue ||
                resourceRequests > int.MaxValue || resourceEvents > int.MaxValue) return false;
            bool effectsOk;
            int extraModifiers = store.AbilityCommitReservation.Modifiers;
            int extraEffectEvents = store.AbilityCommitReservation.EffectEvents;
            if (targets.IsSingle)
            {
                int targetId = targets.TargetIdAt(0);
                effectsOk = store.GameplayEffectsRuntime.CanApplyPlan(targetId,
                    (int)runtimeSlots + store.AbilityCommitReservation.RuntimeFor(targetId),
                    (int)modifiers + extraModifiers, (int)effectEvents + extraEffectEvents);
            }
            else
            {
                effectsOk = store.GameplayEffectsRuntime.Events.CanPublish((int)effectEvents + extraEffectEvents, true);
                long totalRuntime = runtimeSlots * targets.Count + store.AbilityCommitReservation.TotalRuntime;
                if (totalRuntime > store.GameplayEffectPool.FreeCount) effectsOk = false;
                else
                {
                    for (int t = 0; t < targets.Count && effectsOk; t++)
                    {
                        int targetId = targets.TargetIdAt(t);
                        if (!store.GameplayEffectsRuntime.CanApplyPlan(targetId,
                            (int)runtimeSlots + store.AbilityCommitReservation.RuntimeFor(targetId),
                            (int)modifiers + extraModifiers, 0))
                            effectsOk = false;
                    }
                }
            }
            return effectsOk &&
                   store.EffectRequests.CanAdd((int)effectEvents + store.AbilityCommitReservation.EffectRequests) &&
                   store.DamageResolver.CanAccept((int)damageRequests + store.AbilityCommitReservation.DamageRequests,
                       (int)damageEvents + store.AbilityCommitReservation.DamageEvents) &&
                   store.ResourceResolver.CanAccept((int)resourceRequests + store.AbilityCommitReservation.ResourceRequests,
                       (int)resourceEvents + store.AbilityCommitReservation.ResourceEvents);
        }

        private static int CommitPlan(ComponentStore store, GameplayCatalog catalog, AbilityDefinition ability,
            AbilityActivationRequest request, EntityHandle source, EntityHandle target,
            IAbilityPayloadHandler payloadHandler, out AbilityActivationRejectReason failReason)
        {
            failReason = AbilityActivationRejectReason.None;
            var resourceBefore = store.ResourceResolver.LastRejectionReason;
            var damageBefore = store.DamageResolver.LastRejection;
            int applied = 0;
            bool damageInThisPlanQueuedTargetDeath = false;
            for (int i = 0; i < ability.Effects.Count; i++)
            {
                catalog.TryGetEffect(ability.Effects[i], out var effect);
                if (!store.GameplayEffectsRuntime.EnqueueApply(effect.Id, effect, source, target,
                    request.OwnerPlayerId, float.NaN))
                {
                    failReason = AbilityActivationRejectReason.QueueOverflow;
                    return -1;
                }
                applied++;
            }
            for (int i = 0; i < ability.Executions.Count; i++)
            {
                catalog.TryGetExecution(ability.Executions[i], out var execution);
                float magnitude = ResolveMagnitude(store, execution, request, source.Index);
                var context = new AbilityPayloadContext(store, ability, execution, request, source, target, magnitude);
                if (payloadHandler != null && payloadHandler.Supports(execution))
                {
                    int committed = payloadHandler.Commit(context);
                    if (committed < 0)
                    {
                        failReason = MapCommitFailure(store, resourceBefore, damageBefore);
                        return -1;
                    }
                    applied += committed;
                }
                else if (damageInThisPlanQueuedTargetDeath && execution.Payload == EffectPayloadKind.Damage &&
                         store.IsEnemyPendingDeath(target.Index)) applied++;
                else if (!TryBuildBuiltInPayload(context, out var payload, out _) || !CommitBuiltIn(payload, context))
                {
                    failReason = MapCommitFailure(store, resourceBefore, damageBefore);
                    return -1;
                }
                else
                {
                    applied++;
                    if (execution.Payload == EffectPayloadKind.Damage && store.IsEnemyPendingDeath(target.Index))
                        damageInThisPlanQueuedTargetDeath = true;
                }
            }
            return applied;
        }

        private static AbilityActivationRejectReason MapCommitFailure(ComponentStore store,
            ResourceRejectionReason resourceBefore, DamageRejectionReason damageBefore)
        {
            var resource = store.ResourceResolver.LastRejectionReason;
            if (resource != resourceBefore) return MapResourceReject(resource);
            var damage = store.DamageResolver.LastRejection;
            if (damage != damageBefore) return MapDamageReject(damage);
            return AbilityActivationRejectReason.UnsupportedDefinition;
        }

        private static AbilityActivationRejectReason MapResourceReject(ResourceRejectionReason reason)
        {
            switch (reason)
            {
                case ResourceRejectionReason.RequestQueueOverflow: return AbilityActivationRejectReason.QueueOverflow;
                case ResourceRejectionReason.Insufficient: return AbilityActivationRejectReason.Cost;
                case ResourceRejectionReason.TargetAlreadyDead:
                case ResourceRejectionReason.InvalidTarget:
                case ResourceRejectionReason.InvalidSource:
                    return AbilityActivationRejectReason.NoTarget;
                default: return AbilityActivationRejectReason.InvalidRequest;
            }
        }

        private static AbilityActivationRejectReason MapDamageReject(DamageRejectionReason reason)
        {
            switch (reason)
            {
                case DamageRejectionReason.RequestQueueOverflow: return AbilityActivationRejectReason.QueueOverflow;
                case DamageRejectionReason.TargetAlreadyDead:
                case DamageRejectionReason.InvalidTarget:
                case DamageRejectionReason.InvalidSource:
                    return AbilityActivationRejectReason.NoTarget;
                default: return AbilityActivationRejectReason.InvalidRequest;
            }
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
                case EffectPayloadKind.Freeze:
                    if (!Matches(execution.Operation, ExecutionOperation.ApplyFreeze)) { supported = false; return false; }
                    kind = BuiltInPayloadKind.Freeze; break;
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
                case BuiltInPayloadKind.Freeze:
                    return magnitude > 0f && execution.Probability >= 0f && execution.Probability <= 1f && enemy;
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
                case BuiltInPayloadKind.Freeze:
                    if (ShouldApplyProbability(context))
                        store.ApplyEnemyFreeze(targetId, Math.Max(1, (int)Math.Ceiling(context.Magnitude)));
                    return true;
                case BuiltInPayloadKind.GameplayEvent:
                    return store.DamageResolver.Events.TryPublish(new GameplayEvent(GameplayEventType.EffectApplied,
                        context.Source, context.Target, sequence, ownerPlayerId: context.Request.OwnerPlayerId));
                default:
                    return false;
            }
        }

        private static bool ValidateCosts(ComponentStore store, AbilityDefinition ability,
            AbilityActivationRequest request, EntityHandle source, bool accountReservation = true)
        {
            for (int i = 0; i < ability.Costs.Count; i++)
            {
                float amount = EffectiveCost(ability, request, i);
                if (amount < 0f || float.IsNaN(amount) || float.IsInfinity(amount) ||
                    !TryGetResource(store, source.Index, ability.Costs[i].Resource, out float available)) return false;
                if (accountReservation)
                    available -= store.AbilityCommitReservation.PeekCost(source.Index, ability.Costs[i].Resource);
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
                var spend = new ResourceRequest(source, source, ability.Costs[i].Resource, amount,
                    ResourceOperation.Spend, 0, store.AllocateGameplaySequence(source.Index), request.OwnerPlayerId);
                var result = store.ResourceResolver.TryApply(spend);
                if (!result.Accepted || result.Applied != -amount)
                {
                    RefundCostRange(store, ability, request, source, 0, i);
                    return false;
                }
            }
            return true;
        }

        private static void RefundCosts(ComponentStore store, AbilityDefinition ability,
            AbilityActivationRequest request, EntityHandle source) =>
            RefundCostRange(store, ability, request, source, 0, ability.Costs.Count);

        private static void RefundCostRange(ComponentStore store, AbilityDefinition ability,
            AbilityActivationRequest request, EntityHandle source, int startInclusive, int endExclusive)
        {
            for (int i = startInclusive; i < endExclusive; i++)
            {
                float amount = EffectiveCost(ability, request, i);
                if (amount == 0f) continue;
                var refund = new ResourceRequest(source, source, ability.Costs[i].Resource, amount,
                    ResourceOperation.Add, 0, store.AllocateGameplaySequence(source.Index), request.OwnerPlayerId);
                store.ResourceResolver.TryApply(refund);
            }
        }

        private static float EffectiveCost(AbilityDefinition ability, AbilityActivationRequest request, int index) =>
            request.Cost > 0f && ability.Costs.Count == 1 && index == 0 ? request.Cost : ability.Costs[index].Amount;

        private static bool TryReserveActivation(ComponentStore store, GameplayCatalog catalog,
            AbilityActivationRequest request, EntityHandle source, ActivationTargetSet targets,
            IAbilityPayloadHandler payloadHandler, int queueIndex)
        {
            if (!catalog.TryGetAbility(request.Ability, out var ability)) return false;
            if (!TryBuildCapacityNeed(store, catalog, ability, request, source, targets, payloadHandler, out var need))
                return false;
            int costCount = ability.Costs.Count;
            var reservation = store.AbilityCommitReservation;
            if (costCount > reservation.CostKeyScratch.Length || targets.Count > reservation.RuntimeTargetScratch.Length)
                return false;
            for (int i = 0; i < costCount; i++)
            {
                reservation.CostKeyScratch[i] = ability.Costs[i].Resource.Value;
                reservation.CostAmountScratch[i] = EffectiveCost(ability, request, i);
            }
            for (int i = 0; i < targets.Count; i++)
                reservation.RuntimeTargetScratch[i] = targets.TargetIdAt(i);
            return reservation.TryReserve(queueIndex, need, source.Index,
                reservation.RuntimeTargetScratch, targets.Count, reservation.CostKeyScratch,
                reservation.CostAmountScratch, costCount);
        }

        private static bool TryBuildCapacityNeed(ComponentStore store, GameplayCatalog catalog,
            AbilityDefinition ability, AbilityActivationRequest request, EntityHandle source,
            ActivationTargetSet targets, IAbilityPayloadHandler payloadHandler,
            out AbilityCommitReservation.CapacityNeed need)
        {
            need = default(AbilityCommitReservation.CapacityNeed);
            long runtimeSlots = 0;
            long modifiers = 0;
            bool single = targets.IsSingle;
            int occupancyTarget = targets.Count > 0 ? targets.TargetIdAt(0) : -1;
            for (int i = 0; i < ability.Effects.Count; i++)
            {
                catalog.TryGetEffect(ability.Effects[i], out var effect);
                if (single)
                {
                    store.GameplayEffectsRuntime.CountPlanOccupancy(occupancyTarget, effect,
                        out int occupancyRuntime, out int occupancyModifiers);
                    runtimeSlots += occupancyRuntime;
                    modifiers += occupancyModifiers;
                }
                else
                {
                    if (effect.Type != EffectType.Instant) runtimeSlots++;
                    modifiers += effect.Modifiers.Count;
                }
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
                    AccumulateExecutionCapacity(payloadHandler, context,
                        ref damageRequests, ref damageEvents, ref resourceRequests, ref resourceEvents);
                }
            }
            for (int i = 0; i < ability.Costs.Count; i++)
                if (EffectiveCost(ability, request, i) != 0f) { resourceRequests++; resourceEvents++; }
            long effectEvents = (long)ability.Effects.Count * targets.Count;
            if (runtimeSlots > int.MaxValue || modifiers > int.MaxValue || effectEvents > int.MaxValue ||
                damageRequests > int.MaxValue || damageEvents > int.MaxValue ||
                resourceRequests > int.MaxValue || resourceEvents > int.MaxValue) return false;
            need = new AbilityCommitReservation.CapacityNeed
            {
                EffectRequests = (int)effectEvents,
                EffectEvents = (int)effectEvents,
                DamageRequests = (int)damageRequests,
                DamageEvents = (int)damageEvents,
                ResourceRequests = (int)resourceRequests,
                ResourceEvents = (int)resourceEvents,
                Modifiers = (int)modifiers,
                RuntimeSlots = (int)runtimeSlots
            };
            return true;
        }

        private static AbilityActivationRejectReason RecheckQueuedActivation(ComponentStore store,
            GameplayCatalog catalog, AbilityDefinition ability, AbilityActivationRequest request,
            EntityHandle source, ActivationTargetSet targets, IAbilityPayloadHandler payloadHandler, int index)
        {
            if (!QueuedCooldownReady(store, index, request))
                return AbilityActivationRejectReason.Cooldown;
            // 本条预留已 Release；Cost 只看当前值（更早请求已 Spend）。不查 Tag。
            if (!ValidateCosts(store, ability, request, source, accountReservation: false))
                return AbilityActivationRejectReason.Cost;
            if (!ValidateCapacityPlan(store, catalog, ability, request, source, targets, payloadHandler))
                return AbilityActivationRejectReason.QueueOverflow;
            return RecheckPayloadCanCommit(store, catalog, ability, request, source, targets, payloadHandler);
        }

        private static AbilityActivationRejectReason RecheckPayloadCanCommit(ComponentStore store,
            GameplayCatalog catalog, AbilityDefinition ability, AbilityActivationRequest request,
            EntityHandle source, ActivationTargetSet targets, IAbilityPayloadHandler payloadHandler)
        {
            if (payloadHandler == null) return AbilityActivationRejectReason.None;
            for (int t = 0; t < targets.Count; t++)
            {
                targets.TryRequestAt(request, t, out var targetRequest);
                int targetId = targets.TargetIdAt(t);
                var target = store.GetEntityHandle(targetId);
                for (int i = 0; i < ability.Executions.Count; i++)
                {
                    catalog.TryGetExecution(ability.Executions[i], out var execution);
                    if (!payloadHandler.Supports(execution)) continue;
                    float magnitude = ResolveMagnitude(store, execution, targetRequest, source.Index);
                    var context = new AbilityPayloadContext(store, ability, execution, targetRequest,
                        source, target, magnitude);
                    if (payloadHandler.CanCommit(context)) continue;
                    if (execution.Payload == EffectPayloadKind.Damage &&
                        (uint)targetId < ComponentStore.MAX_PLAYERS)
                    {
                        bool canApply = store.ResourceResolver.CanApplyPlayerDamage(new PlayerDamageRequest(
                            source, target, magnitude, 0L, ability.Id, targetId));
                        return canApply
                            ? AbilityActivationRejectReason.QueueOverflow
                            : AbilityActivationRejectReason.NoTarget;
                    }
                    return AbilityActivationRejectReason.UnsupportedDefinition;
                }
            }
            return AbilityActivationRejectReason.None;
        }

        private static bool QueuedCooldownReady(ComponentStore store, int index, AbilityActivationRequest request)
        {
            var kind = (AbilityQueueCooldownKind)store.AbilityQueuedCooldownKinds[index];
            switch (kind)
            {
                case AbilityQueueCooldownKind.FloatArray:
                    return new CooldownArrayActivationState(store.AbilityQueuedFloatArrays[index], request.Slot).IsReady;
                case AbilityQueueCooldownKind.StateArray:
                case AbilityQueueCooldownKind.TowerActive:
                case AbilityQueueCooldownKind.PlayerGlobal:
                case AbilityQueueCooldownKind.Hero:
                    return new AbilityStateArrayActivationState(ResolveQueuedStates(store, index, kind), request.Slot, kind)
                        .IsReady;
                default:
                    return new StoredAbilityActivationState(store, request.OwnerId, request.Slot).IsReady;
            }
        }

        private static void PublishCancelled(ComponentStore store, AbilityActivationRequest request,
            EntityHandle source, ActivationTargetSet targets, AbilityActivationRejectReason reason)
        {
            int targetId = targets.Count > 0 ? targets.TargetIdAt(0) : request.OwnerId;
            var target = store.GetEntityHandle(targetId);
            store.DamageResolver.Events.TryPublish(new GameplayEvent(GameplayEventType.AbilityCancelled, source, target,
                store.AllocateGameplaySequence(targetId), reason: (int)reason, ownerPlayerId: request.OwnerPlayerId));
        }

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

        private static bool ShouldApplyProbability(AbilityPayloadContext context)
        {
            float probability = context.Execution.Probability;
            if (probability <= 0f) return false;
            if (probability >= 1f) return true;
            uint value;
            unchecked
            {
                value = (uint)context.Store.CurrentFrame * 2246822519u;
                value ^= (uint)context.Ability.Id.Value * 3266489917u;
                value ^= (uint)context.Source.Index * 668265263u;
                value ^= (uint)context.Source.Generation * 374761393u;
                value ^= (uint)context.Target.Index * 1274126177u;
                value ^= (uint)context.Target.Generation * 1431374977u;
                value ^= value >> 15;
                value *= 2246822519u;
                value ^= value >> 13;
            }
            return (value & 0x00FFFFFFu) < probability * 16777216f;
        }

        private static void PublishActivation(ComponentStore store, AbilityActivationRequest request,
            EntityHandle source, EntityHandle target, int targetId) =>
            store.DamageResolver.Events.TryPublish(new GameplayEvent(GameplayEventType.AbilityActivated, source, target,
                store.AllocateGameplaySequence(targetId), ownerPlayerId: request.OwnerPlayerId));
        public static AbilityActivationResult ActivateHealTargets(ComponentStore store, GameplayCatalog catalog,
            float[] cooldowns, AbilityActivationRequest request, IReadOnlyList<int> targetIds,
            IReadOnlyList<float> magnitudes)
            => ActivateCore(store, catalog, new CooldownArrayActivationState(cooldowns, request.Slot),
                request, ActivationTargetSet.Heals(targetIds, magnitudes), null);

        public static AbilityActivationResult ActivateHealTargets(ComponentStore store, GameplayCatalog catalog,
            AbilityState[] cooldowns, AbilityActivationRequest request, IReadOnlyList<int> targetIds,
            IReadOnlyList<float> magnitudes)
            => ActivateCore(store, catalog, new AbilityStateArrayActivationState(cooldowns, request.Slot, IdentifyStateArray(store, cooldowns)),
                request, ActivationTargetSet.Heals(targetIds, magnitudes), null);

        public static AbilityActivationResult ActivateHealTargets(ComponentStore store, GameplayCatalog catalog,
            AbilityCooldownColumn cooldowns, AbilityActivationRequest request, IReadOnlyList<int> targetIds,
            IReadOnlyList<float> magnitudes)
            => ActivateHealTargets(store, catalog, cooldowns.States, request, targetIds, magnitudes);

        public static AbilityActivationResult ActivateHealTargets(ComponentStore store, GameplayCatalog catalog,
            int entityId, int slot, AbilityActivationRequest request, IReadOnlyList<int> targetIds,
            IReadOnlyList<float> magnitudes)
            => ActivateCore(store, catalog, new StoredAbilityActivationState(store, entityId, slot),
                request, ActivationTargetSet.Heals(targetIds, magnitudes), null);

        public static int FindStoredSlot(ComponentStore store, int entityId, AbilityId abilityId, string name = null)
        {
            if (store == null || entityId < 0 || entityId >= ComponentStore.MAX_ENTITIES) return -1;
            int count = store.AbilityCount[entityId];
            int nameMatch = -1;
            for (int slot = 0; slot < count; slot++)
            {
                var inst = store.GetAbility(entityId, slot);
                // Owner.IsValid 表示已盖过 catalog Id（含 AbilityId(0)，不能用 Value!=0 当哨兵）
                if (inst.State.Owner.IsValid && inst.State.Id.Value == abilityId.Value) return slot;
                if (nameMatch < 0 && !string.IsNullOrEmpty(name) &&
                    string.Equals(inst.Definition.Name, name, StringComparison.OrdinalIgnoreCase))
                    nameMatch = slot;
            }
            if (nameMatch < 0) return -1;
            var stamped = store.GetAbility(entityId, nameMatch);
            stamped.State.Id = abilityId;
            stamped.State.Owner = store.GetEntityHandle(entityId);
            store.SetAbility(entityId, nameMatch, stamped);
            return nameMatch;
        }

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
            else if (execution.MagnitudeSource == MagnitudeSource.Multiplier)
            {
                float basis = store.EnemyActive[sourceId] ? store.GetEnemyAttackDamageProjection(sourceId)
                    : store.TowerActive[sourceId] ? store.GetTowerAttackDamage(sourceId)
                    : sourceId == store.PlayerEntityId ? store.GetPlayerAttackDamageProjection(sourceId)
                    : 0f;
                magnitude = Math.Max(0f, basis * execution.Magnitude);
            }
            else if (execution.MagnitudeSource == MagnitudeSource.Attribute)
            {
                var key = execution.Parameter != 0 ? new AttributeKey(execution.Parameter) : CatalogRegistries.AttackDamage;
                float attr = store.AttributeAggregator.GetComputed(sourceId, key, 0f);
                float scale = execution.Magnitude == 0f ? 1f : execution.Magnitude;
                magnitude = Math.Max(0f, attr * scale);
            }
            else magnitude = execution.Magnitude;
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

        public static bool TickCooldown(AbilityState[] states, int index, float deltaSeconds)
        {
            if (states == null || deltaSeconds <= 0f || index < 0 || index >= states.Length) return false;
            var state = states[index];
            if (state.Cooldown <= 0f) return true;
            state.Cooldown = Math.Max(0f, state.Cooldown - deltaSeconds);
            states[index] = state;
            return true;
        }

        public static AbilityActivationResult TryActivate(AbilityState[] states, AbilityActivationRequest request)
        {
            var reason = states == null || request.Slot < 0 || request.Slot >= (states?.Length ?? 0)
                ? AbilityActivationRejectReason.InvalidRequest
                : !states[request.Slot].CanActivate() ? AbilityActivationRejectReason.Cooldown : AbilityActivationRejectReason.None;
            return new AbilityActivationResult(reason == AbilityActivationRejectReason.None, request.OwnerId, request.Slot, reason);
        }

        public static AbilityActivationResult TryActivate(AbilityCooldownColumn cooldowns, AbilityActivationRequest request)
            => TryActivate(cooldowns.States, request);

        public static AbilityActivationResult AbilityCommit(AbilityState[] states, AbilityActivationRequest request)
        {
            var ready = TryActivate(states, request);
            if (!ready.Accepted) return ready;
            var state = states[request.Slot];
            state.Cooldown = Math.Max(0f, request.Cooldown);
            states[request.Slot] = state;
            return new AbilityActivationResult(true, request.OwnerId, request.Slot);
        }

        public static AbilityActivationResult AbilityCommit(AbilityCooldownColumn cooldowns, AbilityActivationRequest request)
            => AbilityCommit(cooldowns.States, request);

        public static bool TickCooldown(AbilityCooldownColumn cooldowns, int index, float deltaSeconds)
            => TickCooldown(cooldowns.States, index, deltaSeconds);

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
