using System;
using System.Collections.Generic;
using System.IO;
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
        public int StartingLives { get; set; } = 10;  // 初始基地生命数（漏怪次数上限）
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
        // MagicResist: reduces incoming Magic damage (0.0-1.0 fraction reduction).
        // Separate from armor. Physical ignores magic resist, Magic ignores armor.
        public float MagicResist { get; set; } = 0f;
        // Shield: absorbs incoming damage before health. Boss/Elite types can have shield.
        public float Shield { get; set; } = 0f;
        // Boss: true if this monster type is a boss (participates in phase/enrage system).
        public bool IsBoss { get; set; } = false;
        // Phases: ordered list of boss phase definitions (by threshold, descending).
        // Example: [{\"threshold\": 0.75, \"abilityId\": \"phase2_buff\"}, {\"threshold\": 0.50, \"abilityId\": \"enrage\"}]
        public List<BossPhaseDef> Phases { get; set; } = new List<BossPhaseDef>();
        // Enrage: enrage configuration (timer-based). Null = no enrage.
        public BossEnrageConfig Enrage { get; set; }
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
        // Targeting mode: which enemy the tower prefers to attack
        // 0=Nearest, 1=Furthest, 2=LowestHealth, 3=HighestHealth, 4=FirstSpawned, 5=LastSpawned
        public int TargetingMode { get; set; } = 0;
        // Tower special ability fields (null = no special ability)
        public TowerSpecialAbility SpecialAbility { get; set; }
        // Tower upgrade path: "standard" (default), "fast", or "tank"
        // Determines which upgrade curve the tower follows.
        // When null/empty, defaults to "standard".
        public string UpgradePath { get; set; }
        // Ammo system: 0 = unlimited ammo (no reload needed)
        public int MaxAmmo { get; set; } = 0;
        // Reload time in seconds (0 = instant/unlimited)
        public float ReloadTime { get; set; } = 0f;
        // Homing projectile: if true, projectile tracks target and turns mid-flight
        public bool ProjectileHoming { get; set; } = false;
        // Turn rate: maximum angular change per second in radians (e.g. PI = 180°/sec, 0 = instant/snap to target)
        // Default 0 means instant rotation (existing behavior unchanged)
        public float TurnRate { get; set; } = 0f;
        // DamageType: 0=Physical (reduced by armor), 1=Magic (reduced by magic resist), 2=True (ignores all defenses)
        public int DamageType { get; set; } = 0;
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
        /// <summary>AOE falloff inner radius ratio (0.5 = inner 50% at full damage, default 1.0)</summary>
        public float FalloffInnerRatio { get; set; } = 1.0f;
        /// <summary>AOE falloff outer damage multiplier (0.5 = outer 50% damage, default 1.0)</summary>
        public float FalloffOuterMult { get; set; } = 1.0f;
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
        // summon_minion ability fields
        public float MinionHealthMult { get; set; } // health multiplier for summoned minion
        public float MinionDamageMult { get; set; } // damage multiplier for summoned minion
    }

    /// <summary>
    /// Boss phase definition — loaded from monster JSON (phases[] field).
    /// Each phase specifies a health threshold and the ability to trigger on phase entry.
    /// </summary>
    public class BossPhaseDef
    {
        // Health fraction (0-1) at which this phase activates. E.g., 0.5 = 50% max HP.
        public float Threshold { get; set; }
        // Ability ID to trigger when phase activates (e.g., "boss_summon", "boss_enrage").
        // Empty = no ability triggered on phase entry.
        public string AbilityId { get; set; }
        // Speed multiplier applied when this phase activates (1.0 = no change, 1.5 = +50%).
        public float SpeedMult { get; set; } = 1.0f;
        // Damage multiplier applied when this phase activates (1.0 = no change, 1.25 = +25%).
        public float DamageMult { get; set; } = 1.0f;
        // New behavior tree subtree to use during this phase.
        // Empty = continue using current BT.
        public string NewBehaviorTree { get; set; }
    }

    /// <summary>
    /// Boss enrage configuration — loaded from monster JSON (enrage{} field).
    /// </summary>
    public class BossEnrageConfig
    {
        // Time in seconds after spawn before enrage activates (0 = no timer-based enrage).
        public float EnrageAfterSeconds { get; set; }
        // Speed multiplier when enrage activates (1.0 = no change).
        public float SpeedMult { get; set; } = 1.0f;
        // Damage multiplier when enrage activates (1.0 = no change).
        public float DamageMult { get; set; } = 1.0f;
    }

    /// <summary>
    /// Wave mutator definition — loaded from wave_mutators.json.
    /// A mutator applies a global modifier to all enemies during a specific wave.
    /// </summary>
    public class WaveMutatorDef
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        // "speed_mult" | "regen" | "explosive_death" | "dense_spawn"
        public string EffectType { get; set; }
        // For speed_mult
        public float SpeedMult { get; set; } = 1.0f;
        // For regen
        public float RegenRate { get; set; } = 0f;
        // For explosive_death
        public float ExplosionDamageRatio { get; set; } = 0f;
        public float ExplosionRadius { get; set; } = 0f;
        // For dense_spawn
        public int SpawnBatchSize { get; set; } = 5;
        // Wave number (1-indexed) at which this mutator activates
        public int TriggerWaveStart { get; set; } = 0;
    }

    /// <summary>
    /// Defines a placeable obstacle type (wooden barricade, ice wall, spike trap).
    /// </summary>
    public class ObstacleDef
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        // Gold cost to place
        public float Cost { get; set; }
        // Max health of the obstacle
        public float MaxHealth { get; set; }
        // Whether enemies attack this obstacle when in range
        public bool CanBeAttacked { get; set; } = true;
        // Damage dealt to enemies when they walk over (spike trap)
        public float TrapDamage { get; set; } = 0f;
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
        // Freeze fields (Cold Nova)
        public float FreezeDuration { get; set; }
        public float FreezeChance { get; set; }
        /// <summary>Cone angle in degrees for AreaShape="cone". Controls fan spread. Default: 60.</summary>
        public float ConeAngleDegrees { get; set; } = 60.0f;
        // Slow fields (Slow Nova — move speed reduction AoE)
        public float SlowAmount { get; set; }
        public float SlowDuration { get; set; }
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

    /// <summary>
    /// Combo Kill system configuration — controls combo window, damage/gold scaling, and cap.
    /// </summary>
    public class ComboConfig
    {
        /// <summary>Seconds since last kill before combo resets to 0. Default: 3.0f</summary>
        public float ComboWindowSeconds { get; set; } = 3.0f;
        /// <summary>Damage bonus per combo kill. 0.05f = +5% per kill. Default: 0.05f</summary>
        public float ComboDamageBonusPerKill { get; set; } = 0.05f;
        /// <summary>Gold bonus per combo kill. 0.10f = +10% per kill. Default: 0.10f</summary>
        public float ComboGoldBonusPerKill { get; set; } = 0.10f;
        /// <summary>Maximum combo damage multiplier. Default: 3.0f (reached at 40 kills)</summary>
        public float ComboMaxMultiplier { get; set; } = 3.0f;
    }

    /// <summary>
    /// Auto Skill System configuration — controls which strategy is used to auto-cast skills
    /// during BuildPhase, how many skills can fire per phase, and cooldown protection.
    /// </summary>
    public class AutoSkillConfig
    {
        /// <summary>Enable auto skill casting during BuildPhase. Default: true</summary>
        public bool Enabled { get; set; } = true;
        /// <summary>Maximum number of skills to cast per BuildPhase tick. Default: 2</summary>
        public int MaxSkillsPerPhase { get; set; } = 2;
        /// <summary>Minimum cooldown (seconds) a skill must have to be considered for auto-cast. Default: 0s</summary>
        public float MinCooldownToConsider { get; set; } = 0f;
        /// <summary>Selection strategy for choosing which ready skill to cast. Default: CoolestFirst</summary>
        public AutoSkillStrategy SelectionStrategy { get; set; } = AutoSkillStrategy.CoolestFirst;
    }

    /// <summary>
    /// Tower Mastery Level definition — XP threshold and bonuses granted at each level.
    /// </summary>
    public class TowerMasteryLevelConfig
    {
        /// <summary>Mastery level (1 = fresh, 2 = 2nd tier, etc.)</summary>
        public int Level { get; set; }
        /// <summary>Total XP required to reach this level (cumulative threshold)</summary>
        public float XPThreshold { get; set; }
        /// <summary>Damage multiplier bonus granted at this level (e.g. 0.1 = +10% damage)</summary>
        public float DamageBonus { get; set; }
        /// <summary>Attack speed multiplier bonus granted at this level (e.g. 0.05 = +5% attack speed)</summary>
        public float AttackSpeedBonus { get; set; }
        /// <summary>Range bonus granted at this level (flat tiles, e.g. 1 = +1 tile range)</summary>
        public int RangeBonus { get; set; }
    }

    /// <summary>
    /// Tower Mastery System configuration — level progression table and XP sources.
    /// </summary>
    public class TowerMasteryConfig
    {
        /// <summary>XP granted per enemy kill (scaled by enemy gold reward multiplier). Default: 10</summary>
        public float XPPerKill { get; set; } = 10f;
        /// <summary>Elite kill bonus XP (flat add on top of XPPerKill). Default: 50</summary>
        public float XPPerEliteKill { get; set; } = 50f;
        /// <summary>Mastery level definitions sorted by level ascending.</summary>
        public List<TowerMasteryLevelConfig> Levels { get; set; } = new List<TowerMasteryLevelConfig>
        {
            new TowerMasteryLevelConfig { Level = 1, XPThreshold = 0f,    DamageBonus = 0f,    AttackSpeedBonus = 0f, RangeBonus = 0 },
            new TowerMasteryLevelConfig { Level = 2, XPThreshold = 100f,   DamageBonus = 0.05f, AttackSpeedBonus = 0.02f, RangeBonus = 0 },
            new TowerMasteryLevelConfig { Level = 3, XPThreshold = 300f,   DamageBonus = 0.10f, AttackSpeedBonus = 0.05f, RangeBonus = 1 },
            new TowerMasteryLevelConfig { Level = 4, XPThreshold = 600f,  DamageBonus = 0.15f, AttackSpeedBonus = 0.08f, RangeBonus = 1 },
            new TowerMasteryLevelConfig { Level = 5, XPThreshold = 1000f, DamageBonus = 0.20f, AttackSpeedBonus = 0.10f, RangeBonus = 2 },
            new TowerMasteryLevelConfig { Level = 6, XPThreshold = 1500f, DamageBonus = 0.25f, AttackSpeedBonus = 0.12f, RangeBonus = 2 },
            new TowerMasteryLevelConfig { Level = 7, XPThreshold = 2200f, DamageBonus = 0.30f, AttackSpeedBonus = 0.15f, RangeBonus = 3 },
            new TowerMasteryLevelConfig { Level = 8, XPThreshold = 3000f, DamageBonus = 0.35f, AttackSpeedBonus = 0.18f, RangeBonus = 3 },
        };
    }

    /// <summary>
    /// Auto-skill selection strategy when multiple skills are ready.
    /// </summary>
    public enum AutoSkillStrategy
    {
        /// <summary>Best score = large AoE radius / short cooldown. Default.</summary>
        CoolestFirst = 0,
        /// <summary>Shortest cooldown first (most frequent re-use).</summary>
        CooldownShortest = 1,
        /// <summary>Highest damage multiplier first.</summary>
        DamageHighest = 2,
        /// <summary>Largest AoE radius first.</summary>
        AoeLargest = 3,
        /// <summary>Random selection.</summary>
        Random = 4
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

        // Combo Kill configuration
        public ComboConfig Combo { get; set; } = new ComboConfig();

        // Auto Skill configuration (BuildPhase auto-casting)
        public AutoSkillConfig AutoSkill { get; set; } = new AutoSkillConfig();

        // Tower Mastery / XP system configuration
        public TowerMasteryConfig TowerMastery { get; set; } = new TowerMasteryConfig();

        // Wave mutator definitions (loaded from wave_mutators.json)
        public WaveMutatorDef[] WaveMutatorDefs { get; set; } = Array.Empty<WaveMutatorDef>();

        // Obstacle definitions (loaded from obstacles.json)
        public ObstacleDef[] ObstacleDefs { get; set; } = Array.Empty<ObstacleDef>();

        // Map dimensions (Bug#30 fix: magic numbers 10 and 20 in GameManager/EnemyMovementSystem)
        public int MapWidth { get; set; } = 10;
        public int MapHeight { get; set; } = 20;

        // Weather system configuration
        public WeatherConfig Weather { get; set; } = new WeatherConfig();

        // Terrain system configuration (direction 2)
        public List<TerrainTypeConfig> TerrainTypes { get; set; } = new List<TerrainTypeConfig>();
        public int[][] MapTerrainGrid { get; set; } = Array.Empty<int[]>();

        // Bank / Interest system configuration (direction 2)
        public BankConfig Bank { get; set; } = new BankConfig();
        // Mana/Energy pool system (direction 5)
        public ManaConfig Mana { get; set; } = new ManaConfig();

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

            // Load tower upgrade paths from JSON config (overrides C# defaults where specified)
            LoadTowerUpgradePathsFromJson();

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

        /// <summary>
        /// Loads tower upgrade path definitions from an external JSON file.
        /// Supported file: Data/Configs/tower_upgrade_paths.json
        /// Overrides only the path IDs present in the file; C# defaults remain for all others.
        /// If the file does not exist, C# defaults are used unchanged (safe fallback).
        /// </summary>
        private void LoadTowerUpgradePathsFromJson()
        {
            string basePath = AppDomain.CurrentDomain.BaseDirectory;
            string jsonPath = Path.Combine(basePath, "Data", "Configs", "tower_upgrade_paths.json");
            if (!File.Exists(jsonPath)) return;

            try
            {
                string json = File.ReadAllText(jsonPath);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("paths", out var pathsElement) && pathsElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var pathElem in pathsElement.EnumerateArray())
                    {
                        string pathId = pathElem.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                        if (string.IsNullOrEmpty(pathId)) continue;

                        var cfg = new TowerUpgradePathConfig { Id = pathId };
                        if (pathElem.TryGetProperty("description", out var descProp))
                            cfg.Description = descProp.GetString();

                        cfg.Levels = new Dictionary<int, TowerUpgradeLevelConfig>();
                        if (pathElem.TryGetProperty("levels", out var levelsElem) && levelsElem.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var levelElem in levelsElem.EnumerateArray())
                            {
                                int level = levelElem.TryGetProperty("level", out var lvProp) ? lvProp.GetInt32() : 0;
                                if (level <= 0) continue;

                                var lc = new TowerUpgradeLevelConfig();
                                if (levelElem.TryGetProperty("damageMultiplier", out var dm)) lc.DamageMultiplier = dm.GetSingle();
                                if (levelElem.TryGetProperty("rangeAdd", out var ra)) lc.RangeAdd = ra.GetSingle();
                                if (levelElem.TryGetProperty("attackSpeedMultiplier", out var asm)) lc.AttackSpeedMultiplier = asm.GetSingle();
                                if (levelElem.TryGetProperty("costMultiplier", out var cm)) lc.CostMultiplier = cm.GetSingle();
                                if (levelElem.TryGetProperty("specialAbility", out var sa))
                                {
                                    lc.SpecialAbility = ParseUpgradeAbility(sa.GetString());
                                    if (levelElem.TryGetProperty("specialAbilityParam", out var sap))
                                        lc.SpecialAbilityParam = sap.GetSingle();
                                }
                                cfg.Levels[level] = lc;
                            }
                        }

                        // Merge: only override/add levels in the JSON; keep existing levels not in JSON
                        if (!TowerUpgradePaths.TryGetValue(pathId, out var existing))
                            TowerUpgradePaths[pathId] = cfg;
                        else
                            foreach (var kvp in cfg.Levels)
                                existing.Levels[kvp.Key] = kvp.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GameConfig] Warning: failed to load tower_upgrade_paths.json: {ex.Message}");
            }
        }

        private static TowerUpgradeAbility ParseUpgradeAbility(string s)
        {
            return s?.ToLowerInvariant() switch
            {
                "splashdamage" or "splash" => TowerUpgradeAbility.SplashDamage,
                "chainlightning" or "chain" => TowerUpgradeAbility.ChainLightning,
                "freezepct" or "freeze" => TowerUpgradeAbility.FreezeAoe,
                "armorpierce" or "armor" => TowerUpgradeAbility.ArmorPierce,
                "criticalstrike" or "critical" or "crit" => TowerUpgradeAbility.CriticalStrike,
                _ => TowerUpgradeAbility.None,
            };
        }
    }

    /// <summary>
    /// Bank / Interest system configuration.
    /// </summary>
    public class BankConfig
    {
        // Base interest rate per wave (0.05f = 5% of banked gold)
        public float InterestRateBase { get; set; } = 0.05f;
        // Maximum interest rate cap (even with bonuses, rate cannot exceed this)
        public float InterestRateCap { get; set; } = 0.20f;
        // Maximum gold that can be stored in the bank
        public float BankGoldCap { get; set; } = 100000f;
    }

    /// <summary>
    /// Mana/Energy pool system configuration — defines mana regeneration and costs.
    /// </summary>
    public class ManaConfig
    {
        // Base mana points at game start
        public float BaseMana { get; set; } = 100f;
        // Maximum mana cap
        public float MaxManaBase { get; set; } = 100f;
        // Mana regeneration per second (in active combat)
        public float ManaRegenPerSec { get; set; } = 5f;
        // Mana regen in BuildPhase (typically higher for preparation)
        public float ManaRegenBuildPhase { get; set; } = 10f;
        // Multiplier on all mana costs (buff/debuff from tech tree)
        public float ManaCostMultiplier { get; set; } = 1f;
    }

    /// <summary>
    /// Weather system configuration — defines weather types and their effects.
    /// </summary>
    public class WeatherConfig
    {
        // Weather type IDs (used as array indices)
        public const int Clear = 0;
        public const int Rain = 1;
        public const int Fog = 2;
        public const int Storm = 3;

        // Global config
        public float GlobalEnemySpeedMult { get; set; } = 1.0f;
        public float GlobalTowerRangeMult { get; set; } = 1.0f;
        public float GlobalTowerDamageMult { get; set; } = 1.0f;
        // Per-type overrides
        public Dictionary<string, WeatherTypeConfig> Types { get; set; } = new();
    }

    public class WeatherTypeConfig
    {
        public string Name { get; set; } = "";
        // Multiplier applied to enemy move speed while this weather is active
        public float EnemySpeedMult { get; set; } = 1.0f;
        // Multiplier applied to tower attack range
        public float TowerRangeMult { get; set; } = 1.0f;
        // Multiplier applied to all tower damage
        public float TowerDamageMult { get; set; } = 1.0f;
        // Default duration in turns (-1 = permanent until changed)
        public float DefaultDuration { get; set; } = -1f;
        // Intensity range for random selection
        public float MinIntensity { get; set; } = 0.5f;
        public float MaxIntensity { get; set; } = 1.0f;
    }

    public class TerrainTypeConfig
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public float MoveSpeedMult { get; set; } = 1.0f;
        public float DotDamagePerTick { get; set; }
        public int DotDuration { get; set; }
        public float TowerRangeBonus { get; set; }
    }
}