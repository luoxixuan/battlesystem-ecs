using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Core
{
    /// <summary>
    /// SOA (Struct of Arrays) 组件存储
    /// 提供连续的内存布局，优化缓存命中率和支持 SIMD 指令
    /// 性能提升：10-100 倍
    /// </summary>
    public class ComponentStore
    {
        // 常量定义
        public const int MAX_ENTITIES = 100000;
        internal const int MAX_PLAYERS = 10;
        public int TotalKills = 0;

        // ==================== 位置组件的 SOA 存储 ====================
        public float[] PositionX = new float[MAX_ENTITIES];
        public float[] PositionY = new float[MAX_ENTITIES];
        public bool[] PositionActive = new bool[MAX_ENTITIES];

        // ==================== 玩家组件的 SOA 存储 ====================
        public float[] PlayerAttackRange = new float[MAX_PLAYERS];
        public float[] PlayerAttackSpeed = new float[MAX_PLAYERS];
        public float[] PlayerAttackDamage = new float[MAX_PLAYERS];
        public float[] PlayerMaxHealth = new float[MAX_PLAYERS];  // 玩家最大生命值
        public float[] PlayerCurrentHealth = new float[MAX_PLAYERS];  // 玩家当前生命值
        public float[] PlayerArmor = new float[MAX_PLAYERS];  // 玩家护甲：减少受到伤害
        // Player shield: absorbs damage before health, independent of armor
        public float[] PlayerShield = new float[MAX_PLAYERS];
        public float[] PlayerShieldDuration = new float[MAX_PLAYERS]; // seconds remaining
        // Player thorns: reflects a fraction of damage taken back to the attacking enemy.
        public float[] PlayerThornsRatio = new float[MAX_PLAYERS];
public int[] PlayerCurrentLevel = new int[MAX_PLAYERS];
        // Player damage type: 0=Physical, 1=Magic, 2=True. Drives which resistance enemies use.
        public int[] PlayerDamageType = new int[MAX_PLAYERS];
        public float[] PlayerGold = new float[MAX_PLAYERS];
        public float[] PlayerUpgradeThreshold = new float[MAX_PLAYERS];
        // ==================== 法力/能量池资源系统 (Mana Pool) ====================
        // PlayerMana: current mana points for each player
        public float[] PlayerMana = new float[MAX_PLAYERS];
        // PlayerMaxMana: maximum mana cap
        public float[] PlayerMaxMana = new float[MAX_PLAYERS];
        // PlayerManaRegen: mana regeneration rate per second
        public float[] PlayerManaRegen = new float[MAX_PLAYERS];
        // PlayerManaCost: cost multiplier for skill mana consumption
        public float[] PlayerManaCost = new float[MAX_PLAYERS];
        // ==================== 玩家全局技能/终极技能 (Global Skills / Ultimates) ====================
        // PlayerGlobalSkillUnlocked: bit-flag of which global skills are unlocked per player (indexed by playerId * MAX_GLOBAL_SKILLS + skillIdx)
        public bool[] PlayerGlobalSkillUnlocked = new bool[MAX_PLAYERS * 8];
        // PlayerGlobalSkillCooldown: remaining cooldown in seconds per global skill
        public float[] PlayerGlobalSkillCooldown = new float[MAX_PLAYERS * 8];
        // PlayerGlobalSkillPressed: hotkey pressed signal this frame (consumed by GlobalSkillSystem)
        public bool[] PlayerGlobalSkillPressed = new bool[MAX_PLAYERS];
        // PlayerGlobalSkillHotkey: hotkey string per skill for UI display
        public string[] PlayerGlobalSkillHotkey = new string[MAX_PLAYERS * 8];
        private float _goldKillMultiplier = 1.0f;
        public float GoldKillMultiplier { get => _goldKillMultiplier; set => _goldKillMultiplier = value; }
        // all_income_mult: extra multiplier layered on top of gold kill multiplier
        private float _allIncomeMultKill = 1.0f;
        public float AllIncomeMultKill { get => _allIncomeMultKill; set => _allIncomeMultKill = value; }
        // flat bonus awarded once per elite kill
        private float _goldOnEliteKill = 0f;
        public float GoldOnEliteKill { get => _goldOnEliteKill; set => _goldOnEliteKill = value; }
        public List<string>[] PlayerBuffs = new List<string>[MAX_PLAYERS];

        // Perf: bit-flag buff storage — O(1) lookup, no GC allocation per frame
        public BuffType[] PlayerBuffFlags = new BuffType[MAX_PLAYERS];
        // Player stun duration counter (turns remaining). 0 = not stunned.
        public int[] PlayerStunDuration = new int[MAX_PLAYERS];
        // Player slow: tracks remaining slow turns and factor
        public float[] PlayerSlowFactor = new float[MAX_PLAYERS];
        public int[] PlayerSlowDuration = new int[MAX_PLAYERS];
// Base lives: number of leaks allowed before game over (independent of health)
        public int[] PlayerBaseLives = new int[MAX_PLAYERS];
        public int[] PlayerMaxBaseLives = new int[MAX_PLAYERS];

        // ==================== 天气与环境效果组件 (SOA) ====================
        // WeatherType: current weather condition. 0=Clear, 1=Rain, 2=Fog, 3=Storm
        public int[] CurrentWeather = new int[MAX_PLAYERS];
        // WeatherIntensity: 0-1 strength of current weather effect (slows enemies, affects towers)
        public float[] WeatherIntensity = new float[MAX_PLAYERS];
        // WeatherTimer: turns remaining for current weather (-1 = permanent until changed)
        public float[] WeatherTimer = new float[MAX_PLAYERS];

        // ==================== 昼夜循环系统组件 (SOA) ====================
        // GlobalDayNightPhase: current phase. 0=Day, 1=Night. Same for all players in this simplified version.
        public int[] GlobalDayNightPhase = new int[MAX_PLAYERS];
        // GlobalDayNightTimer: remaining seconds in the current phase. Countdown from DayDuration or NightDuration.
        public float[] GlobalDayNightTimer = new float[MAX_PLAYERS];
        // GlobalDayNightCycleCount: how many Day→Night cycles have occurred (for difficulty scaling)
        public int[] GlobalDayNightCycleCount = new int[MAX_PLAYERS];

        // ==================== Objective System 组件 (Escort / Survival / Timed) ====================
        // CurrentObjectiveType: active objective type for this level (ObjectiveType enum, 0=KillAll default)
        public int[] CurrentObjectiveType = new int[MAX_PLAYERS];
        // Escort NPC: position and state (Survival objective)
        public float[] EscortNpcX = new float[MAX_PLAYERS];
        public float[] EscortNpcY = new float[MAX_PLAYERS];
        public float[] EscortNpcHealth = new float[MAX_PLAYERS];
        public float[] EscortNpcMaxHealth = new float[MAX_PLAYERS];
        public bool[] EscortNpcActive = new bool[MAX_PLAYERS];  // true when escort NPC is alive
        public float[] EscortNpcSpeed = new float[MAX_PLAYERS];  // movement speed in tiles/sec
        // Survival timer: remaining seconds in Timed mode, or remaining waves in Survival mode
        public float[] ObjectiveTimer = new float[MAX_PLAYERS];
        public int[] ObjectiveWavesRemaining = new int[MAX_PLAYERS];
        public float[] ObjectiveTimeLimit = new float[MAX_PLAYERS];  // seconds for Timed mode
        // Objective score: tracks performance (used for Endless mode scoring)
        public int[] ObjectiveWaveScore = new int[MAX_PLAYERS];
        public float[] ObjectiveHealthScore = new float[MAX_PLAYERS];  // remaining health at game end

        // ==================== Adaptive Difficulty System 组件（SOA） ====================
        // EnemiesLeakedThisWave: track leaks during current wave (used to compute difficulty adjustment)
        public int[] EnemiesLeakedThisWave = new int[MAX_PLAYERS];
        // AdaptiveDifficultyLevel: difficulty multiplier applied to enemy health/damage/speed (1.0 = normal, >1 = harder)
        public float[] AdaptiveDifficultyLevel = new float[MAX_PLAYERS];
        // AdaptiveDifficultyScore: cumulative performance score (higher = better player performance)
        public float[] AdaptiveDifficultyScore = new float[MAX_PLAYERS];

        // ==================== Resource Node System 组件（SOA） ====================
        // Fixed-size arrays for map resource nodes (gold mines, mana springs, etc.)
        public const int MAX_RESOURCE_NODES = 50;
        // Position
        public float[] ResourceNodeX = new float[MAX_RESOURCE_NODES];
        public float[] ResourceNodeY = new float[MAX_RESOURCE_NODES];
        // Ownership: -1 = neutral, 0 = player 0, etc.
        public int[] ResourceNodeOwner = new int[MAX_RESOURCE_NODES];
        // Node type: 0=GoldMine, 1=ManaSpring, 2=TechRelic
        public int[] ResourceNodeType = new int[MAX_RESOURCE_NODES];
        // Is the node active (not destroyed)?
        public bool[] ResourceNodeActive = new bool[MAX_RESOURCE_NODES];
        // Production rate: gold/sec for GoldMine, mana/sec for ManaSpring
        public float[] ResourceNodeProductionRate = new float[MAX_RESOURCE_NODES];
        // Remaining health (for destructible nodes)
        public float[] ResourceNodeHealth = new float[MAX_RESOURCE_NODES];
        public float[] ResourceNodeMaxHealth = new float[MAX_RESOURCE_NODES];
        // Accumulated resources (produced since last collection)
        public float[] ResourceNodeAccumulated = new float[MAX_RESOURCE_NODES];
        // Capture progress: -1 = full owner, 0..1 = being captured (higher = closer to owner)
        public float[] ResourceNodeCaptureProgress = new float[MAX_RESOURCE_NODES];
        // Active tower IDs on this node (0 = none)
        public int[] ResourceNodeTowerId = new int[MAX_RESOURCE_NODES];
        // Count of live nodes (maintained by ResourceNodeSystem)
        public int ActiveResourceNodeCount = 0;

        // ==================== 路径修改塔组件（SOA）====================
        // PathModifierX/Y: world position of the path modification influence point
        public float[] PathModifierX = new float[MAX_ENTITIES];
        public float[] PathModifierY = new float[MAX_ENTITIES];
        // PathModifierRadius: radius of influence — enemies within range have their path rerouted
        public float[] PathModifierRadius = new float[MAX_ENTITIES];
        // PathModifierActive: true if this path modifier is currently active
        public bool[] PathModifierActive = new bool[MAX_ENTITIES];
        // PathModifierOwnerId: player who placed this modifier (-1 = neutral/none)
        public int[] PathModifierOwnerId = new int[MAX_ENTITIES];
        // PathModifierTargetPathId: the path ID enemies should follow when inside the influence zone
        public int[] PathModifierTargetPathId = new int[MAX_ENTITIES];
        // PathModifierTurnsRemaining: countdown until the modifier expires (0 = permanent)
        public float[] PathModifierTurnsRemaining = new float[MAX_ENTITIES];
        // ActivePathModifierCount: number of active path modifiers in the store
        private int _activePathModifierCount = 0;
        public int ActivePathModifierCount => _activePathModifierCount;

        // ==================== Time Dilation / Bullet Time 组件（SOA） ====================
        // GlobalTimeScale: per-player time scale multiplier (1.0 = normal, 0.5 = 50% speed, 0.3 = bullet time)
        // Applied at the start of FrameScheduler.Tick() to slow/fast all game systems.
        public float[] GlobalTimeScale = new float[MAX_PLAYERS];
        // GlobalTimeScaleDuration: remaining turns for the current time scale effect. 0 = inactive.
        public float[] GlobalTimeScaleDuration = new float[MAX_PLAYERS];

        // ==================== 随机事件/中期惊喜组件（Random Mid-Wave Events, SOA）====================
        // RandomEventCooldown: cooldown in turns until next event can trigger (-1 = no event pending, 0 = ready)
        public float[] RandomEventCooldown = new float[MAX_PLAYERS];
        // RandomEventActiveType: currently active event type (0=None, 1=Ambush, 2=SupplyDrop, 3=Earthquake, 4=BossRush, 5=Merchant)
        public int[] RandomEventActiveType = new int[MAX_PLAYERS];
        // RandomEventTimer: countdown for the current event in turns (0 = immediate/one-shot event)
        public float[] RandomEventTimer = new float[MAX_PLAYERS];

        // ==================== 战争迷雾 / 视野系统组件 (Fog of War, SOA) ====================
        // TowerVisionRadius: vision radius in grid units for each tower (0 = can see all enemies, no fog)
        // Default 0 means no fog restriction (backward compatible)
        public float[] TowerVisionRadius = new float[MAX_ENTITIES];
        // GlobalFogDensity: global fog density multiplier applied to all tower vision radii (1.0 = normal, <1.0 = reduced visibility)
        // WeatherSystem / DayNightSystem can modify this to simulate fog/night effects
        public float[] GlobalFogDensity = new float[MAX_PLAYERS];
        // TowerVisibilityMask: Dictionary<towerId, bool[]> — tower's visibility to each enemy
        // Key: towerId (entity id of fog-of-war enabled tower)
        // Value: bool array [enemyId] = true if enemy is visible to this tower this frame
        // Uses Dictionary to avoid 10B-entry flat array (only towers with VisionRadius > 0 need entries)
        public Dictionary<int, bool[]> TowerVisibilityByTower = new Dictionary<int, bool[]>();
        // RandomEventParam: event-specific parameter (e.g. gold amount for SupplyDrop, spawn count for Ambush)
        public float[] RandomEventParam = new float[MAX_PLAYERS];
        // RandomEventParam2: second event-specific parameter
        public float[] RandomEventParam2 = new float[MAX_PLAYERS];

        // ==================== Ascension/Difficulty Modifier 组件 ====================
        // AscensionModifierStacks: tracks stack count for each ascension modifier (up to 64 unique modifiers)
        public int[] AscensionModifierStacks = new int[64];

        // ==================== 科技树组件的 SOA 存储 ====================
        public int[] PlayerResearchPoints = new int[MAX_PLAYERS];
        public HashSet<string>[] PlayerUnlockedTechs = new HashSet<string>[MAX_PLAYERS];

        // ==================== 掉落物/拾取道具组件（SOA）====================
        // PickupX / PickupY: world position of each pickup
        public float[] PickupX = new float[MAX_ENTITIES];
        public float[] PickupY = new float[MAX_ENTITIES];
        // PickupType: type index into GameConfig.PickupDefs (0-4 = GoldPile/HealthPack/ManaOrb/SpeedBoost/DamageBoost), -1 = empty slot
        public int[] PickupType = new int[MAX_ENTITIES];
        // PickupValue: effect value (e.g. gold amount, healing amount)
        public float[] PickupValue = new float[MAX_ENTITIES];
        // PickupOwnerId: player ID who can collect this pickup (only that player can pick it up)
        public int[] PickupOwnerId = new int[MAX_ENTITIES];
        // PickupActive: true if slot is occupied
        public bool[] PickupActive = new bool[MAX_ENTITIES];
        // PickupLifetime: seconds remaining before auto-expire; 0 = inactive
        public float[] PickupLifetime = new float[MAX_ENTITIES];
        private int _pickupCount = 0;
        public int PickupCount => _pickupCount;

        // ==================== 波次状态组件 ====================
        // WaveIndex: current wave number (0-indexed), -1 = no wave started
        public int[] PlayerWaveIndex = new int[MAX_PLAYERS];
        // EnemiesRemaining: alive enemies in the current wave (updated when enemies die)
        public int[] PlayerEnemiesRemaining = new int[MAX_PLAYERS];
        // IsWaveActive: true when a wave is in progress (enemies spawned, not yet cleared)
        public bool[] PlayerIsWaveActive = new bool[MAX_PLAYERS];
        // WaveTimer: frames remaining before next wave auto-starts (-1 = waiting for manual start)
        public float[] PlayerWaveTimer = new float[MAX_PLAYERS];
        // WaveCompleteGold: gold awarded when wave was completed (for tech tree bonus calculation)
        public float[] PlayerWaveCompleteGold = new float[MAX_PLAYERS];

        // ==================== Wave Mutator 组件（SOA） ====================
        // CurrentWaveMutatorId: index into WaveMutatorDefs[] for the active mutator this wave, -1 = none
        public int[] CurrentWaveMutatorId = new int[MAX_PLAYERS];

        // ==================== Combo Kill 连击组件（SOA） ====================
        // ComboCount: current consecutive kill streak within combo window
        public float[] PlayerComboCount = new float[MAX_PLAYERS];
        // ComboTimer: seconds since last kill (resets combo when > ComboWindowSeconds)
        public float[] PlayerComboTimer = new float[MAX_PLAYERS];
        // ComboDamageMult: current damage multiplier = min(1 + ComboCount * ComboDamageBonusPerKill, ComboMaxMultiplier)
        public float[] PlayerComboDamageMult = new float[MAX_PLAYERS];
        // ComboKillStreak: max combo achieved this wave (for UI/achievement tracking)
        public float[] PlayerComboKillStreak = new float[MAX_PLAYERS];
        // ComboGoldMult: current gold bonus multiplier = min(1 + ComboCount * ComboGoldBonusPerKill, ComboMaxMultiplier)
        public float[] PlayerComboGoldMult = new float[MAX_PLAYERS];

        // ==================== Bank / Interest System 组件（SOA） ====================
        // PlayerBankedGold: gold stored in the bank (earns interest each wave)
        public float[] PlayerBankedGold = new float[MAX_PLAYERS];
        // PlayerInterestRate: interest rate multiplier (0.05f = 5% per wave, capped at InterestRateCap)
        public float[] PlayerInterestRate = new float[MAX_PLAYERS];

        // ==================== 敌人组件的 SOA 存储 ====================
        public float[] EnemyHealth = new float[MAX_ENTITIES];
        public float[] EnemyMaxHealth = new float[MAX_ENTITIES];
        public float[] EnemyMoveSpeed = new float[MAX_ENTITIES];
        public float[] EnemyDamage = new float[MAX_ENTITIES];
        public int[] EnemyGoldReward = new int[MAX_ENTITIES];
        public int[] EnemyWaveNumber = new int[MAX_ENTITIES];
        public bool[] EnemyActive = new bool[MAX_ENTITIES];
        public float[] EnemyChargeParam = new float[MAX_ENTITIES]; // SOA: replaces ConcurrentDictionary in EnemyAISystem
        // EnemyBuffDamageBonus: tracks buff damage bonus applied by buff_allies ability — separate from EnemyChargeParam
        public float[] EnemyBuffDamageBonus = new float[MAX_ENTITIES];
        // EnemyBuffDurationLeft: tracks remaining duration for buff_allies ability (in turns). 0 = no active buff.
        public float[] EnemyBuffDurationLeft = new float[MAX_ENTITIES];
        public int[] EnemySpawnFrame = new int[MAX_ENTITIES];
        // Armor: reduces incoming damage. Affected by attacker's armor penetration.
        public float[] EnemyArmor = new float[MAX_ENTITIES];
        // Enemy magic resistance: reduces incoming Magic damage (0.0-1.0 fraction reduction)
        // Separate from armor (physical). Physical ignores magic resist, Magic ignores armor.
        public float[] EnemyMagicResist = new float[MAX_ENTITIES];
        // Enemy evasion: probability that this enemy dodges an incoming attack (0.0-1.0, 0.0 = never evade)
        // Applied after hitChance roll; if evasion succeeds the attack deals 0 damage (not a miss sound effect)
        public float[] EnemyEvasion = new float[MAX_ENTITIES];
        // Enemy shield: absorbs incoming damage before it reaches EnemyHealth.
        // Shield is consumed first; remaining damage penetrates to health.
        public float[] EnemyShield = new float[MAX_ENTITIES];
        // Enemy thorns: reflects a fraction of damage taken back to the attacker (player/tower).
        // Applied after damage is dealt, in the same frame's serial phase.
        public float[] EnemyThornsRatio = new float[MAX_ENTITIES];
        // Armor Shred: stacks of armor reduction applied by attacker (e.g. AcidTower debuff)
        // Each stack reduces armor by a fixed amount (_armorShredPerStack in TechTree)
        public float[] EnemyArmorShredStacks = new float[MAX_ENTITIES];
        // Duration in turns remaining for armor shred stacks. 0 = no active shred.
        public float[] EnemyArmorShredDuration = new float[MAX_ENTITIES];
        // Curse debuff: stacks of curse applied by curse towers. Each aura accumulates additively.
        // CurseDmgReduction: damage output reduction (e.g. 0.2 = -20% attack damage)
        public float[] EnemyCurseDmgReduction = new float[MAX_ENTITIES];
        // CurseSpeedReduction: move speed reduction (e.g. 0.3 = -30% move speed)
        public float[] EnemyCurseSpeedReduction = new float[MAX_ENTITIES];
        // CurseArmorReduction: armor reduction (e.g. 0.15 = -15% armor)
        public float[] EnemyCurseArmorReduction = new float[MAX_ENTITIES];
        // CurseDmgTakenIncrease: additional damage taken bonus (e.g. 0.25 = +25% damage taken from attacks)
        public float[] EnemyCurseDmgTakenIncrease = new float[MAX_ENTITIES];
        // ==================== 流血/撕裂 DoT (Bleed — Stacking Physical DoT) ====================
        // EnemyBleedStacks: current number of bleed stacks on this enemy (0 = no bleed)
        // Each stack deals damage equal to bleedPct * EnemyMaxHealth per tick
        public float[] EnemyBleedStacks = new float[MAX_ENTITIES];
        // EnemyBleedDamagePerStack: raw damage per stack per tick (set by tower on application)
        public float[] EnemyBleedDamagePerStack = new float[MAX_ENTITIES];
        // EnemyBleedTimer: remaining time in seconds until next bleed tick (decays to 0 → trigger tick)
        public float[] EnemyBleedTimer = new float[MAX_ENTITIES];
        // EnemyBleedMaxStacks: maximum bleed stacks this enemy can have (Boss = 0 = immune)
        public float[] EnemyBleedMaxStacks = new float[MAX_ENTITIES];
        // EnemyBleedResistance: fraction of bleed application that is resisted (0 = no resist, 0.7 = 70% resist)
        public float[] EnemyBleedResistance = new float[MAX_ENTITIES];
        // EnemyBleedDurationLeft: total duration remaining for the bleed effect in seconds
        public float[] EnemyBleedDurationLeft = new float[MAX_ENTITIES];
        // ==================== 敌人 CC (Crowd Control) 字段 ====================
        // Grouped together after all enemy hot-path fields to preserve cache locality
        // EnemyStunFlag: legacy bool, kept for backward compat; use EnemyStunDurationLeft for correctness
        public bool[] EnemyStunFlag = new bool[MAX_ENTITIES];
        // EnemyStunDurationLeft: stun duration in turns. Decremented by EnemyMovementSystem.Update().
        // When > 0, IsEnemyStunned() returns true regardless of EnemyStunFlag.
        public float[] EnemyStunDurationLeft = new float[MAX_ENTITIES];
        // EnemySlowFactor: speed multiplier (0.5 = 50% speed), 0 = no slow
        public float[] EnemySlowFactor = new float[MAX_ENTITIES];
        // EnemyTerrainMoveSpeedMult: terrain-based speed multiplier (1.0 = normal, 0.5 = mud slow)
        public float[] EnemyTerrainMoveSpeedMult = new float[MAX_ENTITIES];
        // EnemyMoveSpeedBase: stores original speed for slow recovery
        public float[] EnemyMoveSpeedBase = new float[MAX_ENTITIES];
        // EnemySlowDurationLeft: tower-slow duration in turns. Separate from EnemyBuffDurationLeft
        public float[] EnemySlowDurationLeft = new float[MAX_ENTITIES];
        // ==================== Enemy Wound / Cripple (HP-Threshold Slow) ====================
        // EnemyWoundThreshold: HP fraction threshold that triggers wound slow (e.g. 0.3 = 30% HP)
        // Default 0f = no wound mechanic. When HP drops below this ratio, wound slow activates.
        public float[] EnemyWoundThreshold = new float[MAX_ENTITIES];
        // EnemyWoundSlowRatio: speed multiplier when wounded (e.g. 0.5 = 50% speed)
        public float[] EnemyWoundSlowRatio = new float[MAX_ENTITIES];
        // EnemyIsWounded: true when HP is below wound threshold and wound mechanic is active
        public bool[] EnemyIsWounded = new bool[MAX_ENTITIES];
        // EnemyKnockbackForceLeft: remaining knockback force applied this frame (decays to 0)
        public float[] EnemyKnockbackForceLeft = new float[MAX_ENTITIES];
        // EnemyIsElite: true if this enemy was spawned as an elite ([ELITE] prefix in fullName).
        // Used by ResolveEnemiesKilledThisFrame to correctly award GoldOnEliteKill instead of
        // the broken EnemyTypeName == "Elite" check (EnemyTypeName stores base type names).
        public bool[] EnemyIsElite = new bool[MAX_ENTITIES];
        // EnemyIsFlying: true if this enemy is a flying unit (can only be hit by anti-air towers)
        public bool[] EnemyIsFlying = new bool[MAX_ENTITIES];
        // EnemyFlightHeight: flight altitude level (0=ground, 1=low altitude, 2=high altitude)
        // Affects which tower types can target this enemy
        public float[] EnemyFlightHeight = new float[MAX_ENTITIES];
        // EnemyCanLand: true if this flying enemy can land and become a ground unit
        public bool[] EnemyCanLand = new bool[MAX_ENTITIES];
        // EnemyStealthMultiplier: per-entity stealth attack damage multiplier.
        // Set by stealth_attack ability, consumed and reset by EnemyAISystem attack methods.
        public float[] EnemyStealthMultiplier = new float[MAX_ENTITIES];

        // ==================== 钻地/潜行敌人组件 (Burrow / Underground Enemies, SOA) ====================
        // EnemyIsBurrowed: true when enemy is underground (cannot be targeted by towers)
        public bool[] EnemyIsBurrowed = new bool[MAX_ENTITIES];
        // EnemyBurrowTimer: remaining underground duration in turns (0 = about to emerge)
        public float[] EnemyBurrowTimer = new float[MAX_ENTITIES];
        // EnemyBurrowCooldown: cooldown before can burrow again (-1 = cannot burrow, 0 = always can, >0 = turns remaining)
        public float[] EnemyBurrowCooldown = new float[MAX_ENTITIES];
        // EnemyBurrowCooldownRef: original cooldown value (for reset after emerge, only meaningful if CanBurrow)
        public float[] EnemyBurrowCooldownRef = new float[MAX_ENTITIES];
        // EnemyBurrowSpeedMult: movement speed multiplier while underground (typically faster/slower)
        public float[] EnemyBurrowSpeedMult = new float[MAX_ENTITIES];
        // EnemyBurrowEmergeDamage: AoE damage dealt when emerging from ground
        public float[] EnemyBurrowEmergeDamage = new float[MAX_ENTITIES];
        // EnemyBurrowRadius: AoE radius for emerge damage
        public float[] EnemyBurrowRadius = new float[MAX_ENTITIES];

        // ==================== 亡灵法师组件 (Necromancer, SOA) ====================
        // EnemyCanResurrect: true if this enemy is a necromancer
        public bool[] EnemyCanResurrect = new bool[MAX_ENTITIES];
        // EnemyResurrectRange: scan radius for nearby corpses (world units)
        public float[] EnemyResurrectRange = new float[MAX_ENTITIES];
        // EnemyResurrectCooldown: remaining cooldown in turns (0 = ready, < 0 = no cooldown)
        public float[] EnemyResurrectCooldown = new float[MAX_ENTITIES];
        // EnemyResurrectCooldownRef: reference cooldown value (used to reset after use)
        public float[] EnemyResurrectCooldownRef = new float[MAX_ENTITIES];
        // EnemyResurrectHpMult: HP multiplier applied to reanimated minions
        public float[] EnemyResurrectHpMult = new float[MAX_ENTITIES];
        // EnemyMaxResurrectCount: max simultaneous reanimated minions per necromancer (0 = unlimited)
        public int[] EnemyMaxResurrectCount = new int[MAX_ENTITIES];
        // EnemyResurrectCorpseAgeLimit: max corpse age in seconds (default MAX_CORPSE_AGE_SEC)
        public float[] EnemyResurrectCorpseAgeLimit = new float[MAX_ENTITIES];
        // EnemyIsReanimated: true if this enemy was spawned as a reanimated minion by a necromancer
        public bool[] EnemyIsReanimated = new bool[MAX_ENTITIES];
        // EnemyOwnerId: the necromancer entity ID that owns this reanimated minion (-1 if none)
        public int[] EnemyOwnerId = new int[MAX_ENTITIES];

        // ==================== 玩家召唤单位组件 (Player-Summoned Units, SOA) ====================
        // SummonedUnitActive: true if this entity is a player-summoned combat unit
        public bool[] SummonedUnitActive = new bool[MAX_ENTITIES];
        // SummonedUnitType: 0=Melee, 1=Ranged, 2=Bomber
        public int[] SummonedUnitType = new int[MAX_ENTITIES];
        // SummonedUnitHealth / SummonedUnitMaxHealth: current and max HP
        public float[] SummonedUnitHealth = new float[MAX_ENTITIES];
        public float[] SummonedUnitMaxHealth = new float[MAX_ENTITIES];
        // SummonedUnitDamage: attack damage per hit
        public float[] SummonedUnitDamage = new float[MAX_ENTITIES];
        // SummonedUnitMoveSpeed: movement speed (tiles/sec)
        public float[] SummonedUnitMoveSpeed = new float[MAX_ENTITIES];
        // SummonedUnitAttackRange: attack range (tiles)
        public int[] SummonedUnitAttackRange = new int[MAX_ENTITIES];
        // SummonedUnitAttackSpeed: attacks per second
        public float[] SummonedUnitAttackSpeed = new float[MAX_ENTITIES];
        // SummonedUnitAttackTimer: cooldown accumulator for attack timing
        public float[] SummonedUnitAttackTimer = new float[MAX_ENTITIES];
        // SummonedUnitDuration: remaining lifetime in seconds (0 = permanent until killed)
        public float[] SummonedUnitDuration = new float[MAX_ENTITIES];
        // SummonedUnitOwnerId: player who summoned this unit
        public int[] SummonedUnitOwnerId = new int[MAX_ENTITIES];
        // SummonedUnitTargetId: current attack target entity id (-1 = none)
        public int[] SummonedUnitTargetId = new int[MAX_ENTITIES];
        // SummonedUnitGoldReward: gold awarded when this unit kills an enemy
        public int[] SummonedUnitGoldReward = new int[MAX_ENTITIES];

        // ==================== Boss Phase / Enrage 字段（SOA） ====================
        // EnemyBossPhase: current phase index for boss enemies (0 = phase 1, 1 = phase 2, etc.)
        // Non-boss enemies default to 0.
        public int[] EnemyBossPhase = new int[MAX_ENTITIES];
        // EnemyPhaseThreshold: health fraction (0-1) at which next phase triggers.
        // E.g., threshold=0.5f → when health drops below 50% max, phase increments.
        // Multiple thresholds stored as CSV string: "0.75,0.50,0.25" — parsed at spawn.
        // Default empty = no phase transitions (phase 0 only).
        public string[] EnemyPhaseThresholds = new string[MAX_ENTITIES];
        // EnemyEnrageTimer: seconds until enrage mode activates for this enemy (0 = no enrage).
        // When timer reaches 0, the enemy enters permanent enrage (speed/damage boost).
        // Default 0 = no enrage timer.
        public float[] EnemyEnrageTimer = new float[MAX_ENTITIES];
        // EnemyIsEnraged: true once enrage condition is met (permanent flag, no cooldown).
        // When true, the enemy's base stats are boosted per enrage config.
        public bool[] EnemyIsEnraged = new bool[MAX_ENTITIES];

        // ==================== Boss Invulnerable Phase（无敌阶段） ====================
        // EnemyIsInvulnerable: true when the enemy is in an invulnerable phase (e.g. Boss skill animation).
        // When true, the enemy takes 0 damage from all sources.
        public bool[] EnemyIsInvulnerable = new bool[MAX_ENTITIES];
        // EnemyInvulnerablePhaseName: name of the active invulnerable phase (e.g. "shield", "teleport", "rage").
        // Used for UI/feedback. Empty = not in invulnerable phase.
        public string[] EnemyInvulnerablePhaseName = new string[MAX_ENTITIES];

        // ==================== Enemy Fission (Split on Death) ====================
        // EnemyFissionDefId: index into GameConfig.FissionDefs for this enemy's fission definition (-1 = none)
        public int[] EnemyFissionDefId = new int[MAX_ENTITIES];
        // EnemyFissionGeneration: current fission generation (0 = original spawn, 1 = first generation children, etc.)
        // Capped at FissionDef.MaxGeneration — once reached, no more fission on death
        public int[] EnemyFissionGeneration = new int[MAX_ENTITIES];

        // ==================== Enemy Morph (Transform Mid-Wave) ====================
        // EnemyMorphDefId: index into GameConfig.MorphDefs for this enemy's morph definition (-1 = none)
        public int[] EnemyMorphDefId = new int[MAX_ENTITIES];
        // EnemyIsMorphed: true once this enemy has completed a morph transformation
        public bool[] EnemyIsMorphed = new bool[MAX_ENTITIES];
        // EnemyMorphTriggered: set to true when trigger condition is met (consumed at morph execution)
        public bool[] EnemyMorphTriggered = new bool[MAX_ENTITIES];

        // ==================== 敌人生命链接 / Life Link (Damage Sharing, SOA) ====================
        // EnemyIsLifeLinker: true if this enemy is a Life Link master (can establish links with others)
        public bool[] EnemyIsLifeLinker = new bool[MAX_ENTITIES];
        // EnemyLifeLinkDefId: index into GameConfig.LifeLinkDefs (-1 = none, 0+ = active link definition)
        public int[] EnemyLifeLinkDefId = new int[MAX_ENTITIES];
        // EnemyLinkedEnemyId: the entity ID this enemy is linked to (-1 = none, -2 = is link master with no target)
        // For link master: stores primary target; for linked enemy: stores the master ID
        public int[] EnemyLinkedEnemyId = new int[MAX_ENTITIES];
        // EnemyLifeLinkRatio: fraction of incoming damage shared with linked enemy (e.g. 0.5 = 50/50 split)
        public float[] EnemyLifeLinkRatio = new float[MAX_ENTITIES];
        // EnemyLifeLinkCooldownLeft: remaining cooldown in turns before this LifeLinker can link again
        public float[] EnemyLifeLinkCooldownLeft = new float[MAX_ENTITIES];
        // EnemyIsLinked: true if this enemy has an active Life Link (either as master or slave)
        public bool[] EnemyIsLinked = new bool[MAX_ENTITIES];

        // ==================== 路径分叉 / 路点系统字段（SOA） ====================
        // EnemyPathId: which path this enemy is assigned to (-1 = no path, use default straight movement)
        // 0 = default (straight Y-axis), 1 = fork_left, 2 = fork_right, 3 = ring
        public int[] EnemyPathId = new int[MAX_ENTITIES];
        // EnemyPathNodeIndex: current waypoint index in the assigned path (-1 = reached goal / leaked)
        public int[] EnemyPathNodeIndex = new int[MAX_ENTITIES];
        // EnemyTeleportCooldown: turns remaining until teleport is ready (0 = ready / no cooldown)
        public float[] EnemyTeleportCooldown = new float[MAX_ENTITIES];
        // EnemyTeleportDestinationX/Y: target position for teleport/blink destination
        public float[] EnemyTeleportDestinationX = new float[MAX_ENTITIES];
        public float[] EnemyTeleportDestinationY = new float[MAX_ENTITIES];
        // EnemyTeleportType: 0=none, 1=blink_to_destination, 2=portal_entry, 3=random_phase_ahead, 4=retreat_to_player
        public int[] EnemyTeleportType = new int[MAX_ENTITIES];

        // ==================== Fear / Taunt / Charm 行为控制字段（SOA） ====================
        // EnemyFearDurationLeft: turns remaining for fear effect. When > 0, enemy runs away (direction = +1).
        public float[] EnemyFearDurationLeft = new float[MAX_ENTITIES];
        // EnemyTauntTargetId: entity ID that this enemy is forced to attack (taunt effect). -1 = no taunt.
        public int[] EnemyTauntTargetId = new int[MAX_ENTITIES];
        // EnemyCharmDurationLeft: turns remaining for charm effect. When > 0, enemy attacks other enemies.
        public float[] EnemyCharmDurationLeft = new float[MAX_ENTITIES];

        // ==================== 敌人抗性字段（SOA） ====================
        // EnemyStunResistance: 0-1, reduces stun duration and chance
        public float[] EnemyStunResistance = new float[MAX_ENTITIES];
        // EnemyFreezeResistance: 0-1, reduces freeze duration and chance
        public float[] EnemyFreezeResistance = new float[MAX_ENTITIES];
        // EnemySlowResistance: 0-1, reduces slow factor severity
        public float[] EnemySlowResistance = new float[MAX_ENTITIES];
        // EnemyKnockbackResistance: 0-1, reduces knockback distance taken from towers
        public float[] EnemyKnockbackResistance = new float[MAX_ENTITIES];
        // EnemyDamageResistance: 0-1, reduces all damage taken (applied in TowerAttackSystem and SkillSystem)
        public float[] EnemyDamageResistance = new float[MAX_ENTITIES];

        // ==================== 肉盾/前锋掩护组件（SOA）====================
        // EnemyIsVanguard: true if this enemy is a vanguard (shield bearer) protecting allies behind it
        public bool[] EnemyIsVanguard = new bool[MAX_ENTITIES];
        // EnemyVanguardCoverRange: how many cells ahead this vanguard protects (-1 = full row)
        public float[] EnemyVanguardCoverRange = new float[MAX_ENTITIES];
        // EnemyVanguardDmgTransfer: fraction of damage taken by protected enemies that transfers to vanguard (0-1)
        public float[] EnemyVanguardDmgTransfer = new float[MAX_ENTITIES];
        // EnemyVanguardCoverCount: number of allies currently protected by this vanguard (computed each frame)
        public int[] EnemyVanguardCoverCount = new int[MAX_ENTITIES];

        // ==================== 敌人治疗单位组件（SOA）====================
        // EnemyHealerHealAmount: flat HP restored per heal tick (0 = not a healer)
        public float[] EnemyHealerHealAmount = new float[MAX_ENTITIES];
        // EnemyHealerHealInterval: heal cooldown / interval in seconds (also used as range for heal check)
        public float[] EnemyHealerHealInterval = new float[MAX_ENTITIES];
        // EnemyHealerHealTargetPriority: 0=lowest_health, 1=highest_threat (future extension)
        public int[] EnemyHealerHealTargetPriority = new int[MAX_ENTITIES];

        // ==================== 金币窃取敌人组件（SOA）====================
        // EnemyCanStealGold: true if this enemy is a thief that steals gold instead of damaging base
        public bool[] EnemyCanStealGold = new bool[MAX_ENTITIES];
        // EnemyStealAmount: amount of gold this enemy steals when reaching the end
        public float[] EnemyStealAmount = new float[MAX_ENTITIES];
        // EnemyStolenGold: total gold this enemy has stolen (for tracking/debugging)
        public float[] EnemyStolenGold = new float[MAX_ENTITIES];
        // EnemyGoldOnReturn: bonus gold awarded when player kills a thief after it escapes
        public float[] EnemyGoldOnReturn = new float[MAX_ENTITIES];
        // EnemyHasStolenGold: set to true when thief escapes with stolen gold (skips gold reward on death)
        public bool[] EnemyHasStolenGold = new bool[MAX_ENTITIES];

        // ==================== 敌人词缀组件（SOA）====================
        // EnemyAffixFlags: bit-mask of active affixes (see BuffType affix bits 16-22)
        // Each enemy spawns with 1-3 random affixes; stored as a flag set for O(1) HasAffix() checks.
        // Affixes: ExtraFast(×1.5 speed), Vampiric(回复), Molten(爆炸), Shielding(初始护盾),
        //          Teleporter(传送), Regen(回复), Explosive(全屏爆炸)
        public BuffType[] EnemyAffixFlags = new BuffType[MAX_ENTITIES];

        // ==================== 元素状态组件（SOA）====================
        // EnemyElementStatus: bit-mask of active elements on this enemy (ElementType flags)
        // Multiple elements can coexist (e.g., Fire + Poison = Pyroclastic)
        public ElementType[] EnemyElementStatus = new ElementType[MAX_ENTITIES];
        // EnemyElementTimer: remaining duration (in seconds) for each element bit flag
        // Indexed by element ordinal (0-3), matches ElementType bit positions
        public float[] EnemyElementTimer = new float[MAX_ENTITIES * 4];

        // ==================== 敌人产卵/巢穴组件（SOA）====================
        // NestDefId: index into GameConfig.NestDefs for this entity (-1 = not a nest)
        public int[] NestDefId = new int[MAX_ENTITIES];
        // NestHealth / NestMaxHealth: health of the nest structure (separate from enemy HP)
        public float[] NestHealth = new float[MAX_ENTITIES];
        public float[] NestMaxHealth = new float[MAX_ENTITIES];
        // NestSpawnTimer: countdown to next spawn (in seconds)
        public float[] NestSpawnTimer = new float[MAX_ENTITIES];
        // NestSpawnInterval: time between spawns for this nest
        public float[] NestSpawnInterval = new float[MAX_ENTITIES];
        // NestMonsterTypeStr: monster type string for the minion this nest spawns
        public string[] NestMonsterTypeStr = new string[MAX_ENTITIES];
        // NestMaxAlive: max minions alive from this nest simultaneously
        public int[] NestMaxAlive = new int[MAX_ENTITIES];
        // NestActiveCount: current number of alive minions from this nest
        public int[] NestActiveCount = new int[MAX_ENTITIES];
        // NestOriginId: parent nest entity ID for a minion (nestId for minions, -1 for nests themselves)
        public int[] NestOriginId = new int[MAX_ENTITIES];

        // ==================== 敌人 AI 组件的 SOA 存储 ====================
        public string[] EnemyAIAction = new string[MAX_ENTITIES];
        public int[] EnemyAIChargeCounter = new int[MAX_ENTITIES];
        public int[] EnemyAILastAttackTurn = new int[MAX_ENTITIES];
        public string[] EnemyTypeName = new string[MAX_ENTITIES];
        // Pre-cached behavior tree per enemy — set once at spawn in WaveSpawningSystem
        public BTCachedTree[] EnemyBehaviorTree = new BTCachedTree[MAX_ENTITIES];
        // Optimized action type as enum — avoids string comparison per frame
        public EnemyActionType[] EnemyActionEnum = new EnemyActionType[MAX_ENTITIES];
        // Ability ID for enemy_cast_* actions — stores the ability id to invoke
        public string[] EnemyCastAbilityId = new string[MAX_ENTITIES];

        // ==================== 塔组件的 SOA 存储 ====================
        // Tower targeting mode: controls which enemy the tower selects as its target.
        // Maps to TowerTargetingMode enum: Nearest=0, Furthest=1, LowestHealth=2, HighestHealth=3, FirstSpawned=4, LastSpawned=5, Intercept=6
        public int[] TowerTargetingMode = new int[MAX_ENTITIES];
        // Tower projectile homing: if true, this tower's projectiles track targets mid-flight
        public bool[] TowerProjectileHoming = new bool[MAX_ENTITIES];
        // Tower intercept rate: probability of intercepting enemy projectiles (for PointDefense towers)
        // Stored separately from TowerCritChance to keep concerns isolated (reuse CritChance as intercept rate when needed)
        public float[] TowerInterceptRate = new float[MAX_ENTITIES];
        // Tower damage type: 0=Physical, 1=Magic, 2=True. Determines which resistance the target uses.
        public int[] TowerDamageType = new int[MAX_ENTITIES];
        // Tower selection state — O(1) read/write, no GC
        public bool[] TowerSelected = new bool[MAX_ENTITIES];
        public string[] TowerType = new string[MAX_ENTITIES];
        public float[] TowerAttackDamage = new float[MAX_ENTITIES];
        public int[] TowerRange = new int[MAX_ENTITIES];
        public float[] TowerAttackSpeed = new float[MAX_ENTITIES];
        public int[] TowerLevel = new int[MAX_ENTITIES];
        public float[] TowerUpgradeCost = new float[MAX_ENTITIES];
        // Upgrade path ID per tower (e.g., "standard", "fast", "tank") — drives config-driven upgrade curves
        public string[] TowerUpgradePathId = new string[MAX_ENTITIES];
        // Tower fusion tier: incremented each time this tower is merged (0 = never merged)
        public int[] TowerFusionTier = new int[MAX_ENTITIES];
        public bool[] TowerActive = new bool[MAX_ENTITIES];
        public float[] TowerLastAttackTime = new float[MAX_ENTITIES];
        // Tower debuff parameters (read from TowerConfig per tower type)
        public float[] TowerStunChance = new float[MAX_ENTITIES];
        public float[] TowerSlowAmount = new float[MAX_ENTITIES];
        public float[] TowerSlowDuration = new float[MAX_ENTITIES];
        // Tower special abilities from upgrade path (e.g., armor pierce, splash, critical strike)
        public float[] TowerArmorPierceRatio = new float[MAX_ENTITIES];
        public float[] TowerSplashRadius = new float[MAX_ENTITIES];
        // Tower armor shred bonus: bonus armor reduction applied to target on hit (stacks)
        public float[] TowerArmorShredBonus = new float[MAX_ENTITIES];
        // Tower shield break bonus: extra damage multiplier applied to shielded enemies (shreds shield first)
        public float[] TowerShieldBreakBonus = new float[MAX_ENTITIES];
        // Tower accuracy: probability that this tower's attack hits the target (0.0-1.0, 1.0 = always hit)
        // Accuracy < 1.0 results in random misses, creating evasion gameplay for fast enemies
        public float[] TowerAccuracy = new float[MAX_ENTITIES];
        // AOE falloff: inner ratio (0.5 = inner 50% at full damage), outer mult (0.5 = outer half damage)
        // Default 1.0 = no falloff (all targets take full splash damage)
        public float[] TowerFalloffInnerRatio = new float[MAX_ENTITIES];
        public float[] TowerFalloffOuterMult = new float[MAX_ENTITIES];
        public float[] TowerCritChance = new float[MAX_ENTITIES];
        public float[] TowerCritMultiplier = new float[MAX_ENTITIES];
        public bool[] TowerHasChainLightning = new bool[MAX_ENTITIES];
        public bool[] TowerHasFreezeAoe = new bool[MAX_ENTITIES];
        // Tower anti-air flags: controls which height layers a tower can attack
        // TowerCanHitAir=true: tower can attack flying enemies (anti-air tower)
        // TowerCanHitGround=true: tower can attack ground enemies
        // Both can be true (multi-type tower) or false (invalid — will skip all targets)
        public bool[] TowerCanHitAir = new bool[MAX_ENTITIES];
        public bool[] TowerCanHitGround = new bool[MAX_ENTITIES];
        // Tower special ability parameters from TowerSpecialAbility config
        public float[] TowerSpecialAbilityRadius = new float[MAX_ENTITIES];
        public float[] TowerSpecialAbilityDamageMult = new float[MAX_ENTITIES];
        public float[] TowerSpecialAbilityDotDamage = new float[MAX_ENTITIES];
        public float[] TowerSpecialAbilityDotInterval = new float[MAX_ENTITIES];

        // ==================== 塔击退/位移效果 (Knockback) ====================
        // TowerKnockbackForce: strength of knockback applied to enemies on hit (0 = no knockback)
        // Positive values push enemies backward along the path direction
        public float[] TowerKnockbackForce = new float[MAX_ENTITIES];
        // TowerKnockbackRadius: radius within which knockback force is fully applied (beyond it, no effect)
        public float[] TowerKnockbackRadius = new float[MAX_ENTITIES];

        // ==================== 塔散射/多重射击（Scatter / Multi-shot）====================
        // TowerProjectileCount: number of projectiles fired per attack (1 = single shot, >1 = scatter/multicast)
        public int[] TowerProjectileCount = new int[MAX_ENTITIES];
        // TowerScatterAngle: angular spread in radians for multi-shot (0 = all projectiles aimed at target)
        public float[] TowerScatterAngle = new float[MAX_ENTITIES];

        // ==================== 塔弹跳/弹射 (Bouncing Projectiles) ====================
        // TowerBouncesRemaining: number of bounces left after initial hit (0 = no bounce, like scatter)
        // TowerBounceRange: search radius in tiles for finding next bounce target
        // TowerBounceDamageFalloff: damage multiplier per bounce (0.8 = 80% of previous hit's damage)
        // TowerBounceHitsRemaining: per-attack counter — tracks bounces consumed in current attack
        public int[] TowerBouncesRemaining = new int[MAX_ENTITIES];
        public float[] TowerBounceRange = new float[MAX_ENTITIES];
        public float[] TowerBounceDamageFalloff = new float[MAX_ENTITIES];
        public int[] TowerBounceHitsRemaining = new int[MAX_ENTITIES];

        // ==================== 塔穿透弹道系统（Piercing Projectile）====================
        // TowerProjectilePierceCount: number of enemies the projectile can pierce through (0 = no pierce)
        // TowerProjectilePierceDmgFalloff: damage multiplier after each pierce (1.0 = full damage, 0.7 = 70%)
        // TowerPierceHitsRemaining: per-attack counter — tracks pierce consumed in current attack
        public int[] TowerProjectilePierceCount = new int[MAX_ENTITIES];
        public float[] TowerProjectilePierceDmgFalloff = new float[MAX_ENTITIES];
        public int[] TowerPierceHitsRemaining = new int[MAX_ENTITIES];

        // ==================== 塔弹道分裂/子母弹系统（Projectile Fragmentation）====================
        // TowerProjectileFragmentCount: number of child projectiles spawned on impact (0 = no fragmentation)
        // TowerProjectileFragmentRange: search radius in tiles for finding fragment targets
        // TowerProjectileFragmentDmgMult: damage multiplier for each fragment relative to parent projectile
        public int[] TowerProjectileFragmentCount = new int[MAX_ENTITIES];
        public float[] TowerProjectileFragmentRange = new float[MAX_ENTITIES];
        public float[] TowerProjectileFragmentDmgMult = new float[MAX_ENTITIES];

        // ==================== 塔弹药系统（Ammo）====================
        // TowerCurrentAmmo: current ammo count (0 = empty)
        public int[] TowerCurrentAmmo = new int[MAX_ENTITIES];
        // TowerMaxAmmo: maximum ammo capacity (0 = unlimited/infinite)
        public int[] TowerMaxAmmo = new int[MAX_ENTITIES];
        // TowerReloadTime: total time to fully reload (seconds)
        public float[] TowerReloadTime = new float[MAX_ENTITIES];
        // TowerReloadProgress: current reload progress (0 to TowerReloadTime)
        public float[] TowerReloadProgress = new float[MAX_ENTITIES];
        // TowerIsReloading: true if tower is currently reloading
        public bool[] TowerIsReloading = new bool[MAX_ENTITIES];

        // ==================== 塔超载/过载系统（Overcharge）====================
        // TowerIsOvercharged: true if tower is currently in overcharged (boosted) state
        public bool[] TowerIsOvercharged = new bool[MAX_ENTITIES];
        // TowerOverchargeDuration: remaining overcharge duration in seconds (0 = inactive)
        public float[] TowerOverchargeDuration = new float[MAX_ENTITIES];
        // TowerOverchargeCooldown: remaining cooldown before overcharge can be activated again (seconds)
        public float[] TowerOverchargeCooldown = new float[MAX_ENTITIES];
        // TowerCanOvercharge: true if this tower type supports overcharge (from config)
        public bool[] TowerCanOvercharge = new bool[MAX_ENTITIES];

        // ==================== 塔协同增益组件 (Tower Synergy) ====================
        // 每个塔的协同 ID 索引，-1 表示无协同
        public int[] TowerSynergyId = new int[MAX_ENTITIES];
        // 协同增益倍率（从 JSON config 读取并应用，如 bonusChainCount, dotDamageBonus）
        public float[] TowerSynergyMultiplier = new float[MAX_ENTITIES];

        // ==================== 时间操纵塔（Chrono Tower）字段（SOA）====================
        // TowerIsChronoTower: true if this tower is a Chrono Tower that slows enemies in a time field
        public bool[] TowerIsChronoTower = new bool[MAX_ENTITIES];
        // TowerTimeFieldRadius: radius of the time dilation field (in grid units), 0 = no field
        public float[] TowerTimeFieldRadius = new float[MAX_ENTITIES];
        // TowerTimeScale: time scale applied to enemies within this tower's field (e.g. 0.5 = 50% speed)
        public float[] TowerTimeScale = new float[MAX_ENTITIES];
        // EnemyTimeScale: per-enemy time scale multiplier from Chrono Tower fields (1.0 = normal)
        // This is an accumulated value — multiple chrono towers take the minimum (slowest)
        // Initialized to 1f so new enemies start at normal speed (no 0f default freeze risk)
        public float[] EnemyTimeScale = new float[MAX_ENTITIES];

        // ==================== 光环辅助塔（Aura Tower）字段（SOA）====================
        // TowerIsAuraTower: true if this tower is an aura (support) tower that buffs nearby friendly towers
        public bool[] TowerIsAuraTower = new bool[MAX_ENTITIES];
        // TowerAuraRadius: radius within which the aura effect applies (in grid units)
        public float[] TowerAuraRadius = new float[MAX_ENTITIES];
        // TowerAuraAttackSpeedBonus: attack speed multiplier bonus granted to towers in range (e.g. 0.2 = +20%)
        public float[] TowerAuraAttackSpeedBonus = new float[MAX_ENTITIES];
        // TowerAuraDamageBonus: damage multiplier bonus granted to towers in range (e.g. 0.15 = +15%)
        public float[] TowerAuraDamageBonus = new float[MAX_ENTITIES];

        // ==================== 塔沉默/禁用系统 (Tower Silence) ====================
        // TowerIsSilenced: true if this tower is currently silenced (cannot attack). Set by enemy abilities.
        public bool[] TowerIsSilenced = new bool[MAX_ENTITIES];
        // TowerSilenceTimer: remaining silence duration in turns (decremented each turn). 0 = not silenced.
        public float[] TowerSilenceTimer = new float[MAX_ENTITIES];
        // TowerSilenceSourceId: enemy entity ID that applied this silence (-1 = none/unknown)
        public int[] TowerSilenceSourceId = new int[MAX_ENTITIES];

        // ==================== 敌人驱散/净化塔增益 (Tower Dispel) ====================
        // TowerIsDispelled: true if this tower's aura/synergy buffs are currently removed by enemy dispel
        public bool[] TowerIsDispelled = new bool[MAX_ENTITIES];
        // TowerDispelTimer: remaining dispel duration in turns (decremented each turn). 0 = not dispelled.
        public float[] TowerDispelTimer = new float[MAX_ENTITIES];
        // TowerDispelImmunityTimer: immunity duration in turns after dispel expires (prevents rapid re-dispel)
        public float[] TowerDispelImmunityTimer = new float[MAX_ENTITIES];

        // ==================== 诅咒/削弱光环塔 (Curse Tower) ====================
        // TowerIsCurseTower: true if this tower is a curse aura tower that debuffs nearby enemies
        public bool[] TowerIsCurseTower = new bool[MAX_ENTITIES];
        // TowerCurseRadius: radius within which the curse effect applies (in grid units)
        public float[] TowerCurseRadius = new float[MAX_ENTITIES];
        // TowerCurseDmgReduction: damage reduction applied to cursed enemies (e.g. 0.2 = -20% damage)
        public float[] TowerCurseDmgReduction = new float[MAX_ENTITIES];
        // TowerCurseSpeedReduction: speed reduction applied to cursed enemies (e.g. 0.3 = -30% move speed)
        public float[] TowerCurseSpeedReduction = new float[MAX_ENTITIES];
        // TowerCurseArmorReduction: armor reduction applied to cursed enemies (e.g. 0.15 = -15% armor)
        public float[] TowerCurseArmorReduction = new float[MAX_ENTITIES];
        // TowerCurseDmgTakenIncrease: additional damage taken bonus applied to cursed enemies (e.g. 0.25 = +25% damage taken)
        public float[] TowerCurseDmgTakenIncrease = new float[MAX_ENTITIES];

        // ==================== 牵引/磁力/漩涡塔 (Pull / Magnet / Vortex Towers) ====================
        // TowerIsPullTower: true if this tower applies gravitational pull to nearby enemies
        public bool[] TowerIsPullTower = new bool[MAX_ENTITIES];
        // TowerPullStrength: pull force magnitude (units per second toward tower center)
        public float[] TowerPullStrength = new float[MAX_ENTITIES];
        // TowerPullRadius: radius within which enemies are pulled toward the tower
        public float[] TowerPullRadius = new float[MAX_ENTITIES];
        // TowerPullCooldown: cooldown between pull pulses in seconds (0 = continuous pull)
        public float[] TowerPullCooldown = new float[MAX_ENTITIES];
        // TowerPullTimer: remaining cooldown time in seconds
        public float[] TowerPullTimer = new float[MAX_ENTITIES];
        // EnemyIsBeingPulled: true if this enemy is currently affected by a pull effect
        public bool[] EnemyIsBeingPulled = new bool[MAX_ENTITIES];

        // ==================== 流血/撕裂塔 (Bleed / Hemorrhage Towers) ====================
        // TowerIsBleedTower: true if this tower applies stacking bleed on hit (Slash/Pierce type)
        public bool[] TowerIsBleedTower = new bool[MAX_ENTITIES];
        // TowerBleedStacksPerHit: number of bleed stacks applied per successful hit
        public float[] TowerBleedStacksPerHit = new float[MAX_ENTITIES];
        // TowerBleedDmgPct: each stack deals TowerBleedDmgPct * target's EnemyMaxHealth per tick
        public float[] TowerBleedDmgPct = new float[MAX_ENTITIES];
        // TowerBleedTickInterval: seconds between bleed damage ticks (default 1f)
        public float[] TowerBleedTickInterval = new float[MAX_ENTITIES];
        // TowerBleedMaxStacks: maximum stacks that can be applied by this tower (0 = no cap)
        public float[] TowerBleedMaxStacks = new float[MAX_ENTITIES];
        // TowerBleedDuration: total duration in seconds for bleed effect
        public float[] TowerBleedDuration = new float[MAX_ENTITIES];

        // ==================== 塔被动资源生产（Income Tower）====================
        // TowerIsIncomeTower: true if this tower generates gold passively instead of attacking
        public bool[] TowerIsIncomeTower = new bool[MAX_ENTITIES];
        // TowerGoldPerSecond: gold generated per second by this income tower
        public float[] TowerGoldPerSecond = new float[MAX_ENTITIES];

        // ==================== 塔建造延迟系统 (Tower Construction) ====================
        // TowerIsConstructing: true if tower is in construction phase (cannot attack, can be damaged)
        public bool[] TowerIsConstructing = new bool[MAX_ENTITIES];
        // TowerConstructionProgress: 0.0 to 1.0, progress toward completion
        public float[] TowerConstructionProgress = new float[MAX_ENTITIES];
        // TowerConstructionTime: total time in seconds required to complete construction
        public float[] TowerConstructionTime = new float[MAX_ENTITIES];
        // TowerConstructionHP: current construction HP (takes damage from enemies during construction)
        public float[] TowerConstructionHP = new float[MAX_ENTITIES];
        // TowerConstructionMaxHP: maximum construction HP (set from config, decreases on hit)
        public float[] TowerConstructionMaxHP = new float[MAX_ENTITIES];
        // TowerIsVulnerableDuringConstruction: if true, enemies can attack this tower during construction
        public bool[] TowerIsVulnerableDuringConstruction = new bool[MAX_ENTITIES];

        // ==================== 塔牺牲/自毁系统 (Tower Demolish) ====================
        // TowerDemolishEffectRadius: radius of demolish AoE effect in tiles (0 = no demolish)
        public float[] TowerDemolishEffectRadius = new float[MAX_ENTITIES];
        // TowerDemolishDamage: raw damage dealt by demolish explosion
        public float[] TowerDemolishDamage = new float[MAX_ENTITIES];
        // TowerDemolishEffectType: 0=None, 1=Fire, 2=Ice, 3=Lightning, 4=Poison, 5=Arcane
        public int[] TowerDemolishEffectType = new int[MAX_ENTITIES];
        // TowerIsMarkedForDemolish: true when player triggers demolish (consumed this frame)
        public bool[] TowerIsMarkedForDemolish = new bool[MAX_ENTITIES];
        // TowerDemolishDotDamage: DoT damage per tick for fire/poison demolish
        public float[] TowerDemolishDotDamage = new float[MAX_ENTITIES];
        // TowerDemolishDotDuration: total duration of the demolish DoT in seconds
        public float[] TowerDemolishDotDuration = new float[MAX_ENTITIES];
        // TowerDemolishDotInterval: interval between DoT ticks in seconds
        public float[] TowerDemolishDotInterval = new float[MAX_ENTITIES];
        // TowerDemolishStunDuration: stun duration for ice/lightning demolish (turns)
        public int[] TowerDemolishStunDuration = new int[MAX_ENTITIES];

        // ==================== 塔联动/组合攻击 (Tower Link Combo) ====================
        // TowerLinkPartnerId: the tower ID of the partner tower in an active link combo (-1 = none)
        public int[] TowerLinkPartnerId = new int[MAX_ENTITIES];
        // TowerLinkComboType: the combo identifier string from tower_links.json (null = no active combo)
        public string[] TowerLinkComboType = new string[MAX_ENTITIES];
        // TowerLinkCooldown: remaining cooldown in seconds before the link combo can activate again (0 = ready)
        public float[] TowerLinkCooldown = new float[MAX_ENTITIES];
        // TowerLinkDamageBonus: additive damage bonus from link combo (applied as damage mult)
        public float[] TowerLinkDamageBonus = new float[MAX_ENTITIES];

        // ==================== 塔旋转/瞄准延迟 (Turret Rotation & Turn Rate) ====================
        // TowerFacingAngle: current facing angle in radians (0 = East, PI/2 = North)
        public float[] TowerFacingAngle = new float[MAX_ENTITIES];
        // TowerTurnRate: maximum angular change per second in radians (e.g. PI = 180°/sec, 0 = instant/snap)
        public float[] TowerTurnRate = new float[MAX_ENTITIES];

        // ==================== 塔经验/熟练度系统 (Tower XP & Mastery) ====================
        // TowerExperience: accumulated experience points for each tower (kills grant XP)
        public float[] TowerExperience = new float[MAX_ENTITIES];
        // TowerMasteryLevel: current mastery level (1 = fresh, increases with XP thresholds)
        public int[] TowerMasteryLevel = new int[MAX_ENTITIES];
        // TowerKillCount: total enemies killed by this tower (used for mastery tracking)
        public int[] TowerKillCount = new int[MAX_ENTITIES];

        // ==================== 路障/墙体组件（Obstacle）====================
        // 路障是可被敌人攻击的放置物（冰墙、地雷等）
        public const int MAX_OBSTACLES = 5000;
        public bool[] ObstacleActive = new bool[MAX_OBSTACLES];
        public float[] ObstacleHealth = new float[MAX_OBSTACLES];
        public float[] ObstacleMaxHealth = new float[MAX_OBSTACLES];
        public float[] ObstacleX = new float[MAX_OBSTACLES];
        public float[] ObstacleY = new float[MAX_OBSTACLES];
        public int[] ObstacleType = new int[MAX_OBSTACLES];  // index into ObstacleDefs[]

        // ==================== 持久性地面 hazard 区域组件（HazardZone）====================
        // 地面上的持久性区域效果（油沼减速、电网麻痹、火墙DoT等）
        // 站在区域内的敌人持续受影响，离开后效果消失
        public const int MAX_HAZARD_ZONES = 500;
        public bool[] HazardZoneActive = new bool[MAX_HAZARD_ZONES];
        public float[] HazardZoneX = new float[MAX_HAZARD_ZONES];
        public float[] HazardZoneY = new float[MAX_HAZARD_ZONES];
        public float[] HazardZoneRadius = new float[MAX_HAZARD_ZONES];
        public float[] HazardZoneMaxRadius = new float[MAX_HAZARD_ZONES];
        public int[] HazardZoneType = new int[MAX_HAZARD_ZONES];  // 0=none, 1=slow, 2=damage, 3=stun
        public float[] HazardZoneDuration = new float[MAX_HAZARD_ZONES];  // seconds remaining
        public float[] HazardZoneDamagePerSec = new float[MAX_HAZARD_ZONES];  // for type=2 (DoT)
        public float[] HazardZoneOwnerTowerId = new float[MAX_HAZARD_ZONES];  // tower that created this zone (-1 = none)
        private List<int> _activeHazardZoneIds = new List<int>();
        private int _nextHazardZoneId = 0;

        // ==================== 敌人尸体残留效果组件（CorpseGroundEffect）====================
        // 敌人死亡后在死亡位置生成的地面效果（毒池、黏液、火焰地带等）
        // 与 HazardZone 不同：HazardZone 由塔技能创建，CorpseEffect 由敌人死亡触发
        public const int MAX_CORPSE_EFFECTS = 2000;
        public bool[] CorpseEffectActive = new bool[MAX_CORPSE_EFFECTS];
        public float[] CorpseEffectX = new float[MAX_CORPSE_EFFECTS];
        public float[] CorpseEffectY = new float[MAX_CORPSE_EFFECTS];
        // EffectType: 0=Poison(DoT), 1=Slow, 2=Ice(freeze), 3=Fire(DoT), 4=Healing, 5=DamageBoost
        public int[] CorpseEffectType = new int[MAX_CORPSE_EFFECTS];
        public float[] CorpseEffectRadius = new float[MAX_CORPSE_EFFECTS];
        public float[] CorpseEffectDuration = new float[MAX_CORPSE_EFFECTS];  // seconds remaining
        public float[] CorpseEffectDamagePerTick = new float[MAX_CORPSE_EFFECTS];
        public float[] CorpseEffectSlowAmount = new float[MAX_CORPSE_EFFECTS];  // for Slow type
        public float[] CorpseEffectTickTimer = new float[MAX_CORPSE_EFFECTS];  // accumulator for tick timing
        public float[] CorpseEffectTickInterval = new float[MAX_CORPSE_EFFECTS];  // configured tick interval (from JSON)
        private List<int> _activeCorpseEffectIds = new List<int>();
        private int _nextCorpseEffectId = 0;

        // ==================== 亡灵法师尸体队列（Necromancer Corpse Queue）====================
        // Tracks recently-killed enemy corpses for necromancer resurrection.
        // MAX_CORPSE_AGE_SEC = corpses expire after this many seconds (e.g. 30s window).
        // MAX_CORPSE_QUEUE = circular buffer size.
        public const float MAX_CORPSE_AGE_SEC = 30f;
        public const int MAX_CORPSE_QUEUE = 2000;
        // CorpseX/Y: world position where the enemy died.
        // CorpseMonsterType: the monster type name string (for lookup by reanimated enemy).
        // CorpseOwnerId: the necromancer entity ID that owns this corpse (-1 if unclaimed).
        // CorpseHealth: remaining HP% of the reanimated minion (so it scales correctly).
        // CorpseDeathTime: timestamp (in sim seconds via GameManager.SimElapsed) when the enemy died.
        public float[] CorpseX = new float[MAX_CORPSE_QUEUE];
        public float[] CorpseY = new float[MAX_CORPSE_QUEUE];
        public string[] CorpseMonsterType = new string[MAX_CORPSE_QUEUE];
        public int[] CorpseOwnerId = new int[MAX_CORPSE_QUEUE];
        public float[] CorpseHealth = new float[MAX_CORPSE_QUEUE];
        public float[] CorpseDeathTime = new float[MAX_CORPSE_QUEUE];
        public bool[] CorpseActive = new bool[MAX_CORPSE_QUEUE];  // true = slot in use
        public bool[] CorpseReanimated = new bool[MAX_CORPSE_QUEUE];  // true = already reanimated, cannot be resurrected again
        private int _nextCorpseId = 0;

        // ==================== 技能组件的 SOA 存储 ====================
        public string[] SkillName = new string[MAX_PLAYERS];
        public float[] SkillDamageMultiplier = new float[MAX_PLAYERS];
        public int[] SkillAreaWidth = new int[MAX_PLAYERS];
        public int[] SkillAreaHeight = new int[MAX_PLAYERS];
        public int[] SkillAttackRange = new int[MAX_PLAYERS];
        public float[] SkillCooldown = new float[MAX_PLAYERS];
        public float[] SkillCurrentCooldown = new float[MAX_PLAYERS];

        // ==================== GAS 组件的 SOA 存储 ====================
        public const int MAX_ABILITIES_PER_ENTITY = 5;
        public const int MAX_ACTIVE_EFFECTS_PER_ENTITY = 8;

        // Per-entity ability instances (SOA: first dimension = entity, second = slot)
        public AbilityInstance[] AbilityInstances = new AbilityInstance[MAX_ENTITIES * MAX_ABILITIES_PER_ENTITY];
        public int[] AbilityCount = new int[MAX_ENTITIES]; // how many abilities this entity has

        // Per-entity active effects
        public AppliedEffect[] ActiveEffects = new AppliedEffect[MAX_ENTITIES * MAX_ACTIVE_EFFECTS_PER_ENTITY];
        public int[] ActiveEffectCount = new int[MAX_ENTITIES];

        // ==================== 实体管理 ====================
        public int PlayerEntityId { get; private set; } = 1;
        private List<int> _activeEnemyIds = new List<int>();
        private List<int> _activeTowerIds = new List<int>();
        private List<int> _activeObstacleIds = new List<int>();
        private int nextEntityId = 2; // 从 2 开始，1 是玩家
        public int CurrentFrame { get; private set; } = 0;

        // Expose as read-only references — zero allocation on read. All writes go through internal API (Add/Remove).
        // Caller responsibility: read-only access only. Consistent with ref-return patterns in ECS frameworks.
        public IReadOnlyList<int> ActiveEnemyIds => _activeEnemyIds;
        public IReadOnlyList<int> ActiveTowerIds => _activeTowerIds;
        public IReadOnlyList<int> ActiveObstacleIds => _activeObstacleIds;

        // Spatial Grid
        private readonly SpatialGrid _spatialGrid = new SpatialGrid();

        /// <summary>
        /// Rebuild spatial grid for current frame — O(enemies). Call once per frame,
        /// before TowerAttackSystem queries it.
        /// </summary>
        public void RebuildSpatialGrid()
        {
            _spatialGrid.Rebuild(this, _activeEnemyIds);
        }

        /// <summary>
        /// Get the spatial grid for range queries. Call only after RebuildSpatialGrid().
        /// </summary>
        public SpatialGrid SpatialGrid => _spatialGrid;

        /// <summary>
        /// Synchronize spatial grid dimensions with MapSystem. Call once during game initialization,
        /// before any enemies are added. Must match gameConfig.MapWidth/MapHeight.
        /// </summary>
        public void SetMapSize(int width, int height)
        {
            _spatialGrid.SetMapSize(width, height);
        }

        // ==================== 地形系统字段 ====================
        private int[] _mapTerrainGrid = Array.Empty<int>();
        private int _mapTerrainWidth;
        private int _mapTerrainHeight;

        public void InitTerrainGrid(int width, int height, int[][] terrainData)
        {
            _mapTerrainWidth = width;
            _mapTerrainHeight = height;
            _mapTerrainGrid = new int[width * height];
            for (int y = 0; y < height; y++)
            {
                if (terrainData != null && y < terrainData.Length && terrainData[y] != null)
                {
                    for (int x = 0; x < width; x++)
                        _mapTerrainGrid[y * width + x] = x < terrainData[y].Length ? terrainData[y][x] : 0;
                }
                else
                {
                    for (int x = 0; x < width; x++)
                        _mapTerrainGrid[y * width + x] = 0;
                }
            }
        }

        public int GetTerrain(int x, int y)
        {
            if (x < 0 || x >= _mapTerrainWidth || y < 0 || y >= _mapTerrainHeight)
                return 0;
            return _mapTerrainGrid[y * _mapTerrainWidth + x];
        }

        public int GetTerrainAtPosition(float worldX, float worldY)
        {
            return GetTerrain((int)worldX, (int)worldY);
        }

        private readonly ConcurrentStack<int> freeEntityIds = new ConcurrentStack<int>();
        private readonly Dictionary<int, string> entityNames = new Dictionary<int, string>();
        private readonly object entityNamesLock = new object(); // H-1: thread-safe access to entityNames
        private readonly object activeIdsLock = new object(); // BUG-2: thread-safe _activeEnemyIds/_activeTowerIds removal

        // For test setup only — use AddEnemy() / DestroyEntity() in production code
        public void AddActiveEnemyId(int id) => _activeEnemyIds.Add(id);
        public void AddActiveTowerId(int id) => _activeTowerIds.Add(id);

        // Ping-pong double-buffer: eliminates per-frame new ConcurrentBag<>() allocation
        private ConcurrentBag<(int enemyId, int playerId)>[] _deathQueue = new ConcurrentBag<(int, int)>[2];
        private int _deathQueueIdx = 0;

        // Tower kill queue: (enemyId, playerId, towerId) — parallel-safe
        private ConcurrentBag<(int, int, int)>[] _towerKillQueue = new ConcurrentBag<(int, int, int)>[2];
        private int _towerKillQueueIdx = 0;

        private bool _deathQueueResolved = false;

        // Combo kill callback — fired once per killed enemy during ResolveEnemiesKilledThisFrame.
        // Safe for serial use only (called from the resolve loop inside a foreach).
        public event Action<int, int> OnEnemyKilled;
        // Tower kill callback — fired when a tower scores the killing blow.
        // Parameters: (enemyId, playerId, towerId). Thread-safe, serial context.
        public event Action<int, int, int> OnTowerKill;

        public void BeginFrame()
        {
            // M-1 fix: detect programming error — BeginFrame called without Resolve
            if (!_deathQueue[_deathQueueIdx].IsEmpty && !_deathQueueResolved)
            {
                throw new InvalidOperationException(
                    "BeginFrame() called but ResolveEnemiesKilledThisFrame() was not called " +
                    "for the previous frame. Deaths may have been discarded.");
            }
            // Ping-pong: switch to alternate bag, clear it for new frame
            _deathQueueIdx = 1 - _deathQueueIdx;
            _deathQueue[_deathQueueIdx].Clear();
            _deathQueueResolved = false;
            CurrentFrame++;
        }

        /// <summary>
        /// Queue an enemy death from a parallel context. Thread-safe.
        /// Must be matched with a later call to ResolveEnemiesKilledThisFrame().
        /// </summary>
        public void QueueEnemyDeath(int enemyId, int playerId)
        {
            // H-11 fix: validate IDs are within valid range before queueing
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return;
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            _deathQueue[_deathQueueIdx].Add((enemyId, playerId));
        }

        /// <summary>
        /// Queue a tower kill event from a parallel or serial context.
        /// The towerId is used by TowerExperienceSystem to grant XP.
        /// </summary>
        public void QueueTowerKill(int enemyId, int playerId, int towerId)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return;
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            if (towerId < 0 || towerId >= MAX_ENTITIES) return;
            _towerKillQueue[_towerKillQueueIdx].Add((enemyId, playerId, towerId));
        }

        /// <summary>
        /// Serially process all queued tower kill events.
        /// Must be called after OnEnemyKilled but before the frame ends.
        /// </summary>
        private void ResolveTowerKillsThisFrame()
        {
            int readIdx = _towerKillQueueIdx;
            int writeIdx = 1 - _towerKillQueueIdx;
            _towerKillQueueIdx = writeIdx;
            foreach (var (enemyId, playerId, towerId) in _towerKillQueue[readIdx])
            {
                OnTowerKill?.Invoke(enemyId, playerId, towerId);
            }
            _towerKillQueue[writeIdx].Clear();
        }


        /// <summary>
        /// Serially process all queued enemy deaths this frame.
        /// Call once per turn AFTER all parallel systems have run.
        /// </summary>
        public void ResolveEnemiesKilledThisFrame()
        {
            int readIdx = _deathQueueIdx;
            int writeIdx = 1 - _deathQueueIdx;
            _deathQueueIdx = writeIdx;
            foreach (var (enemyId, playerId) in _deathQueue[readIdx])
            {
                if (!EnemyActive[enemyId]) continue; // already destroyed this frame
                TotalKills++;

                // Gold reward logic:
                // - Thief that escaped (HasStolenGold): no gold reward, but if killed later -> GoldOnReturn bonus
                // - Thief killed before escaping: normal gold reward (IsThief but HasStolenGold=false)
                // - Normal enemy: normal gold reward
                float goldReward;
                if (EnemyHasStolenGold[enemyId])
                {
                    // Thief was caught AFTER escaping — award GoldOnReturn bonus instead of normal reward
                    goldReward = EnemyGoldOnReturn[enemyId] * _goldKillMultiplier * _allIncomeMultKill;
                }
                else
                {
                    goldReward = EnemyGoldReward[enemyId] * _goldKillMultiplier * _allIncomeMultKill;
                }
                goldReward *= PlayerComboGoldMult[playerId];
                PlayerGold[playerId] += goldReward;
                if (_goldOnEliteKill > 0f && EnemyIsElite[enemyId])
                    PlayerGold[playerId] += _goldOnEliteKill;
                OnEnemyKilled?.Invoke(enemyId, playerId);
                // Fire tower kill event (for TowerExperienceSystem XP grant) — serial, safe
                ResolveTowerKillsThisFrame();
                DestroyEntity(enemyId);
            }
            _deathQueue[writeIdx].Clear();
            _deathQueueResolved = true;
        }

        public ComponentStore()
        {
            // Initialize ping-pong death queue buffers
            _deathQueue[0] = new ConcurrentBag<(int, int)>();
            _deathQueue[1] = new ConcurrentBag<(int, int)>();
            // Initialize tower kill queue buffers
            _towerKillQueue[0] = new ConcurrentBag<(int, int, int)>();
            _towerKillQueue[1] = new ConcurrentBag<(int, int, int)>();
            // Initialize per-enemy time scale to 1f (normal speed) for all slots
            // ChronoTowerSystem accumulates the minimum (slowest) from nearby towers each frame
            for (int i = 0; i < MAX_ENTITIES; i++)
                EnemyTimeScale[i] = 1f;
            // Initialize player buffs
            for (int i = 0; i < MAX_PLAYERS; i++)
            {
                PlayerBuffs[i] = new List<string>();
                PlayerUnlockedTechs[i] = new HashSet<string>();
                PlayerBuffFlags[i] = BuffType.None;
                PlayerStunDuration[i] = 0;
                PlayerSlowFactor[i] = 0f;
                PlayerSlowDuration[i] = 0;
                PlayerWaveIndex[i] = -1;
                PlayerEnemiesRemaining[i] = 0;
                PlayerIsWaveActive[i] = false;
                PlayerWaveTimer[i] = -1f;
                PlayerWaveCompleteGold[i] = 0f;
                PlayerShield[i] = 0f;
                PlayerShieldDuration[i] = 0f;
                PlayerThornsRatio[i] = 0f;
                PlayerComboGoldMult[i] = 1f;
                PlayerComboDamageMult[i] = 1f;
                PlayerComboKillStreak[i] = 0f;
                CurrentWaveMutatorId[i] = -1;
                GlobalTimeScale[i] = 1f;
                GlobalTimeScaleDuration[i] = 0f;
                PlayerBankedGold[i] = 0f;
                PlayerInterestRate[i] = 0.05f; // default 5% interest per wave
                EnemiesLeakedThisWave[i] = 0;
                AdaptiveDifficultyLevel[i] = 1.0f;
                AdaptiveDifficultyScore[i] = 0f;
                GlobalFogDensity[i] = 1f; // default fog density (no visibility reduction)
            }
        }

        public int CreateEntity()
        {
            // H-1 fix: ConcurrentStack is thread-safe
            if (freeEntityIds.TryPop(out int entityId))
            {
                if (entityId >= 0 && entityId < MAX_ENTITIES)
                {
                    EnemyActionEnum[entityId] = EnemyActionType.None;
                    // Ensure recycled entity has clean stealth multiplier (DestroyEntity already reset it,
                    // but we set it explicitly here to guard against any future code that might
                    // skip DestroyEntity's stealth reset while still using the free list).
                    EnemyStealthMultiplier[entityId] = 1f;
                    return entityId;
                }
            }
            int entityId2 = Interlocked.Increment(ref nextEntityId) - 1;
            if (entityId2 >= MAX_ENTITIES) return -1;
            EnemyActionEnum[entityId2] = EnemyActionType.None;
            // Newly allocated IDs start with default float[] = 0f; set to 1f so that
            // EnemyAISystem attack methods multiply correctly (stealth_mult=1f means no bonus).
            EnemyStealthMultiplier[entityId2] = 1f;
            return entityId2;
        }

        public void DestroyEntity(int entityId)
        {
            // ── Phase 1: determine archetype ────────────────────────────────────────
            bool wasEnemy = EnemyActive[entityId];
            bool wasTower = TowerActive[entityId];

            // ── Phase 2: shared state cleanup ─────────────────────────────────────
            PositionActive[entityId] = false;
            // H-1 fix: lock around dictionary removal (thread-safe)
            lock (entityNamesLock)
            {
                entityNames.Remove(entityId);
            }

            // ── Phase 3: archetype-specific cleanup ────────────────────────────────
            if (wasEnemy)
            {
                lock (activeIdsLock) { _activeEnemyIds.Remove(entityId); }
                EnemyActive[entityId] = false;

                EnemyHealth[entityId] = 0f;
                EnemyMaxHealth[entityId] = 0f;
                EnemyMoveSpeed[entityId] = 0f;
                EnemyDamage[entityId] = 0f;
                EnemyGoldReward[entityId] = 0;
                EnemyWaveNumber[entityId] = 0;
                EnemyChargeParam[entityId] = 0f;
                EnemyBuffDamageBonus[entityId] = 0f;
                EnemyBuffDurationLeft[entityId] = 0f;
                EnemyBehaviorTree[entityId] = null;
                EnemyTypeName[entityId] = null;
                EnemyAIAction[entityId] = null;
                EnemyCastAbilityId[entityId] = null;
                EnemyActionEnum[entityId] = EnemyActionType.None;
                EnemyAIChargeCounter[entityId] = 0;
                EnemyAILastAttackTurn[entityId] = 0;
                EnemyArmor[entityId] = 0f;
                EnemyStunFlag[entityId] = false;
                EnemyStunDurationLeft[entityId] = 0f;
                EnemySlowFactor[entityId] = 0f;
                EnemyTerrainMoveSpeedMult[entityId] = 1f;
                EnemyMoveSpeedBase[entityId] = 0f;
                EnemySlowDurationLeft[entityId] = 0f;
                EnemyKnockbackForceLeft[entityId] = 0f;
                EnemyIsElite[entityId] = false;
                EnemyIsFlying[entityId] = false;
                EnemyFlightHeight[entityId] = 0f;
                EnemyCanLand[entityId] = false;
                EnemyStealthMultiplier[entityId] = 1f;
                EnemyShield[entityId] = 0f;
                EnemyThornsRatio[entityId] = 0f;
                EnemyArmorShredStacks[entityId] = 0f;
                EnemyArmorShredDuration[entityId] = 0f;
                // Fear / Taunt / Charm fields
                EnemyFearDurationLeft[entityId] = 0f;
                EnemyTauntTargetId[entityId] = -1;
                EnemyCharmDurationLeft[entityId] = 0f;
                // Nest / spawner fields
                NestDefId[entityId] = -1;
                NestHealth[entityId] = 0f;
                NestMaxHealth[entityId] = 0f;
                NestSpawnTimer[entityId] = 0f;
                NestSpawnInterval[entityId] = 0f;
                NestMonsterTypeStr[entityId] = null;
                NestMaxAlive[entityId] = 0;
                NestActiveCount[entityId] = 0;
                NestOriginId[entityId] = -1;
                // Path / waypoint fields
                EnemyPathId[entityId] = -1;
                EnemyPathNodeIndex[entityId] = 0;
                // Teleport / portal fields
                EnemyTeleportCooldown[entityId] = 0f;
                EnemyTeleportDestinationX[entityId] = 0f;
                EnemyTeleportDestinationY[entityId] = 0f;
                EnemyTeleportType[entityId] = 0;
                // Resistance fields
                EnemyStunResistance[entityId] = 0f;
                EnemyFreezeResistance[entityId] = 0f;
                EnemySlowResistance[entityId] = 0f;
                EnemyKnockbackResistance[entityId] = 0f;
                EnemyDamageResistance[entityId] = 0f;
                // Curse debuff fields (applied by curse towers)
                EnemyCurseDmgReduction[entityId] = 0f;
                EnemyCurseSpeedReduction[entityId] = 0f;
                EnemyCurseArmorReduction[entityId] = 0f;
                EnemyCurseDmgTakenIncrease[entityId] = 0f;
// Pull debuff field (applied by pull towers)
                EnemyIsBeingPulled[entityId] = false;
                // Burrow/underground fields (reset on entity destruction)
                EnemyIsBurrowed[entityId] = false;
                EnemyBurrowTimer[entityId] = 0f;
                EnemyBurrowCooldown[entityId] = 0f;
                EnemyBurrowCooldownRef[entityId] = 0f;
                EnemyBurrowSpeedMult[entityId] = 1f;
                EnemyBurrowEmergeDamage[entityId] = 0f;
                EnemyBurrowRadius[entityId] = 0f;
                // Necromancer / resurrect fields (reset on entity destruction)
                EnemyCanResurrect[entityId] = false;
                EnemyResurrectRange[entityId] = 0f;
                EnemyResurrectCooldown[entityId] = 0f;
                EnemyResurrectCooldownRef[entityId] = 0f;
                EnemyResurrectHpMult[entityId] = 0f;
                EnemyMaxResurrectCount[entityId] = 0;
                EnemyResurrectCorpseAgeLimit[entityId] = 0f;
                EnemyIsReanimated[entityId] = false;
                EnemyOwnerId[entityId] = -1;
                // Bleed/rupture debuff fields (applied by Slash/Pierce towers)
                EnemyBleedStacks[entityId] = 0f;
                EnemyBleedDamagePerStack[entityId] = 0f;
                EnemyBleedTimer[entityId] = 0f;
                EnemyBleedMaxStacks[entityId] = 0f;
                EnemyBleedResistance[entityId] = 0f;
                EnemyBleedDurationLeft[entityId] = 0f;
                // Boss phase / enrage fields
                EnemyBossPhase[entityId] = 0;
                EnemyPhaseThresholds[entityId] = null;
                EnemyEnrageTimer[entityId] = 0f;
                EnemyIsEnraged[entityId] = false;
                // Invulnerable phase fields
                EnemyIsInvulnerable[entityId] = false;
                EnemyInvulnerablePhaseName[entityId] = null;
// Freeze fields (shared with stun — no separate fields needed, cleanup via StunDurationLeft/StunFlag above)
                // Life Link fields (shared damage link)
                EnemyIsLifeLinker[entityId] = false;
                EnemyLifeLinkDefId[entityId] = -1;
                EnemyLinkedEnemyId[entityId] = -1;
                EnemyLifeLinkRatio[entityId] = 0f;
                EnemyLifeLinkCooldownLeft[entityId] = 0f;
                EnemyIsLinked[entityId] = false;
            }

            if (wasTower)
            {
                lock (activeIdsLock) { _activeTowerIds.Remove(entityId); }
                TowerActive[entityId] = false;
                TowerTargetingMode[entityId] = 0;
                TowerType[entityId] = null;
                TowerAttackDamage[entityId] = 0f;
                TowerRange[entityId] = 0;
                TowerAttackSpeed[entityId] = 0f;
                TowerLevel[entityId] = 0;
                TowerUpgradeCost[entityId] = 0f;
                TowerUpgradePathId[entityId] = null;
                TowerFusionTier[entityId] = 0;
                TowerLastAttackTime[entityId] = 0f;
                TowerStunChance[entityId] = 0f;
                TowerSlowAmount[entityId] = 0f;
                TowerSlowDuration[entityId] = 0f;
                TowerCanHitAir[entityId] = false;
                TowerCanHitGround[entityId] = false;
                // Aura tower fields
                TowerIsAuraTower[entityId] = false;
                TowerAuraRadius[entityId] = 0f;
                TowerAuraAttackSpeedBonus[entityId] = 0f;
                TowerAuraDamageBonus[entityId] = 0f;
                // Dispel fields
                TowerIsDispelled[entityId] = false;
                TowerDispelTimer[entityId] = 0f;
                TowerDispelImmunityTimer[entityId] = 0f;
                // Curse tower fields
                TowerIsCurseTower[entityId] = false;
                TowerCurseRadius[entityId] = 0f;
                TowerCurseDmgReduction[entityId] = 0f;
                TowerCurseSpeedReduction[entityId] = 0f;
                TowerCurseArmorReduction[entityId] = 0f;
                TowerCurseDmgTakenIncrease[entityId] = 0f;
                // Ammo fields
                TowerCurrentAmmo[entityId] = 0;
                TowerMaxAmmo[entityId] = 0;
                TowerReloadTime[entityId] = 0f;
                TowerReloadProgress[entityId] = 0f;
                TowerIsReloading[entityId] = false;
                TowerProjectileHoming[entityId] = false;
                // Scatter/multicast fields
                TowerProjectileCount[entityId] = 0;
                TowerScatterAngle[entityId] = 0f;
                // Overcharge fields
                TowerIsOvercharged[entityId] = false;
                TowerOverchargeDuration[entityId] = 0f;
                TowerOverchargeCooldown[entityId] = 0f;
                TowerCanOvercharge[entityId] = false;
            }

            // ── Phase 4: recycle ID ────────────────────────────────────────────────
            freeEntityIds.Push(entityId);
        }

        public int NextEntityId => nextEntityId;

        public string GetEntityName(int entityId)
        {
            return GetName(entityId);
        }

        public string GetName(int entityId)
        {
            // H-1 fix: lock around dictionary read (thread-safe)
            // Bug#29 fix: TryGetValue is a single hash lookup vs ContainsKey+indexer double lookup
            lock (entityNamesLock)
            {
                if (entityNames.TryGetValue(entityId, out string name))
                {
                    return name;
                }
            }
            return $"Entity_{entityId}";
        }

        public void SetEntityName(int entityId, string name)
        {
            // H-1 fix: lock around dictionary write (thread-safe)
            lock (entityNamesLock)
            {
                entityNames[entityId] = name;
            }
        }

        // ==================== 位置组件访问 ====================

        public void AddPosition(int entityId, float x, float y)
        {
            if (entityId < 0 || entityId >= MAX_ENTITIES) return;

            PositionX[entityId] = x;
            PositionY[entityId] = y;
            PositionActive[entityId] = true;
        }

        public void SetPosition(int entityId, float x, float y)
        {
            if (entityId < 0 || entityId >= MAX_ENTITIES) return;

            PositionX[entityId] = x;
            PositionY[entityId] = y;
        }

// ==================== 玩家组件访问 ====================

        public void AddPlayer(int entityId, float attackRange, float attackSpeed, float attackDamage, int currentLevel, int baseLives = 10)
        {
            if (entityId < 0 || entityId >= MAX_PLAYERS) return;

            PlayerAttackRange[entityId] = attackRange;
            PlayerAttackSpeed[entityId] = attackSpeed;
            PlayerAttackDamage[entityId] = attackDamage;
            PlayerCurrentLevel[entityId] = currentLevel;
            PlayerGold[entityId] = 0f;
            PlayerUpgradeThreshold[entityId] = 1000f;  // 提高到 1000 以更快升级测试技能
            PlayerBuffs[entityId] = new List<string>();
            PlayerBuffFlags[entityId] = BuffType.None;
            PlayerBaseLives[entityId] = baseLives;
            PlayerMaxBaseLives[entityId] = baseLives;
            // Weather: default to clear (type 0), intensity 0
            CurrentWeather[entityId] = 0;
            WeatherIntensity[entityId] = 0f;
            WeatherTimer[entityId] = -1f;

            PlayerEntityId = entityId;
        }

        public float GetPlayerAttackRange(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 0f;
            return PlayerAttackRange[playerId];
        }

        public void SetPlayerAttackRange(int playerId, float range)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            PlayerAttackRange[playerId] = range;
        }

        public float GetPlayerAttackSpeed(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 0f;
            return PlayerAttackSpeed[playerId];
        }

        public float GetPlayerAttackDamage(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 0f;
            return PlayerAttackDamage[playerId];
        }

        public void SetPlayerAttackDamage(int playerId, float damage)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            PlayerAttackDamage[playerId] = damage;
        }

        public float GetPlayerGold(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 0f;
            return PlayerGold[playerId];
}

        public float GetPlayerTotalGold(int playerId)
        {
            return GetPlayerGold(playerId);
        }

        public void SetPlayerGold(int playerId, float gold)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            PlayerGold[playerId] = gold;
        }

        /// <summary>
        /// Remove gold from player (thief steal, penalty, etc.). Clamps to 0.
        /// </summary>
        public void LoseGold(int playerId, float amount)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS || amount <= 0f) return;
            float current = PlayerGold[playerId];
            float newGold = Math.Max(0f, current - amount);
            PlayerGold[playerId] = newGold;
        }

        public int GetPlayerLevel(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 0;
            return PlayerCurrentLevel[playerId];
        }

        public void SetPlayerLevel(int playerId, int level)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            PlayerCurrentLevel[playerId] = level;
        }

        public List<string> GetPlayerBuffs(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return new List<string>();
            // ✅ Bug#17 fix: return a defensive copy to prevent external mutation
            return new List<string>(PlayerBuffs[playerId]);
        }

        public void AddPlayerBuff(int playerId, string buff)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            PlayerBuffs[playerId].Add(buff);
        }

        // ── O(1) buff flag helpers (perf: eliminates per-frame GC) ──────────
        public void AddBuff(int playerId, BuffType buff)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            PlayerBuffFlags[playerId] |= buff;
        }

        public bool HasBuff(int playerId, BuffType buff)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return false;
            return (PlayerBuffFlags[playerId] & buff) != 0;
        }

        public float GetAttackBuffMultiplier(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 1f;
            return (PlayerBuffFlags[playerId] & BuffType.AttackBoost) != 0 ? 1.1f : 1f;
        }

        public bool HasCritRateBuff(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return false;
            return (PlayerBuffFlags[playerId] & BuffType.CritRateBoost) != 0;
        }

        // ── O(1) enemy affix flag helpers ─────────────────────────────────
        public bool HasAffix(int enemyId, BuffType affix)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return false;
            return (EnemyAffixFlags[enemyId] & affix) != 0;
        }

        public float GetPlayerUpgradeThreshold(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 0f;
            return PlayerUpgradeThreshold[playerId];
        }

        public void SetPlayerUpgradeThreshold(int playerId, float threshold)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            PlayerUpgradeThreshold[playerId] = threshold;
        }

        // ==================== 敌人组件访问 ====================

        public int AddEnemy(float startX, float startY, float moveSpeed, float health, float maxHealth, float damage, int goldReward, int waveNumber, string fullName = null, float armor = 0f, float shield = 0f, float magicResist = 0f)
        {
            int entityId = CreateEntity();

            if (entityId < 0 || entityId >= MAX_ENTITIES) 
            {
                return -1;
            }

            PositionX[entityId] = startX;
            PositionY[entityId] = startY;
            PositionActive[entityId] = true;

            EnemyHealth[entityId] = health;
            EnemyMaxHealth[entityId] = maxHealth;
            EnemyMoveSpeed[entityId] = moveSpeed;
            EnemyMoveSpeedBase[entityId] = moveSpeed;
            EnemyDamage[entityId] = damage;
            EnemyGoldReward[entityId] = goldReward;
            EnemyWaveNumber[entityId] = waveNumber;
            EnemyActive[entityId] = true;
            // Path/waypoint: default -1 = no path (use straight Y-axis movement)
            EnemyPathId[entityId] = -1;
            EnemyPathNodeIndex[entityId] = 0;
            EnemySpawnFrame[entityId] = CurrentFrame;
            EnemyArmor[entityId] = armor;
            EnemyMagicResist[entityId] = magicResist;
            EnemyShield[entityId] = shield;  // configurable initial shield
            EnemyEvasion[entityId] = 0f;  // default to no evasion
            // Vanguard: default not a vanguard (false = not protecting anyone)
            EnemyIsVanguard[entityId] = false;
            EnemyVanguardCoverRange[entityId] = 0f;
            EnemyVanguardDmgTransfer[entityId] = 0f;
            EnemyVanguardCoverCount[entityId] = 0;
            // Thief: default not a gold thief
            EnemyCanStealGold[entityId] = false;
            EnemyStealAmount[entityId] = 0f;
            EnemyStolenGold[entityId] = 0f;
            EnemyGoldOnReturn[entityId] = 0f;
            EnemyHasStolenGold[entityId] = false;
            // Teleport: default no cooldown (ready), no destination, type=0 (none)
            EnemyTeleportCooldown[entityId] = 0f;
            EnemyTeleportDestinationX[entityId] = 0f;
            EnemyTeleportDestinationY[entityId] = 0f;
            EnemyTeleportType[entityId] = 0;

            // 缓存怪物类型名（如 "NormalL1W1E0" -> "Normal"），避免每帧解析
            // 同时检测 [ELITE]/[BOSS] 前缀来正确标记精英/首领
            if (fullName != null)
            {
                bool isElite = fullName.StartsWith("[ELITE]");
                bool isBoss = fullName.StartsWith("[BOSS]");
                bool isFlying = false; // default: enemies are ground units
                EnemyIsElite[entityId] = isElite;
                EnemyIsFlying[entityId] = isFlying;
                // 剥除 [BOSS]/[ELITE] 前缀，保留基础类型名
                string nameToStore = fullName;
                if (isElite || isBoss)
                {
                    int spaceIdx = fullName.IndexOf(' ');
                    nameToStore = (spaceIdx > 0) ? fullName.Substring(spaceIdx + 1) : fullName;
                }
                int sepIdx = nameToStore.IndexOf('L');
                EnemyTypeName[entityId] = (sepIdx > 0) ? nameToStore.Substring(0, sepIdx) : nameToStore;
            }

            // H-race fix: lock Add to match Remove in DestroyEntity which uses lock(activeIdsLock)
            lock (activeIdsLock) { _activeEnemyIds.Add(entityId); }
            return entityId;
        }

        /// <summary>
        /// Add a tower with default "standard" upgrade path.
        /// </summary>
        public void AddTower(int entityId, string type, float damage, int range, float speed, int level, float cost)
            => AddTower(entityId, type, damage, range, speed, level, cost, "standard", 0f, 0f, 0f);

        /// <summary>
        /// Add a tower with a specific upgrade path.
        /// </summary>
        public void AddTower(int entityId, string type, float damage, int range, float speed, int level, float cost, string upgradePathId)
            => AddTower(entityId, type, damage, range, speed, level, cost, upgradePathId, 0f, 0f, 0f);

        /// <summary>
        /// Add a tower with debuff parameters.
        /// </summary>
        public void AddTower(int entityId, string type, float damage, int range, float speed, int level, float cost, string upgradePathId, float stunChance, float slowAmount, float slowDuration, int damageType = 0, float turnRate = 0f)
        {
            if (entityId < 0 || entityId >= MAX_ENTITIES) return;
            TowerType[entityId] = type;
            TowerAttackDamage[entityId] = damage;
            TowerRange[entityId] = range;
            TowerAttackSpeed[entityId] = speed;
            TowerLevel[entityId] = level;
            TowerUpgradeCost[entityId] = cost;
            TowerUpgradePathId[entityId] = upgradePathId ?? "standard";
            TowerFusionTier[entityId] = 0;
            TowerActive[entityId] = true;
            TowerLastAttackTime[entityId] = 0f;
            TowerStunChance[entityId] = stunChance;
            TowerSlowAmount[entityId] = slowAmount;
            TowerSlowDuration[entityId] = slowDuration;
            // Aura tower fields: default to non-aura (false/0)
            TowerIsAuraTower[entityId] = false;
            TowerAuraRadius[entityId] = 0f;
            TowerAuraAttackSpeedBonus[entityId] = 0f;
            TowerAuraDamageBonus[entityId] = 0f;
            TowerCanHitAir[entityId] = true;
            TowerCanHitGround[entityId] = true;
            // Curse tower fields: default to non-curse (false/0)
            TowerIsCurseTower[entityId] = false;
            TowerCurseRadius[entityId] = 0f;
            TowerCurseDmgReduction[entityId] = 0f;
            TowerCurseSpeedReduction[entityId] = 0f;
            TowerCurseArmorReduction[entityId] = 0f;
            TowerCurseDmgTakenIncrease[entityId] = 0f;
            // Pull tower fields: default to non-pull (false/0)
            TowerIsPullTower[entityId] = false;
            TowerPullStrength[entityId] = 0f;
            TowerPullRadius[entityId] = 0f;
            TowerPullCooldown[entityId] = 0f;
            TowerPullTimer[entityId] = 0f;
            // Bleed tower fields: default to non-bleed (false/0)
            TowerIsBleedTower[entityId] = false;
            TowerBleedStacksPerHit[entityId] = 0f;
            TowerBleedDmgPct[entityId] = 0f;
            TowerBleedTickInterval[entityId] = 1f;
            TowerBleedMaxStacks[entityId] = 0f;
            TowerBleedDuration[entityId] = 0f;
            // Ammo fields: default to unlimited (maxAmmo=0 means infinite)
            TowerCurrentAmmo[entityId] = 0;
            TowerMaxAmmo[entityId] = 0;
            TowerReloadTime[entityId] = 0f;
            TowerReloadProgress[entityId] = 0f;
            TowerIsReloading[entityId] = false;
            TowerArmorShredBonus[entityId] = 0f;
            TowerShieldBreakBonus[entityId] = 0f;
            TowerAccuracy[entityId] = 1f;  // default to always-hit
            // Scatter/multicast fields: default to single shot (1 projectile, 0 spread)
            TowerProjectileCount[entityId] = 1;
            TowerScatterAngle[entityId] = 0f;
            // Bouncing projectile fields: default to no bounce
            TowerBouncesRemaining[entityId] = 0;
            TowerBounceRange[entityId] = 0f;
            TowerBounceDamageFalloff[entityId] = 1f;
            TowerBounceHitsRemaining[entityId] = 0;
            // Piercing projectile fields: default to no pierce
            TowerProjectilePierceCount[entityId] = 0;
            TowerProjectilePierceDmgFalloff[entityId] = 1f;
            TowerPierceHitsRemaining[entityId] = 0;
            // Fragmentation/projectile split fields: default to no fragmentation
            TowerProjectileFragmentCount[entityId] = 0;
            TowerProjectileFragmentRange[entityId] = 0f;
            TowerProjectileFragmentDmgMult[entityId] = 1f;
            // Overcharge fields: default to inactive (no overcharge, cooldown=0)
            TowerIsOvercharged[entityId] = false;
            TowerOverchargeDuration[entityId] = 0f;
            TowerOverchargeCooldown[entityId] = 0f;
            TowerCanOvercharge[entityId] = false;
            // Knockback fields: default to no knockback (0 force = no effect)
            TowerKnockbackForce[entityId] = 0f;
            TowerKnockbackRadius[entityId] = 0f;
            // Construction fields: default to not in construction (active immediately)
            TowerIsConstructing[entityId] = false;
            TowerConstructionProgress[entityId] = 1f; // start at 100% (complete)
            TowerConstructionTime[entityId] = 0f;
            TowerConstructionHP[entityId] = 0f;
            TowerConstructionMaxHP[entityId] = 0f;
            TowerIsVulnerableDuringConstruction[entityId] = false;
            // Damage type and turn rate from config
            TowerDamageType[entityId] = damageType;
            TowerTurnRate[entityId] = turnRate;
            // Fog of War: default to no fog restriction (visionRadius=0 means see all)
            TowerVisionRadius[entityId] = 0f;
            // M-race fix: lock Add to match Remove in DestroyEntity which uses lock(activeIdsLock)
            lock (activeIdsLock) { _activeTowerIds.Add(entityId); }
        }

        public void RemoveTower(int entityId)
        {
            if (entityId < 0 || entityId >= MAX_ENTITIES) return;
            TowerActive[entityId] = false;
            TowerUpgradePathId[entityId] = null;
            TowerFusionTier[entityId] = 0;
            TowerSelected[entityId] = false;
            // Chrono tower fields
            TowerIsChronoTower[entityId] = false;
            TowerTimeFieldRadius[entityId] = 0f;
            TowerTimeScale[entityId] = 0f;
            // Aura tower fields reset
            TowerIsAuraTower[entityId] = false;
            TowerAuraRadius[entityId] = 0f;
            TowerAuraAttackSpeedBonus[entityId] = 0f;
            TowerAuraDamageBonus[entityId] = 0f;
            // Dispel fields reset
            TowerIsDispelled[entityId] = false;
            TowerDispelTimer[entityId] = 0f;
            TowerDispelImmunityTimer[entityId] = 0f;
            // Curse tower fields reset
            TowerIsCurseTower[entityId] = false;
            TowerCurseRadius[entityId] = 0f;
            TowerCurseDmgReduction[entityId] = 0f;
            TowerCurseSpeedReduction[entityId] = 0f;
            TowerCurseArmorReduction[entityId] = 0f;
            TowerCurseDmgTakenIncrease[entityId] = 0f;
            // Pull tower fields reset
            TowerIsPullTower[entityId] = false;
            TowerPullStrength[entityId] = 0f;
            TowerPullRadius[entityId] = 0f;
            TowerPullCooldown[entityId] = 0f;
            TowerPullTimer[entityId] = 0f;
            // Bleed tower fields reset
            TowerIsBleedTower[entityId] = false;
            TowerBleedStacksPerHit[entityId] = 0f;
            TowerBleedDmgPct[entityId] = 0f;
            TowerBleedTickInterval[entityId] = 1f;
            TowerBleedMaxStacks[entityId] = 0f;
            TowerBleedDuration[entityId] = 0f;
            // Ammo fields reset
            TowerCurrentAmmo[entityId] = 0;
            TowerMaxAmmo[entityId] = 0;
            TowerReloadTime[entityId] = 0f;
            TowerReloadProgress[entityId] = 0f;
            TowerIsReloading[entityId] = false;
            TowerProjectileHoming[entityId] = false;
            TowerBouncesRemaining[entityId] = 0;
            TowerProjectileFragmentCount[entityId] = 0;
            TowerProjectileFragmentRange[entityId] = 0f;
            TowerProjectileFragmentDmgMult[entityId] = 1f;
            TowerArmorShredBonus[entityId] = 0f;
            TowerShieldBreakBonus[entityId] = 0f;
            TowerDamageType[entityId] = 0;
            // Construction fields reset
            TowerIsConstructing[entityId] = false;
            TowerConstructionProgress[entityId] = 1f;
            TowerConstructionTime[entityId] = 0f;
            TowerConstructionHP[entityId] = 0f;
            TowerConstructionMaxHP[entityId] = 0f;
            TowerIsVulnerableDuringConstruction[entityId] = false;
            // Fog of War fields reset
            TowerVisionRadius[entityId] = 0f;
            TowerVisibilityByTower.Remove(entityId); // remove visibility data for this tower
            lock (activeIdsLock) { _activeTowerIds.Remove(entityId); }
        }

        // ==================== 路障管理 ====================
        public void AddObstacle(int obstacleId, int typeId, float x, float y, float maxHealth)
        {
            if (obstacleId < 0 || obstacleId >= MAX_OBSTACLES) return;
            ObstacleActive[obstacleId] = true;
            ObstacleType[obstacleId] = typeId;
            ObstacleX[obstacleId] = x;
            ObstacleY[obstacleId] = y;
            ObstacleHealth[obstacleId] = maxHealth;
            ObstacleMaxHealth[obstacleId] = maxHealth;
            _activeObstacleIds.Add(obstacleId);
        }

        public void RemoveObstacle(int obstacleId)
        {
            if (obstacleId < 0 || obstacleId >= MAX_OBSTACLES) return;
            ObstacleActive[obstacleId] = false;
            ObstacleHealth[obstacleId] = 0f;
            ObstacleMaxHealth[obstacleId] = 0f;
            ObstacleX[obstacleId] = 0f;
            ObstacleY[obstacleId] = 0f;
            ObstacleType[obstacleId] = -1;
            _activeObstacleIds.Remove(obstacleId);
        }

        // ==================== 持久性地面 HazardZone 管理 ====================
        /// <summary>Add a hazard zone at the given position with specified type and parameters.</summary>
        public int AddHazardZone(float x, float y, float radius, int hazardType, float duration, float damagePerSec = 0f, int ownerTowerId = -1)
        {
            int zoneId = -1;
            lock (activeIdsLock)
            {
                // Find a free slot
                for (int i = 0; i < MAX_HAZARD_ZONES; i++)
                {
                    int candidateId = (_nextHazardZoneId + i) % MAX_HAZARD_ZONES;
                    if (!HazardZoneActive[candidateId])
                    {
                        zoneId = candidateId;
                        _nextHazardZoneId = (candidateId + 1) % MAX_HAZARD_ZONES;
                        break;
                    }
                }
            }
            if (zoneId < 0) return -1; // no free slots

            HazardZoneActive[zoneId] = true;
            HazardZoneX[zoneId] = x;
            HazardZoneY[zoneId] = y;
            HazardZoneRadius[zoneId] = radius;
            HazardZoneMaxRadius[zoneId] = radius;
            HazardZoneType[zoneId] = hazardType;
            HazardZoneDuration[zoneId] = duration;
            HazardZoneDamagePerSec[zoneId] = damagePerSec;
            HazardZoneOwnerTowerId[zoneId] = ownerTowerId;
            _activeHazardZoneIds.Add(zoneId);
            return zoneId;
        }

        /// <summary>Remove a hazard zone by ID.</summary>
        public void RemoveHazardZone(int zoneId)
        {
            if (zoneId < 0 || zoneId >= MAX_HAZARD_ZONES) return;
            if (!HazardZoneActive[zoneId]) return;
            HazardZoneActive[zoneId] = false;
            HazardZoneX[zoneId] = 0f;
            HazardZoneY[zoneId] = 0f;
            HazardZoneRadius[zoneId] = 0f;
            HazardZoneMaxRadius[zoneId] = 0f;
            HazardZoneType[zoneId] = 0;
            HazardZoneDuration[zoneId] = 0f;
            HazardZoneDamagePerSec[zoneId] = 0f;
            HazardZoneOwnerTowerId[zoneId] = -1;
            _activeHazardZoneIds.Remove(zoneId);
        }

        /// <summary>Get list of active hazard zone IDs. O(n) over active zones, zero GC.</summary>
        public List<int> GetCachedActiveHazardZoneIds()
        {
            return _activeHazardZoneIds;
        }

        // ==================== 尸体残留效果（CorpseEffect）管理 API ====================

