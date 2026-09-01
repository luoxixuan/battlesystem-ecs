using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Reflection;
using System.Reflection.Emit;
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
                "[{\"Id\":\"control\",\"Name\":\"Control\",\"AbilityType\":\"silence_tower\",\"Cooldown\":2,\"StunDuration\":2,\"SlowFactor\":0.4,\"SlowDuration\":3,\"MinionHealthMult\":0.35,\"MinionDamageMult\":0.25,\"SilenceRadius\":5,\"SilenceDuration\":3,\"DispelRadius\":4,\"DispelDuration\":2,\"DispelImmunityDuration\":1,\"CastTime\":7}]",
                "enemy.json");
            var unknown = Assert.Throws<CatalogValidationException>(() => TypedGameConfigParser.ParseEnemyAbilities(
                "[{\"Id\":\"bad\",\"Name\":\"Bad\",\"AbilityType\":\"self_heal\",\"Unexpected\":1}]", "enemy.json"));
            var zeroSummon = Assert.Throws<CatalogValidationException>(() => TypedGameConfigParser.ParseEnemyAbilities(
                "[{\"Id\":\"summon\",\"Name\":\"Summon\",\"AbilityType\":\"summon_minion\"," +
                "\"MinionHealthMult\":0,\"MinionDamageMult\":0}]", "enemy.json"));

            Assert.Equal(6, config.Combo.TriggerThreshold);
            Assert.Equal(7, config.TowerTypes[0].ActiveSkillId);
            Assert.Equal(9f, config.TowerTypes[0].ActiveCooldown);
            Assert.Equal(4, config.Skills[0].AreaRadius);
            Assert.Equal(0.25f, config.Skills[0].HealPercent);
            Assert.Equal(12f, config.Skills[0].ShieldAmount);
            Assert.Equal(5f, enemy[0].SilenceRadius);
            Assert.Equal(4f, enemy[0].DispelRadius);
            Assert.Equal(2, enemy[0].StunDuration);
            Assert.Equal(0.4f, enemy[0].SlowFactor);
            Assert.Equal(3, enemy[0].SlowDuration);
            Assert.Equal(0.35f, enemy[0].MinionHealthMult);
            Assert.Equal(0.25f, enemy[0].MinionDamageMult);
            Assert.Equal(0f, enemy[0].CastTime);
            Assert.Contains("$[0].Unexpected", unknown.Message, StringComparison.Ordinal);
            Assert.Contains("$[0].MinionHealthMult", zeroSummon.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void BooleanAutoCastRemainsInactiveUntilAbilityBehaviorCutoverIsAuthorized()
        {
            var config = TypedGameConfigParser.ParseProduction(MinimalJson(
                "\"AreaShape\":\"heal\",\"AutoCast\":true", "", "\"triggerThreshold\":1"), "compat.json");

            Assert.False(config.Skills[0].AutoCast);
        }

        [Fact]
        public void ProductionParserPreservesCatalogConsumedAliasFieldsBeforeConflictValidation()
        {
            var config = TypedGameConfigParser.ParseProduction(MinimalJson(
                "\"AreaShape\":\"circle\",\"AutoCast\":true,\"DotDuration\":2,\"DotTickInterval\":1," +
                "\"DotDamagePerTick\":9,\"ConeAngleDegrees\":45,\"Modifiers\":[{\"Name\":\"M\",\"Type\":\"Damage\",\"Value\":2,\"EffectTag\":\"Normal\"}]",
                "", "\"triggerThreshold\":1"), "alias-fields.json");

            Assert.Equal(2f, config.Skills[0].DotDuration);
            Assert.Equal(1f, config.Skills[0].DotTickInterval);
            Assert.Equal(45f, config.Skills[0].ConeAngleDegrees);
            Assert.Single(config.Skills[0].Modifiers);
            Assert.False(config.Skills[0].AutoCast);
            Assert.Equal(0f, config.Skills[0].DotDamagePerTick);
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
        public void ProductionParserRejectsUnknownMainConfigurationField()
        {
            string json = MinimalJson("\"AreaShape\":\"single\"", "", "\"triggerThreshold\":1");
            json = json.Substring(0, json.Length - 1) + ",\"UnexpectedRoot\":true}";

            var error = Assert.Throws<CatalogValidationException>(() =>
                TypedGameConfigParser.ParseProduction(json, "main-unknown.json"));

            Assert.Contains("main-unknown.json", error.Message, StringComparison.Ordinal);
            Assert.Contains("$.UnexpectedRoot", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void CatalogCompilerRejectsUnknownCuratedAndStaticFields()
        {
            string curated = WriteTemporaryJson(
                "[{\"Name\":\"Bad Curated\",\"AreaShape\":\"single\",\"UnexpectedCurated\":1}]");
            string staticSkill = WriteTemporaryJson(
                "{\"Name\":\"Bad Static\",\"AttackRange\":1,\"AreaWidth\":1,\"AreaHeight\":1,\"Cooldown\":1,\"DamageMultiplier\":1,\"ManaCost\":0,\"UnexpectedStatic\":1}");
            try
            {
                var curatedError = Assert.Throws<CatalogValidationException>(() => CatalogCompiler.Compile(curated));
                string canonical = Path.Combine("Data", "Configs", "skills.json");
                var staticError = Assert.Throws<CatalogValidationException>(() =>
                    CatalogCompiler.Compile(canonical, new[] { staticSkill }));

                Assert.Contains("$[0].UnexpectedCurated", curatedError.Message, StringComparison.Ordinal);
                Assert.Contains("$.UnexpectedStatic", staticError.Message, StringComparison.Ordinal);
            }
            finally
            {
                File.Delete(curated);
                File.Delete(staticSkill);
            }
        }

        [Fact]
        public void PlayerSkillSourceRejectsMissingDuplicateAndConflictingAliases()
        {
            string canonical = Path.Combine("Data", "Configs", "skills.json");
            var catalog = CatalogCompiler.Compile(canonical, Directory.GetFiles(Path.Combine("Data", "Skills"), "*.json"));
            string name = catalog.AbilityDefinitions[0].Name;
            float winningCooldown = catalog.AbilityDefinitions[0].Cooldown;
            var costAbility = catalog.AbilityDefinitions.First(ability => ability.ManaCost > 0 &&
                ability.Executions.Any(executionId => catalog.TryGetExecution(executionId, out var execution) &&
                    execution.Operation == ExecutionOperation.ApplyDamage &&
                    execution.MagnitudeSource == MagnitudeSource.Multiplier));
            var damageExecution = costAbility.Executions.Select(executionId =>
                catalog.TryGetExecution(executionId, out var execution) ? execution : default)
                .First(execution => execution.Operation == ExecutionOperation.ApplyDamage &&
                    execution.MagnitudeSource == MagnitudeSource.Multiplier);
            var healAbility = FindAbilityWithExecution(catalog, ExecutionOperation.ApplyHeal, out var healExecution);
            var shieldAbility = FindAbilityWithExecution(catalog, ExecutionOperation.ApplyShield, out var shieldExecution);
            var periodicAbility = catalog.AbilityDefinitions.First(ability => ability.Effects.Any(effectId =>
                catalog.TryGetEffect(effectId, out var effect) && effect.Period > 0f));
            var periodicEffect = periodicAbility.Effects.Select(effectId =>
                catalog.TryGetEffect(effectId, out var effect) ? effect : default)
                .First(effect => effect.Period > 0f);
            var coneAbility = catalog.AbilityDefinitions.First(ability =>
                ability.Targeting.Shape == TargetingShape.Cone &&
                Math.Abs(ability.Targeting.Angle - 60f) > 0.0001f);
            var sourceDefinitions = TypedGameConfigParser.LoadSkillDefinitions(canonical,
                Directory.GetFiles(Path.Combine("Data", "Skills"), "*.json"));
            var debuffSource = sourceDefinitions.First(skill => skill.Modifiers.Any(modifier =>
                string.Equals(modifier.Type, "Debuff", StringComparison.OrdinalIgnoreCase)));
            var debuff = debuffSource.Modifiers.First(modifier =>
                string.Equals(modifier.Type, "Debuff", StringComparison.OrdinalIgnoreCase));

            var missing = Assert.Throws<CatalogValidationException>(() =>
                CatalogCompiler.ValidatePlayerSkillAliases(catalog,
                    new[] { new SkillConfig { Name = "not-declared" } }, "player.json"));
            var duplicate = Assert.Throws<CatalogValidationException>(() =>
                CatalogCompiler.ValidatePlayerSkillAliases(catalog,
                    new[] { new SkillConfig { Name = name }, new SkillConfig { Name = name } }, "player.json"));
            var conflict = Assert.Throws<CatalogValidationException>(() =>
                CatalogCompiler.ValidatePlayerSkillAliases(catalog,
                    new[] { new SkillConfig { Name = name, Cooldown = winningCooldown + 1f } }, "player.json"));
            var manaConflict = Assert.Throws<CatalogValidationException>(() =>
                CatalogCompiler.ValidatePlayerSkillAliases(catalog,
                    new[] { new SkillConfig { Name = costAbility.Name, ManaCost = costAbility.ManaCost + 1f } }, "player.json"));
            var damageConflict = Assert.Throws<CatalogValidationException>(() =>
                CatalogCompiler.ValidatePlayerSkillAliases(catalog,
                    new[] { new SkillConfig { Name = costAbility.Name, DamageMultiplier = damageExecution.Magnitude + 1f } }, "player.json"));
            var healConflict = Assert.Throws<CatalogValidationException>(() =>
                CatalogCompiler.ValidatePlayerSkillAliases(catalog,
                    new[] { new SkillConfig { Name = healAbility.Name, HealPercent = healExecution.Magnitude + 1f } }, "player.json"));
            var shieldConflict = Assert.Throws<CatalogValidationException>(() =>
                CatalogCompiler.ValidatePlayerSkillAliases(catalog,
                    new[] { new SkillConfig { Name = shieldAbility.Name, ShieldAmount = shieldExecution.Magnitude + 1f,
                        ShieldDuration = shieldExecution.Duration + 1f } }, "player.json"));
            var dotConflict = Assert.Throws<CatalogValidationException>(() =>
                CatalogCompiler.ValidatePlayerSkillAliases(catalog,
                    new[] { new SkillConfig { Name = periodicAbility.Name, DotDuration = periodicEffect.Duration + 1f } }, "player.json"));
            var modifierConflict = Assert.Throws<CatalogValidationException>(() =>
                CatalogCompiler.ValidatePlayerSkillAliases(catalog,
                    new[] { new SkillConfig { Name = costAbility.Name, Modifiers = new List<SkillModifierDef>
                    {
                        new SkillModifierDef { Name = "conflict", Type = "Damage", Value = damageExecution.Magnitude + 1f,
                            EffectTag = "Normal" }
                    } } }, "player.json"));
            var modifierPayloadConflict = Assert.Throws<CatalogValidationException>(() =>
                CatalogCompiler.ValidatePlayerSkillAliases(catalog,
                    new[] { new SkillConfig { Name = debuffSource.Name, Modifiers = new List<SkillModifierDef>
                    {
                        new SkillModifierDef { Name = debuff.Name, Type = "CrowdControl", Duration = debuff.Duration,
                            StackingType = debuff.StackingType, StackLimitCount = debuff.StackLimitCount,
                            Value = debuff.Value, EffectTag = debuff.EffectTag }
                    } } }, "player.json"));
            var explicitDefaultConeConflict = Assert.Throws<CatalogValidationException>(() =>
                CatalogCompiler.ValidatePlayerSkillAliases(catalog,
                    new[] { new SkillConfig { Name = coneAbility.Name, ConeAngleDegrees = 60f,
                        SemanticFields = SkillSemanticField.ConeAngleDegrees } }, "player.json"));

            Assert.Contains("not declared", missing.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("duplicate player alias", duplicate.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("higher-precedence", conflict.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("$.Skills[0].ManaCost", manaConflict.Message, StringComparison.Ordinal);
            Assert.Contains(costAbility.Name, manaConflict.Message, StringComparison.Ordinal);
            Assert.Contains("$.Skills[0].DamageMultiplier", damageConflict.Message, StringComparison.Ordinal);
            Assert.Contains("$.Skills[0].HealPercent", healConflict.Message, StringComparison.Ordinal);
            Assert.Contains("$.Skills[0].ShieldAmount", shieldConflict.Message, StringComparison.Ordinal);
            Assert.Contains("compiled Catalog ability", shieldConflict.Message, StringComparison.Ordinal);
            Assert.Contains("$.Skills[0].DotDuration", dotConflict.Message, StringComparison.Ordinal);
            Assert.Contains("$.Skills[0].Modifiers[0]", modifierConflict.Message, StringComparison.Ordinal);
            Assert.Contains("$.Skills[0].Modifiers[0]", modifierPayloadConflict.Message, StringComparison.Ordinal);
            Assert.Contains("$.Skills[0].ConeAngleDegrees", explicitDefaultConeConflict.Message, StringComparison.Ordinal);
            Assert.Contains(coneAbility.Name, explicitDefaultConeConflict.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ExplicitZeroAliasSemanticsConflictWhileOmittedFieldsRemainCompatible()
        {
            string canonical = Path.Combine("Data", "Configs", "skills.json");
            var catalog = CatalogCompiler.Compile(canonical, Directory.GetFiles(Path.Combine("Data", "Skills"), "*.json"));
            var cooldown = catalog.AbilityDefinitions.First(ability => ability.Cooldown > 0f);
            var damage = catalog.AbilityDefinitions.First(ability => ability.Executions.Any(id =>
                catalog.TryGetExecution(id, out var execution) && execution.Operation == ExecutionOperation.ApplyDamage &&
                execution.MagnitudeSource == MagnitudeSource.Multiplier && execution.Magnitude > 0f));
            var freeze = FindAbilityWithExecution(catalog, ExecutionOperation.ApplyFreeze, out var freezeExecution);
            Assert.True(freezeExecution.Probability > 0f);
            var periodic = catalog.AbilityDefinitions.First(ability => ability.Effects.Any(id =>
                catalog.TryGetEffect(id, out var effect) && effect.Duration > 0f && effect.Period > 0f));

            AssertAliasConflict(catalog, ParsePlayerAlias(cooldown.Name, "\"Cooldown\":0"), ".Cooldown");
            AssertAliasConflict(catalog, ParsePlayerAlias(damage.Name, "\"DamageMultiplier\":0"), ".DamageMultiplier");
            AssertAliasConflict(catalog, ParsePlayerAlias(freeze.Name, "\"FreezeChance\":0"), ".FreezeChance");
            AssertAliasConflict(catalog, ParsePlayerAlias(periodic.Name, "\"DotDuration\":0"), ".DotDuration");

            CatalogCompiler.ValidatePlayerSkillAliases(catalog,
                new[] { ParsePlayerAlias(cooldown.Name, "") }, "player.json");
        }

        [Fact]
        public void CanonicalStaticAndPlayerSourcesRetainExplicitSemanticPresence()
        {
            string canonical = Path.Combine("Data", "Configs", "skills.json");
            string staticPath = WriteTemporaryJson(
                "{\"Name\":\"Presence Static Fixture\",\"AttackRange\":1,\"AreaWidth\":1," +
                "\"AreaHeight\":1,\"Cooldown\":0,\"DamageMultiplier\":0,\"ManaCost\":0}");
            try
            {
                var catalog = CatalogCompiler.Compile(canonical, new[] { staticPath });
                var canonicalAbility = catalog.AbilityDefinitions[0];
                var staticAbility = catalog.AbilityDefinitions.First(ability =>
                    string.Equals(ability.Name, "Presence Static Fixture", StringComparison.Ordinal));
                var player = ParsePlayerAlias(canonicalAbility.Name, "\"Cooldown\":0");

                Assert.NotEqual(SkillSemanticField.None, canonicalAbility.SemanticFields);
                Assert.True((staticAbility.SemanticFields & SkillSemanticField.Cooldown) != 0);
                Assert.True((staticAbility.SemanticFields & SkillSemanticField.DamageMultiplier) != 0);
                Assert.True((staticAbility.SemanticFields & SkillSemanticField.ManaCost) != 0);
                Assert.True(player.HasSemanticField(SkillSemanticField.Cooldown));
            }
            finally
            {
                File.Delete(staticPath);
            }
        }

        [Fact]
        public void CanonicalFreezeModifierRoundTripsThroughNormalizedCatalogProvenance()
        {
            string canonical = Path.Combine("Data", "Configs", "skills.json");
            var catalog = CatalogCompiler.Compile(canonical);
            var source = TypedGameConfigParser.LoadSkillDefinitions(canonical, Array.Empty<string>())
                .First(skill => string.Equals(skill.AreaShape, "freeze", StringComparison.OrdinalIgnoreCase) &&
                    skill.Modifiers.Any(modifier => string.Equals(modifier.Type, "CrowdControl",
                        StringComparison.OrdinalIgnoreCase)));

            CatalogCompiler.ValidatePlayerSkillAliases(catalog, new[] { source }, "player.json");

            Assert.True(catalog.TryResolveAlias(source.Name, out var abilityId));
            var semantic = Assert.Single(catalog.AbilityDefinitions[abilityId.Value].SourceModifiers);
            Assert.Equal(EffectPayloadKind.Freeze, semantic.Payload);
            Assert.Equal(ExecutionOperation.ApplyFreeze, semantic.Operation);
            Assert.Equal(source.FreezeDuration, semantic.NormalizedMagnitude);
            Assert.Equal(source.FreezeChance, semantic.Probability);
            Assert.Equal(TargetingShape.Freeze, semantic.Targeting);
        }

        [Fact]
        public void FreezeModifierProvenanceRejectsDetailAndCountMismatches()
        {
            string canonical = Path.Combine("Data", "Configs", "skills.json");
            var catalog = CatalogCompiler.Compile(canonical);
            var source = TypedGameConfigParser.LoadSkillDefinitions(canonical, Array.Empty<string>())
                .First(skill => string.Equals(skill.AreaShape, "freeze", StringComparison.OrdinalIgnoreCase) &&
                    skill.Modifiers.Any(modifier => string.Equals(modifier.Type, "CrowdControl",
                        StringComparison.OrdinalIgnoreCase)));
            Assert.Single(source.Modifiers);

            var durationMismatch = CloneSkill(source);
            durationMismatch.Modifiers[0].Duration += 1f;
            AssertAliasConflict(catalog, durationMismatch, ".Modifiers[0]");

            var probabilityMismatch = CloneSkill(source);
            probabilityMismatch.FreezeChance = Math.Min(0.99f, probabilityMismatch.FreezeChance + 0.1f);
            AssertAliasConflict(catalog, probabilityMismatch, ".FreezeChance");

            var payloadMismatch = CloneSkill(source);
            payloadMismatch.Modifiers[0].Type = "Debuff";
            AssertAliasConflict(catalog, payloadMismatch, ".Modifiers[0]");

            var countMismatch = CloneSkill(source);
            countMismatch.Modifiers.Clear();
            countMismatch.SemanticFields |= SkillSemanticField.Modifiers;
            AssertAliasConflict(catalog, countMismatch, ".Modifiers");
        }

        [Fact]
        public void CatalogValidatorRejectsFreezeModifierProvenanceDrift()
        {
            string canonical = Path.Combine("Data", "Configs", "skills.json");
            var catalog = CatalogCompiler.Compile(canonical);
            var ability = catalog.AbilityDefinitions.First(candidate => candidate.SourceModifiers.Any(modifier =>
                modifier.Payload == EffectPayloadKind.Freeze && modifier.Operation == ExecutionOperation.ApplyFreeze));
            var executionId = ability.Executions.First(id => catalog.Executions[id.Value].Operation == ExecutionOperation.ApplyFreeze);
            var executions = catalog.Executions.ToArray();
            var original = executions[executionId.Value];
            executions[executionId.Value] = new ExecutionDefinition(original.Id, original.Payload,
                original.Magnitude, original.Tag, original.MagnitudeSource, original.Stage, original.Duration,
                original.Operation, Math.Min(0.99f, original.Probability + 0.1f), original.Parameter);
            var drifted = new GameplayCatalog(catalog.AbilityDefinitions, catalog.Targetings, catalog.Effects,
                executions, catalog.Triggers, catalog.Modifiers, catalog.Aliases);

            var error = Assert.Throws<CatalogValidationException>(() => CatalogValidator.Validate(drifted, "drifted"));
            Assert.Contains("modifier provenance", error.Message, StringComparison.OrdinalIgnoreCase);

            executions[executionId.Value] = new ExecutionDefinition(original.Id, original.Payload,
                original.Magnitude, original.Tag, original.MagnitudeSource, original.Stage, original.Duration,
                original.Operation, original.Probability, original.Parameter,
                StackingBehavior.MaxStacksRefresh, original.SemanticMaxStacks);
            var executionStackingDrift = new GameplayCatalog(catalog.AbilityDefinitions, catalog.Targetings, catalog.Effects,
                executions, catalog.Triggers, catalog.Modifiers, catalog.Aliases);
            var executionStackingError = Assert.Throws<CatalogValidationException>(() => CatalogValidator.Validate(executionStackingDrift, "execution-stacking-drift"));
            Assert.Contains("modifier provenance", executionStackingError.Message, StringComparison.OrdinalIgnoreCase);

            executions[executionId.Value] = new ExecutionDefinition(original.Id, original.Payload,
                original.Magnitude, original.Tag, original.MagnitudeSource, original.Stage, original.Duration,
                original.Operation, original.Probability, original.Parameter,
                original.SemanticStacking, 999);
            var executionMaxStackDrift = new GameplayCatalog(catalog.AbilityDefinitions, catalog.Targetings, catalog.Effects,
                executions, catalog.Triggers, catalog.Modifiers, catalog.Aliases);
            var executionMaxStackError = Assert.Throws<CatalogValidationException>(() => CatalogValidator.Validate(executionMaxStackDrift, "execution-max-stack-drift"));
            Assert.Contains("modifier provenance", executionMaxStackError.Message, StringComparison.OrdinalIgnoreCase);

            var semantic = ability.SourceModifiers.ToArray();
            semantic[0] = new SkillModifierSemantic(semantic[0].Name, semantic[0].Type, semantic[0].Value,
                semantic[0].Duration, StackingBehavior.None, semantic[0].MaxStacks, semantic[0].Tag,
                semantic[0].Payload, semantic[0].Operation, semantic[0].NormalizedMagnitude,
                semantic[0].Probability, semantic[0].Targeting);
            var stackingDrift = BuildCatalogWithAbility(catalog, new AbilityDefinition(ability.Id, ability.Name,
                ability.Targeting, ability.Clock, ability.Cooldown, ability.AllowedPhases, ability.Effects.ToArray(),
                ability.Modifiers.ToArray(), ability.Executor, ability.Consumer, ability.Activation, ability.ManaCost,
                ability.Executions.ToArray(), ability.Costs.ToArray(), ability.Targeting.RequiredTags.ToArray(),
                ability.Targeting.BlockedTags.ToArray(), ability.TriggerRefs.ToArray(), ability.SemanticFields, semantic));
            var stackingError = Assert.Throws<CatalogValidationException>(() => CatalogValidator.Validate(stackingDrift, "stacking-drift"));
            Assert.Contains("modifier provenance", stackingError.Message, StringComparison.OrdinalIgnoreCase);

            semantic[0] = new SkillModifierSemantic(semantic[0].Name, semantic[0].Type, semantic[0].Value,
                semantic[0].Duration, semantic[0].Stacking, 0, semantic[0].Tag, semantic[0].Payload,
                semantic[0].Operation, semantic[0].NormalizedMagnitude, semantic[0].Probability, semantic[0].Targeting);
            var maxStackDrift = BuildCatalogWithAbility(catalog, new AbilityDefinition(ability.Id, ability.Name,
                ability.Targeting, ability.Clock, ability.Cooldown, ability.AllowedPhases, ability.Effects.ToArray(),
                ability.Modifiers.ToArray(), ability.Executor, ability.Consumer, ability.Activation, ability.ManaCost,
                ability.Executions.ToArray(), ability.Costs.ToArray(), ability.Targeting.RequiredTags.ToArray(),
                ability.Targeting.BlockedTags.ToArray(), ability.TriggerRefs.ToArray(), ability.SemanticFields, semantic));
            var maxStackError = Assert.Throws<CatalogValidationException>(() => CatalogValidator.Validate(maxStackDrift, "max-stack-drift"));
            Assert.Contains("modifier provenance", maxStackError.Message, StringComparison.OrdinalIgnoreCase);

            semantic[0] = new SkillModifierSemantic(semantic[0].Name, semantic[0].Type, semantic[0].Value,
                semantic[0].Duration, semantic[0].Stacking, semantic[0].MaxStacks, CatalogRegistries.SkillTag,
                semantic[0].Payload, semantic[0].Operation, semantic[0].NormalizedMagnitude,
                semantic[0].Probability, semantic[0].Targeting);
            var tagDrift = BuildCatalogWithAbility(catalog, new AbilityDefinition(ability.Id, ability.Name,
                ability.Targeting, ability.Clock, ability.Cooldown, ability.AllowedPhases, ability.Effects.ToArray(),
                ability.Modifiers.ToArray(), ability.Executor, ability.Consumer, ability.Activation, ability.ManaCost,
                ability.Executions.ToArray(), ability.Costs.ToArray(), ability.Targeting.RequiredTags.ToArray(),
                ability.Targeting.BlockedTags.ToArray(), ability.TriggerRefs.ToArray(), ability.SemanticFields, semantic));
            var tagError = Assert.Throws<CatalogValidationException>(() => CatalogValidator.Validate(tagDrift, "tag-drift"));
            Assert.Contains("modifier provenance", tagError.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void CatalogValidatorRejectsDirectDamageStackContractDrift()
        {
            string canonical = Path.Combine("Data", "Configs", "skills.json");
            var catalog = CatalogCompiler.Compile(canonical);
            var ability = catalog.AbilityDefinitions.First(candidate => candidate.SourceModifiers.Any(modifier =>
                string.Equals(modifier.Type, "Damage", StringComparison.OrdinalIgnoreCase)));
            var executionIds = ability.Executions.Where(id => catalog.Executions[id.Value].Operation == ExecutionOperation.ApplyDamage &&
                catalog.Executions[id.Value].SemanticStacking == StackingBehavior.None).ToArray();
            Assert.NotEmpty(executionIds);
            var original = catalog.Executions[executionIds[0].Value];
            foreach (var stacking in new[] { StackingBehavior.MaxStacksRefresh, StackingBehavior.DurationRefresh })
            {
                var executions = catalog.Executions.ToArray();
                foreach (var executionId in executionIds)
                {
                    var execution = executions[executionId.Value];
                    executions[executionId.Value] = new ExecutionDefinition(execution.Id, execution.Payload,
                        execution.Magnitude, execution.Tag, execution.MagnitudeSource, execution.Stage, execution.Duration,
                        execution.Operation, execution.Probability, execution.Parameter, stacking, 1);
                }
                var drifted = new GameplayCatalog(catalog.AbilityDefinitions, catalog.Targetings, catalog.Effects,
                    executions, catalog.Triggers, catalog.Modifiers, catalog.Aliases);
                var error = Assert.Throws<CatalogValidationException>(() => CatalogValidator.Validate(drifted, "damage-stack-drift"));
                Assert.Contains("modifier provenance", error.Message, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void CatalogValidatorRejectsDirectDamageSourceDescriptorStackDrift()
        {
            string canonical = Path.Combine("Data", "Configs", "skills.json");
            var catalog = CatalogCompiler.Compile(canonical);
            var ability = catalog.AbilityDefinitions.First(candidate => candidate.SourceModifiers.Any(modifier =>
                string.Equals(modifier.Type, "Damage", StringComparison.OrdinalIgnoreCase)));
            var source = ability.SourceModifiers.ToArray();
            var original = source[0];
            source[0] = new SkillModifierSemantic(original.Name, original.Type, original.Value, original.Duration,
                StackingBehavior.DurationRefresh, 1, original.Tag, original.Payload, original.Operation,
                original.NormalizedMagnitude, original.Probability, original.Targeting);
            var drifted = BuildCatalogWithAbility(catalog, new AbilityDefinition(ability.Id, ability.Name,
                ability.Targeting, ability.Clock, ability.Cooldown, ability.AllowedPhases, ability.Effects.ToArray(),
                ability.Modifiers.ToArray(), ability.Executor, ability.Consumer, ability.Activation, ability.ManaCost,
                ability.Executions.ToArray(), ability.Costs.ToArray(), ability.Targeting.RequiredTags.ToArray(),
                ability.Targeting.BlockedTags.ToArray(), ability.TriggerRefs.ToArray(), ability.SemanticFields, source));
            var error = Assert.Throws<CatalogValidationException>(() => CatalogValidator.Validate(drifted, "damage-source-stack-drift"));
            Assert.Contains("modifier provenance", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void CatalogValidatorRejectsGlobalDirectDamageStackContractDrift()
        {
            string canonical = Path.Combine("Data", "Configs", "skills.json");
            var catalog = CatalogCompiler.Compile(canonical);
            var executionId = catalog.Executions.Select((execution, index) => new { execution, index })
                .First(item => item.execution.Payload == EffectPayloadKind.Damage &&
                    item.execution.Operation == ExecutionOperation.ApplyDamage).index;
            var executions = catalog.Executions.ToArray();
            var original = executions[executionId];
            executions[executionId] = new ExecutionDefinition(original.Id, original.Payload,
                original.Magnitude, original.Tag, original.MagnitudeSource, original.Stage, original.Duration,
                original.Operation, original.Probability, original.Parameter,
                StackingBehavior.MaxStacksRefresh, 1);
            var drifted = new GameplayCatalog(catalog.AbilityDefinitions, catalog.Targetings, catalog.Effects,
                executions, catalog.Triggers, catalog.Modifiers, catalog.Aliases);
            var error = Assert.Throws<CatalogValidationException>(() => CatalogValidator.Validate(drifted, "global-damage-stack-drift"));
            Assert.Contains("direct damage execution", error.Message, StringComparison.OrdinalIgnoreCase);
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
        public void StrictAndLegacyBootstrapRoutesUseDistinctParserAdapters()
        {
            string json = MinimalJson("\"AreaShape\":\"single\"", "", "\"triggerThreshold\":1");
            json = json.Substring(0, json.Length - 1) + ",\"LegacyOnlyField\":true}";
            const System.Reflection.BindingFlags nestedFlags = System.Reflection.BindingFlags.NonPublic;
            const System.Reflection.BindingFlags fieldFlags = System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static;
            Type loader = typeof(GameConfigLoader);
            Type strictAdapter = loader.GetNestedType("StrictConfigurationParser", nestedFlags)!;
            Type legacyAdapter = loader.GetNestedType("LegacyConfigurationParser", nestedFlags)!;
            var strictInstance = strictAdapter.GetField("Instance", fieldFlags)!;
            var legacyInstance = legacyAdapter.GetField("Instance", fieldFlags)!;
            var strictEntry = loader.GetMethod(nameof(GameConfigLoader.LoadConfigStrict))!;
            var legacyEntry = loader.GetMethod(nameof(GameConfigLoader.LoadConfig))!;

            Assert.True(ReferencesMetadataToken(strictEntry, strictInstance.MetadataToken));
            Assert.False(ReferencesMetadataToken(strictEntry, legacyInstance.MetadataToken));
            Assert.True(ReferencesMetadataToken(legacyEntry, legacyInstance.MetadataToken));
            Assert.False(ReferencesMetadataToken(legacyEntry, strictInstance.MetadataToken));
            var legacyParser = loader.GetMethod("ParseLegacyGameConfig",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            Assert.NotNull(legacyParser.Invoke(null, new object[] { json }));
            Assert.Throws<CatalogValidationException>(() =>
                TypedGameConfigParser.ParseProduction(json, "strict-route.json"));

            var strictParsers = strictAdapter.GetMethods(BindingFlags.Public | BindingFlags.Instance |
                BindingFlags.DeclaredOnly).Where(method => method.Name.StartsWith("Parse", StringComparison.Ordinal)).ToArray();
            var strictReachable = CollectTransitiveProjectCalls(strictParsers, typeof(GameConfigLoader).Assembly);
            Assert.DoesNotContain(strictReachable, IsForbiddenParserMethod);
            Assert.DoesNotContain(strictReachable, IsReflectionDispatchMethod);
            var expectedStrictRoutes = new Dictionary<string, string>
            {
                ["ParseMain"] = "ParseProduction",
                ["ParseBehaviorTrees"] = "ParseBehaviorTrees",
                ["ParseEnemyAbilities"] = "ParseEnemyAbilities",
                ["ParsePhaseBehaviors"] = "ParsePhaseBehaviors",
                ["ParseWeather"] = "ParseWeather"
            };
            foreach (var route in expectedStrictRoutes)
            {
                var root = Assert.Single(strictParsers, method => method.Name == route.Key);
                var reachable = CollectTransitiveProjectCalls(new[] { root }, typeof(GameConfigLoader).Assembly);
                Assert.Contains(reachable, method => method.DeclaringType == typeof(TypedGameConfigParser) &&
                    method.Name == route.Value);
            }

            var legacyParsers = legacyAdapter.GetMethods(BindingFlags.Public | BindingFlags.Instance |
                BindingFlags.DeclaredOnly).Where(method => method.Name.StartsWith("Parse", StringComparison.Ordinal)).ToArray();
            var legacyReachable = CollectTransitiveProjectCalls(legacyParsers, typeof(GameConfigLoader).Assembly);
            Assert.Contains(legacyReachable, method => method.Name == "ParseLegacyGameConfig");
            Assert.Contains(legacyReachable, method => method.Name.StartsWith("Extract", StringComparison.Ordinal));

            var probe = typeof(TypedConfigParserTests).GetMethod(nameof(ArchitectureProbeRoot),
                BindingFlags.NonPublic | BindingFlags.Static)!;
            var probeReachable = CollectTransitiveProjectCalls(new[] { probe }, typeof(TypedConfigParserTests).Assembly);
            Assert.Contains(probeReachable, IsForbiddenParserMethod);
            var genericProbe = typeof(TypedConfigParserTests).GetMethod(nameof(GenericArchitectureProbeRoot),
                BindingFlags.NonPublic | BindingFlags.Static)!;
            var genericReachable = CollectTransitiveProjectCalls(new[] { genericProbe },
                typeof(TypedConfigParserTests).Assembly);
            Assert.Contains(genericReachable, IsForbiddenParserMethod);
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

        private static SkillConfig ParsePlayerAlias(string name, string fields)
        {
            string json = MinimalJson(fields, "", "\"triggerThreshold\":1");
            json = json.Replace("\"Skills\":[{\"Name\":\"Skill\"",
                "\"Skills\":[{\"Name\":" + JsonSerializer.Serialize(name), StringComparison.Ordinal);
            return Assert.Single(TypedGameConfigParser.ParseProduction(json, "player.json").Skills);
        }

        private static void AssertAliasConflict(GameplayCatalog catalog, SkillConfig source, string field)
        {
            var error = Assert.Throws<CatalogValidationException>(() =>
                CatalogCompiler.ValidatePlayerSkillAliases(catalog, new[] { source }, "player.json"));
            Assert.Contains(field, error.Message, StringComparison.Ordinal);
        }

        private static SkillConfig CloneSkill(SkillConfig source) =>
            JsonSerializer.Deserialize<SkillConfig>(JsonSerializer.Serialize(source))!;

        private static GameplayCatalog BuildCatalogWithAbility(GameplayCatalog source, AbilityDefinition replacement)
        {
            var abilities = source.AbilityDefinitions.ToArray();
            abilities[replacement.Id.Value] = replacement;
            return new GameplayCatalog(abilities, source.Targetings, source.Effects, source.Executions,
                source.Triggers, source.Modifiers, source.Aliases);
        }

        private static string WriteTemporaryJson(string json)
        {
            string path = Path.Combine(Path.GetTempPath(), "typed_catalog_" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, json);
            return path;
        }

        private static bool ReferencesMetadataToken(System.Reflection.MethodInfo method, int metadataToken)
        {
            byte[] body = method.GetMethodBody()!.GetILAsByteArray()!;
            byte[] token = BitConverter.GetBytes(metadataToken);
            for (int i = 0; i <= body.Length - token.Length; i++)
            {
                int offset = 0;
                while (offset < token.Length && body[i + offset] == token[offset]) offset++;
                if (offset == token.Length) return true;
            }
            return false;
        }

        private static HashSet<MethodBase> CollectTransitiveProjectCalls(IEnumerable<MethodInfo> roots,
            params System.Reflection.Assembly[] projectAssemblies)
        {
            var result = new HashSet<MethodBase>();
            var visited = new HashSet<MethodBase>();
            var queue = new Queue<MethodBase>(roots);
            while (queue.Count > 0)
            {
                var method = queue.Dequeue();
                if (!visited.Add(method)) continue;
                foreach (var called in GetCalledMethods(method))
                {
                    result.Add(called);
                    if (called.DeclaringType != null && projectAssemblies.Contains(called.DeclaringType.Assembly))
                        queue.Enqueue(called);
                }
            }
            return result;
        }

        private static IEnumerable<MethodBase> GetCalledMethods(MethodBase method)
        {
            byte[] body = method.GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>();
            var oneByte = new Dictionary<byte, OpCode>();
            var twoByte = new Dictionary<byte, OpCode>();
            foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.GetValue(null) is not OpCode code) continue;
                ushort value = unchecked((ushort)code.Value);
                if (value < 0x100) oneByte[(byte)value] = code;
                else if ((value & 0xff00) == 0xfe00) twoByte[(byte)(value & 0xff)] = code;
            }
            int offset = 0;
            while (offset < body.Length)
            {
                byte first = body[offset++];
                OpCode code;
                if (first == 0xfe)
                {
                    if (offset >= body.Length || !twoByte.TryGetValue(body[offset++], out code))
                        throw new InvalidOperationException("unresolved two-byte opcode in " + method);
                }
                else if (!oneByte.TryGetValue(first, out code))
                    throw new InvalidOperationException("unresolved opcode in " + method);

                int operandSize = OperandSize(code.OperandType, body, offset);
                if (offset + operandSize > body.Length)
                    throw new InvalidOperationException("truncated IL operand in " + method);
                if (code.OperandType == OperandType.InlineMethod || code.OperandType == OperandType.InlineTok)
                {
                    int token = BitConverter.ToInt32(body, offset);
                    Type[]? typeContext = method.DeclaringType != null && method.DeclaringType.IsGenericType
                        ? method.DeclaringType.GetGenericArguments() : null;
                    Type[]? methodContext = method.IsGenericMethod ? method.GetGenericArguments() : null;
                    MemberInfo? resolved;
                    try
                    {
                        resolved = code.OperandType == OperandType.InlineMethod
                            ? method.Module.ResolveMethod(token, typeContext, methodContext)
                            : method.Module.ResolveMember(token, typeContext, methodContext);
                    }
                    catch (Exception error) when (error is ArgumentException || error is BadImageFormatException ||
                                                  error is InvalidOperationException)
                    {
                        throw new InvalidOperationException("unresolved method token 0x" + token.ToString("X8") +
                            " in project method " + method, error);
                    }
                    if (resolved == null)
                        throw new InvalidOperationException("null method token resolution in " + method);
                    if (resolved is MethodBase called) yield return called;
                }
                offset += operandSize;
            }
        }

        private static int OperandSize(OperandType type, byte[] body, int offset)
        {
            switch (type)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineI8:
                case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch:
                    int count = BitConverter.ToInt32(body, offset);
                    return 4 + count * 4;
                default: return 4;
            }
        }

        private static bool IsForbiddenParserMethod(MethodBase method) =>
            method.Name == "ParseLegacyGameConfig" || method.Name.StartsWith("Extract", StringComparison.Ordinal) ||
            method.Name.StartsWith("DefGet", StringComparison.Ordinal);

        private static bool IsReflectionDispatchMethod(MethodBase method)
        {
            Type? type = method.DeclaringType;
            return type != null &&
                (typeof(MethodBase).IsAssignableFrom(type) && method.Name == "Invoke" ||
                 type == typeof(Type) && (method.Name == "GetMethod" || method.Name == "GetMethods" ||
                                          method.Name == "GetMember" || method.Name == "InvokeMember") ||
                 type == typeof(Delegate) && method.Name == "CreateDelegate");
        }

        private static void ArchitectureProbeRoot() => ArchitectureProbeIntermediate();
        private static void ArchitectureProbeIntermediate() => ExtractArchitectureProbe();
        private static void ExtractArchitectureProbe() { }
        private static void GenericArchitectureProbeRoot() => GenericArchitectureProbeIntermediate(1);
        private static void GenericArchitectureProbeIntermediate<T>(T value) => ExtractGenericArchitectureProbe(value);
        private static void ExtractGenericArchitectureProbe<T>(T value) { }

        private static AbilityDefinition FindAbilityWithExecution(GameplayCatalog catalog,
            ExecutionOperation operation, out ExecutionDefinition execution)
        {
            foreach (var ability in catalog.AbilityDefinitions)
            {
                foreach (var executionId in ability.Executions)
                {
                    if (!catalog.TryGetExecution(executionId, out var candidate) || candidate.Operation != operation) continue;
                    execution = candidate;
                    return ability;
                }
            }
            throw new InvalidOperationException("required Catalog execution was not found: " + operation);
        }

        private static string RepositoryRoot()
        {
            string directory = Directory.GetCurrentDirectory();
            while (!File.Exists(Path.Combine(directory, "BattleSystemECS.csproj")))
                directory = Directory.GetParent(directory)?.FullName ?? throw new DirectoryNotFoundException("repository root not found");
            return directory;
        }
    }
}
