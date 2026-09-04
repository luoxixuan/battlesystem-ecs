using BattleSystemECS.Components;

namespace BattleSystemECS.Core.GAS
{
    public readonly struct LegacyEffectSnapshot
    {
        public readonly string Name;
        public readonly int AttributeIndex;
        public readonly AttributeModifierOp ModifierOp;
        public readonly float Magnitude;

        public LegacyEffectSnapshot(string name, int attributeIndex, AttributeModifierOp modifierOp, float magnitude)
        { Name = name ?? string.Empty; AttributeIndex = attributeIndex; ModifierOp = modifierOp; Magnitude = magnitude; }
    }

    public readonly struct GameplayEffectApplication
    {
        public readonly GameplayEffectDefinition Definition;
        public readonly LegacyEffectSnapshot LegacySnapshot;
        public readonly ActiveGameplayEffect Runtime;

        public GameplayEffectApplication(GameplayEffectDefinition definition, LegacyEffectSnapshot legacySnapshot, ActiveGameplayEffect runtime)
        { Definition = definition; LegacySnapshot = legacySnapshot; Runtime = runtime; }
    }

    /// <summary>唯一的旧效果 facade 转换点；运行态由 ActiveGameplayEffectStore 拥有。</summary>
    public static class LegacyEffectAdapter
    {
        public static GameplayEffectApplication CreateApplication(GameplayEffectDef definition, EntityHandle source, EntityHandle target)
        {
            int stableId = StableId(definition.Name);
            var id = new EffectId(stableId);
            var refresh = definition.StackingBehavior == StackingBehavior.DurationRefresh
                ? RefreshPolicy.Duration
                : definition.StackingBehavior == StackingBehavior.MaxStacksRefresh
                    ? RefreshPolicy.StacksAndDuration
                    : RefreshPolicy.None;
            // Periodic 伤害走 payload tick，禁止再挂 ENEMY_HEALTH 这类 modifier（TryApply 会立刻改血）。
            // SkillSystem 等仍写 Multiply(m)；运行时映射为 Percent(m−1)，Catalog 校验看不到这条路径。
            var modifiers = definition.Type != EffectType.Periodic && definition.AttributeIndex >= 0
                ? new[] { MapLegacyModifier(definition.AttributeIndex, definition.ModifierOp, definition.Magnitude) }
                : System.Array.Empty<ModifierDefinition>();
            GameplayEffectDefinition immutable;
            if (definition.Type == EffectType.Periodic && definition.TickInterval > 0f)
            {
                var spec = new PeriodicSpec(definition.TickInterval, FirstTickPolicy.NextInterval, CatchUpPolicy.CatchUpAll,
                    default(ExecutionId), DamageType.True, ElementType.Poison, definition.Magnitude);
                immutable = new GameplayEffectDefinition(id, EffectType.Periodic, modifiers, definition.Duration,
                    ClockId.Combat, definition.StackingBehavior, System.Math.Max(1, definition.MaxStacks),
                    refresh, SourceDeathPolicy.Persist, EffectPayloadKind.Damage, default(TagId), spec,
                    System.Array.Empty<ExecutionId>());
            }
            else
            {
                immutable = new GameplayEffectDefinition(id, definition.Type, modifiers, definition.Duration,
                    definition.TickInterval, ClockId.Combat, definition.StackingBehavior, System.Math.Max(1, definition.MaxStacks),
                    refresh, SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent, default(TagId),
                    System.Array.Empty<ExecutionId>());
            }
            var runtime = new ActiveGameplayEffect(default(EffectHandle), id, source, target, definition.Duration,
                definition.TotalTicks, definition.Magnitude, immutable.Clock,
                immutable.Periodic.HasValue ? immutable.Periodic.Value.FirstTick : FirstTickPolicy.NextInterval,
                immutable.Periodic.HasValue ? immutable.Periodic.Value.CatchUp : CatchUpPolicy.CatchUpAll,
                immutable.SourceDeath);
            return new GameplayEffectApplication(immutable,
                new LegacyEffectSnapshot(definition.Name, definition.AttributeIndex, definition.ModifierOp, definition.Magnitude), runtime);
        }

