using System;
using System.Collections.Generic;
using BattleSystemECS.Components;

namespace BattleSystemECS.Core.GAS
{
    /// <summary>唯一的效果生命周期 owner；定义对象永不修改。</summary>
    public sealed class GameplayEffectRuntime
    {
        private readonly ComponentStore _store;
        private readonly Dictionary<EffectHandle, AttributeModifierHandle[]> _modifiers = new Dictionary<EffectHandle, AttributeModifierHandle[]>(8192);
        public int StateUpdateFailures { get; private set; }
        private int _modifierHandleCount;
        private readonly List<int> _runtimeEntityIds = new List<int>(1024);
        private readonly int[] _runtimeEntityCounts = new int[ComponentStore.MAX_ENTITIES];
        private readonly GameplayScheduleBook _schedule = new GameplayScheduleBook();
        private readonly List<(int entityId, int slot)> _timedAbilities = new List<(int, int)>(32);
        public int ActiveRuntimeCount { get; private set; }
        public int PeakActiveRuntimeCount { get; private set; }
        public bool HasActiveEffects => ActiveRuntimeCount > 0;
        public const int DefaultEventCapacity = 8192;
        public GameplayEventQueue Events { get; }
        /// <summary>效果事件溢出时保留的独立诊断队列。</summary>
        public GameplayEventQueue AbortEvents { get; } = new GameplayEventQueue(64, 1);
        public int Rejections { get; private set; }
        public int PublicationFailures { get; private set; }
        public int AbortPublicationFailures { get; private set; }
        public int ModifierCapacity { get; } = 8192;
        /// <summary>派生缓存占用：每个 ActiveEffect 一份 definition.Modifiers.Count，叠层不加。</summary>
        internal int ModifierHandleCount => _modifierHandleCount;
        public int EventCapacity { get; }
        public GameplayEffectRuntime(ComponentStore store, int eventCapacity = DefaultEventCapacity)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            EventCapacity = Math.Max(1, eventCapacity);
            Events = new GameplayEventQueue(EventCapacity, Math.Min(64, EventCapacity / 8));
        }
        /// <summary>该 clock 当前虚拟时间。排期本按此取件，不按帧号。</summary>
        public double VirtualNow(ClockId clock) => _schedule.VirtualNow(clock);

        /// <summary>从 ActiveEffect 全量重建该 clock 的到期表（派生缓存）。</summary>
        internal void RebuildSchedule(ClockId clock) => _schedule.RebuildEffects(_store, _runtimeEntityIds, clock);
        internal bool CanApplyPlan(int targetId, int runtimeSlots, int modifierCount, int eventCount)
        {
            if (!ComponentStore.IsValidEntity(targetId) || runtimeSlots < 0 || modifierCount < 0 || eventCount < 0) return false;
            return _store.ActiveEffectCount[targetId] <= ComponentStore.MAX_ACTIVE_EFFECTS_PER_ENTITY - runtimeSlots &&
                _store.GameplayEffectPool.FreeCount >= runtimeSlots &&
                CanAllocateModifiers(modifierCount) && Events.CanPublish(eventCount, true);
        }
        internal bool CanApplyPlan(IReadOnlyList<int> targetIds, int runtimeSlotsPerTarget,
            int modifiersPerTarget, int eventCount)
        {
            if (targetIds == null || runtimeSlotsPerTarget < 0 || modifiersPerTarget < 0 || eventCount < 0 ||
                !Events.CanPublish(eventCount, true)) return false;
            long totalSlots = (long)runtimeSlotsPerTarget * targetIds.Count;
            long totalModifiers = (long)modifiersPerTarget * targetIds.Count;
            if (totalSlots > _store.GameplayEffectPool.FreeCount ||
                !CanAllocateModifiers(totalModifiers)) return false;
            for (int i = 0; i < targetIds.Count; i++)
                if (!ComponentStore.IsValidEntity(targetIds[i]) ||
                    _store.ActiveEffectCount[targetIds[i]] > ComponentStore.MAX_ACTIVE_EFFECTS_PER_ENTITY - runtimeSlotsPerTarget)
                    return false;
            return true;
        }

        /// <summary>
        /// modifierCount / additional 是本计划将新占的派生缓存槽（按定义条数计，叠层相对第一层为 0）。
        /// </summary>
        internal bool CanAllocateModifiers(long additional)
        {
            if (additional <= 0) return true;
            if (additional > int.MaxValue) return false;
            return _modifierHandleCount <= ModifierCapacity - (int)additional;
        }

        /// <summary>
        /// 与 TryApply 同一套占用：已有 stack-key 则只补定义条数差额，不为层数扩槽。
        /// </summary>
        internal void CountPlanOccupancy(int targetId, GameplayEffectDefinition definition, out int runtimeSlots, out int modifierSlots)
        {
            runtimeSlots = 0;
            modifierSlots = 0;
            if (TryGetExistingStack(targetId, definition, out _, out var existingDef))
            {
                modifierSlots = Math.Max(0, definition.Modifiers.Count - existingDef.Modifiers.Count);
                return;
            }
            if (definition.Type == EffectType.Instant)
            {
                modifierSlots = definition.Modifiers.Count;
                return;
            }
            runtimeSlots = 1;
            modifierSlots = definition.Modifiers.Count;
        }

        internal bool EnqueueApply(EffectId id, GameplayEffectDefinition definition, EntityHandle source,
            EntityHandle target, int ownerPlayerId, float periodicMagnitude, float modifierCapture = float.NaN)
        {
            if (!_store.EffectRequests.CanAdd(1))
            {
                _store.EffectRequests.RecordCapacityRejection(false);
                return false;
            }
            var context = new ExecutionContext(source, target, default(AbilityId), id, definition.Clock,
                _store.AllocateGameplaySequence(target.Index), ownerPlayerId, periodicMagnitude);
            if (!_store.EffectRequests.TryAdd(new EffectRequest(source, target, id, 1, definition.Clock, context)))
                return false;
            int index = _store.EffectRequests.Count - 1;
            _store.QueuedEffectDefinitions[index] = definition;
            _store.QueuedEffectOwnerPlayerIds[index] = ownerPlayerId;
            _store.QueuedEffectSnapshots[index] = periodicMagnitude;
            _store.QueuedEffectModifierCaptures[index] = modifierCapture;
            if (!_store.DeferAbilityAndEffectCommit)
                CommitQueuedEffects();
            return true;
        }

        private struct PendingRemove
        {
            public EntityHandle Target;
            public EffectHandle Handle;
            public GameplayEventType Reason;
        }

        private readonly List<PendingRemove> _pendingRemoves = new List<PendingRemove>(16);
        private bool _effectCommitBatch;

        /// <summary>
        /// effect.commit 批内顺序：堆叠命中 → 溢出 → Replace 捕获 → 时长/周期（均在 TryApply）
        /// → 过期 / 批内显式 Remove 垫后。禁止「施加又移除 = 抵消」；两条事实都要发布。
        /// AI 组 dispel 是批外 remove-first，不走这里。
        /// </summary>
        internal void CommitQueuedEffects()
        {
            _effectCommitBatch = true;
            _pendingRemoves.Clear();
            try
            {
                int count = _store.EffectRequests.Count;
                for (int i = 0; i < count; i++)
                {
                    var request = _store.EffectRequests.Get(i);
                    TryApply(request.Effect, _store.QueuedEffectDefinitions[i], request.Source, request.Target, out _,
                        request.StackDelta, _store.QueuedEffectSnapshots[i], _store.QueuedEffectModifierCaptures[i],
                        _store.QueuedEffectOwnerPlayerIds[i]);
                }
                for (int i = 0; i < _pendingRemoves.Count; i++)
                {
                    PendingRemove pending = _pendingRemoves[i];
                    RemoveImmediate(pending.Target, pending.Handle, pending.Reason);
                }
            }
            finally
            {
                _effectCommitBatch = false;
                _pendingRemoves.Clear();
                _store.ClearEffectQueue();
            }
        }

        internal void RejectQueuedEffects()
        {
            int n = _store.EffectRequests.Count;
            if (n > 0) _store.UnconsumedEffectRequests += n;
            _store.ClearEffectQueue();
        }