/// <summary>
/// Queue a corpse ground effect at a position when an enemy dies.
/// Called from EnemyFissionSystem or ResolveEnemiesKilledThisFrame.
/// Returns zone ID or -1 if no free slots.
/// </summary>
public int AddCorpseEffect(float x, float y, int effectType, float radius, float duration, float damagePerTick = 0f, float slowAmount = 1f, float tickInterval = 1f)
        {
            int zoneId = -1;
            for (int i = 0; i < MAX_CORPSE_EFFECTS; i++)
            {
                int candidateId = (_nextCorpseEffectId + i) % MAX_CORPSE_EFFECTS;
                if (!CorpseEffectActive[candidateId])
                {
                    zoneId = candidateId;
                    _nextCorpseEffectId = (candidateId + 1) % MAX_CORPSE_EFFECTS;
                    break;
                }
            }
            if (zoneId < 0) return -1; // no free slots

            CorpseEffectActive[zoneId] = true;
            CorpseEffectX[zoneId] = x;
            CorpseEffectY[zoneId] = y;
            CorpseEffectType[zoneId] = effectType;
            CorpseEffectRadius[zoneId] = radius;
            CorpseEffectDuration[zoneId] = duration;
            CorpseEffectDamagePerTick[zoneId] = damagePerTick;
            CorpseEffectSlowAmount[zoneId] = slowAmount;
            CorpseEffectTickTimer[zoneId] = 0f;
            CorpseEffectTickInterval[zoneId] = tickInterval;
            _activeCorpseEffectIds.Add(zoneId);
            return zoneId;
        }

        /// <summary>Remove a corpse effect by ID.</summary>
        public void RemoveCorpseEffect(int zoneId)
        {
            if (zoneId < 0 || zoneId >= MAX_CORPSE_EFFECTS) return;
            if (!CorpseEffectActive[zoneId]) return;
            CorpseEffectActive[zoneId] = false;
            CorpseEffectX[zoneId] = 0f;
            CorpseEffectY[zoneId] = 0f;
            CorpseEffectType[zoneId] = 0;
            CorpseEffectRadius[zoneId] = 0f;
            CorpseEffectDuration[zoneId] = 0f;
            CorpseEffectDamagePerTick[zoneId] = 0f;
            CorpseEffectSlowAmount[zoneId] = 1f;
            CorpseEffectTickTimer[zoneId] = 0f;
            CorpseEffectTickInterval[zoneId] = 1f;
            _activeCorpseEffectIds.Remove(zoneId);
        }

        /// <summary>Get list of active corpse effect IDs. O(n) over active zones, zero GC.</summary>
        public List<int> GetCachedActiveCorpseEffectIds()
        {
            return _activeCorpseEffectIds;
        }

        // ==================== 亡灵法师尸体队列 API ====================
        /// <summary>
        /// Queue a killed enemy as a corpse for potential necromancer resurrection.
        /// Uses a circular buffer. Returns corpse slot index (0 to MAX_CORPSE_QUEUE-1), or -1 if full.
        /// </summary>
        public int NecromancerQueueCorpse(int enemyId, float x, float y, string monsterType, float hpPercent, float simTime)
        {
            for (int i = 0; i < MAX_CORPSE_QUEUE; i++)
            {
                int candidateId = (_nextCorpseId + i) % MAX_CORPSE_QUEUE;
                if (CorpseActive[candidateId]) continue;

                CorpseX[candidateId] = x;
                CorpseY[candidateId] = y;
                CorpseMonsterType[candidateId] = monsterType;
                CorpseOwnerId[candidateId] = -1; // unclaimed
                CorpseHealth[candidateId] = hpPercent;
                CorpseDeathTime[candidateId] = simTime;
                CorpseActive[candidateId] = true;
                CorpseReanimated[candidateId] = false;
                _nextCorpseId = (candidateId + 1) % MAX_CORPSE_QUEUE;
                return candidateId;
            }
            return -1; // queue full
        }

        /// <summary>
        /// Expire old corpses past the age limit. Called from NecromancerSystem or cleanup.
        /// </summary>
        public void ExpireCorpse(int corpseId)
        {
            if (corpseId < 0 || corpseId >= MAX_CORPSE_QUEUE) return;
            if (!CorpseActive[corpseId]) return;
            CorpseActive[corpseId] = false;
            CorpseX[corpseId] = 0f;
            CorpseY[corpseId] = 0f;
            CorpseMonsterType[corpseId] = null;
            CorpseOwnerId[corpseId] = -1;
            CorpseHealth[corpseId] = 0f;
            CorpseDeathTime[corpseId] = 0f;
            CorpseReanimated[corpseId] = false;
        }

        // ==================== 塔选中状态管理 ====================
        /// <summary>Select a tower for build-phase operations.</summary>
        public void SelectTower(int towerId)
        {
            if (towerId < 0 || towerId >= MAX_ENTITIES) return;
            if (!TowerActive[towerId]) return;
            TowerSelected[towerId] = true;
        }

        /// <summary>Deselect a specific tower.</summary>
        public void DeselectTower(int towerId)
        {
            if (towerId < 0 || towerId >= MAX_ENTITIES) return;
            TowerSelected[towerId] = false;
        }

        /// <summary>Deselect all currently selected towers.</summary>
        public void DeselectAllTowers()
        {
            lock (activeIdsLock)
            {
                foreach (int tid in _activeTowerIds)
                    TowerSelected[tid] = false;
            }
        }

        /// <summary>Returns all selected tower IDs. O(n) over active towers, zero GC.</summary>
        public int[] GetSelectedTowerIds()
        {
            int count = 0;
            lock (activeIdsLock)
            {
                foreach (int tid in _activeTowerIds)
                    if (TowerSelected[tid]) count++;
            }
            int[] result = new int[count];
            int idx = 0;
            lock (activeIdsLock)
            {
                foreach (int tid in _activeTowerIds)
                    if (TowerSelected[tid]) result[idx++] = tid;
            }
            return result;
        }

        // ==================== 塔协同增益 (Tower Synergy) ====================
        /// <summary>Gets the synergy ID for a tower (-1 = no synergy).</summary>
        public int GetTowerSynergyId(int towerId)
        {
            if (towerId < 0 || towerId >= MAX_ENTITIES) return -1;
            return TowerSynergyId[towerId];
        }

        /// <summary>Sets the synergy ID for a tower.</summary>
        public void SetTowerSynergyId(int towerId, int synergyId)
        {
            if (towerId < 0 || towerId >= MAX_ENTITIES) return;
            TowerSynergyId[towerId] = synergyId;
        }

        /// <summary>Gets the synergy multiplier for a tower (1.0 = no bonus).</summary>
        public float GetTowerSynergyMultiplier(int towerId)
        {
            if (towerId < 0 || towerId >= MAX_ENTITIES) return 1.0f;
            return TowerSynergyMultiplier[towerId];
        }

        /// <summary>Sets the synergy multiplier for a tower.</summary>
        public void SetTowerSynergyMultiplier(int towerId, float multiplier)
        {
            if (towerId < 0 || towerId >= MAX_ENTITIES) return;
            TowerSynergyMultiplier[towerId] = multiplier;
        }

        // ==================== 塔索敌模式管理 ====================
        /// <summary>Gets the targeting mode for a tower (0=Nearest, 1=Furthest, 2=LowestHealth, 3=HighestHealth, 4=FirstSpawned, 5=LastSpawned).</summary>
        public int GetTowerTargetingMode(int towerId)
        {
            if (towerId < 0 || towerId >= MAX_ENTITIES) return 0;
            return TowerTargetingMode[towerId];
        }

        /// <summary>Sets the targeting mode for a tower.</summary>
        public void SetTowerTargetingMode(int towerId, int mode)
        {
            if (towerId < 0 || towerId >= MAX_ENTITIES) return;
            TowerTargetingMode[towerId] = mode;
        }

        /// <summary>Sets the projectile homing flag for a tower.</summary>
        public void SetTowerProjectileHoming(int towerId, bool isHoming)
        {
            if (towerId < 0 || towerId >= MAX_ENTITIES) return;
            TowerProjectileHoming[towerId] = isHoming;
        }

        /// <summary>Sets the intercept rate for a PointDefense tower.</summary>
        public void SetTowerInterceptRate(int towerId, float rate)
        {
            if (towerId < 0 || towerId >= MAX_ENTITIES) return;
            TowerInterceptRate[towerId] = rate;
        }

        // ==================== 塔联动/组合攻击 (Tower Link Combo) ====================
        /// <summary>Gets the link combo partner tower ID (-1 = no partner).</summary>
        public int GetTowerLinkPartnerId(int towerId)
        {
            if (towerId < 0 || towerId >= MAX_ENTITIES) return -1;
            return TowerLinkPartnerId[towerId];
        }

        /// <summary>Sets the link combo partner tower ID.</summary>
        public void SetTowerLinkPartnerId(int towerId, int partnerId)
        {
            if (towerId < 0 || towerId >= MAX_ENTITIES) return;
            TowerLinkPartnerId[towerId] = partnerId;
        }

        /// <summary>Gets the link combo cooldown in seconds.</summary>
        public float GetTowerLinkCooldown(int towerId)
        {
            if (towerId < 0 || towerId >= MAX_ENTITIES) return 0f;
            return TowerLinkCooldown[towerId];
        }

        /// <summary>Sets the link combo cooldown in seconds.</summary>
        public void SetTowerLinkCooldown(int towerId, float cooldown)
        {
            if (towerId < 0 || towerId >= MAX_ENTITIES) return;
            TowerLinkCooldown[towerId] = cooldown;
        }

        /// <summary>Gets the link combo damage bonus multiplier.</summary>
        public float GetTowerLinkDamageBonus(int towerId)
        {
            if (towerId < 0 || towerId >= MAX_ENTITIES) return 0f;
            return TowerLinkDamageBonus[towerId];
        }

        /// <summary>Sets the link combo damage bonus multiplier.</summary>
        public void SetTowerLinkDamageBonus(int towerId, float bonus)
        {
            if (towerId < 0 || towerId >= MAX_ENTITIES) return;
            TowerLinkDamageBonus[towerId] = bonus;
        }

        public float GetEnemyHealth(int enemyId)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return 0f;
            return EnemyHealth[enemyId];
        }

        public void SetEnemyHealth(int enemyId, float health)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return;
            EnemyHealth[enemyId] = health;
        }

        public float GetEnemyMaxHealth(int enemyId)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return 0f;
            return EnemyMaxHealth[enemyId];
        }

        public float GetEnemyArmor(int enemyId)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return 0f;
            return EnemyArmor[enemyId];
        }

        public void SetEnemyArmor(int enemyId, float armor)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return;
            EnemyArmor[enemyId] = armor;
        }

        /// <summary>
        /// Applies damage to an enemy, with shield absorbing damage before it reaches health.
        /// </summary>
        public void ApplyEnemyDamage(int enemyId, float damage)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return;
            if (damage <= 0f) return;

            float shield = EnemyShield[enemyId];
            if (shield <= 0f)
            {
                EnemyHealth[enemyId] -= damage;
                return;
            }
            if (shield >= damage)
            {
                EnemyShield[enemyId] = shield - damage;
                return;
            }
            float remaining = damage - shield;
            EnemyShield[enemyId] = 0f;
            EnemyHealth[enemyId] -= remaining;
        }

        public float GetEnemyMoveSpeed(int enemyId)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return 0f;
            return EnemyMoveSpeed[enemyId];
        }

        // ==================== CC (Crowd Control) helpers ====================
        /// <summary>Returns true if the enemy is currently stunned.</summary>
        public bool IsEnemyStunned(int enemyId)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return false;
            // Primary check: duration-based stun (set by ApplyEnemyStun, decremented by EnemyMovementSystem.Update)
            if (EnemyStunDurationLeft[enemyId] > 0f) return true;
            // Fallback: legacy flag (set by external systems, cleared by EnemyMovementSystem.SetTurn)
            return EnemyStunFlag[enemyId];
        }

        /// <summary>Returns true if the player is currently stunned.</summary>
        public bool IsPlayerStunned(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return false;
            return PlayerStunDuration[playerId] > 0;
        }

        /// <summary>Returns true if the player is currently slowed.</summary>
        public bool IsPlayerSlowed(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return false;
            return PlayerSlowFactor[playerId] > 0f;
        }

        /// <summary>Applies a stun to the enemy for the current frame. Stun clears automatically at start of each frame via SetTurnCCFlags.</summary>
        public void ApplyStun(int enemyId)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return;
            EnemyStunFlag[enemyId] = true;
        }

        /// <summary>Applies a stun to the player for N turns.</summary>
        public void ApplyPlayerStun(int playerId, int turns)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            if (turns <= 0) return;
            if (PlayerStunDuration[playerId] < turns)
                PlayerStunDuration[playerId] = turns;
        }

        /// <summary>Applies a slow to the enemy. factor is a multiplier (e.g. 0.5 = 50% speed). Duration in turns tracked by EnemySlowDurationLeft.</summary>
        public void ApplySlow(int enemyId, float factor, int duration)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return;
            if (factor <= 0f || factor >= 1f) return; // only valid slow factors

            float baseSpeed = EnemyMoveSpeedBase[enemyId];
            if (baseSpeed <= 0f) baseSpeed = EnemyMoveSpeed[enemyId];

            EnemySlowFactor[enemyId] = factor;
            EnemyMoveSpeed[enemyId] = baseSpeed * factor;
            EnemySlowDurationLeft[enemyId] = duration;
        }

        /// <summary>Applies slow to the player. factor is a speed multiplier (0.5 = 50% speed).</summary>
        public void ApplyPlayerSlow(int playerId, float factor, int duration)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            if (factor <= 0f || factor >= 1f) return;
            // Take the stronger slow if stacking
            if (factor < PlayerSlowFactor[playerId])
            {
                PlayerSlowFactor[playerId] = factor;
                PlayerSlowDuration[playerId] = duration;
            }
            else if (PlayerSlowFactor[playerId] <= 0f)
            {
                PlayerSlowFactor[playerId] = factor;
                PlayerSlowDuration[playerId] = duration;
            }
        }

        /// <summary>Applies a shield to the player. Shield absorbs damage before health.</summary>
        public void ApplyPlayerShield(int playerId, float amount, float duration)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            if (amount <= 0f) return;
            // Stack shields (keep the larger one + add the new amount)
            PlayerShield[playerId] += amount;
            if (duration > PlayerShieldDuration[playerId])
                PlayerShieldDuration[playerId] = duration;
        }

        /// <summary>Returns the current shield value for a player.</summary>
        public float GetPlayerShield(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 0f;
            return PlayerShield[playerId];
        }

        /// <summary>Clears slow effect and restores original speed.</summary>
        public void ClearSlow(int enemyId)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return;
            if (EnemySlowFactor[enemyId] <= 0f) return; // no slow active

            float baseSpeed = EnemyMoveSpeedBase[enemyId];
            if (baseSpeed > 0f)
                EnemyMoveSpeed[enemyId] = baseSpeed;
            EnemySlowFactor[enemyId] = 0f;
        }

        /// <summary>Applies stun to the enemy for `duration` turns. Stored in EnemyStunDurationLeft (not EnemyStunFlag) so it persists across frames.</summary>
        public void ApplyEnemyStun(int enemyId, int duration)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return;
            // Use duration-based stun so it survives the EnemyMovementSystem.SetTurn() clear
            if (duration > EnemyStunDurationLeft[enemyId])
                EnemyStunDurationLeft[enemyId] = duration;
            // Also set legacy flag for backward compat with IsEnemyStunned fallback
            EnemyStunFlag[enemyId] = true;
        }

        /// <summary>Applies freeze to the enemy for `duration` turns. Alias for ApplyEnemyStun — freeze uses the same stun infrastructure.</summary>
        public void ApplyEnemyFreeze(int enemyId, int duration)
        {
            ApplyEnemyStun(enemyId, duration);
        }

        /// <summary>Returns true if the enemy is currently frozen. Alias for IsEnemyStunned — freeze shares the stun mechanism.</summary>
        public bool IsEnemyFrozen(int enemyId)
        {
            return IsEnemyStunned(enemyId);
        }

        /// <summary>Applies slow to the enemy. factor is a speed multiplier (e.g. 0.5 = 50% speed). Duration in turns tracked by EnemySlowDurationLeft.</summary>
        public void ApplyEnemySlow(int enemyId, float factor, int duration)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return;
            if (factor <= 0f || factor >= 1f) return;
            // Take the stronger slow if stacking
            if (factor < EnemySlowFactor[enemyId])
            {
                EnemySlowFactor[enemyId] = factor;
                float baseSpeed = EnemyMoveSpeedBase[enemyId];
                if (baseSpeed <= 0f) baseSpeed = EnemyMoveSpeed[enemyId];
                EnemyMoveSpeed[enemyId] = baseSpeed * factor;
                EnemySlowDurationLeft[enemyId] = duration;
            }
            else if (EnemySlowFactor[enemyId] <= 0f)
            {
                EnemySlowFactor[enemyId] = factor;
                float baseSpeed = EnemyMoveSpeedBase[enemyId];
                if (baseSpeed <= 0f) baseSpeed = EnemyMoveSpeed[enemyId];
                EnemyMoveSpeed[enemyId] = baseSpeed * factor;
                EnemySlowDurationLeft[enemyId] = duration;
            }
        }

        /// <summary>Clears slow effect on enemy and restores original speed.</summary>
        public void ClearEnemySlow(int enemyId)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return;
            if (EnemySlowFactor[enemyId] <= 0f) return;
            float baseSpeed = EnemyMoveSpeedBase[enemyId];
            if (baseSpeed > 0f)
                EnemyMoveSpeed[enemyId] = baseSpeed;
            EnemySlowFactor[enemyId] = 0f;
        }

        /// <summary>Clears wound slow effect on enemy and restores speed from wound state.</summary>
        public void ClearEnemyWound(int enemyId)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return;
            if (!EnemyIsWounded[enemyId]) return;
            EnemyIsWounded[enemyId] = false;
            // Restore from base speed (wound applied additional multiplier on top of base)
            float baseSpeed = EnemyMoveSpeedBase[enemyId];
            if (baseSpeed > 0f)
                EnemyMoveSpeed[enemyId] = baseSpeed;
        }

        /// <summary>Applies knockback force to an enemy. Force is applied instantly and consumed in ResolveKnockback.</summary>
        public void ApplyEnemyKnockback(int enemyId, float force)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return;
            if (force <= 0f) return;
            // Add to existing force (in case multiple towers hit simultaneously)
            EnemyKnockbackForceLeft[enemyId] += force;
        }

        /// <summary>
        /// Called at the start of each turn: clears enemy stun flags and decrements player CC durations.
        /// Enemy stun flags are cleared by EnemyMovementSystem.SetTurn; this method handles player CC only.
        /// Thread-safety note: called in the serial phase (GameManager.Run frame-end), so no additional
        /// synchronization is needed for MAX_PLAYERS=10 CC field access.
        /// </summary>
        public void SetTurnCCFlags()
        {
            // Decrement player CC durations (MAX_PLAYERS = 10, so simple loop is fast)
            for (int i = 0; i < MAX_PLAYERS; i++)
            {
                if (PlayerStunDuration[i] > 0) PlayerStunDuration[i]--;
                if (PlayerSlowDuration[i] > 0)
                {
                    PlayerSlowDuration[i]--;
                    if (PlayerSlowDuration[i] <= 0) PlayerSlowFactor[i] = 0f;
                }
                // Shield duration decrements per turn (1 second per turn in this engine)
                if (PlayerShieldDuration[i] > 0f)
                {
                    PlayerShieldDuration[i] -= 1f;
                    if (PlayerShieldDuration[i] <= 0f)
                    {
                        PlayerShieldDuration[i] = 0f;
                        PlayerShield[i] = 0f;
                        // Log shield dissipation — use static no-op to avoid Console.WriteLine/IO overhead in hot path
                        FileLogger.LogHotPath($"[SHIELD] 护盾消散！ playerId={i}");
                    }
                }
            }
        }

        /// <summary>
        /// Decrement EnemySlowDurationLeft for all active enemies and clear expired slow effects.
        /// Called once per turn from EnemyMovementSystem.SetTurn() to expire tower-slow durations.
        /// Uses _activeEnemyIds which is safe for read during the serial phase.
        /// </summary>
        public void DecrementEnemySlowDurations()
        {
            for (int i = 0; i < _activeEnemyIds.Count; i++)
            {
                int enemyId = _activeEnemyIds[i];
                float dur = EnemySlowDurationLeft[enemyId];
                if (dur > 0f)
                {
                    EnemySlowDurationLeft[enemyId] = dur - 1f;
                    if (EnemySlowDurationLeft[enemyId] <= 0f)
                    {
                        EnemySlowDurationLeft[enemyId] = 0f;
                        ClearEnemySlow(enemyId);
                    }
                }
            }
        }

        public float GetEnemyDamage(int enemyId)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return 0f;
            return EnemyDamage[enemyId];
        }

        public int GetEnemyGoldReward(int enemyId)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return 0;
            return EnemyGoldReward[enemyId];
        }

        // ==================== 敌人 AI 组件访问 ====================

        public string GetEnemyAIAction(int enemyId)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return "";
            return EnemyAIAction[enemyId];
        }

        public string GetEnemyTypeName(int enemyId)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return "";
            return EnemyTypeName[enemyId] ?? "";
        }

        public void SetEnemyAIAction(int enemyId, string action)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return;
            EnemyAIAction[enemyId] = action ?? "";
        }

        public int GetEnemyAIChargeCounter(int enemyId)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return 0;
            return EnemyAIChargeCounter[enemyId];
        }

        public void SetEnemyAIChargeCounter(int enemyId, int counter)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return;
            EnemyAIChargeCounter[enemyId] = counter;
        }

        public int GetEnemyAILastAttackTurn(int enemyId)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return 0;
            return EnemyAILastAttackTurn[enemyId];
        }

        public void SetEnemyAILastAttackTurn(int enemyId, int turn)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return;
            EnemyAILastAttackTurn[enemyId] = turn;
        }

        public EnemyActionType GetEnemyActionEnum(int enemyId)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return EnemyActionType.None;
            return EnemyActionEnum[enemyId];
        }

        public void SetEnemyActionEnum(int enemyId, EnemyActionType action)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return;
            EnemyActionEnum[enemyId] = action;
        }

        // ==================== 技能组件 SOA 访问方法 ====================

        public string GetSkillName(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return "";
            return SkillName[playerId];
        }

        public void SetSkillName(int playerId, string name)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            SkillName[playerId] = name;
        }

        public float GetSkillDamageMultiplier(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 1f;
            return SkillDamageMultiplier[playerId];
        }

        public void SetSkillDamageMultiplier(int playerId, float multiplier)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            SkillDamageMultiplier[playerId] = multiplier;
        }

        public int GetSkillAreaWidth(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 1;
            return SkillAreaWidth[playerId];
        }

        public void SetSkillAreaWidth(int playerId, int width)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            SkillAreaWidth[playerId] = width;
        }

        public int GetSkillAreaHeight(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 1;
            return SkillAreaHeight[playerId];
        }

        public void SetSkillAreaHeight(int playerId, int height)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            SkillAreaHeight[playerId] = height;
        }

        public int GetSkillAttackRange(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 1;
            return SkillAttackRange[playerId];
        }

        public void SetSkillAttackRange(int playerId, int range)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            SkillAttackRange[playerId] = range;
        }

        public float GetSkillCooldown(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 0f;
            return SkillCooldown[playerId];
        }

        public void SetSkillCooldown(int playerId, float cooldown)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            SkillCooldown[playerId] = cooldown;
        }

        public float GetSkillCurrentCooldown(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 0f;
            return SkillCurrentCooldown[playerId];
        }

        public void SetSkillCurrentCooldown(int playerId, float currentCooldown)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            SkillCurrentCooldown[playerId] = currentCooldown;
        }

        // ==================== 实体查询 ====================

        public bool IsEnemyActive(int entityId)
        {
            if (entityId < 0 || entityId >= MAX_ENTITIES) return false;
            return EnemyActive[entityId];
        }

        public bool IsPlayer(int entityId)
        {
            return entityId == PlayerEntityId;
        }

        public List<int> GetActiveEnemyIds()
        {
            // Returns a defensive copy of the internal list — caller modifications don't affect internal state
            return new List<int>(_activeEnemyIds);
        }

        public List<int> GetAllActiveEnemyIds()
        {
            // Returns a single defensive copy — avoids double allocation from ActiveEnemyIds.ToList() + new List<int>(...)
            return new List<int>(_activeEnemyIds);
        }

        /// <summary>
        /// Returns the internal active enemy list directly — zero allocation, read-only use.
        ///
        /// FRAME-ORDER INVARIANT (enforced, not optional):
        /// - Call SetTurn() or equivalent to obtain this reference ONCE per frame.
        /// - Do NOT hold the reference across frames — the next SetTurn() may invalidate it.
        /// - Do NOT mutate the returned list — DestroyEntity removes entries during
        ///   ResolveEnemiesKilledThisFrame(), which runs AFTER all systems in the main loop.
        /// - Concurrent read access from Parallel.For within the same frame is safe.
        ///
        /// Violating these rules causes: stale enumeration, IndexOutOfRange, or enemies
        /// vanishing mid-frame from a system that still holds a cached reference.
        /// </summary>
        public List<int> GetCachedActiveEnemyIds()
        {
            // _activeEnemyIds is mutated only by AddEnemy/RemoveEntity — never during the
            // parallel system chain within a frame. Safe to share as read-only reference.
            if (_activeEnemyIds.Count > 0)
                return _activeEnemyIds;
            // Fallback: empty store (test / standalone usage). Return fresh copy.
            return new List<int>(_activeEnemyIds);
        }

        public int GetActiveEnemyCount()
        {
            return _activeEnemyIds.Count;
        }

        // ==================== 玩家生命值访问方法 ====================

        public float GetPlayerMaxHealth(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 0f;
            return PlayerMaxHealth[playerId];
        }

        public void SetPlayerMaxHealth(int playerId, float maxHealth)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            PlayerMaxHealth[playerId] = maxHealth;
        }

