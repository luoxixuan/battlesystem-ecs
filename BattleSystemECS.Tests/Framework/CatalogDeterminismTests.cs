using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Globalization;
using BattleSystemECS.Core.GAS;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class CatalogDeterminismTests
    {
        private static string CanonicalPath() => File.Exists(Path.Combine(AppContext.BaseDirectory, "Data", "Configs", "skills.json")) ? Path.Combine(AppContext.BaseDirectory, "Data", "Configs", "skills.json") : Path.Combine(Directory.GetCurrentDirectory(), "Data", "Configs", "skills.json");

        [Fact]
        public void RepeatedCompilationHasSameFingerprint()
        {
            string path = CanonicalPath();
            string dir = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(path))!, "Skills");
            string[] files = Directory.GetFiles(dir, "*.json");
            var first = CatalogCompiler.Compile(path, files);
            var second = CatalogCompiler.Compile(path, files);
            Assert.Equal(Fingerprint(first), Fingerprint(second));
        }

        [Fact]
        public void ReversedStaticInputHasSameFingerprint()
        {
            string path = CanonicalPath();
            string dir = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(path))!, "Skills");
            string[] files = Directory.GetFiles(dir, "*.json");
            Assert.Equal(Fingerprint(CatalogCompiler.Compile(path, files)), Fingerprint(CatalogCompiler.Compile(path, files.Reverse())));
        }

        [Fact]
        public void DifferentCulturesHaveSameFingerprint()
        {
            var old = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
            try
            {
                string path = CanonicalPath();
                string dir = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(path))!, "Skills");
                string[] files = Directory.GetFiles(dir, "*.json");
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
                string first = Fingerprint(CatalogCompiler.Compile(path, files));
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                string second = Fingerprint(CatalogCompiler.Compile(path, files));
                Assert.Equal(first, second);
            }
            finally { CultureInfo.CurrentCulture = old.Item1; CultureInfo.CurrentUICulture = old.Item2; }
        }

        [Fact]
        public void StaticSkillOnePreservesLegacyFieldsAndMultiplierStage()
        {
            string path = CanonicalPath();
            string dir = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(path))!, "Skills");
            var catalog = CatalogCompiler.Compile(path, new[] { Path.Combine(dir, "skill_001.json") });
            Assert.True(catalog.TryResolveAlias("Circuit Breaker #1", out var id));
            Assert.True(catalog.TryGetAbility(id, out var ability));
            Assert.Equal(3, ability.Targeting.Range);
            Assert.Equal(1, ability.Targeting.Width);
            Assert.Equal(1, ability.Targeting.Height);
            Assert.Equal(20f, ability.Costs[0].Amount);
            Assert.True(catalog.TryGetExecution(ability.Executions[0], out var execution));
            Assert.Equal(0.3f, execution.Magnitude);
            Assert.Equal(MagnitudeSource.Multiplier, execution.MagnitudeSource);
            Assert.Equal(DamageAmountStage.LegacyMultiplier, execution.Stage);
        }

        private static string Fingerprint(GameplayCatalog catalog)
        {
            var text = new StringBuilder();
            text.Append(CatalogRegistries.Version).Append('|');
            foreach (var ability in catalog.AbilityDefinitions) { text.Append(ability.Id.Value).Append('|').Append(ability.Name).Append('|').Append(ability.Clock).Append('|').Append(ability.Cooldown.ToString("R", CultureInfo.InvariantCulture)).Append('|').Append(ability.AllowedPhases).Append('|').Append(ability.Activation).Append('|').Append(ability.Targeting.Id.Value).Append('|').Append(ability.Executor.Value).Append('|').Append(ability.Consumer.Value).Append('|'); AppendCosts(text, ability.Costs); AppendRefs(text, ability.Effects, 'e'); AppendRefs(text, ability.Executions, 'x'); AppendRefs(text, ability.RequiredTags, 'r'); AppendRefs(text, ability.BlockedTags, 'b'); AppendRefs(text, ability.TriggerRefs, 't'); AppendModifiers(text, ability.Modifiers); }
            foreach (var targeting in catalog.Targetings) { text.Append(targeting.Id.Value).Append('|').Append(targeting.Shape).Append('|').Append(targeting.Range).Append('|').Append(targeting.Width).Append('|').Append(targeting.Height).Append('|').Append(targeting.MaxTargets).Append('|').Append(targeting.Radius.ToString("R", CultureInfo.InvariantCulture)).Append('|').Append(targeting.Angle.ToString("R", CultureInfo.InvariantCulture)).Append('|').Append(targeting.Relation).Append('|').Append(targeting.MaxTargetsMode).Append('|'); AppendRefs(text, targeting.RequiredTags, 'r'); AppendRefs(text, targeting.BlockedTags, 'b'); }
            foreach (var execution in catalog.Executions) text.Append(execution.Id.Value).Append('|').Append(execution.Payload).Append('|').Append(execution.Magnitude.ToString("R", CultureInfo.InvariantCulture)).Append('|').Append(execution.Duration.ToString("R", CultureInfo.InvariantCulture)).Append('|').Append(execution.Tag.Value).Append('|').Append(execution.MagnitudeSource).Append('|').Append(execution.Stage).Append('|').Append(execution.Operation).Append('|');
            foreach (var effect in catalog.Effects) { text.Append(effect.Id.Value).Append('|').Append(effect.Type).Append('|').Append(effect.Duration.ToString("R", CultureInfo.InvariantCulture)).Append('|').Append(effect.Clock).Append('|').Append(effect.Stacking).Append('|').Append(effect.RefreshPolicy).Append('|').Append(effect.SourceDeath).Append('|').Append(effect.Payload).Append('|').Append(effect.Tag.Value).Append('|'); AppendRefs(text, effect.Executions, 'x'); AppendRefs(text, effect.GrantedTags, 'g'); AppendRefs(text, effect.BlockedTags, 'b'); AppendModifiers(text, effect.Modifiers); if (effect.Periodic.HasValue) text.Append(effect.Periodic.Value.Period.ToString("R", CultureInfo.InvariantCulture)).Append('|').Append(effect.Periodic.Value.FirstTick).Append('|').Append(effect.Periodic.Value.CatchUp).Append('|').Append(effect.Periodic.Value.PayloadExecution.Value).Append('|').Append(effect.Periodic.Value.Damage.HasValue ? effect.Periodic.Value.Damage.Value.ToString() : "unspecified").Append('|').Append(effect.Periodic.Value.Element.HasValue ? effect.Periodic.Value.Element.Value.ToString() : "unspecified").Append('|'); }
            foreach (var trigger in catalog.Triggers) text.Append(trigger.Id.Value).Append('|').Append(trigger.EventType).Append('|').Append(trigger.Effect.Value).Append('|').Append(trigger.Consumer.Value).Append('|').Append(trigger.EffectTag.Value).Append('|');
            foreach (var alias in catalog.Aliases.OrderBy(pair => pair.Key, StringComparer.Ordinal)) text.Append(alias.Key).Append('=').Append(alias.Value.Value).Append('|');
            return text.ToString();
        }
        private static void AppendRefs<T>(StringBuilder text, System.Collections.Generic.IReadOnlyList<T> values, char prefix) { foreach (var value in values) text.Append(prefix).Append(value).Append('|'); }
        private static void AppendCosts(StringBuilder text, System.Collections.Generic.IReadOnlyList<CostDefinition> values) { foreach (var value in values) text.Append(value.Resource.Value).Append(':').Append(value.Amount.ToString("R", CultureInfo.InvariantCulture)).Append('|'); }
        private static void AppendModifiers(StringBuilder text, System.Collections.Generic.IReadOnlyList<ModifierDefinition> values) { foreach (var value in values) text.Append(value.Attribute.Value).Append(':').Append(value.Operation).Append(':').Append(value.Magnitude.ToString("R", CultureInfo.InvariantCulture)).Append(':').Append(value.Priority).Append(':').Append(value.MagnitudeSource).Append(':').Append(value.Snapshot).Append('|'); }
    }
}
