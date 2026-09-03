using System;
using System.Collections.Generic;

namespace BattleSystemECS.Core.GAS
{
    public static class CatalogRegistries
    {
        public const int Version = 1;
        public const int MaxAbilities = 4096;
        public const int MaxEffects = 4096;
        public const int MaxTriggers = 4096;
        public const int MaxExecutions = 8192;
        private static readonly Dictionary<string, TagId> _tags = new Dictionary<string, TagId>(StringComparer.OrdinalIgnoreCase)
        { ["Normal"] = new TagId(0), ["Burn"] = new TagId(1), ["Fire"] = new TagId(2), ["Freeze"] = new TagId(3), ["Lightning"] = new TagId(4), ["Explosion"] = new TagId(5), ["Plasma"] = new TagId(6), ["Poison"] = new TagId(7), ["EnemyBuff"] = new TagId(8), ["TowerSilenced"] = new TagId(9), ["Dispellable"] = new TagId(10) };
        private static readonly Dictionary<string, AttributeKey> _attributes = new Dictionary<string, AttributeKey>(StringComparer.OrdinalIgnoreCase)
        { ["AttackDamage"] = new AttributeKey(0), ["AttackRange"] = new AttributeKey(1), ["MaxHealth"] = new AttributeKey(2), ["CurrentHealth"] = new AttributeKey(3), ["Gold"] = new AttributeKey(4), ["CritRate"] = new AttributeKey(5), ["BuffStrength"] = new AttributeKey(6), ["Mana"] = new AttributeKey(7), ["DamageOutputMultiplier"] = new AttributeKey(8), ["Shield"] = new AttributeKey(9), ["Armor"] = new AttributeKey(10) };
        private static readonly Dictionary<string, ExecutorId> _executors = new Dictionary<string, ExecutorId>(StringComparer.OrdinalIgnoreCase) { ["Skill"] = new ExecutorId(0) };
        private static readonly Dictionary<string, ConsumerId> _consumers = new Dictionary<string, ConsumerId>(StringComparer.OrdinalIgnoreCase) { ["Skill"] = new ConsumerId(0) };
        public static bool TryTag(string name, out TagId id) => _tags.TryGetValue(name, out id);
        public static bool TryTag(TagId id) { foreach (var value in _tags.Values) if (value.Equals(id)) return true; return false; }
        public static int TagCount => _tags.Count;
        public static bool TryAttribute(AttributeKey key) { foreach (var value in _attributes.Values) if (value.Equals(key)) return true; return false; }
        public static bool TryExecutor(ExecutorId id) { foreach (var value in _executors.Values) if (value.Equals(id)) return true; return false; }
        public static bool TryConsumer(ConsumerId id) { foreach (var value in _consumers.Values) if (value.Equals(id)) return true; return false; }
        public static ExecutorId SkillExecutor => _executors["Skill"];
        public static ConsumerId SkillConsumer => _consumers["Skill"];
        public static TagId SkillTag => _tags["Normal"];
        public static AttributeKey Mana => _attributes["Mana"];
        public static AttributeKey AttackDamage => _attributes["AttackDamage"];
        public static AttributeKey DamageOutputMultiplier => _attributes["DamageOutputMultiplier"];
        public static TagId EnemyBuffTag => _tags["EnemyBuff"];
        public static TagId TowerSilencedTag => _tags["TowerSilenced"];
        public static TagId DispellableTag => _tags["Dispellable"];
    }
}
