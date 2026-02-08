using System;
using System.IO;
using BattleSystemECS.Components;
using BattleSystemECS.Systems;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Config
{
    public class GameConfigLoader
    {
        private const string CONFIG_FILE = "game_config.json";

        public static GameConfig LoadConfig(IRenderer renderer)
        {
            try
            {
                string jsonContent = File.ReadAllText(CONFIG_FILE);
                var gameConfig = new GameConfig();

                // Parse Player section
                int playerStart = jsonContent.IndexOf("\"Player\"");
                if (playerStart != -1)
                {
                    int playerEnd = FindMatchingBrace(jsonContent, playerStart + 8, '{', '}');
                    string playerJson = jsonContent.Substring(playerStart + 8, playerEnd - (playerStart + 8));

                    gameConfig.Player = ParsePlayerConfig(playerJson);
                }

                // Parse MonsterTypes section
                int monstersStart = jsonContent.IndexOf("\"MonsterTypes\"");
                if (monstersStart != -1)
                {
                    int monstersEnd = FindMatchingBrace(jsonContent, monstersStart + 15, '[', ']');
                    string monstersJson = jsonContent.Substring(monstersStart + 15, monstersEnd - (monstersStart + 15));

                    gameConfig.MonsterTypes = ParseMonsterTypes(monstersJson);
                }

                // Parse Levels section
                int levelsStart = jsonContent.IndexOf("\"Levels\"");
                if (levelsStart != -1)
                {
                    int levelsEnd = FindMatchingBrace(jsonContent, levelsStart + 9, '[', ']');
                    string levelsJson = jsonContent.Substring(levelsStart + 9, levelsEnd - (levelsStart + 9));

                    gameConfig.Levels = ParseLevels(levelsJson);
                }

                // Set current level
                if (gameConfig.Levels.Count > 0)
                {
                    gameConfig.CurrentLevel = gameConfig.Levels[0];
                }

                renderer.Log("[CONFIG] Successfully loaded configuration from " + CONFIG_FILE);
                renderer.Log("[CONFIG]   - " + gameConfig.MonsterTypes.Count + " monster types");
                renderer.Log("[CONFIG]   - " + gameConfig.Levels.Count + " levels");

                return gameConfig;
            }
            catch (Exception ex)
            {
                renderer.Log("[ERROR] Failed to load configuration from " + CONFIG_FILE + ": " + ex.Message);

                // Return default config
                var defaultConfig = new GameConfig();
                defaultConfig.Player = new PlayerConfig();
                defaultConfig.Player.Name = "Player";
                defaultConfig.Player.Type = "Tower";
                defaultConfig.Player.AttackRange = 3f;
                defaultConfig.Player.AttackInterval = 1f;
                defaultConfig.Player.AttackDamage = 10f;
                defaultConfig.Player.CurrentLevel = 1;
                defaultConfig.Player.UpgradeThreshold = 100f;

                var defaultMonster = new MonsterConfig();
                defaultMonster.Name = "Normal Slime";
                defaultMonster.Type = "Normal";
                defaultMonster.Health = 20f;
                defaultMonster.MaxHealth = 20f;
                defaultMonster.Damage = 5f;
                defaultMonster.MoveSpeed = 1f;
                defaultMonster.AttackRange = 1f;
                defaultMonster.AttackInterval = 1.5f;
                defaultMonster.GoldReward = 10;
                defaultMonster.Skills = new System.Collections.Generic.List<string> { "Normal Attack" };
                defaultConfig.MonsterTypes.Add(defaultMonster);

                var defaultLevel = new LevelConfig();
                defaultLevel.LevelNumber = 1;
                defaultLevel.WaveCount = 3;
                defaultLevel.Waves = new System.Collections.Generic.List<WaveConfig>();
                for (int i = 1; i <= 3; i++)
                {
                    defaultLevel.Waves.Add(new WaveConfig { WaveNumber = i, MonsterType = "Normal", EnemyCount = 5 });
                }
                defaultConfig.Levels.Add(defaultLevel);
                defaultConfig.CurrentLevel = defaultLevel;

                renderer.Log("[CONFIG] Using default configuration");
                return defaultConfig;
            }
        }

        private static PlayerConfig ParsePlayerConfig(string json)
        {
            var player = new PlayerConfig();

            var nameValue = ExtractJsonValue(json, "Name");
            if (nameValue != null) player.Name = nameValue;

            var typeValue = ExtractJsonValue(json, "Type");
            if (typeValue != null) player.Type = typeValue;

            player.AttackRange = ParseFloatValue(json, "AttackRange");
            player.AttackInterval = ParseFloatValue(json, "AttackInterval");
            player.AttackDamage = ParseFloatValue(json, "AttackDamage");
            player.CurrentLevel = ParseIntValue(json, "CurrentLevel");
            player.UpgradeThreshold = ParseFloatValue(json, "UpgradeThreshold");

            return player;
        }

        private static System.Collections.Generic.List<MonsterConfig> ParseMonsterTypes(string jsonArray)
        {
            var monsters = new System.Collections.Generic.List<MonsterConfig>();

            int pos = 0;
            while (pos < jsonArray.Length)
            {
                while (pos < jsonArray.Length && (char.IsWhiteSpace(jsonArray[pos]) || jsonArray[pos] == ',')) pos++;

                if (jsonArray[pos] == '{')
                {
                    pos++;
                    int objEnd = FindMatchingBrace(jsonArray, pos, '{', '}');
                    string monsterJson = jsonArray.Substring(pos, objEnd - pos);
                    pos = objEnd;

                    monsters.Add(ParseMonsterConfig(monsterJson));
                }

                pos++;
            }

            return monsters;
        }

        private static MonsterConfig ParseMonsterConfig(string json)
        {
            var monster = new MonsterConfig();

            monster.Name = ExtractJsonValue(json, "Name");
            monster.Type = ExtractJsonValue(json, "Type");
            monster.Health = ParseFloatValue(json, "Health");
            monster.MaxHealth = monster.Health;
            monster.Damage = ParseFloatValue(json, "Damage");
            monster.MoveSpeed = ParseFloatValue(json, "MoveSpeed");
            monster.AttackRange = ParseFloatValue(json, "AttackRange");
            monster.AttackInterval = ParseFloatValue(json, "AttackInterval");
            monster.GoldReward = ParseIntValue(json, "GoldReward");
            monster.Skills = ParseStringArray(json, "Skills");

            return monster;
        }

        private static System.Collections.Generic.List<LevelConfig> ParseLevels(string jsonArray)
        {
            var levels = new System.Collections.Generic.List<LevelConfig>();

            int pos = 0;
            while (pos < jsonArray.Length)
            {
                while (pos < jsonArray.Length && (char.IsWhiteSpace(jsonArray[pos]) || jsonArray[pos] == ',')) pos++;

                if (jsonArray[pos] == '{')
                {
                    pos++;
                    int objEnd = FindMatchingBrace(jsonArray, pos, '{', '}');
                    string levelJson = jsonArray.Substring(pos, objEnd - pos);
                    pos = objEnd;

                    levels.Add(ParseLevelConfig(levelJson));
                }

                pos++;
            }

            return levels;
        }

        private static LevelConfig ParseLevelConfig(string json)
        {
            var level = new LevelConfig();

            level.LevelNumber = ParseIntValue(json, "LevelNumber");
            level.WaveCount = ParseIntValue(json, "WaveCount");
            level.Waves = ParseWaveArray(json, "Waves");

            return level;
        }

        private static System.Collections.Generic.List<WaveConfig> ParseWaveArray(string json, string key)
        {
            var waves = new System.Collections.Generic.List<WaveConfig>();

            int arrayStart = json.IndexOf("\"" + key + "\" : [");
            if (arrayStart == -1) return waves;

            int pos = arrayStart + key.Length + 4;
            int arrayEnd = FindMatchingBrace(json, pos, '[', ']');

            if (arrayEnd == -1) return waves;

            var arrayContent = json.Substring(pos, arrayEnd - pos);
            pos = 0;

            while (pos < arrayContent.Length)
            {
                while (pos < arrayContent.Length && (char.IsWhiteSpace(arrayContent[pos]) || arrayContent[pos] == ',')) pos++;

                if (arrayContent[pos] == '{')
                {
                    pos++;
                    int objEnd = FindMatchingBrace(arrayContent, pos, '{', '}');
                    string waveJson = arrayContent.Substring(pos, objEnd - pos);
                    pos = objEnd;

                    waves.Add(ParseWaveConfig(waveJson));
                }

                pos++;
            }

            return waves;
        }

        private static WaveConfig ParseWaveConfig(string json)
        {
            var wave = new WaveConfig();

            wave.WaveNumber = ParseIntValue(json, "WaveNumber");
            wave.MonsterType = ExtractJsonValue(json, "MonsterType");
            wave.EnemyCount = ParseIntValue(json, "EnemyCount");

            return wave;
        }

        private static System.Collections.Generic.List<string> ParseStringArray(string json, string key)
        {
            var items = new System.Collections.Generic.List<string>();

            int arrayStart = json.IndexOf("\"" + key + "\" : [");
            if (arrayStart == -1) return items;

            int pos = arrayStart + key.Length + 4;
            int arrayEnd = FindMatchingBrace(json, pos, '[', ']');

            if (arrayEnd == -1) return items;

            var arrayContent = json.Substring(pos, arrayEnd - pos);
            pos = 0;

            while (pos < arrayContent.Length)
            {
                while (pos < arrayContent.Length && (char.IsWhiteSpace(arrayContent[pos]) || arrayContent[pos] == ',')) pos++;

                if (arrayContent[pos] == '"')
                {
                    pos++;
                    int endQuote = arrayContent.IndexOf('"', pos);
                    if (endQuote == -1) break;

                    items.Add(arrayContent.Substring(pos, endQuote - pos));
                    pos = endQuote + 1;
                }

                pos++;
            }

            return items;
        }

        private static string ExtractJsonObject(string json, ref int pos)
        {
            var start = pos;
            var end = FindMatchingBrace(json, pos, '{', '}');
            if (end == -1) return "";

            pos = end;
            return json.Substring(start, end - start);
        }

        private static string ExtractJsonArray(string json, ref int pos)
        {
            var start = pos;
            var end = FindMatchingBrace(json, pos, '[', ']');
            if (end == -1) return "";

            pos = end;
            return json.Substring(start, end - start);
        }

        private static string ExtractJsonValue(string json, string key)
        {
            var keyPattern = "\"" + key + "\" :";
            var keyIndex = json.IndexOf(keyPattern);
            if (keyIndex == -1) return null;

            var pos = keyIndex + keyPattern.Length;
            while (pos < json.Length && char.IsWhiteSpace(json[pos])) pos++;

            if (pos >= json.Length) return null;

            if (json[pos] == '"')
            {
                pos++;
                int endQuote = json.IndexOf('"', pos);
                if (endQuote == -1) return null;
                return json.Substring(pos, endQuote - pos);
            }
            else if (json[pos] == '{' || json[pos] == '[')
            {
                return json.Substring(pos, FindMatchingBrace(json, pos, '{', '}') - pos);
            }

            return null;
        }

        private static float ParseFloatValue(string json, string key)
        {
            string value = ExtractJsonValue(json, key);
            if (value == null) return 0f;

            float result;
            if (float.TryParse(value, out result))
            {
                return result;
            }
            return 0f;
        }

        private static int ParseIntValue(string json, string key)
        {
            string value = ExtractJsonValue(json, key);
            if (value == null) return 0;

            int result;
            if (int.TryParse(value, out result))
            {
                return result;
            }
            return 0;
        }

        private static int FindMatchingBrace(string str, int startPos, char open, char close)
        {
            int count = 1;
            for (int i = startPos + 1; i < str.Length; i++)
            {
                if (str[i] == open)
                {
                    count++;
                }
                else if (str[i] == close)
                {
                    count--;
                    if (count == 0)
                    {
                        return i + 1;
                    }
                }
            }

            return -1;
        }
    }
}
