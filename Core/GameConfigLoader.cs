using System;
using System.IO;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Config
{
    public class GameConfigLoader
    {
        private const string CONFIG_FILE = "game_config.json";

        public static GameConfig LoadConfig(IRenderer renderer)
        {
            try
            {
                if (!File.Exists(CONFIG_FILE))
                {
                    renderer.Log("[CONFIG] Configuration file not found: " + CONFIG_FILE);
                    renderer.Log("[CONFIG] Using default configuration");
                    return GetDefaultConfig();
                }

                string jsonContent = File.ReadAllText(CONFIG_FILE);

                if (string.IsNullOrWhiteSpace(jsonContent))
                {
                    renderer.Log("[CONFIG] Configuration file is empty: " + CONFIG_FILE);
                    renderer.Log("[CONFIG] Using default configuration");
                    return GetDefaultConfig();
                }

                var gameConfig = ParseGameConfig(jsonContent);

                // Load behavior trees
                LoadBehaviorTrees(gameConfig, renderer);

                if (gameConfig == null)
                {
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
                renderer.Log("[ERROR] Failed to load configuration from " + CONFIG_FILE + ": " + ex.Message);
                return GetDefaultConfig();
            }
        }

        private static void LoadBehaviorTrees(GameConfig gameConfig, IRenderer renderer)
        {
            const string btFile = "Data/Configs/behavior_trees.json";
            try
            {
                if (!File.Exists(btFile))
                {
                    renderer.Log("[BT] Behavior trees file not found: " + btFile + ", using empty map");
                    return;
                }
                string json = File.ReadAllText(btFile);
                if (string.IsNullOrWhiteSpace(json))
                {
                    renderer.Log("[BT] Behavior trees file is empty: " + btFile);
                    return;
                }
                ParseBehaviorTrees(gameConfig, json);
                renderer.Log("[BT] Loaded " + gameConfig.BehaviorTrees.Count + " behavior trees from " + btFile);
            }
            catch (Exception ex)
            {
                renderer.Log("[BT] Failed to load behavior trees: " + ex.Message);
            }
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
                        Children = ParseStringArray(nodeObjJson, "Children")?.ToArray()
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

            return gameConfig;
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
            tower.Type = ExtractString(json, "Type");
            tower.Damage = ExtractFloat(json, "Damage");
            tower.Range = ExtractInt(json, "Range");
            tower.AttackSpeed = ExtractFloat(json, "AttackSpeed");
            tower.Cost = ExtractFloat(json, "Cost");
            tower.UpgradeCost = ExtractFloat(json, "UpgradeCost");
            return tower;
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
            player.StartingSkills = ExtractStringList(json, "StartingSkills");

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

            return level;
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

            return wave;
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

        private static string ExtractString(string json, string key)
        {
            string keyPattern = "\"" + key + "\":";
            int keyIndex = json.IndexOf(keyPattern);
            if (keyIndex == -1) return null;

            int pos = keyIndex + keyPattern.Length;
            while (pos < json.Length && char.IsWhiteSpace(json[pos])) pos++;

            if (pos >= json.Length) return null;

            // Check if value is a number (not quoted)
            if (char.IsDigit(json[pos]) || json[pos] == '-' || json[pos] == '+')
            {
                // Extract number
                int valueEnd = pos;
                while (valueEnd < json.Length && (char.IsDigit(json[valueEnd]) || json[valueEnd] == '.'))
                {
                    valueEnd++;
                }
                return json.Substring(pos, valueEnd - pos);
            }

            // Check if value is quoted string
            if (json[pos] == '"')
            {
                pos++;
                int endQuote = json.IndexOf("\"", pos);
                if (endQuote == -1) return null;
                return json.Substring(pos, endQuote - pos);
            }
            else if (json[pos] == '{' || json[pos] == '[')
            {
                int braceEnd = FindMatchingBrace(json, pos);
                if (braceEnd == -1) return null;
                return json.Substring(pos, braceEnd - pos);
            }

            return null;
        }

        private static List<string> ExtractStringList(string json, string key)
        {
            var list = new List<string>();
            int keyIndex = json.IndexOf(key);
            if (keyIndex == -1) return list;

            int arrayStart = json.IndexOf("[", keyIndex);
            if (arrayStart == -1) return list;

            int depth = 0;
            int start = arrayStart + 1;
            for (int i = arrayStart; i < json.Length; i++)
            {
                if (json[i] == '[') depth++;
                else if (json[i] == ']') { depth--; if (depth == 0) { string arr = json.Substring(start, i - start); var matches = System.Text.RegularExpressions.Regex.Matches(arr, "\"[^\"]*\""); foreach (System.Text.RegularExpressions.Match m in matches) list.Add(m.Value.Trim('\"')); break; } }
            }
            return list;
        }

        private static float ExtractFloat(string json, string key)
        {
            string value = ExtractString(json, key);
            if (string.IsNullOrEmpty(value)) return 0f;

            float result;
            if (float.TryParse(value, out result))
            {
                return result;
            }
            return 0f;
        }

        private static int ExtractInt(string json, string key)
        {
            string value = ExtractString(json, key);
            if (string.IsNullOrEmpty(value)) return 0;

            int result;
            if (int.TryParse(value, out result))
            {
                return result;
            }
            return 0;
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
            return skill;
        }
    }
}
