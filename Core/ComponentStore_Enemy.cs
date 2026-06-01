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
        // ==================== 元素护盾 (Elemental Shield — shield type with elemental weakness/resistance) ====================
        // EnemyShieldType: which element this shield is weak to (ElementType). None = no elemental interaction.
        // When damage of matching element hits this shield, damage is amplified (EnemyShieldWeakMult).
        // Damage of opposing element (next ordinal) is reduced (EnemyShieldResistMult).
        public ElementType[] EnemyShieldType = new ElementType[MAX_ENTITIES];
        // EnemyShieldWeakMult: damage multiplier applied to shield when hit by EnemyShieldType damage
        // (e.g. 2.0 = shield takes 2x damage, breaks much faster — "shield is weak to this element")
        public float[] EnemyShieldWeakMult = new float[MAX_ENTITIES];
        // EnemyShieldResistMult: damage multiplier applied to shield when hit by an element NOT matching EnemyShieldType
        // (e.g. 0.5 = shield takes half damage from off-type attacks — "shield resists other elements")
        public float[] EnemyShieldResistMult = new float[MAX_ENTITIES];
        // EnemyShieldBreakReaction: which element is applied to the enemy on shield break (ElementType).
        // None = no element applied. The reaction uses the existing ElementalReactionSystem pathway.
        public ElementType[] EnemyShieldBreakReaction = new ElementType[MAX_ENTITIES];
        // EnemyShieldBreakElementDuration: how long the break-reaction element lasts in seconds.
        public float[] EnemyShieldBreakElementDuration = new float[MAX_ENTITIES];
        // _pendingShieldBreaks: queue of enemy IDs whose elemental shield just broke this frame.
        // Consumed by ElementalReactionSystem.Update() to trigger break-element reactions
        // against any existing elements on the target (parallel-safe in serial phase).
        private readonly System.Collections.Generic.List<int> _pendingShieldBreaks = new System.Collections.Generic.List<int>(32);
        public System.Collections.Generic.List<int> PendingShieldBreaks => _pendingShieldBreaks;
        // ==================== N 击护盾 (Hit Shield — blocks N hits regardless of damage) ====================
        // EnemyHitShieldCount: current number of hit-shield layers (0 = no hit shield, attack passes through)
        // Each incoming tower/player attack removes exactly 1 layer — damage is fully blocked
        public float[] EnemyHitShieldCount = new float[MAX_ENTITIES];
        // EnemyHitShieldMax: maximum layers this enemy can have (Boss = 0 = immune to hit shields)
        public float[] EnemyHitShieldMax = new float[MAX_ENTITIES];
        // EnemyHitShieldTimer: seconds until next layer regenerates (0 = no regen, don't tick)
        public float[] EnemyHitShieldTimer = new float[MAX_ENTITIES];
        // EnemyHitShieldRegenInterval: seconds between layer regen ticks (0 = no regen)
        public float[] EnemyHitShieldRegenInterval = new float[MAX_ENTITIES];
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
        // ==================== 敌人堆叠惩罚 (Enemy Tile Stacking Penalty) ====================
        // EnemyStackCount: number of other enemies sharing the same cell this frame.
        // 0 = no stacking (alone in cell), 1 = one other enemy in same cell, etc.
        // Computed by EnemyMovementSystem after movement update using SpatialGrid data.
        public int[] EnemyStackCount = new int[MAX_ENTITIES];
        // EnemyStackSlowRatio: current slow multiplier from crowding (1.0 = no slow, 0.7 = 30% slow).
        // Stacking penalty (StackingConfig.PenaltyPerStack) is applied per stack and clamped to
        // [StackingConfig.MaxStackSlow, 1.0]. Reset to 1.0 each frame and recomputed serially.
        public float[] EnemyStackSlowRatio = new float[MAX_ENTITIES];
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
        // ==================== Enemy Path Deviation (Lateral X-axis Drift) ====================
        // EnemyPathDeviationType: 0=none (deterministic Y-axis only), 1=sine, 2=random
        // Default 0 keeps existing behavior. Sine produces a smooth wave lateral offset
        // (amplitude × sin(turn * frequency)). Random adds per-turn ±amplitude jitter.
        public int[] EnemyPathDeviationType = new int[MAX_ENTITIES];
        // EnemyPathDeviationAmplitude: max lateral X offset in world units (e.g. 0.5 = ±0.5 cells)
        public float[] EnemyPathDeviationAmplitude = new float[MAX_ENTITIES];
        // EnemyPathDeviationPhase: per-enemy phase offset (radians) for sine — de-synchronizes waves
        public float[] EnemyPathDeviationPhase = new float[MAX_ENTITIES];
        // EnemyPathDeviationSeed: per-enemy random-seed base for type=2 (deterministic per turn)
        public int[] EnemyPathDeviationSeed = new int[MAX_ENTITIES];
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
        // ==================== 敌人侧移/闪避移动 (Enemy Strafe / Dodge) ====================
        // EnemyDodgeChance: probability that this enemy dodges a tower attack (0.0-1.0, 0.0 = never dodge)
        // Checked in TowerAttackSystem when rolling accuracy/evasion; if succeeded, attack deals 0 damage
        public float[] EnemyDodgeChance = new float[MAX_ENTITIES];
        // EnemyDodgeDistance: how far the enemy strafes laterally when dodging (world units)
        // Applied as a +/- X offset added to PositionX during dodge movement
        public float[] EnemyDodgeDistance = new float[MAX_ENTITIES];
        // EnemyDodgeCooldown: turns remaining before dodge can be triggered again (0 = ready)
        public float[] EnemyDodgeCooldown = new float[MAX_ENTITIES];
        // EnemyDodgeTimer: countdown in seconds for periodic/periodic-random dodge behavior (0 = event-driven only)
        public float[] EnemyDodgeTimer = new float[MAX_ENTITIES];
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
        // EnemyHasTrueSight: true if this enemy can detect and target stealthed towers
        public bool[] EnemyHasTrueSight = new bool[MAX_ENTITIES];

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
        // EnemyIsFeared: bool flag set when enemy is currently under fear effect (synced from fear duration)
        public bool[] EnemyIsFeared = new bool[MAX_ENTITIES];
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
        // EnemyDamageImmunityMask: bit mask of damage types this enemy is immune to.
        // Computed from DamageImmunities[] in monster JSON. If (damageType & mask) != 0, damage = 0.
        // True damage (DamageType.True) bypasses immunity entirely and ignores this mask.
        // Values: Physical=1, Magic=2, Fire=4, Ice=8, Lightning=16. Default 0 = no immunity.
        public int[] EnemyDamageImmunityMask = new int[MAX_ENTITIES];
        // EnemyIsUnstoppable: total CC immunity flag. When true, enemy ignores ALL crowd control
        // (stun, freeze, slow, fear, knockback, pull, charm, taunt). Boss-level CC immunity.
        public bool[] EnemyIsUnstoppable = new bool[MAX_ENTITIES];
        // EnemyFearResistance: 0-1, reduces fear duration fraction (1 = complete fear immunity)
        public float[] EnemyFearResistance = new float[MAX_ENTITIES];

        // ==================== 自爆/殉爆敌人 (Suicide Bomber / Kamikaze, SOA) ====================
        // EnemyIsSuicide: true if this enemy is a suicide bomber that explodes near towers
        public bool[] EnemyIsSuicide = new bool[MAX_ENTITIES];
        // EnemySuicideTriggerRange: radius within which a suicide enemy triggers its explosion (distance to nearest tower)
        public float[] EnemySuicideTriggerRange = new float[MAX_ENTITIES];
        // EnemySuicideDmgRadius: AoE explosion radius when the suicide enemy detonates
        public float[] EnemySuicideDmgRadius = new float[MAX_ENTITIES];
        // EnemySuicideDmgAmount: raw damage of the suicide explosion (before falloff)
        public float[] EnemySuicideDmgAmount = new float[MAX_ENTITIES];

        // ==================== 敌人移动方向（背刺系统 SOA）====================
        // EnemyMoveDirX: normalized X component of enemy's current movement direction
        // EnemyMoveDirY: normalized Y component of enemy's current movement direction
        // Used for backstab/flank positional damage bonus (TowerAttackSystem).
        // Default (0,0) = no direction (stationary or unknown heading).
        public float[] EnemyMoveDirX = new float[MAX_ENTITIES];
        public float[] EnemyMoveDirY = new float[MAX_ENTITIES];

        // ==================== 幽灵/相位敌人组件（SOA）====================
        // EnemyIsPhased: true when enemy is in ghost/phase state — ignores tower attacks and obstacles
        // Can be countered by IsAntiPhase towers (magic towers that can hit phased enemies)
        public bool[] EnemyIsPhased = new bool[MAX_ENTITIES];
        // EnemyPhaseDuration: total duration of phase state in seconds (0 = permanent phase)
        public float[] EnemyPhaseDuration = new float[MAX_ENTITIES];
        // EnemyPhaseTimer: countdown timer — when reaches 0, phase ends (unless permanent)
        public float[] EnemyPhaseTimer = new float[MAX_ENTITIES];
        // EnemyPhaseCooldown: seconds until phase can be activated again (0 = can phase anytime)
        public float[] EnemyPhaseCooldown = new float[MAX_ENTITIES];
        // EnemyIsAntiPhase: true if this tower type can damage phased enemies
        public bool[] TowerIsAntiPhase = new bool[MAX_ENTITIES];

        // ==================== 敌人破坏/瘫痪塔能力组件（SOA）====================
        // EnemyCanSabotage: true if this enemy can disable/sabotage towers (EMP-like ability)
        public bool[] EnemyCanSabotage = new bool[MAX_ENTITIES];
        // EnemySabotageRadius: radius within which sabotage effect applies (AoE EMP)
        public float[] EnemySabotageRadius = new float[MAX_ENTITIES];
        // EnemySabotageDuration: how long the target tower stays disabled in seconds
        public float[] EnemySabotageDuration = new float[MAX_ENTITIES];
        // EnemySabotageTimer: countdown until next sabotage attack (0 = ready to attack)
        public float[] EnemySabotageTimer = new float[MAX_ENTITIES];
        // EnemySabotageCooldown: cooldown between sabotage attacks in seconds
        public float[] EnemySabotageCooldown = new float[MAX_ENTITIES];

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

        // ==================== 法力燃烧敌人组件（SOA）====================
        // EnemyManaBurnAmount: amount of mana this enemy burns per attack (0 = no mana burn)
        public float[] EnemyManaBurnAmount = new float[MAX_ENTITIES];
        // EnemyManaBurnRadius: range within which mana burn is effective (0 = global effect)
        public float[] EnemyManaBurnRadius = new float[MAX_ENTITIES];
        // EnemyManaBurnCooldown: seconds until mana burn can be used again (0 = ready)
        public float[] EnemyManaBurnCooldown = new float[MAX_ENTITIES];
        // EnemyManaBurnType: 0=flat, 1=percent_current, 2=percent_max (default 0=flat)
        public int[] EnemyManaBurnType = new int[MAX_ENTITIES];

        // ==================== 敌人吸血组件（SOA）====================
        // EnemyLifestealRatio: fraction of damage dealt that is healed back (0.3 = 30% lifesteal)
        public float[] EnemyLifestealRatio = new float[MAX_ENTITIES];
        // EnemyLifestealCap: maximum heal per attack event (prevents burst healing)
        public float[] EnemyLifestealCap = new float[MAX_ENTITIES];
        // EnemyLifestealActive: whether lifesteal is currently active (enemies can toggle it)
        public bool[] EnemyLifestealActive = new bool[MAX_ENTITIES];

        // ==================== 治疗抑制/重伤减免组件（SOA）====================
        // EnemyHealingReduction: fraction of healing that is suppressed (0-1).
        // 0 = no reduction, 0.5 = 50% healing blocked. Applied when tower attacks apply anti-heal debuff.
        public float[] EnemyHealingReduction = new float[MAX_ENTITIES];
        // EnemyHealingReductionDuration: remaining duration in turns for healing reduction (0 = no active reduction).
        public float[] EnemyHealingReductionDuration = new float[MAX_ENTITIES];

        // ==================== 保护者敌人组件（SOA）====================
        // EnemyIsProtector: true if this enemy is a protector/guardian that shields allies
        public bool[] EnemyIsProtector = new bool[MAX_ENTITIES];
        // EnemyProtectRadius: range within which this protector shields allies (world units)
        public float[] EnemyProtectRadius = new float[MAX_ENTITIES];
        // EnemyProtectDamageTransfer: fraction of damage redirected to the protector (0.5 = 50%)
        public float[] EnemyProtectDamageTransfer = new float[MAX_ENTITIES];
        // EnemyProtectMaxTargets: maximum number of allies this protector can shield (0 = unlimited)
        public int[] EnemyProtectMaxTargets = new int[MAX_ENTITIES];

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
            EnemyDamageImmunityMask[entityId] = 0;  // default: no damage immunities
            EnemyShield[entityId] = shield;  // configurable initial shield
            // Hit Shield: default 0 layers, 0 max, 0 regen timer
            EnemyHitShieldCount[entityId] = 0f;
            EnemyHitShieldMax[entityId] = 0f;
            EnemyHitShieldTimer[entityId] = 0f;
            EnemyHitShieldRegenInterval[entityId] = 0f;
            EnemyEvasion[entityId] = 0f;  // default to no evasion
            // Tile-stacking penalty: default 0 stack, 1.0 slow ratio (no penalty until first frame of crowding)
            EnemyStackCount[entityId] = 0;
            EnemyStackSlowRatio[entityId] = 1f;
            // Elemental Shield: default None, no weakness/resistance, no break reaction
            EnemyShieldType[entityId] = ElementType.None;
            EnemyShieldWeakMult[entityId] = 0f;   // 0 = use default 2x when triggered
            EnemyShieldResistMult[entityId] = 0f; // 0 = use default 0.5x when triggered
            EnemyShieldBreakReaction[entityId] = ElementType.None;
            EnemyShieldBreakElementDuration[entityId] = 0f;
            // Dodge/Strafe: default 0 chance, 0 distance, cooldown=0 (ready), timer=0 (event-driven)
            EnemyDodgeChance[entityId] = 0f;
            EnemyDodgeDistance[entityId] = 0f;
            EnemyDodgeCooldown[entityId] = 0f;
            EnemyDodgeTimer[entityId] = 0f;
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
            // Path deviation: default 0/0/0/0 = no lateral drift. WaveSpawningSystem overrides
            // per archetype if the monster config specifies deviation type/amplitude.
            EnemyPathDeviationType[entityId] = 0;
            EnemyPathDeviationAmplitude[entityId] = 0f;
            EnemyPathDeviationPhase[entityId] = 0f;
            EnemyPathDeviationSeed[entityId] = 0;

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

            // Mana Burn: default 0 (no mana burn ability)
            EnemyManaBurnAmount[entityId] = 0f;
            EnemyManaBurnRadius[entityId] = 0f;
            EnemyManaBurnCooldown[entityId] = 0f;
            EnemyManaBurnType[entityId] = 0;

            // Lifesteal: default 0 (no lifesteal)
            EnemyLifestealRatio[entityId] = 0f;
            EnemyLifestealCap[entityId] = 0f;
            EnemyLifestealActive[entityId] = false;

            // Protector: default false (no protector ability)
            EnemyIsProtector[entityId] = false;
            EnemyProtectRadius[entityId] = 0f;
            EnemyProtectDamageTransfer[entityId] = 0f;
            EnemyProtectMaxTargets[entityId] = 0;

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
            ApplyEnemyDamage(enemyId, damage, ElementType.None);
        }

        /// <summary>
        /// Applies damage to an enemy with an optional element tag, applying elemental shield rules.
        /// If the enemy has an EnemyShieldType that matches the incoming element, the shield takes
        /// EnemyShieldWeakMult damage (default 2.0 = breaks twice as fast). If the element is
        /// non-matching and the shield has EnemyShieldResistMult set, shield takes reduced damage.
        /// On shield break, the configured EnemyShieldBreakReaction element is applied to the enemy
        /// via the ElementalReactionSystem pathway.
        /// </summary>
        public void ApplyEnemyDamage(int enemyId, float damage, ElementType attackElement)
        {
            if (!IsValidEntity(enemyId)) return;
            if (damage <= 0f) return;

            float shield = EnemyShield[enemyId];
            if (shield <= 0f)
            {
                EnemyHealth[enemyId] -= damage;
                return;
            }

            // Apply elemental shield modifier if the enemy has a configured shield type
            // and the incoming attack carries an element tag.
            float shieldMult = 1f;
            bool shieldHasElement = EnemyShieldType[enemyId] != ElementType.None;
            if (shieldHasElement && attackElement != ElementType.None)
            {
                if (attackElement == EnemyShieldType[enemyId])
                {
                    // Weak element: amplify damage to shield (default 2x)
                    shieldMult = EnemyShieldWeakMult[enemyId] > 0f ? EnemyShieldWeakMult[enemyId] : 2f;
                }
                else
                {
                    // Off-element: resist (default 0.5x)
                    float resist = EnemyShieldResistMult[enemyId];
                    shieldMult = resist > 0f ? resist : 0.5f;
                }
                damage *= shieldMult;
            }

            if (shield >= damage)
            {
                EnemyShield[enemyId] = shield - damage;
                return;
            }
            float remaining = damage - shield;
            EnemyShield[enemyId] = 0f;
            EnemyHealth[enemyId] -= remaining;

            // Shield broke — apply break-reaction element to the enemy
            if (shieldHasElement)
            {
                ElementType breakElement = EnemyShieldBreakReaction[enemyId];
                if (breakElement != ElementType.None)
                {
                    float breakDur = EnemyShieldBreakElementDuration[enemyId] > 0f
                        ? EnemyShieldBreakElementDuration[enemyId]
                        : 2f;
                    // Apply element status mask and timer directly (parallel-safe in serial phase)
                    int elemIdx = ElementOrdinalForShield(breakElement);
                    if (elemIdx >= 0)
                    {
                        EnemyElementStatus[enemyId] |= breakElement;
                        // Refresh the break-element timer (use the longer of current vs. break duration)
                        float existing = EnemyElementTimer[enemyId * 4 + elemIdx];
                        if (existing < breakDur) EnemyElementTimer[enemyId * 4 + elemIdx] = breakDur;
                        // Enqueue for ElementalReactionSystem to process (check for further reactions)
                        _pendingShieldBreaks.Add(enemyId);
                    }
                }
            }
        }

        private static int ElementOrdinalForShield(ElementType element)
        {
            // Mirrors ElementalReactionSystem ordinal mapping (Fire=0, Ice=1, Lightning=2, Poison=3)
            switch (element)
            {
                case ElementType.Fire: return 0;
                case ElementType.Ice: return 1;
                case ElementType.Lightning: return 2;
                case ElementType.Poison: return 3;
                default: return -1;
            }
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
            // Check total CC immunity (unstoppable enemies ignore all CC)
            if (EnemyIsUnstoppable[enemyId]) return;
            if (factor <= 0f || factor >= 1f) return; // only valid slow factors
            // Apply slow resistance: effectiveFactor = factor + (1-factor) * resistance
            // e.g., 0.5 slow + 0.5 resistance → 0.5 + 0.5*0.5 = 0.75 (less effective slow)
            if (EnemySlowResistance[enemyId] > 0f)
            {
                factor = factor + (1f - factor) * EnemySlowResistance[enemyId];
                if (factor >= 1f) return; // fully resisted
            }

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
            // Check total CC immunity (unstoppable enemies ignore all CC)
            if (EnemyIsUnstoppable[enemyId]) return;
            // Apply stun resistance: reduce duration by resistance fraction
            if (EnemyStunResistance[enemyId] > 0f && duration > 0)
            {
                duration = (int)(duration * (1f - EnemyStunResistance[enemyId]));
                if (duration <= 0) return;
            }
            // Use duration-based stun so it survives the EnemyMovementSystem.SetTurn() clear
            if (duration > EnemyStunDurationLeft[enemyId])
                EnemyStunDurationLeft[enemyId] = duration;
            // Also set legacy flag for backward compat with IsEnemyStunned fallback
            EnemyStunFlag[enemyId] = true;
        }

        /// <summary>Applies freeze to the enemy for `duration` turns. Applies freeze resistance directly, then sets stun duration.</summary>
        public void ApplyEnemyFreeze(int enemyId, int duration)
        {
            if (!IsValidEntity(enemyId)) return;
            // Check total CC immunity (unstoppable enemies ignore all CC)
            if (EnemyIsUnstoppable[enemyId]) return;
            // Apply freeze resistance: reduce duration by resistance fraction
            if (EnemyFreezeResistance[enemyId] > 0f && duration > 0)
            {
                duration = (int)(duration * (1f - EnemyFreezeResistance[enemyId]));
                if (duration <= 0) return;
            }
            // Direct stun logic (don't call ApplyEnemyStun to avoid double-applying stun resistance)
            if (duration > EnemyStunDurationLeft[enemyId])
                EnemyStunDurationLeft[enemyId] = duration;
            EnemyStunFlag[enemyId] = true;
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
