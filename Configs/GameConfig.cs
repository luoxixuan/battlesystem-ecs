using System.Collections.Generic;

namespace BattleSystemECS.Config
{
    public class LevelConfig
    {
        public int LevelNumber { get; set; }
        public int WaveCount { get; set; }
        public List<WaveConfig> Waves { get; set; } = new List<WaveConfig>();
    }

    public class WaveConfig
    {
        public int WaveNumber { get; set; }
        public string MonsterType { get; set; }
        public int EnemyCount { get; set; }
    }

    public class MonsterConfig
    {
        public string Name { get; set; }
        public string Type { get; set; }

        public float Health { get; set; }
        public float MaxHealth { get; set; }
        public float Damage { get; set; }

        public float MoveSpeed { get; set; }
        public float AttackRange { get; set; }
        public float AttackInterval { get; set; }

        public int GoldReward { get; set; }
        public List<string> Skills { get; set; } = new List<string>();
    }

    public class PlayerConfig
    {
        public string Name { get; set; }
        public string Type { get; set; }

        public float AttackRange { get; set; }
        public float AttackInterval { get; set; }
        public float AttackDamage { get; set; }

        public int CurrentLevel { get; set; }
        public float UpgradeThreshold { get; set; }
    }

    public class GameConfig
    {
        public LevelConfig CurrentLevel { get; set; }
        public List<LevelConfig> Levels { get; set; } = new List<LevelConfig>();
        public List<MonsterConfig> MonsterTypes { get; set; } = new List<MonsterConfig>();
        public PlayerConfig Player { get; set; } = new PlayerConfig();

        public GameConfig()
        {
            InitializeDefaultConfig();
        }

        private void InitializeDefaultConfig()
        {
            MonsterTypes.Add(new MonsterConfig
            {
                Name = "Normal Slime",
                Type = "Normal",
                Health = 20f,
                Damage = 5f,
                MoveSpeed = 1f,
                AttackRange = 1f,
                AttackInterval = 1.5f,
                GoldReward = 10,
                Skills = new List<string> { "Normal Attack" }
            });

            Levels.Add(new LevelConfig
            {
                LevelNumber = 1,
                WaveCount = 3,
                Waves = new List<WaveConfig>
                {
                    new WaveConfig { WaveNumber = 1, MonsterType = "Normal", EnemyCount = 5 },
                    new WaveConfig { WaveNumber = 2, MonsterType = "Normal", EnemyCount = 5 },
                    new WaveConfig { WaveNumber = 3, MonsterType = "Normal", EnemyCount = 5 }
                }
            });

            Player = new PlayerConfig
            {
                Name = "Player",
                Type = "Tower",
                AttackRange = 3f,
                AttackInterval = 1f,
                AttackDamage = 10f,
                CurrentLevel = 1,
                UpgradeThreshold = 100f
            };

            CurrentLevel = Levels[0];
        }

        public MonsterConfig GetMonsterConfig(string type)
        {
            return MonsterTypes.Find(m => m.Type == type);
        }

        public LevelConfig GetLevelConfig(int levelNumber)
        {
            return Levels.Find(l => l.LevelNumber == levelNumber);
        }
    }
}
