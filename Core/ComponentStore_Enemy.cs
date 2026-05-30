using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Core
{
    public partial class ComponentStore
    {
        #region Enemy Fields
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

        // ==================== Enemy Clone (Duplicate Mid-Wave, SOA) ====================
        // EnemyCloneDefId: index into GameConfig.CloneDefs for this enemy's clone definition (-1 = none)
        public int[] EnemyCloneDefId = new int[MAX_ENTITIES];
        // EnemyCloneCooldown: remaining cooldown in seconds before this enemy can clone again
        public float[] EnemyCloneCooldown = new float[MAX_ENTITIES];
        // EnemyCloneTimer: clone duration in seconds (0 = permanent clone, -1 = no duration tracking)
        // When CloneDuration > 0 and timer expires, clone is killed (optional mechanic)
        public float[] EnemyCloneTimer = new float[MAX_ENTITIES];
        // EnemyCloneCount: number of active clones this enemy has currently spawned
        // Enforced against CloneDef.MaxClones
        public int[] EnemyCloneCount = new int[MAX_ENTITIES];
        // EnemyIsClone: true if this entity is a clone (cannot clone further, lower tower priority)
        public bool[] EnemyIsClone = new bool[MAX_ENTITIES];
        // EnemyCloneMasterId: the entity ID of the master that this clone was spawned from (-1 = none)
        public int[] EnemyCloneMasterId = new int[MAX_ENTITIES];

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

        // ==================== 敌人移动方向（背刺系统 SOA）====================
        // EnemyMoveDirX: normalized X component of enemy's current movement direction
        // EnemyMoveDirY: normalized Y component of enemy's current movement direction
        // Used for backstab/flank positional damage bonus (TowerAttackSystem).
        // Default (0,0) = no direction (stationary or unknown heading).
        public float[] EnemyMoveDirX = new float[MAX_ENTITIES];
        public float[] EnemyMoveDirY = new float[MAX_ENTITIES];

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

        #endregion

        // ==================== 敌人组件访问 ====================

        // ── O(1) enemy affix flag helpers ─────────────────────────────────
        public bool HasAffix(int enemyId, BuffType affix)
        {
            if (!IsValidEntity(enemyId)) return false;
            return (EnemyAffixFlags[enemyId] & affix) != 0;
        }

        public int AddEnemy(float startX, float startY, float moveSpeed, float health, float maxHealth, float damage, int goldReward, int waveNumber, string fullName = null, float armor = 0f, float shield = 0f, float magicResist = 0f)
        {
            int entityId = CreateEntity();

            if (!IsValidEntity(entityId)) 
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
            lock (activeIdsLock) { _activeEnemyIds.Add(entityId); _enemyIndexInList[entityId] = _activeEnemyIds.Count - 1; }
            return entityId;
        }

        // ==================== 敌人基础属性访问 ====================

        public float GetEnemyHealth(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return 0f;
            return EnemyHealth[enemyId];
        }

        public void SetEnemyHealth(int enemyId, float health)
        {
            if (!IsValidEntity(enemyId)) return;
            EnemyHealth[enemyId] = health;
        }

        public float GetEnemyMaxHealth(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return 0f;
            return EnemyMaxHealth[enemyId];
        }

        public float GetEnemyArmor(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return 0f;
            return EnemyArmor[enemyId];
        }

        public void SetEnemyArmor(int enemyId, float armor)
        {
            if (!IsValidEntity(enemyId)) return;
            EnemyArmor[enemyId] = armor;
        }

        /// <summary>
        /// Applies damage to an enemy, with shield absorbing damage before it reaches health.
        /// </summary>
        public void ApplyEnemyDamage(int enemyId, float damage)
        {
            if (!IsValidEntity(enemyId)) return;
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
            if (!IsValidEntity(enemyId)) return 0f;
            return EnemyMoveSpeed[enemyId];
        }

        public float GetEnemyDamage(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return 0f;
            return EnemyDamage[enemyId];
        }

        public int GetEnemyGoldReward(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return 0;
            return EnemyGoldReward[enemyId];
        }

        // ==================== CC (Crowd Control) helpers ====================
        /// <summary>Returns true if the enemy is currently stunned.</summary>
        public bool IsEnemyStunned(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return false;
            // Primary check: duration-based stun (set by ApplyEnemyStun, decremented by EnemyMovementSystem.Update)
            if (EnemyStunDurationLeft[enemyId] > 0f) return true;
            // Fallback: legacy flag (set by external systems, cleared by EnemyMovementSystem.SetTurn)
            return EnemyStunFlag[enemyId];
        }

        /// <summary>Applies a stun to the enemy for the current frame. Stun clears automatically at start of each frame via SetTurnCCFlags.</summary>
        public void ApplyStun(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return;
            EnemyStunFlag[enemyId] = true;
        }

        /// <summary>Applies a slow to the enemy. factor is a multiplier (e.g. 0.5 = 50% speed). Duration in turns tracked by EnemySlowDurationLeft.</summary>
        public void ApplySlow(int enemyId, float factor, int duration)
        {
            if (!IsValidEntity(enemyId)) return;
            if (factor <= 0f || factor >= 1f) return; // only valid slow factors

            float baseSpeed = EnemyMoveSpeedBase[enemyId];
            if (baseSpeed <= 0f) baseSpeed = EnemyMoveSpeed[enemyId];

            EnemySlowFactor[enemyId] = factor;
            EnemyMoveSpeed[enemyId] = baseSpeed * factor;
            EnemySlowDurationLeft[enemyId] = duration;
        }

        /// <summary>Clears slow effect and restores original speed.</summary>
        public void ClearSlow(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return;
            if (EnemySlowFactor[enemyId] <= 0f) return; // no slow active

            float baseSpeed = EnemyMoveSpeedBase[enemyId];
            if (baseSpeed > 0f)
                EnemyMoveSpeed[enemyId] = baseSpeed;
            EnemySlowFactor[enemyId] = 0f;
        }

        /// <summary>Applies stun to the enemy for `duration` turns. Stored in EnemyStunDurationLeft (not EnemyStunFlag) so it persists across frames.</summary>
        public void ApplyEnemyStun(int enemyId, int duration)
        {
            if (!IsValidEntity(enemyId)) return;
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
            if (!IsValidEntity(enemyId)) return;
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
            if (!IsValidEntity(enemyId)) return;
            if (EnemySlowFactor[enemyId] <= 0f) return;
            float baseSpeed = EnemyMoveSpeedBase[enemyId];
            if (baseSpeed > 0f)
                EnemyMoveSpeed[enemyId] = baseSpeed;
            EnemySlowFactor[enemyId] = 0f;
        }

        /// <summary>Clears wound slow effect on enemy and restores speed from wound state.</summary>
        public void ClearEnemyWound(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return;
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
            if (!IsValidEntity(enemyId)) return;
            if (force <= 0f) return;
            // Add to existing force (in case multiple towers hit simultaneously)
            EnemyKnockbackForceLeft[enemyId] += force;
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

        // ==================== 敌人 AI 组件访问 ====================

        public string GetEnemyAIAction(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return "";
            return EnemyAIAction[enemyId];
        }

        public string GetEnemyTypeName(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return "";
            return EnemyTypeName[enemyId] ?? "";
        }

        public void SetEnemyAIAction(int enemyId, string action)
        {
            if (!IsValidEntity(enemyId)) return;
            EnemyAIAction[enemyId] = action ?? "";
        }

        public int GetEnemyAIChargeCounter(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return 0;
            return EnemyAIChargeCounter[enemyId];
        }

        public void SetEnemyAIChargeCounter(int enemyId, int counter)
        {
            if (!IsValidEntity(enemyId)) return;
            EnemyAIChargeCounter[enemyId] = counter;
        }

        public int GetEnemyAILastAttackTurn(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return 0;
            return EnemyAILastAttackTurn[enemyId];
        }

        public void SetEnemyAILastAttackTurn(int enemyId, int turn)
        {
            if (!IsValidEntity(enemyId)) return;
            EnemyAILastAttackTurn[enemyId] = turn;
        }

        public EnemyActionType GetEnemyActionEnum(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return EnemyActionType.None;
            return EnemyActionEnum[enemyId];
        }

        public void SetEnemyActionEnum(int enemyId, EnemyActionType action)
        {
            if (!IsValidEntity(enemyId)) return;
            EnemyActionEnum[enemyId] = action;
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
