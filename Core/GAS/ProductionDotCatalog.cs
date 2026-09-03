using System;
using System.Collections.Generic;
using BattleSystemECS.Components;

namespace BattleSystemECS.Core.GAS
{
    /// <summary>
    /// 生产 Periodic DoT 的 catalog 模板。Id 与 Compile() 时 effects 下标连续；
    /// 运行时按名字物化出空 modifier、Periodic Damage payload 的定义，再走 TryApply。
    /// </summary>
    public static class ProductionDotCatalog
    {
        private static readonly Dictionary<string, EffectId> _byName =
            new Dictionary<string, EffectId>(StringComparer.Ordinal);

        public static void AppendTo(List<GameplayEffectDefinition> effects)
        {
            if (effects == null) throw new ArgumentNullException(nameof(effects));
            _byName.Clear();
            Append(effects, "Firewall_Burn");
            Append(effects, "prod.dot.lava");
            Append(effects, "DemolishBurn");
            Append(effects, "DemolishPoison");
            for (int t = 0; t <= 10; t++)
                Append(effects, "corpse_zone_tick_" + t);
        }

        public static bool TryGetId(string name, out EffectId id)
        {
            if (string.IsNullOrEmpty(name)) { id = default(EffectId); return false; }
            return _byName.TryGetValue(name, out id);
        }

        public static bool TryMaterialize(GameplayCatalog catalog, string name, float duration, float period,
            float magnitude, StackingBehavior stacking, int maxStacks, out GameplayEffectDefinition definition)
        {
            definition = default(GameplayEffectDefinition);
            if (catalog == null || string.IsNullOrEmpty(name) || duration <= 0f || period <= 0f ||
                magnitude <= 0f || float.IsNaN(duration) || float.IsNaN(period) || float.IsNaN(magnitude))
                return false;
            if (!TryGetId(name, out var id) || !catalog.TryGetEffect(id, out var template) ||
                template.Type != EffectType.Periodic || !template.Periodic.HasValue || template.Modifiers.Count != 0)
                return false;
            var refresh = stacking == StackingBehavior.DurationRefresh ? RefreshPolicy.Duration
                : stacking == StackingBehavior.MaxStacksRefresh ? RefreshPolicy.StacksAndDuration
                : RefreshPolicy.None;
            var spec = new PeriodicSpec(period, template.Periodic.Value.FirstTick, template.Periodic.Value.CatchUp,
                template.Periodic.Value.PayloadExecution, DamageType.True, ElementType.Poison, magnitude);
            definition = new GameplayEffectDefinition(id, EffectType.Periodic, Array.Empty<ModifierDefinition>(),
                duration, template.Clock, stacking, maxStacks < 1 ? 1 : maxStacks, refresh, template.SourceDeath,
                EffectPayloadKind.Damage, template.Tag, spec, Array.Empty<ExecutionId>(),
                null, null, template.StackKey);
            return true;
        }

        private static void Append(List<GameplayEffectDefinition> effects, string name)
        {
            int id = effects.Count;
            var spec = new PeriodicSpec(1f, FirstTickPolicy.NextInterval, CatchUpPolicy.CatchUpAll,
                default(ExecutionId), DamageType.True, ElementType.Poison, 1f);
            effects.Add(new GameplayEffectDefinition(new EffectId(id), EffectType.Periodic,
                Array.Empty<ModifierDefinition>(), 1f, ClockId.Combat, StackingBehavior.None, 1,
                RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.Damage,
                CatalogRegistries.SkillTag, spec, Array.Empty<ExecutionId>()));
            _byName[name] = new EffectId(id);
        }
    }
}
