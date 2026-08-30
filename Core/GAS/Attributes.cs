using System;
using System.Collections.Generic;

namespace BattleSystemECS.Core.GAS
{
    public enum AttributeDomain { Combat, Defense, Resource, Economy, Movement }
    public enum AttributeUnit { Scalar, Points, Percent, Tiles, Currency }

    public readonly struct AttributeDefinition
    {
        public readonly AttributeKey Key;
        public readonly string Name;
        public readonly AttributeDomain Domain;
        public readonly float DefaultValue, Minimum, Maximum;
        public readonly AttributeUnit Unit;
        public readonly bool AllowsModifiers;
        public AttributeDefinition(AttributeKey key, string name, AttributeDomain domain, float defaultValue,
            AttributeUnit unit = AttributeUnit.Scalar, float minimum = float.NegativeInfinity,
            float maximum = float.PositiveInfinity, bool allowsModifiers = true)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Attribute name is required", nameof(name));
            if (float.IsNaN(defaultValue) || defaultValue < minimum || defaultValue > maximum) throw new ArgumentOutOfRangeException(nameof(defaultValue));
            Key = key; Name = name; Domain = domain; DefaultValue = defaultValue; Unit = unit;
            Minimum = minimum; Maximum = maximum; AllowsModifiers = allowsModifiers;
        }
        public float Clamp(float value) => Math.Min(Maximum, Math.Max(Minimum, value));
    }

    /// <summary>Immutable registry for the attributes exposed by the compatibility projection.</summary>
    public sealed class AttributeSchema
    {
        private readonly IReadOnlyDictionary<AttributeKey, AttributeDefinition> _definitions;
        public AttributeSchema(IEnumerable<AttributeDefinition> definitions)
        {
            var map = new Dictionary<AttributeKey, AttributeDefinition>();
            foreach (var definition in definitions ?? throw new ArgumentNullException(nameof(definitions)))
                if (!map.TryAdd(definition.Key, definition)) throw new ArgumentException("Duplicate attribute key", nameof(definitions));
            _definitions = new System.Collections.ObjectModel.ReadOnlyDictionary<AttributeKey, AttributeDefinition>(map);
        }
        public IReadOnlyDictionary<AttributeKey, AttributeDefinition> Definitions => _definitions;
        public AttributeDefinition Get(AttributeKey key) => _definitions.TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key.ToString());
        public bool TryGet(AttributeKey key, out AttributeDefinition definition) => _definitions.TryGetValue(key, out definition);
        public static AttributeSchema Default { get; } = new AttributeSchema(new[] {
            new AttributeDefinition(new AttributeKey(0), "AttackDamage", AttributeDomain.Combat, 0f, AttributeUnit.Points, 0f),
            new AttributeDefinition(new AttributeKey(1), "AttackRange", AttributeDomain.Movement, 0f, AttributeUnit.Tiles, 0f),
            new AttributeDefinition(new AttributeKey(2), "MaxHealth", AttributeDomain.Resource, 0f, AttributeUnit.Points, 0f),
            new AttributeDefinition(new AttributeKey(3), "CurrentHealth", AttributeDomain.Resource, 0f, AttributeUnit.Points, 0f, float.PositiveInfinity, false),
            new AttributeDefinition(new AttributeKey(4), "Gold", AttributeDomain.Economy, 0f, AttributeUnit.Currency, 0f, float.PositiveInfinity, false),
            new AttributeDefinition(new AttributeKey(5), "CritRate", AttributeDomain.Combat, 0f, AttributeUnit.Percent, 0f, 1f),
            new AttributeDefinition(new AttributeKey(6), "BuffStrength", AttributeDomain.Combat, 0f, AttributeUnit.Percent, 0f),
            new AttributeDefinition(new AttributeKey(7), "Mana", AttributeDomain.Resource, 0f, AttributeUnit.Points, 0f, float.PositiveInfinity, false),
            new AttributeDefinition(new AttributeKey(8), "DamageOutputMultiplier", AttributeDomain.Combat, 1f, AttributeUnit.Scalar, 0f)
        });
    }

    public readonly struct AttributeModifierHandle : IEquatable<AttributeModifierHandle>
    {
        internal readonly long Id; internal AttributeModifierHandle(long id) { Id = id; }
        public bool Equals(AttributeModifierHandle other) => Id == other.Id;
        public override bool Equals(object obj) => obj is AttributeModifierHandle other && Equals(other);
        public override int GetHashCode() => Id.GetHashCode();
    }

    /// <summary>唯一属性解释器：每次 dirty 聚合都从 base 重算，避免浮点逆运算误差。</summary>
    public sealed class AttributeAggregator
    {
        private sealed class Modifier { public AttributeModifierHandle Handle; public AttributeKey Key; public AttributeModifierOp Op; public float Magnitude; public int Priority; public float Captured; public long Sequence; }
        private readonly AttributeSchema _schema;
        private readonly Dictionary<(int, AttributeKey), float> _base = new Dictionary<(int, AttributeKey), float>();
        private readonly Dictionary<(int, AttributeKey), float> _computed = new Dictionary<(int, AttributeKey), float>();
        private readonly Dictionary<(int, AttributeKey), List<Modifier>> _modifiers = new Dictionary<(int, AttributeKey), List<Modifier>>();
        private readonly HashSet<(int, AttributeKey)> _dirty = new HashSet<(int, AttributeKey)>();
        private long _nextId, _sequence;
        public AttributeAggregator(AttributeSchema schema = null) { _schema = schema ?? AttributeSchema.Default; }
        public int DirtyCount => _dirty.Count;
        public void SetBase(int entityId, AttributeKey key, float value) { var d = _schema.Get(key); _base[(entityId, key)] = d.Clamp(value); _dirty.Add((entityId, key)); }
        public AttributeModifierHandle AddModifier(int entityId, ModifierDefinition definition, float capturedMagnitude = float.NaN)
        {
            var d = _schema.Get(definition.Attribute); if (!d.AllowsModifiers) throw new InvalidOperationException("Attribute does not allow modifiers");
            var id = new AttributeModifierHandle(++_nextId); var value = definition.Snapshot == SnapshotPolicy.CaptureOnApply && !float.IsNaN(capturedMagnitude) ? capturedMagnitude : definition.Magnitude;
            var m = new Modifier { Handle = id, Key = definition.Attribute, Op = definition.Operation, Magnitude = definition.Magnitude, Captured = value, Priority = definition.Priority, Sequence = ++_sequence };
            var slot = (entityId, definition.Attribute); if (!_modifiers.TryGetValue(slot, out var list)) _modifiers[slot] = list = new List<Modifier>(); list.Add(m); _dirty.Add(slot); return id;
        }
        public bool RemoveModifier(int entityId, AttributeModifierHandle handle) { foreach (var pair in _modifiers) if (pair.Key.Item1 == entityId) { var index = pair.Value.FindIndex(m => m.Handle.Equals(handle)); if (index >= 0) { _dirty.Add(pair.Key); pair.Value.RemoveAt(index); return true; } } return false; }
        public bool RefreshModifier(int entityId, AttributeModifierHandle handle, float magnitude)
        { foreach (var pair in _modifiers) if (pair.Key.Item1 == entityId) { var modifier = pair.Value.Find(m => m.Handle.Equals(handle)); if (modifier != null) { modifier.Magnitude = magnitude; modifier.Captured = magnitude; _dirty.Add(pair.Key); return true; } } return false; }
        public void MarkDirty(int entityId, AttributeKey key) => _dirty.Add((entityId, key));
        public void AggregateDirty() { var pending = new List<(int, AttributeKey)>(_dirty); _dirty.Clear(); foreach (var slot in pending) Aggregate(slot.Item1, slot.Item2); }
        public float GetComputed(int entityId, AttributeKey key, float fallback = 0f) { if (_dirty.Contains((entityId, key))) AggregateDirty(); return _computed.TryGetValue((entityId, key), out var value) ? value : fallback; }
        private void Aggregate(int entityId, AttributeKey key) { var slot = (entityId, key); var value = _base.TryGetValue(slot, out var b) ? b : _schema.Get(key).DefaultValue; if (_modifiers.TryGetValue(slot, out var list)) { list.Sort((x, y) => x.Priority != y.Priority ? x.Priority.CompareTo(y.Priority) : x.Sequence.CompareTo(y.Sequence)); foreach (var m in list) { var magnitude = m.Captured; if (m.Op == AttributeModifierOp.Override) value = magnitude; else if (m.Op == AttributeModifierOp.Add) value += magnitude; else value *= magnitude; } } _computed[slot] = _schema.Get(key).Clamp(value); }
    }

    /// <summary>
    /// Represents a named, modifiable attribute (e.g., MaxHealth, AttackDamage, Gold).
    /// BaseValue is the base value; CurrentValue includes all modifiers applied this frame.
    /// </summary>
    public struct GameplayAttribute
    {
        public float BaseValue;
        public float CurrentValue; // after modifiers applied

        public GameplayAttribute(float baseValue) { BaseValue = baseValue; CurrentValue = baseValue; }

        public void ApplyModifier(float modifier) { CurrentValue += modifier; }
        public void RemoveModifier(float modifier) { CurrentValue -= modifier; }
        public void ResetToBase() { CurrentValue = BaseValue; }
    }

    /// <summary>
    /// Attribute sets define which attributes an entity has.
    /// Multiple entities can share the same AttributeSetDefinition; data lives in per-entity GASComponent.
    /// </summary>
    public static class AttributeSetDefinitions
    {
        // Player attributes
        public const int ATTACK_DAMAGE = 0;
        public const int ATTACK_RANGE = 1;
        public const int MAX_HEALTH = 2;
        public const int CURRENT_HEALTH = 3;
        public const int GOLD = 4;
        public const int CRIT_RATE = 5;
        public const int BUFF_STRENGTH = 6;
        public const int PLAYER_ATTRIBUTE_COUNT = 7;

        // Enemy attributes
        public const int ENEMY_HEALTH = 0;
        public const int ENEMY_DAMAGE = 1;
        public const int ENEMY_GOLD_REWARD = 2;
        public const int ENEMY_ATTRIBUTE_COUNT = 3;

        public static string PlayerAttributeName(int index) => index switch {
            ATTACK_DAMAGE => "AttackDamage",
            ATTACK_RANGE => "AttackRange",
            MAX_HEALTH => "MaxHealth",
            CURRENT_HEALTH => "CurrentHealth",
            GOLD => "Gold",
            CRIT_RATE => "CritRate",
            BUFF_STRENGTH => "BuffStrength",
            _ => $"Unknown_{index}"
        };
    }
}
