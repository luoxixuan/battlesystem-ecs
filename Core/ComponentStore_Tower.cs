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
        // Tower elemental affinity: -1 = no affinity (zero-overhead path). 0..3 = Fire/Ice/Lightning/Poison (see Core/ElementType.cs).
        // When non-negative and the enemy has the matching element, the tower's damage is multiplied by (1 + TowerElementalAffinityBonus).
        public int[] TowerElementalAffinity = new int[MAX_ENTITIES];
        // Tower elemental affinity bonus: fraction multiplier when affinity matches enemy element (0.30 = +30% damage). 0 = inactive.
        public float[] TowerElementalAffinityBonus = new float[MAX_ENTITIES];
        // Tower projectile homing: if true, this tower's projectiles track targets mid-flight
        public bool[] TowerProjectileHoming = new bool[MAX_ENTITIES];
        // Tower intercept rate: probability of intercepting enemy projectiles (for PointDefense towers)
        // Stored separately from TowerCritChance to keep concerns isolated (reuse CritChance as intercept rate when needed)
        public float[] TowerInterceptRate = new float[MAX_ENTITIES];
        // Tower damage type: determines which resistance the target uses for mitigation.
        public DamageType[] TowerDamageType = new DamageType[MAX_ENTITIES];
        // Round 100 — Palisade Tower Fields ─────────────────────────────────────────
        // TowerIsPalisade: true if this tower is a Palisade (control-type, no attack).
        // Default false = backward compatible zero-overhead path. Set at PlaceTower based on
        // TowerType == Palisade.
        public bool[] TowerIsPalisade = new bool[MAX_ENTITIES];
        // PalisadeStunFrames: number of frames the palisade stuns an enemy on collision
        // (range check inside EnemyMovementSystem). Stun is applied via existing
        // EnemyStunDurationLeft[] field so duration countdown reuses the standard path.
        public int[] PalisadeStunFrames = new int[MAX_ENTITIES];
        // PalisadeBlockRadius: Manhattan-style distance in grid cells within which
        // an enemy's movement is delayed. 1 = 3x3 cell block centered on the palisade.
        public int[] PalisadeBlockRadius = new int[MAX_ENTITIES];
        // PalisadeHP: health pool of the palisade tower. Enemies deal melee damage to
        // nearby palisades; HP=0 → DestroyEntity. Default 0 means indestructible (legacy).
        public float[] PalisadeHP = new float[MAX_ENTITIES];
        // PalisadeMaxHP: snapshot of starting HP for UI / repair computations.
        public float[] PalisadeMaxHP = new float[MAX_ENTITIES];
        // PalisadeContactDamageAccumulator: per-frame contact-damage accumulator (parallel-safe
        // by index). Each Parallel.For enemy iteration adds EnemyContactDamageToPalisade to
        // PalisadeHP[towerId] — Interlocked? No, we only WRITE per tower (last-write-wins within
        // a frame is acceptable for a stagger damage per frame), so a flat array add is safe.
        // Reset to 0 at start of each frame's palisade pass; EnemyMovementSystem reads the
        // accumulated damage and subtracts from PalisadeHP in the serial pass.
        // Round 100 — used to be: HashSet<int> in EnemyMovementSystem (NOT thread-safe).
        // Replaced with parallel-safe index-based accumulator (Claude bug scan fix #1).
        public float[] PalisadeContactDamageAccumulator = new float[MAX_ENTITIES];
        // PalisadeDestroyFlag: per-tower destroy request flag (parallel-safe by index).
        // Set true inside Parallel.For when palisade HP drops to 0. The serial pass after
        // Parallel.For scans ActiveTowerIds and DestroyEntity any with flag set. Replaces
        // the old HashSet<int> _palisadeDestroyQueue (Claude bug scan fix #1).
        public bool[] PalisadeDestroyFlag = new bool[MAX_ENTITIES];
        // Tower damage conversion: fraction of damage converted to ConvertedDamageType (0 = no conversion)
        // E.g. 0.5 = 50% damage converted, bypassing enemy immunity to primary type
        public float[] TowerDamageConversionRatio = new float[MAX_ENTITIES];
        // Tower converted damage type: the target type for damage conversion
        public DamageType[] TowerConvertedDamageType = new DamageType[MAX_ENTITIES];
        // ── Mana Drain (Round 101 Direction 10) ─────────────────────────────
        // TowerManaDrainPct: fraction of target enemy's current mana drained on hit.
        // 0 = no drain. 0.1 = 10% of target's current mana → player mana per hit.
        // Default 0 = backward compatible; designers opt-in per-tower via TowerConfig.ManaDrainPct.
        public float[] TowerManaDrainPct = new float[MAX_ENTITIES];
        // TowerManaDrainCap: per-hit cap on drained mana (overrides global ManaDrainConfig.ManaDrainCap).
        // 0 = use global cap. Designers can tighten per-tower if needed.
        public float[] TowerManaDrainCap = new float[MAX_ENTITIES];
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
        // Total gold spent upgrading this tower (cumulative across all levels + path switches).
        // Used by TowerPlacementSystem.SellTower to compute the salvage refund (TowerTotalUpgradeSpent × salvageUpgradeRate).
        public float[] TowerTotalUpgradeSpent = new float[MAX_ENTITIES];
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
        // ── Kill-Triggered Player Sustain (Leech/Vampiric/Soul-Drain towers) ────
        // TowerHealOnKillAmount: HP restored to the owning player whenever this tower scores a kill.
        // TowerHealOnKillAmount: HP restored to the owning player whenever this tower scores a kill.
        // Capped at PlayerMaxHealth by store.SetPlayerCurrentHealth's natural ceiling.
        // Default 0 = disabled (backward compatible).
        public float[] TowerHealOnKillAmount = new float[MAX_ENTITIES];
        // Round 71 — On-Hit Lifesteal: HP restored to the owning player for every damage
        // instance this tower lands on an enemy (proportional to raw damage). Works on
        // multi-projectile and AoE hits; final heal is capped per-frame so a 10K-enemy
        // burst can't overheal. Default 0f = inactive (zero-overhead hot path).
        // - LifestealFraction: ratio of raw damage converted to heal (e.g. 0.20 = 20% vamp)
        // - LifestealMaxPerFrame: hard ceiling on per-frame heal sum (e.g. 50f = no overheal)
        public float[] TowerLifestealFraction = new float[MAX_ENTITIES];
        public float[] TowerLifestealMaxPerFrame = new float[MAX_ENTITIES];
        // TowerManaOnKillAmount: mana restored to the owning player whenever this tower scores a kill.
        // Capped at PlayerMaxMana inside AddPlayerMana. Default 0 = disabled.
        public float[] TowerManaOnKillAmount = new float[MAX_ENTITIES];
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

        // ==================== 塔词缀槽位 (Tower Affix Slots — Reforge Split A) ====================
        // TowerAffixSlotCount: how many affix slots this tower has (0 = no affixes, 1-3 = enabled)
        // Default 0 keeps backward compatibility for existing towers that haven't been reforged.
        public int[] TowerAffixSlotCount = new int[MAX_ENTITIES];
        // TowerAffixIds: [slotIndex][towerId] = index into GameConfig.TowerAffixes[] (-1 = empty slot)
        // Jagged array pattern matches TowerMorphDamage etc. — avoids MAX_AFFIX_SLOTS × MAX_ENTITIES
        // flat allocation (100K × 3 = 300K ints but only towers with slots pay the per-slot cost).
        public int[][] TowerAffixIds = new int[3][];
        // TowerAffixStackCount: [slotIndex][towerId] = number of times this slot's affix is stacked
        // (1 = single, N = up to TowerAffixDef.MaxStack). Default 0 = no affix assigned.
        public int[][] TowerAffixStackCount = new int[3][];
        // TowerAffixLockMask: bitmask of which slots are locked against reroll (bit 0 = slot 0, bit 1 = slot 1, bit 2 = slot 2)
        // 0 = no locks. Reforge Split B: locked slots retain their affix during RerollAffix. Default 0.
        public int[] TowerAffixLockMask = new int[MAX_ENTITIES];
        // TowerReforgeCount: how many times this tower has been reforged (RerollAffix calls). Drives cost curve.
        // Used by Reforge Split B to compute RerollCost(base + count * increment). Default 0.
        public int[] TowerReforgeCount = new int[MAX_ENTITIES];

        // ==================== 塔击退/位移效果 (Knockback) ====================
        // TowerKnockbackForce: strength of knockback applied to enemies on hit (0 = no knockback)
        // Positive values push enemies backward along the path direction
        public float[] TowerKnockbackForce = new float[MAX_ENTITIES];
        // TowerKnockbackRadius: radius within which knockback force is fully applied (beyond it, no effect)
        public float[] TowerKnockbackRadius = new float[MAX_ENTITIES];

        // ==================== 视线系统 (Line of Sight / LoS Blocker) ====================
        // TowerRequiresLOS: when true, the tower can only target enemies with an unobstructed
        // line-of-sight raycast (no other LoS-blocking tower in any grid cell between the tower
        // and the enemy). When false (default), the tower ignores LoS — backward compatible.
        // Stealth/sniper towers set this true; standard towers leave it false.
        public bool[] TowerRequiresLOS = new bool[MAX_ENTITIES];
        // TowerBlocksLOS: when true, the tower itself blocks other towers' line-of-sight rays.
        // Wall / obstacle / shroud towers set this true; standard towers leave it false.
        public bool[] TowerBlocksLOS = new bool[MAX_ENTITIES];
        // TowerIsPhasing: when true, the tower's targeting ignores LoS-blocking towers entirely
        // (its shots phase through obstacles, regardless of TowerRequiresLOS). Dual of the LoS
        // system: LoS = sniper requires clear sight; Phasing = ghost ignores blockers. Default
        // false — backward compatible (no overhead when no phasing tower is on the field).
        public bool[] TowerIsPhasing = new bool[MAX_ENTITIES];

        // ==================== 塔散射/多重射击（Scatter / Multi-shot）====================
        // TowerProjectileCount: number of projectiles fired per attack (1 = single shot, >1 = scatter/multicast)
        public int[] TowerProjectileCount = new int[MAX_ENTITIES];
        // TowerScatterAngle: angular spread in radians for multi-shot (0 = all projectiles aimed at target)
        public float[] TowerScatterAngle = new float[MAX_ENTITIES];
        // TowerPelletDamageMult: per-pellet damage multiplier (1.0 = full damage each; 0.4 = classic shotgun where 5 pellets each deal 40%)
        // Default 1.0 to keep existing scatter behavior intact when this field is not configured
        public float[] TowerPelletDamageMult = new float[MAX_ENTITIES];
        // TowerPelletConeRadius: search radius in tiles around primary target for finding unique pellet targets
        // (0 = use TowerRange for search; >0 = use explicit cone radius for shotgun-style fan)
        public float[] TowerPelletConeRadius = new float[MAX_ENTITIES];

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
        // Round 91 同类塔聚集 tier：0=无 tier / 1=tier1 (3 塔聚集) / 2=tier2 (5 塔) / 3=tier3 (8 塔)
        // 越高 tier = 越高的 damage mult 叠加。零开销（默认 0）
        public int[] TowerSynergyTier = new int[MAX_ENTITIES];

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

        // ==================== 嘲讽塔 (Taunt Tower — 强制敌人攻击该塔) ====================
        // TowerIsTaunt: true if this tower is a taunt tower that forces nearby enemies to target it
        //   (dual of the Aggro/Leash system: Aggro = enemy actively chases player; Taunt = tower
        //   actively forces enemy to attack itself). Default false — zero-overhead when no
        //   taunt tower is on the field.
        public bool[] TowerIsTaunt = new bool[MAX_ENTITIES];
        // TowerTauntRadius: world-units radius within which enemies are forced to retarget this
        //   tower. 0 = no taunt effect (TowerIsTaunt=true with radius 0 = inert). Towers with
        //   IsTaunt=false skip all taunt work in the hot path.
        public float[] TowerTauntRadius = new float[MAX_ENTITIES];

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

        // ==================== 冰霜减速区 (Frost Zone — Round 82 Direction 1) ====================
        // TowerFrostZoneRadius: radius in grid cells of the "frost tile" centered on this tower.
        // Enemies inside the radius have their effective move speed multiplied by
        // TowerFrostZoneSlowFactor (lower = stronger slow; 1.0 = no slow). 0 = no frost zone
        // (zero-overhead default; no allocations, no per-frame work for non-frost towers).
        public float[] TowerFrostZoneRadius = new float[MAX_ENTITIES];
        // TowerFrostZoneSlowFactor: per-tower slow factor applied to enemies in the zone
        // (0.5 = 50% move speed, 0.3 = 30% move speed). Multiple overlapping zones take the
        // MIN (most severe) of all contributing towers (resolved in FrostZoneSystem).
        public float[] TowerFrostZoneSlowFactor = new float[MAX_ENTITIES];
        // TowerFrostZoneDuration: seconds the zone has been active (0 = permanent, >0 = decaying).
        // Decremented each frame; when the timer hits 0, the tower's frost zone disables itself
        // by writing 0 to TowerFrostZoneRadius. Default 0 means permanent (zero-decrement path).
        public float[] TowerFrostZoneDuration = new float[MAX_ENTITIES];

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

        // ==================== 塔幸运/掉落稀有度加成 (Tower Luck) ====================
        // TowerLuck: flat bonus added to Rare/Epic/Legendary pickup tier weight (per tower).
        // 0 = no luck contribution (default, zero-overhead path).
        // Each tower with luck > 0 nudges the global pickup tier roll toward rarer drops
        // for the owning player, capped by PickupRarityConfig.MaxLuckBonus.
        public float[] TowerLuck = new float[MAX_ENTITIES];

        // ==================== 路径吸附塔（Path-Hug Tower）====================
        // TowerPathHugOnly: when true, the tower can only target enemies currently on a path
        // (EnemyPathId[enemyId] >= 0). Designed for "Roadblock" / "Path Sentry" style towers
        // whose semantics are "attack enemies traversing the path" — avoids wasting shots on
        // off-path stealth / decoy enemies. Default false = no filter (backward compatible).
        public bool[] TowerPathHugOnly = new bool[MAX_ENTITIES];

        // ==================== 锁定目标塔（Target Lock-On Tower）====================
        // TowerIsLockOn: when true, the tower caches its first-selected target into
        // TowerLockedTargetId and re-targets the same enemy each frame (ignoring CC/Fear
        // transitions and superior-scored candidates). Default false = no lock-on
        // (backward compatible, zero-overhead hot path).
        public bool[] TowerIsLockOn = new bool[MAX_ENTITIES];
        // TowerLockedTargetId: cached enemy ID this lock-on tower is currently locked onto
        // (-1 = no active lock, e.g. no enemies in range or target died). Cleared when the
        // locked target becomes inactive / out of range.
        public int[] TowerLockedTargetId = new int[MAX_ENTITIES];

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

        // ==================== 塔属性被吸取组件（Stat Drain）====================
        // TowerBaseDamage: original tower damage captured at placement — used to restore
        // TowerAttackDamage when a drainer enemy dies or leaves range. Cached so upgrades
        // applied to TowerAttackDamage don't permanently lower the "base" reference.
        public float[] TowerBaseDamage = new float[MAX_ENTITIES];
        // TowerDrainedByEnemy: ID of the enemy currently draining this tower, or -1 if not drained.
        // Single attribution per tower (one drainer at a time) to avoid split-stacking.
        public int[] TowerDrainedByEnemy = new int[MAX_ENTITIES];
        // TowerCurrentDrain: current fraction of base damage drained (0 = undrained, DrainRatio = capped).
        // TowerAttackDamage is dynamically recomputed as TowerBaseDamage * (1 - TowerCurrentDrain).
        public float[] TowerCurrentDrain = new float[MAX_ENTITIES];
        // TowerDamageAtDrainStart: snapshot of TowerAttackDamage when the drainer claimed this
        // tower. On release we restore TowerAttackDamage to this value (not to TowerBaseDamage)
        // so that upgrades applied DURING the drain window persist. Caveat: upgrades applied
        // AFTER the drain starts but BEFORE the drain ends are preserved; upgrades applied
        // while a drain is in progress and whose multiplier was rolled into the "drained"
        // TowerAttackDamage will need to be re-applied at release if we want exact fidelity.
        // For this implementation, we accept "drain is a multiplicative penalty on the value
        // at the moment of release" — upgrades during drain are kept in the snapshot.
        public float[] TowerDamageAtDrainStart = new float[MAX_ENTITIES];

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

        // ==================== 可部署陷阱塔 (Deployable Trap Tower) ====================
        // Passive "tower" type — does not actively attack, instead triggers an effect
        // on enemies that walk into its trigger radius. Each trigger consumes 1 charge.
        // When charges hit 0, the trap is destroyed (auto-removed from active tower list).
        // Conceptually similar to a stationary, one-shot tower.
        // TowerIsTrap: true if this tower is a passive trap (no active attacks, only triggers)
        public bool[] TowerIsTrap = new bool[MAX_ENTITIES];
        // TowerTrapTriggerRadius: in grid units, the radius within which enemies trigger the trap
        // 0 = disabled / passive (no trigger check)
        public float[] TowerTrapTriggerRadius = new float[MAX_ENTITIES];
        // TowerTrapCharges: remaining trigger count. Decremented per enemy trigger.
        // 0 = inactive (trap cannot trigger), -1 = unlimited charges.
        public int[] TowerTrapCharges = new int[MAX_ENTITIES];
        // TowerTrapEffectType: 0=none (no trap configured), 1=stun, 2=damage, 3=slow
        public int[] TowerTrapEffectType = new int[MAX_ENTITIES];
        // TowerTrapEffectValue: stun duration (sec) / damage (flat HP) / slow amount (0-1) per trigger
        public float[] TowerTrapEffectValue = new float[MAX_ENTITIES];

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

        // ==================== 玩家停用塔 (Player-Disabled Tower) ====================
        // TowerPlayerDisabled: persistent player-initiated toggle. While true, the tower
        // does not attack and does not generate income (income towers also check this).
        // Distinct from TowerIsDisabled (enemy sabotage) — both can be true simultaneously.
        // The two are OR-gated in TowerAttackSystem.Update() so the tower stays inert until
        // BOTH flags clear. ToggleTower() in TowerPlacementSystem flips this flag.
        // Default false (zero-overhead path: when false, the gate is a single array read).
        public bool[] TowerPlayerDisabled = new bool[MAX_ENTITIES];

        // ==================== 塔蓄力/前摇 (Tower Windup / Pre-Cast) ====================
        // TowerWindupFrames: number of frames between cooldown end and actual fire (0 = no windup, instant fire).
        // When > 0, the tower will count down TowerWindupCountdown to 0 before releasing its shot.
        // Common in MOBAs/high-damage skills: a 5-30 frame "charging" phase gives enemies a window
        // to interrupt with CC (silence/stun) — high-risk high-reward tradeoff for the player.
        // Default 0 (zero-overhead path: when frames=0, countdown is never checked).
        public int[] TowerWindupFrames = new int[MAX_ENTITIES];
        // TowerWindupCountdown: frames remaining before fire (set to WindupFrames when cooldown ends, decrements each frame).
        // When > 0, the tower is in "charging" state — target may have moved or CC may have landed.
        public int[] TowerWindupCountdown = new int[MAX_ENTITIES];

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

        // ==================== 塔受击反击 (Tower Retaliate) ====================
        // TowerRetaliateChance: per-hit probability (0..1) of triggering a retaliation strike back at the
        // attacking enemy when this tower takes damage. Retaliate is independent of Reflect:
        //   Reflect = "return a fraction of the damage received" (scales with the incoming hit)
        //   Retaliate = "fire a fixed-percentage-of-base-damage strike on a chance" (scales with this tower)
        // Default 0 = no retaliate (zero-cost path — branch is skipped on hot path).
        public float[] TowerRetaliateChance = new float[MAX_ENTITIES];
        // TowerRetaliateDamageMult: damage multiplier on a successful retaliate hit, applied to
        // TowerBaseDamage (NOT to the incoming damage). E.g. 0.5 = retaliate deals 50% of base damage
        // as a single independent strike to the attacker. Default 0 = no effect.
        public float[] TowerRetaliateDamageMult = new float[MAX_ENTITIES];

        // ==================== 塔诱饵 / 路径偏向 (Tower Lure / Bait) ====================
        // TowerLureRadius: radius (in tiles) within which this tower exerts a soft "pull" on enemies
        // — their movement is biased toward the tower. Differs from Pull (which is hard force):
        //   Pull = "physics displacement" (immediate positional offset each frame, hard control)
        //   Lure = "steering bias" (additive weight to enemy velocity toward this tower, soft control)
        // Default 0 = no lure (zero-cost path — branch is skipped on hot path).
        public float[] TowerLureRadius = new float[MAX_ENTITIES];
        // TowerLureStrength: max additional speed (tiles/frame) that the lure adds toward the tower
        // when an enemy is within TowerLureRadius. Applied as: dx = (tx - ex) / dist * TowerLureStrength.
        // E.g. 0.3 = enemy gains 0.3 tiles/frame of inward bias when fully inside the zone, scaling
        // linearly with proximity (max at center, 0 at the rim). Default 0 = no effect.
        public float[] TowerLureStrength = new float[MAX_ENTITIES];

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
            TowerBaseDamage[entityId] = damage;  // cache original damage for stat-drain restoration
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
            // Taunt tower fields: default to non-taunt (false/0)
            TowerIsTaunt[entityId] = false;
            TowerTauntRadius[entityId] = 0f;
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
            // Shotgun pellet fields: default to full damage per pellet + 0 cone radius (auto fallback to TowerRange)
            TowerPelletDamageMult[entityId] = 1f;
            TowerPelletConeRadius[entityId] = 0f;
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
            // LoS fields: default to no LoS requirement, no LoS blocking (backward compatible)
            TowerRequiresLOS[entityId] = false;
            TowerBlocksLOS[entityId] = false;
            // Phasing field: default to no phasing (regular tower, zero-overhead path)
            TowerIsPhasing[entityId] = false;
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
            // Round 101 — Mana Drain defaults: no drain until PlaceTower overrides
            TowerManaDrainPct[entityId] = 0f;
            TowerManaDrainCap[entityId] = 0f; // 0 = use global cap
            TowerTurnRate[entityId] = turnRate;
            // Round 100 — Palisade defaults: indestructible (HP=0) until PlaceTower overrides
            TowerIsPalisade[entityId] = false;
            PalisadeStunFrames[entityId] = 0;
            PalisadeBlockRadius[entityId] = 0;
            PalisadeHP[entityId] = 0f;
            PalisadeMaxHP[entityId] = 0f;
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
            // Tower Luck field: default to 0 (no luck contribution, zero-overhead)
            TowerLuck[entityId] = 0f;
            // Path-Hug filter: default to false (no path restriction, backward compatible)
            TowerPathHugOnly[entityId] = false;
            // Lock-On filter: default to false (no lock-on, backward compatible) + -1 = no cached target
            TowerIsLockOn[entityId] = false;
            TowerLockedTargetId[entityId] = -1;
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
            // Stat drain fields: default to no drainer (-1), 0% drained
            TowerDrainedByEnemy[entityId] = -1;
            TowerCurrentDrain[entityId] = 0f;
            // Snapshot used by the drain system to restore post-drain damage without losing
            // upgrades that happened between drain start and drain end.
            TowerDamageAtDrainStart[entityId] = 0f;
            // Cached base damage — reset to 0 so a recycled tower slot doesn't carry
            // over a stale base value before AddTower reinitializes it.
            TowerBaseDamage[entityId] = 0f;
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
            // Kill-triggered player sustain: default to no heal / no mana restore
            TowerHealOnKillAmount[entityId] = 0f;
            TowerManaOnKillAmount[entityId] = 0f;
            // Round 71 — On-Hit Lifesteal: default to no vampiric heal
            TowerLifestealFraction[entityId] = 0f;
            TowerLifestealMaxPerFrame[entityId] = 0f;
            // Retaliate fields: default to no retaliate (0% chance, 0% damage mult → branch skipped on hot path)
            TowerRetaliateChance[entityId] = 0f;
            TowerRetaliateDamageMult[entityId] = 0f;
            // Lure / bait fields: default to no lure (0 radius → branch skipped on hot path)
            TowerLureRadius[entityId] = 0f;
            TowerLureStrength[entityId] = 0f;
            // Burst fire: default to no burst (0 count = single-shot)
            TowerBurstCount[entityId] = 0;
            TowerBurstInterval[entityId] = 0f;
            TowerBurstCooldown[entityId] = 0f;
            TowerBurstTimer[entityId] = 0f;
            TowerBurstShotsFired[entityId] = 0;
            // Deployable trap fields: default to non-trap (false, radius=0, charges=0, no effect)
            // Trap towers are passive — they do not actively attack, instead they trigger
            // effects (stun / damage / slow) on enemies that walk into their trigger radius,
            // consuming one charge per trigger. When charges hit 0 the trap is destroyed.
            TowerIsTrap[entityId] = false;
            TowerTrapTriggerRadius[entityId] = 0f;
            TowerTrapCharges[entityId] = 0;
            TowerTrapEffectType[entityId] = 0;  // 0=none, 1=stun, 2=damage, 3=slow
            TowerTrapEffectValue[entityId] = 0f;
            // Ramp-Up / Spool-Up: default to no ramp-up (rate=0, max=1, current=1.0, no target)
            TowerRampUpRate[entityId] = 0f;
            TowerRampUpMax[entityId] = 1f;
            TowerRampUpCurrent[entityId] = 1f;
            TowerRampUpTargetId[entityId] = -1;
            TowerRampUpResetOnSwitch[entityId] = true;
            // Elemental affinity fields: default to no affinity (-1) and no bonus (0 = inactive zero-overhead path)
            TowerElementalAffinity[entityId] = -1;
            TowerElementalAffinityBonus[entityId] = 0f;
            // Frost Zone fields: default to no zone (radius=0, factor=1=passthrough, duration=0=permanent)
            // 1f is the neutral slow factor so the "no zone" default applies ZERO slow to enemies.
            TowerFrostZoneRadius[entityId] = 0f;
            TowerFrostZoneSlowFactor[entityId] = 1f;
            TowerFrostZoneDuration[entityId] = 0f;
            // Player-disabled flag: default false (active). ToggleTower() flips to true on player request.
            TowerPlayerDisabled[entityId] = false;
            // Round 98 — Tower Windup / Pre-cast: default to no windup (0 frames = instant fire, zero-overhead path)
            TowerWindupFrames[entityId] = 0;
            TowerWindupCountdown[entityId] = 0;
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
            // Taunt tower fields reset
            TowerIsTaunt[entityId] = false;
            TowerTauntRadius[entityId] = 0f;
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
            // Round 100 — Palisade fields reset (recycled slot must not leak palisade state)
            TowerIsPalisade[entityId] = false;
            PalisadeStunFrames[entityId] = 0;
            PalisadeBlockRadius[entityId] = 0;
            PalisadeHP[entityId] = 0f;
            PalisadeMaxHP[entityId] = 0f;
            // Round 100 — Palisade frame-scratch fields reset (Claude bug scan fix #1)
            PalisadeContactDamageAccumulator[entityId] = 0f;
            PalisadeDestroyFlag[entityId] = false;
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
            // Tower Luck field reset
            TowerLuck[entityId] = 0f;
            // Lock-On fields reset (no lock-on, no cached target — recycled slot starts inert)
            TowerIsLockOn[entityId] = false;
            TowerLockedTargetId[entityId] = -1;
            // Path-Hug filter reset
            TowerPathHugOnly[entityId] = false;
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
            // Kill-triggered player sustain field reset
            TowerHealOnKillAmount[entityId] = 0f;
            TowerManaOnKillAmount[entityId] = 0f;
            // Round 71 — On-Hit Lifesteal field reset
            TowerLifestealFraction[entityId] = 0f;
            TowerLifestealMaxPerFrame[entityId] = 0f;
            // Burst fire fields reset
            TowerBurstCount[entityId] = 0;
            TowerBurstInterval[entityId] = 0f;
            TowerBurstCooldown[entityId] = 0f;
            TowerBurstTimer[entityId] = 0f;
            TowerBurstShotsFired[entityId] = 0;
            // Deployable trap fields reset
            TowerIsTrap[entityId] = false;
            TowerTrapTriggerRadius[entityId] = 0f;
            TowerTrapCharges[entityId] = 0;
            TowerTrapEffectType[entityId] = 0;
            TowerTrapEffectValue[entityId] = 0f;
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
            // Stat drain fields reset
            TowerDrainedByEnemy[entityId] = -1;
            TowerCurrentDrain[entityId] = 0f;
            TowerDamageAtDrainStart[entityId] = 0f;
            TowerBaseDamage[entityId] = 0f;
            // Retaliate fields reset (chance=0 disables trigger, mult=0 disables damage — both defaults)
            TowerRetaliateChance[entityId] = 0f;
            TowerRetaliateDamageMult[entityId] = 0f;
            // Lure / bait fields reset (radius=0 disables lure, strength=0 disables bias)
            TowerLureRadius[entityId] = 0f;
            TowerLureStrength[entityId] = 0f;
            // Elemental affinity fields reset (-1 = no affinity, 0 = no bonus)
            TowerElementalAffinity[entityId] = -1;
            TowerElementalAffinityBonus[entityId] = 0f;
            // Frost Zone fields reset (radius=0=disabled, factor=1=neutral, duration=0=permanent off)
            TowerFrostZoneRadius[entityId] = 0f;
            TowerFrostZoneSlowFactor[entityId] = 1f;
            TowerFrostZoneDuration[entityId] = 0f;
            // Player-disabled flag reset (false = active tower on recycle — stale 'true' would leak)
            TowerPlayerDisabled[entityId] = false;
            // Round 98 — Windup fields reset (frames=0 = no windup, countdown=0 = not in windup)
            TowerWindupFrames[entityId] = 0;
            TowerWindupCountdown[entityId] = 0;
            // Phasing field reset (false = no phasing, zero-overhead)
            TowerIsPhasing[entityId] = false;
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

        // ==================== 塔词缀槽位 (Tower Affix Slots — Reforge Split A) ====================
        // Lazy-initializes the jagged slot arrays on first use. Called from accessor methods below.
        private const int TOWER_AFFIX_SLOT_COUNT = 3;

        private void EnsureAffixArrays(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= TOWER_AFFIX_SLOT_COUNT) return;
            if (TowerAffixIds[slotIndex] == null)
                TowerAffixIds[slotIndex] = new int[MAX_ENTITIES];
            if (TowerAffixStackCount[slotIndex] == null)
                TowerAffixStackCount[slotIndex] = new int[MAX_ENTITIES];
        }

        /// <summary>Gets the number of affix slots this tower has (0 = no affixes).</summary>
        public int GetTowerAffixSlotCount(int towerId)
        {
            if (!IsValidEntity(towerId)) return 0;
            return TowerAffixSlotCount[towerId];
        }

        /// <summary>Sets the number of affix slots for a tower (1-3, 0 to disable).</summary>
        public void SetTowerAffixSlotCount(int towerId, int slotCount)
        {
            if (!IsValidEntity(towerId)) return;
            if (slotCount < 0) slotCount = 0;
            if (slotCount > TOWER_AFFIX_SLOT_COUNT) slotCount = TOWER_AFFIX_SLOT_COUNT;
            // Pre-warm all slot arrays up to slotCount so reads are safe
            for (int s = 0; s < slotCount; s++)
                EnsureAffixArrays(s);
            TowerAffixSlotCount[towerId] = slotCount;
        }

        /// <summary>Gets the affix def index assigned to a given slot (returns -1 if empty).</summary>
        public int GetTowerAffixId(int towerId, int slotIndex)
        {
            if (!IsValidEntity(towerId)) return -1;
            if (slotIndex < 0 || slotIndex >= TOWER_AFFIX_SLOT_COUNT) return -1;
            if (TowerAffixIds[slotIndex] == null) return -1;
            return TowerAffixIds[slotIndex][towerId];
        }

        /// <summary>Assigns an affix def index to a slot. Pass -1 to clear.</summary>
        public void SetTowerAffixId(int towerId, int slotIndex, int affixIndex)
        {
            if (!IsValidEntity(towerId)) return;
            if (slotIndex < 0 || slotIndex >= TOWER_AFFIX_SLOT_COUNT) return;
            EnsureAffixArrays(slotIndex);
            TowerAffixIds[slotIndex][towerId] = affixIndex;
        }

        /// <summary>Gets the stack count for a slot (0 = no affix, 1+ = stacked).</summary>
        public int GetTowerAffixStackCount(int towerId, int slotIndex)
        {
            if (!IsValidEntity(towerId)) return 0;
            if (slotIndex < 0 || slotIndex >= TOWER_AFFIX_SLOT_COUNT) return 0;
            if (TowerAffixStackCount[slotIndex] == null) return 0;
            return TowerAffixStackCount[slotIndex][towerId];
        }

        /// <summary>Sets the stack count for a slot (1 = single, 2+ = stacked up to MaxStack).</summary>
        public void SetTowerAffixStackCount(int towerId, int slotIndex, int stackCount)
        {
            if (!IsValidEntity(towerId)) return;
            if (slotIndex < 0 || slotIndex >= TOWER_AFFIX_SLOT_COUNT) return;
            if (stackCount < 0) stackCount = 0;
            EnsureAffixArrays(slotIndex);
            TowerAffixStackCount[slotIndex][towerId] = stackCount;
        }

        /// <summary>
        /// Clears all affix data for a tower (used in DestroyEntity and when resetting a slot).
        /// Resets slot count to 0 and all 3 slot assignments to -1/0.
        /// Also resets the lock mask and reforge count (Reforge Split B).
        /// </summary>
        public void ClearTowerAffixes(int towerId)
        {
            if (!IsValidEntity(towerId)) return;
            TowerAffixSlotCount[towerId] = 0;
            for (int s = 0; s < TOWER_AFFIX_SLOT_COUNT; s++)
            {
                if (TowerAffixIds[s] != null) TowerAffixIds[s][towerId] = -1;
                if (TowerAffixStackCount[s] != null) TowerAffixStackCount[s][towerId] = 0;
            }
            // Reforge Split B: clear lock mask + reforge count too so reset is total
            TowerAffixLockMask[towerId] = 0;
            TowerReforgeCount[towerId] = 0;
        }

        // ==================== 词缀锁定/重洗 (Affix Lock + Reforge Count — Reforge Split B) ====================

        /// <summary>Gets the affix lock bitmask for a tower (bit s = slot s locked).</summary>
        public int GetTowerAffixLockMask(int towerId)
        {
            if (!IsValidEntity(towerId)) return 0;
            return TowerAffixLockMask[towerId];
        }

        /// <summary>Sets the affix lock bitmask for a tower. Out-of-range bits are masked off (bits 0-2 only).</summary>
        public void SetTowerAffixLockMask(int towerId, int mask)
        {
            if (!IsValidEntity(towerId)) return;
            // Only the low 3 bits are valid (we have TOWER_AFFIX_SLOT_COUNT=3 slots)
            TowerAffixLockMask[towerId] = mask & 0b0111;
        }

        /// <summary>Returns true if the given slot is locked against reroll.</summary>
        public bool IsTowerAffixSlotLocked(int towerId, int slotIndex)
        {
            if (!IsValidEntity(towerId)) return false;
            if (slotIndex < 0 || slotIndex >= TOWER_AFFIX_SLOT_COUNT) return false;
            return (TowerAffixLockMask[towerId] & (1 << slotIndex)) != 0;
        }

        /// <summary>Sets whether a single slot is locked. Returns true on success.</summary>
        public bool SetTowerAffixSlotLocked(int towerId, int slotIndex, bool locked)
        {
            if (!IsValidEntity(towerId)) return false;
            if (slotIndex < 0 || slotIndex >= TOWER_AFFIX_SLOT_COUNT) return false;
            if (locked) TowerAffixLockMask[towerId] |= (1 << slotIndex);
            else TowerAffixLockMask[towerId] &= ~(1 << slotIndex);
            return true;
        }

        /// <summary>Gets the number of reforges performed on this tower.</summary>
        public int GetTowerReforgeCount(int towerId)
        {
            if (!IsValidEntity(towerId)) return 0;
            return TowerReforgeCount[towerId];
        }

        /// <summary>Sets the reforge count (used by tests and by ReforgeSystem to increment).</summary>
        public void SetTowerReforgeCount(int towerId, int count)
        {
            if (!IsValidEntity(towerId)) return;
            if (count < 0) count = 0;
            TowerReforgeCount[towerId] = count;
        }

        /// <summary>Increments the reforge count by 1 (returns the new value).</summary>
        public int IncrementTowerReforgeCount(int towerId)
        {
            if (!IsValidEntity(towerId)) return 0;
            TowerReforgeCount[towerId]++;
            return TowerReforgeCount[towerId];
        }
    }
}