public float GetPlayerCurrentHealth(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 0f;
            return PlayerCurrentHealth[playerId];
        }

        public int GetPlayerBaseLives(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 0;
            return PlayerBaseLives[playerId];
        }

        public void SetPlayerBaseLives(int playerId, int lives)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            PlayerBaseLives[playerId] = lives;
        }

        public void DecrementPlayerBaseLives(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            if (PlayerBaseLives[playerId] > 0)
                PlayerBaseLives[playerId]--;
        }

        public int GetCurrentWeather(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 0;
            return CurrentWeather[playerId];
        }

        public void SetCurrentWeather(int playerId, int weatherType)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            CurrentWeather[playerId] = weatherType;
        }

        public float GetWeatherIntensity(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 0f;
            return WeatherIntensity[playerId];
        }

        public void SetWeatherIntensity(int playerId, float intensity)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            WeatherIntensity[playerId] = intensity;
        }

        public float GetWeatherTimer(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return -1f;
            return WeatherTimer[playerId];
        }

        public void SetWeatherTimer(int playerId, float timer)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            WeatherTimer[playerId] = timer;
        }

        // ==================== 昼夜循环系统访问方法 ====================
        public int GetDayNightPhase(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 0;
            return GlobalDayNightPhase[playerId];
        }

        public void SetDayNightPhase(int playerId, int phase)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            GlobalDayNightPhase[playerId] = phase;
        }

        public float GetDayNightTimer(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return -1f;
            return GlobalDayNightTimer[playerId];
        }

        public void SetDayNightTimer(int playerId, float timer)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            GlobalDayNightTimer[playerId] = timer;
        }

        public int GetDayNightCycleCount(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 0;
            return GlobalDayNightCycleCount[playerId];
        }

        public void IncrementDayNightCycleCount(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            GlobalDayNightCycleCount[playerId]++;
        }

        public void SetPlayerCurrentHealth(int playerId, float currentHealth)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            PlayerCurrentHealth[playerId] = currentHealth;
        }

        public void DecreasePlayerHealth(int playerId, float damage)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            // Shield absorbs damage before health (independent of armor)
            float shield = PlayerShield[playerId];
            if (shield > 0f)
            {
                float absorbed = System.Math.Min(shield, damage);
                PlayerShield[playerId] = shield - absorbed;
                damage -= absorbed;
                if (damage <= 0f) return;
            }
            float armor = PlayerArmor[playerId];
            float mitigatedDamage = damage * (1f - armor);
            PlayerCurrentHealth[playerId] = System.Math.Max(0f, PlayerCurrentHealth[playerId] - mitigatedDamage);
        }

        public bool IsPlayerAlive(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return false;
            return PlayerCurrentHealth[playerId] > 0f;
        }

        // ==================== GAS 组件访问方法 ====================

        public AbilityInstance GetAbility(int entityId, int slot) {
            if (entityId < 0 || entityId >= MAX_ENTITIES) return default;
            if (slot < 0 || slot >= MAX_ABILITIES_PER_ENTITY) return default;
            return AbilityInstances[entityId * MAX_ABILITIES_PER_ENTITY + slot];
        }

        public void SetAbility(int entityId, int slot, AbilityInstance inst) {
            if (entityId < 0 || entityId >= MAX_ENTITIES) return;
            if (slot < 0 || slot >= MAX_ABILITIES_PER_ENTITY) return;
            AbilityInstances[entityId * MAX_ABILITIES_PER_ENTITY + slot] = inst;
        }

        public void AddAbility(int entityId, GameplayAbilityDef def) {
            if (entityId < 0 || entityId >= MAX_ENTITIES) return;
            int slot = AbilityCount[entityId];
            if (slot < MAX_ABILITIES_PER_ENTITY) { SetAbility(entityId, slot, new AbilityInstance(def)); AbilityCount[entityId]++; }
        }

        // Bug#9: Reset abilities for entity — clears all slots (used before re-initializing)
        public void ResetPlayerAbilities(int entityId) {
            if (entityId < 0 || entityId >= MAX_ENTITIES) return;
            AbilityCount[entityId] = 0;
            ActiveEffectCount[entityId] = 0;
        }

        public AppliedEffect GetEffect(int entityId, int slot) {
            if (entityId < 0 || entityId >= MAX_ENTITIES) return default;
            if (slot < 0 || slot >= MAX_ACTIVE_EFFECTS_PER_ENTITY) return default;
            return ActiveEffects[entityId * MAX_ACTIVE_EFFECTS_PER_ENTITY + slot];
        }

        public void SetEffect(int entityId, int slot, AppliedEffect eff) {
            if (entityId < 0 || entityId >= MAX_ENTITIES) return;
            if (slot < 0 || slot >= MAX_ACTIVE_EFFECTS_PER_ENTITY) return;
            ActiveEffects[entityId * MAX_ACTIVE_EFFECTS_PER_ENTITY + slot] = eff;
        }

        public int GetEffectCount(int entityId) {
            if (entityId < 0 || entityId >= MAX_ENTITIES) return 0;
            return ActiveEffectCount[entityId];
        }

        public void AddEffect(int entityId, AppliedEffect eff) {
            if (entityId < 0 || entityId >= MAX_ENTITIES) return;
            int slot = ActiveEffectCount[entityId];
            if (slot < MAX_ACTIVE_EFFECTS_PER_ENTITY) { SetEffect(entityId, slot, eff); ActiveEffectCount[entityId]++; }
        }

        public void SetEffectCount(int entityId, int count) {
            if (entityId < 0 || entityId >= MAX_ENTITIES) return;
            if (count < 0) count = 0;
            if (count > MAX_ACTIVE_EFFECTS_PER_ENTITY) count = MAX_ACTIVE_EFFECTS_PER_ENTITY;
            ActiveEffectCount[entityId] = count;
        }

        // ==================== 科技树组件访问方法 ====================

        public int GetResearchPoints(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 0;
            return PlayerResearchPoints[playerId];
        }

        public void AddResearchPoints(int playerId, int amount)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            PlayerResearchPoints[playerId] += amount;
        }

        public bool IsTechUnlocked(int playerId, string nodeId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return false;
            return PlayerUnlockedTechs[playerId].Contains(nodeId);
        }

        public void UnlockTech(int playerId, string nodeId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            PlayerUnlockedTechs[playerId].Add(nodeId);
        }

        public HashSet<string> GetUnlockedTechs(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return new HashSet<string>();
            // L-1 fix: return a defensive copy to prevent external mutation
            return new HashSet<string>(PlayerUnlockedTechs[playerId]);
        }

        // ==================== 路径修改塔访问方法 ====================

        /// <summary>
        /// Activate a path modifier at the given position with the specified influence zone.
        /// </summary>
        public void ActivatePathModifier(int modifierId, float x, float y, float radius, int targetPathId, int ownerId, float turnsRemaining = 0f)
        {
            if (modifierId < 0 || modifierId >= MAX_ENTITIES) return;
            PathModifierX[modifierId] = x;
            PathModifierY[modifierId] = y;
            PathModifierRadius[modifierId] = radius;
            PathModifierTargetPathId[modifierId] = targetPathId;
            PathModifierOwnerId[modifierId] = ownerId;
            PathModifierTurnsRemaining[modifierId] = turnsRemaining;
            PathModifierActive[modifierId] = true;
            _activePathModifierCount++;
        }

        /// <summary>
        /// Deactivate a path modifier by its entity ID.
        /// </summary>
        public void DeactivatePathModifier(int modifierId)
        {
            if (modifierId < 0 || modifierId >= MAX_ENTITIES) return;
            if (!PathModifierActive[modifierId]) return;
            PathModifierActive[modifierId] = false;
            _activePathModifierCount = System.Math.Max(0, _activePathModifierCount - 1);
        }

        /// <summary>
        /// Returns true if the given position is within the influence zone of any active path modifier.
        /// </summary>
        public bool IsWithinAnyPathModifier(float x, float y)
        {
            for (int i = 0; i < MAX_ENTITIES; i++)
            {
                if (!PathModifierActive[i]) continue;
                float dx = PathModifierX[i] - x;
                float dy = PathModifierY[i] - y;
                float distSq = dx * dx + dy * dy;
                float radius = PathModifierRadius[i];
                if (distSq <= radius * radius)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Get the target path ID for the first active path modifier that covers the given position.
        /// Returns -1 if no active modifier covers the position.
        /// </summary>
        public int GetPathModifierTargetPathId(float x, float y)
        {
            for (int i = 0; i < MAX_ENTITIES; i++)
            {
                if (!PathModifierActive[i]) continue;
                float dx = PathModifierX[i] - x;
                float dy = PathModifierY[i] - y;
                float distSq = dx * dx + dy * dy;
                float radius = PathModifierRadius[i];
                if (distSq <= radius * radius)
                    return PathModifierTargetPathId[i];
            }
            return -1;
        }

        /// <summary>
        /// Returns the modifier ID of the first active path modifier covering the given position, or -1.
        /// </summary>
        public int GetPathModifierIdAt(float x, float y)
        {
            for (int i = 0; i < MAX_ENTITIES; i++)
            {
                if (!PathModifierActive[i]) continue;
                float dx = PathModifierX[i] - x;
                float dy = PathModifierY[i] - y;
                float distSq = dx * dx + dy * dy;
                float radius = PathModifierRadius[i];
                if (distSq <= radius * radius)
                    return i;
            }
            return -1;
        }
    }
}
