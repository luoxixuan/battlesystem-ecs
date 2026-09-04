using System;
using System.Collections.Generic;

namespace BattleSystemECS.Core.GAS
{
    public static class CatalogValidator
    {
        private const int MaxCatalogEntries = 4096;

        public static void Validate(GameplayCatalog catalog, string path)
        {
            if (catalog == null) throw new CatalogValidationException($"{path}: catalog is null");
            ValidateCapacity(catalog, path);
            if (catalog.Executions.Count > CatalogRegistries.MaxExecutions) throw new CatalogValidationException($"{path}: execution capacity exceeded");
            var ids = new HashSet<int>();
            for (int i = 0; i < catalog.AbilityDefinitions.Count; i++)
            {
                var ability = catalog.AbilityDefinitions[i];
                if (!ids.Add(ability.Id.Value)) throw new CatalogValidationException($"{path}: duplicate ability id {ability.Id.Value} ({ability.Name})");
                if (ability.Id.Value != i) throw new CatalogValidationException($"{path}: ability id {ability.Id.Value} is not contiguous at index {i}");
                if (!ability.Targeting.Id.Equals(new TargetingId(ability.Id.Value))) throw new CatalogValidationException($"{path}: ability {ability.Id.Value} targeting reference is not closed");
                const GameplayPhaseMask knownPhases = GameplayPhaseMask.Build | GameplayPhaseMask.Wave | GameplayPhaseMask.Intermission;
                if (ability.AllowedPhases == GameplayPhaseMask.None || (ability.AllowedPhases & ~knownPhases) != 0)
                    throw new CatalogValidationException($"{path}: ability {ability.Id.Value} ({ability.Name}) has invalid allowed phases");
                if (!CatalogRegistries.TryExecutor(ability.Executor)) throw new CatalogValidationException($"{path}: ability {ability.Id.Value} ({ability.Name}) has unregistered executor");
                if (!CatalogRegistries.TryConsumer(ability.Consumer)) throw new CatalogValidationException($"{path}: ability {ability.Id.Value} ({ability.Name}) has unregistered consumer");
                foreach (var cost in ability.Costs) if (!CatalogRegistries.TryAttribute(cost.Resource)) throw new CatalogValidationException($"{path}: ability {ability.Id.Value} ({ability.Name}) has unregistered cost attribute");
                foreach (var tag in ability.RequiredTags) if (!CatalogRegistries.TryTag(tag)) throw new CatalogValidationException($"{path}: ability {ability.Id.Value} ({ability.Name}) has unregistered required tag");
                foreach (var tag in ability.BlockedTags) if (!CatalogRegistries.TryTag(tag)) throw new CatalogValidationException($"{path}: ability {ability.Id.Value} ({ability.Name}) has unregistered blocked tag");
                foreach (var execution in ability.Executions) if ((uint)execution.Value >= (uint)catalog.Executions.Count) throw new CatalogValidationException($"{path}: ability {ability.Id.Value} references missing execution {execution.Value}");
                foreach (var effect in ability.Effects) if ((uint)effect.Value >= (uint)catalog.Effects.Count) throw new CatalogValidationException($"{path}: ability {ability.Id.Value} references missing effect {effect.Value}");
                foreach (var trigger in ability.TriggerRefs) if ((uint)trigger.Value >= (uint)catalog.Triggers.Count) throw new CatalogValidationException($"{path}: ability {ability.Id.Value} ({ability.Name}) references missing trigger {trigger.Value}");
                foreach (var modifier in ability.Modifiers) RejectResidualMultiply(modifier, path, $"ability {ability.Id.Value}");
                ValidateModifierProvenance(catalog, ability, path);
            }
            if (catalog.AbilityDefinitions == null || catalog.AbilityDefinitions.Count != catalog.Abilities.Count)
                throw new CatalogValidationException($"{path}: ability definition/reference count mismatch");
            if (catalog.Targetings == null || catalog.Targetings.Count != catalog.Abilities.Count)
                throw new CatalogValidationException($"{path}: targeting definition/reference count mismatch");
            foreach (var targeting in catalog.Targetings)
            {
                if (targeting.Range < 0 || targeting.Width < 0 || targeting.Height < 0)
                    throw new CatalogValidationException($"{path}: invalid targeting range/size");
                foreach (var tag in targeting.RequiredTags)
                    if (!CatalogRegistries.TryTag(tag)) throw new CatalogValidationException($"{path}: targeting {targeting.Id.Value} has unregistered required tag");
                foreach (var tag in targeting.BlockedTags)
                    if (!CatalogRegistries.TryTag(tag)) throw new CatalogValidationException($"{path}: targeting {targeting.Id.Value} has unregistered blocked tag");
            }
            if (catalog.Effects != null)
            {
                var effectIds = new HashSet<int>();
                for (int i = 0; i < catalog.Effects.Count; i++)
                {
                    var effect = catalog.Effects[i];
                    if (!effectIds.Add(effect.Id.Value)) throw new CatalogValidationException($"{path}: duplicate effect id {effect.Id.Value}");
                    if (effect.Id.Value != i) throw new CatalogValidationException($"{path}: effect id {effect.Id.Value} is not contiguous at index {i}");
                    if (float.IsNaN(effect.Duration) || float.IsInfinity(effect.Duration) || float.IsNaN(effect.Period) || float.IsInfinity(effect.Period) || effect.Duration < 0 || effect.Period < 0 || effect.MaxStacks < 1) throw new CatalogValidationException($"{path}: invalid duration/period/stack for effect {effect.Id.Value}");
                    if (effect.Type == EffectType.Periodic && (effect.Period <= 0 || effect.Duration <= 0)) throw new CatalogValidationException($"{path}: periodic effect {effect.Id.Value} requires finite duration and period > 0");
                    if (effect.Type == EffectType.Instant && effect.DurationPolicy != DurationPolicy.Instant) throw new CatalogValidationException($"{path}: instant effect {effect.Id.Value} has incompatible duration policy");
                    if (effect.Type == EffectType.Duration && (effect.DurationPolicy != DurationPolicy.Duration && effect.DurationPolicy != DurationPolicy.Infinite || effect.Periodic.HasValue)) throw new CatalogValidationException($"{path}: duration effect {effect.Id.Value} has incompatible duration policy/spec");
                    if (effect.Type == EffectType.Periodic && (effect.DurationPolicy != DurationPolicy.Duration || !effect.Periodic.HasValue)) throw new CatalogValidationException($"{path}: periodic effect {effect.Id.Value} has incompatible duration policy/spec");
                    if (effect.Type == EffectType.Periodic && effect.Periodic.HasValue
                        && effect.Periodic.Value.Payload != EffectPayloadKind.GameplayEvent
                        && (effect.Periodic.Value.Magnitude <= 0f
                            || float.IsNaN(effect.Periodic.Value.Magnitude)
                            || float.IsInfinity(effect.Periodic.Value.Magnitude)))
                        throw new CatalogValidationException($"{path}: periodic effect {effect.Id.Value} requires magnitude > 0");
                    if (!Enum.IsDefined(typeof(ClockId), effect.Clock) || effect.Clock == ClockId.Invalid) throw new CatalogValidationException($"{path}: invalid clock for effect {effect.Id.Value}");
                    if (!CatalogRegistries.TryTag(effect.Tag)) throw new CatalogValidationException($"{path}: effect {effect.Id.Value} has unregistered tag");
                    foreach (var execution in effect.Executions) if ((uint)execution.Value >= (uint)catalog.Executions.Count) throw new CatalogValidationException($"{path}: effect {effect.Id.Value} references missing execution {execution.Value}");
                    foreach (var tag in effect.GrantedTags) if (!CatalogRegistries.TryTag(tag)) throw new CatalogValidationException($"{path}: effect {effect.Id.Value} has unregistered granted tag");
                    foreach (var tag in effect.BlockedTags) if (!CatalogRegistries.TryTag(tag)) throw new CatalogValidationException($"{path}: effect {effect.Id.Value} has unregistered blocked tag");
                    foreach (var modifier in effect.Modifiers)
                    {
                        if (!CatalogRegistries.TryAttribute(modifier.Attribute)) throw new CatalogValidationException($"{path}: effect {effect.Id.Value} has unregistered modifier attribute");
                        RejectResidualMultiply(modifier, path, $"effect {effect.Id.Value}");
                    }
                }
            }
            for (int i = 0; i < CatalogRegistries.TagCount; i++)
                if (!CatalogRegistries.TryTag(new TagId(i))) throw new CatalogValidationException($"{path}: tag id {i} is not contiguous");
            if (catalog.Triggers != null) foreach (var trigger in catalog.Triggers)
            {
                if ((uint)trigger.Id.Value >= (uint)catalog.Triggers.Count || catalog.Triggers[trigger.Id.Value].Id.Value != trigger.Id.Value) throw new CatalogValidationException($"{path}: trigger {trigger.Id.Value} is not contiguous");
                if ((uint)trigger.Effect.Value >= (uint)catalog.Effects.Count) throw new CatalogValidationException($"{path}: trigger {trigger.Id.Value} references missing effect {trigger.Effect.Value}");
                if (!CatalogRegistries.TryConsumer(trigger.Consumer)) throw new CatalogValidationException($"{path}: trigger {trigger.Id.Value} has unregistered consumer");
                if (trigger.Threshold <= 0 || trigger.EffectStackDelta <= 0) throw new CatalogValidationException($"{path}: trigger {trigger.Id.Value} requires positive threshold and effect stack delta");
                foreach (var tag in trigger.FilterTags) if (!CatalogRegistries.TryTag(tag)) throw new CatalogValidationException($"{path}: trigger {trigger.Id.Value} has unregistered filter tag");
            }
            if (catalog.Executions != null) for (int i = 0; i < catalog.Executions.Count; i++)
            {
                var execution = catalog.Executions[i];
                if (execution.Id.Value != i) throw new CatalogValidationException($"{path}: execution id {execution.Id.Value} is not contiguous");
                if (float.IsNaN(execution.Magnitude) || float.IsInfinity(execution.Magnitude) || execution.Magnitude < 0f || float.IsNaN(execution.Duration) || float.IsInfinity(execution.Duration) || execution.Duration < 0f)
                    throw new CatalogValidationException($"{path}: invalid execution magnitude/duration for id {execution.Id.Value}");
                if (float.IsNaN(execution.Probability) || float.IsInfinity(execution.Probability) ||
                    execution.Probability < 0f || execution.Probability > 1f)
                    throw new CatalogValidationException($"{path}: invalid execution probability for id {execution.Id.Value}");
                if (execution.Payload == EffectPayloadKind.Telegraph &&
                    (execution.Duration <= 0f || execution.Parameter < 0 || execution.Parameter > 2))
                    throw new CatalogValidationException($"{path}: invalid telegraph duration/color for execution {execution.Id.Value}");
                if (execution.Operation == ExecutionOperation.SummonEnemy &&
                    (execution.Payload != EffectPayloadKind.WorldAction || execution.Magnitude <= 0f || execution.Duration <= 0f))
                    throw new CatalogValidationException($"{path}: summon execution {execution.Id.Value} requires positive health and damage multipliers");
                if (execution.Payload == EffectPayloadKind.Damage && execution.Operation == ExecutionOperation.ApplyDamage &&
                    (execution.SemanticStacking != StackingBehavior.None || execution.SemanticMaxStacks != 0))
                    throw new CatalogValidationException($"{path}: direct damage execution {execution.Id.Value} has an invalid stack contract");
                if (!ProductionAbilityPayloadRegistry.Supports(execution))
                    throw new CatalogValidationException($"{path}: execution {execution.Id.Value} has no production handler for {execution.Payload}/{execution.Operation}");
            }
            if (catalog.Modifiers != null)
            {
                for (int i = 0; i < catalog.Modifiers.Count; i++)
                    RejectResidualMultiply(catalog.Modifiers[i], path, $"catalog modifier {i}");
            }
            if (catalog.Aliases != null) foreach (var alias in catalog.Aliases) if ((uint)alias.Value.Value >= (uint)catalog.AbilityDefinitions.Count) throw new CatalogValidationException($"{path}: alias '{alias.Key}' references missing ability {alias.Value.Value}");
            for (int i = 0; i < catalog.Abilities.Count; i++)
            {
                var legacy = catalog.Abilities[i];
                if (legacy.Id.Value != i || legacy.Id.Value != catalog.AbilityDefinitions[i].Id.Value || !string.Equals(legacy.Name, catalog.AbilityDefinitions[i].Name, StringComparison.OrdinalIgnoreCase))
                    throw new CatalogValidationException($"{path}: legacy ability mapping is not closed at index {i}");
            }
        }

