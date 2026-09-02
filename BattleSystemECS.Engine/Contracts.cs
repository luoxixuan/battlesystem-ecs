using System;
using System.Collections.Generic;

namespace BattleSystemECS.Engine
{
    public readonly struct EntityHandle : IEquatable<EntityHandle> { public int Value { get; } public EntityHandle(int value) { Value = value; } public bool Equals(EntityHandle other) => Value == other.Value; public override bool Equals(object obj) => obj is EntityHandle other && Equals(other); public override int GetHashCode() => Value; }
    public readonly struct AbilityId : IEquatable<AbilityId> { public int Value { get; } public AbilityId(int value) { Value = value; } public bool Equals(AbilityId other) => Value == other.Value; public override bool Equals(object obj) => obj is AbilityId other && Equals(other); public override int GetHashCode() => Value; }
    public readonly struct EffectId : IEquatable<EffectId> { public int Value { get; } public EffectId(int value) { Value = value; } public bool Equals(EffectId other) => Value == other.Value; public override bool Equals(object obj) => obj is EffectId other && Equals(other); public override int GetHashCode() => Value; }
    public interface IWorldView { bool Exists(EntityHandle entity); }
    public interface IAttributeView { bool TryGet(EntityHandle entity, string key, out float value); }
    public interface ISpatialQuery { int Query(EntityHandle[] results, float x, float y, float radius); }
    public interface ICatalog { bool Contains(AbilityId ability); bool Contains(EffectId effect); }
    public interface ICommandSink<T> { bool TrySubmit(T command); }
    public interface IDamageResolver { bool TryResolve(EntityHandle source, EntityHandle target, float amount); }
    public interface IResourceResolver { bool TryResolve(EntityHandle target, string resource, float amount); }
    public interface IFrameGraph { IReadOnlyList<string> NodeIds { get; } }
    public interface IFrameContext { float DeltaTime { get; } int Turn { get; } }
    public interface IFrameNode { string Id { get; } void Execute(IFrameContext context); }
    public interface IFrameExecutionPlan { IReadOnlyList<IFrameNode> Nodes { get; } }
    public interface IDiagnostics { void Record(string code, string detail); }
    public interface ISystemInstaller { string Id { get; } }
}
