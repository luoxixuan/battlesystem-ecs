using System;
using System.IO;
using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;
using BattleSystemECS.Core.GAS;

namespace BattleSystemECS.Config
{
    public class GameConfigLoader
    {
        private const string CONFIG_FILE = "game_config.json";

        public static GameConfig LoadStrictCatalog(IRenderer renderer, string canonicalPath = null, string staticDirectory = null)
        {
            string canonical = canonicalPath ?? Path.Combine("Data", "Configs", "skills.json");
            string directory = staticDirectory ?? Path.Combine("Data", "Skills");
            var files = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json") : throw new CatalogValidationException($"{directory}: static skill directory not found");
            var catalog = CatalogCompiler.Compile(canonical, files);
            var config = LoadConfigStrict(renderer);
            catalog = CatalogCompiler.CompileEnemyExtensions(catalog, config.EnemyAbilities);
            config.CompiledCatalog = catalog;
            string configDirectory = Path.GetDirectoryName(canonical) ?? Path.Combine("Data", "Configs");
            ValidateStrictReferences(config, catalog, Path.Combine(configDirectory, "hero_skills.json"));
            config.StrictCatalogReferences = true;
            return config;
        }

        public static GameConfig LoadConfig(IRenderer renderer)
        {
            return LoadConfigInternal(renderer, strict: false);
        }

        /// <summary>
        /// Production bootstrap variant. Configuration errors are reported as a structured
        /// validation exception; the compatibility loader remains the only path allowed to
        /// synthesize defaults after an input failure.
        /// </summary>
        public static GameConfig LoadConfigStrict(IRenderer renderer)
        {
            return LoadConfigInternal(renderer, strict: true);
        }

        private static GameConfig LoadConfigInternal(IRenderer renderer, bool strict)
        {
            try
            {
                if (!File.Exists(CONFIG_FILE))
                {
                    RequireStrictInput(strict, CONFIG_FILE, "configuration file not found");
                    renderer.Log("[CONFIG] Configuration file not found: " + CONFIG_FILE);
                    renderer.Log("[CONFIG] Using default configuration");
                    return GetDefaultConfig();
                }

                string jsonContent = File.ReadAllText(CONFIG_FILE);

                if (string.IsNullOrWhiteSpace(jsonContent))
                {
                    RequireStrictInput(strict, CONFIG_FILE, "configuration file is empty");
                    renderer.Log("[CONFIG] Configuration file is empty: " + CONFIG_FILE);
                    renderer.Log("[CONFIG] Using default configuration");
                    return GetDefaultConfig();
                }

                using (var document = System.Text.Json.JsonDocument.Parse(jsonContent))
                    RequireJsonKind(strict, CONFIG_FILE, document.RootElement,
                        System.Text.Json.JsonValueKind.Object, "a JSON object");

                var gameConfig = ParseGameConfig(jsonContent);

                // Load behavior trees
                LoadBehaviorTrees(gameConfig, renderer, strict);

                // Load enemy abilities
                LoadEnemyAbilities(gameConfig, renderer, strict);

                // Load phase behaviors
                LoadPhaseBehaviors(gameConfig, renderer, strict);

                // Load weather config
                LoadWeatherConfig(gameConfig, renderer, strict);

                // Load terrain config
                LoadTerrainConfig(gameConfig, renderer, strict);

                // Load wave mutators config
                LoadWaveMutatorsConfig(gameConfig, renderer, strict);

                // Load pickup definitions
                LoadPickupDefs(gameConfig, renderer, strict);

                // Load inventory item definitions (Round 130)
                LoadItemDefs(gameConfig, renderer, strict);
                LoadCraftingRecipes(gameConfig, renderer, strict);

                // Load shared skill definition table (SkillDefs): curated skills from
                // Data/Configs/skills.json + per-file static defs from Data/Skills/*.json,
                // deduplicated by name (curated wins). Consumed by name-lookup paths
                // (HeroSkillSystem, TowerActiveSkillSystem); player skill bar (Skills)
                // still comes from game_config.json above.
                LoadSkillDefs(gameConfig, renderer, strict);

                // Load enemy fission definitions
                LoadFissionDefs(gameConfig, renderer, strict);

                // Load enemy morph definitions
                LoadMorphDefs(gameConfig, renderer, strict);

                // Load corpse ground effect definitions (direction 9)
                LoadCorpseEffectDefs(gameConfig, renderer, strict);

                // Load elemental terrain zone definitions (Direction 2 — Round 200)
                LoadTerrainZoneDefs(gameConfig, renderer, strict);

                // Load tower affix (reforge) definitions (Round 34, Reforge — Split A)
                LoadTowerAffixDefs(gameConfig, renderer, strict);

                // Load tower-vs-enemy type effectiveness matrix (Round 143 Direction 1)
                LoadTowerEffectiveness(gameConfig, renderer, strict);

                // Load per-tower modifier pool (Round 145 Direction 3 — 塔类型专精重随)
                // The system rolls ONE modifier per tower at placement time from this
                // weighted pool. Missing file → safe empty pool → towers spawn with
                // ModifierId == -1 (a no-op fast path).
                LoadTowerModifiers(gameConfig, renderer, strict);

                // Load summon definitions (direction 1: player-summoned combat units)
                LoadSummonDefs(gameConfig, renderer, strict);

                // Load random mid-wave event definitions (direction 9)
                LoadRandomEventDefs(gameConfig, renderer, strict);

                // Load daily challenge modifier pool (Round 105 Direction 9)
                // and resolve today's daily seed into the GameConfig. Safe no-op
                // when the JSON is missing or the pool is empty — the daily
                // system is opt-in.
                LoadDailyModifierPool(gameConfig, renderer, strict);
                ResolveDailyChallenge(gameConfig, renderer);

                // Round175 Direction1 — Mana Shield config (mana → damage shield)
 LoadManaShieldConfig(gameConfig, renderer, strict);

 // Round178 Direction6 — Pre-fight Buff Selection (BuildPhase末「3选1」出战 buff)
 // Reads Data/Configs/prefight_buffs.json if present; otherwise the GameConfig
 // keeps its coded PreFightBuffConfig defaults (Enabled=true, OptionsPerWave=3,
 // Pool=Array.Empty<PreFightBuffOptionDef>()). All knobs are optional.
 LoadPreFightBuffConfig(gameConfig, renderer, strict);

 // Round174+ Direction3 — Momentum (global per-(wave-time) ramping damage /
 // attack-speed buff). Reads Data/Configs/momentum.json if present; otherwise
 // the GameConfig keeps its coded MomentumConfig defaults (Enabled=true,
 // TierDuration=30s, MaxTiers=10, DamageBonusPerTier=0.02, SpeedBonusPerTier
 // =0.01, ResetOnWave=true). All knobs are optional.
 LoadMomentumConfig(gameConfig, renderer, strict);

// Round 207 Direction 2 — Adrenaline (low-HP / critical-HP player-side buff +
// one-shot Rush). Reads Data/Configs/adrenaline.json if present; otherwise
// the GameConfig keeps its coded AdrenalineConfig defaults. All knobs are optional.
LoadAdrenalineConfig(gameConfig, renderer, strict);

 // Round 178+ Direction 5 — Tide / Crest (wave-indexed periodic buffs).
 // Reads Data/Configs/crests.json if present; otherwise the GameConfig
 // keeps its coded CrestConfig defaults (Enabled=true, Crests=Array.Empty
 // <CrestDef>()). The JSON file ships a small default roster (CrestOfFury
 // / CrestOfBounty / TideOfHealing / CrestOfFortitude) that gets the
 // system working out of the box.
 LoadCrestConfig(gameConfig, renderer, strict);

 // Load damage saturation tunables (Round92 Direction1: per-enemy diminishing returns
 // on incoming damage within a short rolling window). All three knobs are optional —
 // missing fields fall back to the safe defaults in DamageSaturationConfig.
 LoadDamageSaturationConfig(gameConfig, renderer, strict);

                // Load destructible object definitions (Round 95 Direction 5: tower-attackable objects
                // with on-destroy effects like gold drop or AoE explosion).
                LoadDestructibleDefs(gameConfig, renderer, strict);

                // Load mark subsystem config (Round 107 Direction 6: target mark debuff).
                // Opt-in: missing JSON file or missing fields fall back to MarkSubsystemConfig
                // safe defaults (decay=1.0s, cap=100, no per-mark type registered).
                LoadMarkConfig(gameConfig, renderer, strict);

                if (gameConfig == null)
                {
                    RequireStrictInput(strict, CONFIG_FILE, "parser returned null");
                    renderer.Log("[ERROR] Failed to parse configuration: parser returned null");
                    renderer.Log("[CONFIG] Using default configuration");
                    return GetDefaultConfig();
                }

                renderer.Log("[CONFIG] Successfully loaded configuration from " + CONFIG_FILE);
                renderer.Log("[CONFIG]   - " + gameConfig.MonsterTypes.Count + " monster types");
                renderer.Log("[CONFIG]   - " + gameConfig.Levels.Count + " levels");
                renderer.Log("[CONFIG]   - " + gameConfig.BehaviorTrees.Count + " behavior trees");

                return gameConfig;
            }
            catch (Exception ex)
            {
                if (strict)
                {
                    if (ex is CatalogValidationException) throw;
                    throw ConfigLoadFailure(CONFIG_FILE, ex.Message);
                }
                renderer.Log("[ERROR] Failed to load configuration from " + CONFIG_FILE + ": " + ex.Message);
                return GetDefaultConfig();
            }
        }

        internal static void ValidateStrictReferences(GameConfig config, GameplayCatalog catalog, string heroSkillsPath)
        {
            if (config == null) throw new CatalogValidationException($"{CONFIG_FILE}: configuration is null");
            if (config.Player == null) throw new CatalogValidationException($"{CONFIG_FILE}: missing Player configuration");
            if (config.MonsterTypes == null || config.MonsterTypes.Count == 0) throw new CatalogValidationException($"{CONFIG_FILE}: missing MonsterTypes");
            if (config.Levels == null || config.Levels.Count == 0) throw new CatalogValidationException($"{CONFIG_FILE}: missing Levels");
            string heroJson = ReadStrictJsonObject(heroSkillsPath, "hero skill configuration");
            HeroSkillSystem.HeroSkillsConfigDef hero;
            try { hero = HeroSkillSystem.HeroSkillsConfigLoader.Parse(heroJson); }
            catch (Exception error) { throw ConfigLoadFailure(heroSkillsPath, error.Message); }
            if (hero == null || hero.Skills == null) throw ConfigLoadFailure(heroSkillsPath, "invalid hero skill configuration");
            if (hero.Skills.Count == 0) throw ConfigLoadFailure(heroSkillsPath, "no hero skill bindings declared");
            var slots = new HashSet<int>();
            foreach (var slot in hero.Skills)
            {
                if (slot.SlotIndex < 0) throw ConfigLoadFailure(heroSkillsPath, $"invalid SlotIndex {slot.SlotIndex}");
                if (!slots.Add(slot.SlotIndex)) throw ConfigLoadFailure(heroSkillsPath, $"duplicate SlotIndex {slot.SlotIndex}");
                if (string.IsNullOrWhiteSpace(slot.SkillName)) throw ConfigLoadFailure(heroSkillsPath, $"missing SkillName at slot {slot.SlotIndex}");
                RequireClosedAlias(catalog, slot.SkillName,
                    $"{ConfigPathLabel(heroSkillsPath)}: slot {slot.SlotIndex}");
            }

            for (int i = 0; i < config.Skills.Count; i++)
                RequireClosedAlias(catalog, config.Skills[i]?.Name, $"{CONFIG_FILE}: Skills[{i}]");

            for (int i = 0; i < config.GlobalSkills.Count; i++)
                RequireClosedAlias(catalog, config.GlobalSkills[i]?.Name, $"{CONFIG_FILE}: GlobalSkills[{i}]");

            for (int i = 0; i < config.TowerTypes.Count; i++)
            {
                var tower = config.TowerTypes[i];
                if (tower == null) throw new CatalogValidationException($"{CONFIG_FILE}: Towers[{i}] is null");
                if (tower.ActiveSkillId >= 0)
                {
                    var skill = config.TryGetSkillById(tower.ActiveSkillId);
                    if (skill == null) throw new CatalogValidationException($"{CONFIG_FILE}: Towers[{i}] ActiveSkillId {tower.ActiveSkillId} is unknown");
                    RequireClosedAlias(catalog, skill.Name, $"{CONFIG_FILE}: Towers[{i}].ActiveSkillId");
                }
                if (tower.SpecialAbility != null && !string.IsNullOrWhiteSpace(tower.SpecialAbility.AbilityType))
                    RequireClosedAlias(catalog, NormalizeAlias(tower.SpecialAbility.AbilityType), $"{CONFIG_FILE}: Towers[{i}].SpecialAbility");
            }

            if (config.AutoSkill != null && config.AutoSkill.Enabled && config.Skills.Count == 0)
                throw new CatalogValidationException($"{CONFIG_FILE}: AutoSkill requires at least one catalog-backed skill");

            var enemyById = new Dictionary<string, EnemyAbilityDef>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < config.EnemyAbilities.Count; i++)
            {
                var enemy = config.EnemyAbilities[i];
                if (enemy == null || string.IsNullOrWhiteSpace(enemy.Id))
                    throw new CatalogValidationException($"Data/Configs/enemy_abilities.json: missing Id at index {i}");
                if (!enemyById.TryAdd(enemy.Id, enemy))
                    throw new CatalogValidationException($"Data/Configs/enemy_abilities.json: duplicate Id '{enemy.Id}'");
                var byName = RequireClosedAlias(catalog, enemy.Name, $"Data/Configs/enemy_abilities.json: {enemy.Id}");
                var byId = RequireClosedAlias(catalog, enemy.Id, $"Data/Configs/enemy_abilities.json: {enemy.Id}");
                ValidateEnemyExecution(catalog, byId, enemy);
            }

            foreach (var tree in config.BehaviorTrees)
            {
                if (tree.Value?.Nodes == null) continue;
                foreach (var node in tree.Value.Nodes)
                {
                    string reference = node.Value?.AbilityId;
                    if (string.IsNullOrWhiteSpace(reference)) continue;
                    if (!enemyById.ContainsKey(reference))
                        throw new CatalogValidationException($"Data/Configs/behavior_trees.json: unknown AbilityId '{reference}' in tree '{tree.Key}'");
                    RequireClosedAlias(catalog, reference, $"Data/Configs/behavior_trees.json: tree '{tree.Key}' node '{node.Key}'");
                }
            }

            foreach (var monster in config.MonsterTypes)
            {
                if (monster?.Phases == null) continue;
                foreach (var phase in monster.Phases)
                {
                    if (string.IsNullOrWhiteSpace(phase?.AbilityId)) continue;
                    if (!enemyById.ContainsKey(phase.AbilityId))
                        throw new CatalogValidationException($"{CONFIG_FILE}: monster '{monster.Name}' references unknown phase AbilityId '{phase.AbilityId}'");
                    RequireClosedAlias(catalog, phase.AbilityId, $"{CONFIG_FILE}: monster '{monster.Name}' phase");
                }
            }

            foreach (var phase in config.PhaseBehaviors)
                foreach (string ability in phase.Value?.UnlockAbilities ?? new List<string>())
                    RequireClosedAlias(catalog, ability, $"Data/Configs/phase_behavior.json: phase '{phase.Key}'");
        }

        private static AbilityId RequireClosedAlias(GameplayCatalog catalog, string alias, string source)
        {
            if (string.IsNullOrWhiteSpace(alias) || !catalog.TryResolveAlias(alias, out var abilityId) ||
                !catalog.TryGetAbility(abilityId, out var ability))
                throw new CatalogValidationException($"{source}: unknown catalog alias '{alias}'");
            int targetingId = ability.Targeting.Id.Value;
            if ((uint)targetingId >= (uint)catalog.Targetings.Count || catalog.Targetings[targetingId].Id.Value != targetingId)
                throw new CatalogValidationException($"{source}: ability '{alias}' has an unclosed TargetingId");
            foreach (var execution in ability.Executions)
                if (!catalog.TryGetExecution(execution, out _)) throw new CatalogValidationException($"{source}: ability '{alias}' has an unclosed ExecutionId {execution.Value}");
            foreach (var effect in ability.Effects)
                if (!catalog.TryGetEffect(effect, out _)) throw new CatalogValidationException($"{source}: ability '{alias}' has an unclosed EffectId {effect.Value}");
            foreach (var trigger in ability.TriggerRefs)
                if (!catalog.TryGetTrigger(trigger, out _)) throw new CatalogValidationException($"{source}: ability '{alias}' has an unclosed TriggerId {trigger.Value}");
            return abilityId;
        }

        private static void ValidateEnemyExecution(GameplayCatalog catalog, AbilityId abilityId, EnemyAbilityDef source)
        {
            if (!EnemyAbilityTypeRegistry.TryResolve(source.AbilityType, out var type))
                throw new CatalogValidationException($"Data/Configs/enemy_abilities.json: unsupported AbilityType '{source.AbilityType}' for '{source.Id}'");
            if (!type.Payload.HasValue) return;
            catalog.TryGetAbility(abilityId, out var ability);
            foreach (var executionId in ability.Executions)
                if (catalog.TryGetExecution(executionId, out var execution) && execution.Payload == type.Payload.Value &&
                    execution.Operation == type.Operation) return;
            throw new CatalogValidationException($"Data/Configs/enemy_abilities.json: '{source.Id}' requires typed {type.Payload.Value}/{type.Operation} execution");
        }

        private static string NormalizeAlias(string value) => value.Replace('_', ' ').Replace('-', ' ');

        private static CatalogValidationException ConfigLoadFailure(string path, string reason)
        {
            return new CatalogValidationException($"{ConfigPathLabel(path)}: {reason}");
        }

        private static string ConfigPathLabel(string path)
        {
            string fullPath;
            try { fullPath = Path.GetFullPath(path); }
            catch { fullPath = path; }
            return $"{path} ({fullPath})";
        }

        private static void ThrowIfStrict(bool strict, string path, Exception error)
        {
            if (!strict) return;
            if (error is CatalogValidationException) throw error;
            throw ConfigLoadFailure(path, error.Message);
        }

        private static void RequireStrictInput(bool strict, string path, string reason)
        {
            if (strict) throw ConfigLoadFailure(path, reason);
        }

        private static void RequireJsonKind(bool strict, string path, System.Text.Json.JsonElement root,
            System.Text.Json.JsonValueKind expectedKind, string expectedDescription)
        {
            if (root.ValueKind == expectedKind) return;
            string reason = "expected " + expectedDescription;
            if (strict) throw ConfigLoadFailure(path, reason);
            throw new InvalidDataException(path + ": " + reason);
        }

