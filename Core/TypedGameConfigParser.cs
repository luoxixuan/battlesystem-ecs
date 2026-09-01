using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BattleSystemECS.Components;
using BattleSystemECS.Core.GAS;

namespace BattleSystemECS.Config
{
    /// <summary>
    /// Strict configuration seam. Production JSON is deserialized into typed models here;
    /// the legacy text extractors remain isolated in GameConfigLoader.LoadConfig.
    /// </summary>
    internal static class TypedGameConfigParser
    {
        private static readonly JsonSerializerOptions Options = CreateOptions();
        private static readonly HashSet<string> ExplicitEnemyLegacyFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "StunDuration", "SlowFactor", "SlowDuration", "MinionHealthMult", "MinionDamageMult",
            "TelegraphDuration", "TelegraphColor", "CastTime", "Interruptible"
        };

        internal static GameConfig ParseProduction(string json, string sourcePath)
        {
            var source = Deserialize<GameConfigSource>(json, sourcePath);
            Require(source.Player != null, sourcePath, "$.Player", "required object is missing");
            Require(source.MonsterTypes != null && source.MonsterTypes.Count > 0, sourcePath, "$.MonsterTypes", "at least one monster is required");
            Require(source.Towers != null && source.Towers.Count > 0, sourcePath, "$.Towers", "at least one tower is required");
            Require(source.Skills != null && source.Skills.Count > 0, sourcePath, "$.Skills", "at least one player skill is required");
            Require(source.Levels != null && source.Levels.Count > 0, sourcePath, "$.Levels", "at least one level is required");
            for (int i = 0; i < source.Skills.Count; i++) NormalizeLegacyPlayerSkill(source.Skills[i]);

            var config = new GameConfig
            {
                Player = source.Player,
                MonsterTypes = source.MonsterTypes,
                TowerTypes = source.Towers,
                Skills = source.Skills,
                Levels = source.Levels,
                GlobalSkills = source.GlobalSkills ?? new List<GlobalSkillDef>(),
                Combo = source.Combo ?? new ComboConfig(),
                TowerOvercharge = source.TowerOvercharge ?? new TowerOverchargeConfig(),
                PositionalDamage = source.PositionalDamage ?? new PositionalDamageConfig()
            };
            config.CurrentLevel = config.Levels[0];
            ValidateMain(config, sourcePath);
            return config;
        }