        public bool TryApply(EffectId id, GameplayEffectDefinition definition, EntityHandle source, EntityHandle target, out EffectHandle handle, int stackDelta = 1, float periodicMagnitude = float.NaN, float modifierCapture = float.NaN, int ownerPlayerId = -1, long provenanceId = 0L)
        {
            handle = default(EffectHandle);
            if (!source.IsValid || !target.IsValid || definition.Id.Value != id.Value || !_store.TryResolve(target, out int targetId, out _) || !_store.TryResolve(source, out _, out _)) { Reject(source, target, id, 1); return false; }
            if (!IsDurationContractValid(definition)) { Reject(source, target, id, 6); return false; }
            if (definition.Type == EffectType.Periodic && (definition.DurationPolicy != DurationPolicy.Duration || definition.Duration <= 0f || float.IsNaN(definition.Duration) || float.IsInfinity(definition.Duration))) { Reject(source, target, id, 6); return false; }
            if (definition.Type == EffectType.Duration && definition.DurationPolicy == DurationPolicy.Duration && (definition.Duration <= 0f || float.IsNaN(definition.Duration) || float.IsInfinity(definition.Duration))) { Reject(source, target, id, 6); return false; }
            if (definition.Type == EffectType.Duration && definition.DurationPolicy == DurationPolicy.Infinite && (definition.Duration != 0f || float.IsNaN(definition.Duration) || float.IsInfinity(definition.Duration))) { Reject(source, target, id, 6); return false; }
            if (RejectPeriodicAndBlocked(definition, source, target, targetId, periodicMagnitude)) return false;
            if (TryGetExistingStack(targetId, definition, out var existing, out _))
            {
                if (definition.Stacking == StackingBehavior.None || definition.Stacking == StackingBehavior.DurationRefresh)
                {
                    if (definition.Refresh != RefreshPolicy.None) { RefreshRuntime(ref existing, definition); if (!UpdateActive(target, existing)) return false; }
                    handle = existing.Handle;
                    Publish(new GameplayEvent(GameplayEventType.EffectApplied, source, target, existing.Handle, id, DamageFlags.None, _store.AllocateGameplaySequence(targetId), tag: definition.Tag, ownerPlayerId: ownerPlayerId));
                    return true;
                }
                if (!RestackLedger(ref existing, definition, targetId, stackDelta, modifierCapture))
                { Reject(source, target, id, 3); return false; }
                if (definition.Refresh == RefreshPolicy.StacksAndDuration || definition.Stacking == StackingBehavior.MaxStacksRefresh)
                    RefreshRuntime(ref existing, definition);
                if (!UpdateActive(target, existing)) return false;
                handle = existing.Handle;
                Publish(new GameplayEvent(GameplayEventType.EffectApplied, source, target, existing.Handle, id, DamageFlags.None, _store.AllocateGameplaySequence(targetId), tag: definition.Tag, ownerPlayerId: ownerPlayerId));
                return true;
            }
            if (definition.Type == EffectType.Instant)
            {
                Publish(new GameplayEvent(GameplayEventType.EffectApplied, source, target, default(EffectHandle), id, DamageFlags.None, _store.AllocateGameplaySequence(targetId), tag: definition.Tag, ownerPlayerId: ownerPlayerId));
                return true;
            }
            int ticks = CalculateTicks(definition);
            if (definition.Modifiers.Count > 0 && !CanAllocateModifiers(definition.Modifiers.Count)) { Reject(source, target, id, 3); return false; }
            float magnitude = float.IsNaN(periodicMagnitude) ? (definition.Periodic.HasValue ? definition.Periodic.Value.Magnitude : 0f) : periodicMagnitude;
            if (definition.Type == EffectType.Periodic && definition.Periodic.Value.MagnitudeSource == MagnitudeSource.Attribute && float.IsNaN(periodicMagnitude))
                magnitude = ResolveAttributeMagnitude(source.Index, definition.Periodic.Value);
            if (definition.Type == EffectType.Periodic && definition.Periodic.Value.Payload != EffectPayloadKind.GameplayEvent && definition.Periodic.Value.MagnitudeSource != MagnitudeSource.Attribute && (magnitude <= 0f || float.IsNaN(magnitude) || float.IsInfinity(magnitude))) { Reject(source, target, id, 4); return false; }
            var runtime = new ActiveGameplayEffect(default(EffectHandle), id, source, target, definition.Duration, ticks, magnitude, definition.Clock,
                definition.Periodic.HasValue ? definition.Periodic.Value.FirstTick : FirstTickPolicy.NextInterval,
                definition.Periodic.HasValue ? definition.Periodic.Value.CatchUp : CatchUpPolicy.CatchUpAll, definition.SourceDeath, ownerPlayerId, _store.AllocateGameplaySequence(targetId), provenanceId, definition.Tag);
            runtime.RuntimeOwned = true;
            runtime.CapturedModifierMagnitude = modifierCapture;
            BindSchedule(ref runtime, definition, resetPeriodic: true);
            var app = new GameplayEffectApplication(definition, default(LegacyEffectSnapshot), runtime);
            if (!_store.TryAddGameplayEffect(targetId, app, out handle)) { Reject(source, target, id, 5); return false; }
            ActiveRuntimeCount++;
            if (ActiveRuntimeCount > PeakActiveRuntimeCount) PeakActiveRuntimeCount = ActiveRuntimeCount;
            RegisterRuntimeEntity(targetId);
            if (!ApplyModifiers(targetId, definition, modifierCapture, handle, runtime.StackCount))
            {
                _store.TryRemoveEffect(target, handle, out _);
                ActiveRuntimeCount = Math.Max(0, ActiveRuntimeCount - 1);
                UnregisterRuntimeEntity(targetId);
                handle = default(EffectHandle);
                Reject(source, target, id, 3);
                return false;
            }
            SyncSchedule(handle, runtime, definition);
            Publish(new GameplayEvent(GameplayEventType.EffectApplied, source, target, handle, id, DamageFlags.None, _store.AllocateGameplaySequence(targetId), tag: definition.Tag, ownerPlayerId: ownerPlayerId));
            return true;
        }

        /// <summary>
        /// 把 legacy Periodic 收进 runtime owner。挂槽后与 TryApply 一样 ApplyModifiers；
        /// modifier 捕获不得回落到 Periodic 跳伤。失败撤槽并 EffectRejected。
        /// </summary>
        internal bool TryAdopt(GameplayEffectApplication application, int ownerPlayerId, out EffectHandle handle)
        {
            handle = default(EffectHandle);
            var runtime = application.Runtime;
            var definition = application.Definition;
            if (!runtime.Target.IsValid || !_store.TryResolve(runtime.Target, out int targetId, out _)) return false;
            if (!IsDurationContractValid(definition)) { Reject(runtime.Source, runtime.Target, definition.Id, 6); return false; }
            if (RejectPeriodicAndBlocked(definition, runtime.Source, runtime.Target, targetId, runtime.CapturedMagnitude)) return false;
            float modifierCapture = ResolveAdoptModifierCapture(application);
            runtime.RuntimeOwned = true;
            runtime.OwnerPlayerId = ownerPlayerId;
            runtime.CapturedModifierMagnitude = modifierCapture;
            BindSchedule(ref runtime, definition, resetPeriodic: runtime.ExpireAtVirtual <= 0d);
            if (runtime.ApplicationSequence == 0L)
                runtime.ApplicationSequence = _store.AllocateGameplaySequence(targetId);
            application = new GameplayEffectApplication(definition, application.LegacySnapshot, runtime);
            if (!_store.TryAddGameplayEffect(targetId, application, out handle)) return false;
            ActiveRuntimeCount++;
            if (ActiveRuntimeCount > PeakActiveRuntimeCount) PeakActiveRuntimeCount = ActiveRuntimeCount;
            RegisterRuntimeEntity(targetId);
            if (!ApplyModifiers(targetId, definition, modifierCapture, handle, Math.Max(1, runtime.StackCount)))
            {
                _store.TryRemoveEffect(runtime.Target, handle, out _);
                ActiveRuntimeCount = Math.Max(0, ActiveRuntimeCount - 1);
                UnregisterRuntimeEntity(targetId);
                handle = default(EffectHandle);
                Reject(runtime.Source, runtime.Target, definition.Id, 3);
                return false;
            }
            SyncSchedule(handle, runtime, definition);
            Publish(new GameplayEvent(GameplayEventType.EffectApplied, runtime.Source, runtime.Target, handle, definition.Id,
                DamageFlags.None, _store.AllocateGameplaySequence(targetId), tag: definition.Tag, ownerPlayerId: ownerPlayerId));
            return true;
        }

