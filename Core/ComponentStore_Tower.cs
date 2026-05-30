using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Core;

namespace BattleSystemECS.Core
{
    public partial class ComponentStore
    {
        #region Tower Fields
        // ==================== 战争迷雾 / 视野系统组件 (Fog of War, SOA) ====================
        // TowerVisionRadius: vision radius in grid units for each tower (0 = can see all enemies, no fog)
        // Default 0 means no fog restriction (backward compatible)
        public float[] TowerVisionRadius = new float[MAX_ENTITIES];
        // TowerVisibilityMask: Dictionary<towerId, bool[]> — tower's visibility to each enemy
        // Key: towerId (entity id of fog-of-war enabled tower)
        // Value: bool array [enemyId] = true if enemy is visible to this tower this frame
        // Uses Dictionary to avoid 10B-entry flat array (only towers with VisionRadius > 0 need entries)
        public Dictionary<int, bool[]> TowerVisibilityByTower = new Dictionary<int, bool[]>();
        // ==================== 塔组件的 SOA 存储 ====================
        // Tower targeting mode: controls which enemy the tower selects as its target.
        public TowerTargetingMode[] TowerTargetingMode = new TowerTargetingMode[MAX_ENTITIES];
        // Tower projectile homing: if true, this tower's projectiles track targets mid-flight
        public bool[] TowerProjectileHoming = new bool[MAX_ENTITIES];
        // Tower intercept rate: probability of intercepting enemy projectiles (for PointDefense towers)
        // Stored separately from TowerCritChance to keep concerns isolated (reuse CritChance as intercept rate when needed)
        public float[] TowerInterceptRate = new float[MAX_ENTITIES];
        // Tower damage type: determines which resistance the target uses for mitigation.
        public DamageType[] TowerDamageType = new DamageType[MAX_ENTITIES];
        // Tower selection state — O(1) read/write, no GC
        public bool[] TowerSelected = new bool[MAX_ENTITIES];
        public TowerType[] TowerType = new TowerType[MAX_ENTITIES];
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

        // ==================== 移动/巡逻塔组件 (Mobile / Patrol Tower) ====================
        // TowerIsMobile: true if this tower moves along a patrol path during combat
        public bool[] TowerIsMobile = new bool[MAX_ENTITIES];
        // TowerMoveSpeed: movement speed in grid units per second
        public float[] TowerMoveSpeed = new float[MAX_ENTITIES];
        // TowerPatrolPathId: ID of the patrol path this tower follows (-1 = no path)
        public int[] TowerPatrolPathId = new int[MAX_ENTITIES];
        // TowerPatrolWaypointIndex: current target waypoint index in the patrol path
        public int[] TowerPatrolWaypointIndex = new int[MAX_ENTITIES];
        // TowerPatrolDirection: +1 = forward, -1 = backward (ping-pong), 0 = one-way
        public int[] TowerPatrolDirection = new int[MAX_ENTITIES];
        // TowerPatrolAttackSpeedPenalty: attack speed multiplier while moving (e.g. 0.7 = 30% slower)
        public float[] TowerPatrolAttackSpeedPenalty = new float[MAX_ENTITIES];

        // ==================== 塔组件访问 ====================

        /// <summary>
        /// Add a tower with default "standard" upgrade path.
        /// </summary>
        public void AddTower(int entityId, TowerType type, float damage, int range, float speed, int level, float cost)
            => AddTower(entityId, type, damage, range, speed, level, cost, "standard", 0f, 0f, 0f);

        /// <summary>
        /// Add a tower with a specific upgrade path.
        /// </summary>
        public void AddTower(int entityId, TowerType type, float damage, int range, float speed, int level, float cost, string upgradePathId)
            => AddTower(entityId, type, damage, range, speed, level, cost, upgradePathId, 0f, 0f, 0f);

        /// <summary>
        /// Add a tower with debuff parameters.
        /// </summary>
        public void AddTower(int entityId, TowerType type, float damage, int range, float speed, int level, float cost, string upgradePathId, float stunChance, float slowAmount, float slowDuration, DamageType damageType = DamageType.Physical, float turnRate = 0f)
        {
            if (!IsValidEntity(entityId)) return;
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
            lock (activeIdsLock) { _activeTowerIds.Add(entityId); _towerIndexInList[entityId] = _activeTowerIds.Count - 1; }
        }

