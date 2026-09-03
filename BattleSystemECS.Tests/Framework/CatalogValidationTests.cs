using System;
using System.Collections.Generic;
using BattleSystemECS.Core.GAS;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class CatalogValidationTests : IDisposable
    {
        private const string Path = "fixture/catalog.json";
        private readonly string _temp = System.IO.Directory.CreateTempSubdirectory("catalog-validation-").FullName;

        private static GameplayCatalog Make(AbilityDefinition ability, GameplayEffectDefinition[]? effects = null, ExecutionDefinition[]? executions = null)
        {
            return new GameplayCatalog(new[] { ability }, new[] { ability.Targeting }, effects ?? Array.Empty<GameplayEffectDefinition>(), executions ?? Array.Empty<ExecutionDefinition>(), Array.Empty<TriggerDefinition>(), Array.Empty<ModifierDefinition>(), new Dictionary<string, AbilityId> { [ability.Name] = ability.Id });
        }

        private static AbilityDefinition Ability(TargetingId targeting = default(TargetingId), EffectId[]? effects = null, ExecutionId[]? executions = null, ExecutorId executor = default(ExecutorId), ConsumerId consumer = default(ConsumerId))
        {
            return new AbilityDefinition(new AbilityId(0), "fixture-ability", new TargetingDefinition(targeting, TargetingShape.Single, 1, 1, 1, 1), ClockId.Combat, 1, GameplayPhaseMask.Wave, effects ?? Array.Empty<EffectId>(), Array.Empty<ModifierDefinition>(), executor, consumer, executions: executions);
        }

        [Fact] public void RejectsMissingTargetingReference() => AssertInvalid(Make(Ability(new TargetingId(9))), "targeting");
        [Fact] public void RejectsMissingEffectReference() => AssertInvalid(Make(Ability(effects: new[] { new EffectId(4) })), "effect");
        [Fact] public void RejectsMissingExecutionReference() => AssertInvalid(Make(Ability(executions: new[] { new ExecutionId(4) })), "execution");
        [Fact] public void RejectsUnregisteredExecutor() => AssertInvalid(Make(Ability(executor: new ExecutorId(99))), "executor");
        [Fact] public void RejectsUnregisteredConsumer() => AssertInvalid(Make(Ability(consumer: new ConsumerId(99))), "consumer");
        [Fact] public void RejectsNegativeDuration() => AssertInvalid(Make(Ability(), new[] { new GameplayEffectDefinition(new EffectId(0), EffectType.Duration, Array.Empty<ModifierDefinition>(), -1, 0, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent, new TagId(0), Array.Empty<ExecutionId>()) }), "duration");
        [Fact] public void RejectsNegativePeriod() => AssertInvalid(Make(Ability(), new[] { new GameplayEffectDefinition(new EffectId(0), EffectType.Periodic, Array.Empty<ModifierDefinition>(), 1, -1, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.Damage, new TagId(0), Array.Empty<ExecutionId>()) }), "period");
        [Fact] public void RejectsZeroPeriodicPeriod() => AssertInvalid(Make(Ability(), new[] { new GameplayEffectDefinition(new EffectId(0), EffectType.Periodic, Array.Empty<ModifierDefinition>(), 1, 0, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.Damage, new TagId(0), Array.Empty<ExecutionId>()) }), "period");
        [Fact] public void RejectsZeroPeriodicMagnitude() => AssertInvalid(Make(Ability(), new[] { new GameplayEffectDefinition(new EffectId(0), EffectType.Periodic, Array.Empty<ModifierDefinition>(), 1f, 1f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.Damage, new TagId(0), Array.Empty<ExecutionId>(), periodicMagnitude: 0f) }), "magnitude");
        [Fact] public void RejectsInvalidMaxStacks() => AssertInvalid(Make(Ability(), new[] { new GameplayEffectDefinition(new EffectId(0), EffectType.Duration, Array.Empty<ModifierDefinition>(), 1, 0, ClockId.Combat, StackingBehavior.None, 0, RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent, new TagId(0), Array.Empty<ExecutionId>()) }), "stack");
        [Fact] public void RejectsInvalidClock() => AssertInvalid(Make(Ability(), new[] { new GameplayEffectDefinition(new EffectId(0), EffectType.Duration, Array.Empty<ModifierDefinition>(), 1, 0, ClockId.Invalid, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent, new TagId(0), Array.Empty<ExecutionId>()) }), "clock");
        [Fact] public void RejectsNonContiguousExecutionId() => AssertInvalid(new GameplayCatalog(new[] { Ability() }, new[] { Ability().Targeting }, Array.Empty<GameplayEffectDefinition>(), new[] { new ExecutionDefinition(new ExecutionId(2), EffectPayloadKind.Damage, 1, new TagId(0)) }, Array.Empty<TriggerDefinition>(), Array.Empty<ModifierDefinition>(), new Dictionary<string, AbilityId>()), "execution id");
        [Fact] public void RejectsAliasWithoutAbility() => AssertInvalid(new GameplayCatalog(new[] { Ability() }, new[] { Ability().Targeting }, Array.Empty<GameplayEffectDefinition>(), Array.Empty<ExecutionDefinition>(), Array.Empty<TriggerDefinition>(), Array.Empty<ModifierDefinition>(), new Dictionary<string, AbilityId> { ["orphan"] = new AbilityId(4) }), "alias");

        [Fact]
        public void AcceptsClosedTagCostModifierAndTriggerGraph()
        {
            var execution = new ExecutionDefinition(new ExecutionId(0), EffectPayloadKind.Damage, 2f, new TagId(0));
            var modifier = new ModifierDefinition(new AttributeKey(0), AttributeModifierOp.Add, 1f);
            var effect = new GameplayEffectDefinition(new EffectId(0), EffectType.Duration, new[] { modifier }, 1f, 0f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.Damage, new TagId(0), new[] { execution.Id }, new[] { new TagId(0) });
            var ability = new AbilityDefinition(new AbilityId(0), "closed", new TargetingDefinition(new TargetingId(0), TargetingShape.Single, 1, 1, 1, 1, requiredTags: new[] { new TagId(0) }), ClockId.Combat, 1f, GameplayPhaseMask.Wave, new[] { new EffectId(0) }, new[] { modifier }, CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer, executions: new[] { new ExecutionId(0) }, costs: new[] { new CostDefinition(new AttributeKey(0), 1f) }, requiredTags: new[] { new TagId(0) }, triggerRefs: new[] { new TriggerId(0) });
            var trigger = new TriggerDefinition(new TriggerId(0), GameplayEventType.DamageApplied, new EffectId(0), CatalogRegistries.SkillConsumer, new[] { new TagId(0) }, new TagId(0));
            var catalog = new GameplayCatalog(new[] { ability }, new[] { ability.Targeting }, new[] { effect }, new[] { execution }, new[] { trigger }, new[] { modifier }, new Dictionary<string, AbilityId> { ["closed"] = new AbilityId(0) });
            CatalogValidator.Validate(catalog, Path);
            Assert.True(catalog.TryGetTrigger(new TriggerId(0), out _));
        }

        private static void AssertInvalid(GameplayCatalog catalog, string expected)
        {
            var error = Assert.Throws<CatalogValidationException>(() => CatalogValidator.Validate(catalog, Path));
            Assert.Contains(Path, error.Message, StringComparison.Ordinal);
            Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("unknown-shape", "{\"Name\":\"bad\",\"AreaShape\":\"hex\"}", "shape")]
        [InlineData("unknown-type", "{\"Name\":\"bad\",\"AreaShape\":\"single\",\"Modifiers\":[{\"Name\":\"x\",\"Type\":\"Other\",\"EffectTag\":\"Normal\",\"Value\":1}]}", "modifier type")]
        [InlineData("unknown-tag", "{\"Name\":\"bad\",\"AreaShape\":\"single\",\"Modifiers\":[{\"Name\":\"x\",\"Type\":\"Damage\",\"EffectTag\":\"Other\",\"Value\":1}]}", "effect tag")]
        [InlineData("unknown-stacking", "{\"Name\":\"bad\",\"AreaShape\":\"single\",\"Modifiers\":[{\"Name\":\"x\",\"Type\":\"Debuff\",\"EffectTag\":\"Poison\",\"StackingType\":\"Other\",\"Value\":1}]}", "stacking")]
        public void RejectsMalformedJsonWithPathAndId(string name, string json, string expected)
        {
            string file = System.IO.Path.Combine(_temp, name + ".json");
            System.IO.File.WriteAllText(file, "[" + json + "]");
            var error = Assert.Throws<CatalogValidationException>(() => CatalogCompiler.Compile(file));
            Assert.Contains(file, error.Message, StringComparison.Ordinal);
            Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose() { if (System.IO.Directory.Exists(_temp)) System.IO.Directory.Delete(_temp, true); }
    }
}