        /// <summary>
        /// 叠层刷新的唯一 timer writer：DurationRefresh / MaxStacks / MaxStacksRefresh 都在这里改 RemainingTime 与 stacks。
        /// MaxStacks 路径与 TryApply 共用 RestackLedger，禁止第二套 StackCount++。
        /// </summary>
        internal bool TryRestack(GameplayEffectApplication application, int ownerPlayerId, out EffectHandle handle)
        {
            handle = default(EffectHandle);
            var definition = application.Definition;
            if (!application.Runtime.Target.IsValid || !_store.TryResolve(application.Runtime.Target, out int targetId, out _))
                return false;
            if (!IsDurationContractValid(definition))
            { Reject(application.Runtime.Source, application.Runtime.Target, definition.Id, 6); return false; }
            if (RejectPeriodicAndBlocked(definition, application.Runtime.Source, application.Runtime.Target, targetId, application.Runtime.CapturedMagnitude))
                return false;
            TagId key = StackIdentity(definition);
            int count = _store.GetEffectCount(targetId);
            float modifierCapture = ResolveAdoptModifierCapture(application);
            for (int i = 0; i < count; i++)
            {
                if (!_store.TryGetActiveEffectAt(targetId, i, out var existing, out var existingDef, out var existingSnapshot)) continue;
                TagId existingKey = StackIdentity(existingDef);
                if (!existingKey.Equals(key) || existingDef.Type != EffectType.Periodic) continue;
                string incomingName = application.LegacySnapshot.Name;
                if (!string.IsNullOrEmpty(incomingName) && !string.IsNullOrEmpty(existingSnapshot.Name) &&
                    !string.Equals(incomingName, existingSnapshot.Name, StringComparison.Ordinal)) continue;
                if (!existing.RuntimeOwned)
                {
                    existing.RuntimeOwned = true;
                    existing.OwnerPlayerId = ownerPlayerId;
                    ActiveRuntimeCount++;
                    if (ActiveRuntimeCount > PeakActiveRuntimeCount) PeakActiveRuntimeCount = ActiveRuntimeCount;
                    RegisterRuntimeEntity(targetId);
                }
                switch (definition.Stacking)
                {
                    case StackingBehavior.DurationRefresh:
                        RefreshRuntime(ref existing, definition);
                        break;
                    case StackingBehavior.MaxStacks:
                        if (!RestackLedger(ref existing, definition, targetId, 1, modifierCapture))
                        { Reject(application.Runtime.Source, application.Runtime.Target, definition.Id, 3); return false; }
                        break;
                    case StackingBehavior.MaxStacksRefresh:
                        if (!RestackLedger(ref existing, definition, targetId, 1, modifierCapture))
                        { Reject(application.Runtime.Source, application.Runtime.Target, definition.Id, 3); return false; }
                        RefreshRuntime(ref existing, definition);
                        break;
                    default:
                        return TryAdopt(application, ownerPlayerId, out handle);
                }
                if (!UpdateActive(existing.Target, existing)) return false;
                handle = existing.Handle;
                Publish(new GameplayEvent(GameplayEventType.EffectApplied, existing.Source, existing.Target, handle,
                    definition.Id, DamageFlags.None, _store.AllocateGameplaySequence(targetId), tag: definition.Tag,
                    ownerPlayerId: ownerPlayerId));
                return true;
            }
            return TryAdopt(application, ownerPlayerId, out handle);
        }

        private void Reject(EntityHandle source, EntityHandle target, EffectId id, int reason)
        {
            Rejections++;
            Publish(new GameplayEvent(GameplayEventType.EffectRejected, source, target, default(EffectHandle), id, DamageFlags.None, _store.AllocateGameplaySequence(target.IsValid ? target.Index : 0), reason: reason));
        }

        private void Publish(GameplayEvent e)
        {
            if (!Events.TryPublish(e, true))
            {
                PublicationFailures++;
                var abort = new GameplayEvent(GameplayEventType.GameplayLoopAborted, e.Source, e.Target, e.Sequence, 6);
                if (!AbortEvents.TryPublish(abort, true)) AbortPublicationFailures++;
            }
        }

        private bool RejectPeriodicAndBlocked(GameplayEffectDefinition definition, EntityHandle source,
            EntityHandle target, int targetId, float snapshot)
        {
            if (definition.Type == EffectType.Periodic)
            {
                if (!definition.Periodic.HasValue || !ValidatePeriodicPayload(definition.Periodic.Value, targetId))
                { Reject(source, target, definition.Id, 2); return true; }
                bool attributeMagnitude = definition.Periodic.Value.MagnitudeSource == MagnitudeSource.Attribute;
                float requestedMagnitude = float.IsNaN(snapshot) ? definition.Periodic.Value.Magnitude : snapshot;
                if (!attributeMagnitude && definition.Periodic.Value.Payload != EffectPayloadKind.GameplayEvent &&
                    (requestedMagnitude <= 0f || float.IsNaN(requestedMagnitude) || float.IsInfinity(requestedMagnitude)))
                { Reject(source, target, definition.Id, 4); return true; }
            }
            if (definition.BlockedTags != null && definition.BlockedTags.Count > 0)
            {
                for (int b = 0; b < definition.BlockedTags.Count; b++)
                    if (GameplayTagRuntime.HasTag(_store, targetId, definition.BlockedTags[b]))
                    { Reject(source, target, definition.Id, 8); return true; }
            }
            return false;
        }

        private bool ValidatePeriodicPayload(PeriodicSpec spec, int targetId)
        {
            if (spec.Period <= 0f || float.IsNaN(spec.Period) || float.IsInfinity(spec.Period)) return false;
            bool player = (uint)targetId < ComponentStore.MAX_PLAYERS && _store.PositionActive[targetId];
            bool enemy = ComponentStore.IsValidEntity(targetId) && _store.EnemyActive[targetId];
            if (!player && !enemy) return false;
            if (spec.MagnitudeSource == MagnitudeSource.Attribute)
                return spec.Resource.Value >= 0 && CatalogRegistries.TryAttribute(spec.Resource);
            if (spec.MagnitudeSource != MagnitudeSource.Constant) return false;
            switch (spec.Payload)
            {
                case EffectPayloadKind.Damage:
                case EffectPayloadKind.Heal:
                case EffectPayloadKind.GameplayEvent:
                    return true;
                case EffectPayloadKind.Resource:
                    // CurrentHealth 属于 Heal/Damage 语义，不是通用资源 tick。
                    if (spec.Resource.Value == 3) return false;
                    if (spec.Resource.Value == 4 && !player) return false;
                    return spec.Resource.Value == 2 || spec.Resource.Value == 4 || spec.Resource.Value == 7 || spec.Resource.Value == 9;
                default:
                    return false;
            }
        }
        internal static bool IsDurationContractValid(GameplayEffectDefinition definition)
        {
            if (definition.Type == EffectType.Instant) return definition.DurationPolicy == DurationPolicy.Instant && !definition.Periodic.HasValue;
            if (definition.Type == EffectType.Duration) return (definition.DurationPolicy == DurationPolicy.Duration || definition.DurationPolicy == DurationPolicy.Infinite) && !definition.Periodic.HasValue;
            if (definition.Type == EffectType.Periodic) return definition.DurationPolicy == DurationPolicy.Duration && definition.Periodic.HasValue;
            return false;
        }

        private static TagId StackIdentity(GameplayEffectDefinition definition)
        {
            return definition.StackKey.Equals(default(TagId)) ? new TagId(definition.Id.Value) : definition.StackKey;
        }

        private bool TryGetExistingStack(int targetId, GameplayEffectDefinition definition,
            out ActiveGameplayEffect existing, out GameplayEffectDefinition existingDef)
        {
            existing = default(ActiveGameplayEffect);
            existingDef = default(GameplayEffectDefinition);
            if (!ComponentStore.IsValidEntity(targetId)) return false;
            TagId key = StackIdentity(definition);
            int count = _store.GetEffectCount(targetId);
            for (int i = 0; i < count; i++)
            {
                if (!_store.TryGetActiveEffectAt(targetId, i, out existing, out existingDef, out _)) continue;
                if (!StackIdentity(existingDef).Equals(key)) continue;
                return true;
            }
            existing = default(ActiveGameplayEffect);
            existingDef = default(GameplayEffectDefinition);
            return false;
        }

        private static float ResolveAdoptModifierCapture(GameplayEffectApplication application)
        {
            var definition = application.Definition;
            bool captureOnApply = false;
            for (int i = 0; i < definition.Modifiers.Count; i++)
                if (definition.Modifiers[i].Snapshot == SnapshotPolicy.CaptureOnApply) { captureOnApply = true; break; }
            if (!captureOnApply) return float.NaN;
            // CaptureOnApply 的账本捕获来自 legacy modifier 值；禁止回落到 Periodic 跳伤。
            if (application.LegacySnapshot.AttributeIndex >= 0) return application.LegacySnapshot.Magnitude;
            return float.NaN;
        }

