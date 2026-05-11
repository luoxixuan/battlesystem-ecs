namespace BattleSystemECS.Core.GAS
{
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