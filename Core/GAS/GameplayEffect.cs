namespace BattleSystemECS.Core.GAS
{
    public enum EffectType { Instant, Duration, Infinite }
    public enum AttributeModifierOp { Add, Multiply, Override }

    /// <summary>
    /// A gameplay effect that modifies attributes.
    /// </summary>
    public struct GameplayEffectDef
    {
        public string Name;
        public EffectType Type;
        public int AttributeIndex; // refers to AttributeSetDefinitions index
        public AttributeModifierOp ModifierOp;
        public float Magnitude; // the modifier value (e.g., +10, ×1.5)
        public float Duration;  // for Duration effects (seconds)
        public float RemainingTime; // runtime: countdown for duration effects

        public GameplayEffectDef(string name, EffectType type, int attrIdx, AttributeModifierOp op, float magnitude, float duration = 0f)
        {
            Name = name; Type = type; AttributeIndex = attrIdx; ModifierOp = op; Magnitude = magnitude; Duration = duration; RemainingTime = duration;
        }
    }

    /// <summary>
    /// An active (applied) gameplay effect on an entity. Duration/infinite effects track remaining time.
    /// </summary>
    public struct AppliedEffect
    {
        public GameplayEffectDef Definition;
        public int SourceEntityId; // who applied it

        public AppliedEffect(GameplayEffectDef def, int sourceId)
        {
            Definition = def;
            SourceEntityId = sourceId;
        }
    }
}