        public void RemoveTower(int entityId)
        {
            if (!IsValidEntity(entityId)) return;
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
            TowerDamageType[entityId] = DamageType.Physical;
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
            // Patrol tower fields reset
            TowerIsMobile[entityId] = false;
            TowerMoveSpeed[entityId] = 0f;
            TowerPatrolPathId[entityId] = -1;
            TowerPatrolWaypointIndex[entityId] = 0;
            TowerPatrolDirection[entityId] = 1;
            TowerPatrolAttackSpeedPenalty[entityId] = 1f;
            lock (activeIdsLock) { RemoveTowerFromList(entityId); }
        }
        #endregion

        // ==================== 塔选中状态管理 ====================
        /// <summary>Select a tower for build-phase operations.</summary>
        public void SelectTower(int towerId)
        {
            if (!IsValidEntity(towerId)) return;
            if (!TowerActive[towerId]) return;
            TowerSelected[towerId] = true;
        }

        /// <summary>Deselect a specific tower.</summary>
        public void DeselectTower(int towerId)
        {
            if (!IsValidEntity(towerId)) return;
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
            if (!IsValidEntity(towerId)) return -1;
            return TowerSynergyId[towerId];
        }

        /// <summary>Sets the synergy ID for a tower.</summary>
        public void SetTowerSynergyId(int towerId, int synergyId)
        {
            if (!IsValidEntity(towerId)) return;
            TowerSynergyId[towerId] = synergyId;
        }

        /// <summary>Gets the synergy multiplier for a tower (1.0 = no bonus).</summary>
        public float GetTowerSynergyMultiplier(int towerId)
        {
            if (!IsValidEntity(towerId)) return 1.0f;
            return TowerSynergyMultiplier[towerId];
        }

        /// <summary>Sets the synergy multiplier for a tower.</summary>
        public void SetTowerSynergyMultiplier(int towerId, float multiplier)
        {
            if (!IsValidEntity(towerId)) return;
            TowerSynergyMultiplier[towerId] = multiplier;
        }

        // ==================== 塔索敌模式管理 ====================
        /// <summary>Gets the targeting mode for a tower.</summary>
        public TowerTargetingMode GetTowerTargetingMode(int towerId)
        {
            if (!IsValidEntity(towerId)) return Components.TowerTargetingMode.Nearest;
            return TowerTargetingMode[towerId];
        }

        /// <summary>Sets the targeting mode for a tower.</summary>
        public void SetTowerTargetingMode(int towerId, TowerTargetingMode mode)
        {
            if (!IsValidEntity(towerId)) return;
            TowerTargetingMode[towerId] = mode;
        }

        /// <summary>Sets the projectile homing flag for a tower.</summary>
        public void SetTowerProjectileHoming(int towerId, bool isHoming)
        {
            if (!IsValidEntity(towerId)) return;
            TowerProjectileHoming[towerId] = isHoming;
        }

        /// <summary>Sets the intercept rate for a PointDefense tower.</summary>
        public void SetTowerInterceptRate(int towerId, float rate)
        {
            if (!IsValidEntity(towerId)) return;
            TowerInterceptRate[towerId] = rate;
        }

        // ==================== 塔联动/组合攻击 (Tower Link Combo) ====================
        /// <summary>Gets the link combo partner tower ID (-1 = no partner).</summary>
        public int GetTowerLinkPartnerId(int towerId)
        {
            if (!IsValidEntity(towerId)) return -1;
            return TowerLinkPartnerId[towerId];
        }

        /// <summary>Sets the link combo partner tower ID.</summary>
        public void SetTowerLinkPartnerId(int towerId, int partnerId)
        {
            if (!IsValidEntity(towerId)) return;
            TowerLinkPartnerId[towerId] = partnerId;
        }

        /// <summary>Gets the link combo cooldown in seconds.</summary>
        public float GetTowerLinkCooldown(int towerId)
        {
            if (!IsValidEntity(towerId)) return 0f;
            return TowerLinkCooldown[towerId];
        }

        /// <summary>Sets the link combo cooldown in seconds.</summary>
        public void SetTowerLinkCooldown(int towerId, float cooldown)
        {
            if (!IsValidEntity(towerId)) return;
            TowerLinkCooldown[towerId] = cooldown;
        }

        /// <summary>Gets the link combo damage bonus multiplier.</summary>
        public float GetTowerLinkDamageBonus(int towerId)
        {
            if (!IsValidEntity(towerId)) return 0f;
            return TowerLinkDamageBonus[towerId];
        }

        /// <summary>Sets the link combo damage bonus multiplier.</summary>
        public void SetTowerLinkDamageBonus(int towerId, float bonus)
        {
            if (!IsValidEntity(towerId)) return;
            TowerLinkDamageBonus[towerId] = bonus;
        }
    }
}