        private static void RejectResidualMultiply(ModifierDefinition modifier, string path, string location)
        {
            if (modifier.Operation == AttributeModifierOp.Multiply)
                throw new CatalogValidationException($"{path}: {location} residual Multiply is rejected; use Percent");
        }

        private static void ValidateCapacity(GameplayCatalog catalog, string path)
        {
            if (catalog.Abilities.Count > MaxCatalogEntries || catalog.AbilityDefinitions.Count > MaxCatalogEntries || catalog.Targetings.Count > MaxCatalogEntries || catalog.Effects.Count > MaxCatalogEntries || catalog.Executions.Count > MaxCatalogEntries || catalog.Triggers.Count > MaxCatalogEntries || catalog.Modifiers.Count > MaxCatalogEntries)
                throw new CatalogValidationException($"{path}: catalog exceeds capacity {MaxCatalogEntries}");
        }

        private static void ValidateModifierProvenance(GameplayCatalog catalog, AbilityDefinition ability, string path)
        {
            for (int i = 0; i < ability.SourceModifiers.Count; i++)
            {
                var semantic = ability.SourceModifiers[i];
                bool matched = false;
                CatalogRegistries.TryTag("Freeze", out var freezeTag);
                if (semantic.Targeting == ability.Targeting.Shape)
                {
                    bool directDamageAbsentStack = semantic.Operation == ExecutionOperation.ApplyDamage &&
                        semantic.Payload == EffectPayloadKind.Damage &&
                        string.Equals(semantic.Type, "Damage", StringComparison.OrdinalIgnoreCase) &&
                        semantic.Stacking == StackingBehavior.None && semantic.MaxStacks == 0;
                    if (semantic.Operation == ExecutionOperation.ApplyDamage &&
                        semantic.Payload == EffectPayloadKind.Damage &&
                        string.Equals(semantic.Type, "Damage", StringComparison.OrdinalIgnoreCase) && !directDamageAbsentStack)
                        throw new CatalogValidationException($"{path}: ability {ability.Id.Value} modifier provenance {i} is not closed");
                    for (int j = 0; j < ability.Executions.Count && !matched; j++)
                    {
                        var execution = catalog.Executions[ability.Executions[j].Value];
                        matched = execution.Payload == semantic.Payload && execution.Operation == semantic.Operation &&
                                  (semantic.Operation == ExecutionOperation.ApplyFreeze
                                      ? execution.Tag.Equals(CatalogRegistries.SkillTag) && semantic.Tag.Equals(freezeTag)
                                      : execution.Tag.Equals(semantic.Tag)) &&
                                  Math.Abs(execution.Magnitude - semantic.NormalizedMagnitude) <= 0.0001f &&
                                  Math.Abs(execution.Probability - semantic.Probability) <= 0.0001f &&
                                  (semantic.Operation == ExecutionOperation.ApplyFreeze
                                      ? Math.Abs(execution.Duration - semantic.Duration) <= 0.0001f &&
                                        execution.SemanticStacking == semantic.Stacking &&
                                        execution.SemanticMaxStacks == semantic.MaxStacks &&
                                        semantic.Stacking != StackingBehavior.None && semantic.MaxStacks > 0
                                      : execution.SemanticStacking == StackingBehavior.None &&
                                        execution.SemanticMaxStacks == 0);
                    }
                    for (int j = 0; j < ability.Effects.Count && !matched; j++)
                    {
                        var effect = catalog.Effects[ability.Effects[j].Value];
                        if (effect.Payload != semantic.Payload || !effect.Tag.Equals(semantic.Tag) ||
                            Math.Abs(effect.Duration - semantic.Duration) > 0.0001f ||
                            effect.Stacking != semantic.Stacking || effect.MaxStacks != semantic.MaxStacks) continue;
                        for (int k = 0; k < effect.Executions.Count && !matched; k++)
                        {
                            var execution = catalog.Executions[effect.Executions[k].Value];
                            matched = execution.Payload == semantic.Payload && execution.Operation == semantic.Operation &&
                                      execution.Tag.Equals(semantic.Tag) &&
                                      Math.Abs(execution.Magnitude - semantic.NormalizedMagnitude) <= 0.0001f &&
                                      Math.Abs(execution.Probability - semantic.Probability) <= 0.0001f;
                        }
                    }
                }
                if (!matched)
                    throw new CatalogValidationException($"{path}: ability {ability.Id.Value} modifier provenance {i} is not closed");
            }
        }
    }
}