        /// <summary>
        /// 唯一叠层入口：stackCount 是 ΣAdd 乘数；V1 Replace 只替换 modifier 捕获；派生缓存按定义条数重建。
        /// </summary>
        private bool RestackLedger(ref ActiveGameplayEffect existing, GameplayEffectDefinition definition,
            int targetId, int stackDelta, float modifierCapture)
        {
            int maxStacks = definition.MaxStacks < 1 ? 1 : definition.MaxStacks;
            existing.StackCount = Math.Min(maxStacks, existing.StackCount + Math.Max(1, stackDelta));
            existing.CapturedModifierMagnitude = modifierCapture;
            if (existing.Inhibited) return true;
            return SyncModifierCache(targetId, definition, existing.Handle, existing.StackCount,
                existing.CapturedModifierMagnitude);
        }

        private bool SyncModifierCache(int targetId, GameplayEffectDefinition definition, EffectHandle handle,
            int stackCount, float modifierCapture)
        {
            int current = 0;
            if (handle.IsValid && _modifiers.TryGetValue(handle, out var prior) && prior != null)
                current = prior.Length;
            int additional = definition.Modifiers.Count - current;
            if (additional > 0 && !CanAllocateModifiers(additional)) return false;
            ReleaseModifierCache(targetId, handle);
            return ApplyModifiers(targetId, definition, modifierCapture, handle, stackCount);
        }

        /// <summary>
        /// 摘除式抑制：去掉 modifier 与 granted tag 贡献，效果槽仍在。不新增 Inhibited 枚举。
        /// </summary>
        public bool TryInhibit(EntityHandle target, EffectHandle handle)
        {
            if (!handle.IsValid || !_store.TryGetActiveEffect(target, handle, out var runtime, out var definition, out _))
                return false;
            if (runtime.Inhibited) return true;
            if (_modifiers.TryGetValue(handle, out _)) ReleaseModifierCache(target.Index, handle);
            _store.TagState.RemoveGranted(target.Index, definition.GrantedTags);
            runtime.Inhibited = true;
            return UpdateActive(target, runtime);
        }

        /// <summary>解除抑制：从账本重建派生缓存（与叠层同一套 SyncModifierCache）。</summary>
        public bool TryUninhibit(EntityHandle target, EffectHandle handle)
        {
            if (!handle.IsValid || !_store.TryGetActiveEffect(target, handle, out var runtime, out var definition, out _))
                return false;
            if (!runtime.Inhibited) return true;
            if (!SyncModifierCache(target.Index, definition, handle, runtime.StackCount, runtime.CapturedModifierMagnitude))
                return false;
            _store.TagState.AddGranted(target.Index, definition.GrantedTags);
            runtime.Inhibited = false;
            return UpdateActive(target, runtime);
        }

        private void ReleaseModifierCache(int targetId, EffectHandle handle)
        {
            if (!handle.IsValid || !_modifiers.TryGetValue(handle, out var modifiers)) return;
            for (int i = 0; i < modifiers.Length; i++)
                _store.RemoveAttributeModifier(targetId, modifiers[i]);
            _modifierHandleCount -= modifiers.Length;
            _modifiers.Remove(handle);
        }

        /// <summary>
        /// 一份定义贡献一份 handle。Add / Percent 的捕获/幅度乘 stackCount；Override 不乘。
        /// </summary>
        private static void ContributeModifier(ModifierDefinition def, float modifierCapture, int stacks,
            out ModifierDefinition stacked, out float capture)
        {
            bool scale = (def.Operation == AttributeModifierOp.Add || def.Operation == AttributeModifierOp.Percent)
                && stacks > 1;
            stacked = scale
                ? new ModifierDefinition(def.Attribute, def.Operation, def.Magnitude * stacks, def.Priority,
                    def.MagnitudeSource, def.Snapshot)
                : def;
            capture = modifierCapture;
            if (def.Snapshot == SnapshotPolicy.CaptureOnApply && !float.IsNaN(modifierCapture) && scale)
                capture = modifierCapture * stacks;
        }

        private bool ApplyModifiers(int targetId, GameplayEffectDefinition definition, float modifierCapture, EffectHandle handle, int stackCount)
        {
            if (definition.Modifiers.Count == 0) return true;
            int stacks = Math.Max(1, stackCount);
            if (handle.IsValid)
            {
                if (!CanAllocateModifiers(definition.Modifiers.Count)) return false;
                var hs = new AttributeModifierHandle[definition.Modifiers.Count];
                try
                {
                    for (int i = 0; i < hs.Length; i++)
                    {
                        ContributeModifier(definition.Modifiers[i], modifierCapture, stacks, out var stacked, out var capture);
                        hs[i] = _store.AddAttributeModifier(targetId, stacked, float.IsNaN(capture) ? float.NaN : capture);
                        if (!hs[i].IsValid) throw new InvalidOperationException("modifier 分配失败");
                    }
                }
                catch
                {
                    for (int i = 0; i < hs.Length; i++)
                        if (hs[i].IsValid) _store.RemoveAttributeModifier(targetId, hs[i]);
                    return false;
                }
                _modifiers[handle] = hs;
                _modifierHandleCount += hs.Length;
            }
            else
            {
                for (int i = 0; i < definition.Modifiers.Count; i++)
                {
                    ContributeModifier(definition.Modifiers[i], modifierCapture, stacks, out var stacked, out var capture);
                    var modifier = _store.AddAttributeModifier(targetId, stacked, float.IsNaN(capture) ? float.NaN : capture);
                    if (!modifier.IsValid) return false;
                }
            }
            return true;
        }
        private static int CalculateTicks(GameplayEffectDefinition definition)
        {
            return definition.Periodic.HasValue && definition.Periodic.Value.Period > 0f && definition.Duration > 0f
                ? Math.Max(1, (int)Math.Floor(definition.Duration / definition.Periodic.Value.Period)) : 0;
        }
        private bool UpdateActive(EntityHandle target, ActiveGameplayEffect runtime)
        {
            if (_store.TryUpdateActiveEffect(target, runtime)) return true;
            StateUpdateFailures++;
            return false;
        }
        private void BindSchedule(ref ActiveGameplayEffect active, GameplayEffectDefinition definition, bool resetPeriodic)
        {
            double now = _schedule.VirtualNow(active.Clock);
            if (definition.DurationPolicy == DurationPolicy.Infinite)
                active.ExpireAtVirtual = double.PositiveInfinity;
            else
            {
                float remaining = active.RemainingTime > 0f ? active.RemainingTime : definition.Duration;
                active.ExpireAtVirtual = now + Math.Max(0f, remaining);
                active.RemainingTime = remaining;
            }
            if (definition.Type == EffectType.Periodic && definition.Periodic.HasValue)
            {
                float period = definition.Periodic.Value.Period;
                if (resetPeriodic || active.NextTickAtVirtual <= 0d)
                {
                    bool immediate = active.FirstTick == FirstTickPolicy.Immediate &&
                        (resetPeriodic || active.FirstTickPending);
                    float remainder = resetPeriodic ? 0f : Math.Max(0f, active.TickAccumulator);
                    active.NextTickAtVirtual = immediate ? now : now + Math.Max(0f, period - remainder);
                    if (resetPeriodic) active.TickAccumulator = 0f;
                }
            }
            else active.NextTickAtVirtual = double.PositiveInfinity;
        }

        private void SyncSchedule(EffectHandle handle, ActiveGameplayEffect runtime, GameplayEffectDefinition definition)
        {
            if (!handle.IsValid) return;
            _schedule.ClearEffect(handle);
            if (definition.DurationPolicy != DurationPolicy.Infinite)
                _schedule.UpsertEffectExpire(runtime.Clock, handle, runtime.Target, runtime.ExpireAtVirtual);
            if (definition.Type == EffectType.Periodic && runtime.TicksRemaining > 0)
                _schedule.UpsertPeriodic(runtime.Clock, handle, runtime.Target, runtime.NextTickAtVirtual);
        }

        private void RefreshRuntime(ref ActiveGameplayEffect active, GameplayEffectDefinition definition)
        {
            active.RemainingTime = definition.DurationPolicy == DurationPolicy.Infinite ? 0f : definition.Duration;
            active.TicksRemaining = CalculateTicks(definition);
            active.TickAccumulator = 0f;
            active.FirstTickPending = true;
            BindSchedule(ref active, definition, resetPeriodic: true);
            SyncSchedule(active.Handle, active, definition);
        }

