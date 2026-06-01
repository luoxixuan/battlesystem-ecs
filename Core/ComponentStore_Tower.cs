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
        // Tower damage conversion: fraction of damage converted to ConvertedDamageType (0 = no conversion)
        // E.g. 0.5 = 50% damage converted, bypassing enemy immunity to primary type
        public float[] TowerDamageConversionRatio = new float[MAX_ENTITIES];
        // Tower converted damage type: the target type for damage conversion
        public DamageType[] TowerConvertedDamageType = new DamageType[MAX_ENTITIES];
        // Tower selection state — O(1) read/write, no GC
        public bool[] TowerSelected = new bool[MAX_ENTITIES];
        // Tower cooldown reduction: per-tower CDR (0 = no reduction, 0.3 = 30% faster cooldowns)
        // Multiplicative: effectiveCooldown = baseCooldown * (1 - cdr), capped at 60% (0.6)
        public float[] TowerCooldownReduction = new float[MAX_ENTITIES];
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
        // ── Tower Morph / Mode Switch ──────────────────────────────────────
        // TowerCurrentMorph: index of the currently active morph (0 = first form, 1 = second form, ...)
        public int[] TowerCurrentMorph = new int[MAX_ENTITIES];
        // TowerMorphCount: how many morphs this tower has (1 = no morph available, 2+ = morphable)
        public int[] TowerMorphCount = new int[MAX_ENTITIES];
        // TowerMorphCooldown: seconds remaining before next morph switch is allowed (0 = ready)
        public float[] TowerMorphCooldown = new float[MAX_ENTITIES];
        // TowerMorphDamage/Radius/Speed: stat snapshots per morph index
        // [morphIndex] -> float[] indexed by towerId — avoids MAX_MORPHS × MAX_ENTITIES flat allocation
        public float[][] TowerMorphDamage  = new float[MAX_MORPHS][];
        public float[][] TowerMorphAttackSpeed = new float[MAX_MORPHS][];
        public int[][]    TowerMorphRange   = new int[MAX_MORPHS][];
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
        // ── Overkill / Excess Damage ─────────────────────────────────────────────
        // TowerOverkillType: 0=None (no overkill effect), 1=Splash (excess damage splashes to nearby enemies)
        // Default 0 = no overkill effect (backward compatible)
        public int[] TowerOverkillType = new int[MAX_ENTITIES];
        // TowerOverkillRatio: fraction of excess damage that becomes splash/secondary effect (0-1)
        // E.g. 0.6 = 60% of overkill is distributed to nearby enemies, 40% is wasted
        public float[] TowerOverkillRatio = new float[MAX_ENTITIES];
        // TowerOverkillRadius: search radius in tiles for finding overkill splash targets
        // 0 = no radius (effect disabled, even if type is non-zero)
        public float[] TowerOverkillRadius = new float[MAX_ENTITIES];
        // ── Kill-Triggered Cooldown Reset (ARPG/Roguelike mechanic) ────────────
        // TowerResetOnKill: 0=None (no reset on kill), 1=Full (resets attack timer to ready state immediately),
        // 2=Partial (reduces attack timer by TowerResetAmount seconds). Default 0 = disabled.
        public int[] TowerResetOnKill = new int[MAX_ENTITIES];
        // TowerResetAmount: for Partial mode, seconds to subtract from TowerLastAttackTime (clamped at 0).
        // For Full mode, value is ignored (reset is unconditional). Default 0.
        public float[] TowerResetAmount = new float[MAX_ENTITIES];
        // Tower anti-air flags: controls which height layers a tower can attack
        // TowerCanHitAir=true: tower can attack flying enemies (anti-air tower)
        // TowerCanHitGround=true: tower can attack ground enemies
        // Both can be true (multi-type tower) or false (invalid — will skip all targets)
        public bool[] TowerCanHitAir = new bool[MAX_ENTITIES];
        public bool[] TowerCanHitGround = new bool[MAX_ENTITIES];
        // Tower placement timestamp (Time.TotalTime at AddTower) — used by sell-back value decay
        public float[] TowerPlaceTime = new float[MAX_ENTITIES];
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

        // ==================== 恐惧光环塔 (Fear Aura Towers) ====================
        // TowerFearRadius: radius within which enemies are affected by fear (0 = no fear aura)
        public float[] TowerFearRadius = new float[MAX_ENTITIES];
        // TowerFearDuration: duration of fear applied to enemies in the aura (in frames)
        public float[] TowerFearDuration = new float[MAX_ENTITIES];
        // TowerFearChance: probability (0-1) that fear is applied each frame an enemy is in range
        public float[] TowerFearChance = new float[MAX_ENTITIES];

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

        // ==================== 塔连锁攻击伤害倍率 (Tower Chain / Link Attack) ====================
        // TowerChainDmgRatio: damage multiplier for chain-attacks from linked partner tower
        // When this tower attacks, its linked partner (TowerLinkPartnerId) also deals damage
        // to the same target, multiplied by this ratio (0.5 = 50% of partner's damage)
        // 0 = no chain attack behavior
        public float[] TowerChainDmgRatio = new float[MAX_ENTITIES];

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

        // ==================== 塔隐形/伪装 (Tower Stealth) ====================
        // TowerIsStealthed: true if this tower is currently hidden from enemies (True Sight penetrates)
        public bool[] TowerIsStealthed = new bool[MAX_ENTITIES];
        // TowerStealthType: 0=none, 1=Passive (always stealthed), 2=Active (decloak on attack), 3=SemiStealth (takes partial damage while stealthed)
        public int[] TowerStealthType = new int[MAX_ENTITIES];
        // TowerDecloakOnFire: true if this tower reveals itself when it attacks (type2: active)
        public bool[] TowerDecloakOnFire = new bool[MAX_ENTITIES];
        // TowerStealthTimer: countdown timer for temporary stealth (type 2 active, seconds remaining)
        public float[] TowerStealthTimer = new float[MAX_ENTITIES];
        // TowerStealthDuration: total duration for temporary stealth (type 2 active)
        public float[] TowerStealthDuration = new float[MAX_ENTITIES];
        // TowerWasStealthedLastFrame: true if tower was stealthed last frame (used for decloak-on-attack tracking)
        public bool[] TowerWasStealthedLastFrame = new bool[MAX_ENTITIES];

        // ==================== 地图热区加成 (Hot Zone Terrain Bonus) ====================
        // TowerHotZoneDamageBonus: cached damage multiplier bonus from hot zone placement (e.g. 0.15 = +15%)
        // Set once at tower placement via HotZoneSystem.OnTowerPlaced(), read during combat.
        public float[] TowerHotZoneDamageBonus = new float[MAX_ENTITIES];
        // TowerHotZoneRangeBonus: cached range bonus (in cells) from hot zone placement.
        // Added to TowerRange during attack resolution.
        public float[] TowerHotZoneRangeBonus = new float[MAX_ENTITIES];
        // TowerHotZoneSpeedBonus: cached attack speed multiplier bonus from hot zone (e.g. 0.1 = +10%).
        public float[] TowerHotZoneSpeedBonus = new float[MAX_ENTITIES];

        // ==================== 迫击炮/弧线弹道 (Mortar / Arc Projectiles) ====================
        // TowerProjectileArcType: 0=直线（默认）, 1=跟踪, 2=弧线（抛物线）
        // Affects how the projectile moves through the air — arc uses gravity simulation
        public int[] TowerProjectileArcType = new int[MAX_ENTITIES];
        // TowerProjectileArcPeakHeight: peak height for arc-type projectiles (grid units)
        // Only used when TowerProjectileArcType == 2 (Arc)
        public float[] TowerProjectileArcPeakHeight = new float[MAX_ENTITIES];
        // TowerProjectileGravityScale: gravity multiplier for arc projectiles (default 1.0)
        public float[] TowerProjectileGravityScale = new float[MAX_ENTITIES];

        // ==================== 塔过热/热量系统 (Tower Heat / Overheat) ====================
        // TowerHeat: current accumulated heat level for each tower (0 = cold, max = overheated)
        public float[] TowerHeat = new float[MAX_ENTITIES];
        // TowerMaxHeat: maximum heat capacity before overheat triggers
        public float[] TowerMaxHeat = new float[MAX_ENTITIES];
        // TowerHeatPerShot: heat generated per attack shot
        public float[] TowerHeatPerShot = new float[MAX_ENTITIES];
        // TowerHeatCooldownRate: heat dissipation rate in heat units per second (passive cooling)
        public float[] TowerHeatCooldownRate = new float[MAX_ENTITIES];
        // TowerIsOverheated: true if tower is currently overheated (reduced performance)
        public bool[] TowerIsOverheated = new bool[MAX_ENTITIES];
        // TowerOverheatTimer: remaining cooldown/lockout after overheat clears (seconds)
        public float[] TowerOverheatTimer = new float[MAX_ENTITIES];
        // TowerOverheatBonus: attack speed multiplier when overheated (e.g., 2.0 = double speed)
        public float[] TowerOverheatBonus = new float[MAX_ENTITIES];
        // TowerOverheatPenalty: damage penalty when overheated (0.0-1.0, e.g., 0.5 = -50% damage)
        public float[] TowerOverheatPenalty = new float[MAX_ENTITIES];
        // TowerCanOverheat: true if this tower type supports overheat (from config)
        public bool[] TowerCanOverheat = new bool[MAX_ENTITIES];

        // ==================== 塔能量/法力资源系统 (Tower Energy) ====================
        // TowerEnergy: current energy level for each tower (0 = depleted, cannot fire if below TowerEnergyPerShot)
        public float[] TowerEnergy = new float[MAX_ENTITIES];
        // TowerMaxEnergy: maximum energy capacity (0 = no energy system)
        public float[] TowerMaxEnergy = new float[MAX_ENTITIES];
        // TowerEnergyPerShot: energy consumed per attack shot (0 = no energy cost)
        public float[] TowerEnergyPerShot = new float[MAX_ENTITIES];
        // TowerEnergyRegen: energy regeneration rate per second (passive recharge)
        public float[] TowerEnergyRegen = new float[MAX_ENTITIES];
        // TowerIsEnergyTower: true if this tower is an energy source that regenerates nearby towers' energy
        public bool[] TowerIsEnergyTower = new bool[MAX_ENTITIES];
        // TowerEnergyRegenRadius: radius within which this energy tower regenerates nearby towers
        public float[] TowerEnergyRegenRadius = new float[MAX_ENTITIES];

        // ==================== 塔光束/激光连续攻击系统 (Beam Tower) ====================
        // TowerIsBeam: true if this tower fires a continuous beam (DPS-based, not projectile)
        public bool[] TowerIsBeam = new bool[MAX_ENTITIES];
        // TowerBeamDPS: damage per second for beam towers (continuous damage applied per frame)
        public float[] TowerBeamDPS = new float[MAX_ENTITIES];
        // TowerBeamChainCount: number of chain targets for beam towers (0 = no chain, max 3)
        public int[] TowerBeamChainCount = new int[MAX_ENTITIES];
        // TowerBeamChainDecay: damage decay per chain hop (0.7 = 70% of previous link's damage)
        public float[] TowerBeamChainDecay = new float[MAX_ENTITIES];
        // ==================== 塔瘫痪/破坏系统（Sabotage / Tower Disable）====================
        // TowerIsDisabled: true if tower is currently disabled/sabotaged by enemy ability
        public bool[] TowerIsDisabled = new bool[MAX_ENTITIES];
        // TowerDisabledTimer: countdown timer — when reaches 0, disable ends
        public float[] TowerDisabledTimer = new float[MAX_ENTITIES];
        // TowerDisabledDuration: total duration of disable effect in seconds
        public float[] TowerDisabledDuration = new float[MAX_ENTITIES];

        // TowerBeamMaxRange: maximum range for beam targeting and chaining
        public float[] TowerBeamMaxRange = new float[MAX_ENTITIES];

        // ==================== 塔爆发射击/齐射模式 (Burst Fire / Salvo Mode) ====================
        // TowerBurstCount: number of shots fired per burst cycle (0 = no burst fire, standard single-shot)
        public int[] TowerBurstCount = new int[MAX_ENTITIES];
        // TowerBurstInterval: time in seconds between shots within a burst (e.g. 0.1 = 10 shots/sec during burst)
        public float[] TowerBurstInterval = new float[MAX_ENTITIES];
        // TowerBurstCooldown: total cooldown time in seconds for one full burst cycle
        // After firing all burst shots, tower enters cooldown before next burst can start
        public float[] TowerBurstCooldown = new float[MAX_ENTITIES];
        // TowerBurstTimer: current burst phase timer — tracks interval between burst shots
        public float[] TowerBurstTimer = new float[MAX_ENTITIES];
        // TowerBurstShotsFired: how many shots have been fired in the current burst cycle
        // Resets to 0 when burst cooldown completes
        public int[] TowerBurstShotsFired = new int[MAX_ENTITIES];

        // ── 射程伤害衰减 (Range-Based Damage Falloff) ====================
        // TowerFalloffType: 0=None, 1=Standard (closer=more dmg), 2=Reverse (sniper: farther=more dmg)
        public int[] TowerFalloffType = new int[MAX_ENTITIES];
        // TowerFalloffStartRatio: fraction of max range where falloff begins
        public float[] TowerFalloffStartRatio = new float[MAX_ENTITIES];
        // TowerFalloffMinRatio: minimum damage multiplier at max range (Standard) or min range (Reverse)
        public float[] TowerFalloffMinRatio = new float[MAX_ENTITIES];

        // ==================== 随机伤害范围（Damage Variance / Gambling）====================
        // TowerDamageVariance: fraction of damage that can randomly vary (0 = no variance, fixed damage)
        // baseDmg = TowerAttackDamage * (1 ± variance), uniformly distributed
        // E.g. 0.2 = 80%-120% of base damage per hit
        public float[] TowerDamageVariance = new float[MAX_ENTITIES];

        // ==================== 塔持续升温伤害 (Ramp-Up / Spool-Up Damage) ====================
        // TowerRampUpRate: damage increase per consecutive hit on same target (0 = no ramp-up)
        // E.g. 0.05 = +5% per hit, stacks up to TowerRampUpMax cap
        public float[] TowerRampUpRate = new float[MAX_ENTITIES];
        // TowerRampUpMax: maximum damage multiplier cap (e.g. 2.0 = 200% max dmg)
        // Default 1.0 = no ramp-up (no increase)
        public float[] TowerRampUpMax = new float[MAX_ENTITIES];
        // TowerRampUpCurrent: current accumulated ramp-up multiplier (starts at 1.0)
        // Reset to 1.0 on target switch (if RampUpResetOnSwitch is true)
        public float[] TowerRampUpCurrent = new float[MAX_ENTITIES];
        // TowerRampUpTargetId: entity ID of the target being tracked for ramp-up (-1 = none)
        // Used to detect target switches and reset the ramp-up multiplier
        public int[] TowerRampUpTargetId = new int[MAX_ENTITIES];
        // TowerRampUpResetOnSwitch: if true (default), ramp-up resets on target switch
        // If false, ramp-up persists even when switching targets
        public bool[] TowerRampUpResetOnSwitch = new bool[MAX_ENTITIES];

        // ==================== 塔伤害反弹系统 (Reflect Tower) ====================
        // TowerReflectRatio: fraction of damage received that is reflected back to attacker (e.g. 0.3 = 30% reflect)
        // Applied when this tower is attacked by an enemy — reflects back to the attacking enemy.
        // Default 0 = no reflect. Max reasonable value ~0.5 (50% — beyond that creates runaway feedback).
        public float[] TowerReflectRatio = new float[MAX_ENTITIES];
        // TowerReflectCap: maximum total reflect damage per frame for this tower (prevents oneshot from large hits)
        // Default 0 = no cap. If > 0, reflects min(TowerReflectRatio * damage, TowerReflectCap).
        public float[] TowerReflectCap = new float[MAX_ENTITIES];
        // TowerReflectAuraRadius: if > 0, nearby towers within radius also reflect damage when this tower is hit
        // Creates a "reflect aura" — group，塔共享反射光环
        public float[] TowerReflectAuraRadius = new float[MAX_ENTITIES];

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
            TowerDamageConversionRatio[entityId] = 0f; // default: no conversion
            TowerConvertedDamageType[entityId] = DamageType.Physical;
            TowerTurnRate[entityId] = turnRate;
            // Fog of War: default to no fog restriction (visionRadius=0 means see all)
            TowerVisionRadius[entityId] = 0f;
            // Arc projectile fields: default to straight trajectory (0=straight, 1=homing, 2=arc)
            TowerProjectileArcType[entityId] = 0;
            TowerProjectileArcPeakHeight[entityId] = 0f;
            TowerProjectileGravityScale[entityId] = 1f;
            // Heat/overheat fields: default to no heat, no overheat
            TowerHeat[entityId] = 0f;
            TowerMaxHeat[entityId] = 0f;
            TowerHeatPerShot[entityId] = 0f;
            TowerHeatCooldownRate[entityId] = 0f;
            TowerIsOverheated[entityId] = false;
            TowerOverheatTimer[entityId] = 0f;
            TowerOverheatBonus[entityId] = 1f;
            TowerOverheatPenalty[entityId] = 0f;
            TowerCanOverheat[entityId] = false;
            // Tower energy fields: default to no energy (0 capacity = no energy system)
            TowerEnergy[entityId] = 0f;
            TowerMaxEnergy[entityId] = 0f;
            TowerEnergyPerShot[entityId] = 0f;
            TowerEnergyRegen[entityId] = 0f;
            TowerIsEnergyTower[entityId] = false;
            TowerEnergyRegenRadius[entityId] = 0f;
            // Beam tower fields: default to no beam (not a beam tower)
            TowerIsBeam[entityId] = false;
            TowerBeamDPS[entityId] = 0f;
            TowerBeamChainCount[entityId] = 0;
            TowerBeamChainDecay[entityId] = 1f;
            TowerBeamMaxRange[entityId] = 0f;
            // Sabotage/tower disable fields: default to not disabled
            TowerIsDisabled[entityId] = false;
            TowerDisabledTimer[entityId] = 0f;
            TowerDisabledDuration[entityId] = 0f;
            // Reflect tower fields: default to no reflect (0 ratio = inactive)
            TowerReflectRatio[entityId] = 0f;
            TowerReflectCap[entityId] = 0f;
            TowerReflectAuraRadius[entityId] = 0f;
            // Falloff fields: default to no falloff
            TowerFalloffType[entityId] = 0;
            TowerFalloffStartRatio[entityId] = 1f;
            TowerFalloffMinRatio[entityId] = 1f;
            // Chain attack: default to no chain (0 ratio = inactive)
            TowerChainDmgRatio[entityId] = 0f;
            // Overkill / excess damage: default to no overkill effect (type=0, ratio=0, radius=0)
            TowerOverkillType[entityId] = 0;
            TowerOverkillRatio[entityId] = 0f;
            TowerOverkillRadius[entityId] = 0f;
            // Kill-triggered cooldown reset: default to no reset (type=0, amount=0)
            TowerResetOnKill[entityId] = 0;
            TowerResetAmount[entityId] = 0f;
            // Burst fire: default to no burst (0 count = single-shot)
            TowerBurstCount[entityId] = 0;
            TowerBurstInterval[entityId] = 0f;
            TowerBurstCooldown[entityId] = 0f;
            TowerBurstTimer[entityId] = 0f;
            TowerBurstShotsFired[entityId] = 0;
            // Ramp-Up / Spool-Up: default to no ramp-up (rate=0, max=1, current=1.0, no target)
            TowerRampUpRate[entityId] = 0f;
            TowerRampUpMax[entityId] = 1f;
            TowerRampUpCurrent[entityId] = 1f;
            TowerRampUpTargetId[entityId] = -1;
            TowerRampUpResetOnSwitch[entityId] = true;
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
            TowerDamageConversionRatio[entityId] = 0f;
            TowerConvertedDamageType[entityId] = DamageType.Physical;
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
            // Arc projectile fields reset
            TowerProjectileArcType[entityId] = 0;
            TowerProjectileArcPeakHeight[entityId] = 0f;
            TowerProjectileGravityScale[entityId] = 1f;
            // Heat/overheat fields reset
            TowerHeat[entityId] = 0f;
            TowerMaxHeat[entityId] = 0f;
            TowerHeatPerShot[entityId] = 0f;
            TowerHeatCooldownRate[entityId] = 0f;
            TowerIsOverheated[entityId] = false;
            TowerOverheatTimer[entityId] = 0f;
            TowerOverheatBonus[entityId] = 1f;
            TowerOverheatPenalty[entityId] = 0f;
            TowerCanOverheat[entityId] = false;
            // Tower energy fields reset
            TowerEnergy[entityId] = 0f;
            TowerMaxEnergy[entityId] = 0f;
            TowerEnergyPerShot[entityId] = 0f;
            TowerEnergyRegen[entityId] = 0f;
            TowerIsEnergyTower[entityId] = false;
            TowerEnergyRegenRadius[entityId] = 0f;
            // Beam tower fields reset
            TowerIsBeam[entityId] = false;
            TowerBeamDPS[entityId] = 0f;
            TowerBeamChainCount[entityId] = 0;
            TowerBeamChainDecay[entityId] = 1f;
            TowerBeamMaxRange[entityId] = 0f;
            // Chain attack field reset
            TowerChainDmgRatio[entityId] = 0f;
            // Overkill / excess damage field reset
            TowerOverkillType[entityId] = 0;
            TowerOverkillRatio[entityId] = 0f;
            TowerOverkillRadius[entityId] = 0f;
            // Kill-triggered cooldown reset field reset
            TowerResetOnKill[entityId] = 0;
            TowerResetAmount[entityId] = 0f;
            // Burst fire fields reset
            TowerBurstCount[entityId] = 0;
            TowerBurstInterval[entityId] = 0f;
            TowerBurstCooldown[entityId] = 0f;
            TowerBurstTimer[entityId] = 0f;
            TowerBurstShotsFired[entityId] = 0;
            // Falloff fields reset
            TowerFalloffType[entityId] = 0;
            TowerFalloffStartRatio[entityId] = 1f;
            TowerFalloffMinRatio[entityId] = 1f;
            // Ramp-Up fields reset
            TowerRampUpRate[entityId] = 0f;
            TowerRampUpMax[entityId] = 1f;
            TowerRampUpCurrent[entityId] = 1f;
            TowerRampUpTargetId[entityId] = -1;
            TowerRampUpResetOnSwitch[entityId] = true;
            // Sabotage/tower disable fields reset
            TowerIsDisabled[entityId] = false;
            TowerDisabledTimer[entityId] = 0f;
            TowerDisabledDuration[entityId] = 0f;
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
