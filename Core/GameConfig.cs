using System.Collections.Generic;

namespace BattleSystemECS.Config
{
    public class PlayerConfig
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public float AttackRange { get; set; }
        public float AttackSpeed { get; set; }
        public float AttackInterval { get; set; }
        public float AttackDamage { get; set; }
        public float MaxHealth { get; set; }
        public int CurrentLevel { get; set; }
        public float UpgradeThreshold { get; set; }
        public List<string> StartingSkills { get; set; } = new List<string>();
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

    public class TowerConfig
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public float Damage { get; set; }
        public int Range { get; set; }
        public float AttackSpeed { get; set; }
        public float Cost { get; set; }
        public float UpgradeCost { get; set; }
    }

    public class EnemyTypeEntry
    {
        public string MonsterType { get; set; }
        public int Count { get; set; } = 0;
    }

    public class WaveConfig
    {
        public int WaveNumber { get; set; }
        public string MonsterType { get; set; }
        public int EnemyCount { get; set; }
        // Multi-type support: if EnemyTypes is non-empty, use it instead of MonsterType
        public List<EnemyTypeEntry> EnemyTypes { get; set; } = new List<EnemyTypeEntry>();

        /// <summary>
        /// Returns how many enemies of a given monster type should spawn this wave.
        /// Uses EnemyTypes[] if populated, otherwise falls back to MonsterType + EnemyCount.
        /// </summary>
        public int GetEnemyCountForType(string monsterType)
        {
            if (EnemyTypes != null && EnemyTypes.Count > 0)
            {
                foreach (var entry in EnemyTypes)
                {
                    if (!string.IsNullOrEmpty(entry.MonsterType) && entry.MonsterType == monsterType)
                        return entry.Count;
                }
                return 0;
            }
            return !string.IsNullOrEmpty(MonsterType) ? EnemyCount : 0;
        }

        /// <summary>
        /// Returns all monster types configured for this wave, in order.
        /// </summary>
        public List<string> GetAllMonsterTypes()
        {
            if (EnemyTypes != null && EnemyTypes.Count > 0)
            {
                var result = new List<string>();
                foreach (var entry in EnemyTypes)
                {
                    if (!string.IsNullOrEmpty(entry.MonsterType) && entry.Count > 0)
                        result.Add(entry.MonsterType);
                }
                return result;
            }
            return !string.IsNullOrEmpty(MonsterType) ? new List<string> { MonsterType } : new List<string>();
        }

        /// <summary>
        /// Returns total enemy count for this wave.
        /// </summary>
        public int GetTotalEnemyCount()
        {
            if (EnemyTypes != null && EnemyTypes.Count > 0)
            {
                int total = 0;
                foreach (var entry in EnemyTypes)
                    total += entry.Count;
                return total;
            }
            return EnemyCount;
        }
    }

    public class LevelConfig
    {
        public int LevelNumber { get; set; }
        public int WaveCount { get; set; }
        public List<WaveConfig> Waves { get; set; } = new List<WaveConfig>();
    }

    /// <summary>
    /// Enemy ability definition — loaded from enemy_abilities.json.
    /// </summary>
    public class EnemyAbilityDef
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string AbilityType { get; set; } // "self_heal", "aoe_damage", "buff_allies"
        public float Cooldown { get; set; }
        public float CooldownRemaining { get; set; }
        public int AoeRadius { get; set; }
        public float DamageMultiplier { get; set; }
        public float HealAmount { get; set; }
        public string BuffStat { get; set; }
        public int BuffDuration { get; set; }
        public int StunDuration { get; set; }   // turns to stun (for stun_aoe abilities)
        public float SlowFactor { get; set; }   // speed multiplier for slow (0.5 = 50%)
        public int SlowDuration { get; set; }  // turns for slow
    }

    public class SkillConfig
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public float DamageMultiplier { get; set; }
        public int AreaWidth { get; set; }
        public int AreaHeight { get; set; }
        public int AttackRange { get; set; }
        public float Cooldown { get; set; }
        public bool AutoCast { get; set; }
        public string Hotkey { get; set; }
        // Area shape string maps to AreaShapeType via FromString()
        public string AreaShape { get; set; }
        // Effect radius (tiles). Box uses this as half-size → 3×3 box → AreaRadius=1
        public int AreaRadius { get; set; }
        // DoT fields (0 = no DoT)
        public float DotDuration { get; set; }
        public float DotTickInterval { get; set; }
        public float DotDamagePerTick { get; set; }
    }

    public class BehaviorTreeDef
    {
        public string MonsterType;
        public string RootId;
        public Dictionary<string, BTNodeDef> Nodes;
    }

    public class BTNodeDef
    {
        public string Id;
        public string Type;
        public string Action;
        public string Condition;
        public string Operator;
        public float Value;
        public float Param;
        public string[] Children;
        // Ability ID for enemy_cast_* action nodes
        public string AbilityId;
    }

    public class GameConfig
    {
        public PlayerConfig Player { get; set; } = new PlayerConfig();
        public List<SkillConfig> Skills { get; set; } = new List<SkillConfig>();
        public List<MonsterConfig> MonsterTypes { get; set; } = new List<MonsterConfig>();
        public List<TowerConfig> TowerTypes { get; set; } = new List<TowerConfig>();
        public List<LevelConfig> Levels { get; set; } = new List<LevelConfig>();
        public LevelConfig CurrentLevel { get; set; }

        // Behavior tree definitions keyed by monster type
        public Dictionary<string, BehaviorTreeDef> BehaviorTrees { get; set; } = new Dictionary<string, BehaviorTreeDef>();
        private Dictionary<string, BehaviorTreeDef> _btCache = new Dictionary<string, BehaviorTreeDef>();
        private Dictionary<string, BattleSystemECS.Systems.BTCachedTree> _cachedBtCache = new Dictionary<string, BattleSystemECS.Systems.BTCachedTree>();
        private Dictionary<string, MonsterConfig> _monsterCache = new Dictionary<string, MonsterConfig>();

        // Enemy abilities keyed by ability id
        public List<EnemyAbilityDef> EnemyAbilities { get; set; } = new List<EnemyAbilityDef>();

        // Buff definitions for UpgradeSystem (Bug#31 fix: was hardcoded strings)
        public List<string> UpgradeBuffs { get; set; } = new List<string> { "Attack+10%", "Crit Rate+5%", "Defense+10%" };

        // Map dimensions (Bug#30 fix: magic numbers 10 and 20 in GameManager/EnemyMovementSystem)
        public int MapWidth { get; set; } = 10;
        public int MapHeight { get; set; } = 20;

        public GameConfig()
        {
            InitializeDefaultConfig();
        }

        private void InitializeDefaultConfig()
        {
            // Default skills
            Skills.Add(new SkillConfig
            {
                Name = "Cross Slash",
                Description = "十字范围伤害 - 400% 伤害倍率，3x3 十字形范围",
                DamageMultiplier = 4f,
                AreaWidth = 3,
                AreaHeight = 3,
                AttackRange = 3,
                Cooldown = 5f,
                AutoCast = false,
                Hotkey = "1"
            });

            Skills.Add(new SkillConfig
            {
                Name = "Mega Explosion",
                Description = "3x3 范围伤害 - 400% 伤害倍率，9 格范围",
                DamageMultiplier = 4f,
                AreaWidth = 3,
                AreaHeight = 3,
                AttackRange = 5,
                Cooldown = 10f,
                AutoCast = false,
                Hotkey = "2"
            });

            Skills.Add(new SkillConfig
            {
                Name = "Sniper Shot",
                Description = "超远距离单体攻击 - 400% 伤害倍率，9 格攻击距离",
                DamageMultiplier = 4f,
                AreaWidth = 1,
                AreaHeight = 1,
                AttackRange = 9,
                Cooldown = 8f,
                AutoCast = false,
                Hotkey = "3"
            });

            // Default towers
            TowerTypes.Add(new TowerConfig
            {
                Name = "Basic Tower",
                Type = "Basic",
                Damage = 10f,
                Range = 3,
                AttackSpeed = 1f,
                Cost = 50f,
                UpgradeCost = 30f
            });

            TowerTypes.Add(new TowerConfig
            {
                Name = "Sniper Tower",
                Type = "Sniper",
                Damage = 25f,
                Range = 8,
                AttackSpeed = 0.5f,
                Cost = 100f,
                UpgradeCost = 60f
            });

            TowerTypes.Add(new TowerConfig
            {
                Name = "AOE Tower",
                Type = "AOE",
                Damage = 8f,
                Range = 2,
                AttackSpeed = 1.5f,
                Cost = 75f,
                UpgradeCost = 45f
            });

            // Default monsters
            MonsterTypes.Add(new MonsterConfig
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
            });

            MonsterTypes.Add(new MonsterConfig
            {
                Name = "Fast Slime",
                Type = "Fast",
                Health = 15f,
                MaxHealth = 15f,
                Damage = 3f,
                MoveSpeed = 2f,
                AttackRange = 1f,
                AttackInterval = 1f,
                GoldReward = 15,
                Skills = new List<string> { "Normal Attack", "Quick Dash" }
            });

            MonsterTypes.Add(new MonsterConfig
            {
                Name = "Strong Slime",
                Type = "Strong",
                Health = 30f,
                MaxHealth = 30f,
                Damage = 8f,
                MoveSpeed = 0.5f,
                AttackRange = 2f,
                AttackInterval = 2f,
                GoldReward = 20,
                Skills = new List<string> { "Normal Attack", "Heavy Strike" }
            });

            MonsterTypes.Add(new MonsterConfig
            {
                Name = "Ranged Slime",
                Type = "Ranged",
                Health = 15f,
                MaxHealth = 15f,
                Damage = 6f,
                MoveSpeed = 0.8f,
                AttackRange = 5f,
                AttackInterval = 1.2f,
                GoldReward = 25,
                Skills = new List<string> { "Normal Attack", "Ranged Shot" }
            });

            // Default levels
            var level1 = new LevelConfig { LevelNumber = 1, WaveCount = 3 };
            for (int i = 1; i <= 3; i++)
            {
                level1.Waves.Add(new WaveConfig { WaveNumber = i, MonsterType = "Normal", EnemyCount = 100 });
            }
            Levels.Add(level1);

            // Default player
            Player = new PlayerConfig
            {
                Name = "Player",
                Type = "Tower",
                AttackRange = 3f,
                AttackInterval = 1f,
                AttackDamage = 10f,
                MaxHealth = 200f,
                CurrentLevel = 1,
                UpgradeThreshold = 1000f,
                StartingSkills = new List<string> { "Cross Slash", "Mega Explosion", "Sniper Shot" }
            };

            // Default upgrade buffs (Bug#31 fix: moved from UpgradeSystem hardcoded strings)
            // Field initializer provides the canonical 3 buffs: Attack+10%, Crit Rate+5%, Defense+10%
            // These match the buff names consumed by PlayerTowerAttackSystem.cs

            if (Levels.Count > 0)
            {
                CurrentLevel = Levels[0];
            }
        }

        public MonsterConfig GetMonsterConfig(string type)
        {
            if (_monsterCache.TryGetValue(type, out var cached))
                return cached;
            var found = MonsterTypes.Find(m => m.Type == type);
            if (found != null)
                _monsterCache[type] = found;
            return found;
        }

        public LevelConfig GetLevelConfig(int levelNumber)
        {
            return Levels.Find(l => l.LevelNumber == levelNumber);
        }

        public SkillConfig GetSkillConfig(string skillName)
        {
            return Skills.Find(s => s.Name == skillName);
        }

        public TowerConfig GetTowerConfig(string type)
        {
            return TowerTypes.Find(t => t.Type == type);
        }

        public BehaviorTreeDef GetBehaviorTree(string monsterType)
        {
            if (string.IsNullOrEmpty(monsterType)) return null;
            if (_btCache.TryGetValue(monsterType, out var cached))
                return cached;
            if (BehaviorTrees.TryGetValue(monsterType, out var bt))
            {
                _btCache[monsterType] = bt;
                return bt;
            }
            return null;
        }

        /// <summary>
        /// Returns the pre-built O(1) cached behavior tree for this monster type.
        /// Builds the cache on first call; subsequent calls are O(1) dictionary hit.
        /// </summary>
        public BattleSystemECS.Systems.BTCachedTree GetCachedBehaviorTree(string monsterType)
        {
            if (string.IsNullOrEmpty(monsterType)) return null;
            if (_cachedBtCache.TryGetValue(monsterType, out var cached))
                return cached;
            // Bug#35 fix: query BehaviorTrees directly instead of via GetBehaviorTree()
            // to avoid the double dictionary lookup (BehaviorTrees.TryGetValue + _btCache.TryGetValue).
            // The _btCache still works as a side effect for GetBehaviorTree() callers.
            if (!BehaviorTrees.TryGetValue(monsterType, out var bt)) return null;
            var cachedBt = BattleSystemECS.Systems.BTCachedTreeBuilder.Build(bt);
            _cachedBtCache[monsterType] = cachedBt;
            return cachedBt;
        }

        /// <summary>
        /// Returns upgrade buff options (Bug#31 fix: was hardcoded in UpgradeSystem).
        /// </summary>
        public IReadOnlyList<string> GetUpgradeBuffs() => UpgradeBuffs;
    }
}