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
    public enum EffectPayloadKind { Damage, Heal, Shield, Resurrect, Resource, CrowdControl, Slow, GameplayEvent, WorldAction, Status, Dispel, Freeze, Telegraph }
    public enum ExecutionOperation { Default, ApplyDamage, ApplyHeal, ApplyShield, Resurrect, RestoreSnapshot, ApplyCrowdControl, ApplySlow, SummonEnemy, PrepareStealth, ApplyEnemyBuff, ApplyTowerSilence, RemoveDispellableEffects, ApplyFreeze, QueueTelegraph }
    public enum RefreshPolicy { None, Duration, StacksAndDuration }
    public enum SourceDeathPolicy { Persist, Remove }
    [Flags]
    public enum SkillSemanticField : ulong
    {
        None = 0,
        Cooldown = 1UL << 0,
        AttackRange = 1UL << 1,
        AreaWidth = 1UL << 2,
        AreaHeight = 1UL << 3,
        AreaRadius = 1UL << 4,
        ConeAngleDegrees = 1UL << 5,
        ManaCost = 1UL << 6,
        DamageMultiplier = 1UL << 7,
        HealPercent = 1UL << 8,
        ShieldAmount = 1UL << 9,
        ShieldDuration = 1UL << 10,
        SlowAmount = 1UL << 11,
        SlowDuration = 1UL << 12,
        AoeStunDuration = 1UL << 13,
        AoeRootDuration = 1UL << 14,
        AoeKnockbackForce = 1UL << 15,
        FreezeDuration = 1UL << 16,
        FreezeChance = 1UL << 17,
        DotDuration = 1UL << 18,
        DotTickInterval = 1UL << 19,
        Modifiers = 1UL << 20,
        AreaShape = 1UL << 21
    }
    public enum DurationPolicy { Instant, Duration, Infinite }
    public enum ActivationPolicy { Instant, InputPressed, Passive }
    public enum MagnitudeSource { Constant, Attribute, Multiplier }
    public enum DamageAmountStage { Raw, PostCrit, LegacyMultiplier, PostMitigation }
    [Flags]
    public enum DamageFlags { None = 0, IgnoreArmor = 1, IgnoreResistance = 2, IgnoreShield = 4, IgnoreInvulnerability = 8, Execute = 16, Reflect = 32, Transfer = 64 }
    public enum DamageCommitBoundary { EarlyResolve, GameplayResolve }
    public enum SnapshotPolicy { CaptureOnApply, ReevaluateOnRead }
    public enum TriggerScope { PerSource, PerTarget, PerSourceTarget, PerPlayer }
    public enum TriggerMode { Once, EveryN }
    public enum TriggerResetPolicy { None, Explicit }
    public enum EffectTargetPolicy { Source, Target }
    public enum FirstTickPolicy { NextInterval, Immediate }
    public enum CatchUpPolicy { CatchUpAll, OnePerFrame, SkipMissed }
    public enum RelationFilter { Any, Enemies, Allies, Self }
    public enum MaxTargetsPolicy { Derived, Unlimited, Fixed }
    public readonly struct RuntimeCatalogSpec
    {
        public readonly float DamageBonusPerKill;
        public readonly float MaxMultiplier;
        public readonly int TriggerThreshold;
        public RuntimeCatalogSpec(float damageBonusPerKill, float maxMultiplier, int triggerThreshold)
        { DamageBonusPerKill = damageBonusPerKill; MaxMultiplier = maxMultiplier; TriggerThreshold = triggerThreshold; }
    }
    [Flags] public enum GameplayPhaseMask { None = 0, Build = 1, Wave = 2, Intermission = 4 }
    public readonly struct ExecutorId : IEquatable<ExecutorId> { public readonly int Value; public ExecutorId(int value) { Value = value; } public bool Equals(ExecutorId other) => Value == other.Value; public override bool Equals(object obj) => obj is ExecutorId other && Equals(other); public override int GetHashCode() => Value; }
    public readonly struct ConsumerId : IEquatable<ConsumerId> { public readonly int Value; public ConsumerId(int value) { Value = value; } public bool Equals(ConsumerId other) => Value == other.Value; public override bool Equals(object obj) => obj is ConsumerId other && Equals(other); public override int GetHashCode() => Value; }
    public enum EffectTag { None, Normal, Burn, Fire, Freeze, Lightning, Explosion, Plasma, Poison }
    public readonly struct ExecutionId : IEquatable<ExecutionId> { public readonly int Value; public ExecutionId(int value) { Value = value; } public bool Equals(ExecutionId other) => Value == other.Value; public override bool Equals(object obj) => obj is ExecutionId other && Equals(other); public override int GetHashCode() => Value; }
    public readonly struct TargetingId : IEquatable<TargetingId> { public readonly int Value; public TargetingId(int value) { Value = value; } public bool Equals(TargetingId other) => Value == other.Value; public override bool Equals(object obj) => obj is TargetingId other && Equals(other); public override int GetHashCode() => Value; }
    public readonly struct TriggerId : IEquatable<TriggerId> { public readonly int Value; public TriggerId(int value) { Value = value; } public bool Equals(TriggerId other) => Value == other.Value; public override bool Equals(object obj) => obj is TriggerId other && Equals(other); public override int GetHashCode() => Value; }
    public readonly struct PeriodicSpec { public readonly float Period; public readonly FirstTickPolicy FirstTick; public readonly CatchUpPolicy CatchUp; public readonly ExecutionId PayloadExecution; public readonly DamageType? Damage; public readonly ElementType? Element; public readonly EffectPayloadKind Payload; public readonly MagnitudeSource MagnitudeSource; public readonly float Magnitude; public readonly AttributeKey Resource; public readonly GameplayEventType EventType;
        public PeriodicSpec(float period, ExecutionId payloadExecution, EffectPayloadKind payload, MagnitudeSource magnitudeSource, FirstTickPolicy firstTick, CatchUpPolicy catchUp, DamageType? damage = null, ElementType? element = null, float magnitude = 0f, AttributeKey resource = default(AttributeKey), GameplayEventType eventType = GameplayEventType.EffectApplied) { Period = period; FirstTick = firstTick; CatchUp = catchUp; PayloadExecution = payloadExecution; Payload = payload; MagnitudeSource = magnitudeSource; Damage = damage; Element = element; Magnitude = magnitude; Resource = resource; EventType = eventType; }
        public PeriodicSpec(float period, FirstTickPolicy firstTick, CatchUpPolicy catchUp, ExecutionId payloadExecution, DamageType? damage = null, ElementType? element = null, float magnitude = 0f, AttributeKey resource = default(AttributeKey), GameplayEventType eventType = GameplayEventType.EffectApplied) : this(period, payloadExecution, EffectPayloadKind.Damage, MagnitudeSource.Constant, firstTick, catchUp, damage, element, magnitude, resource, eventType) { }
        public PeriodicSpec(float period, FirstTickPolicy firstTick, CatchUpPolicy catchUp, ExecutionId payloadExecution, DamageType? damage, ElementType? element) : this(period, firstTick, catchUp, payloadExecution, damage, element, 0f, default(AttributeKey), GameplayEventType.EffectApplied) { } }
    public readonly struct CostDefinition { public readonly AttributeKey Resource; public readonly float Amount; public CostDefinition(AttributeKey resource, float amount) { Resource = resource; Amount = amount; } }
    public readonly struct ExecutionDefinition { public readonly ExecutionId Id; public readonly EffectPayloadKind Payload; public readonly float Magnitude; public readonly float Duration; public readonly MagnitudeSource MagnitudeSource; public readonly DamageAmountStage Stage; public readonly TagId Tag; public readonly ExecutionOperation Operation; public readonly float Probability; public readonly int Parameter; public readonly StackingBehavior SemanticStacking; public readonly int SemanticMaxStacks; public ExecutionDefinition(ExecutionId id, EffectPayloadKind payload, float magnitude, TagId tag, MagnitudeSource source = MagnitudeSource.Constant, DamageAmountStage stage = DamageAmountStage.Raw, float duration = 0f, ExecutionOperation operation = ExecutionOperation.Default, float probability = 1f, int parameter = 0, StackingBehavior semanticStacking = StackingBehavior.None, int semanticMaxStacks = 0) { Id = id; Payload = payload; Magnitude = magnitude; Duration = duration; Tag = tag; MagnitudeSource = source; Stage = stage; Operation = operation; Probability = probability; Parameter = parameter; SemanticStacking = semanticStacking; SemanticMaxStacks = semanticMaxStacks; } }
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
    public readonly struct SkillModifierSemantic
    {
        public readonly string Name;
        public readonly string Type;
        public readonly float Value;
        public readonly float Duration;
        public readonly StackingBehavior Stacking;
        public readonly int MaxStacks;
        public readonly TagId Tag;
        public readonly EffectPayloadKind Payload;
        public readonly ExecutionOperation Operation;
        public readonly float NormalizedMagnitude;
        public readonly float Probability;
        public readonly TargetingShape Targeting;
        public SkillModifierSemantic(string name, string type, float value, float duration,
            StackingBehavior stacking, int maxStacks, TagId tag, EffectPayloadKind payload,
            ExecutionOperation operation, float normalizedMagnitude, float probability,
            TargetingShape targeting)
        {
            Name = name; Type = type; Value = value; Duration = duration; Stacking = stacking;
            MaxStacks = maxStacks; Tag = tag; Payload = payload; Operation = operation;
            NormalizedMagnitude = normalizedMagnitude; Probability = probability; Targeting = targeting;
        }
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
        public readonly TriggerScope Scope;
        public readonly TriggerMode Mode;
        public readonly int Threshold;
        public readonly bool PreserveRemainder;
        public readonly int EffectStackDelta;
        public readonly EffectTargetPolicy EffectTarget;
        public readonly TriggerResetPolicy ResetPolicy;
        public TriggerDefinition(TriggerId id, GameplayEventType eventType, EffectId effect, ConsumerId consumer, TagId[] filterTags = null, TagId effectTag = default(TagId), TriggerScope scope = TriggerScope.PerSource, int threshold = 1, TriggerMode mode = TriggerMode.Once, bool preserveRemainder = false, int effectStackDelta = 1, EffectTargetPolicy effectTarget = EffectTargetPolicy.Source, TriggerResetPolicy resetPolicy = TriggerResetPolicy.None) { Id = id; EventType = eventType; Effect = effect; Consumer = consumer; FilterTags = ImmutableViews.List(filterTags); EffectTag = effectTag; Scope = scope; Threshold = threshold; Mode = mode; PreserveRemainder = preserveRemainder; EffectStackDelta = effectStackDelta; EffectTarget = effectTarget; ResetPolicy = resetPolicy; }
        public TriggerDefinition(TriggerId id, GameplayEventType eventType, EffectId effect, ConsumerId consumer, TagId[] filterTags, TagId effectTag) : this(id, eventType, effect, consumer, filterTags, effectTag, TriggerScope.PerSource, 1, TriggerMode.Once, false, 1, EffectTargetPolicy.Source, TriggerResetPolicy.None) { }
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
        internal readonly SkillSemanticField SemanticFields;
        public readonly IReadOnlyList<SkillModifierSemantic> SourceModifiers;
        internal int SourceModifierCount => SourceModifiers.Count;
        public AbilityDefinition(AbilityId id, string name, TargetingDefinition targeting, ClockId clock, float cooldown, GameplayPhaseMask allowedPhases, EffectId[] effects, ModifierDefinition[] modifiers, ExecutorId executor, ConsumerId consumer, ActivationPolicy activation = ActivationPolicy.Instant, int manaCost = 0, ExecutionId[] executions = null, CostDefinition[] costs = null, TagId[] requiredTags = null, TagId[] blockedTags = null, TriggerId[] triggerRefs = null, SkillSemanticField semanticFields = SkillSemanticField.None, SkillModifierSemantic[] sourceModifiers = null) { Id = id; Name = name; Targeting = targeting; Clock = clock; Cooldown = cooldown; AllowedPhases = allowedPhases; Effects = ImmutableViews.List(effects); Modifiers = ImmutableViews.List(modifiers); Executor = executor; Consumer = consumer; Activation = activation; Executions = ImmutableViews.List(executions); Costs = ImmutableViews.List(costs); RequiredTags = ImmutableViews.List(requiredTags); BlockedTags = ImmutableViews.List(blockedTags); TriggerRefs = ImmutableViews.List(triggerRefs); SemanticFields = semanticFields; SourceModifiers = ImmutableViews.List(sourceModifiers); }
    }
    public readonly struct GameplayEffectDefinition
    {
        public readonly EffectId Id;
        public readonly EffectType Type;
        public readonly IReadOnlyList<ModifierDefinition> Modifiers;
        public readonly float Duration;
        public readonly DurationPolicy DurationPolicy;
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
        public readonly TagId StackKey;
        public float Period { get { return Periodic.HasValue ? Periodic.Value.Period : 0f; } }
        public bool RefreshDuration { get { return Refresh != RefreshPolicy.None; } }
        public GameplayEffectDefinition(EffectId id, EffectType type, ModifierDefinition[] modifiers, float duration, ClockId clock, StackingBehavior stacking, int maxStacks, RefreshPolicy refresh, SourceDeathPolicy sourceDeath, EffectPayloadKind payload, TagId tag, PeriodicSpec periodic, ExecutionId[] executions, TagId[] grantedTags = null, TagId[] blockedTags = null, TagId stackKey = default(TagId)) { Id = id; Type = type; Duration = duration; DurationPolicy = type == EffectType.Periodic ? DurationPolicy.Duration : type == EffectType.Instant ? DurationPolicy.Instant : duration == 0f ? DurationPolicy.Infinite : DurationPolicy.Duration; Clock = clock; Stacking = stacking; MaxStacks = maxStacks; Refresh = refresh; Payload = payload; Tag = tag; StackKey = stackKey; Periodic = periodic; Executions = ImmutableViews.List(executions); SourceDeath = sourceDeath; GrantedTags = ImmutableViews.List(grantedTags); BlockedTags = ImmutableViews.List(blockedTags); Modifiers = ImmutableViews.List(modifiers); }
        public GameplayEffectDefinition(EffectId id, EffectType type, ModifierDefinition[] modifiers, float duration, DurationPolicy durationPolicy, ClockId clock, StackingBehavior stacking, int maxStacks, RefreshPolicy refresh, SourceDeathPolicy sourceDeath, EffectPayloadKind payload, TagId tag, PeriodicSpec periodic, ExecutionId[] executions, TagId[] grantedTags = null, TagId[] blockedTags = null, TagId stackKey = default(TagId)) : this(id, type, modifiers, duration, clock, stacking, maxStacks, refresh, sourceDeath, payload, tag, periodic, executions, grantedTags, blockedTags, stackKey) { DurationPolicy = durationPolicy; }
        public GameplayEffectDefinition(EffectId id, EffectType type, ModifierDefinition[] modifiers, float duration, float period, ClockId clock, StackingBehavior stacking, int maxStacks, RefreshPolicy refresh, SourceDeathPolicy sourceDeath, EffectPayloadKind payload, TagId tag, ExecutionId[] executions, TagId[] grantedTags = null, TagId[] blockedTags = null, TagId stackKey = default(TagId), float periodicMagnitude = 0f) { Id = id; Type = type; Duration = duration; DurationPolicy = type == EffectType.Periodic ? DurationPolicy.Duration : type == EffectType.Instant ? DurationPolicy.Instant : duration == 0f ? DurationPolicy.Infinite : DurationPolicy.Duration; Clock = clock; Stacking = stacking; MaxStacks = maxStacks; Refresh = refresh; Payload = payload; Tag = tag; StackKey = stackKey; Periodic = period > 0f ? new PeriodicSpec(period, FirstTickPolicy.NextInterval, CatchUpPolicy.CatchUpAll, executions == null || executions.Length == 0 ? default(ExecutionId) : executions[0], magnitude: periodicMagnitude) : (PeriodicSpec?)null; Executions = ImmutableViews.List(executions); SourceDeath = sourceDeath; GrantedTags = ImmutableViews.List(grantedTags); BlockedTags = ImmutableViews.List(blockedTags); Modifiers = ImmutableViews.List(modifiers); }
        public GameplayEffectDefinition(EffectId id, EffectType type, ModifierDefinition[] modifiers, float duration, float period, ClockId clock, StackingBehavior stacking, int maxStacks, RefreshPolicy refresh, SourceDeathPolicy sourceDeath, EffectPayloadKind payload, TagId tag, ExecutionId[] executions) : this(id, type, modifiers, duration, period, clock, stacking, maxStacks, refresh, sourceDeath, payload, tag, executions, null, null, default(TagId), 0f) { }
    }
    public struct ActiveGameplayEffect
    {
        public EffectHandle Handle;
        public EffectId DefinitionId;
        public EntityHandle Source, Target;
        public float RemainingTime, TickAccumulator;
        public float CapturedMagnitude;
        public int TicksRemaining, StackCount;
        public ClockId Clock;
        public FirstTickPolicy FirstTick;
        public CatchUpPolicy CatchUp;
        public SourceDeathPolicy SourceDeath;
        public bool RuntimeOwned;
        public int TicksProcessed;
        public TagId Tag;
        public int OwnerPlayerId;
        public long ApplicationSequence, ProvenanceId;
        public bool FirstTickPending;
        public ActiveGameplayEffect(EffectHandle handle, EffectId definitionId, EntityHandle source, EntityHandle target, float remainingTime, int ticksRemaining = 0, float capturedMagnitude = 0f, ClockId clock = ClockId.Combat, FirstTickPolicy firstTick = FirstTickPolicy.NextInterval, CatchUpPolicy catchUp = CatchUpPolicy.CatchUpAll, SourceDeathPolicy sourceDeath = SourceDeathPolicy.Persist, int ownerPlayerId = -1, long applicationSequence = 0L, long provenanceId = 0L, TagId tag = default(TagId)) { Handle = handle; DefinitionId = definitionId; Source = source; Target = target; RemainingTime = remainingTime; TickAccumulator = 0f; CapturedMagnitude = capturedMagnitude; TicksRemaining = ticksRemaining; StackCount = 1; Clock = clock; FirstTick = firstTick; CatchUp = catchUp; SourceDeath = sourceDeath; OwnerPlayerId = ownerPlayerId; ApplicationSequence = applicationSequence; ProvenanceId = provenanceId; RuntimeOwned = false; TicksProcessed = 0; Tag = tag; FirstTickPending = true; }
        public ActiveGameplayEffect(EffectHandle handle, EffectId definitionId, EntityHandle source, EntityHandle target, float remainingTime) : this(handle, definitionId, source, target, remainingTime, 0, 0f, ClockId.Combat, FirstTickPolicy.NextInterval, CatchUpPolicy.CatchUpAll, SourceDeathPolicy.Persist, -1, 0L, 0L, default(TagId)) { }
        public ActiveGameplayEffect(EffectHandle handle, EffectId definitionId, EntityHandle source, EntityHandle target, float remainingTime, int ticksRemaining, float capturedMagnitude, ClockId clock, FirstTickPolicy firstTick, CatchUpPolicy catchUp, SourceDeathPolicy sourceDeath) : this(handle, definitionId, source, target, remainingTime, ticksRemaining, capturedMagnitude, clock, firstTick, catchUp, sourceDeath, -1, 0L, 0L, default(TagId)) { }
    }
}
