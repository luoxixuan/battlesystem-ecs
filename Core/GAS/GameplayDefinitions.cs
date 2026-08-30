using System;
using System.Collections.Generic;
using BattleSystemECS.Components;

namespace BattleSystemECS.Core.GAS
{
    internal static class ImmutableViews
    {
        internal static IReadOnlyList<T> List<T>(IReadOnlyList<T> values) { var copy = new T[values == null ? 0 : values.Count]; if (values != null) for (int i = 0; i < copy.Length; i++) copy[i] = values[i]; return Array.AsReadOnly(copy); }
    }
    public enum TargetingShape { Single, Cross, Box, Circle, Chain, Heal, Shield, Line, Freeze, Cone, GroundTarget, Slow, TimeRewind, ChainHeal, MassResurrect, AoeStun, AoeRoot, AoeKnockback }
    public enum EffectPayloadKind { Damage, Heal, Shield, Resurrect, Resource, CrowdControl, Slow, GameplayEvent }
    public enum ExecutionOperation { Default, ApplyDamage, ApplyHeal, ApplyShield, Resurrect, RestoreSnapshot, ApplyCrowdControl, ApplySlow }
    public enum RefreshPolicy { None, Duration, StacksAndDuration }
    public enum SourceDeathPolicy { Persist, Remove }
    public enum ActivationPolicy { Instant, InputPressed, Passive }
    public enum MagnitudeSource { Constant, Attribute, Multiplier }
    public enum DamageAmountStage { Raw, LegacyMultiplier }
    public enum SnapshotPolicy { CaptureOnApply, ReevaluateOnRead }
    public enum FirstTickPolicy { NextInterval, Immediate }
    public enum CatchUpPolicy { CatchUpAll, OnePerFrame, SkipMissed }
    public enum RelationFilter { Any, Enemies, Allies, Self }
    public enum MaxTargetsPolicy { Derived, Unlimited, Fixed }
    [Flags] public enum GameplayPhaseMask { None = 0, Build = 1, Wave = 2, Intermission = 4 }
    public readonly struct ExecutorId : IEquatable<ExecutorId> { public readonly int Value; public ExecutorId(int value) { Value = value; } public bool Equals(ExecutorId other) => Value == other.Value; public override bool Equals(object obj) => obj is ExecutorId other && Equals(other); public override int GetHashCode() => Value; }
    public readonly struct ConsumerId : IEquatable<ConsumerId> { public readonly int Value; public ConsumerId(int value) { Value = value; } public bool Equals(ConsumerId other) => Value == other.Value; public override bool Equals(object obj) => obj is ConsumerId other && Equals(other); public override int GetHashCode() => Value; }
    public enum EffectTag { None, Normal, Burn, Fire, Freeze, Lightning, Explosion, Plasma, Poison }
    public readonly struct ExecutionId : IEquatable<ExecutionId> { public readonly int Value; public ExecutionId(int value) { Value = value; } public bool Equals(ExecutionId other) => Value == other.Value; public override bool Equals(object obj) => obj is ExecutionId other && Equals(other); public override int GetHashCode() => Value; }
    public readonly struct TargetingId : IEquatable<TargetingId> { public readonly int Value; public TargetingId(int value) { Value = value; } public bool Equals(TargetingId other) => Value == other.Value; public override bool Equals(object obj) => obj is TargetingId other && Equals(other); public override int GetHashCode() => Value; }
    public readonly struct TriggerId : IEquatable<TriggerId> { public readonly int Value; public TriggerId(int value) { Value = value; } public bool Equals(TriggerId other) => Value == other.Value; public override bool Equals(object obj) => obj is TriggerId other && Equals(other); public override int GetHashCode() => Value; }
    public readonly struct PeriodicSpec { public readonly float Period; public readonly FirstTickPolicy FirstTick; public readonly CatchUpPolicy CatchUp; public readonly ExecutionId PayloadExecution; public readonly DamageType? Damage; public readonly ElementType? Element; public PeriodicSpec(float period, FirstTickPolicy firstTick, CatchUpPolicy catchUp, ExecutionId payloadExecution, DamageType? damage = null, ElementType? element = null) { Period = period; FirstTick = firstTick; CatchUp = catchUp; PayloadExecution = payloadExecution; Damage = damage; Element = element; } }
    public readonly struct CostDefinition { public readonly AttributeKey Resource; public readonly float Amount; public CostDefinition(AttributeKey resource, float amount) { Resource = resource; Amount = amount; } }
    public readonly struct ExecutionDefinition { public readonly ExecutionId Id; public readonly EffectPayloadKind Payload; public readonly float Magnitude; public readonly float Duration; public readonly MagnitudeSource MagnitudeSource; public readonly DamageAmountStage Stage; public readonly TagId Tag; public readonly ExecutionOperation Operation; public ExecutionDefinition(ExecutionId id, EffectPayloadKind payload, float magnitude, TagId tag, MagnitudeSource source = MagnitudeSource.Constant, DamageAmountStage stage = DamageAmountStage.Raw, float duration = 0f, ExecutionOperation operation = ExecutionOperation.Default) { Id = id; Payload = payload; Magnitude = magnitude; Duration = duration; Tag = tag; MagnitudeSource = source; Stage = stage; Operation = operation; } }
    public readonly struct ModifierDefinition
    {
        public readonly AttributeKey Attribute;
        public readonly AttributeModifierOp Operation;
        public readonly float Magnitude;
        public readonly MagnitudeSource MagnitudeSource;
        public readonly SnapshotPolicy Snapshot;
        public readonly int Priority;
        public ModifierDefinition(AttributeKey attribute, AttributeModifierOp operation, float magnitude, int priority = 0, MagnitudeSource source = MagnitudeSource.Constant, SnapshotPolicy snapshot = SnapshotPolicy.ReevaluateOnRead) { Attribute = attribute; Operation = operation; Magnitude = magnitude; Priority = priority; MagnitudeSource = source; Snapshot = snapshot; }
    }
    public readonly struct TargetingDefinition
    {
        public readonly TargetingId Id;
        public readonly TargetingShape Shape;
        public readonly int Range, Width, Height, MaxTargets;
        public readonly float Radius, Angle;
        public readonly RelationFilter Relation;
        public readonly MaxTargetsPolicy MaxTargetsMode;
        public readonly IReadOnlyList<TagId> RequiredTags, BlockedTags;
        public TargetingDefinition(TargetingId id, TargetingShape shape, int range, int width, int height, int maxTargets, float radius = 0f, float angle = 0f, TagId[] requiredTags = null, TagId[] blockedTags = null, RelationFilter relation = RelationFilter.Any, MaxTargetsPolicy maxTargetsMode = MaxTargetsPolicy.Derived) { Id = id; Shape = shape; Range = range; Width = width; Height = height; MaxTargets = maxTargets; Radius = radius; Angle = angle; RequiredTags = ImmutableViews.List(requiredTags); BlockedTags = ImmutableViews.List(blockedTags); Relation = relation; MaxTargetsMode = maxTargetsMode; }
    }
    public readonly struct TriggerDefinition
    {
        public readonly TriggerId Id;
        public readonly IReadOnlyList<TagId> FilterTags;
        public readonly TagId EffectTag;
        public readonly ConsumerId Consumer;
        public readonly GameplayEventType EventType;
        public readonly EffectId Effect;
        public TriggerDefinition(TriggerId id, GameplayEventType eventType, EffectId effect, ConsumerId consumer, TagId[] filterTags = null, TagId effectTag = default(TagId)) { Id = id; EventType = eventType; Effect = effect; Consumer = consumer; FilterTags = ImmutableViews.List(filterTags); EffectTag = effectTag; }
    }
    public readonly struct AbilityDefinition
    {
        public readonly AbilityId Id;
        public readonly TargetingDefinition Targeting;
        public readonly ClockId Clock;
        public readonly float Cooldown;
        public readonly GameplayPhaseMask AllowedPhases;
        public readonly IReadOnlyList<EffectId> Effects;
        public readonly string Name;
        public readonly ExecutorId Executor;
        public readonly ConsumerId Consumer;
        public readonly IReadOnlyList<ModifierDefinition> Modifiers;
        public readonly ActivationPolicy Activation;
        public readonly IReadOnlyList<CostDefinition> Costs;
        public int ManaCost { get { return Costs.Count == 0 ? 0 : (int)Costs[0].Amount; } }
        public readonly IReadOnlyList<ExecutionId> Executions;
        public readonly IReadOnlyList<TagId> RequiredTags, BlockedTags;
        public readonly IReadOnlyList<TriggerId> TriggerRefs;
        public AbilityDefinition(AbilityId id, string name, TargetingDefinition targeting, ClockId clock, float cooldown, GameplayPhaseMask allowedPhases, EffectId[] effects, ModifierDefinition[] modifiers, ExecutorId executor, ConsumerId consumer, ActivationPolicy activation = ActivationPolicy.Instant, int manaCost = 0, ExecutionId[] executions = null, CostDefinition[] costs = null, TagId[] requiredTags = null, TagId[] blockedTags = null, TriggerId[] triggerRefs = null) { Id = id; Name = name; Targeting = targeting; Clock = clock; Cooldown = cooldown; AllowedPhases = allowedPhases; Effects = ImmutableViews.List(effects); Modifiers = ImmutableViews.List(modifiers); Executor = executor; Consumer = consumer; Activation = activation; Executions = ImmutableViews.List(executions); Costs = ImmutableViews.List(costs); RequiredTags = ImmutableViews.List(requiredTags); BlockedTags = ImmutableViews.List(blockedTags); TriggerRefs = ImmutableViews.List(triggerRefs); }
    }
    public readonly struct GameplayEffectDefinition
    {
        public readonly EffectId Id;
        public readonly EffectType Type;
        public readonly IReadOnlyList<ModifierDefinition> Modifiers;
        public readonly float Duration;
        public readonly ClockId Clock;
        public readonly StackingBehavior Stacking;
        public readonly int MaxStacks;
        public readonly RefreshPolicy Refresh;
        public RefreshPolicy RefreshPolicy => Refresh;
        public readonly EffectPayloadKind Payload;
        public readonly TagId Tag;
        public readonly PeriodicSpec? Periodic;
        public readonly IReadOnlyList<ExecutionId> Executions;
        public readonly SourceDeathPolicy SourceDeath;
        public readonly IReadOnlyList<TagId> GrantedTags, BlockedTags;
        public float Period { get { return Periodic.HasValue ? Periodic.Value.Period : 0f; } }
        public bool RefreshDuration { get { return Refresh != RefreshPolicy.None; } }
        public GameplayEffectDefinition(EffectId id, EffectType type, ModifierDefinition[] modifiers, float duration, float period, ClockId clock, StackingBehavior stacking, int maxStacks, RefreshPolicy refresh, SourceDeathPolicy sourceDeath, EffectPayloadKind payload, TagId tag, ExecutionId[] executions, TagId[] grantedTags = null, TagId[] blockedTags = null) { Id = id; Type = type; Duration = duration; Clock = clock; Stacking = stacking; MaxStacks = maxStacks; Refresh = refresh; Payload = payload; Tag = tag; Periodic = period > 0f ? new PeriodicSpec(period, FirstTickPolicy.NextInterval, CatchUpPolicy.CatchUpAll, executions == null || executions.Length == 0 ? default(ExecutionId) : executions[0]) : (PeriodicSpec?)null; Executions = ImmutableViews.List(executions); SourceDeath = sourceDeath; GrantedTags = ImmutableViews.List(grantedTags); BlockedTags = ImmutableViews.List(blockedTags); Modifiers = ImmutableViews.List(modifiers); }
    }
    public struct ActiveGameplayEffect
    {
        public EffectHandle Handle;
        public EffectId DefinitionId;
        public EntityHandle Source, Target;
        public float RemainingTime, TickAccumulator;
        public int StackCount;
        public ActiveGameplayEffect(EffectHandle handle, EffectId definitionId, EntityHandle source, EntityHandle target, float remainingTime) { Handle = handle; DefinitionId = definitionId; Source = source; Target = target; RemainingTime = remainingTime; TickAccumulator = 0f; StackCount = 1; }
    }
}
