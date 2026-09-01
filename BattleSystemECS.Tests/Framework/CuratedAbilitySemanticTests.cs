using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using BattleSystemECS.Config;
using BattleSystemECS.Core.GAS;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class CuratedAbilitySemanticTests
    {
        [Fact]
        public void TwentyCuratedAbilitiesRetainLegacyPayloadSemantics()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Data", "Configs", "skills.json");
            if (!File.Exists(path)) path = Path.Combine(Directory.GetCurrentDirectory(), "Data", "Configs", "skills.json");
            string json = File.ReadAllText(path);
            var configured = JsonSerializer.Deserialize<SkillConfig[]>(json) ?? Array.Empty<SkillConfig>();
            var catalog = CatalogCompiler.Compile(path);
            Assert.Equal(configured.Length, catalog.AbilityDefinitions.Count);
            for (int i = 0; i < configured.Length; i++)
            {
                SkillConfig source = configured[i];
                AbilityDefinition compiled = catalog.AbilityDefinitions[i];
                Assert.Equal(source.Name, compiled.Name);
                Assert.Equal(i, compiled.Id.Value);
                Assert.Equal(i, compiled.Targeting.Id.Value);
                Assert.Equal(AreaShapeType.FromString(source.AreaShape), catalog.Abilities[i].AreaShape);
                Assert.Equal(source.AttackRange, compiled.Targeting.Range);
                Assert.Equal(source.AreaWidth, compiled.Targeting.Width);
                Assert.Equal(source.AreaHeight, compiled.Targeting.Height);
                Assert.Equal(source.AreaRadius, compiled.Targeting.Radius);
                Assert.Equal(source.Cooldown, compiled.Cooldown);
                Assert.True(catalog.TryResolveAlias(source.Name, out var alias));
                Assert.Equal(compiled.Id, alias);
                Assert.True(compiled.Executions.Count > 0 || compiled.Effects.Count > 0, source.Name);
            }

            AssertMultiplier(catalog, "Cross Slash", 4f);
            AssertMultiplier(catalog, "Mega Explosion", 3f);
            AssertMultiplier(catalog, "Sniper Shot", 6f);
            AssertMultiplier(catalog, "Chain Lightning", 5f);
            AssertMultiplier(catalog, "Laser Beam", 3f);
            AssertMultiplier(catalog, "Cold Nova", 2f);
            AssertMultiplier(catalog, "Dragon Breath", 3f);
            AssertMultiplier(catalog, "Plasma Cannon", 5f);
            AssertMultiplier(catalog, "Artillery Strike", 6f);
            AssertMultiplier(catalog, "Meteor Strike", 8f);
            AssertMultiplier(catalog, "Slow Nova", 2f);

            AssertPayload(catalog, "Guardian Heal", EffectPayloadKind.Heal, 0.3f, 0f, MagnitudeSource.Constant);
            AssertPayload(catalog, "Chain Heal", EffectPayloadKind.Heal, 0.25f, 0f, MagnitudeSource.Constant);
            AssertPayload(catalog, "Chain Heal", EffectPayloadKind.Shield, 15f, 3f, MagnitudeSource.Constant);
            AssertPayload(catalog, "Mass Resurrect", EffectPayloadKind.Resurrect, 0.3f, 0f, MagnitudeSource.Constant);
            AssertPayload(catalog, "War Stomp", EffectPayloadKind.CrowdControl, 2f, 0f, MagnitudeSource.Constant);
            AssertPayload(catalog, "Earthroot", EffectPayloadKind.CrowdControl, 3f, 0f, MagnitudeSource.Constant);
            AssertPayload(catalog, "Shockwave", EffectPayloadKind.CrowdControl, 80f, 0f, MagnitudeSource.Constant);
            AssertPayload(catalog, "Energy Shield", EffectPayloadKind.Shield, 50f, 5f, MagnitudeSource.Constant);
            AssertPayload(catalog, "Slow Nova", EffectPayloadKind.Slow, 0.5f, 3f, MagnitudeSource.Constant);
            AssertPayload(catalog, "Time Rewind", EffectPayloadKind.Resource, 3f, 0f, MagnitudeSource.Constant, ExecutionOperation.RestoreSnapshot);

            AssertPeriodic(catalog, "Poison Nova", 8f, 5f, 1f);
            AssertPeriodic(catalog, "Dragon Breath", 5f, 3f, 1f);
            AssertPeriodic(catalog, "Meteor Strike", 4f, 3f, 1f);
            var cold = Find(catalog, "Cold Nova");
            Assert.Contains(cold.Executions, id =>
            {
                var execution = catalog.Executions[id.Value];
                return execution.Payload == EffectPayloadKind.Freeze &&
                    execution.Operation == ExecutionOperation.ApplyFreeze &&
                    execution.Magnitude == 2f && execution.Probability == 0.3f;
            });
        }

        private static void AssertMultiplier(GameplayCatalog catalog, string name, float value)
        {
            var ability = Find(catalog, name);
            Assert.Contains(ability.Executions, id => catalog.Executions[id.Value].Magnitude == value && catalog.Executions[id.Value].MagnitudeSource == MagnitudeSource.Multiplier && catalog.Executions[id.Value].Stage == DamageAmountStage.LegacyMultiplier);
        }

        private static void AssertPayload(GameplayCatalog catalog, string name, EffectPayloadKind payload, float magnitude, float duration, MagnitudeSource source, ExecutionOperation operation = ExecutionOperation.Default)
        {
            var ability = Find(catalog, name);
            Assert.Contains(ability.Executions, id => { var e = catalog.Executions[id.Value]; return e.Payload == payload && e.Magnitude == magnitude && e.Duration == duration && e.MagnitudeSource == source && (operation == ExecutionOperation.Default || e.Operation == operation); });
        }

        private static void AssertPeriodic(GameplayCatalog catalog, string name, float magnitude, float duration, float period)
        {
            var ability = Find(catalog, name);
            Assert.Contains(ability.Effects, id => { var e = catalog.Effects[id.Value]; return e.Duration == duration && e.Period == period && e.Executions.Any(x => catalog.Executions[x.Value].Magnitude == magnitude && catalog.Executions[x.Value].MagnitudeSource == MagnitudeSource.Constant); });
        }

        private static AbilityDefinition Find(GameplayCatalog catalog, string name) => catalog.AbilityDefinitions.First(a => a.Name == name);
    }
}