        private bool ApplySourceDeathPolicy(ref ActiveGameplayEffect active)
        {
            if (active.Source.IsValid && _store.TryResolve(active.Source, out _, out _)) return true;
            switch (active.SourceDeath)
            {
                case SourceDeathPolicy.Remove:
                    return false;
                case SourceDeathPolicy.Transfer:
                    EntityHandle transferred = default(EntityHandle);
                    if (active.OwnerPlayerId >= 0 && active.OwnerPlayerId < ComponentStore.MAX_PLAYERS)
                    {
                        transferred = _store.GetEntityHandle(active.OwnerPlayerId);
                        if (!transferred.IsValid || !_store.TryResolve(transferred, out _, out _))
                            transferred = default(EntityHandle);
                    }
                    if (!transferred.IsValid) transferred = active.Target;
                    if (!transferred.IsValid || !_store.TryResolve(transferred, out _, out _)) return false;
                    active.Source = transferred;
                    return true;
                default:
                    return true;
            }
        }

        private float ResolveAttributeMagnitude(int sourceId, PeriodicSpec spec)
        {
            float attr = _store.AttributeAggregator.GetComputed(sourceId, spec.Resource, float.NaN);
            if (float.IsNaN(attr))
            {
                if (spec.Resource.Equals(CatalogRegistries.AttackDamage))
                    attr = _store.EnemyActive[sourceId] ? _store.GetEnemyAttackDamageProjection(sourceId)
                        : _store.TowerActive[sourceId] ? _store.GetTowerAttackDamage(sourceId)
                        : sourceId == _store.PlayerEntityId ? _store.GetPlayerAttackDamageProjection(sourceId)
                        : 0f;
                else attr = 0f;
            }
            float scale = spec.Magnitude == 0f ? 1f : spec.Magnitude;
            return Math.Max(0f, attr * scale);
        }

        private float ResolvePeriodicMagnitude(ActiveGameplayEffect active, PeriodicSpec spec)
        {
            if (spec.MagnitudeSource != MagnitudeSource.Attribute) return active.CapturedMagnitude;
            int sourceId = active.Source.IsValid ? active.Source.Index : active.Target.Index;
            return ResolveAttributeMagnitude(sourceId, spec);
        }

        public bool Remove(EntityHandle target, EffectHandle handle, GameplayEventType reason = GameplayEventType.EffectRemoved)
        {
            if (!handle.IsValid || !_store.TryGetActiveEffect(target, handle, out _, out _, out _)) return false;
            if (_effectCommitBatch)
            {
                // 批内 Remove 垫后：先把本批 Apply 做完，再移除，两条事件都进 digest。
                for (int i = 0; i < _pendingRemoves.Count; i++)
                    if (_pendingRemoves[i].Handle.Equals(handle) && _pendingRemoves[i].Target.Equals(target))
                        return true;
                _pendingRemoves.Add(new PendingRemove { Target = target, Handle = handle, Reason = reason });
                return true;
            }
            return RemoveImmediate(target, handle, reason);
        }

        private bool RemoveImmediate(EntityHandle target, EffectHandle handle, GameplayEventType reason)
        {
            if (!handle.IsValid || !_store.TryGetActiveEffect(target, handle, out var runtime, out _, out _)) return false;
            if (!runtime.Inhibited && _modifiers.TryGetValue(handle, out _)) ReleaseModifierCache(target.Index, handle);
            _schedule.ClearEffect(handle);
            bool removed = _store.TryRemoveEffect(target, handle, out _);
            if (removed) { if (runtime.RuntimeOwned) { ActiveRuntimeCount = Math.Max(0, ActiveRuntimeCount - 1); UnregisterRuntimeEntity(target.Index); } Publish(new GameplayEvent(reason, runtime.Source, target, handle, runtime.DefinitionId, DamageFlags.None, _store.AllocateGameplaySequence(target.Index), tag: runtime.Tag, ownerPlayerId: runtime.OwnerPlayerId)); }
            return removed;
        }

        public void CleanupEntity(int entityId)
        {
            if (ActiveRuntimeCount == 0 && _modifiers.Count == 0 && _timedAbilities.Count == 0) return;
            for (int i = _timedAbilities.Count - 1; i >= 0; i--)
                if (_timedAbilities[i].entityId == entityId)
                {
                    _schedule.ClearAbility(entityId, _timedAbilities[i].slot);
                    _timedAbilities.RemoveAt(i);
                }
            for (int i = _store.GetEffectCount(entityId) - 1; i >= 0; i--)
                if (_store.TryGetActiveEffectAt(entityId, i, out var active, out _, out _)) Remove(active.Target, active.Handle);
            for (int i = 0; i < _runtimeEntityIds.Count; i++)
            {
                int targetId = _runtimeEntityIds[i];
                CleanupSourceEffects(entityId, targetId);
                if (i < _runtimeEntityIds.Count && _runtimeEntityIds[i] != targetId) i--;
            }
        }

        private void CleanupSourceEffects(int sourceId, IReadOnlyList<int> entities)
        {
            for (int i = 0; i < entities.Count; i++) CleanupSourceEffects(sourceId, entities[i]);
        }

        private void CleanupSourceEffects(int sourceId, int targetId)
        {
            for (int slot = _store.GetEffectCount(targetId) - 1; slot >= 0; slot--)
            {
                if (!_store.TryGetActiveEffectAt(targetId, slot, out var active, out _, out _)) continue;
                if (active.Source.Index == sourceId && active.SourceDeath == SourceDeathPolicy.Remove) Remove(active.Target, active.Handle);
            }
        }

        public int Tick(float deltaTime, ClockId clock)
        {
            _store.ResourceResolver.TickTimedShields(deltaTime, clock);
            _schedule.Advance(clock, deltaTime);
            int expired = TickTimedAbilities(clock);
            if (ActiveRuntimeCount == 0) return expired;
            for (int n = 0; n < _runtimeEntityIds.Count; n++)
            {
                int entityId = _runtimeEntityIds[n];
                expired += TickEntity(entityId, clock);
                if (n < _runtimeEntityIds.Count && _runtimeEntityIds[n] != entityId) n--;
            }
            return expired;
        }
        private void RegisterRuntimeEntity(int entityId)
        {
            if (_runtimeEntityCounts[entityId]++ == 0) _runtimeEntityIds.Add(entityId);
        }
        private void UnregisterRuntimeEntity(int entityId)
        {
            if (_runtimeEntityCounts[entityId] <= 0) return;
            if (--_runtimeEntityCounts[entityId] != 0) return;
            int at = _runtimeEntityIds.IndexOf(entityId);
            if (at < 0) return;
            int last = _runtimeEntityIds.Count - 1;
            _runtimeEntityIds[at] = _runtimeEntityIds[last];
            _runtimeEntityIds.RemoveAt(last);
        }
        public void ResetFrame() { Events.Clear(); AbortEvents.Clear(); }
        private int TickEntity(int entityId, ClockId clock)
        {
            int expired = 0;
            double now = _schedule.VirtualNow(clock);
            for (int slot = _store.GetEffectCount(entityId) - 1; slot >= 0; slot--)
            {
                if (!_store.TryGetActiveEffectAt(entityId, slot, out var active, out var definition, out _)) continue;
                if (!active.RuntimeOwned || active.Clock != clock) continue;
                if (active.ExpireAtVirtual <= 0d && definition.DurationPolicy != DurationPolicy.Infinite)
                    BindSchedule(ref active, definition, resetPeriodic: false);
                if (!ApplySourceDeathPolicy(ref active)) { Remove(active.Target, active.Handle); expired++; continue; }
                if (definition.DurationPolicy == DurationPolicy.Infinite)
                {
                    UpdateActive(active.Target, active);
                    continue;
                }
                if (definition.Type == EffectType.Periodic && definition.Periodic.HasValue && active.TicksRemaining > 0)
                    TickPeriodicDue(ref active, definition, now);
                active.RemainingTime = (float)Math.Max(0d, active.ExpireAtVirtual - now);
                if (definition.Type == EffectType.Periodic && definition.Periodic.HasValue)
                {
                    float period = definition.Periodic.Value.Period;
                    if (active.CatchUp == CatchUpPolicy.SkipMissed)
                        active.TickAccumulator = (float)Math.Max(0d, period - (active.NextTickAtVirtual - now));
                    else
                        active.TickAccumulator = (float)Math.Max(0d, period - (active.NextTickAtVirtual - now));
                }
                if (active.RemainingTime <= 0f)
                {
                    Remove(active.Target, active.Handle, GameplayEventType.EffectExpired);
                    expired++;
                    continue;
                }
                SyncSchedule(active.Handle, active, definition);
                UpdateActive(active.Target, active);
            }
            return expired;
        }