        public static AppliedEffect ToRuntime(GameplayEffectDef definition, int sourceEntityId, int targetEntityId = -1)
        {
            var effect = new AppliedEffect(definition, sourceEntityId);
            effect.DefinitionId = new EffectId(StableId(definition.Name));
            effect.Clock = ClockId.Combat;
            effect.FirstTick = FirstTickPolicy.NextInterval;
            effect.CatchUp = CatchUpPolicy.CatchUpAll;
            effect.SourceDeath = SourceDeathPolicy.Persist;
            return effect;
        }

        internal static GameplayEffectApplication FromProjection(AppliedEffect effect, EntityHandle source, EntityHandle target)
        {
            var application = CreateApplication(effect.Definition, source, target);
            var runtime = application.Runtime;
            runtime.RemainingTime = effect.RemainingTime;
            runtime.TickAccumulator = effect.TimeSinceLastTick;
            runtime.TicksRemaining = effect.TicksRemaining;
            runtime.StackCount = effect.StackCount <= 0 ? 1 : effect.StackCount;
            runtime.Clock = effect.Clock;
            runtime.FirstTick = effect.FirstTick;
            runtime.CatchUp = effect.CatchUp;
            runtime.SourceDeath = effect.SourceDeath;
            runtime.FirstTickPending = effect.FirstTickPending;
            return new GameplayEffectApplication(application.Definition, application.LegacySnapshot, runtime);
        }

        internal static AppliedEffect ToProjection(ActiveGameplayEffect runtime, GameplayEffectDefinition definition, LegacyEffectSnapshot snapshot)
        {
            var legacy = new GameplayEffectDef(snapshot.Name, definition.Type, snapshot.AttributeIndex,
                snapshot.ModifierOp, snapshot.Magnitude, definition.Duration);
            legacy.TickInterval = definition.Period;
            legacy.TotalTicks = definition.Period > 0f ? System.Math.Max(1, (int)System.Math.Floor(definition.Duration / definition.Period)) : 0;
            legacy.StackingBehavior = definition.Stacking;
            legacy.MaxStacks = definition.MaxStacks;
            legacy.RefreshDuration = definition.Refresh != RefreshPolicy.None;
            legacy.RemainingTime = runtime.RemainingTime;
            legacy.TicksRemaining = runtime.TicksRemaining;
            var projection = new AppliedEffect(legacy, runtime.Source, runtime.Target)
            {
                Handle = runtime.Handle,
                DefinitionId = runtime.DefinitionId,
                RemainingTime = runtime.RemainingTime,
                TimeSinceLastTick = runtime.TickAccumulator,
                TicksRemaining = runtime.TicksRemaining,
                StackCount = runtime.StackCount,
                Clock = runtime.Clock,
                FirstTick = runtime.FirstTick,
                CatchUp = runtime.CatchUp,
                SourceDeath = runtime.SourceDeath,
                FirstTickPending = runtime.FirstTickPending
            };
            return projection;
        }

        private static ModifierDefinition MapLegacyModifier(int attributeIndex, AttributeModifierOp op, float magnitude)
        {
            if (op == AttributeModifierOp.Multiply)
            {
                op = AttributeModifierOp.Percent;
                magnitude -= 1f;
            }
            return new ModifierDefinition(new AttributeKey(attributeIndex), op, magnitude, snapshot: SnapshotPolicy.CaptureOnApply);
        }

        private static int StableId(string name)
        {
            int stableId = 0;
            if (name != null)
                for (int i = 0; i < name.Length; i++) stableId = unchecked(stableId * 31 + name[i]);
            return stableId;
        }
    }
}
