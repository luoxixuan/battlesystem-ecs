using System;

namespace BattleSystemECS.Core.GAS
{
    public enum EffectType { Instant, Duration, Periodic, Heal }

    /// <summary>
    /// 属性修饰运算。Percent 的 magnitude 是加项（+30% 配 0.30）。
    /// Multiply 仅作 legacy 输入，不得进入 Aggregator（adapter 映射为 Percent(m−1)）。
    /// </summary>
    public enum AttributeModifierOp { Add, Multiply, Override, Percent }

    /// <summary>
    /// Defines how multiple instances of the same effect stack on a target.
    /// </summary>
    public enum StackingBehavior
    {
        None = 0,           // No stacking: replaces any existing effect of same name
        DurationRefresh = 1, // Refresh duration only, no stacking
        MaxStacks = 2,       // Stack up to MaxStacks, no duration refresh
        MaxStacksRefresh = 3 // Stack up to MaxStacks, refresh duration on each application
    }

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
        // Legacy compatibility facade; production runtime state belongs to ActiveGameplayEffectStore.
        public float RemainingTime;

        // Periodic (DoT) fields
        public float TickInterval;  // seconds between ticks (e.g., 1.0 = once per second)
        public int TotalTicks;      // total number of ticks (e.g., 5 for 5s DoT at 1s interval)
        // Legacy compatibility facade; production runtime state belongs to ActiveGameplayEffectStore.
        public int TicksRemaining;

        // Stacking fields
        public StackingBehavior StackingBehavior;
        public int MaxStacks;       // max stack count (1 = single, >1 = stacking)
        // Legacy compatibility facade; derive refresh behavior from StackingBehavior.
        public bool RefreshDuration;

        public GameplayEffectDef(string name, EffectType type, int attrIdx, AttributeModifierOp op, float magnitude, float duration = 0f)
        {
            Name = name; Type = type; AttributeIndex = attrIdx; ModifierOp = op; Magnitude = magnitude;
            Duration = duration; RemainingTime = duration;
            TickInterval = 0f; TotalTicks = 0; TicksRemaining = 0;
            StackingBehavior = StackingBehavior.None;
            MaxStacks = 1;
            RefreshDuration = false;
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
            def.StackingBehavior = StackingBehavior.None;
            def.MaxStacks = 1;
            def.RefreshDuration = false;
            return def;
        }

        /// <summary>
        /// Full constructor for Periodic (DoT) effects with stacking behavior.
        /// </summary>
        public static GameplayEffectDef Periodic(string name, int attrIdx, float damagePerTick, float totalDuration, float tickInterval, StackingBehavior stacking, int maxStacks)
        {
            int ticks = totalDuration <= 0 ? 0 : Math.Max(1, (int)Math.Floor(totalDuration / tickInterval));
            var def = new GameplayEffectDef(name, EffectType.Periodic, attrIdx, AttributeModifierOp.Add, damagePerTick, totalDuration);
            def.TickInterval = tickInterval;
            def.TotalTicks = ticks;
            def.TicksRemaining = ticks;
            def.StackingBehavior = stacking;
            def.MaxStacks = Math.Max(1, maxStacks);
            def.RefreshDuration = stacking == StackingBehavior.DurationRefresh || stacking == StackingBehavior.MaxStacksRefresh;
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

        // Stacking: current stack count for this applied effect
        public int StackCount;
        // Legacy runtime projection. ActiveGameplayEffectStore remains authoritative.
        public float RemainingTime;
        public int TicksRemaining;
        public EffectHandle Handle;
        public EffectId DefinitionId;
        public EntityHandle Source;
        public EntityHandle Target;
        public ClockId Clock;
        public FirstTickPolicy FirstTick;
        public CatchUpPolicy CatchUp;
        public SourceDeathPolicy SourceDeath;
        public bool FirstTickPending;

        public AppliedEffect(GameplayEffectDef def, int sourceId)
        {
            Definition = def;
            SourceEntityId = sourceId;
            TimeSinceLastTick = 0f;
            StackCount = 1;
            RemainingTime = def.Duration;
            TicksRemaining = def.TotalTicks;
            Handle = default(EffectHandle);
            DefinitionId = default(EffectId);
            Source = default(EntityHandle);
            Target = default(EntityHandle);
            Clock = ClockId.Combat;
            FirstTick = FirstTickPolicy.NextInterval;
            CatchUp = CatchUpPolicy.CatchUpAll;
            SourceDeath = SourceDeathPolicy.Persist;
            FirstTickPending = true;
        }

        public AppliedEffect(GameplayEffectDef def, EntityHandle source, EntityHandle target)
            : this(def, source.Index)
        {
            Source = source;
            Target = target;
        }
    }
}
