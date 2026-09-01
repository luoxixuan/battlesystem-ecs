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
        public int ActiveRuntimeCount { get; private set; }
        public bool HasActiveEffects => ActiveRuntimeCount > 0;
        public const int DefaultEventCapacity = 8192;
        public GameplayEventQueue Events { get; }
        /// <summary>效果事件溢出时保留的独立诊断队列。</summary>
        public GameplayEventQueue AbortEvents { get; } = new GameplayEventQueue(64, 1);
        public int Rejections { get; private set; }
        public int PublicationFailures { get; private set; }
        public int AbortPublicationFailures { get; private set; }
        public int ModifierCapacity { get; } = 8192;
        public int EventCapacity { get; }
        public GameplayEffectRuntime(ComponentStore store, int eventCapacity = DefaultEventCapacity) { _store = store ?? throw new ArgumentNullException(nameof(store)); EventCapacity = Math.Max(1, eventCapacity); Events = new GameplayEventQueue(EventCapacity, Math.Min(64, EventCapacity / 8)); }
        internal bool CanApplyPlan(int targetId, int runtimeSlots, int modifierCount, int eventCount)
        {
            if (!ComponentStore.IsValidEntity(targetId) || runtimeSlots < 0 || modifierCount < 0 || eventCount < 0) return false;
            return _store.ActiveEffectCount[targetId] <= ComponentStore.MAX_ACTIVE_EFFECTS_PER_ENTITY - runtimeSlots &&
                _store.GameplayEffectPool.FreeCount >= runtimeSlots &&
                _modifierHandleCount <= ModifierCapacity - modifierCount && Events.CanPublish(eventCount, true);
        }
        internal bool CanApplyPlan(IReadOnlyList<int> targetIds, int runtimeSlotsPerTarget,
            int modifiersPerTarget, int eventCount)
        {
            if (targetIds == null || runtimeSlotsPerTarget < 0 || modifiersPerTarget < 0 || eventCount < 0 ||
                !Events.CanPublish(eventCount, true)) return false;
            long totalSlots = (long)runtimeSlotsPerTarget * targetIds.Count;
            long totalModifiers = (long)modifiersPerTarget * targetIds.Count;
            if (totalSlots > _store.GameplayEffectPool.FreeCount ||
                totalModifiers > ModifierCapacity - _modifierHandleCount) return false;
            for (int i = 0; i < targetIds.Count; i++)
                if (!ComponentStore.IsValidEntity(targetIds[i]) ||
                    _store.ActiveEffectCount[targetIds[i]] > ComponentStore.MAX_ACTIVE_EFFECTS_PER_ENTITY - runtimeSlotsPerTarget)
                    return false;
            return true;
        }

        public bool TryApply(EffectId id, GameplayEffectDefinition definition, EntityHandle source, EntityHandle target, out EffectHandle handle, int stackDelta = 1, float snapshot = float.NaN, int ownerPlayerId = -1, long provenanceId = 0L)
        {
            handle = default(EffectHandle);
            if (!source.IsValid || !target.IsValid || definition.Id.Value != id.Value || !_store.TryResolve(target, out int targetId, out _) || !_store.TryResolve(source, out _, out _)) { Reject(source, target, id, 1); return false; }
            if (!IsDurationContractValid(definition)) { Reject(source, target, id, 6); return false; }
            if (definition.Type == EffectType.Periodic && (definition.DurationPolicy != DurationPolicy.Duration || definition.Duration <= 0f || float.IsNaN(definition.Duration) || float.IsInfinity(definition.Duration))) { Reject(source, target, id, 6); return false; }
            if (definition.Type == EffectType.Duration && definition.DurationPolicy == DurationPolicy.Duration && (definition.Duration <= 0f || float.IsNaN(definition.Duration) || float.IsInfinity(definition.Duration))) { Reject(source, target, id, 6); return false; }
            if (definition.Type == EffectType.Duration && definition.DurationPolicy == DurationPolicy.Infinite && (definition.Duration != 0f || float.IsNaN(definition.Duration) || float.IsInfinity(definition.Duration))) { Reject(source, target, id, 6); return false; }
            if (definition.Type == EffectType.Periodic && (!definition.Periodic.HasValue || !ValidatePeriodicPayload(definition.Periodic.Value, targetId))) { Reject(source, target, id, 2); return false; }
            if (definition.Type == EffectType.Periodic)
            {
                float requestedMagnitude = float.IsNaN(snapshot) ? definition.Periodic.Value.Magnitude : snapshot;
                if (definition.Periodic.Value.Payload != EffectPayloadKind.GameplayEvent && (requestedMagnitude <= 0f || float.IsNaN(requestedMagnitude) || float.IsInfinity(requestedMagnitude))) { Reject(source, target, id, 4); return false; }
            }
            int count = _store.GetEffectCount(targetId);
            TagId key = definition.StackKey.Equals(default(TagId)) ? new TagId(id.Value) : definition.StackKey;
            for (int i = 0; i < count; i++)
            {
                if (!_store.TryGetActiveEffectAt(targetId, i, out var existing, out var existingDef, out _)) continue;
                TagId existingKey = existingDef.StackKey.Equals(default(TagId)) ? new TagId(existingDef.Id.Value) : existingDef.StackKey;
                if (!existingKey.Equals(key)) continue;
                if (definition.Stacking == StackingBehavior.None || definition.Stacking == StackingBehavior.DurationRefresh)
                {
                    if (definition.Refresh != RefreshPolicy.None) { RefreshRuntime(ref existing, definition); if (!UpdateActive(target, existing)) return false; }
                    handle = existing.Handle;
                    Publish(new GameplayEvent(GameplayEventType.EffectApplied, source, target, existing.Handle, id, DamageFlags.None, _store.AllocateGameplaySequence(targetId), tag: definition.Tag, ownerPlayerId: ownerPlayerId));
                    return true;
                }
                int previous = existing.StackCount;
                int next = Math.Min(definition.MaxStacks < 1 ? 1 : definition.MaxStacks, existing.StackCount + Math.Max(1, stackDelta));
                existing.StackCount = next;
                if (next > previous && _modifiers.TryGetValue(existing.Handle, out var prior))
                {
                    int added = (next - previous) * definition.Modifiers.Count;
                    if (_modifierHandleCount + added > ModifierCapacity) { Reject(source, target, id, 3); return false; }
                    var expanded = new AttributeModifierHandle[prior.Length + added];
                    Array.Copy(prior, expanded, prior.Length);
                    int at = prior.Length;
                    try { for (int layer = previous; layer < next; layer++) for (int m = 0; m < definition.Modifiers.Count; m++) { expanded[at++] = _store.AddAttributeModifier(targetId, definition.Modifiers[m], float.IsNaN(snapshot) ? float.NaN : snapshot); if (!expanded[at - 1].IsValid) throw new InvalidOperationException("modifier 分配失败"); } }
                    catch { for (int j = prior.Length; j < at; j++) _store.RemoveAttributeModifier(targetId, expanded[j]); Reject(source, target, id, 3); return false; }
                    _modifiers[existing.Handle] = expanded;
                    _modifierHandleCount += added;
                }
                if (definition.Refresh == RefreshPolicy.StacksAndDuration || definition.Stacking == StackingBehavior.MaxStacksRefresh) RefreshRuntime(ref existing, definition);
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
            if (definition.Modifiers.Count > 0 && _modifierHandleCount + definition.Modifiers.Count > ModifierCapacity) { Reject(source, target, id, 3); return false; }
            float magnitude = float.IsNaN(snapshot) ? (definition.Periodic.HasValue ? definition.Periodic.Value.Magnitude : 0f) : snapshot;
            if (definition.Type == EffectType.Periodic && definition.Periodic.Value.Payload != EffectPayloadKind.GameplayEvent && (magnitude <= 0f || float.IsNaN(magnitude) || float.IsInfinity(magnitude))) { Reject(source, target, id, 4); return false; }
            var runtime = new ActiveGameplayEffect(default(EffectHandle), id, source, target, definition.Duration, ticks, magnitude, definition.Clock,
                definition.Periodic.HasValue ? definition.Periodic.Value.FirstTick : FirstTickPolicy.NextInterval,
                definition.Periodic.HasValue ? definition.Periodic.Value.CatchUp : CatchUpPolicy.CatchUpAll, definition.SourceDeath, ownerPlayerId, _store.AllocateGameplaySequence(targetId), provenanceId, definition.Tag);
            runtime.RuntimeOwned = true;
            var app = new GameplayEffectApplication(definition, default(LegacyEffectSnapshot), runtime);
            if (!_store.TryAddGameplayEffect(targetId, app, out handle)) { Reject(source, target, id, 5); return false; }
            ActiveRuntimeCount++;
            RegisterRuntimeEntity(targetId);
            if (!ApplyModifiers(targetId, definition, snapshot, handle))
            {
                _store.TryRemoveEffect(target, handle, out _);
                ActiveRuntimeCount = Math.Max(0, ActiveRuntimeCount - 1);
                UnregisterRuntimeEntity(targetId);
                handle = default(EffectHandle);
                Reject(source, target, id, 3);
                return false;
            }
            Publish(new GameplayEvent(GameplayEventType.EffectApplied, source, target, handle, id, DamageFlags.None, _store.AllocateGameplaySequence(targetId), tag: definition.Tag, ownerPlayerId: ownerPlayerId));
            return true;
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

        private bool ValidatePeriodicPayload(PeriodicSpec spec, int targetId)
        {
            if (spec.Period <= 0f || float.IsNaN(spec.Period) || float.IsInfinity(spec.Period)) return false;
            bool player = (uint)targetId < ComponentStore.MAX_PLAYERS && _store.PositionActive[targetId];
            bool enemy = ComponentStore.IsValidEntity(targetId) && _store.EnemyActive[targetId];
            if (!player && !enemy || spec.MagnitudeSource != MagnitudeSource.Constant) return false;
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

        private bool ApplyModifiers(int targetId, GameplayEffectDefinition definition, float snapshot, EffectHandle handle)
        {
            if (definition.Modifiers.Count == 0) return true;
            if (handle.IsValid)
            {
                if (_modifierHandleCount + definition.Modifiers.Count > ModifierCapacity) return false;
                var hs = new AttributeModifierHandle[definition.Modifiers.Count];
                try { for (int i = 0; i < hs.Length; i++) { hs[i] = _store.AddAttributeModifier(targetId, definition.Modifiers[i], float.IsNaN(snapshot) ? float.NaN : snapshot); if (!hs[i].IsValid) throw new InvalidOperationException("modifier 分配失败"); } }
                catch { for (int i = 0; i < hs.Length; i++) if (hs[i].IsValid) _store.RemoveAttributeModifier(targetId, hs[i]); return false; }
                _modifiers[handle] = hs;
                _modifierHandleCount += hs.Length;
            }
            else
            {
                for (int i = 0; i < definition.Modifiers.Count; i++)
                {
                    var modifier = _store.AddAttributeModifier(targetId, definition.Modifiers[i], float.IsNaN(snapshot) ? float.NaN : snapshot);
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
        private static void RefreshRuntime(ref ActiveGameplayEffect active, GameplayEffectDefinition definition)
        {
            active.RemainingTime = definition.DurationPolicy == DurationPolicy.Infinite ? 0f : definition.Duration;
            active.TicksRemaining = CalculateTicks(definition);
            active.TickAccumulator = 0f;
            active.FirstTickPending = true;
        }

        public bool Remove(EntityHandle target, EffectHandle handle, GameplayEventType reason = GameplayEventType.EffectRemoved)
        {
            if (!handle.IsValid || !_store.TryGetActiveEffect(target, handle, out var runtime, out _, out _)) return false;
            if (_modifiers.TryGetValue(handle, out var modifiers)) { for (int i = 0; i < modifiers.Length; i++) _store.RemoveAttributeModifier(target.Index, modifiers[i]); _modifierHandleCount -= modifiers.Length; _modifiers.Remove(handle); }
            bool removed = _store.TryRemoveEffect(target, handle, out _);
            if (removed) { if (runtime.RuntimeOwned) { ActiveRuntimeCount = Math.Max(0, ActiveRuntimeCount - 1); UnregisterRuntimeEntity(target.Index); } Publish(new GameplayEvent(reason, runtime.Source, target, handle, runtime.DefinitionId, DamageFlags.None, _store.AllocateGameplaySequence(target.Index), tag: runtime.Tag, ownerPlayerId: runtime.OwnerPlayerId)); }
            return removed;
        }

        public void CleanupEntity(int entityId)
        {
            if (ActiveRuntimeCount == 0 && _modifiers.Count == 0) return;
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
            if (ActiveRuntimeCount == 0) return 0;
            int expired = 0;
            for (int n = 0; n < _runtimeEntityIds.Count; n++)
            {
                int entityId = _runtimeEntityIds[n];
                expired += TickEntity(entityId, deltaTime, clock);
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
        private int TickEntity(int entityId, float dt, ClockId clock)
        {
            int expired = 0;
            for (int slot = _store.GetEffectCount(entityId) - 1; slot >= 0; slot--)
            {
                if (!_store.TryGetActiveEffectAt(entityId, slot, out var active, out var definition, out _)) continue;
                if (!active.RuntimeOwned || active.Clock != clock) continue;
                if (active.SourceDeath == SourceDeathPolicy.Remove && (!active.Source.IsValid || !_store.TryResolve(active.Source, out _, out _))) { Remove(active.Target, active.Handle); expired++; continue; }
                if (definition.DurationPolicy == DurationPolicy.Infinite) continue;
                if (definition.Type == EffectType.Periodic && definition.Periodic.HasValue && active.TicksRemaining > 0)
                {
                    active.TickAccumulator += dt;
                    int scheduledDue = (int)Math.Floor(active.TickAccumulator / definition.Periodic.Value.Period);
                    bool immediateTick = active.FirstTickPending && active.FirstTick == FirstTickPolicy.Immediate;
                    int due = scheduledDue;
                    if (immediateTick) { due++; active.FirstTickPending = false; }
                    if (active.FirstTickPending) active.FirstTickPending = false;
                    if (active.CatchUp == CatchUpPolicy.OnePerFrame && due > 1) due = 1;
                    if (active.CatchUp == CatchUpPolicy.SkipMissed && due > 0) { due = 1; active.TickAccumulator = 0f; }
                    due = Math.Min(due, active.TicksRemaining);
                    if (active.CatchUp != CatchUpPolicy.SkipMissed && due > 0)
                    {
                        int scheduledConsumed = Math.Min(scheduledDue, immediateTick ? Math.Max(0, due - 1) : due);
                        active.TickAccumulator -= scheduledConsumed * definition.Periodic.Value.Period;
                    }
                    active.TicksRemaining -= due;
                    for (int tick = 0; tick < due; tick++) { DispatchPeriodic(active, definition, active.CapturedMagnitude); active.TicksProcessed++; }
                }
                if (definition.DurationPolicy != DurationPolicy.Infinite)
                {
                    active.RemainingTime -= dt;
                    if (active.RemainingTime <= 0f) { Remove(active.Target, active.Handle, GameplayEventType.EffectExpired); expired++; continue; }
                }
                UpdateActive(active.Target, active);
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
        public int DefinitionCapacity { get; } = 4096;
        public int SeenCapacity { get; } = 8192;
        public int MaxCounterEntries { get; } = 4096;
        public GameplayTriggerRuntime(ComponentStore store, GameplayEffectRuntime effects, int capacity = 8192, int maxEventsPerFrame = 8192) { _store = store ?? throw new ArgumentNullException(nameof(store)); _effects = effects ?? throw new ArgumentNullException(nameof(effects)); NextEvents = new GameplayEventQueue(Math.Max(1, capacity), Math.Min(64, Math.Max(0, capacity / 8))); AbortEvents = new GameplayEventQueue(Math.Max(4, Math.Min(64, Math.Max(1, capacity))), 1); MaxEventsPerFrame = Math.Max(1, maxEventsPerFrame); }
        public bool RegisterEffect(GameplayEffectDefinition definition)
        {
            if (definition.Id.Value < 0 || !GameplayEffectRuntime.IsDurationContractValid(definition) || (definition.Type == EffectType.Periodic && (!definition.Periodic.HasValue || definition.Periodic.Value.MagnitudeSource != MagnitudeSource.Constant || definition.Periodic.Value.Period <= 0f || float.IsNaN(definition.Periodic.Value.Period) || float.IsInfinity(definition.Periodic.Value.Period) || definition.Duration <= 0f || float.IsNaN(definition.Duration) || float.IsInfinity(definition.Duration) || (definition.Periodic.Value.Payload != EffectPayloadKind.GameplayEvent && (definition.Periodic.Value.Magnitude <= 0f || float.IsNaN(definition.Periodic.Value.Magnitude) || float.IsInfinity(definition.Periodic.Value.Magnitude))))))
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
                    for (int k = 0; k < crossings; k++) { if (!_definitions.TryGetValue(d.Effect, out var effect)) { Rejections++; PublishNext(new GameplayEvent(GameplayEventType.EffectRejected, e.Source, e.Target, NextSequence(e.Source, e.Target), 3)); continue; } var effectTarget = d.EffectTarget == EffectTargetPolicy.Target ? e.Target : e.Source; int effectEventIndex = _effects.Events.Count; bool applied = _effects.TryApply(d.Effect, effect, e.Source, effectTarget, out _, d.EffectStackDelta, float.NaN, e.OwnerPlayerId, e.ProvenanceId); if (applied) fired++; if (_effects.Events.Count > effectEventIndex) { var generated = _effects.Events.Get(effectEventIndex); PublishNext(generated); _effects.Events.RemoveAt(effectEventIndex); } }
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
            _staleCounters.Clear();
            foreach (var pair in _counters) if (_resetPolicies.TryGetValue(pair.Key.id, out var policy) && policy == TriggerResetPolicy.Explicit) _staleCounters.Add(pair.Key);
            for (int i = 0; i < _staleCounters.Count; i++) _counters.Remove(_staleCounters[i]);
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
