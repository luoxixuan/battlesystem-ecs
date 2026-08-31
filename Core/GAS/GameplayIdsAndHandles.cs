using System;
using BattleSystemECS.Components;

namespace BattleSystemECS.Core.GAS
{
    public sealed class CatalogValidationException : Exception
    {
        public CatalogValidationException(string message) : base(message) { }
    }

    public readonly struct AbilityId : IEquatable<AbilityId>
    {
        public readonly int Value;
        public AbilityId(int value) { Value = value; }
        public bool Equals(AbilityId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is AbilityId other && Equals(other);
        public override int GetHashCode() => Value;
        public static implicit operator int(AbilityId id) => id.Value;
    }

    public readonly struct EffectId : IEquatable<EffectId>
    {
        public readonly int Value;
        public EffectId(int value) { Value = value; }
        public bool Equals(EffectId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is EffectId other && Equals(other);
        public override int GetHashCode() => Value;
        public static implicit operator int(EffectId id) => id.Value;
    }

    public readonly struct AttributeKey : IEquatable<AttributeKey>
    {
        public readonly int Value;
        public AttributeKey(int value) { Value = value; }
        public bool Equals(AttributeKey other) => Value == other.Value;
        public override bool Equals(object obj) => obj is AttributeKey other && Equals(other);
        public override int GetHashCode() => Value;
    }

    public readonly struct TagId : IEquatable<TagId>
    {
        public readonly int Value;
        public TagId(int value) { Value = value; }
        public bool Equals(TagId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is TagId other && Equals(other);
        public override int GetHashCode() => Value;
    }

    public enum ClockId { Invalid = -1, Build = 0, Enemy = 1, Combat = 2, RealTime = 3, Global = 4 }
    public enum HandleResolveFailure { None, InvalidIndex, StaleGeneration, Inactive, Capacity }

    public readonly struct EntityHandle : IEquatable<EntityHandle>
    {
        public readonly int Index;
        public readonly int Generation;
        public EntityHandle(int index, int generation) { Index = index; Generation = generation; }
        public bool IsValid => Index >= 0 && Generation > 0;
        public bool Equals(EntityHandle other) => Index == other.Index && Generation == other.Generation;
        public override bool Equals(object obj) => obj is EntityHandle other && Equals(other);
        public override int GetHashCode() => (Index * 397) ^ Generation;
    }

    public readonly struct EffectHandle : IEquatable<EffectHandle>
    {
        public readonly int Index;
        public readonly int Generation;
        public EffectHandle(int index, int generation) { Index = index; Generation = generation; }
        public bool IsValid => Index >= 0 && Generation > 0;
        public bool Equals(EffectHandle other) => Index == other.Index && Generation == other.Generation;
        public override bool Equals(object obj) => obj is EffectHandle other && Equals(other);
        public override int GetHashCode() => (Index * 397) ^ Generation;
    }

    public readonly struct ExecutionContext
    {
        public readonly EntityHandle Source, Target;
        public readonly AbilityId Ability;
        public readonly EffectId Effect;
        public readonly ClockId Clock;
        public readonly long Sequence;
        public readonly int OwnerPlayerId;
        public readonly float Snapshot;
        public readonly long ProvenanceId;
        public readonly int ProvenanceDepth;
        public ExecutionContext(EntityHandle source, EntityHandle target, AbilityId ability, EffectId effect, ClockId clock, long sequence, int ownerPlayerId = -1, float snapshot = 0f, long provenanceId = 0L, int provenanceDepth = 0)
        { Source = source; Target = target; Ability = ability; Effect = effect; Clock = clock; Sequence = sequence; OwnerPlayerId = ownerPlayerId; Snapshot = snapshot; ProvenanceId = provenanceId; ProvenanceDepth = provenanceDepth; }
    }

    public readonly struct AbilityRequest { public readonly EntityHandle Source, Target; public readonly AbilityId Ability; public readonly long Sequence; public AbilityRequest(EntityHandle source, AbilityId ability, EntityHandle target, long sequence) { Source = source; Ability = ability; Target = target; Sequence = sequence; } }
    public readonly struct EffectRequest { public readonly EntityHandle Source, Target; public readonly EffectId Effect; public readonly EffectHandle ActiveEffect; public readonly int StackDelta; public readonly ClockId Clock; public readonly ExecutionContext Context; public EffectRequest(EntityHandle source, EntityHandle target, EffectId effect, int stackDelta, ClockId clock, ExecutionContext context) { Source = source; Target = target; Effect = effect; ActiveEffect = default(EffectHandle); StackDelta = stackDelta; Clock = clock; Context = context; } public EffectRequest(EntityHandle source, EntityHandle target, EffectId effect, EffectHandle activeEffect, int stackDelta, ClockId clock, ExecutionContext context) : this(source, target, effect, stackDelta, clock, context) { ActiveEffect = activeEffect; } }
    public readonly struct DamageRequest {
        public readonly EntityHandle Source, Target; public readonly float RawAmount; public readonly DamageType DamageType; public readonly ElementType ElementType; public readonly DamageFlags Flags; public readonly DamageAmountStage AmountStage; public readonly DamageCommitBoundary CommitBoundary; public readonly AbilityId Ability; public readonly EffectId Effect; public readonly int OwnerPlayerId; public readonly long Sequence, ParentSequence, ProvenanceId; public readonly int ProvenanceDepth;
        public readonly ExecutionContext Context; internal readonly bool AllowMissingSource;
        public DamageRequest(EntityHandle source, EntityHandle target, float amount, DamageType type, long sequence, AbilityId ability = default(AbilityId), EffectId effect = default(EffectId), int ownerPlayerId = -1) : this(source, target, amount, type, ElementType.None, DamageFlags.None, DamageAmountStage.Raw, DamageCommitBoundary.GameplayResolve, sequence, 0L, ability, effect, ownerPlayerId, default(ExecutionContext), 0L, 0, false) { }
        public DamageRequest(EntityHandle source, EntityHandle target, float amount, DamageType type, long sequence, AbilityId ability, EffectId effect) : this(source, target, amount, type, sequence, ability, effect, -1) { }
        public DamageRequest(EntityHandle source, EntityHandle target, float amount, DamageType type, ElementType element, DamageFlags flags, DamageAmountStage stage, DamageCommitBoundary boundary, long sequence, long parentSequence = 0L, AbilityId ability = default(AbilityId), EffectId effect = default(EffectId), int ownerPlayerId = -1, ExecutionContext context = default(ExecutionContext), long provenanceId = 0L, int provenanceDepth = 0) : this(source, target, amount, type, element, flags, stage, boundary, sequence, parentSequence, ability, effect, ownerPlayerId, context, provenanceId, provenanceDepth, false) { }
        private DamageRequest(EntityHandle source, EntityHandle target, float amount, DamageType type, ElementType element, DamageFlags flags, DamageAmountStage stage, DamageCommitBoundary boundary, long sequence, long parentSequence, AbilityId ability, EffectId effect, int ownerPlayerId, ExecutionContext context, long provenanceId, int provenanceDepth, bool allowMissingSource) { Source = source; Target = target; RawAmount = amount; DamageType = type; ElementType = element; Flags = flags; AmountStage = stage; CommitBoundary = boundary; Sequence = sequence; ParentSequence = parentSequence; Ability = ability; Effect = effect; OwnerPlayerId = ownerPlayerId; Context = context; ProvenanceId = provenanceId; ProvenanceDepth = provenanceDepth; AllowMissingSource = allowMissingSource; }
        internal static DamageRequest ForPersistentEffect(EntityHandle source, EntityHandle target, float amount, DamageType type, ElementType element, DamageFlags flags, DamageAmountStage stage, DamageCommitBoundary boundary, long sequence, EffectId effect, int ownerPlayerId, ExecutionContext context, long provenanceId, int provenanceDepth) => new DamageRequest(source, target, amount, type, element, flags, stage, boundary, sequence, 0L, default(AbilityId), effect, ownerPlayerId, context, provenanceId, provenanceDepth, true);
    }
    public readonly struct HealRequest { public readonly EntityHandle Source, Target; public readonly float RawAmount; public readonly long Sequence; public readonly int OwnerPlayerId; internal readonly bool AllowMissingSource; public HealRequest(EntityHandle source, EntityHandle target, float amount, long sequence, int ownerPlayerId = -1) : this(source, target, amount, sequence, ownerPlayerId, false) { } public HealRequest(EntityHandle source, EntityHandle target, float amount, long sequence) : this(source, target, amount, sequence, -1, false) { } private HealRequest(EntityHandle source, EntityHandle target, float amount, long sequence, int ownerPlayerId, bool allowMissingSource) { Source = source; Target = target; RawAmount = amount; Sequence = sequence; OwnerPlayerId = ownerPlayerId; AllowMissingSource = allowMissingSource; } internal static HealRequest ForPersistentEffect(EntityHandle source, EntityHandle target, float amount, long sequence, int ownerPlayerId) => new HealRequest(source, target, amount, sequence, ownerPlayerId, true); }
    public readonly struct ShieldRequest { public readonly EntityHandle Source, Target; public readonly float Amount, Duration; public readonly ClockId Clock; public readonly long Sequence; public ShieldRequest(EntityHandle source, EntityHandle target, float amount, float duration, ClockId clock, long sequence) { Source = source; Target = target; Amount = amount; Duration = duration; Clock = clock; Sequence = sequence; } }
    public enum ResourceOperation { Add, Set }
    public readonly struct ResourceRequest { public readonly EntityHandle Source, Target; public readonly AttributeKey Resource; public readonly float Delta; public readonly ResourceOperation Operation; public readonly int CauseId; public readonly long Sequence, ProvenanceId; public readonly DamageCommitBoundary CommitBoundary; public readonly int OwnerPlayerId; internal readonly bool AllowMissingSource; public ResourceRequest(EntityHandle source, EntityHandle target, AttributeKey resource, float delta, long sequence, int ownerPlayerId = -1) : this(source, target, resource, delta, ResourceOperation.Add, 0, sequence, DamageCommitBoundary.GameplayResolve, ownerPlayerId, 0L, false) { } public ResourceRequest(EntityHandle source, EntityHandle target, AttributeKey resource, float delta, ResourceOperation operation, int causeId, long sequence, int ownerPlayerId = -1) : this(source, target, resource, delta, operation, causeId, sequence, DamageCommitBoundary.GameplayResolve, ownerPlayerId, 0L, false) { } public ResourceRequest(EntityHandle source, EntityHandle target, AttributeKey resource, float delta, ResourceOperation operation, int causeId, long sequence, DamageCommitBoundary boundary, int ownerPlayerId = -1) : this(source, target, resource, delta, operation, causeId, sequence, boundary, ownerPlayerId, 0L, false) { } private ResourceRequest(EntityHandle source, EntityHandle target, AttributeKey resource, float delta, ResourceOperation operation, int causeId, long sequence, DamageCommitBoundary boundary, int ownerPlayerId, long provenanceId, bool allowMissingSource) { Source = source; Target = target; Resource = resource; Delta = delta; Operation = operation; CauseId = causeId; Sequence = sequence; CommitBoundary = boundary; OwnerPlayerId = ownerPlayerId; ProvenanceId = provenanceId; AllowMissingSource = allowMissingSource; } internal static ResourceRequest ForPersistentEffect(EntityHandle source, EntityHandle target, AttributeKey resource, float delta, long sequence, int ownerPlayerId, long provenanceId = 0L) => new ResourceRequest(source, target, resource, delta, ResourceOperation.Add, 0, sequence, DamageCommitBoundary.GameplayResolve, ownerPlayerId, provenanceId, true); }

    public enum GameplayEventType { HitConfirmed, DamageApplied, AbilityActivated, AbilityRejected, EffectApplied, EffectRejected, EffectExpired, EffectRemoved, HitMissed, DamageBlocked, HealApplied, ShieldChanged, ResourceChanged, DeathQueued, KillConfirmed, GameplayLoopAborted }
    public readonly struct GameplayEvent { public readonly GameplayEventType Type; public readonly EntityHandle Source, Target; public readonly EffectHandle Effect; public readonly EffectId EffectDefinition; public readonly DamageFlags Flags; public readonly long ParentSequence, ProvenanceId; public readonly int ProvenanceDepth; public readonly int ProducerIndex; public readonly long Sequence; public readonly int Reason; public readonly TagId Tag; public readonly int OwnerPlayerId; public GameplayEvent(GameplayEventType type, EntityHandle source, EntityHandle target, long sequence, int reason = 0, int ownerPlayerId = -1) : this(type, source, target, default(EffectHandle), default(EffectId), sequence, 0L, reason, 0, DamageFlags.None, 0L, 0, default(TagId), ownerPlayerId) { } public GameplayEvent(GameplayEventType type, EntityHandle source, EntityHandle target, long sequence, int reason) : this(type, source, target, sequence, reason, -1) { } public GameplayEvent(GameplayEventType type, EntityHandle source, EntityHandle target, EffectHandle effect, long sequence, int reason = 0, int producerIndex = 0) : this(type, source, target, effect, default(EffectId), sequence, 0L, reason, producerIndex, DamageFlags.None, 0L, 0, default(TagId), -1) { } public GameplayEvent(GameplayEventType type, EntityHandle source, EntityHandle target, EffectHandle effect, EffectId effectDefinition, long sequence, long parentSequence = 0L, int reason = 0, int producerIndex = 0, DamageFlags flags = DamageFlags.None, long provenanceId = 0L, int provenanceDepth = 0, TagId tag = default(TagId), int ownerPlayerId = -1) { Type = type; Source = source; Target = target; Effect = effect; EffectDefinition = effectDefinition; Flags = flags; ParentSequence = parentSequence; ProvenanceId = provenanceId; ProvenanceDepth = provenanceDepth; ProducerIndex = producerIndex; Sequence = sequence; Reason = reason; Tag = tag; OwnerPlayerId = ownerPlayerId; } public GameplayEvent(GameplayEventType type, EntityHandle source, EntityHandle target, EffectHandle effect, EffectId effectDefinition, long sequence, long parentSequence, int reason, int producerIndex, DamageFlags flags, long provenanceId, int provenanceDepth) : this(type, source, target, effect, effectDefinition, sequence, parentSequence, reason, producerIndex, flags, provenanceId, provenanceDepth, default(TagId), -1) { } public GameplayEvent(GameplayEventType type, EntityHandle source, EntityHandle target, EffectHandle effect, EffectId effectDefinition, long sequence, long parentSequence, int reason, int producerIndex, DamageFlags flags, long provenanceId, int provenanceDepth, TagId tag) : this(type, source, target, effect, effectDefinition, sequence, parentSequence, reason, producerIndex, flags, provenanceId, provenanceDepth, tag, -1) { } public GameplayEvent(GameplayEventType type, EntityHandle source, EntityHandle target, EffectHandle effect, EffectId effectDefinition, DamageFlags flags, long sequence, long parentSequence, int reason, int producerIndex, long provenanceId, int provenanceDepth) : this(type, source, target, effect, effectDefinition, flags, sequence, parentSequence, reason, producerIndex, provenanceId, provenanceDepth, default(TagId), -1) { } public GameplayEvent(GameplayEventType type, EntityHandle source, EntityHandle target, EffectHandle effect, EffectId effectDefinition, DamageFlags flags, long sequence, long parentSequence = 0L, int reason = 0, int producerIndex = 0, long provenanceId = 0L, int provenanceDepth = 0, TagId tag = default(TagId), int ownerPlayerId = -1) : this(type, source, target, effect, effectDefinition, sequence, parentSequence, reason, producerIndex, flags, provenanceId, provenanceDepth, tag, ownerPlayerId) { } public GameplayEvent(GameplayEventType type, EntityHandle source, EntityHandle target, EffectId effectDefinition, DamageFlags flags, long sequence, long parentSequence, int reason, int producerIndex, long provenanceId, int provenanceDepth) : this(type, source, target, default(EffectHandle), effectDefinition, flags, sequence, parentSequence, reason, producerIndex, provenanceId, provenanceDepth) { } public GameplayEvent(GameplayEventType type, EntityHandle source, EntityHandle target, EffectId effectDefinition, DamageFlags flags, long sequence, long parentSequence = 0L, int reason = 0, int producerIndex = 0, long provenanceId = 0L, int provenanceDepth = 0, int ownerPlayerId = -1) : this(type, source, target, default(EffectHandle), effectDefinition, sequence, parentSequence, reason, producerIndex, flags, provenanceId, provenanceDepth, default(TagId), ownerPlayerId) { } }

    public static class GameplayEventOrdering
    {
        public static int Compare(GameplayEvent left, GameplayEvent right)
        {
            int c = left.Sequence.CompareTo(right.Sequence); if (c != 0) return c;
            c = left.Type.CompareTo(right.Type); if (c != 0) return c;
            c = left.Target.Index.CompareTo(right.Target.Index); if (c != 0) return c;
            c = left.Target.Generation.CompareTo(right.Target.Generation); if (c != 0) return c;
            c = left.Source.Index.CompareTo(right.Source.Index); if (c != 0) return c;
            c = left.Source.Generation.CompareTo(right.Source.Generation); if (c != 0) return c;
            c = left.Effect.Index.CompareTo(right.Effect.Index); if (c != 0) return c;
            c = left.Effect.Generation.CompareTo(right.Effect.Generation); if (c != 0) return c;
            c = left.ParentSequence.CompareTo(right.ParentSequence); if (c != 0) return c;
            c = left.ProvenanceId.CompareTo(right.ProvenanceId); if (c != 0) return c;
            c = left.ProvenanceDepth.CompareTo(right.ProvenanceDepth); if (c != 0) return c;
            c = ((int)left.Flags).CompareTo((int)right.Flags); if (c != 0) return c;
            return left.ProducerIndex.CompareTo(right.ProducerIndex);
        }
    }

    public sealed class ProducerSequence
    {
        private readonly int _producerId;
        private long _local;
        public ProducerSequence(int producerId) { if (producerId < 0) throw new ArgumentOutOfRangeException(nameof(producerId)); _producerId = producerId; }
        public long Next() => ((long)_producerId << 32) | (uint)_local++;
        public static int Producer(long sequence) => (int)(sequence >> 32);
        public static int Local(long sequence) => (int)sequence;
    }

}
