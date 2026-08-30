using System;
using System.IO;
using System.Linq;
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
            var catalog = CatalogCompiler.Compile(path);
            string[] names = { "Cross Slash", "Mega Explosion", "Sniper Shot", "Poison Nova", "Chain Lightning", "Guardian Heal", "Chain Heal", "Mass Resurrect", "War Stomp", "Earthroot", "Shockwave", "Energy Shield", "Laser Beam", "Cold Nova", "Dragon Breath", "Plasma Cannon", "Artillery Strike", "Meteor Strike", "Slow Nova", "Time Rewind" };
            TargetingShape[] shapes = { TargetingShape.Cross, TargetingShape.Box, TargetingShape.Single, TargetingShape.Circle, TargetingShape.Chain, TargetingShape.Heal, TargetingShape.ChainHeal, TargetingShape.MassResurrect, TargetingShape.AoeStun, TargetingShape.AoeRoot, TargetingShape.AoeKnockback, TargetingShape.Shield, TargetingShape.Line, TargetingShape.Freeze, TargetingShape.Cone, TargetingShape.Cone, TargetingShape.GroundTarget, TargetingShape.GroundTarget, TargetingShape.Slow, TargetingShape.TimeRewind };
            int[] ranges = { 3, 5, 9, 4, 6, 0, 0, 0, 0, 0, 0, 0, 8, 4, 5, 6, 8, 10, 4, 0 };
            int[] widths = { 3, 3, 1, 5, 1, 1, 1, 4, 3, 4, 4, 1, 1, 5, 60, 45, 3, 5, 5, 0 };
            int[] heights = { 3, 3, 1, 5, 1, 1, 1, 4, 3, 4, 4, 1, 1, 5, 5, 6, 3, 5, 5, 0 };
            float[] radii = { 3f, 1f, 9f, 4f, 6f, 0f, 200f, 4f, 200f, 250f, 300f, 0f, 8f, 3f, 4f, 5f, 3f, 5f, 4f, 0f };
            Assert.Equal(names.Length, catalog.AbilityDefinitions.Count);
            for (int i = 0; i < names.Length; i++)
            {
                Assert.Equal(names[i], catalog.AbilityDefinitions[i].Name);
                Assert.Equal(i, catalog.AbilityDefinitions[i].Id.Value);
                Assert.Equal(i, catalog.AbilityDefinitions[i].Targeting.Id.Value);
                Assert.Equal(shapes[i], catalog.AbilityDefinitions[i].Targeting.Shape);
                Assert.Equal(ranges[i], catalog.AbilityDefinitions[i].Targeting.Range);
                Assert.Equal(widths[i], catalog.AbilityDefinitions[i].Targeting.Width);
                Assert.Equal(heights[i], catalog.AbilityDefinitions[i].Targeting.Height);
                Assert.Equal(radii[i], catalog.AbilityDefinitions[i].Targeting.Radius);
                Assert.True(catalog.AbilityDefinitions[i].Executions.Count > 0 || catalog.AbilityDefinitions[i].Effects.Count > 0, names[i]);
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
            Assert.Contains(cold.Effects, id => catalog.Effects[id.Value].Payload == EffectPayloadKind.CrowdControl && catalog.Effects[id.Value].Duration == 2f && catalog.Effects[id.Value].Executions.Any(x => catalog.Executions[x.Value].Magnitude == 0f));
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
