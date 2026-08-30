using System;
using System.Collections.Generic;

namespace BattleSystemECS.Core.GAS
{
    public static class CatalogValidator
    {
        public static void Validate(GameplayCatalog catalog, string path)
        {
            if (catalog == null) throw new CatalogValidationException($"{path}: catalog is null");
            var ids = new HashSet<int>();
            foreach (var ability in catalog.AbilityDefinitions)
            {
                if (!ids.Add(ability.Id.Value)) throw new CatalogValidationException($"{path}: duplicate ability id {ability.Id.Value} ({ability.Name})");
                if (!ability.Targeting.Id.Equals(new TargetingId(ability.Id.Value))) throw new CatalogValidationException($"{path}: ability {ability.Id.Value} targeting reference is not closed");
                if (!CatalogRegistries.TryExecutor(ability.Executor)) throw new CatalogValidationException($"{path}: ability {ability.Id.Value} ({ability.Name}) has unregistered executor");
                if (!CatalogRegistries.TryConsumer(ability.Consumer)) throw new CatalogValidationException($"{path}: ability {ability.Id.Value} ({ability.Name}) has unregistered consumer");
                foreach (var cost in ability.Costs) if (!CatalogRegistries.TryAttribute(cost.Resource)) throw new CatalogValidationException($"{path}: ability {ability.Id.Value} ({ability.Name}) has unregistered cost attribute");
                foreach (var tag in ability.RequiredTags) if (!CatalogRegistries.TryTag(tag)) throw new CatalogValidationException($"{path}: ability {ability.Id.Value} ({ability.Name}) has unregistered required tag");
                foreach (var tag in ability.BlockedTags) if (!CatalogRegistries.TryTag(tag)) throw new CatalogValidationException($"{path}: ability {ability.Id.Value} ({ability.Name}) has unregistered blocked tag");
                foreach (var execution in ability.Executions) if ((uint)execution.Value >= (uint)catalog.Executions.Count) throw new CatalogValidationException($"{path}: ability {ability.Id.Value} references missing execution {execution.Value}");
                foreach (var effect in ability.Effects) if ((uint)effect.Value >= (uint)catalog.Effects.Count) throw new CatalogValidationException($"{path}: ability {ability.Id.Value} references missing effect {effect.Value}");
                foreach (var trigger in ability.TriggerRefs) if ((uint)trigger.Value >= (uint)catalog.Triggers.Count) throw new CatalogValidationException($"{path}: ability {ability.Id.Value} ({ability.Name}) references missing trigger {trigger.Value}");
            }
            if (catalog.AbilityDefinitions == null || catalog.AbilityDefinitions.Count != catalog.Abilities.Count)
                throw new CatalogValidationException($"{path}: ability definition/reference count mismatch");
            if (catalog.Targetings == null || catalog.Targetings.Count != catalog.Abilities.Count)
                throw new CatalogValidationException($"{path}: targeting definition/reference count mismatch");
            foreach (var targeting in catalog.Targetings)
            {
                if (targeting.Range < 0 || targeting.Width < 0 || targeting.Height < 0)
                    throw new CatalogValidationException($"{path}: invalid targeting range/size");
            }
            if (catalog.Effects != null)
            {
                var effectIds = new HashSet<int>();
                foreach (var effect in catalog.Effects)
                {
                    if (!effectIds.Add(effect.Id.Value)) throw new CatalogValidationException($"{path}: duplicate effect id {effect.Id.Value}");
                    if (effect.Duration < 0 || effect.Period < 0 || effect.MaxStacks < 1) throw new CatalogValidationException($"{path}: invalid duration/period/stack for effect {effect.Id.Value}");
                    if (effect.Type == EffectType.Periodic && effect.Period <= 0) throw new CatalogValidationException($"{path}: periodic effect {effect.Id.Value} requires period > 0");
                    if (!Enum.IsDefined(typeof(ClockId), effect.Clock) || effect.Clock == ClockId.Invalid) throw new CatalogValidationException($"{path}: invalid clock for effect {effect.Id.Value}");
                    if (!CatalogRegistries.TryTag(effect.Tag)) throw new CatalogValidationException($"{path}: effect {effect.Id.Value} has unregistered tag");
                    foreach (var execution in effect.Executions) if ((uint)execution.Value >= (uint)catalog.Executions.Count) throw new CatalogValidationException($"{path}: effect {effect.Id.Value} references missing execution {execution.Value}");
                    foreach (var tag in effect.GrantedTags) if (!CatalogRegistries.TryTag(tag)) throw new CatalogValidationException($"{path}: effect {effect.Id.Value} has unregistered granted tag");
                    foreach (var tag in effect.BlockedTags) if (!CatalogRegistries.TryTag(tag)) throw new CatalogValidationException($"{path}: effect {effect.Id.Value} has unregistered blocked tag");
                    foreach (var modifier in effect.Modifiers) if (!CatalogRegistries.TryAttribute(modifier.Attribute)) throw new CatalogValidationException($"{path}: effect {effect.Id.Value} has unregistered modifier attribute");
                }
            }
            if (catalog.Triggers != null) foreach (var trigger in catalog.Triggers)
            {
                if ((uint)trigger.Id.Value >= (uint)catalog.Triggers.Count || catalog.Triggers[trigger.Id.Value].Id.Value != trigger.Id.Value) throw new CatalogValidationException($"{path}: trigger {trigger.Id.Value} is not contiguous");
                if ((uint)trigger.Effect.Value >= (uint)catalog.Effects.Count) throw new CatalogValidationException($"{path}: trigger {trigger.Id.Value} references missing effect {trigger.Effect.Value}");
                if (!CatalogRegistries.TryConsumer(trigger.Consumer)) throw new CatalogValidationException($"{path}: trigger {trigger.Id.Value} has unregistered consumer");
                foreach (var tag in trigger.FilterTags) if (!CatalogRegistries.TryTag(tag)) throw new CatalogValidationException($"{path}: trigger {trigger.Id.Value} has unregistered filter tag");
            }
            if (catalog.Executions != null) for (int i = 0; i < catalog.Executions.Count; i++)
            {
                var execution = catalog.Executions[i];
                if (execution.Id.Value != i) throw new CatalogValidationException($"{path}: execution id {execution.Id.Value} is not contiguous");
                if (float.IsNaN(execution.Magnitude) || float.IsInfinity(execution.Magnitude) || execution.Magnitude < 0f || float.IsNaN(execution.Duration) || float.IsInfinity(execution.Duration) || execution.Duration < 0f)
                    throw new CatalogValidationException($"{path}: invalid execution magnitude/duration for id {execution.Id.Value}");
            }
            if (catalog.Aliases != null) foreach (var alias in catalog.Aliases) if ((uint)alias.Value.Value >= (uint)catalog.AbilityDefinitions.Count) throw new CatalogValidationException($"{path}: alias '{alias.Key}' references missing ability {alias.Value.Value}");
        }
    }
}
