using System;
using System.Collections.Generic;
using BattleSystemECS.Core.GAS;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class CatalogImmutabilityTests
    {
        [Fact]
        public void ConstructorCopiesInputsAndViewsRejectMutation()
        {
            var executions = new[] { new ExecutionDefinition(new ExecutionId(0), EffectPayloadKind.Damage, 4f, new TagId(0)) };
            var ability = new AbilityDefinition(new AbilityId(0), "immutable", new TargetingDefinition(new TargetingId(0), TargetingShape.Single, 1, 1, 1, 1), ClockId.Combat, 1, GameplayPhaseMask.Wave, Array.Empty<EffectId>(), Array.Empty<ModifierDefinition>(), CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer, executions: new[] { new ExecutionId(0) });
            var catalog = new GameplayCatalog(new[] { ability }, new[] { ability.Targeting }, Array.Empty<GameplayEffectDefinition>(), executions, Array.Empty<TriggerDefinition>(), Array.Empty<ModifierDefinition>(), new Dictionary<string, AbilityId> { ["immutable"] = new AbilityId(0) });
            executions[0] = new ExecutionDefinition(new ExecutionId(0), EffectPayloadKind.Damage, 99f, new TagId(0));
            Assert.True(catalog.TryGetExecution(new ExecutionId(0), out var stored));
            Assert.Equal(4f, stored.Magnitude);
            Assert.IsNotType<ExecutionDefinition[]>(catalog.Executions);
            Assert.IsNotType<Dictionary<string, AbilityId>>(catalog.Aliases);
            var list = Assert.IsAssignableFrom<IList<ExecutionDefinition>>(catalog.Executions);
            Assert.Throws<NotSupportedException>(() => list[0] = executions[0]);
        }
    }
}
