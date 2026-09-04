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
        private static readonly Dictionary<string, AttributeKey> _attributes = new Dictionary<string, AttributeKey>(StringComparer.OrdinalIgnoreCase)
        { ["AttackDamage"] = new AttributeKey(0), ["AttackRange"] = new AttributeKey(1), ["MaxHealth"] = new AttributeKey(2), ["CurrentHealth"] = new AttributeKey(3), ["Gold"] = new AttributeKey(4), ["CritRate"] = new AttributeKey(5), ["BuffStrength"] = new AttributeKey(6), ["Mana"] = new AttributeKey(7), ["DamageOutputMultiplier"] = new AttributeKey(8), ["Shield"] = new AttributeKey(9), ["Armor"] = new AttributeKey(10) };
        private static readonly Dictionary<string, ExecutorId> _executors = new Dictionary<string, ExecutorId>(StringComparer.OrdinalIgnoreCase) { ["Skill"] = new ExecutorId(0) };
        private static readonly Dictionary<string, ConsumerId> _consumers = new Dictionary<string, ConsumerId>(StringComparer.OrdinalIgnoreCase) { ["Skill"] = new ConsumerId(0) };
        public static bool TryTag(string name, out TagId id) => GameplayTagVocabulary.TryResolve(name, out id);
        public static bool TryTag(TagId id) => GameplayTagVocabulary.Contains(id);
        public static int TagCount => GameplayTagVocabulary.Count;
        public static bool TryAttribute(AttributeKey key) { foreach (var value in _attributes.Values) if (value.Equals(key)) return true; return false; }
        public static bool TryExecutor(ExecutorId id) { foreach (var value in _executors.Values) if (value.Equals(id)) return true; return false; }
        public static bool TryConsumer(ConsumerId id) { foreach (var value in _consumers.Values) if (value.Equals(id)) return true; return false; }
        public static ExecutorId SkillExecutor => _executors["Skill"];
        public static ConsumerId SkillConsumer => _consumers["Skill"];
        public static TagId SkillTag => GameplayTagVocabulary.Normal;
        public static AttributeKey Mana => _attributes["Mana"];
        public static AttributeKey AttackDamage => _attributes["AttackDamage"];
        public static AttributeKey DamageOutputMultiplier => _attributes["DamageOutputMultiplier"];
        public static TagId EnemyBuffTag => GameplayTagVocabulary.EnemyBuff;
        public static TagId TowerSilencedTag => GameplayTagVocabulary.TowerSilenced;
        public static TagId DispellableTag => GameplayTagVocabulary.Dispellable;
        public static TagId DebuffTag => GameplayTagVocabulary.Debuff;
        public static TagId ControlTag => GameplayTagVocabulary.Control;
        public static TagId StunTag => GameplayTagVocabulary.Stun;
    }
}