        private void TickPeriodicDue(ref ActiveGameplayEffect active, GameplayEffectDefinition definition, double now)
        {
            float period = definition.Periodic.Value.Period;
            if (period <= 0f) return;
            int dueCount = 0;
            active.FirstTickPending = false;
            while (active.TicksRemaining > 0 && active.NextTickAtVirtual <= now)
            {
                if (active.CatchUp == CatchUpPolicy.OnePerFrame && dueCount >= 1) break;
                if (active.CatchUp == CatchUpPolicy.SkipMissed && dueCount >= 1) break;
                float tickMagnitude = ResolvePeriodicMagnitude(active, definition.Periodic.Value);
                DispatchPeriodic(active, definition, tickMagnitude);
                active.TicksProcessed++;
                active.TicksRemaining--;
                dueCount++;
                if (active.CatchUp == CatchUpPolicy.SkipMissed)
                {
                    active.NextTickAtVirtual = now + period;
                    active.TickAccumulator = 0f;
                    break;
                }
                active.NextTickAtVirtual += period;
            }
        }

        internal void RegisterTimedAbility(int entityId, int slot, ClockId clock, double expireAt)
        {
            _schedule.UpsertAbility(clock, entityId, slot, expireAt);
            for (int i = 0; i < _timedAbilities.Count; i++)
                if (_timedAbilities[i].entityId == entityId && _timedAbilities[i].slot == slot) return;
            _timedAbilities.Add((entityId, slot));
        }

        internal void UnregisterTimedAbility(int entityId, int slot)
        {
            _schedule.ClearAbility(entityId, slot);
            for (int i = _timedAbilities.Count - 1; i >= 0; i--)
                if (_timedAbilities[i].entityId == entityId && _timedAbilities[i].slot == slot)
                    _timedAbilities.RemoveAt(i);
        }

