using System;

namespace BattleSystemECS.Core.GAS
{
    public enum EffectType { Instant, Duration, Periodic }

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

        // Periodic (DoT) fields
        public float TickInterval;  // seconds between ticks (e.g., 1.0 = once per second)
        public int TotalTicks;      // total number of ticks (e.g., 5 for 5s DoT at 1s interval)
        public int TicksRemaining;  // runtime: ticks left

        public GameplayEffectDef(string name, EffectType type, int attrIdx, AttributeModifierOp op, float magnitude, float duration = 0f)
        {
            Name = name; Type = type; AttributeIndex = attrIdx; ModifierOp = op; Magnitude = magnitude;
            Duration = duration; RemainingTime = duration;
            TickInterval = 0f; TotalTicks = 0; TicksRemaining = 0;
        }

        /// <summary>
        /// Convenience constructor for Periodic (DoT) effects.
        /// </summary>
        public static GameplayEffectDef Periodic(string name, int attrIdx, float damagePerTick, float totalDuration, float tickInterval)
        {
            int ticks = totalDuration <= 0 ? 0 : Math.Max(1, (int)Math.Floor(totalDuration / tickInterval));
            var def = new GameplayEffectDef(name, EffectType.Periodic, attrIdx, AttributeModifierOp.Add, damagePerTick, totalDuration);
            def.TickInterval = tickInterval;
            def.TotalTicks = ticks;
            def.TicksRemaining = ticks;
            return def;
        }
    }

    /// <summary>
    /// An active (applied) gameplay effect on an entity. Duration/Periodic effects track remaining time.
    /// </summary>
    public struct AppliedEffect
    {
        public GameplayEffectDef Definition;
        public int SourceEntityId; // who applied it

        // Periodic-specific: time accumulator since last tick
        public float TimeSinceLastTick;

        public AppliedEffect(GameplayEffectDef def, int sourceId)
        {
            Definition = def;
            SourceEntityId = sourceId;
            TimeSinceLastTick = 0f;
        }
    }
}