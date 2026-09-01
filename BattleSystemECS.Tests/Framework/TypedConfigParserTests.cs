using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BattleSystemECS.Config;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class TypedConfigParserTests
    {
        [Fact]
        public void ProductionParserPreservesLegacyMainConfigurationGolden()
        {
            string json = File.ReadAllText("game_config.json");
            var typed = TypedGameConfigParser.ParseProduction(json, "game_config.json");
            var legacy = GameConfigLoader.LoadConfig(new MockRenderer());

            Assert.Equal(legacy.MonsterTypes.Count, typed.MonsterTypes.Count);
            Assert.Equal(legacy.TowerTypes.Count, typed.TowerTypes.Count);
            Assert.Equal(legacy.Skills.Count, typed.Skills.Count);
            Assert.Equal(legacy.Levels.Count, typed.Levels.Count);
            Assert.Equal(legacy.Combo.TriggerThreshold, typed.Combo.TriggerThreshold);
            Assert.Equal(legacy.TowerOvercharge.ManaCost, typed.TowerOvercharge.ManaCost);
            Assert.Equal(legacy.PositionalDamage.Enabled, typed.PositionalDamage.Enabled);
            Assert.Equal(legacy.Skills[0].HealPercent, typed.Skills[0].HealPercent);
            for (int i = 0; i < legacy.Skills.Count; i++)
                Assert.Equal(JsonSerializer.Serialize(legacy.Skills[i]), JsonSerializer.Serialize(typed.Skills[i]));
        }

        [Fact]
        public void ProductionParserLoadsNewAbilityAndTowerFields()
        {
            var config = TypedGameConfigParser.ParseProduction(MinimalJson(
                "\"AreaShape\":\"shield\",\"AreaRadius\":4,\"HealPercent\":0.25,\"ShieldAmount\":12,\"ShieldDuration\":3",
                "\"ActiveSkillId\":7,\"ActiveCooldown\":9",
                "\"triggerThreshold\":6"), "memory.json");
            var enemy = TypedGameConfigParser.ParseEnemyAbilities(
                "[{\"Id\":\"control\",\"Name\":\"Control\",\"AbilityType\":\"silence_tower\",\"Cooldown\":2,\"SilenceRadius\":5,\"SilenceDuration\":3,\"DispelRadius\":4,\"DispelDuration\":2,\"DispelImmunityDuration\":1,\"CastTime\":7}]",
                "enemy.json");
            var unknown = Assert.Throws<CatalogValidationException>(() => TypedGameConfigParser.ParseEnemyAbilities(
                "[{\"Id\":\"bad\",\"Name\":\"Bad\",\"AbilityType\":\"self_heal\",\"Unexpected\":1}]", "enemy.json"));

            Assert.Equal(6, config.Combo.TriggerThreshold);
            Assert.Equal(7, config.TowerTypes[0].ActiveSkillId);
            Assert.Equal(9f, config.TowerTypes[0].ActiveCooldown);
            Assert.Equal(4, config.Skills[0].AreaRadius);
            Assert.Equal(0.25f, config.Skills[0].HealPercent);
            Assert.Equal(12f, config.Skills[0].ShieldAmount);
            Assert.Equal(5f, enemy[0].SilenceRadius);
            Assert.Equal(4f, enemy[0].DispelRadius);
            Assert.Equal(0f, enemy[0].CastTime);
            Assert.Contains("$[0].Unexpected", unknown.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void BooleanAutoCastRemainsInactiveUntilAbilityBehaviorCutoverIsAuthorized()
        {
            var config = TypedGameConfigParser.ParseProduction(MinimalJson(
                "\"AreaShape\":\"heal\",\"AutoCast\":true", "", "\"triggerThreshold\":1"), "compat.json");

            Assert.False(config.Skills[0].AutoCast);
        }

        [Theory]
        [InlineData("\"AreaShape\":\"not-a-shape\"", "$.Skills[0].AreaShape", 1)]
        [InlineData("\"AreaShape\":\"single\",\"Cooldown\":-1", "$.Skills[0].Cooldown", 1)]
        [InlineData("\"AreaShape\":\"single\"", "$.Combo.triggerThreshold", 0)]
        public void ProductionParserRejectsUnknownAndOutOfRangeFields(string skillFields, string expectedPath, int threshold)
        {
            var error = Assert.Throws<CatalogValidationException>(() =>
                TypedGameConfigParser.ParseProduction(MinimalJson(skillFields, "", "\"triggerThreshold\":" + threshold), "invalid.json"));

            Assert.Contains("invalid.json", error.Message, StringComparison.Ordinal);
            Assert.Contains(expectedPath, error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void PlayerSkillSourceRejectsMissingDuplicateAndConflictingAliases()
        {
            string canonical = Path.Combine("Data", "Configs", "skills.json");
            var catalog = CatalogCompiler.Compile(canonical);
            string name = catalog.AbilityDefinitions[0].Name;
            float winningCooldown = catalog.AbilityDefinitions[0].Cooldown;

            var missing = Assert.Throws<CatalogValidationException>(() =>
                CatalogCompiler.ValidatePlayerSkillAliases(catalog,
                    new[] { new SkillConfig { Name = "not-declared" } }, "player.json"));
            var duplicate = Assert.Throws<CatalogValidationException>(() =>
                CatalogCompiler.ValidatePlayerSkillAliases(catalog,
                    new[] { new SkillConfig { Name = name }, new SkillConfig { Name = name } }, "player.json"));
            var conflict = Assert.Throws<CatalogValidationException>(() =>
                CatalogCompiler.ValidatePlayerSkillAliases(catalog,
                    new[] { new SkillConfig { Name = name, Cooldown = winningCooldown + 1f } }, "player.json"));

            Assert.Contains("not declared", missing.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("duplicate player alias", duplicate.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("higher-precedence", conflict.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void StrictSkillMergePreservesLegacyDefinitionsAndClosesPlayerAliases()
        {
            var strict = GameConfigLoader.LoadStrictCatalog(new MockRenderer());
            var legacy = GameConfigLoader.LoadConfig(new MockRenderer());
            var strictNames = strict.SkillDefs.Select(skill => skill.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var legacyNames = legacy.SkillDefs.Select(skill => skill.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            Assert.Equal(legacyNames.Count, strictNames.Count);
            Assert.True(legacyNames.SetEquals(strictNames));
            for (int i = 0; i < legacy.SkillDefs.Count; i++)
            {
                Assert.Equal(JsonSerializer.Serialize(legacy.SkillDefs[i]), JsonSerializer.Serialize(strict.SkillDefs[i]));
            }
            Assert.All(strict.Skills, skill => Assert.True(strict.CompiledCatalog!.TryResolveAlias(skill.Name, out _), skill.Name));
            Assert.Equal(strict.SkillDefs[0].Name, strict.CompiledCatalog!.AbilityDefinitions[0].Name);
        }

        [Fact]
        public void StrictBootstrapSourceRoutesEveryLegacyExtractorFamilyToTypedParser()
        {
            string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "Core", "GameConfigLoader.cs"));

            Assert.Contains("TypedGameConfigParser.ParseProduction(jsonContent, CONFIG_FILE)", source, StringComparison.Ordinal);
            Assert.Contains("if (strict) gameConfig.BehaviorTrees = TypedGameConfigParser.ParseBehaviorTrees", source, StringComparison.Ordinal);
            Assert.Contains("if (strict) gameConfig.EnemyAbilities = TypedGameConfigParser.ParseEnemyAbilities", source, StringComparison.Ordinal);
            Assert.Contains("if (strict) gameConfig.PhaseBehaviors = TypedGameConfigParser.ParsePhaseBehaviors", source, StringComparison.Ordinal);
            Assert.Contains("if (strict) gameConfig.Weather = TypedGameConfigParser.ParseWeather", source, StringComparison.Ordinal);
            Assert.Contains(": ParseLegacyGameConfig(jsonContent)", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Extract", File.ReadAllText(Path.Combine(RepositoryRoot(), "Core", "TypedGameConfigParser.cs")), StringComparison.Ordinal);
        }

        private static string MinimalJson(string skillFields, string towerFields, string comboFields)
        {
            return "{" +
                "\"Player\":{\"Name\":\"Player\",\"AttackDamage\":1,\"MaxHealth\":10}," +
                "\"MonsterTypes\":[{\"Name\":\"Enemy\",\"Type\":\"Normal\",\"Health\":10}]," +
                "\"Towers\":[{\"Name\":\"Tower\",\"Type\":\"Basic\",\"Damage\":1,\"AttackSpeed\":1" + Comma(towerFields) + "}]," +
                "\"Skills\":[{\"Name\":\"Skill\"" + Comma(skillFields) + "}]," +
                "\"Levels\":[{\"LevelNumber\":1,\"WaveCount\":1,\"Waves\":[]}]," +
                "\"Combo\":{" + comboFields + "}}";
        }

        private static string Comma(string value) => string.IsNullOrEmpty(value) ? "" : "," + value;

        private static string RepositoryRoot()
        {
            string directory = Directory.GetCurrentDirectory();
            while (!File.Exists(Path.Combine(directory, "BattleSystemECS.csproj")))
                directory = Directory.GetParent(directory)?.FullName ?? throw new DirectoryNotFoundException("repository root not found");
            return directory;
        }
    }
}