        private int TickTimedAbilities(ClockId clock)
        {
            int expired = 0;
            double now = _schedule.VirtualNow(clock);
            for (int i = _timedAbilities.Count - 1; i >= 0; i--)
            {
                int entityId = _timedAbilities[i].entityId;
                int slot = _timedAbilities[i].slot;
                var instance = _store.GetAbility(entityId, slot);
                if (instance.State.Phase != AbilityPhase.Executing || instance.State.DurationClock != clock)
                    continue;
                if (!instance.TryTickTimed(now))
                {
                    _store.SetAbility(entityId, slot, instance);
                    continue;
                }
                _store.SetAbility(entityId, slot, instance);
                _schedule.ClearAbility(entityId, slot);
                _timedAbilities.RemoveAt(i);
                expired++;
            }
            return expired;
        }
        private void DispatchPeriodic(ActiveGameplayEffect active, GameplayEffectDefinition definition, float magnitude)
        {
            var spec = definition.Periodic.Value;
            magnitude *= Math.Max(1, active.StackCount);
            long sequence = _store.AllocateGameplaySequence(active.Target.Index);
            bool allowMissingSource = active.SourceDeath == SourceDeathPolicy.Persist &&
                (!active.Source.IsValid || !_store.TryResolve(active.Source, out _, out _));
            switch (spec.Payload)
            {
                case EffectPayloadKind.Damage:
                    var damageRequest = DamageRequest.ForPersistentEffect(active.Source, active.Target, magnitude, spec.Damage ?? DamageType.True, spec.Element ?? ElementType.None, DamageFlags.None, DamageAmountStage.Raw, DamageCommitBoundary.GameplayResolve, sequence, active.DefinitionId, active.OwnerPlayerId, new ExecutionContext(active.Source, active.Target, default(AbilityId), active.DefinitionId, active.Clock, active.ApplicationSequence, active.OwnerPlayerId, magnitude, active.ProvenanceId, 1), active.ProvenanceId, 1);
                    var damageResult = allowMissingSource ? _store.DamageResolver.TryApply(damageRequest) : _store.DamageResolver.TryApply(new DamageRequest(active.Source, active.Target, magnitude, spec.Damage ?? DamageType.True, spec.Element ?? ElementType.None, DamageFlags.None, DamageAmountStage.Raw, DamageCommitBoundary.GameplayResolve, sequence, effect: active.DefinitionId, ownerPlayerId: active.OwnerPlayerId, context: new ExecutionContext(active.Source, active.Target, default(AbilityId), active.DefinitionId, active.Clock, active.ApplicationSequence, active.OwnerPlayerId, magnitude, active.ProvenanceId, 1), provenanceId: active.ProvenanceId, provenanceDepth: 1));
                    if (!damageResult.Accepted) Reject(active.Source, active.Target, active.DefinitionId, (int)damageResult.Reason + 10);
                    break;
                case EffectPayloadKind.Heal:
                    var healResult = _store.ResourceResolver.TryApply(allowMissingSource ? HealRequest.ForPersistentEffect(active.Source, active.Target, magnitude, sequence, active.OwnerPlayerId) : new HealRequest(active.Source, active.Target, magnitude, sequence, active.OwnerPlayerId));
                    if (!healResult.Accepted) Reject(active.Source, active.Target, active.DefinitionId, (int)healResult.Reason + 20);
                    break;
                case EffectPayloadKind.Resource:
                    var resourceResult = _store.ResourceResolver.TryApply(allowMissingSource ? ResourceRequest.ForPersistentEffect(active.Source, active.Target, spec.Resource, magnitude, sequence, active.OwnerPlayerId, active.ProvenanceId) : new ResourceRequest(active.Source, active.Target, spec.Resource, magnitude, sequence, active.OwnerPlayerId));
                    if (!resourceResult.Accepted) Reject(active.Source, active.Target, active.DefinitionId, (int)resourceResult.Reason + 30);
                    break;
                case EffectPayloadKind.GameplayEvent:
                    Publish(new GameplayEvent(spec.EventType, active.Source, active.Target, active.Handle, active.DefinitionId, DamageFlags.None, sequence, active.ApplicationSequence, provenanceId: active.ProvenanceId, provenanceDepth: 1, tag: definition.Tag, ownerPlayerId: active.OwnerPlayerId));
                    break;
                default:
                    Rejections++; Publish(new GameplayEvent(GameplayEventType.EffectRejected, active.Source, active.Target, active.Handle, active.DefinitionId, DamageFlags.None, sequence, reason: 2)); break;
            }
        }
    }

    /// <summary>只消费 Resolver 产生的 GameplayEvent，并将链式请求送入下一队列。</summary>
    public sealed class GameplayTriggerRuntime
    {
        private readonly ComponentStore _store;
        private readonly GameplayEffectRuntime _effects;
        private readonly Dictionary<(int id, int source, int sourceGen, int target, int targetGen, int player, int playerGen), int> _counters = new Dictionary<(int, int, int, int, int, int, int), int>(4096);
        private readonly HashSet<(GameplayEventType, long, int, int, int, int)> _seen = new HashSet<(GameplayEventType, long, int, int, int, int)>(8192);
        private readonly Dictionary<EffectId, GameplayEffectDefinition> _definitions = new Dictionary<EffectId, GameplayEffectDefinition>(4096);
        private readonly Dictionary<int, TriggerResetPolicy> _resetPolicies = new Dictionary<int, TriggerResetPolicy>(4096);
        private readonly List<(int, int, int, int, int, int, int)> _staleCounters = new List<(int, int, int, int, int, int, int)>(256);
        private GameplayEventType[] _allowedTypes;
        private static readonly GameplayEventType[] _hitTypes = { GameplayEventType.HitConfirmed, GameplayEventType.DamageApplied, GameplayEventType.EffectApplied };
        private static readonly GameplayEventType[] _resourceTypes = { GameplayEventType.HealApplied, GameplayEventType.ShieldChanged, GameplayEventType.ResourceChanged };
        private static readonly GameplayEventType[] _killTypes = { GameplayEventType.KillConfirmed, GameplayEventType.ResourceChanged, GameplayEventType.DeathQueued };
        public GameplayEventQueue NextEvents { get; }
        /// <summary>当前帧的持久诊断；与 NextEvents 不同，不会被触发器轮次消费。</summary>
        public GameplayEventQueue AbortEvents { get; }
        public long LastAbortSequence { get; private set; }
        public int LastAbortReason { get; private set; }
        public int LastAbortRemaining { get; private set; }
        public int MaxEventsPerFrame { get; }
        public int LoopAborts { get; private set; }
        public int PublicationFailures { get; private set; }
        public int AbortPublicationFailures { get; private set; }
        public CommandRejection LastAbortRejection { get; private set; }
        public int Rejections { get; private set; }
        private int _frameEventsConsumed;
        public int SeenCount => _seen.Count;
        public int CounterCount => _counters.Count;
        public int DefinitionCount => _definitions.Count;
        public int TriggerDefinitionCount { get; private set; }
        public int PeakSeenCount { get; private set; }
        public int PeakCounterCount { get; private set; }
        public int PeakDefinitionCount { get; private set; }
        public int PeakTriggerDefinitionCount { get; private set; }
        public int DefinitionCapacity { get; } = 4096;
        public int SeenCapacity { get; } = 8192;
        public int MaxCounterEntries { get; } = 4096;
        public GameplayTriggerRuntime(ComponentStore store, GameplayEffectRuntime effects, int capacity = 8192, int maxEventsPerFrame = 8192) { _store = store ?? throw new ArgumentNullException(nameof(store)); _effects = effects ?? throw new ArgumentNullException(nameof(effects)); NextEvents = new GameplayEventQueue(Math.Max(1, capacity), Math.Min(64, Math.Max(0, capacity / 8))); AbortEvents = new GameplayEventQueue(Math.Max(4, Math.Min(64, Math.Max(1, capacity))), 1); MaxEventsPerFrame = Math.Max(1, maxEventsPerFrame); }
        public bool RegisterEffect(GameplayEffectDefinition definition)
        {
            if (definition.Id.Value < 0 || !GameplayEffectRuntime.IsDurationContractValid(definition) || (definition.Type == EffectType.Periodic && (!definition.Periodic.HasValue || (definition.Periodic.Value.MagnitudeSource != MagnitudeSource.Constant && definition.Periodic.Value.MagnitudeSource != MagnitudeSource.Attribute) || definition.Periodic.Value.Period <= 0f || float.IsNaN(definition.Periodic.Value.Period) || float.IsInfinity(definition.Periodic.Value.Period) || definition.Duration <= 0f || float.IsNaN(definition.Duration) || float.IsInfinity(definition.Duration) || (definition.Periodic.Value.Payload != EffectPayloadKind.GameplayEvent && definition.Periodic.Value.MagnitudeSource == MagnitudeSource.Constant && (definition.Periodic.Value.Magnitude <= 0f || float.IsNaN(definition.Periodic.Value.Magnitude) || float.IsInfinity(definition.Periodic.Value.Magnitude))))))
            {
                Rejections++;
                var invalid = new GameplayEvent(GameplayEventType.EffectRejected, default(EntityHandle), default(EntityHandle), default(EffectHandle), definition.Id, DamageFlags.None, 0L, reason: 7);
                PublishNext(invalid);
                Abort(invalid, 7, 0);
                return false;
            }
            if (!_definitions.ContainsKey(definition.Id) && _definitions.Count >= DefinitionCapacity)
            {
                Rejections++;
                var rejected = new GameplayEvent(GameplayEventType.EffectRejected, default(EntityHandle), default(EntityHandle), default(EffectHandle), definition.Id, DamageFlags.None, 0L, reason: 4);
                PublishNext(rejected);
                Abort(rejected, 4, 0);
                return false;
            }
            _definitions[definition.Id] = definition;
            if (_definitions.Count > PeakDefinitionCount) PeakDefinitionCount = _definitions.Count;
            return true;
        }
        public int GetCounter(TriggerDefinition definition, EntityHandle source, EntityHandle target)
        {
            int sourceId = definition.Scope == TriggerScope.PerTarget || definition.Scope == TriggerScope.PerPlayer ? 0 : source.Index;
            int sourceGen = definition.Scope == TriggerScope.PerTarget || definition.Scope == TriggerScope.PerPlayer ? 0 : source.Generation;
            int targetId = definition.Scope == TriggerScope.PerSource || definition.Scope == TriggerScope.PerPlayer ? 0 : target.Index;
            int targetGen = definition.Scope == TriggerScope.PerSource || definition.Scope == TriggerScope.PerPlayer ? 0 : target.Generation;
            int playerId = definition.Scope == TriggerScope.PerPlayer ? (source.IsValid ? source.Index : -1) : 0;
            int playerGen = definition.Scope == TriggerScope.PerPlayer ? _store.GetEntityHandle(playerId).Generation : 0;
            return _counters.TryGetValue((definition.Id.Value, sourceId, sourceGen, targetId, targetGen, playerId, playerGen), out int value) ? value : 0;
        }

        /// <summary>按事件归属玩家读取 PerPlayer 计数，避免敌方或塔 source 索引污染 owner 维度。</summary>
        public int GetCounter(TriggerDefinition definition, EntityHandle source, EntityHandle target, int ownerPlayerId)
        {
            if (definition.Scope != TriggerScope.PerPlayer) return GetCounter(definition, source, target);
            if (ownerPlayerId < 0 || ownerPlayerId >= ComponentStore.MAX_PLAYERS) return 0;
            int generation = _store.GetEntityHandle(ownerPlayerId).Generation;
            return _counters.TryGetValue((definition.Id.Value, 0, 0, 0, 0, ownerPlayerId, generation), out int value) ? value : 0;
        }
        [Obsolete("请使用带 TriggerDefinition 的精确读取重载")]
        public int GetCounter(TriggerId trigger, EntityHandle source, EntityHandle target = default(EntityHandle)) => 0;

        public int Consume(GameplayEventQueue events, IReadOnlyList<TriggerDefinition> definitions, bool clear = false)
        {
            if (events == null || definitions == null) return 0;
            TriggerDefinitionCount = definitions.Count;
            if (TriggerDefinitionCount > PeakTriggerDefinitionCount)
                PeakTriggerDefinitionCount = TriggerDefinitionCount;
            int fired = 0;
            int inputCount = events.Count;
            for (int i = 0; i < inputCount; i++)
            {
                var e = events.Get(i);
                if (_allowedTypes != null) { bool allowed = false; for (int a = 0; a < _allowedTypes.Length; a++) if (e.Type == _allowedTypes[a]) { allowed = true; break; } if (!allowed) continue; }
                var dedupe = (e.Type, e.Sequence, e.Source.Index, e.Target.Index, e.Source.Generation, e.Target.Generation);
                if (_seen.Contains(dedupe)) continue;
                if (_frameEventsConsumed >= MaxEventsPerFrame) { Abort(e, 1, inputCount - i); break; }
                if (_seen.Count >= SeenCapacity && !_seen.Contains(dedupe)) { Abort(e, 2, inputCount - i); continue; }
                if (!_seen.Add(dedupe)) continue;
                if (_seen.Count > PeakSeenCount) PeakSeenCount = _seen.Count;
                _frameEventsConsumed++;
                for (int t = 0; t < definitions.Count; t++)
                {
                    var d = definitions[t]; if (d.EventType != e.Type) continue;
                    if (d.Threshold <= 0 || d.EffectStackDelta <= 0)
                    {
                        Rejections++;
                        PublishNext(new GameplayEvent(GameplayEventType.EffectRejected, e.Source, e.Target, NextSequence(e.Source, e.Target), reason: 7));
                        continue;
                    }
                    if (!_resetPolicies.ContainsKey(d.Id.Value) && _resetPolicies.Count >= DefinitionCapacity)
                    {
                        Abort(e, 4, inputCount - i);
                        continue;
                    }
                    _resetPolicies[d.Id.Value] = d.ResetPolicy;
                    bool tagMatch = d.FilterTags.Count == 0;
                    for (int ft = 0; ft < d.FilterTags.Count; ft++) if (d.FilterTags[ft].Equals(e.Tag)) { tagMatch = true; break; }
                    if (!tagMatch || (!d.EffectTag.Equals(default(TagId)) && !d.EffectTag.Equals(e.Tag))) { PublishNext(new GameplayEvent(GameplayEventType.EffectRejected, e.Source, e.Target, NextSequence(e.Source, e.Target), 1)); continue; }
                    int source = d.Scope == TriggerScope.PerTarget || d.Scope == TriggerScope.PerPlayer ? 0 : e.Source.Index;
                    int sourceGen = d.Scope == TriggerScope.PerTarget || d.Scope == TriggerScope.PerPlayer ? 0 : e.Source.Generation;
                    int target = d.Scope == TriggerScope.PerSource || d.Scope == TriggerScope.PerPlayer ? 0 : e.Target.Index;
                    int targetGen = d.Scope == TriggerScope.PerSource || d.Scope == TriggerScope.PerPlayer ? 0 : e.Target.Generation;
                    int player = d.Scope == TriggerScope.PerPlayer ? e.OwnerPlayerId : 0;
                    if (d.Scope == TriggerScope.PerPlayer && (player < 0 || player >= ComponentStore.MAX_PLAYERS))
                    {
                        Rejections++;
                        PublishNext(new GameplayEvent(GameplayEventType.EffectRejected, e.Source, e.Target, NextSequence(e.Source, e.Target), reason: 8));
                        continue;
                    }
                    bool sourceValid = _store.TryResolve(e.Source, out _, out _);
                    bool targetValid = _store.TryResolve(e.Target, out _, out _);
                    bool requiredTargetValid = targetValid || ((e.Type == GameplayEventType.KillConfirmed || e.Type == GameplayEventType.DeathQueued) && d.EffectTarget == EffectTargetPolicy.Source);
                    if (!sourceValid || !requiredTargetValid)
                    {
                        Rejections++;
                        PublishNext(new GameplayEvent(GameplayEventType.EffectRejected, e.Source, e.Target, NextSequence(e.Source, e.Target), reason: 9));
                        continue;
                    }
                    int playerGen = d.Scope == TriggerScope.PerPlayer ? _store.GetEntityHandle(player).Generation : 0;
                    var key = (id: d.Id.Value, source, sourceGen, target, targetGen, player, playerGen);
                    if (!_counters.ContainsKey(key) && _counters.Count >= MaxCounterEntries) { Abort(e, 3, events.Count - i); continue; }
                    _counters.TryGetValue(key, out int old); int total = old + 1; int crossings = d.Mode == TriggerMode.EveryN ? (total / d.Threshold) - (old / d.Threshold) : (old < d.Threshold && total >= d.Threshold ? 1 : 0);
                    _counters[key] = d.Mode == TriggerMode.EveryN && crossings > 0 && !d.PreserveRemainder ? 0 : (d.Mode == TriggerMode.EveryN && d.PreserveRemainder ? total % d.Threshold : total);
                    if (_counters.Count > PeakCounterCount) PeakCounterCount = _counters.Count;
                    for (int k = 0; k < crossings; k++) { if (!_definitions.TryGetValue(d.Effect, out var effect)) { Rejections++; PublishNext(new GameplayEvent(GameplayEventType.EffectRejected, e.Source, e.Target, NextSequence(e.Source, e.Target), 3)); continue; } var effectTarget = d.EffectTarget == EffectTargetPolicy.Target ? e.Target : e.Source; int effectEventIndex = _effects.Events.Count; bool applied = _effects.TryApply(d.Effect, effect, e.Source, effectTarget, out _, d.EffectStackDelta, float.NaN, float.NaN, e.OwnerPlayerId, e.ProvenanceId); if (applied) fired++; if (_effects.Events.Count > effectEventIndex) { var generated = _effects.Events.Get(effectEventIndex); PublishNext(generated); _effects.Events.RemoveAt(effectEventIndex); } }
                }
            }
            if (clear) events.RemovePrefix(inputCount); return fired;
        }
        public int ConsumeOnly(GameplayEventQueue events, IReadOnlyList<TriggerDefinition> definitions, bool clear, params GameplayEventType[] allowed)
        {
            if (events == null) return 0;
            var previous = _allowedTypes; _allowedTypes = allowed;
            int result = Consume(events, definitions, clear);
            _allowedTypes = previous;
            return result;
        }
        /// <summary>使用缓存过滤数组消费常用战斗事实，避免热路径 params 分配。</summary>
        public int ConsumeOnly(GameplayEventQueue events, IReadOnlyList<TriggerDefinition> definitions, bool clear, GameplayEventType first, GameplayEventType second, GameplayEventType third)
        {
            if (first == GameplayEventType.HitConfirmed && second == GameplayEventType.DamageApplied && third == GameplayEventType.EffectApplied) return ConsumeOnly(events, definitions, clear, _hitTypes);
            if (first == GameplayEventType.HealApplied && second == GameplayEventType.ShieldChanged && third == GameplayEventType.ResourceChanged) return ConsumeOnly(events, definitions, clear, _resourceTypes);
            if (first == GameplayEventType.KillConfirmed && second == GameplayEventType.ResourceChanged && third == GameplayEventType.DeathQueued) return ConsumeOnly(events, definitions, clear, _killTypes);
            return ConsumeOnly(events, definitions, clear, new[] { first, second, third });
        }
        /// <summary>使用缓存过滤数组消费死亡/资源事实，避免热路径 params 分配。</summary>
        public int ConsumeOnly(GameplayEventQueue events, IReadOnlyList<TriggerDefinition> definitions, bool clear, GameplayEventType first, GameplayEventType second)
        {
            if (first == GameplayEventType.KillConfirmed && second == GameplayEventType.ResourceChanged) return ConsumeOnly(events, definitions, clear, _killTypes);
            return ConsumeOnly(events, definitions, clear, new[] { first, second });
        }
        private void PublishNext(GameplayEvent e)
        {
            if (NextEvents.TryPublish(e, true)) return;
            PublicationFailures++;
            var abort = new GameplayEvent(GameplayEventType.GameplayLoopAborted, e.Source, e.Target, e.Sequence, 5);
            if (!AbortEvents.TryPublish(abort, true)) { AbortPublicationFailures++; LastAbortRejection = AbortEvents.LastRejection; }
        }
        private long NextSequence(EntityHandle source, EntityHandle target)
        {
            int entityId = source.IsValid ? source.Index : (target.IsValid ? target.Index : 0);
            return _store.AllocateGameplaySequence(entityId);
        }
        private void Abort(GameplayEvent trigger, int reason, int remaining)
        {
            LoopAborts++; LastAbortSequence = trigger.Sequence; LastAbortReason = reason; LastAbortRemaining = Math.Max(0, remaining);
            var abort = new GameplayEvent(GameplayEventType.GameplayLoopAborted, trigger.Source, trigger.Target, trigger.Sequence, reason);
            if (!AbortEvents.TryPublish(abort, true)) { AbortPublicationFailures++; LastAbortRejection = AbortEvents.LastRejection; }
        }
        public void ResetFrame()
        {
            _seen.Clear(); NextEvents.Clear(); AbortEvents.Clear(); _frameEventsConsumed = 0; LastAbortSequence = 0L; LastAbortReason = 0; LastAbortRemaining = 0;
            // Explicit 只在 ResetCounters 时清；None 跨帧保留（EveryN 依赖）。ResetFrame 不再自动清任何计数器。
        }
        public void ResetCounters() { _counters.Clear(); }
        public void CleanupEntity(int entityId)
        {
            if (_counters.Count == 0) return;
            _staleCounters.Clear(); foreach (var pair in _counters) if (pair.Key.source == entityId || pair.Key.target == entityId || pair.Key.player == entityId) _staleCounters.Add(pair.Key);
            for (int i = 0; i < _staleCounters.Count; i++) _counters.Remove(_staleCounters[i]);
        }
        public int ConsumeFrame(GameplayEventQueue damageEvents, GameplayEventQueue resourceEvents, IReadOnlyList<TriggerDefinition> definitions)
        {
            int total = 0;
            for (int round = 0; round < 8; round++)
            {
                int before = NextEvents.Count;
                total += Consume(damageEvents, definitions);
                total += Consume(resourceEvents, definitions);
                total += Consume(NextEvents, definitions, true);
                if (before == 0 && NextEvents.Count == 0) break;
            }
            if (NextEvents.Count > 0) { var trigger = NextEvents.Get(NextEvents.Count - 1); Abort(trigger, 4, NextEvents.Count); NextEvents.Clear(); }
            return total;
        }
        public int ConsumeNextRounds(IReadOnlyList<TriggerDefinition> definitions)
        {
            int total = 0;
            for (int round = 0; round < 8 && NextEvents.Count > 0; round++) total += Consume(NextEvents, definitions, true);
            if (NextEvents.Count > 0) { var trigger = NextEvents.Get(NextEvents.Count - 1); Abort(trigger, 4, NextEvents.Count); NextEvents.Clear(); }
            return total;
        }
    }
}