        internal static Dictionary<string, BehaviorTreeDef> ParseBehaviorTrees(string json, string sourcePath)
        {
            var values = Deserialize<List<BehaviorTreeDef>>(json, sourcePath);
            Require(values != null && values.Count > 0, sourcePath, "$", "at least one behavior tree is required");
            var result = new Dictionary<string, BehaviorTreeDef>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < values.Count; i++)
            {
                var tree = values[i];
                string path = "$[" + i + "]";
                Require(tree != null && !string.IsNullOrWhiteSpace(tree.MonsterType), sourcePath, path + ".MonsterType", "required string is missing");
                Require(!string.IsNullOrWhiteSpace(tree.RootId), sourcePath, path + ".RootId", "required string is missing");
                Require(tree.Nodes != null && tree.Nodes.ContainsKey(tree.RootId), sourcePath, path + ".Nodes", "root node reference is missing");
                Require(!result.ContainsKey(tree.MonsterType), sourcePath, path + ".MonsterType", "duplicate monster type '" + tree.MonsterType + "'");
                result.Add(tree.MonsterType, tree);
            }
            return result;
        }

        internal static List<EnemyAbilityDef> ParseEnemyAbilities(string json, string sourcePath)
        {
            var sources = Deserialize<List<EnemyAbilitySource>>(json, sourcePath);
            Require(sources != null && sources.Count > 0, sourcePath, "$", "at least one enemy ability is required");
            var values = new List<EnemyAbilityDef>(sources.Count);
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < sources.Count; i++)
            {
                var source = sources[i];
                if (source.LegacyFields != null)
                    foreach (string field in source.LegacyFields.Keys)
                        Require(ExplicitEnemyLegacyFields.Contains(field), sourcePath, "$[" + i + "]." + field, "unknown field");
                var ability = new EnemyAbilityDef
                {
                    Id = source.Id,
                    Name = source.Name,
                    Description = source.Description,
                    AbilityType = source.AbilityType,
                    BuffStat = source.BuffStat,
                    Cooldown = source.Cooldown,
                    CooldownRemaining = source.CooldownRemaining,
                    AoeRadius = source.AoeRadius,
                    DamageMultiplier = source.DamageMultiplier,
                    HealAmount = source.HealAmount,
                    BuffDuration = source.BuffDuration,
                    SilenceRadius = source.SilenceRadius,
                    SilenceDuration = source.SilenceDuration,
                    DispelRadius = source.DispelRadius,
                    DispelDuration = source.DispelDuration,
                    DispelImmunityDuration = source.DispelImmunityDuration
                };
                string path = "$[" + i + "]";
                Require(ability != null && !string.IsNullOrWhiteSpace(ability.Id), sourcePath, path + ".Id", "required string is missing");
                Require(!string.IsNullOrWhiteSpace(ability.Name), sourcePath, path + ".Name", "required string is missing");
                Require(!string.IsNullOrWhiteSpace(ability.AbilityType), sourcePath, path + ".AbilityType", "required string is missing");
                Require(ids.Add(ability.Id), sourcePath, path + ".Id", "duplicate id '" + ability.Id + "'");
                NonNegative(ability.Cooldown, sourcePath, path + ".Cooldown");
                AtLeast(ability.SilenceRadius, -1f, sourcePath, path + ".SilenceRadius");
                NonNegative(ability.SilenceDuration, sourcePath, path + ".SilenceDuration");
                AtLeast(ability.DispelRadius, -1f, sourcePath, path + ".DispelRadius");
                NonNegative(ability.DispelDuration, sourcePath, path + ".DispelDuration");
                NonNegative(ability.DispelImmunityDuration, sourcePath, path + ".DispelImmunityDuration");
                values.Add(ability);
            }
            return values;
        }

        internal static Dictionary<string, PhaseBehaviorDef> ParsePhaseBehaviors(string json, string sourcePath)
        {
            var values = Deserialize<Dictionary<string, PhaseBehaviorDef>>(json, sourcePath);
            Require(values != null && values.Count > 0, sourcePath, "$", "at least one phase behavior is required");
            foreach (var pair in values)
            {
                Require(!string.IsNullOrWhiteSpace(pair.Key) && pair.Value != null, sourcePath, "$." + pair.Key, "invalid phase behavior");
                pair.Value.UnlockTowers = pair.Value.UnlockTowers ?? new List<string>();
                pair.Value.UnlockAbilities = pair.Value.UnlockAbilities ?? new List<string>();
                Require(pair.Value.IntermissionDelayMs >= 0, sourcePath, "$." + pair.Key + ".intermissionDelayMs", "must be non-negative");
                Require(pair.Value.TurnIntervalMs >= 0, sourcePath, "$." + pair.Key + ".turnIntervalMs", "must be non-negative");
            }
            return values;
        }

        internal static WeatherConfig ParseWeather(string json, string sourcePath)
        {
            var source = Deserialize<WeatherSource>(json, sourcePath);
            var result = new WeatherConfig
            {
                GlobalEnemySpeedMult = source.GlobalEnemySpeedMult,
                GlobalTowerRangeMult = source.GlobalTowerRangeMult,
                GlobalTowerDamageMult = source.GlobalTowerDamageMult
            };
            Require(source.Types != null && source.Types.Count > 0, sourcePath, "$.types", "at least one weather type is required");
            for (int i = 0; i < source.Types.Count; i++)
            {
                var type = source.Types[i];
                string path = "$.types[" + i + "]";
                Require(type != null && !string.IsNullOrWhiteSpace(type.Name), sourcePath, path + ".type", "required string is missing");
                Require(!result.Types.ContainsKey(type.Name), sourcePath, path + ".type", "duplicate weather type '" + type.Name + "'");
                NonNegative(type.EnemySpeedMult, sourcePath, path + ".enemySpeedMult");
                NonNegative(type.TowerRangeMult, sourcePath, path + ".towerRangeMult");
                NonNegative(type.TowerDamageMult, sourcePath, path + ".towerDamageMult");
                NonNegative(type.EnemyDotPct, sourcePath, path + ".enemyDotPct");
                Require(type.MinIntensity >= 0f && type.MaxIntensity >= type.MinIntensity, sourcePath, path + ".minIntensity", "intensity range is invalid");
                result.Types.Add(type.Name, type);
            }
            return result;
        }

        internal static List<SkillConfig> LoadSkillDefinitions(string canonicalPath, IEnumerable<string> staticPaths)
        {
            var result = Deserialize<List<SkillConfig>>(ReadRequired(canonicalPath), canonicalPath);
            Require(result != null && result.Count > 0, canonicalPath, "$", "at least one curated skill is required");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < result.Count; i++)
            {
                NormalizeSkillStrings(result[i]);
                ValidateSkill(result[i], canonicalPath, "$[" + i + "]", requireShape: true);
                Require(names.Add(result[i].Name), canonicalPath, "$[" + i + "].Name", "duplicate alias '" + result[i].Name + "'");
            }
            var staticNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<string>(staticPaths ?? Array.Empty<string>());
            ordered.Sort(StringComparer.Ordinal);
            foreach (string path in ordered)
            {
                var skill = Deserialize<SkillConfig>(ReadRequired(path), path);
                NormalizeSkillStrings(skill);
                ValidateSkill(skill, path, "$", requireShape: false);
                Require(staticNames.Add(skill.Name), path, "$.Name", "duplicate static alias '" + skill.Name + "'");
                if (names.Add(skill.Name)) result.Add(skill); // curated has explicit precedence
            }
            return result;
        }

        private static void ValidateMain(GameConfig config, string sourcePath)
        {
            Require(!string.IsNullOrWhiteSpace(config.Player.Name), sourcePath, "$.Player.Name", "required string is missing");
            NonNegative(config.Player.AttackDamage, sourcePath, "$.Player.AttackDamage");
            NonNegative(config.Player.MaxHealth, sourcePath, "$.Player.MaxHealth");
            Require(config.Combo.TriggerThreshold > 0, sourcePath, "$.Combo.triggerThreshold", "must be positive");
            Require(config.Combo.ComboWindowSeconds > 0f, sourcePath, "$.Combo.comboWindowSeconds", "must be positive");
            NonNegative(config.Combo.ComboDamageBonusPerKill, sourcePath, "$.Combo.comboDamageBonusPerKill");
            NonNegative(config.Combo.ComboGoldBonusPerKill, sourcePath, "$.Combo.comboGoldBonusPerKill");
            Require(config.Combo.ComboMaxMultiplier >= 1f, sourcePath, "$.Combo.comboMaxMultiplier", "must be at least one");
            for (int i = 0; i < config.Skills.Count; i++) ValidateSkill(config.Skills[i], sourcePath, "$.Skills[" + i + "]", requireShape: false);
            for (int i = 0; i < config.MonsterTypes.Count; i++)
            {
                var monster = config.MonsterTypes[i];
                string path = "$.MonsterTypes[" + i + "]";
                Require(monster != null && !string.IsNullOrWhiteSpace(monster.Name), sourcePath, path + ".Name", "required string is missing");
                Require(!string.IsNullOrWhiteSpace(monster.Type), sourcePath, path + ".Type", "required string is missing");
                Require(monster.Health > 0f && !float.IsNaN(monster.Health) && !float.IsInfinity(monster.Health), sourcePath, path + ".Health", "must be a finite positive number");
            }
            for (int i = 0; i < config.Levels.Count; i++)
            {
                var level = config.Levels[i];
                string path = "$.Levels[" + i + "]";
                Require(level != null && level.LevelNumber > 0, sourcePath, path + ".LevelNumber", "must be positive");
                Require(level.WaveCount >= 0, sourcePath, path + ".WaveCount", "must be non-negative");
                level.Waves = level.Waves ?? new List<WaveConfig>();
            }
            for (int i = 0; i < config.TowerTypes.Count; i++)
            {
                var tower = config.TowerTypes[i];
                string path = "$.Towers[" + i + "]";
                Require(tower != null && !string.IsNullOrWhiteSpace(tower.Name), sourcePath, path + ".Name", "required string is missing");
                NonNegative(tower.Damage, sourcePath, path + ".Damage");
                NonNegative(tower.AttackSpeed, sourcePath, path + ".AttackSpeed");
                Require(tower.ActiveSkillId >= -1, sourcePath, path + ".ActiveSkillId", "must be -1 or a catalog id");
                NonNegative(tower.ActiveCooldown, sourcePath, path + ".ActiveCooldown");
            }
        }

        private static void ValidateSkill(SkillConfig skill, string sourcePath, string path, bool requireShape)
        {
            Require(skill != null && !string.IsNullOrWhiteSpace(skill.Name), sourcePath, path + ".Name", "required string is missing");
            if (requireShape || !string.IsNullOrWhiteSpace(skill.AreaShape))
                Require(IsKnownShape(skill.AreaShape), sourcePath, path + ".AreaShape", "unknown shape '" + skill.AreaShape + "'");
            NonNegative(skill.Cooldown, sourcePath, path + ".Cooldown");
            NonNegative(skill.ManaCost, sourcePath, path + ".ManaCost");
            NonNegative(skill.AreaRadius, sourcePath, path + ".AreaRadius");
            NonNegative(skill.HealPercent, sourcePath, path + ".HealPercent");
            NonNegative(skill.ShieldAmount, sourcePath, path + ".ShieldAmount");
            NonNegative(skill.ShieldDuration, sourcePath, path + ".ShieldDuration");
        }

        private static void NormalizeSkillStrings(SkillConfig skill)
        {
            if (skill == null) return;
            skill.Name = skill.Name ?? "";
            skill.Description = skill.Description ?? "";
            skill.AreaShape = skill.AreaShape ?? "";
            skill.Hotkey = skill.Hotkey ?? "";
            skill.SummonDefId = skill.SummonDefId ?? "";
            skill.Modifiers = skill.Modifiers ?? new List<SkillModifierDef>();
        }

        private static void NormalizeLegacyPlayerSkill(SkillConfig skill)
        {
            if (skill == null) return;
            // These fields were not active in the legacy game_config parser. Keeping them
            // inert prevents a parser cutover from changing activation or effect behavior.
            skill.AutoCast = false;
            skill.DotDuration = 0f;
            skill.DotTickInterval = 0f;
            skill.DotDamagePerTick = 0f;
            skill.PolymorphDuration = 0f;
            skill.PolymorphDamageTakenMultiplier = 1f;
            skill.SummonDefId = null;
            skill.ConeAngleDegrees = 0f;
            skill.Modifiers = new List<SkillModifierDef>();
        }

        private static bool IsKnownShape(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            switch (value.Trim().ToLowerInvariant())
            {
                case "single": case "circle": case "box": case "line": case "cross": case "cone":
                case "chain": case "heal": case "shield": case "slow": case "freeze": case "groundtarget":
                case "chainheal": case "massresurrect": case "aoestun": case "aoeroot":
                case "aoeknockback": case "timerwind": return true;
                default: return false;
            }
        }

        private static T Deserialize<T>(string json, string sourcePath)
        {
            try { return JsonSerializer.Deserialize<T>(json, Options); }
            catch (JsonException error)
            {
                throw new CatalogValidationException(sourcePath + ":" + (error.Path ?? "$") + ": " + error.Message);
            }
        }

        private static string ReadRequired(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new CatalogValidationException(path + ": configuration file not found");
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) throw new CatalogValidationException(path + ": configuration file is empty");
            return json;
        }

        private static void NonNegative(float value, string sourcePath, string nodePath)
        {
            AtLeast(value, 0f, sourcePath, nodePath);
        }

        private static void AtLeast(float value, float minimum, string sourcePath, string nodePath)
        {
            Require(!float.IsNaN(value) && !float.IsInfinity(value) && value >= minimum, sourcePath, nodePath,
                "must be a finite number greater than or equal to " + minimum);
        }

        private static void Require(bool condition, string sourcePath, string nodePath, string message)
        {
            if (!condition) throw new CatalogValidationException(sourcePath + ":" + nodePath + ": " + message);
        }

        private static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, IncludeFields = true };
            options.Converters.Add(new TowerTypeConverter());
            return options;
        }

        private sealed class GameConfigSource
        {
            public PlayerConfig Player { get; set; }
            public List<MonsterConfig> MonsterTypes { get; set; }
            public List<LevelConfig> Levels { get; set; }
            public List<SkillConfig> Skills { get; set; }
            public List<GlobalSkillDef> GlobalSkills { get; set; }
            public List<TowerConfig> Towers { get; set; }
            public ComboConfig Combo { get; set; }
            public TowerOverchargeConfig TowerOvercharge { get; set; }
            public PositionalDamageConfig PositionalDamage { get; set; }
        }

        private sealed class EnemyAbilitySource
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Description { get; set; }
            public string AbilityType { get; set; }
            public string BuffStat { get; set; }
            public float Cooldown { get; set; }
            public float CooldownRemaining { get; set; }
            public int AoeRadius { get; set; }
            public float DamageMultiplier { get; set; }
            public float HealAmount { get; set; }
            public int BuffDuration { get; set; }
            public float SilenceRadius { get; set; }
            public float SilenceDuration { get; set; }
            public float DispelRadius { get; set; }
            public float DispelDuration { get; set; }
            public float DispelImmunityDuration { get; set; }
            [JsonExtensionData]
            public Dictionary<string, JsonElement> LegacyFields { get; set; }
        }

        private sealed class WeatherSource
        {
            public float GlobalEnemySpeedMult { get; set; } = 1f;
            public float GlobalTowerRangeMult { get; set; } = 1f;
            public float GlobalTowerDamageMult { get; set; } = 1f;
            public List<WeatherTypeSource> Types { get; set; }
        }

        private sealed class WeatherTypeSource : WeatherTypeConfig
        {
            [JsonPropertyName("type")]
            public string TypeName { set { Name = value; } }
        }

        private sealed class TowerTypeConverter : JsonConverter<TowerType>
        {
            private static readonly HashSet<string> BasicAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Acid", "Chrome", "Cryo", "Cyber", "Doom", "Drone", "Gravity", "Hacker",
                "Hologram", "Ion", "Mech", "Nano", "Neon", "Phase", "Plasma", "Pulse",
                "Railgun", "Repair", "Shock", "Virus"
            };

            public override TowerType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int number) && Enum.IsDefined(typeof(TowerType), number))
                    return (TowerType)number;
                if (reader.TokenType != JsonTokenType.String) throw new JsonException("tower Type must be a string or defined numeric value");
                string value = reader.GetString();
                if (Enum.TryParse(value, true, out TowerType parsed) && Enum.IsDefined(typeof(TowerType), parsed)) return parsed;
                if (BasicAliases.Contains(value)) return TowerType.Basic;
                throw new JsonException("unknown tower Type alias '" + value + "'");
            }

            public override void Write(Utf8JsonWriter writer, TowerType value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
        }
    }
}
