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
        public readonly ExecutionContext Context;
        public DamageRequest(EntityHandle source, EntityHandle target, float amount, DamageType type, long sequence, AbilityId ability = default(AbilityId), EffectId effect = default(EffectId), int ownerPlayerId = -1) : this(source, target, amount, type, ElementType.None, DamageFlags.None, DamageAmountStage.Raw, DamageCommitBoundary.GameplayResolve, sequence, 0L, ability, effect, ownerPlayerId, default(ExecutionContext), 0L, 0) { }
        public DamageRequest(EntityHandle source, EntityHandle target, float amount, DamageType type, ElementType element, DamageFlags flags, DamageAmountStage stage, DamageCommitBoundary boundary, long sequence, long parentSequence = 0L, AbilityId ability = default(AbilityId), EffectId effect = default(EffectId), int ownerPlayerId = -1, ExecutionContext context = default(ExecutionContext), long provenanceId = 0L, int provenanceDepth = 0) { Source = source; Target = target; RawAmount = amount; DamageType = type; ElementType = element; Flags = flags; AmountStage = stage; CommitBoundary = boundary; Sequence = sequence; ParentSequence = parentSequence; Ability = ability; Effect = effect; OwnerPlayerId = ownerPlayerId; Context = context; ProvenanceId = provenanceId; ProvenanceDepth = provenanceDepth; }
    }
    public readonly struct HealRequest { public readonly EntityHandle Source, Target; public readonly float RawAmount; public readonly long Sequence; public HealRequest(EntityHandle source, EntityHandle target, float amount, long sequence) { Source = source; Target = target; RawAmount = amount; Sequence = sequence; } }
    public readonly struct ShieldRequest { public readonly EntityHandle Source, Target; public readonly float Amount, Duration; public readonly ClockId Clock; public readonly long Sequence; public ShieldRequest(EntityHandle source, EntityHandle target, float amount, float duration, ClockId clock, long sequence) { Source = source; Target = target; Amount = amount; Duration = duration; Clock = clock; Sequence = sequence; } }
    public enum ResourceOperation { Add, Set }
    public readonly struct ResourceRequest { public readonly EntityHandle Source, Target; public readonly AttributeKey Resource; public readonly float Delta; public readonly ResourceOperation Operation; public readonly int CauseId; public readonly long Sequence; public readonly DamageCommitBoundary CommitBoundary; public readonly int OwnerPlayerId; public ResourceRequest(EntityHandle source, EntityHandle target, AttributeKey resource, float delta, long sequence, int ownerPlayerId = -1) : this(source, target, resource, delta, ResourceOperation.Add, 0, sequence, DamageCommitBoundary.GameplayResolve, ownerPlayerId) { } public ResourceRequest(EntityHandle source, EntityHandle target, AttributeKey resource, float delta, ResourceOperation operation, int causeId, long sequence, int ownerPlayerId = -1) : this(source, target, resource, delta, operation, causeId, sequence, DamageCommitBoundary.GameplayResolve, ownerPlayerId) { } public ResourceRequest(EntityHandle source, EntityHandle target, AttributeKey resource, float delta, ResourceOperation operation, int causeId, long sequence, DamageCommitBoundary boundary, int ownerPlayerId = -1) { Source = source; Target = target; Resource = resource; Delta = delta; Operation = operation; CauseId = causeId; Sequence = sequence; CommitBoundary = boundary; OwnerPlayerId = ownerPlayerId; } }

    public enum GameplayEventType { HitConfirmed, DamageApplied, AbilityActivated, AbilityRejected, EffectApplied, EffectRejected, EffectExpired, EffectRemoved, HitMissed, DamageBlocked, HealApplied, ShieldChanged, ResourceChanged, DeathQueued, KillConfirmed, GameplayLoopAborted }
    public readonly struct GameplayEvent { public readonly GameplayEventType Type; public readonly EntityHandle Source, Target; public readonly EffectHandle Effect; public readonly EffectId EffectDefinition; public readonly DamageFlags Flags; public readonly long ParentSequence, ProvenanceId; public readonly int ProvenanceDepth; public readonly int ProducerIndex; public readonly long Sequence; public readonly int Reason; public GameplayEvent(GameplayEventType type, EntityHandle source, EntityHandle target, long sequence, int reason = 0) : this(type, source, target, default(EffectHandle), default(EffectId), DamageFlags.None, sequence, 0L, reason, 0, 0L, 0) { } public GameplayEvent(GameplayEventType type, EntityHandle source, EntityHandle target, EffectHandle effect, long sequence, int reason = 0, int producerIndex = 0) : this(type, source, target, effect, default(EffectId), DamageFlags.None, sequence, 0L, reason, producerIndex, 0L, 0) { } public GameplayEvent(GameplayEventType type, EntityHandle source, EntityHandle target, EffectHandle effect, EffectId effectDefinition, long sequence, long parentSequence = 0L, int reason = 0, int producerIndex = 0, DamageFlags flags = DamageFlags.None, long provenanceId = 0L, int provenanceDepth = 0) : this(type, source, target, effect, effectDefinition, flags, sequence, parentSequence, reason, producerIndex, provenanceId, provenanceDepth) { } public GameplayEvent(GameplayEventType type, EntityHandle source, EntityHandle target, EffectId effectDefinition, DamageFlags flags, long sequence, long parentSequence = 0L, int reason = 0, int producerIndex = 0, long provenanceId = 0L, int provenanceDepth = 0) : this(type, source, target, default(EffectHandle), effectDefinition, flags, sequence, parentSequence, reason, producerIndex, provenanceId, provenanceDepth) { } public GameplayEvent(GameplayEventType type, EntityHandle source, EntityHandle target, EffectHandle effect, EffectId effectDefinition, DamageFlags flags, long sequence, long parentSequence = 0L, int reason = 0, int producerIndex = 0, long provenanceId = 0L, int provenanceDepth = 0) { Type = type; Source = source; Target = target; Effect = effect; EffectDefinition = effectDefinition; Flags = flags; ParentSequence = parentSequence; ProvenanceId = provenanceId; ProvenanceDepth = provenanceDepth; ProducerIndex = producerIndex; Sequence = sequence; Reason = reason; } }

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
