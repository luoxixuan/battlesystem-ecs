using System.Collections.Generic;
using System.Linq;

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
        // Armor: reduces incoming damage. Tank/Elite/Boss types get high armor (5-15),
        // Normal/Fast types get low armor (0-2). Affected by attacker's armor penetration.
        public float Armor { get; set; } = 0f;
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
        // Tower debuff fields (0 = no debuff)
        public float StunChance { get; set; } = 0f;   // probability per hit (0-1)
        public float SlowAmount { get; set; } = 0f;   // speed multiplier (e.g. 0.5 = 50% speed)
        public float SlowDuration { get; set; } = 0f; // duration in turns
        // Tower special ability fields (null = no special ability)
        public TowerSpecialAbility SpecialAbility { get; set; }
    }

    /// <summary>
    /// Tower special ability definition — allows towers to have active skills
    /// that are triggered manually (or auto) with area-of-effect effects.
    /// Mirrors the AreaShape pattern from SkillSystem for consistency.
    /// </summary>
    public class TowerSpecialAbility
    {
        /// <summary>Ability identifier, e.g. "aoe_burn", "freeze_stun", "chain_lightning"</summary>
        public string AbilityType { get; set; }
        /// <summary>Cooldown in seconds between activations</summary>
        public float Cooldown { get; set; } = 0f;
        /// <summary>Area shape: circle, box, cross, line, chain. Maps to AreaShapeType enum.</summary>
        public string AreaShape { get; set; }
        /// <summary>Radius in tiles for circle/chain shapes, or half-size for box shapes</summary>
        public int Radius { get; set; } = 0;
        /// <summary>Damage dealt by the ability (multiplied by tower damage)</summary>
        public float DamageMultiplier { get; set; } = 1f;
        /// <summary>Duration in seconds for effects like burn DoT</summary>
        public float Duration { get; set; } = 0f;
        /// <summary>DoT damage per tick (0 = no DoT)</summary>
        public float DotDamagePerTick { get; set; } = 0f;
        /// <summary>DoT tick interval in seconds</summary>
        public float DotTickInterval { get; set; } = 1f;
        /// <summary>Stun duration in turns (0 = no stun)</summary>
        public int StunDuration { get; set; } = 0;
        /// <summary>Slow factor (0.5 = 50% speed, 0 = no slow)</summary>
        public float SlowFactor { get; set; } = 0f;
        /// <summary>Slow duration in turns</summary>
        public int SlowDuration { get; set; } = 0;
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
        // Heal/Shield fields (0 = no heal/shield)
        public float HealPercent { get; set; }
        public float ShieldAmount { get; set; }
        public float ShieldDuration { get; set; }
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

    /// <summary>
    /// Special ability granted by a tower upgrade level.
    /// </summary>
    public enum TowerUpgradeAbility
    {
        None = 0,
        ArmorPierce,     // Ignore part of enemy armor
        SplashDamage,    // Deal damage to nearby enemies
        CriticalStrike,  // Chance to deal bonus damage
        ChainLightning,  // Chain to nearby enemies (uses existing Tesla logic)
        FreezeAoe        // Slow nearby enemies on hit
    }

    /// <summary>
    /// Per-level upgrade multipliers for a tower upgrade path.
    /// Keys are upgrade levels (1-based). Level 1 = first upgrade from base.
    /// </summary>
    public class TowerUpgradeLevelConfig
    {
        public float DamageMultiplier { get; set; } = 1.2f;
        public float RangeAdd { get; set; } = 1f;
        public float AttackSpeedMultiplier { get; set; } = 1.0f;
        public float CostMultiplier { get; set; } = 1.5f;
        /// <summary>Special ability granted by this upgrade level (e.g., "armor_pierce", "splash_damage").</summary>
        public TowerUpgradeAbility SpecialAbility { get; set; } = TowerUpgradeAbility.None;
        /// <summary>Parameter for special ability (e.g., armor pierce ratio, splash radius, crit chance).</summary>
        public float SpecialAbilityParam { get; set; } = 0f;
    }

    /// <summary>
    /// A named tower upgrade path (e.g., "standard", "fast", "tank").
    /// Maps upgrade levels to per-level multipliers.
    /// </summary>
    public class TowerUpgradePathConfig
    {
        public string Id { get; set; }
        public string Description { get; set; }
        /// <summary>Keys are level numbers (1, 2, 3, ...). If a level is missing, fall back to the highest defined level.</summary>
        public Dictionary<int, TowerUpgradeLevelConfig> Levels { get; set; } = new Dictionary<int, TowerUpgradeLevelConfig>();
    }

    // ── Phase Behavior Config ────────────────────────────────────────────────

    /// <summary>
    /// Per-phase behavior settings loaded from phase_behavior.json.
    /// </summary>
    public class PhaseBehaviorDef
    {
        public string Description { get; set; }
        public string EnterMessage { get; set; }
        public bool AutoAdvance { get; set; }
        public List<string> UnlockTowers { get; set; } = new List<string>();
        public List<string> UnlockAbilities { get; set; } = new List<string>();
        public int IntermissionDelayMs { get; set; }
        public string WaveStartMessage { get; set; }
        public int TurnIntervalMs { get; set; }
        public string NextWaveMessage { get; set; }
        public bool AutoAdvanceToBuild { get; set; }
        public int AdvanceDelayMs { get; set; }
        public bool ShowStats { get; set; }
    }

    public class GameConfig
    {
        public PlayerConfig Player { get; set; } = new PlayerConfig();
        public List<SkillConfig> Skills { get; set; } = new List<SkillConfig>();
        public List<MonsterConfig> MonsterTypes { get; set; } = new List<MonsterConfig>();
        public List<TowerConfig> TowerTypes { get; set; } = new List<TowerConfig>();
        public List<LevelConfig> Levels { get; set; } = new List<LevelConfig>();
        public LevelConfig CurrentLevel { get; set; }

        // Phase behavior keyed by GameState name
        public Dictionary<string, PhaseBehaviorDef> PhaseBehaviors { get; set; } = new Dictionary<string, PhaseBehaviorDef>();

        // Tower upgrade paths (config-driven upgrade curves)
        public Dictionary<string, TowerUpgradePathConfig> TowerUpgradePaths { get; set; } = new Dictionary<string, TowerUpgradePathConfig>();

        // Wave-based difficulty scaling
        public float DifficultyGrowthPerWave { get; set; } = 0.05f;
        public float PlayerDamageScalingPerWave { get; set; } = 0.05f;

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

            // Default towers — now with debuff fields so TowerPlacementSystem.GetTowerConfig() finds them
            TowerTypes.Add(new TowerConfig
            {
                Name = "Basic Tower",
                Type = "Basic",
                Damage = 10f,
                Range = 3,
                AttackSpeed = 1f,
                Cost = 50f,
                UpgradeCost = 30f,
                StunChance = 0.10f,   // 10% stun on hit
                SlowAmount = 0f,
                SlowDuration = 0f
            });

            TowerTypes.Add(new TowerConfig
            {
                Name = "Sniper Tower",
                Type = "Sniper",
                Damage = 25f,
                Range = 8,
                AttackSpeed = 0.5f,
                Cost = 100f,
                UpgradeCost = 60f,
                StunChance = 0.05f,   // 5% stun — precision shot can stun briefly
                SlowAmount = 0f,
                SlowDuration = 0f
            });

            TowerTypes.Add(new TowerConfig
            {
                Name = "AOE Tower",
                Type = "AOE",
                Damage = 8f,
                Range = 2,
                AttackSpeed = 1.5f,
                Cost = 75f,
                UpgradeCost = 45f,
                StunChance = 0f,
                SlowAmount = 0.30f,   // 30% slow on hit (area of effect)
                SlowDuration = 1f
            });

            // Frost Tower — dedicated cryo tower, applies heavy slow
            TowerTypes.Add(new TowerConfig
            {
                Name = "Frost Tower",
                Type = "Frost",
                Damage = 6f,
                Range = 3,
                AttackSpeed = 1.2f,
                Cost = 80f,
                UpgradeCost = 48f,
                StunChance = 0f,
                SlowAmount = 0.50f,   // 50% slow on hit
                SlowDuration = 2f
            });

            // Stun Tower — dedicated stun tower, high stun chance
            TowerTypes.Add(new TowerConfig
            {
                Name = "Stun Tower",
                Type = "Stun",
                Damage = 8f,
                Range = 3,
                AttackSpeed = 0.8f,
                Cost = 90f,
                UpgradeCost = 54f,
                StunChance = 0.35f,   // 35% stun on hit
                SlowAmount = 0f,
                SlowDuration = 0f
            });

            // EMP Tower — silence/disable tower (future: enemy ability suppression)
            TowerTypes.Add(new TowerConfig
            {
                Name = "EMP Tower",
                Type = "EMP",
                Damage = 10f,
                Range = 4,
                AttackSpeed = 0.6f,
                Cost = 100f,
                UpgradeCost = 60f,
                StunChance = 0.15f,   // 15% stun on hit
                SlowAmount = 0.20f,   // 20% slow
                SlowDuration = 1f
            });

            // Tesla Tower — chain lightning tower with built-in SpecialAbility
            TowerTypes.Add(new TowerConfig
            {
                Name = "Tesla Coil",
                Type = "Tesla",
                Damage = 8f,
                Range = 4,
                AttackSpeed = 1.5f,
                Cost = 70f,
                UpgradeCost = 40f,
                StunChance = 0f,
                SlowAmount = 0f,
                SlowDuration = 0f,
                SpecialAbility = new TowerSpecialAbility
                {
                    AbilityType = "chain_lightning",
                    Radius = 3
                }
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

            // Default tower upgrade paths (replaces hardcoded +20%/+1/+1.5x in TowerUpgradeSystem)
            // "standard" — matches the original hardcoded curve
            // Note: undefined levels fall back to the highest defined level
            TowerUpgradePaths["standard"] = new TowerUpgradePathConfig
            {
                Id = "standard",
                Description = "Standard upgrade path: +20% damage, +1 range, +1.5x cost per level",
                Levels = new Dictionary<int, TowerUpgradeLevelConfig>
                {
                    { 1, new TowerUpgradeLevelConfig { DamageMultiplier = 1.2f, RangeAdd = 1f, AttackSpeedMultiplier = 1.0f, CostMultiplier = 1.5f } },
                    { 2, new TowerUpgradeLevelConfig { DamageMultiplier = 1.2f, RangeAdd = 0f, AttackSpeedMultiplier = 1.0f, CostMultiplier = 1.5f, SpecialAbility = TowerUpgradeAbility.SplashDamage, SpecialAbilityParam = 1f } },
                    { 3, new TowerUpgradeLevelConfig { DamageMultiplier = 1.2f, RangeAdd = 0f, AttackSpeedMultiplier = 1.0f, CostMultiplier = 1.5f, SpecialAbility = TowerUpgradeAbility.ChainLightning, SpecialAbilityParam = 0f } },
                    { 4, new TowerUpgradeLevelConfig { DamageMultiplier = 1.2f, RangeAdd = 0f, AttackSpeedMultiplier = 1.0f, CostMultiplier = 1.5f, SpecialAbility = TowerUpgradeAbility.FreezeAoe, SpecialAbilityParam = 0f } },
                }
            };

            // "fast" — prioritizes attack speed, minimal range growth (suitable for Weapon/Fast towers)
            TowerUpgradePaths["fast"] = new TowerUpgradePathConfig
            {
                Id = "fast",
                Description = "Fast upgrade path: +15% damage, +0.5 range, +25% attack speed, +1.6x cost",
                Levels = new Dictionary<int, TowerUpgradeLevelConfig>
                {
                    { 1, new TowerUpgradeLevelConfig { DamageMultiplier = 1.15f, RangeAdd = 0.5f, AttackSpeedMultiplier = 1.25f, CostMultiplier = 1.6f } },
                    { 2, new TowerUpgradeLevelConfig { DamageMultiplier = 1.15f, RangeAdd = 0f, AttackSpeedMultiplier = 1.10f, CostMultiplier = 1.6f, SpecialAbility = TowerUpgradeAbility.CriticalStrike, SpecialAbilityParam = 0.25f } },
                    { 3, new TowerUpgradeLevelConfig { DamageMultiplier = 1.15f, RangeAdd = 0f, AttackSpeedMultiplier = 1.05f, CostMultiplier = 1.6f, SpecialAbility = TowerUpgradeAbility.SplashDamage, SpecialAbilityParam = 1f } },
                    { 4, new TowerUpgradeLevelConfig { DamageMultiplier = 1.15f, RangeAdd = 0f, AttackSpeedMultiplier = 1.05f, CostMultiplier = 1.6f, SpecialAbility = TowerUpgradeAbility.ChainLightning, SpecialAbilityParam = 0f } },
                }
            };

            // "tank" — prioritizes damage and range (suitable for Defense/Special towers)
            TowerUpgradePaths["tank"] = new TowerUpgradePathConfig
            {
                Id = "tank",
                Description = "Tank upgrade path: +30% damage, +2 range, +1.4x cost",
                Levels = new Dictionary<int, TowerUpgradeLevelConfig>
                {
                    { 1, new TowerUpgradeLevelConfig { DamageMultiplier = 1.3f, RangeAdd = 2f, AttackSpeedMultiplier = 1.0f, CostMultiplier = 1.4f } },
                    { 2, new TowerUpgradeLevelConfig { DamageMultiplier = 1.3f, RangeAdd = 0f, AttackSpeedMultiplier = 1.0f, CostMultiplier = 1.4f, SpecialAbility = TowerUpgradeAbility.ArmorPierce, SpecialAbilityParam = 0.5f } },
                    { 3, new TowerUpgradeLevelConfig { DamageMultiplier = 1.3f, RangeAdd = 0f, AttackSpeedMultiplier = 1.0f, CostMultiplier = 1.4f, SpecialAbility = TowerUpgradeAbility.CriticalStrike, SpecialAbilityParam = 0.35f } },
                    { 4, new TowerUpgradeLevelConfig { DamageMultiplier = 1.3f, RangeAdd = 0f, AttackSpeedMultiplier = 1.0f, CostMultiplier = 1.4f, SpecialAbility = TowerUpgradeAbility.FreezeAoe, SpecialAbilityParam = 0f } },
                }
            };

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

        /// <summary>
        /// Returns the upgrade path config for the given pathId, or null if not found.
        /// </summary>
        public TowerUpgradePathConfig GetUpgradePath(string pathId)
        {
            if (string.IsNullOrEmpty(pathId)) return null;
            TowerUpgradePaths.TryGetValue(pathId, out var path);
            return path;
        }

        /// <summary>
        /// Returns the per-level upgrade config for the given path and level.
        /// Falls back to the highest defined level if the exact level is not defined.
        /// Returns null if the path is not found.
        /// </summary>
        public TowerUpgradeLevelConfig GetUpgradeLevelConfig(string pathId, int level)
        {
            var path = GetUpgradePath(pathId);
            if (path == null || path.Levels == null || path.Levels.Count == 0) return null;

            if (path.Levels.TryGetValue(level, out var levelCfg))
                return levelCfg;

            // Fall back to the highest defined level
            int highestLevel = path.Levels.Keys.Max();
            return path.Levels[highestLevel];
        }

        /// <summary>
        /// Returns phase behavior settings for the given GameState name.
        /// Returns null if not configured.
        /// </summary>
        public PhaseBehaviorDef GetPhaseBehavior(string stateName)
        {
            if (string.IsNullOrEmpty(stateName)) return null;
            PhaseBehaviors.TryGetValue(stateName, out var def);
            return def;
        }
    }
}