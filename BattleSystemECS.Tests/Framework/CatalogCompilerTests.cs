using System;
using System.IO;
using System.Linq;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class CatalogCompilerTests
    {
        [Fact]
        public void CanonicalSkillsCompileWithStableIds()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Data", "Configs", "skills.json");
            if (!File.Exists(path)) path = Path.Combine(Directory.GetCurrentDirectory(), "Data", "Configs", "skills.json");
            var catalog = CatalogCompiler.Compile(path);
            Assert.NotEmpty(catalog.Abilities);
            Assert.Equal(0, catalog.Abilities[0].Id.Value);
            string configDir = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("config path has no directory");
            string dataDir = Path.GetDirectoryName(configDir) ?? throw new InvalidOperationException("config directory has no parent");
            string staticRoot = Path.Combine(dataDir, "Skills");
            string[] staticFiles = Directory.GetFiles(staticRoot, "*.json");
            var merged = CatalogCompiler.Compile(path, staticFiles);
            Assert.True(merged.Abilities.Count >= catalog.Abilities.Count);
            Assert.Equal(merged.Abilities.Count, merged.AbilityDefinitions.Count);
            Assert.NotEmpty(merged.Effects);
            Assert.NotEmpty(merged.AbilityDefinitions[0].Executions);
            Assert.True(merged.TryResolveAlias("Poison Nova", out var poisonId));
            Assert.True(merged.TryGetAbility(poisonId, out var poison));
            Assert.Single(poison.Effects);
            Assert.True(merged.TryGetEffect(poison.Effects[0], out var poisonEffect));
            Assert.True(poisonEffect.Duration > 0f);
            Assert.True(poisonEffect.Period > 0f);
            Assert.Equal(new TagId(7), poisonEffect.Tag);
            var poisonTick = merged.Executions[poisonEffect.Executions[0].Value];
            Assert.True(poisonTick.Magnitude > 0f);
            Assert.True(poisonEffect.Periodic.HasValue);
            Assert.Equal(poisonTick.Magnitude, poisonEffect.Periodic.Value.Magnitude);
            Assert.True(merged.TryResolveAlias("Cold Nova", out var coldId));
            Assert.True(merged.TryGetAbility(coldId, out var cold));
            Assert.Empty(cold.Effects);
            var coldFreeze = Assert.Single(cold.Executions.Select(id => merged.Executions[id.Value]),
                execution => execution.Operation == ExecutionOperation.ApplyFreeze);
            Assert.Equal(EffectPayloadKind.Freeze, coldFreeze.Payload);
            Assert.Equal(2f, coldFreeze.Duration);
            Assert.Equal(0.3f, coldFreeze.Probability);
            Assert.True(merged.TryResolveAlias("Meteor Strike", out var meteorId));
            Assert.True(merged.TryGetAbility(meteorId, out var meteor));
            Assert.Single(meteor.Effects);
            Assert.Equal(2, meteor.Executions.Count);
            Assert.Contains(meteor.Executions, executionId => merged.Executions[executionId.Value].MagnitudeSource == MagnitudeSource.Multiplier && merged.Executions[executionId.Value].Magnitude > 0f);
            Assert.Contains(meteor.Executions, executionId => merged.Executions[executionId.Value].MagnitudeSource == MagnitudeSource.Constant && merged.Executions[executionId.Value].Magnitude > 0f);
            Assert.True(merged.TryGetEffect(meteor.Effects[0], out var meteorDot));
            var meteorTick = merged.Executions[meteorDot.Executions[0].Value];
            Assert.True(meteorTick.Magnitude > 0f);
            Assert.True(meteorDot.Periodic.HasValue);
            Assert.Equal(meteorTick.Magnitude, meteorDot.Periodic.Value.Magnitude);
            Assert.Equal(2, meteorDot.MaxStacks);
            Assert.True(merged.TryResolveAlias("Cross Slash", out var crossId));
            Assert.True(merged.TryGetAbility(crossId, out var cross));
            Assert.Contains(cross.Executions, executionId => merged.Executions[executionId.Value].Magnitude == 40f && merged.Executions[executionId.Value].MagnitudeSource == MagnitudeSource.Constant);
            Assert.Equal(3, cross.Targeting.Width);
            Assert.True(merged.TryResolveAlias("Chain Heal", out var chainHealId));
            Assert.True(merged.TryGetAbility(chainHealId, out var chainHeal));
            Assert.Equal(2, chainHeal.Executions.Count);
            Assert.True(merged.TryGetExecution(chainHeal.Executions[1], out var chainShield));
            Assert.Equal(EffectPayloadKind.Shield, chainShield.Payload);
            Assert.Equal(15f, chainShield.Magnitude);
            Assert.Equal(3f, chainShield.Duration);
            Assert.True(merged.TryResolveAlias("Energy Shield", out var shieldId));
            Assert.True(merged.TryGetAbility(shieldId, out var shieldAbility));
            Assert.True(merged.TryGetExecution(shieldAbility.Executions[0], out var shieldExecution));
            Assert.Equal(50f, shieldExecution.Magnitude);
            Assert.Equal(5f, shieldExecution.Duration);
            Assert.False(merged.TryGetAbility(new AbilityId(-1), out _));
            Assert.False(merged.TryGetEffect(new EffectId(-1), out _));
            Assert.False(merged.TryGetExecution(new ExecutionId(-1), out _));
            foreach (var definition in catalog.AbilityDefinitions)
                Assert.True(definition.Executions.Count > 0 || definition.Effects.Count > 0, definition.Name);
            Assert.NotEmpty(LegacySkillImporter.ImportAliases(new[] { "legacy_skill" }, "legacy.json"));
        }

        [Fact]
        public void StrictBootstrapValidatesCanonicalAndStaticSkillsBeforeLegacyLoad()
        {
            var config = BattleSystemECS.Config.GameConfigLoader.LoadStrictCatalog(new MockRenderer());
            Assert.NotEmpty(config.SkillDefs);
            Assert.NotNull(config.CompiledCatalog);
            Assert.NotEmpty(config.CompiledCatalog!.AbilityDefinitions);
            Assert.NotEmpty(config.CompiledCatalog.Effects);
        }

        [Fact]
        public void RuntimeExtensionsCompileStableEffectAndTriggerIds()
        {
            var path = Path.Combine(Directory.GetCurrentDirectory(), "Data", "Configs", "skills.json");
            var baseCatalog = CatalogCompiler.Compile(path);
            var extended = CatalogCompiler.CompileRuntimeExtensions(baseCatalog, new RuntimeCatalogSpec(0.2f, 2f, 3));
            var repeated = CatalogCompiler.CompileRuntimeExtensions(extended, new RuntimeCatalogSpec(0.9f, 9f, 99));
            Assert.Same(extended, repeated);
            Assert.Equal(baseCatalog.Effects.Count, extended.Effects[extended.Effects.Count - 1].Id.Value);
            Assert.Equal(baseCatalog.Triggers.Count, extended.Triggers[extended.Triggers.Count - 1].Id.Value);
            Assert.Equal(CatalogRegistries.DamageOutputMultiplier, extended.Effects[extended.Effects.Count - 1].Modifiers[0].Attribute);
            Assert.Equal(AttributeModifierOp.Add, extended.Effects[extended.Effects.Count - 1].Modifiers[0].Operation);
            Assert.Equal(0.2f, extended.Effects[extended.Effects.Count - 1].Modifiers[0].Magnitude);
            Assert.Equal(3, extended.Triggers[extended.Triggers.Count - 1].Threshold);
        }

        [Fact]
        public void RuntimeExtensionsRejectInvalidThresholdAndMultiplier()
        {
            var path = Path.Combine(Directory.GetCurrentDirectory(), "Data", "Configs", "skills.json");
            var catalog = CatalogCompiler.Compile(path);
            Assert.Throws<CatalogValidationException>(() => CatalogCompiler.CompileRuntimeExtensions(catalog, new RuntimeCatalogSpec(0.1f, 2f, 0)));
            Assert.Throws<CatalogValidationException>(() => CatalogCompiler.CompileRuntimeExtensions(catalog, new RuntimeCatalogSpec(-0.1f, 2f, 1)));
        }

        [Fact]
        public void StaticStaticAliasConflictFailsFastWhileCanonicalWins()
        {
            string canonical = Path.Combine(AppContext.BaseDirectory, "Data", "Configs", "skills.json");
            if (!File.Exists(canonical)) canonical = Path.Combine(Directory.GetCurrentDirectory(), "Data", "Configs", "skills.json");
            string root = Path.Combine(Path.GetTempPath(), "catalog-static-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string json = "{\"Name\":\"Static Conflict\",\"AttackRange\":1,\"AreaWidth\":1,\"AreaHeight\":1,\"Cooldown\":1,\"DamageMultiplier\":1,\"ManaCost\":1}";
            string first = Path.Combine(root, "a.json");
            string second = Path.Combine(root, "b.json");
            File.WriteAllText(first, json);
            File.WriteAllText(second, json);
            try { Assert.Throws<CatalogValidationException>(() => CatalogCompiler.Compile(canonical, new[] { first, second })); }
            finally { Directory.Delete(root, true); }
        }

        [Theory]
        [InlineData("[\"missing-tag\"]", "unknown tag")]
        [InlineData("[\"Fire\",\"Fire\"]", "duplicate tag")]
        public void StrictTagCompilationRejectsUnknownAndDuplicateIds(string tags, string expected)
        {
            string path = Path.Combine(Path.GetTempPath(), "catalog-tags-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, "[{\"Name\":\"tagged\",\"AreaShape\":\"shield\",\"AttackRange\":0," +
                "\"AreaWidth\":1,\"AreaHeight\":1,\"Cooldown\":1,\"ShieldAmount\":1," +
                "\"ShieldDuration\":1,\"AllowedPhases\":[\"Build\",\"Wave\"],\"RequiredTags\":" + tags + ",\"Modifiers\":[]}]");
            try
            {
                var error = Assert.Throws<CatalogValidationException>(() => CatalogCompiler.Compile(path));
                Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(path, error.Message, StringComparison.Ordinal);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void StrictGrantedTagsCompilationRejectsUnknownIds()
        {
            string path = Path.Combine(Path.GetTempPath(), "catalog-granted-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, "[{\"Name\":\"granted\",\"AreaShape\":\"single\",\"AttackRange\":1," +
                "\"AreaWidth\":1,\"AreaHeight\":1,\"Cooldown\":1,\"Modifiers\":[{\"Name\":\"x\",\"Type\":\"Debuff\"," +
                "\"EffectTag\":\"Normal\",\"StackingType\":\"None\",\"Value\":1,\"Duration\":1," +
                "\"GrantedTags\":[\"missing-granted-tag\"]}]}]");
            try
            {
                var error = Assert.Throws<CatalogValidationException>(() => CatalogCompiler.Compile(path));
                Assert.Contains("unknown tag", error.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(path, error.Message, StringComparison.Ordinal);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void KnownHierarchyTagNameCompilesIntoRequiredTags()
        {
            string path = Path.Combine(Path.GetTempPath(), "catalog-stun-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, "[{\"Name\":\"needs-stun\",\"AreaShape\":\"shield\",\"AttackRange\":0," +
                "\"AreaWidth\":1,\"AreaHeight\":1,\"Cooldown\":1,\"ShieldAmount\":1,\"ShieldDuration\":1," +
                "\"AllowedPhases\":[\"Build\",\"Wave\"],\"RequiredTags\":[\"Stun\"],\"Modifiers\":[]}]");
            try
            {
                var catalog = CatalogCompiler.Compile(path);
                Assert.Equal(CatalogRegistries.StunTag, catalog.AbilityDefinitions[0].RequiredTags[0]);
            }
            finally { File.Delete(path); }
        }
    }
}