        private static string ReadStrictJsonObject(string path, string description)
        {
            try
            {
                if (!File.Exists(path)) throw ConfigLoadFailure(path, description + " not found");
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) throw ConfigLoadFailure(path, description + " is empty");
                using (var document = System.Text.Json.JsonDocument.Parse(json))
                    RequireJsonKind(true, path, document.RootElement,
                        System.Text.Json.JsonValueKind.Object, "an object");
                return json;
            }
            catch (Exception error)
            {
                if (error is CatalogValidationException) throw;
                throw ConfigLoadFailure(path, error.Message);
            }
        }

        private static void LoadBehaviorTrees(GameConfig gameConfig, IRenderer renderer, bool strict)
        {
            const string btFile = "Data/Configs/behavior_trees.json";
            try
            {
                if (!File.Exists(btFile))
                {
                    RequireStrictInput(strict, btFile, "behavior tree configuration not found");
                    renderer.Log("[BT] Behavior trees file not found: " + btFile + ", using empty map");
                    return;
                }
                string json = File.ReadAllText(btFile);
                if (string.IsNullOrWhiteSpace(json))
                {
                    RequireStrictInput(strict, btFile, "behavior tree configuration is empty");
                    renderer.Log("[BT] Behavior trees file is empty: " + btFile);
                    return;
                }
                using (var document = System.Text.Json.JsonDocument.Parse(json))
                    RequireJsonKind(strict, btFile, document.RootElement,
                        System.Text.Json.JsonValueKind.Array, "an array");
                ParseBehaviorTrees(gameConfig, json);
                renderer.Log("[BT] Loaded " + gameConfig.BehaviorTrees.Count + " behavior trees from " + btFile);
            }
            catch (Exception ex)
            {
                ThrowIfStrict(strict, btFile, ex);
                renderer.Log("[BT] Failed to load behavior trees: " + ex.Message);
            }
        }

        private static void LoadEnemyAbilities(GameConfig gameConfig, IRenderer renderer, bool strict)
        {
            const string abFile = "Data/Configs/enemy_abilities.json";
            try
            {
                if (!File.Exists(abFile))
                {
                    RequireStrictInput(strict, abFile, "enemy ability configuration not found");
                    renderer.Log("[ABILITY] Enemy abilities file not found: " + abFile + ", using empty list");
                    return;
                }
                string json = File.ReadAllText(abFile);
                if (string.IsNullOrWhiteSpace(json))
                {
                    RequireStrictInput(strict, abFile, "enemy ability configuration is empty");
                    renderer.Log("[ABILITY] Enemy abilities file is empty: " + abFile);
                    return;
                }
                using (var document = System.Text.Json.JsonDocument.Parse(json))
                    RequireJsonKind(strict, abFile, document.RootElement,
                        System.Text.Json.JsonValueKind.Array, "an array");
                ParseEnemyAbilities(gameConfig, json);
                if (strict && gameConfig.EnemyAbilities.Count == 0)
                    throw ConfigLoadFailure(abFile, "no enemy abilities declared");
                renderer.Log("[ABILITY] Loaded " + gameConfig.EnemyAbilities.Count + " enemy abilities from " + abFile);
            }
            catch (Exception ex)
            {
                ThrowIfStrict(strict, abFile, ex);
                renderer.Log("[ABILITY] Failed to load enemy abilities: " + ex.Message);
            }
        }

        private static void LoadPhaseBehaviors(GameConfig gameConfig, IRenderer renderer, bool strict)
        {
            const string phaseFile = "Data/Configs/phase_behavior.json";
            try
            {
                if (!File.Exists(phaseFile))
                {
                    RequireStrictInput(strict, phaseFile, "phase behavior configuration not found");
                    renderer.Log("[PHASE] Phase behavior file not found: " + phaseFile + ", using defaults");
                    return;
                }
                string json = File.ReadAllText(phaseFile);
                if (string.IsNullOrWhiteSpace(json))
                {
                    RequireStrictInput(strict, phaseFile, "phase behavior configuration is empty");
                    renderer.Log("[PHASE] Phase behavior file is empty: " + phaseFile);
                    return;
                }
                using (var document = System.Text.Json.JsonDocument.Parse(json))
                    RequireJsonKind(strict, phaseFile, document.RootElement,
                        System.Text.Json.JsonValueKind.Object, "an object");
                ParsePhaseBehaviors(gameConfig, json);
                renderer.Log("[PHASE] Loaded " + gameConfig.PhaseBehaviors.Count + " phase behaviors from " + phaseFile);
            }
            catch (Exception ex)
            {
                ThrowIfStrict(strict, phaseFile, ex);
                renderer.Log("[PHASE] Failed to load phase behaviors: " + ex.Message);
            }
        }

        private static void ParsePhaseBehaviors(GameConfig gameConfig, string json)
        {
            // Parse top-level keys (phase names)
            int pos = 0;
            while (pos < json.Length)
            {
                // Find key: "PhaseName": {
                while (pos < json.Length && (char.IsWhiteSpace(json[pos]) || json[pos] == ',')) pos++;
                if (pos >= json.Length) break;
                if (json[pos] != '"') { pos++; continue; }

                int keyStart = pos + 1;
                int keyEnd = json.IndexOf('"', keyStart);
                if (keyEnd < 0) break;
                string phaseName = json.Substring(keyStart, keyEnd - keyStart);
                pos = keyEnd + 1;

                // Skip to {
                while (pos < json.Length && json[pos] != ':') pos++;
                if (pos >= json.Length) break;
                pos++;
                while (pos < json.Length && char.IsWhiteSpace(json[pos])) pos++;
                if (pos >= json.Length || json[pos] != '{') { pos++; continue; }

                int objEnd = FindMatchingBrace(json, pos);
                if (objEnd < 0) { pos++; continue; }
                string objJson = json.Substring(pos + 1, objEnd - pos - 1);

                var def = new PhaseBehaviorDef();
                def.Description = ExtractString(objJson, "Description");
                def.EnterMessage = ExtractString(objJson, "enterMessage");
                def.AutoAdvance = ExtractBool(objJson, "autoAdvance");
                def.UnlockTowers = ParseStringList(objJson, "unlockTowers");
                def.UnlockAbilities = ParseStringList(objJson, "unlockAbilities");
                def.IntermissionDelayMs = ExtractInt(objJson, "intermissionDelayMs");
                def.WaveStartMessage = ExtractString(objJson, "waveStartMessage");
                def.TurnIntervalMs = ExtractInt(objJson, "turnIntervalMs");
                def.NextWaveMessage = ExtractString(objJson, "nextWaveMessage");
                def.AutoAdvanceToBuild = ExtractBool(objJson, "autoAdvanceToBuild");
                def.AdvanceDelayMs = ExtractInt(objJson, "advanceDelayMs");
                def.ShowStats = ExtractBool(objJson, "showStats");

                gameConfig.PhaseBehaviors[phaseName] = def;
                pos = objEnd + 1;
            }
        }

        private static List<string> ParseStringList(string json, string key)
        {
            var result = new List<string>();
            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern);
            if (idx < 0) return result;
            int bracket = json.IndexOf('[', idx);
            if (bracket < 0) return result;
            int endBracket = FindMatchingBrace(json, bracket);
            if (endBracket < 0) return result;
            string arrJson = json.Substring(bracket + 1, endBracket - bracket - 1);

            int p = 0;
            while (p < arrJson.Length)
            {
                while (p < arrJson.Length && (char.IsWhiteSpace(arrJson[p]) || arrJson[p] == ',')) p++;
                if (p >= arrJson.Length) break;
                if (arrJson[p] == '"')
                {
                    int s = p + 1;
                    int e = arrJson.IndexOf('"', s);
                    if (e < 0) break;
                    result.Add(arrJson.Substring(s, e - s));
                    p = e + 1;
                }
                else p++;
            }
            return result;
        }

        private static bool ExtractBool(string json, string key)
        {
            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern);
            if (idx < 0) return false;
            int colon = json.IndexOf(':', idx);
            if (colon < 0) return false;
            int start = colon + 1;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            int end = start;
            while (end < json.Length && char.IsLetter(json[end])) end++;
            string val = json.Substring(start, end - start).Trim();
            return val.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static void ParseEnemyAbilities(GameConfig gameConfig, string jsonArray)
        {
            // Simple JSON array parsing for enemy abilities
            int pos = 0;
            while (pos < jsonArray.Length)
            {
                // Find next '{'
                int objStart = jsonArray.IndexOf('{', pos);
                if (objStart < 0) break;
                int objEnd = jsonArray.IndexOf('}', objStart);
                if (objEnd < 0) break;

                string objJson = jsonArray.Substring(objStart, objEnd - objStart + 1);
                var ability = ParseEnemyAbility(objJson);
                if (ability != null)
                    gameConfig.EnemyAbilities.Add(ability);

                pos = objEnd + 1;
            }
        }

        private static EnemyAbilityDef ParseEnemyAbility(string json)
        {
            var ability = new EnemyAbilityDef();
            ability.Id = ExtractString(json, "Id");
            ability.Name = ExtractString(json, "Name");
            ability.Description = ExtractString(json, "Description");
            ability.AbilityType = ExtractString(json, "AbilityType");
            ability.BuffStat = ExtractString(json, "BuffStat");
            ability.Cooldown = ExtractFloat(json, "Cooldown");
            ability.CooldownRemaining = ExtractFloat(json, "CooldownRemaining");
            ability.AoeRadius = ExtractInt(json, "AoeRadius");
            ability.DamageMultiplier = ExtractFloat(json, "DamageMultiplier");
            ability.HealAmount = ExtractFloat(json, "HealAmount");
            ability.BuffDuration = ExtractInt(json, "BuffDuration");
            return ability;
        }

        private static string ExtractString(string json, string key)
        {
            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern);
            if (idx < 0) return null;
            int colon = json.IndexOf(':', idx);
            if (colon < 0) return null;
            int start = json.IndexOf('"', colon);
            if (start < 0) return null;
            int end = json.IndexOf('"', start + 1);
            if (end < 0) return null;
            return json.Substring(start + 1, end - start - 1);
        }

        private static float ExtractFloat(string json, string key)
        {
            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern);
            if (idx < 0) return 0f;
            int colon = json.IndexOf(':', idx);
            if (colon < 0) return 0f;
            int start = colon + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;
            int end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '.' || json[end] == '-')) end++;
            if (end == start) return 0f;
            if (float.TryParse(json.Substring(start, end - start), out float val)) return val;
            return 0f;
        }

        private static int ExtractInt(string json, string key)
        {
            return (int)ExtractFloat(json, key);
        }

        private static int ExtractInt(string json, string key, int fallback)
        {
            int value = ExtractInt(json, key);
            return value == 0 && json.IndexOf('"' + key + '"', StringComparison.OrdinalIgnoreCase) < 0 ? fallback : value;
        }


        private static void ParseBehaviorTrees(GameConfig gameConfig, string jsonArray)
        {
            int pos = 0;
            while (pos < jsonArray.Length)
            {
                while (pos < jsonArray.Length && (char.IsWhiteSpace(jsonArray[pos]) || jsonArray[pos] == ',')) pos++;
                if (pos >= jsonArray.Length) break;
                if (jsonArray[pos] == '{')
                {
                    int objEnd = FindMatchingBrace(jsonArray, pos);
                    if (objEnd == -1) break;
                    string btJson = jsonArray.Substring(pos, objEnd - pos);
                    var bt = ParseOneBehaviorTree(btJson);
                    if (bt != null && !string.IsNullOrEmpty(bt.MonsterType))
                        gameConfig.BehaviorTrees[bt.MonsterType] = bt;
                    pos = objEnd + 1;
                }
                else
                {
                    pos++;
                }
            }
        }

        private static BehaviorTreeDef ParseOneBehaviorTree(string json)
        {
            var bt = new BehaviorTreeDef();
            bt.Nodes = new Dictionary<string, BTNodeDef>();

            bt.MonsterType = ExtractString(json, "MonsterType");
            bt.RootId = ExtractString(json, "RootId");

            // Parse Nodes object
            string nodesKeyPattern = "\"Nodes\":";
            int nodesIdx = json.IndexOf(nodesKeyPattern);
            if (nodesIdx == -1) return bt;

            int nodesBrace = json.IndexOf("{", nodesIdx);
            if (nodesBrace == -1) return bt;
            int nodesEnd = FindMatchingBrace(json, nodesBrace);
            if (nodesEnd == -1) return bt;

            string nodesJson = json.Substring(nodesBrace + 1, nodesEnd - nodesBrace - 1);

            int nodePos = 0;
            while (nodePos < nodesJson.Length)
            {
                while (nodePos < nodesJson.Length && (char.IsWhiteSpace(nodesJson[nodePos]) || nodesJson[nodePos] == ',')) nodePos++;
                if (nodePos >= nodesJson.Length) break;
                if (nodesJson[nodePos] == '"')
                {
                    // Key: "nodeId"
                    int keyStart = nodePos + 1;
                    int keyEnd = nodesJson.IndexOf('"', keyStart);
                    if (keyEnd == -1) break;
                    string nodeId = nodesJson.Substring(keyStart, keyEnd - keyStart);
                    nodePos = keyEnd + 1;

                    // Find :
                    while (nodePos < nodesJson.Length && nodesJson[nodePos] != ':') nodePos++;
                    if (nodePos >= nodesJson.Length) break;
                    nodePos++;
                    while (nodePos < nodesJson.Length && char.IsWhiteSpace(nodesJson[nodePos])) nodePos++;
                    if (nodesJson[nodePos] != '{') { nodePos++; continue; }

                    int nodeObjEnd = FindMatchingBrace(nodesJson, nodePos);
                    if (nodeObjEnd == -1) break;
                    string nodeObjJson = nodesJson.Substring(nodePos, nodeObjEnd - nodePos);

                    var nodeDef = new BTNodeDef
                    {
                        Id = nodeId,
                        Type = ExtractString(nodeObjJson, "Type"),
                        Action = ExtractString(nodeObjJson, "Action"),
                        Condition = ExtractString(nodeObjJson, "Condition"),
                        Operator = ExtractString(nodeObjJson, "Operator"),
                        Value = ExtractFloat(nodeObjJson, "Value"),
                        Param = ExtractFloat(nodeObjJson, "Param"),
                        Children = ParseStringArray(nodeObjJson, "Children")?.ToArray(),
                        AbilityId = ExtractString(nodeObjJson, "AbilityId")
                    };
                    bt.Nodes[nodeId] = nodeDef;
                    nodePos = nodeObjEnd + 1;
                }
                else
                {
                    nodePos++;
                }
            }

            return bt;
        }

        public static GameConfig GetDefaultConfig()
        {
            var gameConfig = new GameConfig();

            gameConfig.Player = new PlayerConfig
            {
                Name = "Player",
                Type = "Tower",
                AttackRange = 3f,
                AttackInterval = 1f,
                AttackDamage = 10f,
                MaxHealth = 200f,
                CurrentLevel = 1,
                UpgradeThreshold = 100f,
                StartingSkills = new List<string> { "Cross Slash", "Mega Explosion", "Sniper Shot" }
            };

            var defaultMonster = new MonsterConfig
            {
                Name = "Normal Slime",
                Type = "Normal",
                Health = 20f,
                MaxHealth = 20f,
                Damage = 5f,
                MoveSpeed = 1f,
                AttackRange = 1f,
                AttackInterval = 1.5f,
                GoldReward = 10,
                Skills = new List<string> { "Normal Attack" }
            };
            gameConfig.MonsterTypes.Add(defaultMonster);

            var defaultLevel = new LevelConfig
            {
                LevelNumber = 1,
                WaveCount = 3,
                Waves = new List<WaveConfig>()
            };
            for (int i = 1; i <= 3; i++)
            {
                defaultLevel.Waves.Add(new WaveConfig { WaveNumber = i, MonsterType = "Normal", EnemyCount = 5 });
            }
            gameConfig.Levels.Add(defaultLevel);
            gameConfig.CurrentLevel = defaultLevel;

            return gameConfig;
        }

        private static GameConfig ParseGameConfig(string jsonContent)
        {
            var gameConfig = new GameConfig();

            int playerStart = jsonContent.IndexOf("\"Player\"");
            if (playerStart != -1)
            {
                int playerStartBrace = jsonContent.IndexOf("{", playerStart);
                if (playerStartBrace != -1)
                {
                    int playerEndBrace = FindMatchingBrace(jsonContent, playerStartBrace);
                    if (playerEndBrace != -1)
                    {
                        string playerJson = jsonContent.Substring(playerStartBrace, playerEndBrace - playerStartBrace + 1);
                        gameConfig.Player = ParsePlayerConfig(playerJson);
                    }
                }
            }

            int monstersStart = jsonContent.IndexOf("\"MonsterTypes\"");
            if (monstersStart != -1)
            {
                int monstersStartBracket = jsonContent.IndexOf("[", monstersStart);
                if (monstersStartBracket != -1)
                {
                    int monstersEndBracket = FindMatchingBrace(jsonContent, monstersStartBracket);
                    if (monstersEndBracket != -1)
                    {
                        string monstersJson = jsonContent.Substring(monstersStartBracket, monstersEndBracket - monstersStartBracket + 1);
                        gameConfig.MonsterTypes = ParseMonsterTypes(monstersJson);
                    }
                }
            }

            int levelsStart = jsonContent.IndexOf("\"Levels\"");
            if (levelsStart != -1)
            {
                int levelsStartBracket = jsonContent.IndexOf("[", levelsStart);
                if (levelsStartBracket != -1)
                {
                    int levelsEndBracket = FindMatchingBrace(jsonContent, levelsStartBracket);
                    if (levelsEndBracket != -1)
                    {
                        string levelsJson = jsonContent.Substring(levelsStartBracket, levelsEndBracket - levelsStartBracket + 1);
                        gameConfig.Levels = ParseLevels(levelsJson);
                    }
                }
            }

            int skillsStart = jsonContent.IndexOf("\"Skills\"");
            if (skillsStart != -1)
            {
                int playerEnd = -1;
                int playerStartIdx = jsonContent.IndexOf("\"Player\"");
                if (playerStartIdx != -1)
                {
                    int pBrace = jsonContent.IndexOf("{", playerStartIdx);
                    if (pBrace != -1)
                        playerEnd = FindMatchingBrace(jsonContent, pBrace);
                }
                int skillsSearchStart = (playerEnd > skillsStart) ? playerEnd : skillsStart;
                int skillsStartBracket = jsonContent.IndexOf("[", skillsSearchStart);
                if (skillsStartBracket != -1)
                {
                    int skillsEndBracket = FindMatchingBrace(jsonContent, skillsStartBracket);
                    if (skillsEndBracket != -1)
                    {
                        int diff = skillsEndBracket - skillsStartBracket;
                        string skillsJson = jsonContent.Substring(skillsStartBracket, diff);
                        gameConfig.Skills = ParseSkillConfigs(skillsJson);
                    }
                }
            }

            // Parse "Towers" array (game_config.json uses "Towers" key, not "TowerTypes")
            int towersStart = jsonContent.IndexOf("\"Towers\"");
            if (towersStart != -1)
            {
                int towersSearchStart = skillsStart != -1 && skillsStart < towersStart ? skillsStart : towersStart;
                int towersStartBracket = jsonContent.IndexOf("[", towersStart);
                if (towersStartBracket != -1)
                {
                    int towersEndBracket = FindMatchingBrace(jsonContent, towersStartBracket);
                    if (towersEndBracket != -1)
                    {
                        string towersJson = jsonContent.Substring(towersStartBracket, towersEndBracket - towersStartBracket);
                        gameConfig.TowerTypes = ParseTowerConfigs(towersJson);
                    }
                }
            }

            if (gameConfig.Levels.Count > 0)
            {
                gameConfig.CurrentLevel = gameConfig.Levels[0];
            }

            // Parse Combo config from JSON (fills GameConfig.Combo)
            ParseComboConfig(gameConfig, jsonContent);

            // Parse TowerOvercharge / PositionalDamage sections (previously present in
            // game_config.json but silently ignored — TowerOvercharge ran on coded defaults,
            // PositionalDamage had no model at all)
            ParseTowerOverchargeConfig(gameConfig, jsonContent);
            ParsePositionalDamageConfig(gameConfig, jsonContent);

            return gameConfig;
        }

        private static void ParseComboConfig(GameConfig gameConfig, string jsonContent)
        {
            int comboStart = jsonContent.IndexOf("\"Combo\"");
            if (comboStart == -1) return;

            int braceStart = jsonContent.IndexOf('{', comboStart);
            if (braceStart == -1) return;
            int braceEnd = FindMatchingBrace(jsonContent, braceStart);
            if (braceEnd == -1) return;

            string comboJson = jsonContent.Substring(braceStart + 1, braceEnd - braceStart - 1);

            gameConfig.Combo = new ComboConfig
            {
                ComboWindowSeconds = ExtractFloat(comboJson, "comboWindowSeconds"),
                ComboDamageBonusPerKill = ExtractFloat(comboJson, "comboDamageBonusPerKill"),
                ComboGoldBonusPerKill = ExtractFloat(comboJson, "comboGoldBonusPerKill"),
                ComboMaxMultiplier = ExtractFloat(comboJson, "comboMaxMultiplier"),
                TriggerThreshold = ExtractInt(comboJson, "triggerThreshold", 10)
            };
        }

        /// <summary>
        /// 解析 game_config.json 的 "TowerOvercharge" 段。此前该段存在于 JSON 但无解析代码，
        /// TowerOverchargeSystem 一直跑 TowerOverchargeConfig 代码默认值（两处值恰好一致）。
        /// public static：供 loader 单测注入 JSON 片段驱动（HeroSkillsConfigLoader.Parse 先例）。
        /// </summary>
        public static void ParseTowerOverchargeConfig(GameConfig gameConfig, string jsonContent)
        {
            int sectionStart = jsonContent.IndexOf("\"TowerOvercharge\"");
            if (sectionStart == -1) return;

            int braceStart = jsonContent.IndexOf('{', sectionStart);
            if (braceStart == -1) return;
            int braceEnd = FindMatchingBrace(jsonContent, braceStart);
            if (braceEnd == -1) return;

            string sectionJson = jsonContent.Substring(braceStart + 1, braceEnd - braceStart - 1);

            gameConfig.TowerOvercharge = new TowerOverchargeConfig
            {
                DamageMultiplier = ExtractFloat(sectionJson, "DamageMultiplier"),
                AttackSpeedMultiplier = ExtractFloat(sectionJson, "AttackSpeedMultiplier"),
                RangeMultiplier = ExtractFloat(sectionJson, "RangeMultiplier"),
                Duration = ExtractFloat(sectionJson, "Duration"),
                Cooldown = ExtractFloat(sectionJson, "Cooldown"),
                ManaCost = ExtractFloat(sectionJson, "ManaCost"),
                MinManaRequired = ExtractFloat(sectionJson, "MinManaRequired")
            };
        }

        /// <summary>
        /// 解析 game_config.json 的 "PositionalDamage" 段（全局朝向伤害层）。
        /// 此前该段无模型无解析。Enabled 键缺失时保持默认 false —— 零行为变化。
        /// public static：供 loader 单测注入 JSON 片段驱动。
        /// </summary>
        public static void ParsePositionalDamageConfig(GameConfig gameConfig, string jsonContent)
        {
            int sectionStart = jsonContent.IndexOf("\"PositionalDamage\"");
            if (sectionStart == -1) return;

            int braceStart = jsonContent.IndexOf('{', sectionStart);
            if (braceStart == -1) return;
            int braceEnd = FindMatchingBrace(jsonContent, braceStart);
            if (braceEnd == -1) return;

            string sectionJson = jsonContent.Substring(braceStart + 1, braceEnd - braceStart - 1);

            gameConfig.PositionalDamage = new PositionalDamageConfig
            {
                Enabled = ExtractBool(sectionJson, "Enabled"),
                BackstabAngleDegrees = ExtractFloat(sectionJson, "BackstabAngleDegrees"),
                FlankAngleDegrees = ExtractFloat(sectionJson, "FlankAngleDegrees"),
                BackstabDamageMultiplier = ExtractFloat(sectionJson, "BackstabDamageMultiplier"),
                FlankDamageMultiplier = ExtractFloat(sectionJson, "FlankDamageMultiplier")
            };
        }

        private static List<TowerConfig> ParseTowerConfigs(string jsonArray)
        {
            var towers = new List<TowerConfig>();
            int pos = 0;
            while (pos < jsonArray.Length)
            {
                while (pos < jsonArray.Length && (char.IsWhiteSpace(jsonArray[pos]) || jsonArray[pos] == ',')) pos++;
                if (pos >= jsonArray.Length) break;
                if (jsonArray[pos] == '{')
                {
                    int objEnd = FindMatchingBrace(jsonArray, pos);
                    if (objEnd == -1) break;
                    string towerJson = jsonArray.Substring(pos, objEnd - pos);
                    towers.Add(ParseTowerConfig(towerJson));
                    pos = objEnd + 1;
                }
                else { pos++; }
            }
            return towers;
        }

        private static TowerConfig ParseTowerConfig(string json)
        {
            var tower = new TowerConfig();
            tower.Name = ExtractString(json, "Name");
            tower.Type = ParseTowerType(ExtractString(json, "Type"));
            tower.Damage = ExtractFloat(json, "Damage");
            tower.Range = ExtractInt(json, "Range");
            tower.AttackSpeed = ExtractFloat(json, "AttackSpeed");
            tower.Cost = ExtractFloat(json, "Cost");
            tower.UpgradeCost = ExtractFloat(json, "UpgradeCost");
            tower.StunChance = ExtractFloat(json, "StunChance");
            tower.SlowAmount = ExtractFloat(json, "SlowAmount");
            tower.SlowDuration = ExtractFloat(json, "SlowDuration");
            tower.TargetingMode = (TowerTargetingMode)ExtractInt(json, "TargetingMode");
            tower.SpecialAbility = ParseTowerSpecialAbility(json);
            tower.ActiveSkillId = ExtractInt(json, "ActiveSkillId", -1);
            tower.ActiveCooldown = ExtractFloat(json, "ActiveCooldown");
            tower.ProjectileHoming = ExtractBool(json, "ProjectileHoming");
            // Round 114 — Predictive Aim / Lead Targeting
            // Parse the optional LeadAimFactor (default 0f = no lead, straight aim).
            // Only meaningful for ProjectileSystem-fired projectiles (fragments, homing
            // chains, etc.). Instant-hit attacks ignore it. Capped at 2.0 in the
            // SetTowerLeadAimFactor accessor.
            tower.LeadAimFactor = ExtractFloat(json, "LeadAimFactor");
            tower.TurnRate = ExtractFloat(json, "TurnRate");
            tower.DamageType = (DamageType)ExtractInt(json, "DamageType");
            tower.InterceptRate = ExtractFloat(json, "InterceptRate");
            tower.Bounces = ExtractInt(json, "Bounces");
            tower.BounceRange = ExtractFloat(json, "BounceRange");
            tower.BounceDamageFalloff = ExtractFloat(json, "BounceDamageFalloff");
            tower.PierceCount = ExtractInt(json, "PierceCount");
            tower.PierceDmgFalloff = ExtractFloat(json, "PierceDmgFalloff");
            tower.Demolish = ParseTowerDemolishConfig(json);
            tower.FalloffType = ExtractInt(json, "FalloffType");
            tower.FalloffStartRatio = ExtractFloat(json, "FalloffStartRatio");
            tower.FalloffMinRatio = ExtractFloat(json, "FalloffMinRatio");
            tower.RampUpRate = ExtractFloat(json, "RampUpRate");
            tower.RampUpMax = ExtractFloat(json, "RampUpMax");
            tower.RampUpResetOnSwitch = ExtractBool(json, "RampUpResetOnSwitch");
            tower.DamageConversionRatio = ExtractFloat(json, "DamageConversionRatio");
            tower.ConvertedDamageType = (DamageType)ExtractInt(json, "ConvertedDamageType");
            // Overkill / excess damage config (defaults preserve backward compat)
            tower.OverkillType = ExtractInt(json, "OverkillType");
            tower.OverkillRatio = ExtractFloat(json, "OverkillRatio");
            tower.OverkillRadius = ExtractFloat(json, "OverkillRadius");
            // Round 184 Direction 7 — Volley / Multi-Pellet Tower (scatter/shotgun mechanics).
            //   All 3 default to inert single-shot values (1 / 1.0 / 0.0), so legacy towers are
            //   unaffected. Designers opt in by setting ProjectileCount > 1.
            //   Note: ExtractInt/ExtractFloat return 0 when the JSON key is missing, which would
            //   override the C# defaults (= 1 / = 1f) for legacy towers. Explicit fallback here
            //   means a future guard relax (e.g. `if (projCount >= 1)`) cannot silently break
            //   every pre-184 tower. Negative values are also clamped to safe minimums.
            int projCount = ExtractInt(json, "ProjectileCount");
            tower.ProjectileCount = projCount > 0 ? projCount : 1;
            float pelletMult = ExtractFloat(json, "PelletDamageMult");
            tower.PelletDamageMult = pelletMult > 0f ? pelletMult : 1f;
            tower.PelletConeRadius = ExtractFloat(json, "PelletConeRadius");
            // Round 186 Direction 2 — Sapper-vulnerable HP pool. Default 0 = indestructible
            // legacy path; designers opt in by setting MaxHp > 0. The store-level
            // initialization in PlaceTower sets TowerCurrentHp = TowerMaxHp so a freshly
            // placed tower always starts at full HP. Clamped to [0, 10000] so a typo
            // (e.g. 99999999) doesn't balloon memory or trivialize the Sapper threat.
            float maxHp = ExtractFloat(json, "MaxHp");
            tower.MaxHp = maxHp < 0f ? 0f : (maxHp > 10000f ? 10000f : maxHp);
            return tower;
        }

        private static TowerDemolishConfig ParseTowerDemolishConfig(string json)
        {
            string key = "\"Demolish\"";
            int idx = json.IndexOf(key);
            if (idx < 0) return null;

            int braceStart = json.IndexOf('{', idx);
            if (braceStart < 0) return null;
            int braceEnd = FindMatchingBrace(json, braceStart);
            if (braceEnd < 0) return null;

            string subJson = json.Substring(braceStart, braceEnd - braceStart + 1);
            var cfg = new TowerDemolishConfig();
            cfg.DemolishRadius = ExtractFloat(subJson, "DemolishRadius");
            cfg.DemolishDamage = ExtractFloat(subJson, "DemolishDamage");
            cfg.DemolishEffectType = ExtractInt(subJson, "DemolishEffectType");
            cfg.DemolishDotDamagePerTick = ExtractFloat(subJson, "DemolishDotDamagePerTick");
            cfg.DemolishDotDuration = ExtractFloat(subJson, "DemolishDotDuration");
            cfg.DemolishDotInterval = ExtractFloat(subJson, "DemolishDotInterval");
            cfg.DemolishStunDuration = ExtractInt(subJson, "DemolishStunDuration");
            return cfg;
        }

        private static TowerSpecialAbility ParseTowerSpecialAbility(string json)
        {
            string key = "\"SpecialAbility\"";
            int idx = json.IndexOf(key);
            if (idx < 0) return null;

            int braceStart = json.IndexOf('{', idx);
            if (braceStart < 0) return null;
            int braceEnd = FindMatchingBrace(json, braceStart);
            if (braceEnd < 0) return null;

            string subJson = json.Substring(braceStart, braceEnd - braceStart + 1);
            var ability = new TowerSpecialAbility();
            ability.AbilityType = ExtractString(subJson, "AbilityType");
            ability.Cooldown = ExtractFloat(subJson, "Cooldown");
            ability.AreaShape = ExtractString(subJson, "AreaShape");
            ability.Radius = ExtractInt(subJson, "Radius");
            ability.DamageMultiplier = ExtractFloat(subJson, "DamageMultiplier");
            ability.Duration = ExtractFloat(subJson, "Duration");
            ability.DotDamagePerTick = ExtractFloat(subJson, "DotDamagePerTick");
            ability.DotTickInterval = ExtractFloat(subJson, "DotTickInterval");
            ability.StunDuration = ExtractInt(subJson, "StunDuration");
            ability.SlowFactor = ExtractFloat(subJson, "SlowFactor");
            ability.SlowDuration = ExtractInt(subJson, "SlowDuration");
            return ability;
        }

        private static PlayerConfig ParsePlayerConfig(string json)
        {
            var player = new PlayerConfig();

            player.Name = ExtractString(json, "Name");
            player.Type = ExtractString(json, "Type");
            player.AttackRange = ExtractFloat(json, "AttackRange");
            player.AttackInterval = ExtractFloat(json, "AttackInterval");
            player.AttackDamage = ExtractFloat(json, "AttackDamage");
            player.CurrentLevel = ExtractInt(json, "CurrentLevel");
            player.UpgradeThreshold = ExtractFloat(json, "UpgradeThreshold");
            player.MaxHealth = ExtractFloat(json, "MaxHealth");
            player.StartingLives = ExtractInt(json, "StartingLives");
            player.StartingSkills = ParseStringArray(json, "StartingSkills");

            return player;
        }

        private static List<MonsterConfig> ParseMonsterTypes(string jsonArray)
        {
            var monsters = new List<MonsterConfig>();

            int pos = 0;
            while (pos < jsonArray.Length)
            {
                while (pos < jsonArray.Length && (char.IsWhiteSpace(jsonArray[pos]) || jsonArray[pos] == ',')) pos++;

                if (pos >= jsonArray.Length) break;

                if (jsonArray[pos] == '{')
                {
                    int objEnd = FindMatchingBrace(jsonArray, pos);
                    if (objEnd == -1) break;

                    string monsterJson = jsonArray.Substring(pos, objEnd - pos);
                    monsters.Add(ParseMonsterConfig(monsterJson));
                    pos = objEnd + 1;
                }
                else
                {
                    pos++;
                }
            }

            return monsters;
        }

        private static MonsterConfig ParseMonsterConfig(string json)
        {
            var monster = new MonsterConfig();

            monster.Name = ExtractString(json, "Name");
            monster.Type = ExtractString(json, "Type");
            monster.Health = ExtractFloat(json, "Health");
            monster.MaxHealth = monster.Health;
            monster.Damage = ExtractFloat(json, "Damage");
            monster.MoveSpeed = ExtractFloat(json, "MoveSpeed");
            monster.AttackRange = ExtractFloat(json, "AttackRange");
            monster.AttackInterval = ExtractFloat(json, "AttackInterval");
            monster.GoldReward = ExtractInt(json, "GoldReward");
            monster.Skills = ParseStringArray(json, "Skills");
            monster.Armor = ExtractFloat(json, "Armor");
            monster.MagicResist = ExtractFloat(json, "MagicResist");
            // Damage-type immunities: "Physical", "Magic", "Fire", "Ice", "Lightning".
            // Empty/null list = no immunities. Used by ComponentStore.SetDamageImmunityMask.
            var immunities = ParseStringArray(json, "DamageImmunities");
            if (immunities != null) monster.DamageImmunities = immunities;
            // Pierce resistance: 0-1 fraction ignored, 1.0 = full immunity.
            // PierceImmune: binary flag, true = piercing projectiles deal 0 damage.
            // Both are wired to EnemyPierceResist / EnemyIsPierceImmune via SetPierceResist.
            monster.PierceResist = ExtractFloat(json, "PierceResist");
            monster.PierceImmune = ExtractBool(json, "PierceImmune");
            // Crit resistance: 0-1, suppresses a fraction of incoming crit chance.
            // Used by Boss/Elite monsters to dampen crit-sniper tower builds. Default 0.
            monster.CritResist = ExtractFloat(json, "CritResist");
            // Deflect chance: 0-1, probability of deflecting an incoming projectile.
            // Used by Boss/Elite monsters to add visual punch and force reliable follow-up towers. Default 0.
            monster.DeflectChance = ExtractFloat(json, "DeflectChance");
            // FactionId: 0 = no faction (immune to infighting), >0 = "挤死小怪" mechanic.
            // Used by swarm archetypes to make them damage each other in close proximity.
            // Default 0; opt-in via monster JSON's "FactionId" field.
            monster.FactionId = ExtractInt(json, "FactionId");
            // Round 134 Direction 3 — Boss HP natural regen. Default 0 keeps every existing
            // monster config at zero overhead. Opt-in via monster JSON's "HealthRegenPerSec".
            // PhaseRegenMult is a per-phase multiplier on HealthRegenPerSec (indexed by phase
            // 0..BOSS_PHASE_MAX-1). Falls back to 1.0× when the array is empty or the phase
            // index is out of range.
            monster.HealthRegenPerSec = ExtractFloat(json, "HealthRegenPerSec");
            monster.PhaseRegenMult = ParseFloatArray(json, "PhaseRegenMult");
            // Round 179 Direction 3 — Bounty enemy marker. Default false = inert; when
            // IsBounty=true, WaveSpawningSystem calls SetEnemyBounty() to wire the multiplier
            // into the ComponentStore. BountyGoldMult clamped at the store level to [1.0, 20.0].
            monster.IsBounty = ExtractBool(json, "IsBounty");
            monster.BountyGoldMult = ExtractFloat(json, "BountyGoldMult");
            // Round 181 Direction 9 — Phaser enemy marker. Default false = inert; when
            // IsPhaser=true, WaveSpawningSystem calls SetEnemyPhaser() to wire the cycle
            // interval + phase duration into the ComponentStore. Both clamped at the store
            // level to [0.1, 30.0] and [0.1, 10.0] respectively.
            monster.IsPhaser = ExtractBool(json, "IsPhaser");
            monster.PhaserInterval = ExtractFloat(json, "PhaserInterval");
            monster.PhaserPhaseDuration = ExtractFloat(json, "PhaserPhaseDuration");
            // Round 182 Direction 6 — Blinker enemy marker. Default false = inert; when
            // IsBlinker=true, WaveSpawningSystem calls SetEnemyBlinker() to wire the
            // interval + distance into the ComponentStore. Both clamped at the store
            // level to [0.5, 30.0] and [0.5, 5.0] respectively (min interval prevents
            // runaway 60Hz spam, max distance prevents skipping tower layers).
            monster.IsBlinker = ExtractBool(json, "IsBlinker");
            monster.BlinkInterval = ExtractFloat(json, "BlinkInterval");
            monster.BlinkDistance = ExtractFloat(json, "BlinkDistance");
            // Round 186 Direction 2 — Sapper enemy marker. Default false = inert; when
            // IsSapper=true, WaveSpawningSystem calls SetEnemySapper() to wire damage
            // / interval / slow / range into the ComponentStore. All 5 fields are clamped
            // at the store level to safe ranges (damage [0.1, 1000.0], interval
            // [0.25, 30.0], slow per stack [0, 0.5], max stacks [0, 10], range [0.5, 20.0]).
            monster.IsSapper = ExtractBool(json, "IsSapper");
            monster.SapperDamage = ExtractFloat(json, "SapperDamage");
            monster.SapperAttackInterval = ExtractFloat(json, "SapperAttackInterval");
            monster.SapperAtkSpdSlowPerStack = ExtractFloat(json, "SapperAtkSpdSlowPerStack");
            monster.SapperMaxSlowStacks = ExtractInt(json, "SapperMaxSlowStacks");
            monster.SapperRange = ExtractFloat(json, "SapperRange");

            return monster;
        }

        private static List<LevelConfig> ParseLevels(string jsonArray)
        {
            var levels = new List<LevelConfig>();

            int pos = 0;
            while (pos < jsonArray.Length)
            {
                while (pos < jsonArray.Length && (char.IsWhiteSpace(jsonArray[pos]) || jsonArray[pos] == ',')) pos++;

                if (pos >= jsonArray.Length) break;

                if (jsonArray[pos] == '{')
                {
                    int objEnd = FindMatchingBrace(jsonArray, pos);
                    if (objEnd == -1) break;

                    string levelJson = jsonArray.Substring(pos, objEnd - pos);
                    levels.Add(ParseLevelConfig(levelJson));
                    pos = objEnd + 1;
                }
                else
                {
                    pos++;
                }
            }

            return levels;
        }

        private static LevelConfig ParseLevelConfig(string json)
        {
            var level = new LevelConfig();

            level.LevelNumber = ExtractInt(json, "LevelNumber");
            level.WaveCount = ExtractInt(json, "WaveCount");
            level.Waves = ParseWaveArray(json, "Waves");
            // Round 95 Direction 5: destructible placements on this level (crates/oil barrels).
            // Empty list = no destructibles spawn, zero hot-path overhead.
            level.Destructibles = ParseDestructiblePlacements(json, "Destructibles");
            // Round 110 Direction 10: objective type + time limit (already existed for
            // Timed/Escort/Survival/Endless). DoomClock reuses the same fields but
            // the actual countdown is driven by the DoomClock-specific fields below.
            level.ObjectiveType = ExtractInt(json, "ObjectiveType");
            level.ObjectiveTimeLimit = ExtractFloat(json, "ObjectiveTimeLimit", level.ObjectiveTimeLimit);
            level.SurvivalWaveCount = (int)ExtractFloat(json, "SurvivalWaveCount", level.SurvivalWaveCount);
            // Round 110 Direction 10 — DoomClock objective tunables. All default
            // sensibly when absent (level isn't a DoomClock level). The system
            // helper short-circuits to zero overhead when ObjectiveType != DoomClock.
            level.DoomClockDuration = ExtractFloat(json, "DoomClockDuration", level.DoomClockDuration);
            level.DoomClockWaveScore = (int)ExtractFloat(json, "DoomClockWaveScore", level.DoomClockWaveScore);
            level.DoomClockTimeBonusPerSec = (int)ExtractFloat(json, "DoomClockTimeBonusPerSec", level.DoomClockTimeBonusPerSec);
            level.DoomClockHealthBonusPerPercent = (int)ExtractFloat(json, "DoomClockHealthBonusPerPercent", level.DoomClockHealthBonusPerPercent);
            level.DoomClockWaveScaling = ExtractFloat(json, "DoomClockWaveScaling", level.DoomClockWaveScaling);
            level.DoomClockInitialWaves = ParseDoomClockInitialWaves(json, "DoomClockInitialWaves");
 // Round201 Direction7 — side quest bonus objectives. Empty list = no side
 // quests; ObjectiveSystem fast-paths zero-overhead when list is empty.
 level.SideQuests = ParseSideQuests(json, "SideQuests");

            return level;
        }

        /// <summary>
        /// Parse the DoomClock wave template pool — Round 110 Direction 10.
        /// Empty list (key absent or array empty) means the level will fall
        /// back to the regular level.Waves pool with cycling. Each entry is
        /// expected to have {MonsterType, EnemyCount}. Malformed entries are
        /// silently skipped (defensive against hand-edited JSON).
        /// </summary>
        private static List<DoomClockWaveTemplate> ParseDoomClockInitialWaves(string json, string key)
        {
            var list = new List<DoomClockWaveTemplate>();
            string keyPattern = "\"" + key + "\":";
            int keyIndex = json.IndexOf(keyPattern);
            if (keyIndex == -1) return list;
            int arrayStart = json.IndexOf("[", keyIndex);
            if (arrayStart == -1) return list;
            int arrayEnd = FindMatchingBrace(json, arrayStart);
            if (arrayEnd == -1) return list;
            string arrayContent = json.Substring(arrayStart + 1, arrayEnd - arrayStart - 1);
            int pos = 0;
            while (pos < arrayContent.Length)
            {
                while (pos < arrayContent.Length && (char.IsWhiteSpace(arrayContent[pos]) || arrayContent[pos] == ',')) pos++;
                if (pos >= arrayContent.Length) break;
                if (arrayContent[pos] == '{')
                {
                    int objEnd = FindMatchingBrace(arrayContent, pos);
                    if (objEnd == -1) break;
                    string objJson = arrayContent.Substring(pos, objEnd - pos + 1);
                    var entry = new DoomClockWaveTemplate
                    {
                        MonsterType = ExtractString(objJson, "MonsterType"),
                        EnemyCount = (int)ExtractFloat(objJson, "EnemyCount", 10f)
                    };
                    if (!string.IsNullOrEmpty(entry.MonsterType))
                        list.Add(entry);
                    pos = objEnd + 1;
                }
                else
                {
                    pos++;
                }
            }
            return list;
 }

 /// <summary>
 /// Parse a flat array of side quest definitions from a JSON object string.
 /// Round201 Direction7. Each entry is expected to have {Id, Type, Threshold,
 /// TimeLimit?, GoldReward?, SoulReward?}. Missing or malformed entries are
 /// silently skipped (side quests are opt-in, no required fields). When the
 /// "SideQuests" key is absent or empty, returns an empty list.
 /// </summary>
 private static List<SideQuestDef> ParseSideQuests(string json, string key)
 {
 var list = new List<SideQuestDef>();
 string keyPattern = "\"" + key + "\":";
 int keyIndex = json.IndexOf(keyPattern);
 if (keyIndex == -1) return list;
 int arrayStart = json.IndexOf("[", keyIndex);
 if (arrayStart == -1) return list;
 int arrayEnd = FindMatchingBrace(json, arrayStart);
 if (arrayEnd == -1) return list;
 string arrayContent = json.Substring(arrayStart +1, arrayEnd - arrayStart -1);
 int pos =0;
 while (pos < arrayContent.Length)
 {
 while (pos < arrayContent.Length && (char.IsWhiteSpace(arrayContent[pos]) || arrayContent[pos] == ',')) pos++;
 if (pos >= arrayContent.Length) break;
 if (arrayContent[pos] == '{')
 {
 int objEnd = FindMatchingBrace(arrayContent, pos);
 if (objEnd == -1) break;
 string objJson = arrayContent.Substring(pos, objEnd - pos +1);
 var entry = new SideQuestDef
 {
 Id = ExtractString(objJson, "Id"),
 Type = ExtractInt(objJson, "Type"),
 Threshold = ExtractInt(objJson, "Threshold"),
 TimeLimit = ExtractFloat(objJson, "TimeLimit",0f),
 GoldReward = ExtractInt(objJson, "GoldReward"),
 SoulReward = ExtractInt(objJson, "SoulReward"),
 };
 if (!string.IsNullOrEmpty(entry.Id))
 list.Add(entry);
 pos = objEnd +1;
 }
 else { pos++; }
 }
 return list;
 }

 /// <summary>
 /// Parse a flat array of destructible placement entries from a JSON object string.
        /// Round 95 Direction 5. Each entry is expected to have {DefId, X, Y}. DefId is a
        /// string referencing a DestructibleDef.Id; X and Y are floats for grid coordinates.
        /// Missing or malformed entries are silently skipped (an opt-in feature with no
        /// required fields — a level without this array simply has no destructibles).
        /// </summary>
        private static List<DestructiblePlacement> ParseDestructiblePlacements(string json, string key)
        {
            var placements = new List<DestructiblePlacement>();
            string keyPattern = "\"" + key + "\":";
            int keyIndex = json.IndexOf(keyPattern);
            if (keyIndex == -1) return placements;
            int arrayStart = json.IndexOf("[", keyIndex);
            if (arrayStart == -1) return placements;
            int arrayEnd = FindMatchingBrace(json, arrayStart);
            if (arrayEnd == -1) return placements;
            string arrayContent = json.Substring(arrayStart + 1, arrayEnd - arrayStart - 1);
            int pos = 0;
            while (pos < arrayContent.Length)
            {
                while (pos < arrayContent.Length && (char.IsWhiteSpace(arrayContent[pos]) || arrayContent[pos] == ',')) pos++;
                if (pos >= arrayContent.Length) break;
                if (arrayContent[pos] == '{')
                {
                    int objEnd = FindMatchingBrace(arrayContent, pos);
                    if (objEnd == -1) break;
                    string objJson = arrayContent.Substring(pos, objEnd - pos + 1);
                    var placement = new DestructiblePlacement
                    {
                        DefId = ExtractString(objJson, "DefId"),
                        X = ExtractFloat(objJson, "X"),
                        Y = ExtractFloat(objJson, "Y")
                    };
                    if (!string.IsNullOrEmpty(placement.DefId))
                        placements.Add(placement);
                    pos = objEnd + 1;
                }
                else
                {
                    pos++;
                }
            }
            return placements;
        }

        private static List<WaveConfig> ParseWaveArray(string json, string key)
        {
            var waves = new List<WaveConfig>();

            string keyPattern = "\"" + key + "\":";
            int keyIndex = json.IndexOf(keyPattern);
            if (keyIndex == -1) return waves;

            int arrayStart = json.IndexOf("[", keyIndex);
            if (arrayStart == -1) return waves;

            int arrayEnd = FindMatchingBrace(json, arrayStart);
            if (arrayEnd == -1) return waves;

            string arrayContent = json.Substring(arrayStart + 1, arrayEnd - arrayStart - 1);

            int pos = 0;
            while (pos < arrayContent.Length)
            {
                while (pos < arrayContent.Length && (char.IsWhiteSpace(arrayContent[pos]) || arrayContent[pos] == ',')) pos++;

                if (pos >= arrayContent.Length) break;

                if (arrayContent[pos] == '{')
                {
                    int objEnd = FindMatchingBrace(arrayContent, pos);
                    if (objEnd == -1) break;

                    string waveJson = arrayContent.Substring(pos, objEnd - pos);
                    waves.Add(ParseWaveConfig(waveJson));
                    pos = objEnd + 1;
                }
                else
                {
                    pos++;
                }
            }

            return waves;
        }

        private static WaveConfig ParseWaveConfig(string json)
        {
            var wave = new WaveConfig();

            wave.WaveNumber = ExtractInt(json, "WaveNumber");
            wave.MonsterType = ExtractString(json, "MonsterType");
            wave.EnemyCount = ExtractInt(json, "EnemyCount");

            // Parse EnemyTypes[] for multi-type wave support
            wave.EnemyTypes = ParseEnemyTypeEntries(json, "EnemyTypes");

            return wave;
        }

        private static List<EnemyTypeEntry> ParseEnemyTypeEntries(string json, string key)
        {
            var entries = new List<EnemyTypeEntry>();

            string keyPattern = "\"" + key + "\":";
            int keyIndex = json.IndexOf(keyPattern);
            if (keyIndex == -1) return entries;

            int arrayStart = json.IndexOf("[", keyIndex);
            if (arrayStart == -1) return entries;

            int arrayEnd = FindMatchingBrace(json, arrayStart);
            if (arrayEnd == -1) return entries;

            string arrayContent = json.Substring(arrayStart + 1, arrayEnd - arrayStart - 1);

            int pos = 0;
            while (pos < arrayContent.Length)
            {
                while (pos < arrayContent.Length && (char.IsWhiteSpace(arrayContent[pos]) || arrayContent[pos] == ',')) pos++;
                if (pos >= arrayContent.Length) break;

                if (arrayContent[pos] == '{')
                {
                    int objEnd = FindMatchingBrace(arrayContent, pos);
                    if (objEnd == -1) break;

                    string entryJson = arrayContent.Substring(pos, objEnd - pos);
                    var entry = new EnemyTypeEntry
                    {
                        MonsterType = ExtractString(entryJson, "MonsterType"),
                        Count = ExtractInt(entryJson, "Count")
                    };
                    if (!string.IsNullOrEmpty(entry.MonsterType))
                        entries.Add(entry);
                    pos = objEnd + 1;
                }
                else
                {
                    pos++;
                }
            }

            return entries;
        }

        private static List<string> ParseStringArray(string json, string key)
        {
            var items = new List<string>();

            string keyPattern = "\"" + key + "\":";
            int keyIndex = json.IndexOf(keyPattern);
            if (keyIndex == -1) return items;

            int arrayStart = json.IndexOf("[", keyIndex);
            if (arrayStart == -1) return items;

            int arrayEnd = FindMatchingBrace(json, arrayStart);
            if (arrayEnd == -1) return items;

            string arrayContent = json.Substring(arrayStart + 1, arrayEnd - arrayStart - 1);

            int pos = 0;
            while (pos < arrayContent.Length)
            {
                while (pos < arrayContent.Length && (char.IsWhiteSpace(arrayContent[pos]) || arrayContent[pos] == ',')) pos++;

                if (pos >= arrayContent.Length) break;

                if (arrayContent[pos] == '"')
                {
                    pos++;
                    int endQuote = arrayContent.IndexOf("\"", pos);
                    if (endQuote == -1) break;

                    items.Add(arrayContent.Substring(pos, endQuote - pos));
                    pos = endQuote + 1;
                }
                else
                {
                    pos++;
                }
            }

            return items;
        }

        // Round 134 Direction 3 — parse a JSON array of floats (e.g. PhaseRegenMult).
        // Mirrors ParseStringArray but for unquoted numbers. Tolerates whitespace and
        // trailing commas; malformed entries are silently skipped (defensive against
        // hand-edited JSON). Returns an empty array when the key is absent so callers
        // can use Length==0 as the "feature disabled" sentinel.
        private static float[] ParseFloatArray(string json, string key)
        {
            var items = new List<float>();

            string keyPattern = "\"" + key + "\":";
            int keyIndex = json.IndexOf(keyPattern);
            if (keyIndex == -1) return items.ToArray();

            int arrayStart = json.IndexOf("[", keyIndex);
            if (arrayStart == -1) return items.ToArray();

            int arrayEnd = FindMatchingBrace(json, arrayStart);
            if (arrayEnd == -1) return items.ToArray();

            string arrayContent = json.Substring(arrayStart + 1, arrayEnd - arrayStart - 1);

            int pos = 0;
            while (pos < arrayContent.Length)
            {
                while (pos < arrayContent.Length && (char.IsWhiteSpace(arrayContent[pos]) || arrayContent[pos] == ',')) pos++;
                if (pos >= arrayContent.Length) break;

                // Read a number token: optional sign + digits + optional dot + digits
                int tokenStart = pos;
                if (arrayContent[pos] == '-' || arrayContent[pos] == '+') pos++;
                while (pos < arrayContent.Length &&
                       (char.IsDigit(arrayContent[pos]) || arrayContent[pos] == '.' || arrayContent[pos] == 'e' || arrayContent[pos] == 'E' || arrayContent[pos] == '-' || arrayContent[pos] == '+'))
                {
                    // Only advance on digits, '.', 'e', 'E' (sign handled separately above).
                    char c = arrayContent[pos];
                    if (c == '-' || c == '+')
                    {
                        // Sign only valid directly after 'e' or 'E'
                        if (pos > tokenStart && (arrayContent[pos - 1] != 'e' && arrayContent[pos - 1] != 'E'))
                            break;
                    }
                    pos++;
                }
                if (pos <= tokenStart) { pos++; continue; }
                string token = arrayContent.Substring(tokenStart, pos - tokenStart);
                if (float.TryParse(token, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float v))
                {
                    items.Add(v);
                }
            }

            return items.ToArray();
        }

        private static int FindMatchingBrace(string str, int startPos)
        {
            char openChar = str[startPos];
            char closeChar = openChar == '{' ? '}' : ']';

            int count = 1;
            for (int i = startPos + 1; i < str.Length; i++)
            {
                if (str[i] == openChar) count++;
                else if (str[i] == closeChar) count--;
                if (count == 0) return i + 1;
            }

            return -1;
        }

        private static List<SkillConfig> ParseSkillConfigs(string jsonArray)
        {
            var skills = new List<SkillConfig>();
            int pos = 0;
            while (pos < jsonArray.Length)
            {
                while (pos < jsonArray.Length && (char.IsWhiteSpace(jsonArray[pos]) || jsonArray[pos] == ',')) pos++;
                if (pos >= jsonArray.Length) break;
                if (jsonArray[pos] == '{')
                {
                    int objEnd = FindMatchingBrace(jsonArray, pos);
                    if (objEnd == -1) break;
                    string skillJson = jsonArray.Substring(pos, objEnd - pos);
                    skills.Add(ParseSkillConfig(skillJson));
                    pos = objEnd + 1;
                }
                else { pos++; }
            }
            return skills;
        }

        private static SkillConfig ParseSkillConfig(string json)
        {
            var skill = new SkillConfig();
            skill.Name = ExtractString(json, "Name");
            skill.Description = ExtractString(json, "Description");
            skill.DamageMultiplier = ExtractFloat(json, "DamageMultiplier");
            skill.AreaWidth = ExtractInt(json, "AreaWidth");
            skill.AreaHeight = ExtractInt(json, "AreaHeight");
            skill.AttackRange = ExtractInt(json, "AttackRange");
            skill.Cooldown = ExtractFloat(json, "Cooldown");
            string autoCastStr = ExtractString(json, "AutoCast");
            bool autoCast = false;
            if (!string.IsNullOrEmpty(autoCastStr))
                bool.TryParse(autoCastStr, out autoCast);
            skill.AutoCast = autoCast;
            skill.Hotkey = ExtractString(json, "Hotkey");
            skill.ConeAngleDegrees = ExtractFloat(json, "ConeAngleDegrees");
            skill.FreezeDuration = ExtractFloat(json, "FreezeDuration");
            skill.FreezeChance = ExtractFloat(json, "FreezeChance");
            skill.SlowAmount = ExtractFloat(json, "SlowAmount");
            skill.SlowDuration = ExtractFloat(json, "SlowDuration");
            skill.ManaCost = ExtractFloat(json, "ManaCost");
            // Round 136 Direction 2 — AOE CC group control
            skill.AoeStunDuration = ExtractFloat(json, "AoeStunDuration");
            skill.AoeRootDuration = ExtractFloat(json, "AoeRootDuration");
            skill.AoeKnockbackForce = ExtractFloat(json, "AoeKnockbackForce");
            return skill;
        }

        private static void LoadWeatherConfig(GameConfig gameConfig, IRenderer renderer, bool strict)
        {
            const string weatherFile = "Data/Configs/weather.json";
            try
            {
                if (!File.Exists(weatherFile))
                {
                    renderer.Log("[WEATHER] Weather config file not found: " + weatherFile + ", using defaults");
                    return;
                }
                string json = File.ReadAllText(weatherFile);
                if (string.IsNullOrWhiteSpace(json))
                {
                    RequireStrictInput(strict, weatherFile, "weather configuration is empty");
                    renderer.Log("[WEATHER] Weather config file is empty: " + weatherFile);
                    return;
                }
                using (var document = System.Text.Json.JsonDocument.Parse(json))
                    RequireJsonKind(strict, weatherFile, document.RootElement,
                        System.Text.Json.JsonValueKind.Object, "an object");
                ParseWeatherConfig(gameConfig, json);
                renderer.Log("[WEATHER] Loaded weather config from " + weatherFile);
            }
            catch (Exception ex)
            {
                ThrowIfStrict(strict, weatherFile, ex);
                renderer.Log("[WEATHER] Failed to load weather config: " + ex.Message);
            }
        }

        private static void ParseWeatherConfig(GameConfig gameConfig, string json)
        {
            var config = new WeatherConfig();

            // Global multipliers
            config.GlobalEnemySpeedMult = ExtractFloat(json, "globalEnemySpeedMult", 1.0f);
            config.GlobalTowerRangeMult = ExtractFloat(json, "globalTowerRangeMult", 1.0f);
            config.GlobalTowerDamageMult = ExtractFloat(json, "globalTowerDamageMult", 1.0f);

            // Parse types array — each entry: { "type": "Rain", "enemySpeedMult": 0.8, ... }
            int typesStart = json.IndexOf("\"types\"");
            if (typesStart >= 0)
            {
                int arrStart = json.IndexOf('[', typesStart);
                int arrEnd = json.IndexOf(']', arrStart);
                if (arrStart >= 0 && arrEnd > arrStart)
                {
                    string arr = json.Substring(arrStart + 1, arrEnd - arrStart - 1);
                    int pos = 0;
                    while (pos < arr.Length)
                    {
                        while (pos < arr.Length && (char.IsWhiteSpace(arr[pos]) || arr[pos] == ',')) pos++;
                        if (pos >= arr.Length) break;
                        if (arr[pos] == '{')
                        {
                            int objEnd = FindMatchingBrace(arr, pos);
                            if (objEnd == -1) break;
                            string obj = arr.Substring(pos, objEnd - pos + 1);
                            var typeConfig = new WeatherTypeConfig();
                            typeConfig.Name = ExtractString(obj, "type");
                            typeConfig.EnemySpeedMult = ExtractFloat(obj, "enemySpeedMult", 1.0f);
                            typeConfig.TowerRangeMult = ExtractFloat(obj, "towerRangeMult", 1.0f);
                            typeConfig.TowerDamageMult = ExtractFloat(obj, "towerDamageMult", 1.0f);
                            typeConfig.DefaultDuration = ExtractFloat(obj, "defaultDuration", -1f);
                            typeConfig.MinIntensity = ExtractFloat(obj, "minIntensity", 0.5f);
                            typeConfig.MaxIntensity = ExtractFloat(obj, "maxIntensity", 1.0f);
                            // Round 185: parse enemyDotPct for Sandstorm-style weather (default 0 = no DoT)
                            typeConfig.EnemyDotPct = ExtractFloat(obj, "enemyDotPct", 0f);
                            if (!string.IsNullOrEmpty(typeConfig.Name))
                                config.Types[typeConfig.Name] = typeConfig;
                            pos = objEnd + 1;
                        }
                        else { pos++; }
                    }
                }
            }
            gameConfig.Weather = config;
        }

        private static float ExtractFloat(string json, string key, float defaultValue = 0f)
        {
            try
            {
                // Try quoted float
                int keyPos = json.IndexOf("\"" + key + "\"");
                if (keyPos < 0) return defaultValue;
                int colonPos = json.IndexOf(':', keyPos);
                if (colonPos < 0) return defaultValue;
                int start = colonPos + 1;
                while (start < json.Length && (char.IsWhiteSpace(json[start]) || json[start] == ',')) start++;
                if (start >= json.Length) return defaultValue;
                if (json[start] == '"')
                {
                    int end = json.IndexOf('"', start + 1);
                    if (end < 0) return defaultValue;
                    if (float.TryParse(json.Substring(start + 1, end - start - 1), out float result)) return result;
                    return defaultValue;
                }
                else
                {
                    int end = start;
                    while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '.' || json[end] == '-' || json[end] == 'e' || json[end] == 'E')) end++;
                    if (float.TryParse(json.Substring(start, end - start), out float result)) return result;
                    return defaultValue;
                }
            }
            catch { return defaultValue; }
        }

        private static void LoadWaveMutatorsConfig(GameConfig gameConfig, IRenderer renderer, bool strict)
        {
            const string mutatorFile = "Data/Configs/wave_mutators.json";
            try
            {
                if (!File.Exists(mutatorFile))
                {
                    renderer.Log("[MUTATOR] Wave mutators config file not found: " + mutatorFile + ", using defaults");
                    return;
                }
                string json = File.ReadAllText(mutatorFile);
                if (string.IsNullOrWhiteSpace(json))
                {
                    RequireStrictInput(strict, mutatorFile, "wave mutator configuration is empty");
                    renderer.Log("[MUTATOR] Wave mutators config file is empty: " + mutatorFile);
                    return;
                }

                var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("mutators", out var mutatorsArr))
                {
                    var defs = new List<WaveMutatorDef>();
                    foreach (var elem in mutatorsArr.EnumerateArray())
                    {
                        var m = new WaveMutatorDef();
                        m.Id = elem.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                        m.Name = elem.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                        m.Description = elem.TryGetProperty("description", out var descProp) ? descProp.GetString() ?? "" : "";
                        m.EffectType = elem.TryGetProperty("effectType", out var etProp) ? etProp.GetString() ?? "" : "";
                        m.SpeedMult = elem.TryGetProperty("speedMult", out var smProp) ? (float)smProp.GetDouble() : 1.0f;
                        m.RegenRate = elem.TryGetProperty("regenRate", out var rrProp) ? (float)rrProp.GetDouble() : 0f;
                        m.ExplosionDamageRatio = elem.TryGetProperty("explosionDamageRatio", out var edrProp) ? (float)edrProp.GetDouble() : 0f;
                        m.ExplosionRadius = elem.TryGetProperty("explosionRadius", out var erProp) ? (float)erProp.GetDouble() : 0f;
                        m.SpawnBatchSize = elem.TryGetProperty("spawnBatchSize", out var sbProp) ? sbProp.GetInt32() : 5;
                        m.TriggerWaveStart = elem.TryGetProperty("triggerWaveStart", out var twsProp) ? twsProp.GetInt32() : 0;
                        defs.Add(m);
                    }
                    gameConfig.WaveMutatorDefs = defs.ToArray();
                }

                renderer.Log("[MUTATOR] Loaded wave mutators config from " + mutatorFile + " (" + gameConfig.WaveMutatorDefs.Length + " mutators)");
            }
            catch (Exception ex)
            {
                ThrowIfStrict(strict, mutatorFile, ex);
                renderer.Log("[MUTATOR] Failed to load wave mutators config: " + ex.Message);
            }
        }

        private static void LoadTerrainConfig(GameConfig gameConfig, IRenderer renderer, bool strict)
        {
            const string terrainFile = "Data/Configs/terrain.json";
            try
            {
                if (!File.Exists(terrainFile))
                {
                    renderer.Log("[TERRAIN] Terrain config file not found: " + terrainFile + ", using defaults");
                    return;
                }
                string json = File.ReadAllText(terrainFile);
                if (string.IsNullOrWhiteSpace(json))
                {
                    RequireStrictInput(strict, terrainFile, "terrain configuration is empty");
                    renderer.Log("[TERRAIN] Terrain config file is empty: " + terrainFile);
                    return;
                }

                var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Parse terrainTypes
                if (root.TryGetProperty("terrainTypes", out var typesArr))
                {
                    foreach (var elem in typesArr.EnumerateArray())
                    {
                        var tc = new TerrainTypeConfig();
                        if (elem.TryGetProperty("id", out var idProp)) tc.Id = idProp.GetInt32();
                        if (elem.TryGetProperty("name", out var nameProp)) tc.Name = nameProp.GetString() ?? "";
                        if (elem.TryGetProperty("description", out var descProp)) tc.Description = descProp.GetString() ?? "";
                        if (elem.TryGetProperty("moveSpeedMult", out var msmProp)) tc.MoveSpeedMult = (float)msmProp.GetDouble();
                        if (elem.TryGetProperty("dotDamagePerTick", out var ddpProp)) tc.DotDamagePerTick = (float)ddpProp.GetDouble();
                        if (elem.TryGetProperty("dotDuration", out var ddProp)) tc.DotDuration = ddProp.GetInt32();
                        if (elem.TryGetProperty("towerRangeBonus", out var trbProp)) tc.TowerRangeBonus = (float)trbProp.GetDouble();
                        gameConfig.TerrainTypes.Add(tc);
                    }
                }

                // Parse mapTerrain grid
                if (root.TryGetProperty("mapTerrain", out var gridArr))
                {
                    var grid = new List<int[]>();
                    foreach (var row in gridArr.EnumerateArray())
                    {
                        var rowList = new List<int>();
                        foreach (var cell in row.EnumerateArray())
                            rowList.Add(cell.GetInt32());
                        grid.Add(rowList.ToArray());
                    }
                    gameConfig.MapTerrainGrid = grid.ToArray();
                }

                renderer.Log("[TERRAIN] Loaded terrain config from " + terrainFile + " (" + gameConfig.TerrainTypes.Count + " types, " + gameConfig.MapTerrainGrid.Length + " rows)");
            }
            catch (Exception ex)
            {
                ThrowIfStrict(strict, terrainFile, ex);
                renderer.Log("[TERRAIN] Failed to load terrain config: " + ex.Message);
            }
        }

        private static void LoadPickupDefs(GameConfig gameConfig, IRenderer renderer, bool strict)
        {
            const string pickupFile = "Data/Configs/pickup_defs.json";
            try
            {
                if (!File.Exists(pickupFile))
                {
                    renderer.Log("[PICKUP] Pickup defs file not found: " + pickupFile + ", using defaults");
                    return;
                }
                string json = File.ReadAllText(pickupFile);
                if (string.IsNullOrWhiteSpace(json))
                {
                    RequireStrictInput(strict, pickupFile, "pickup definition configuration is empty");
                    renderer.Log("[PICKUP] Pickup defs file is empty: " + pickupFile);
                    return;
                }

                var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                var defs = new List<PickupDef>();
                foreach (var elem in root.EnumerateArray())
                {
                    var p = new PickupDef();
                    p.Type = elem.TryGetProperty("Type", out var t) ? t.GetString() ?? "" : "";
                    p.Value = elem.TryGetProperty("Value", out var v) ? (float)v.GetDouble() : 0f;
                    p.CollectRadius = elem.TryGetProperty("CollectRadius", out var cr) ? (float)cr.GetDouble() : 1.5f;
                    p.LifetimeSeconds = elem.TryGetProperty("LifetimeSeconds", out var ls) ? (float)ls.GetDouble() : 30f;
                    p.Color = elem.TryGetProperty("Color", out var c) ? c.GetString() ?? "White" : "White";
                    p.Fx = elem.TryGetProperty("Fx", out var fx) ? fx.GetString() ?? "None" : "None";
                    defs.Add(p);
                }

                gameConfig.PickupDefs = defs.ToArray();
                renderer.Log("[PICKUP] Loaded " + defs.Count + " pickup defs from " + pickupFile);
            }
            catch (Exception ex)
            {
                ThrowIfStrict(strict, pickupFile, ex);
                renderer.Log("[PICKUP] Failed to load pickup defs: " + ex.Message);
            }
        }

        // ── Shared skill definition table (SkillDefs) ────────────────────────
        // 接线此前无任何代码加载的两处死数据：Data/Configs/skills.json（精选技能表，
        // 顶层为数组，含完整 shape/DoT/CC 字段与 Modifiers）与 Data/Skills/*.json
        // （150 个静态定义，字段为 SkillConfig 子集）。按名去重合并，精选优先。
        // 消费方：HeroSkillSystem / TowerActiveSkillSystem 的按名解析（优先 SkillDefs，
        // 回退 Skills）。玩家技能栏（Skills）仍来自 game_config.json 主文件。

        private static void LoadSkillDefs(GameConfig gameConfig, IRenderer renderer, bool strict)
        {
            const string curatedFile = "Data/Configs/skills.json";
            const string staticDir = "Data/Skills";
            try
            {
                int curatedCount = 0;
                if (File.Exists(curatedFile))
                {
                    string json = File.ReadAllText(curatedFile);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        curatedCount = ParseSkillDefsArrayJson(gameConfig, json);
                    }
                    else
                    {
                        RequireStrictInput(strict, curatedFile, "curated skill definition configuration is empty");
                    }
                }
                else
                {
                    RequireStrictInput(strict, curatedFile, "curated skill definition configuration not found");
                    renderer.Log("[SKILLDEF] Curated skill defs file not found: " + curatedFile);
                }

                int staticCount = 0, skipped = 0;
                if (Directory.Exists(staticDir))
                {
                    foreach (string path in Directory.GetFiles(staticDir, "skill_*.json"))
                    {
                        try
                        {
                            string json = File.ReadAllText(path);
                            if (string.IsNullOrWhiteSpace(json))
                            {
                                RequireStrictInput(strict, path, "static skill definition is empty");
                                continue;
                            }
                            using (var doc = System.Text.Json.JsonDocument.Parse(json))
                            {
                                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                                {
                                    RequireStrictInput(strict, path, "expected an object");
                                    continue;
                                }
                                var def = ParseSkillDefElement(doc.RootElement);
                                if (string.IsNullOrEmpty(def.Name) || NameExists(gameConfig, def.Name)) { skipped++; continue; }
                                gameConfig.SkillDefs.Add(def);
                                staticCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            ThrowIfStrict(strict, path, ex);
                            renderer.Log("[SKILLDEF] Failed to parse " + path + ": " + ex.Message);
                        }
                    }
                }
                else
                {
                    RequireStrictInput(strict, staticDir, "static skill definition directory not found");
                    renderer.Log("[SKILLDEF] Static skill defs directory not found: " + staticDir);
                }

                renderer.Log("[SKILLDEF] Loaded " + curatedCount + " curated + " + staticCount
                    + " static skill defs (" + skipped + " skipped as duplicates/empty)");
            }
            catch (Exception ex)
            {
                ThrowIfStrict(strict, curatedFile, ex);
                renderer.Log("[SKILLDEF] Failed to load skill defs: " + ex.Message);
            }
        }

        /// <summary>
        /// 解析顶层为 JSON 数组的技能定义表，按名去重追加到 SkillDefs（同名跳过）。
        /// 返回新增条数。public static：供单测注入 JSON 片段驱动（HeroSkillsConfigLoader.Parse 先例）。
        /// </summary>
        public static int ParseSkillDefsArrayJson(GameConfig gameConfig, string jsonArrayJson)
        {
            if (string.IsNullOrWhiteSpace(jsonArrayJson)) return 0;
            using (var doc = System.Text.Json.JsonDocument.Parse(jsonArrayJson))
            {
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return 0;
                int added = 0;
                foreach (var elem in doc.RootElement.EnumerateArray())
                {
                    var def = ParseSkillDefElement(elem);
                    if (string.IsNullOrEmpty(def.Name) || NameExists(gameConfig, def.Name)) continue;
                    gameConfig.SkillDefs.Add(def);
                    added++;
                }
                return added;
            }
        }

        private static bool NameExists(GameConfig gameConfig, string name)
        {
            for (int i = 0; i < gameConfig.SkillDefs.Count; i++)
            {
                if (string.Equals(gameConfig.SkillDefs[i]?.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>System.Text.Json 元素 → SkillConfig（只覆盖 JSON 中存在的键，其余保持 SkillConfig 默认值）。</summary>
        private static SkillConfig ParseSkillDefElement(System.Text.Json.JsonElement elem)
        {
            var s = new SkillConfig();
            if (elem.ValueKind != System.Text.Json.JsonValueKind.Object) return s;

            s.Name = DefGetString(elem, "Name");
            s.Description = DefGetString(elem, "Description");
            s.Hotkey = DefGetString(elem, "Hotkey");
            s.AreaShape = DefGetString(elem, "AreaShape");
            s.SummonDefId = DefGetString(elem, "SummonDefId");
            s.DamageMultiplier = DefGetFloat(elem, "DamageMultiplier");
            s.Cooldown = DefGetFloat(elem, "Cooldown");
            s.DotDuration = DefGetFloat(elem, "DotDuration");
            s.DotTickInterval = DefGetFloat(elem, "DotTickInterval");
            s.DotDamagePerTick = DefGetFloat(elem, "DotDamagePerTick");
            s.HealPercent = DefGetFloat(elem, "HealPercent");
            s.ShieldAmount = DefGetFloat(elem, "ShieldAmount");
            s.ShieldDuration = DefGetFloat(elem, "ShieldDuration");
            s.FreezeDuration = DefGetFloat(elem, "FreezeDuration");
            s.FreezeChance = DefGetFloat(elem, "FreezeChance");
            s.SlowAmount = DefGetFloat(elem, "SlowAmount");
            s.SlowDuration = DefGetFloat(elem, "SlowDuration");
            s.PolymorphDuration = DefGetFloat(elem, "PolymorphDuration");
            s.ManaCost = DefGetFloat(elem, "ManaCost");
            s.AoeStunDuration = DefGetFloat(elem, "AoeStunDuration");
            s.AoeRootDuration = DefGetFloat(elem, "AoeRootDuration");
            s.AoeKnockbackForce = DefGetFloat(elem, "AoeKnockbackForce");
            s.AreaWidth = (int)DefGetFloat(elem, "AreaWidth");
            s.AreaHeight = (int)DefGetFloat(elem, "AreaHeight");
            s.AttackRange = (int)DefGetFloat(elem, "AttackRange");
            s.AreaRadius = (int)DefGetFloat(elem, "AreaRadius");

            if (elem.TryGetProperty("AutoCast", out var ac))
            {
                if (ac.ValueKind == System.Text.Json.JsonValueKind.True) s.AutoCast = true;
                else if (ac.ValueKind == System.Text.Json.JsonValueKind.False) s.AutoCast = false;
                else if (ac.ValueKind == System.Text.Json.JsonValueKind.String && bool.TryParse(ac.GetString(), out bool parsed)) s.AutoCast = parsed;
            }
            if (elem.TryGetProperty("PolymorphDamageTakenMultiplier", out var pdm)
                && pdm.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                s.PolymorphDamageTakenMultiplier = (float)pdm.GetDouble();
            }
            // ConeAngleDegrees 默认 60：键缺失时保持默认（避免非 cone 技能把默认值清零）
            if (elem.TryGetProperty("ConeAngleDegrees", out var cad)
                && cad.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                s.ConeAngleDegrees = (float)cad.GetDouble();
            }

            if (elem.TryGetProperty("Modifiers", out var mods) && mods.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var m in mods.EnumerateArray())
                {
                    if (m.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    s.Modifiers.Add(new SkillModifierDef
                    {
                        Name = DefGetString(m, "Name"),
                        Type = DefGetString(m, "Type"),
                        Duration = DefGetFloat(m, "Duration"),
                        StackingType = DefGetString(m, "StackingType"),
                        StackLimitCount = (int)DefGetFloat(m, "StackLimitCount"),
                        Value = DefGetFloat(m, "Value"),
                        EffectTag = DefGetString(m, "EffectTag")
                    });
                }
            }
            return s;
        }

        private static string DefGetString(System.Text.Json.JsonElement e, string key)
        {
            return e.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString() ?? ""
                : "";
        }

        private static float DefGetFloat(System.Text.Json.JsonElement e, string key)
        {
            return e.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number
                ? (float)v.GetDouble()
                : 0f;
        }

        // ── Round 130 Inventory items ─────────────────────────────────────
        // Load item definitions from items.json. Each item has a Type (unique id),
        // Name (display), ItemType (semantic category), Value/BuffDuration/Radius
        // (typed meaning per ItemType), MaxStack (per-slot count cap).
        // On parse failure, ItemDefs stays empty (InventorySystem fast-paths on empty).
        private static void LoadItemDefs(GameConfig gameConfig, IRenderer renderer, bool strict)
        {
            const string itemFile = "Data/Configs/items.json";
            try
            {
                if (!File.Exists(itemFile))
                {
                    renderer.Log("[INVENTORY] Item defs file not found: " + itemFile + ", inventory disabled");
                    return;
                }
                string json = File.ReadAllText(itemFile);
                if (string.IsNullOrWhiteSpace(json))
                {
                    RequireStrictInput(strict, itemFile, "item definition configuration is empty");
                    renderer.Log("[INVENTORY] Item defs file is empty: " + itemFile);
                    return;
                }

                var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                var defs = new List<ItemDef>();
                foreach (var elem in root.EnumerateArray())
                {
                    var it = new ItemDef();
                    it.Type = elem.TryGetProperty("Type", out var t) ? t.GetString() ?? "" : "";
                    it.Name = elem.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                    string itemTypeStr = elem.TryGetProperty("ItemType", out var it0) ? it0.GetString() ?? "Unknown" : "Unknown";
                    it.ItemType = ParseItemType(itemTypeStr);
                    it.Value = elem.TryGetProperty("Value", out var v) ? (float)v.GetDouble() : 0f;
                    it.BuffDuration = elem.TryGetProperty("BuffDuration", out var bd) ? (float)bd.GetDouble() : 0f;
                    it.Radius = elem.TryGetProperty("Radius", out var rd) ? (float)rd.GetDouble() : 0f;
                    it.MaxStack = elem.TryGetProperty("MaxStack", out var ms) ? ms.GetInt32() : 1;
                    if (it.MaxStack < 1) it.MaxStack = 1;
                    defs.Add(it);
                }

                gameConfig.ItemDefs = defs.ToArray();
                renderer.Log("[INVENTORY] Loaded " + defs.Count + " item defs from " + itemFile);
            }
            catch (Exception ex)
            {
                ThrowIfStrict(strict, itemFile, ex);
                renderer.Log("[INVENTORY] Failed to load item defs: " + ex.Message);
            }
        }

        // Map JSON string to InventoryItemType enum. Unknown → Unknown (caller must skip).
        private static InventoryItemType ParseItemType(string s)
        {
            if (string.IsNullOrEmpty(s)) return InventoryItemType.Unknown;
            switch (s.Trim().ToLowerInvariant())
            {
                case "heal": return InventoryItemType.Heal;
                case "mana": return InventoryItemType.Mana;
                case "shield": return InventoryItemType.Shield;
                case "speedboost": return InventoryItemType.SpeedBoost;
                case "damageboost": return InventoryItemType.DamageBoost;
                case "aoeburst": return InventoryItemType.AoEBurst;
                case "summon": return InventoryItemType.Summon;
                case "cleanse": return InventoryItemType.Cleanse;
                default: return InventoryItemType.Unknown;
            }
        }

        // ── Round 199 Direction 6 — Crafting recipe loading ────────────────────────
        // Load crafting recipes from crafting_recipes.json. Each recipe declares its
        // input/output stacks by ItemId (index into GameConfig.ItemDefs), plus three
        // probability knobs: SuccessRate, RefundRate (failure), RareBonusRate.
        //
        // Defensive: parse failures leave CraftingRecipes empty so CraftingSystem
        // fast-paths on the empty array (no crashes, no auto-success). ItemId and Count
        // are clamped to safe ranges so a malformed config can't craft a -1 stack or
        // produce billions of items.
        private static void LoadCraftingRecipes(GameConfig gameConfig, IRenderer renderer, bool strict)
        {
            const string recipeFile = "Data/Configs/crafting_recipes.json";
            try
            {
                if (!File.Exists(recipeFile))
                {
                    renderer.Log("[CRAFTING] Recipe file not found: " + recipeFile + ", crafting disabled");
                    return;
                }
                string json = File.ReadAllText(recipeFile);
                if (string.IsNullOrWhiteSpace(json))
                {
                    RequireStrictInput(strict, recipeFile, "crafting recipe configuration is empty");
                    renderer.Log("[CRAFTING] Recipe file is empty: " + recipeFile);
                    return;
                }

                // using var so the JsonDocument's rented buffer is returned to the
                // pool deterministically; LoadItemDefs and similar methods in this
                // file follow the older "var" pattern (potential leak), but for new
                // methods we follow the dispose-correct pattern.
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                int maxItemId = gameConfig.ItemDefs != null ? gameConfig.ItemDefs.Length : 0;
                var recipes = new List<CraftingRecipeDef>();
                foreach (var elem in root.EnumerateArray())
                {
                    var rec = new CraftingRecipeDef();
                    rec.Type = elem.TryGetProperty("Type", out var t) ? t.GetString() ?? "" : "";
                    rec.Name = elem.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                    rec.Inputs = ParseCraftingStacks(elem, "Inputs", maxItemId, renderer);
                    rec.Outputs = ParseCraftingStacks(elem, "Outputs", maxItemId, renderer);
                    rec.RareBonusOutputs = ParseCraftingStacks(elem, "RareBonusOutputs", maxItemId, renderer);
                    rec.SuccessRate = Clamp01(elem.TryGetProperty("SuccessRate", out var sr) ? (float)sr.GetDouble() : 1f);
                    rec.RefundRate = Clamp01(elem.TryGetProperty("RefundRate", out var rr) ? (float)rr.GetDouble() : 0.5f);
                    rec.RareBonusRate = Clamp01(elem.TryGetProperty("RareBonusRate", out var rb) ? (float)rb.GetDouble() : 0f);
                    recipes.Add(rec);
                }

                gameConfig.CraftingRecipes = recipes.ToArray();
                renderer.Log("[CRAFTING] Loaded " + recipes.Count + " recipes from " + recipeFile);
            }
            catch (Exception ex)
            {
                ThrowIfStrict(strict, recipeFile, ex);
                renderer.Log("[CRAFTING] Failed to load recipes: " + ex.Message);
            }
        }

        // Parse a JSON array of { ItemId, Count } objects from a recipe field. ItemId
        // is clamped to [-1, maxItemId) — -1 is kept as a sentinel meaning "skip this
        // entry" so a recipe author can leave a bonus output blank and still ship the
        // file. Count is clamped to [0, 999] to prevent runaway crafting yields.
        private static CraftingItemStack[] ParseCraftingStacks(System.Text.Json.JsonElement parent, string fieldName, int maxItemId, IRenderer renderer)
        {
            if (!parent.TryGetProperty(fieldName, out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return Array.Empty<CraftingItemStack>();
            }
            var stacks = new List<CraftingItemStack>();
            foreach (var el in arr.EnumerateArray())
            {
                var s = new CraftingItemStack();
                s.ItemId = el.TryGetProperty("ItemId", out var id) ? id.GetInt32() : -1;
                s.Count = el.TryGetProperty("Count", out var c) ? c.GetInt32() : 1;
                // Clamp ItemId to valid range. -1 means "skip / unset" and is preserved.
                if (s.ItemId < -1 || s.ItemId >= maxItemId) s.ItemId = -1;
                if (s.Count < 0) s.Count = 0;
                if (s.Count > 999) s.Count = 999;
                if (s.ItemId == -1 || s.Count == 0) continue; // drop invalid/empty entries
                stacks.Add(s);
            }
            return stacks.ToArray();
        }

        // Clamp a float to the [0, 1] probability range. NaN gets mapped to 0 (failure
        // is the safe default for an indeterminate rate).
        private static float Clamp01(float v)
        {
            if (float.IsNaN(v)) return 0f;
            if (v < 0f) return 0f;
            if (v > 1f) return 1f;
            return v;
        }

        private static void LoadFissionDefs(GameConfig gameConfig, IRenderer renderer, bool strict)
        {
            const string fissionFile = "Data/Configs/enemy_fission.json";
            try
            {
                if (!File.Exists(fissionFile))
                {
                    renderer.Log("[FISSION] Enemy fission config file not found: " + fissionFile + ", fission disabled");
                    return;
                }
                string json = File.ReadAllText(fissionFile);
                if (string.IsNullOrWhiteSpace(json))
                {
                    RequireStrictInput(strict, fissionFile, "enemy fission configuration is empty");
                    renderer.Log("[FISSION] Enemy fission config file is empty: " + fissionFile);
                    return;
                }

                var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                RequireJsonKind(strict, fissionFile, root, System.Text.Json.JsonValueKind.Array, "an array");

                if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var defs = new List<FissionDef>();
                    foreach (var elem in root.EnumerateArray())
                    {
                        var f = new FissionDef();
                        f.FissionId = elem.TryGetProperty("fissionId", out var fid) ? fid.GetString() ?? "" : "";
                        f.SourceMonsterType = elem.TryGetProperty("sourceMonsterType", out var smt) ? smt.GetString() ?? "" : "";
                        f.ChildMonsterType = elem.TryGetProperty("childMonsterType", out var cmt) ? cmt.GetString() ?? "" : "";
                        f.ChildrenCount = elem.TryGetProperty("childrenCount", out var cc) ? cc.GetInt32() : 2;
                        f.HealthScale = elem.TryGetProperty("healthScale", out var hs) ? (float)hs.GetDouble() : 0.4f;
                        f.DamageScale = elem.TryGetProperty("damageScale", out var ds) ? (float)ds.GetDouble() : 0.3f;
                        f.SpeedScale = elem.TryGetProperty("speedScale", out var ss) ? (float)ss.GetDouble() : 1.2f;
                        f.GoldScale = elem.TryGetProperty("goldScale", out var gs) ? (float)gs.GetDouble() : 0.5f;
                        f.MaxGeneration = elem.TryGetProperty("maxGeneration", out var mg) ? mg.GetInt32() : 2;
                        defs.Add(f);
                    }
                    gameConfig.FissionDefs = defs.ToArray();
                    renderer.Log("[FISSION] Loaded " + defs.Count + " fission defs from " + fissionFile);
                }
            }
            catch (Exception ex)
            {
                ThrowIfStrict(strict, fissionFile, ex);
                renderer.Log("[FISSION] Failed to load fission defs: " + ex.Message);
            }
        }

        private static void LoadMorphDefs(GameConfig gameConfig, IRenderer renderer, bool strict)
        {
            const string morphFile = "Data/Configs/enemy_morphs.json";
            try
            {
                if (!File.Exists(morphFile))
                {
                    renderer.Log("[MORPH] Enemy morph config file not found: " + morphFile + ", morph disabled");
                    return;
                }
                string json = File.ReadAllText(morphFile);
                if (string.IsNullOrWhiteSpace(json))
                {
                    RequireStrictInput(strict, morphFile, "enemy morph configuration is empty");
                    renderer.Log("[MORPH] Enemy morph config file is empty: " + morphFile);
                    return;
                }

                var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                RequireJsonKind(strict, morphFile, root, System.Text.Json.JsonValueKind.Array, "an array");

                if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var defs = new List<MorphDef>();
                    foreach (var elem in root.EnumerateArray())
                    {
                        var m = new MorphDef();
                        m.MorphId = elem.TryGetProperty("morphId", out var mid) ? mid.GetString() ?? "" : "";
                        m.SourceMonsterType = elem.TryGetProperty("sourceMonsterType", out var smt) ? smt.GetString() ?? "" : "";
                        m.TargetMonsterType = elem.TryGetProperty("targetMonsterType", out var tmt) ? tmt.GetString() ?? "" : "";
                        m.TriggerType = elem.TryGetProperty("triggerType", out var tt) ? tt.GetString() ?? "HP_THRESHOLD" : "HP_THRESHOLD";
                        m.TriggerValue = elem.TryGetProperty("triggerValue", out var tv) ? (float)tv.GetDouble() : 0.5f;
                        m.Description = elem.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "";
                        m.SpeedMultOnMorph = elem.TryGetProperty("speedMultOnMorph", out var sms) ? (float)sms.GetDouble() : 1.0f;
                        m.DamageMultOnMorph = elem.TryGetProperty("damageMultOnMorph", out var dms) ? (float)dms.GetDouble() : 1.0f;
                        m.HealthMultOnMorph = elem.TryGetProperty("healthMultOnMorph", out var hms) ? (float)hms.GetDouble() : 1.0f;
                        m.Duration = elem.TryGetProperty("duration", out var dur) ? (float)dur.GetDouble() : 0f;
                        defs.Add(m);
                    }
                    gameConfig.MorphDefs = defs.ToArray();
                    renderer.Log("[MORPH] Loaded " + defs.Count + " morph defs from " + morphFile);
                }
            }
            catch (Exception ex)
            {
                ThrowIfStrict(strict, morphFile, ex);
                renderer.Log("[MORPH] Failed to load morph defs: " + ex.Message);
            }
        }

        private static void LoadCorpseEffectDefs(GameConfig gameConfig, IRenderer renderer, bool strict)
        {
            const string corpseFile = "Data/Configs/corpse_effects.json";
            try
            {
                if (!File.Exists(corpseFile))
                {
                    renderer.Log("[CORPSE] Corpse effect defs file not found: " + corpseFile + ", using defaults (no corpse effects)");
                    return;
                }
                string json = File.ReadAllText(corpseFile);
                if (string.IsNullOrWhiteSpace(json))
                {
                    RequireStrictInput(strict, corpseFile, "corpse effect configuration is empty");
                    renderer.Log("[CORPSE] Corpse effect defs file is empty: " + corpseFile);
                    return;
                }
                var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                RequireJsonKind(strict, corpseFile, root, System.Text.Json.JsonValueKind.Array, "an array");

                if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var defs = new List<CorpseEffectDef>();
                    foreach (var elem in root.EnumerateArray())
                    {
                        var c = new CorpseEffectDef();
                        c.Id = elem.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
                        c.Name = elem.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "";
                        c.EffectType = elem.TryGetProperty("effectType", out var et) ? et.GetInt32() : 0;
                        c.Duration = elem.TryGetProperty("duration", out var dur) ? (float)dur.GetDouble() : 5f;
                        c.Radius = elem.TryGetProperty("radius", out var rad) ? (float)rad.GetDouble() : 1.5f;
                        c.DamagePerTick = elem.TryGetProperty("damagePerTick", out var dpt) ? (float)dpt.GetDouble() : 0f;
                        c.TickInterval = elem.TryGetProperty("tickInterval", out var ti) ? (float)ti.GetDouble() : 1f;
                        c.SlowAmount = elem.TryGetProperty("slowAmount", out var sa) ? (float)sa.GetDouble() : 1f;
                        // Round 171 Direction 4 — Blighted Ground debuff fields (optional in JSON,
                        // default 0 so existing effect types are unaffected).
                        c.ArmorReduction = elem.TryGetProperty("armorReduction", out var ar) ? (float)ar.GetDouble() : 0f;
                        c.SpeedReduction = elem.TryGetProperty("speedReduction", out var sr) ? (float)sr.GetDouble() : 0f;
                        // Round 175 Direction 9 — Smokescreen fields (optional in JSON, default 0
                        // and 1f so existing effect types are unaffected).
                        c.MissChance = elem.TryGetProperty("missChance", out var mc) ? (float)mc.GetDouble() : 0f;
                        c.EnemySpeedBoost = elem.TryGetProperty("enemySpeedBoost", out var esb) ? (float)esb.GetDouble() : 1f;
                        // Round 183 Direction 8 — Scorched Earth fields (optional in JSON,
                        // default 0 so existing effect types are unaffected). DamageType
                        // 0=Physical / 1=Fire. VisionReduction 0..1 multiplicative penalty.
                        c.DamageType = elem.TryGetProperty("damageType", out var dt) ? dt.GetInt32() : 0;
                        c.VisionReduction = elem.TryGetProperty("visionReduction", out var vr) ? (float)vr.GetDouble() : 0f;
                        if (elem.TryGetProperty("monsterTypes", out var mtElem) && mtElem.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            c.MonsterTypes = new List<string>();
                            foreach (var mt in mtElem.EnumerateArray())
                            {
                                c.MonsterTypes.Add(mt.GetString() ?? "");
                            }
                        }
                        defs.Add(c);
                    }
                    gameConfig.CorpseEffectDefs = defs;
                    renderer.Log("[CORPSE] Loaded " + defs.Count + " corpse effect defs from " + corpseFile);
                }
            }
            catch (Exception ex)
            {
                ThrowIfStrict(strict, corpseFile, ex);
                renderer.Log("[CORPSE] Failed to load corpse effect defs: " + ex.Message);
            }
        }

        /// <summary>
        /// Load elemental terrain zone definitions from Data/Configs/terrain_zones.json.
        /// Direction 2 — Round 200 Elemental Terrain Zones (Frozen Lake / Burning Ground / Toxic Swamp).
        /// Each zone carries element type, base DPS, slow-per-stack, max stacks, lifetime, radius,
        /// tick interval, expand-over-time. Defaults to safe empty list on missing file.
        /// </summary>
        private static void LoadTerrainZoneDefs(GameConfig gameConfig, IRenderer renderer, bool strict)
        {
            const string file = "Data/Configs/terrain_zones.json";
            try
            {
                if (!File.Exists(file))
                {
                    renderer.Log("[TERRAIN_ZONE] Terrain zone defs file not found: " + file + ", using defaults (no terrain zones)");
                    return;
                }
                string json = File.ReadAllText(file);
                if (string.IsNullOrWhiteSpace(json))
                {
                    RequireStrictInput(strict, file, "terrain zone configuration is empty");
                    renderer.Log("[TERRAIN_ZONE] Terrain zone defs file is empty: " + file);
                    return;
                }
                var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                RequireJsonKind(strict, file, root, System.Text.Json.JsonValueKind.Array, "an array");

                var defs = new List<GameConfig.TerrainZoneDef>();
                if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var elem in root.EnumerateArray())
                    {
                        var tz = new GameConfig.TerrainZoneDef();
                        tz.Id = elem.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
                        tz.Name = elem.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                        tz.Element = elem.TryGetProperty("element", out var el) ? el.GetInt32() : 0;
                        tz.BaseDps = elem.TryGetProperty("baseDps", out var bd) ? (float)bd.GetDouble() : 0f;
                        tz.SlowPerStack = elem.TryGetProperty("slowPerStack", out var sp) ? (float)sp.GetDouble() : 0f;
                        tz.MaxStacks = elem.TryGetProperty("maxStacks", out var ms) ? ms.GetInt32() : 1;
                        tz.Lifetime = elem.TryGetProperty("lifetime", out var lf) ? (float)lf.GetDouble() : 8f;
                        tz.Radius = elem.TryGetProperty("radius", out var rd) ? (float)rd.GetDouble() : 3f;
                        tz.TickInterval = elem.TryGetProperty("tickInterval", out var ti) ? (float)ti.GetDouble() : 1f;
                        tz.ExpandOverTime = elem.TryGetProperty("expandOverTime", out var eot) && eot.GetBoolean();
                        tz.Description = elem.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "";
                        defs.Add(tz);
                    }
                }
                gameConfig.TerrainZoneDefs = defs;
                renderer.Log("[TERRAIN_ZONE] Loaded " + defs.Count + " terrain zone defs from " + file);
            }
            catch (Exception ex)
            {
                ThrowIfStrict(strict, file, ex);
                renderer.Log("[TERRAIN_ZONE] Failed to load terrain zone defs: " + ex.Message);
            }
        }

        /// <summary>
        /// Load tower affix (Reforge) definitions from Data/Configs/tower_affixes.json.
        /// Reforge — Split A: data layer + affix slot infrastructure. The actual reroll
        /// API is implemented in Split B.
        /// </summary>
        private static void LoadTowerAffixDefs(GameConfig gameConfig, IRenderer renderer, bool strict)
        {
            const string affixFile = "Data/Configs/tower_affixes.json";
            try
            {
                if (!File.Exists(affixFile))
                {
                    renderer.Log("[AFFIX] Tower affix defs file not found: " + affixFile + ", reforge pool empty");
                    return;
                }
                string json = File.ReadAllText(affixFile);
                if (string.IsNullOrWhiteSpace(json))
                {
                    RequireStrictInput(strict, affixFile, "tower affix configuration is empty");
                    renderer.Log("[AFFIX] Tower affix defs file is empty: " + affixFile);
                    return;
                }
                var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                RequireJsonKind(strict, affixFile, root, System.Text.Json.JsonValueKind.Array, "an array");

                if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var defs = new List<GameConfig.TowerAffixDef>();
                    foreach (var elem in root.EnumerateArray())
                    {
                        var a = new GameConfig.TowerAffixDef();
                        a.AffixId = elem.TryGetProperty("affixId", out var aid) ? aid.GetString() ?? "" : "";
                        a.Name = elem.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "";
                        a.Stat = elem.TryGetProperty("stat", out var stat) ? stat.GetString() ?? "" : "";
                        a.Magnitude = elem.TryGetProperty("magnitude", out var mag) ? (float)mag.GetDouble() : 0f;
                        a.Rarity = elem.TryGetProperty("rarity", out var rar) ? rar.GetInt32() : 0;
                        a.MinLevel = elem.TryGetProperty("minLevel", out var ml) ? ml.GetInt32() : 0;
                        a.MaxStack = elem.TryGetProperty("maxStack", out var ms) ? ms.GetInt32() : 1;
                        a.Description = elem.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "";
                        defs.Add(a);
                    }
                    gameConfig.TowerAffixes = defs.ToArray();
                    renderer.Log("[AFFIX] Loaded " + defs.Count + " tower affix defs from " + affixFile);
                }
            }
            catch (Exception ex)
            {
                ThrowIfStrict(strict, affixFile, ex);
                renderer.Log("[AFFIX] Failed to load tower affix defs: " + ex.Message);
            }
        }

        /// <summary>
        /// Round 143 Direction 1 — Load tower-vs-enemy type effectiveness matrix from
        /// Data/Configs/tower_effectiveness.json. The JSON has the shape:
        ///   { "Round143Dir1_TowerEffectiveness": [
        ///       { "towerType": 1, "towerTypeName": "AOE",
        ///         "effectiveness": [ { "enemyType": "Swarm", "multiplier": 1.30 }, ... ] },
        ///       ...
        ///     ] }
        /// Each entry is keyed as "&lt;towerTypeIndex&gt;|&lt;enemyType&gt;" in
        /// GameConfig.TowerEffectivenessMatrix. Missing entries default to 1.0 at lookup time.
        /// Safe no-op when the file is missing (effectiveness disabled).
        /// </summary>
        private static void LoadTowerEffectiveness(GameConfig gameConfig, IRenderer renderer, bool strict)
        {
            const string effFile = "Data/Configs/tower_effectiveness.json";
            try
            {
                if (!File.Exists(effFile))
                {
                    renderer.Log("[EFFECTIVENESS] Tower effectiveness file not found: " + effFile + ", using defaults (multiplier = 1.0)");
                    return;
                }
                string json = File.ReadAllText(effFile);
                if (string.IsNullOrWhiteSpace(json))
                {
                    RequireStrictInput(strict, effFile, "tower effectiveness configuration is empty");
                    renderer.Log("[EFFECTIVENESS] Tower effectiveness file is empty: " + effFile);
                    return;
                }
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Find the array under any of the known wrapper keys.
                System.Text.Json.JsonElement arrayElem = default;
                bool found = false;
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        arrayElem = prop.Value;
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    RequireStrictInput(strict, effFile, "expected an object containing an array");
                    renderer.Log("[EFFECTIVENESS] No array root in " + effFile);
                    return;
                }

                int entryCount = 0;
                var matrix = gameConfig.TowerEffectivenessMatrix;
                matrix.Clear();
                foreach (var towerElem in arrayElem.EnumerateArray())
                {
                    int towerType = towerElem.TryGetProperty("towerType", out var tt) ? tt.GetInt32() : 0;
                    if (towerType < 0) continue;
                    if (towerElem.TryGetProperty("effectiveness", out var effList) &&
                        effList.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var e in effList.EnumerateArray())
                        {
                            string enemyType = e.TryGetProperty("enemyType", out var et) ? (et.GetString() ?? "") : "";
                            float mult = e.TryGetProperty("multiplier", out var m) ? (float)m.GetDouble() : 1.0f;
                            if (string.IsNullOrEmpty(enemyType)) continue;
                            // Composite key: "towerType|enemyType" — avoids tuple allocation.
                            string key = towerType + "|" + enemyType;
                            matrix[key] = mult;
                            entryCount++;
                        }
                    }
                }
                gameConfig.TowerEffectivenessEntryCount = entryCount;
                renderer.Log("[EFFECTIVENESS] Loaded " + entryCount + " tower-vs-enemy effectiveness entries from " + effFile);
            }
            catch (Exception ex)
            {
                ThrowIfStrict(strict, effFile, ex);
                renderer.Log("[EFFECTIVENESS] Failed to load tower effectiveness: " + ex.Message);
            }
        }

        /// <summary>
        /// Load the per-tower modifier pool from <c>Data/Configs/tower_modifiers.json</c>.
        /// Each entry is a <see cref="GameConfig.TowerModifierDef"/> with weight + rarity.
        /// Distinct from the affix system: affixes are stackable stat rerolls (Round 35),
        /// modifiers are ONE roll per tower at placement (Round 145 Direction 3).
        /// Missing file → safe empty pool (no modifiers rolled — towers spawn with -1).
        /// </summary>
        private static void LoadTowerModifiers(GameConfig gameConfig, IRenderer renderer, bool strict)
        {
            const string modFile = "Data/Configs/tower_modifiers.json";
            try
            {
                if (!File.Exists(modFile))
                {
                    renderer.Log("[TOWER-MODIFIER] Modifier file not found: " + modFile + " (towers spawn with no modifier — -1 sentinel).");
                    gameConfig.TowerModifiers = Array.Empty<GameConfig.TowerModifierDef>();
                    return;
                }
                string json = File.ReadAllText(modFile);
                if (string.IsNullOrWhiteSpace(json))
                {
                    RequireStrictInput(strict, modFile, "tower modifier configuration is empty");
                    renderer.Log("[TOWER-MODIFIER] Modifier file is empty: " + modFile);
                    gameConfig.TowerModifiers = Array.Empty<GameConfig.TowerModifierDef>();
                    return;
                }
                var arr = System.Text.Json.JsonSerializer.Deserialize<List<GameConfig.TowerModifierDef>>(json,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (arr == null)
                {
                    RequireStrictInput(strict, modFile, "tower modifier configuration deserialized to null");
                    renderer.Log("[TOWER-MODIFIER] No modifiers parsed from " + modFile);
                    gameConfig.TowerModifiers = Array.Empty<GameConfig.TowerModifierDef>();
                    return;
                }
                if (arr.Count == 0)
                {
                    renderer.Log("[TOWER-MODIFIER] Empty modifier pool in " + modFile);
                    gameConfig.TowerModifiers = Array.Empty<GameConfig.TowerModifierDef>();
                    return;
                }

                // Drop malformed entries (no ModifierId or zero-weight) and clamp fields.
                var clean = new List<GameConfig.TowerModifierDef>(arr.Count);
                foreach (var m in arr)
                {
                    if (m == null) continue;
                    if (string.IsNullOrWhiteSpace(m.ModifierId)) continue;
                    if (m.Weight <= 0) m.Weight = 1;
                    if (m.Rarity < 0) m.Rarity = 0;
                    if (m.Rarity > 4) m.Rarity = 4;
                    clean.Add(m);
                }
                gameConfig.TowerModifiers = clean.ToArray();
                renderer.Log("[TOWER-MODIFIER] Loaded " + gameConfig.TowerModifiers.Length + " tower modifiers from " + modFile);
            }
            catch (Exception ex)
            {
                ThrowIfStrict(strict, modFile, ex);
                renderer.Log("[TOWER-MODIFIER] Failed to load tower modifiers: " + ex.Message);
                gameConfig.TowerModifiers = Array.Empty<GameConfig.TowerModifierDef>();
            }
        }

        /// <summary>
        /// Load random event definitions from Data/Configs/random_events.json.
        /// </summary>
        private static void LoadRandomEventDefs(GameConfig gameConfig, IRenderer renderer, bool strict)
        {
            const string eventFile = "Data/Configs/random_events.json";
            try
            {
                if (!File.Exists(eventFile))
                {
                    renderer.Log("[EVENT] Random event defs file not found: " + eventFile + ", using defaults (events disabled)");
                    return;
                }
                string json = File.ReadAllText(eventFile);
                if (string.IsNullOrWhiteSpace(json))
                {
                    RequireStrictInput(strict, eventFile, "random event configuration is empty");
                    renderer.Log("[EVENT] Random event defs file is empty: " + eventFile);
                    return;
                }
                var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                var config = gameConfig.RandomEvents;
                if (root.TryGetProperty("globalEventChance", out var gec))
                    config.GlobalEventChance = (float)gec.GetDouble();
                if (root.TryGetProperty("minEventGap", out var meg))
                    config.MinEventGap = (float)meg.GetDouble();

                if (!root.TryGetProperty("events", out var declaredEvents) ||
                    declaredEvents.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    RequireStrictInput(strict, eventFile, "expected an events array");
                    renderer.Log("[EVENT] No events array in " + eventFile + ", events disabled");
                    return;
                }
                if (root.TryGetProperty("events", out var eventsElem) && eventsElem.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var elem in eventsElem.EnumerateArray())
                    {
                        var evt = new RandomEventDef();
                        evt.Id = elem.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
                        evt.Name = elem.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                        evt.EventType = elem.TryGetProperty("eventType", out var et) ? et.GetInt32() : 0;
                        evt.Weight = elem.TryGetProperty("weight", out var wt) ? (float)wt.GetDouble() : 0f;
                        evt.MinWave = elem.TryGetProperty("minWave", out var mw) ? mw.GetInt32() : 0;
                        evt.MaxWave = elem.TryGetProperty("maxWave", out var xw) ? xw.GetInt32() : -1;
                        evt.Cooldown = elem.TryGetProperty("cooldown", out var cd) ? (float)cd.GetDouble() : 60f;
                        evt.Duration = elem.TryGetProperty("duration", out var dur) ? (float)dur.GetDouble() : 0f;
                        evt.DifficultyMult = elem.TryGetProperty("difficultyMult", out var dm) ? (float)dm.GetDouble() : 1f;
                        evt.BonusGold = elem.TryGetProperty("bonusGold", out var bg) ? (float)bg.GetDouble() : 0f;
                        evt.BonusResearch = elem.TryGetProperty("bonusResearch", out var br) ? br.GetInt32() : 0;
                        evt.Param = elem.TryGetProperty("param", out var p) ? (float)p.GetDouble() : 0f;
                        evt.Param2 = elem.TryGetProperty("param2", out var p2) ? (float)p2.GetDouble() : 0f;
                        config.Events.Add(evt);
                    }
                    renderer.Log("[EVENT] Loaded " + config.Events.Count + " random event defs from " + eventFile);
                }
            }
            catch (Exception ex)
            {
                ThrowIfStrict(strict, eventFile, ex);
                renderer.Log("[EVENT] Failed to load random event defs: " + ex.Message);
            }
        }

        /// <summary>
        /// Load the daily challenge modifier pool from <c>Data/Configs/daily_modifiers.json</c>.
        /// Each entry becomes a <see cref="DailyModifierDef"/> available for selection.
        /// Missing or empty file → empty pool → daily system is a no-op (stock values).
        /// Optional <c>modifierCount</c> at the top level overrides the default 3.
        /// </summary>
        private static void LoadDailyModifierPool(GameConfig gameConfig, IRenderer renderer, bool strict)
        {
            const string file = "Data/Configs/daily_modifiers.json";
            try
            {
                if (!File.Exists(file))
                {
                    renderer.Log("[DAILY] Daily modifier file not found: " + file + ", using defaults (no daily challenge)");
                    return;
                }
                string json = File.ReadAllText(file);
                if (string.IsNullOrWhiteSpace(json))
                {
                    RequireStrictInput(strict, file, "daily modifier configuration is empty");
                    renderer.Log("[DAILY] Daily modifier file is empty: " + file);
                    return;
                }
                var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("modifierCount", out var mc) && mc.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    int newCount = mc.GetInt32();
                    if (newCount > 0) gameConfig.DailyModifierCount = newCount;
                }
                if (!root.TryGetProperty("modifiers", out var declaredModifiers) ||
                    declaredModifiers.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    RequireStrictInput(strict, file, "expected a modifiers array");
                    renderer.Log("[DAILY] No modifiers array in " + file + ", daily challenge disabled");
                    return;
                }
                if (root.TryGetProperty("modifiers", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var elem in arr.EnumerateArray())
                    {
                        var m = new DailyModifierDef();
                        m.Id = elem.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
                        m.Name = elem.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                        m.Description = elem.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                        m.DamageMult = elem.TryGetProperty("damageMult", out var dm) ? (float)dm.GetDouble() : 1.0f;
                        m.GoldMult = elem.TryGetProperty("goldMult", out var gm) ? (float)gm.GetDouble() : 1.0f;
                        m.EnemyHpMult = elem.TryGetProperty("enemyHpMult", out var ehm) ? (float)ehm.GetDouble() : 1.0f;
                        m.StartingGoldBonus = elem.TryGetProperty("startingGoldBonus", out var sgb) ? (float)sgb.GetDouble() : 0f;
                        gameConfig.DailyModifierPool.Add(m);
                    }
                }
                renderer.Log("[DAILY] Loaded " + gameConfig.DailyModifierPool.Count + " daily modifier defs from " + file);
            }
            catch (Exception ex)
            {
                ThrowIfStrict(strict, file, ex);
                renderer.Log("[DAILY] Failed to load daily modifier defs: " + ex.Message);
            }
        }

        /// <summary>
        /// Resolve today's daily challenge and apply it to the GameConfig. Pure
        /// pass-through to <see cref="DailyChallengeSystem.ResolveForDate"/> +
        /// <see cref="DailyChallengeSystem.ApplyToConfig"/>. Logs a one-line
        /// summary so the run start screen can show "Daily: Glass Cannon + Rich Start + Tank Horde".
        /// Safe no-op when the pool is empty.
        /// </summary>
        private static void ResolveDailyChallenge(GameConfig gameConfig, IRenderer renderer)
        {
            try
            {
                if (gameConfig.DailyModifierPool == null || gameConfig.DailyModifierPool.Count == 0)
                {
                    renderer.Log("[DAILY] Daily pool empty — daily system disabled (stock values)");
                    return;
                }
                var result = DailyChallengeSystem.ResolveForDate(
                    gameConfig.DailyModifierPool, DateTime.Today, gameConfig.DailyModifierCount);
                DailyChallengeSystem.ApplyToConfig(gameConfig, result);
                var names = new System.Text.StringBuilder();
                for (int i = 0; i < result.Selected.Count; i++)
                {
                    if (i > 0) names.Append(" + ");
                    names.Append(string.IsNullOrEmpty(result.Selected[i].Name)
                        ? result.Selected[i].Id
                        : result.Selected[i].Name);
                }
                renderer.Log("[DAILY] " + result.Date + " seed=" + result.Seed
                    + " modifiers=[" + names.ToString() + "]"
                    + " dmg=" + gameConfig.DailyDamageMult.ToString("F2")
                    + " gold=" + gameConfig.DailyGoldMult.ToString("F2")
                    + " enemyHp=" + gameConfig.DailyEnemyHpMult.ToString("F2")
                    + " startGoldBonus=" + gameConfig.DailyStartingGoldBonus.ToString("F0"));
            }
            catch (Exception ex)
            {
                renderer.Log("[DAILY] Failed to resolve daily challenge: " + ex.Message);
            }
        }

        private static void LoadSummonDefs(GameConfig gameConfig, IRenderer renderer, bool strict)
        {
            const string summonFile = "Data/Configs/summons.json";
            try
            {
                if (!File.Exists(summonFile))
                {
                    renderer.Log("[SUMMON] Summon defs file not found: " + summonFile + ", using defaults (no summons)");
                    return;
                }
                string json = File.ReadAllText(summonFile);
                if (string.IsNullOrWhiteSpace(json))
                {
                    RequireStrictInput(strict, summonFile, "summon definition configuration is empty");
                    renderer.Log("[SUMMON] Summon defs file is empty: " + summonFile);
                    return;
                }

                var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                RequireJsonKind(strict, summonFile, root, System.Text.Json.JsonValueKind.Array, "an array");

                if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var defs = new List<SummonDef>();
                    foreach (var elem in root.EnumerateArray())
                    {
                        var s = new SummonDef();
                        s.Id = elem.TryGetProperty("Id", out var id) ? id.GetString() ?? "" : "";
                        s.Name = elem.TryGetProperty("Name", out var name) ? name.GetString() ?? "" : "";
                        s.Description = elem.TryGetProperty("Description", out var desc) ? desc.GetString() ?? "" : "";
                        s.UnitType = elem.TryGetProperty("UnitType", out var ut) ? ut.GetInt32() : 0;
                        s.Health = elem.TryGetProperty("Health", out var hp) ? (float)hp.GetDouble() : 80f;
                        s.Damage = elem.TryGetProperty("Damage", out var dmg) ? (float)dmg.GetDouble() : 15f;
                        s.MoveSpeed = elem.TryGetProperty("MoveSpeed", out var ms) ? (float)ms.GetDouble() : 3f;
                        s.AttackRange = elem.TryGetProperty("AttackRange", out var ar) ? ar.GetInt32() : 1;
                        s.AttackSpeed = elem.TryGetProperty("AttackSpeed", out var atks) ? (float)atks.GetDouble() : 2f;
                        s.Cost = elem.TryGetProperty("Cost", out var cost) ? (float)cost.GetDouble() : 30f;
                        s.ManaCost = elem.TryGetProperty("ManaCost", out var mc) ? (float)mc.GetDouble() : 25f;
                        s.Duration = elem.TryGetProperty("Duration", out var dur) ? (float)dur.GetDouble() : 15f;
                        s.Cooldown = elem.TryGetProperty("Cooldown", out var cd) ? (float)cd.GetDouble() : 8f;
                        defs.Add(s);
                    }
                    gameConfig.Summons = defs;
                    renderer.Log("[SUMMON] Loaded " + defs.Count + " summon defs from " + summonFile);
                }
            }
            catch (Exception ex)
            {
                ThrowIfStrict(strict, summonFile, ex);
                renderer.Log("[SUMMON] Failed to load summon defs: " + ex.Message);
            }
        }

        private static TowerType ParseTowerType(string type)
        {
            if (string.IsNullOrEmpty(type)) return TowerType.Basic;
            return type switch
            {
                "Basic"    => TowerType.Basic,
                "AOE"      => TowerType.AOE,
                "Sniper"   => TowerType.Sniper,
                "Tesla"    => TowerType.Tesla,
                "Leech"    => TowerType.Leech,
                "Frost"    => TowerType.Frost,
                "Stun"     => TowerType.Stun,
                "EMP"      => TowerType.EMP,
                "Firewall" => TowerType.Firewall,
                _          => TowerType.Basic
            };
        }

        /// <summary>
        /// Load damage-saturation tunables from <c>Data/Configs/damage_saturation.json</c>.
        /// All fields are optional — missing values preserve the safe defaults in
        /// <see cref="DamageSaturationConfig"/>. The whole block can be disabled via
        /// <c>"Enabled": false</c> (in which case <see cref="DamageSaturationConfig.SaturationWindowFrames"/>
        /// is set to a sentinel value of -1 to signal "do not apply saturation" — checked in
        /// the per-damage hot path).
        /// </summary>
        private static void LoadDamageSaturationConfig(GameConfig gameConfig, IRenderer renderer, bool strict)
        {
            const string satFile = "Data/Configs/damage_saturation.json";
            try
            {
                if (!File.Exists(satFile))
                {
                    renderer.Log("[SATURATION] Damage saturation config not found: " + satFile + ", using defaults (window=30, threshold=2.0, scale=0.1)");
                    return;
                }
                string json = File.ReadAllText(satFile);
                if (string.IsNullOrWhiteSpace(json))
                {
                    RequireStrictInput(strict, satFile, "damage saturation configuration is empty");
                    renderer.Log("[SATURATION] Damage saturation config is empty: " + satFile + ", using defaults");
                    return;
                }
                if (string.IsNullOrWhiteSpace(json))
                {
                    renderer.Log("[SATURATION] Damage saturation config is empty: " + satFile);
                    return;
                }

                var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Disabled toggle — sentinel -1 window disables the entire system (checked at hot-path).
                bool enabled = root.TryGetProperty("Enabled", out var en) && en.ValueKind == System.Text.Json.JsonValueKind.False
                    ? false
                    : true;
                if (!enabled)
                {
                    DamageSaturationConfig.SaturationWindowFrames = -1;
                    renderer.Log("[SATURATION] Damage saturation DISABLED via config");
                    return;
                }

                if (root.TryGetProperty("WindowFrames", out var wf) && wf.ValueKind == System.Text.Json.JsonValueKind.Number)
                    DamageSaturationConfig.SaturationWindowFrames = wf.GetInt32();
                if (root.TryGetProperty("ThresholdMultipleOfMaxHp", out var tm) && tm.ValueKind == System.Text.Json.JsonValueKind.Number)
                    DamageSaturationConfig.SaturationThresholdMult = (float)tm.GetDouble();
                if (root.TryGetProperty("ScaleMultiplier", out var sm) && sm.ValueKind == System.Text.Json.JsonValueKind.Number)
                    DamageSaturationConfig.SaturationScaleMult = (float)sm.GetDouble();

                renderer.Log($"[SATURATION] Loaded damage saturation: window={DamageSaturationConfig.SaturationWindowFrames} frames, threshold={DamageSaturationConfig.SaturationThresholdMult:F2}× maxHP, scale={DamageSaturationConfig.SaturationScaleMult:F2}×");
            }
            catch (Exception ex)
            {
                ThrowIfStrict(strict, satFile, ex);
                renderer.Log("[SATURATION] Failed to load damage saturation config: " + ex.Message + " — using defaults");
            }
        }

        /// <summary>
        /// Load destructible object prototypes from <c>Data/Configs/destructibles.json</c>.
        /// Round 95 Direction 5. Missing or malformed entries are silently skipped — the
        /// destructible system is opt-in (empty DestructibleDefs → no destructibles spawned
        /// → no hot-path overhead). Per-entry fields fall back to safe defaults when absent.
        /// </summary>
        private static void LoadDestructibleDefs(GameConfig gameConfig, IRenderer renderer, bool strict)
        {
            const string file = "Data/Configs/destructibles.json";
            try
            {
                if (!File.Exists(file))
                {
                    renderer.Log("[DESTRUCTIBLE] Destructible config not found: " + file + " — no destructibles will spawn (opt-in)");
                    return;
                }
                string json = File.ReadAllText(file);
                if (string.IsNullOrWhiteSpace(json))
                {
                    RequireStrictInput(strict, file, "destructible configuration is empty");
                    renderer.Log("[DESTRUCTIBLE] Destructible config is empty: " + file);
                    return;
                }
                var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("Destructibles", out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    RequireStrictInput(strict, file, "expected a Destructibles array");
                    renderer.Log("[DESTRUCTIBLE] No 'Destructibles' array in " + file + " — opt-in, no destructibles");
                    return;
                }
                var defs = new List<DestructibleDef>();
                int idx = 0;
                foreach (var entry in arr.EnumerateArray())
                {
                    var def = new DestructibleDef
                    {
                        Id = entry.TryGetProperty("Id", out var idEl) ? idEl.GetString() : ("destructible_" + idx),
                        Name = entry.TryGetProperty("Name", out var nEl) ? nEl.GetString() : "Destructible",
                        Description = entry.TryGetProperty("Description", out var dEl) ? dEl.GetString() : "",
                        MaxHealth = entry.TryGetProperty("MaxHealth", out var mhEl) && mhEl.ValueKind == System.Text.Json.JsonValueKind.Number
                            ? (float)mhEl.GetDouble() : 0f,
                        OnDestroyEffect = entry.TryGetProperty("OnDestroyEffect", out var effEl) && effEl.ValueKind == System.Text.Json.JsonValueKind.Number
                            ? effEl.GetInt32() : 0,
                        OnDestroyValue = entry.TryGetProperty("OnDestroyValue", out var vEl) && vEl.ValueKind == System.Text.Json.JsonValueKind.Number
                            ? (float)vEl.GetDouble() : 0f,
                        ExplosionRadius = entry.TryGetProperty("ExplosionRadius", out var rEl) && rEl.ValueKind == System.Text.Json.JsonValueKind.Number
                            ? (float)rEl.GetDouble() : 5f
                    };
                    defs.Add(def);
                    idx++;
                }
                gameConfig.DestructibleDefs = defs.ToArray();
                renderer.Log($"[DESTRUCTIBLE] Loaded {gameConfig.DestructibleDefs.Length} destructible prototypes from {file}");
            }
            catch (Exception ex)
            {
                ThrowIfStrict(strict, file, ex);
                renderer.Log("[DESTRUCTIBLE] Failed to load destructible config: " + ex.Message + " — no destructibles will spawn");
            }
        }

        /// <summary>
        /// Round 107 Direction 6 — load mark subsystem config (Data/Configs/marks.json).
        /// Top-level fields: <c>defaultDecayInterval</c>, <c>maxStackCap</c>. <c>marks</c>
        /// array is informational/logged only; per-mark-type wiring (e.g. which tower
        /// applies which mark) is done via monster/tower configs, not in this loader.
        /// </summary>
        private static void LoadMarkConfig(GameConfig gameConfig, IRenderer renderer, bool strict)
        {
            const string file = "Data/Configs/marks.json";
            try
            {
                if (!File.Exists(file))
                {
                    renderer.Log("[MARK] Mark config not found: " + file + " — using MarkSubsystemConfig defaults (opt-in)");
                    return;
                }
                string json = File.ReadAllText(file);
                if (string.IsNullOrWhiteSpace(json))
                {
                    RequireStrictInput(strict, file, "mark configuration is empty");
                    renderer.Log("[MARK] Mark config is empty: " + file + " — using defaults");
                    return;
                }
                var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                int markCount = 0;
                float decay = MarkSubsystemConfig.DefaultDecayInterval;
                int cap = MarkSubsystemConfig.DefaultMaxStackCap;
                if (root.TryGetProperty("defaultDecayInterval", out var decEl) && decEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    decay = (float)decEl.GetDouble();
                    if (decay < 0.05f) decay = 0.05f; // clamp: < 0.05s is effectively per-frame
                }
                if (root.TryGetProperty("maxStackCap", out var capEl) && capEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    cap = capEl.GetInt32();
                    if (cap < 1) cap = 1;
                    if (cap > 1000) cap = 1000;
                }
                if (root.TryGetProperty("marks", out var marksEl) && marksEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    markCount = marksEl.GetArrayLength();
                }
                renderer.Log($"[MARK] Loaded mark subsystem config: decay={decay:F2}s, cap={cap}, {markCount} mark types defined in {file}");
                // (We don't store these as fields on gameConfig because MarkSystem
                // owns its own MarkConfig which defaults to safe values. The JSON is
                // loaded for future per-mark-type wiring / debugging only.)
            }
            catch (Exception ex)
            {
                ThrowIfStrict(strict, file, ex);
                renderer.Log("[MARK] Failed to load mark config: " + ex.Message + " — using MarkSubsystemConfig defaults");
            }
        }

        /// <summary>
 /// Round178 Direction6 — Pre-fight Buff config. Reads from
 /// Data/Configs/prefight_buffs.json if present; otherwise the GameConfig
 /// keeps its coded PreFightBuffConfig defaults (Enabled=true,
 /// OptionsPerWave=3, Pool=Array.Empty). The Pool array is parsed
 /// into PreFightBuffOptionDef entries; missing fields fall back to
 /// per-field defaults so the file can be partial without breaking.
 /// </summary>
 private static void LoadPreFightBuffConfig(GameConfig gameConfig, IRenderer renderer, bool strict)
 {
     const string file = "Data/Configs/prefight_buffs.json";
     try
     {
         if (!File.Exists(file))
         {
             renderer.Log("[PREFIGHT] prefight_buffs.json not found, using coded defaults");
             return;
         }
         string json = File.ReadAllText(file);
         if (string.IsNullOrWhiteSpace(json))
         {
             RequireStrictInput(strict, file, "pre-fight buff configuration is empty");
             return;
         }
         using var doc = System.Text.Json.JsonDocument.Parse(json);
         var root = doc.RootElement;

         var cfg = gameConfig.PreFight ?? new PreFightBuffConfig();
         if (root.TryGetProperty("Enabled", out var en)) cfg.Enabled = en.GetBoolean();
         if (root.TryGetProperty("OptionsPerWave", out var opw)) cfg.OptionsPerWave = opw.GetInt32();

         if (root.TryGetProperty("Pool", out var poolEl) && poolEl.ValueKind == System.Text.Json.JsonValueKind.Array)
         {
             var pool = new List<PreFightBuffOptionDef>();
             foreach (var item in poolEl.EnumerateArray())
             {
                 if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                 var def = new PreFightBuffOptionDef();
                 if (item.TryGetProperty("Id", out var idEl)) def.Id = idEl.GetString() ?? "";
                 // Round178 bug-scan fix: if "Name" is missing from JSON, fall back to Id (not empty string).
                 if (item.TryGetProperty("Name", out var nameEl)) def.Name = nameEl.GetString() ?? def.Id;
                 if (string.IsNullOrEmpty(def.Name)) def.Name = def.Id;
                 if (item.TryGetProperty("Weight", out var wEl)) def.Weight = (float)wEl.GetDouble();
                 if (item.TryGetProperty("DamageMult", out var dmEl)) def.DamageMult = (float)dmEl.GetDouble();
                 if (item.TryGetProperty("SpeedMult", out var smEl)) def.SpeedMult = (float)smEl.GetDouble();
                 if (item.TryGetProperty("CritChance", out var ccEl)) def.CritChance = (float)ccEl.GetDouble();
                 if (item.TryGetProperty("MaxHpMult", out var mhEl)) def.MaxHpMult = (float)mhEl.GetDouble();
                 // Round178 bug-scan fix: skip entries without an Id or with non-positive weight.
                 if (string.IsNullOrEmpty(def.Id) || def.Weight <= 0f) continue;
                 pool.Add(def);
             }
             cfg.Pool = pool.ToArray();
         }

         gameConfig.PreFight = cfg;
         renderer.Log("[PREFIGHT] Loaded prefight_buffs config from " + file + " (pool=" + cfg.Pool.Length + ")");
     }
     catch (Exception ex)
     {
         ThrowIfStrict(strict, file, ex);
         renderer.Log("[PREFIGHT] Failed to load prefight_buffs config: " + ex.Message + " — using coded defaults");
     }
 }

        /// <summary>
        /// Round174+ Direction3 — Momentum config. Reads from
        /// Data/Configs/momentum.json if present; otherwise the GameConfig
        /// keeps its coded MomentumConfig defaults (Enabled=true,
        /// TierDuration=30s, MaxTiers=10, DamageBonusPerTier=0.02,
        /// SpeedBonusPerTier=0.01, ResetOnWave=true). All five knobs are
        /// optional — missing fields fall back to those defaults so the
        /// file can be partial without breaking the loader.
        /// </summary>
        private static void LoadMomentumConfig(GameConfig gameConfig, IRenderer renderer, bool strict)
        {
            const string file = "Data/Configs/momentum.json";
            try
            {
                if (!File.Exists(file))
                {
                    renderer.Log("[MOMENTUM] momentum.json not found, using coded defaults");
                    return;
                }
                string json = File.ReadAllText(file);
                if (string.IsNullOrWhiteSpace(json))
                {
                    RequireStrictInput(strict, file, "momentum configuration is empty");
                    return;
                }
                // Sentinel-tolerant: System.Text.Json is happy with missing
                // fields, they fall back to the coded property defaults
                // (Enabled=true, TierDuration=30f, MaxTiers=10, etc).
                var opts = new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };
                var cfg = System.Text.Json.JsonSerializer.Deserialize<MomentumConfig>(json, opts);
                if (cfg == null)
                {
                    RequireStrictInput(strict, file, "momentum configuration deserialized to null");
                    renderer.Log("[MOMENTUM] momentum.json deserialized to null, using coded defaults");
                    return;
                }
                gameConfig.Momentum = cfg;
                renderer.Log("[MOMENTUM] Loaded momentum config from " + file + " (tierDuration=" + cfg.TierDuration + "s, maxTiers=" + cfg.MaxTiers + ", dmg/tier=" + cfg.DamageBonusPerTier + ", spd/tier=" + cfg.SpeedBonusPerTier + ", resetOnWave=" + cfg.ResetOnWave + ")");
            }
            catch (Exception ex)
            {
                ThrowIfStrict(strict, file, ex);
                renderer.Log("[MOMENTUM] Failed to load momentum config: " + ex.Message + " — using coded defaults");
            }
        }

        /// <summary>
        /// Round 207 Direction 2 — Adrenaline config. Reads from
        /// Data/Configs/adrenaline.json if present; otherwise the GameConfig
        /// keeps its coded AdrenalineConfig defaults (Enabled=true,
        /// LowHpThreshold=0.30, CriticalHpThreshold=0.10, LowTierAtkSpd=0.25,
        /// CriticalTierAtkSpd=0.50, LowTierCdMult=0.80, CriticalTierCdMult=0.50,
        /// RushDurationFrames=60). All eight knobs are optional — missing fields
        /// fall back to those defaults so the file can be partial without breaking
        /// the loader (System.Text.Json is happy with missing properties).
        /// </summary>
        private static void LoadAdrenalineConfig(GameConfig gameConfig, IRenderer renderer, bool strict)
        {
            const string file = "Data/Configs/adrenaline.json";
            try
            {
                if (!File.Exists(file))
                {
                    renderer.Log("[ADRENALINE] adrenaline.json not found, using coded defaults");
                    return;
                }
                string json = File.ReadAllText(file);
                if (string.IsNullOrWhiteSpace(json))
                {
                    RequireStrictInput(strict, file, "adrenaline configuration is empty");
                    return;
                }
                // Sentinel-tolerant: System.Text.Json is happy with missing fields,
                // they fall back to the coded property defaults.
                var opts = new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };
                var cfg = System.Text.Json.JsonSerializer.Deserialize<AdrenalineConfig>(json, opts);
                if (cfg == null)
                {
                    RequireStrictInput(strict, file, "adrenaline configuration deserialized to null");
                    renderer.Log("[ADRENALINE] adrenaline.json deserialized to null, using coded defaults");
                    return;
                }
                gameConfig.Adrenaline = cfg;
                renderer.Log("[ADRENALINE] Loaded adrenaline config from " + file + " (lowHp=" + cfg.LowHpThreshold + ", criticalHp=" + cfg.CriticalHpThreshold + ", lowAtkSpdBonus=" + cfg.LowTierAttackSpeedBonus + ", critAtkSpdBonus=" + cfg.CriticalTierAttackSpeedBonus + ", lowCdMult=" + cfg.LowTierCooldownMult + ", critCdMult=" + cfg.CriticalTierCooldownMult + ", rushFrames=" + cfg.RushDurationFrames + ")");
            }
            catch (Exception ex)
            {
                ThrowIfStrict(strict, file, ex);
                renderer.Log("[ADRENALINE] Failed to load adrenaline config: " + ex.Message + " — using coded defaults");
            }
        }

 /// <summary>
 /// Round175 Direction1 — Mana Shield config. Reads from
 /// Data/Configs/mana_shield.json if present; otherwise the GameConfig
 /// keeps its coded ManaShieldConfig defaults (Enabled=true, ratio=1.0,
 /// MaxShieldPercent=0.5, Decay=5.0/s, TriggerThreshold=0.7). All five
 /// knobs are optional — missing fields fall back to those defaults so
 /// the file can be partial without breaking the loader.
 /// </summary>
 private static void LoadManaShieldConfig(GameConfig gameConfig, IRenderer renderer, bool strict)
        {
            const string file = "Data/Configs/mana_shield.json";
            try
            {
                if (!File.Exists(file))
                {
                    renderer.Log("[MANASHIELD] mana_shield.json not found, using coded defaults");
                    return;
                }
                string json = File.ReadAllText(file);
                if (string.IsNullOrWhiteSpace(json))
                {
                    RequireStrictInput(strict, file, "mana shield configuration is empty");
                    return;
                }
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                var cfg = gameConfig.ManaShield ?? new ManaShieldConfig();
                if (root.TryGetProperty("Enabled", out var en)) cfg.Enabled = en.GetBoolean();
                if (root.TryGetProperty("ConversionRatio", out var cr)) cfg.ConversionRatio = (float)cr.GetDouble();
                if (root.TryGetProperty("MaxShieldPercent", out var ms)) cfg.MaxShieldPercent = (float)ms.GetDouble();
                if (root.TryGetProperty("DecayPerSecond", out var dp)) cfg.DecayPerSecond = (float)dp.GetDouble();
                if (root.TryGetProperty("TriggerThresholdPercent", out var tp)) cfg.TriggerThresholdPercent = (float)tp.GetDouble();
                gameConfig.ManaShield = cfg;
                renderer.Log("[MANASHIELD] Loaded mana_shield config from " + file);
            }
            catch (Exception ex)
            {
                ThrowIfStrict(strict, file, ex);
                renderer.Log("[MANASHIELD] Failed to load mana_shield config: " + ex.Message + " — using coded defaults");
            }
        }

        /// <summary>
        /// Round 178+ Direction 5 — Tide / Crest config. Reads from
        /// Data/Configs/crests.json if present; otherwise the GameConfig
        /// keeps its coded CrestConfig defaults (Enabled=true,
        /// Crests=Array.Empty<CrestDef>()). The JSON file ships a default
        /// roster (CrestOfFury / CrestOfBounty / TideOfHealing /
        /// CrestOfFortitude) that gets the system working out of the box.
        /// All knobs are optional — missing fields fall back to the coded
        /// property defaults so the file can be partial without breaking
        /// the loader.
        /// </summary>
        private static void LoadCrestConfig(GameConfig gameConfig, IRenderer renderer, bool strict)
        {
            const string file = "Data/Configs/crests.json";
            try
            {
                if (!File.Exists(file))
                {
                    renderer.Log("[CREST] crests.json not found, using coded defaults (empty crest roster)");
                    return;
                }
                string json = File.ReadAllText(file);
                if (string.IsNullOrWhiteSpace(json))
                {
                    RequireStrictInput(strict, file, "crest configuration is empty");
                    return;
                }
                var opts = new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };
                var cfg = System.Text.Json.JsonSerializer.Deserialize<CrestConfig>(json, opts);
                if (cfg == null)
                {
                    RequireStrictInput(strict, file, "crest configuration deserialized to null");
                    renderer.Log("[CREST] crests.json deserialized to null, using coded defaults");
                    return;
                }
                gameConfig.Crest = cfg;
                int crestCount = cfg.Crests != null ? cfg.Crests.Length : 0;
                renderer.Log("[CREST] Loaded crests config from " + file + " (enabled=" + cfg.Enabled + ", crests=" + crestCount + ")");
            }
            catch (Exception ex)
            {
                ThrowIfStrict(strict, file, ex);
                renderer.Log("[CREST] Failed to load crests config: " + ex.Message + " — using coded defaults");
            }
        }
    }
}
