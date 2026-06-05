using System.Collections.Generic;
using System.Linq;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
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
        // EnemyCCImmuneMask: per-enemy bitmask of CC types this enemy fully ignores (Round 97).
        // Stacks with EnemyIsUnstoppable: if either the bit OR the unstoppable flag is set, CC is skipped.
        // Bit layout matches CCImmunityConfig.Mask_* (Slow=0, Stun=1, Freeze=2, Knockback=3,
        // Polymorph=4, Stagger=5). Default 0 = no CC immunity, fully backward compatible.
        public int[] EnemyCCImmuneMask = new int[MAX_ENTITIES];
        // EnemySlowFactor: speed multiplier (0.5 = 50% speed), 0 = no slow
        public float[] EnemySlowFactor = new float[MAX_ENTITIES];
        // EnemyTerrainMoveSpeedMult: terrain-based speed multiplier (1.0 = normal, 0.5 = mud slow)
        public float[] EnemyTerrainMoveSpeedMult = new float[MAX_ENTITIES];
        // EnemyFrostZoneSlowMultiplier: per-enemy slow multiplier from overlapping FrostZone towers
        // (1.0 = no frost zone touching this enemy, 0.5 = 50% move speed). Set each frame by
        // FrostZoneSystem as the MIN over all active frost towers whose radius covers the enemy.
        // Default 1.0 = neutral (no slow); falls back to 1.0 automatically on enemy death.
        public float[] EnemyFrostZoneSlowMultiplier = new float[MAX_ENTITIES];
        // ── Path Tile Cost (Round 89) — per-enemy precomputed terrain mults ──
        // EnemyPathTerrainSpeedMult: derived at the start of each EnemyMovementSystem.Update
        // from PathNodeTerrain[EnemyPathNodeIndex[enemyId]]. 1.0 = no speed change. <1.0 = slow
        // (Slow tiles), >1.0 = boost (Boost tiles). Default 1.0f (no path or neutral node).
        // Read by EnemyMovementSystem AFTER the existing EnemyTerrainMoveSpeedMult factor so
        // path terrain stacks multiplicatively with world-position terrain (Mud/Ice/Lava).
        public float[] EnemyPathTerrainSpeedMult = new float[MAX_ENTITIES];
        // EnemyPathTerrainDmgMult: derived from PathNodeTerrain[EnemyPathNodeIndex[enemyId]].
        // 1.0 = no change to damage taken. >1.0 = take more damage (Snow tiles). Default 1.0f.
        // Applied at ApplyEnemyDamage() entry so all shield/health damage routes respect it.
        public float[] EnemyPathTerrainDmgMult = new float[MAX_ENTITIES];
        // EnemyMoveSpeedBase: stores original speed for slow recovery
        public float[] EnemyMoveSpeedBase = new float[MAX_ENTITIES];
        // EnemySlowDurationLeft: tower-slow duration in turns. Separate from EnemyBuffDurationLeft
        public float[] EnemySlowDurationLeft = new float[MAX_ENTITIES];
        // ==================== Polymorph CC (变羊/变小鸡 — 强制转阵营 + 失去攻击) ====================
        // EnemyIsPolymorphed: per-enemy bool flag. When true, enemy cannot attack or move
        // and is treated as a harmless target. Decays when EnemyPolymorphDurationLeft hits 0.
        // Default false — fully backward compatible (no enemy polymorphed by default).
        public bool[] EnemyIsPolymorphed = new bool[MAX_ENTITIES];
        // EnemyPolymorphDurationLeft: remaining polymorph duration in turns. Decremented each
        // frame by EnemyAISystem. When <= 0 and IsPolymorphed, flag is cleared.
        public float[] EnemyPolymorphDurationLeft = new float[MAX_ENTITIES];
        // EnemyPolymorphDamageTakenMultiplier: damage multiplier while polymorphed.
        // e.g. 1.5 = polymorphed enemies take 50% more damage (1.0 = no change).
        // Default 1.0f — no damage modifier unless explicitly set.
        public float[] EnemyPolymorphDamageTakenMultiplier = new float[MAX_ENTITIES];
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
        // ==================== Deployable Trap Cooldown (per-enemy, per-trap) ====================
        // Tracks how many frames are left before a given trap tower can trigger on this enemy again.
        // Outer key = enemyId, inner array indexed by trap towerId (sparse — only enemies
        // that have actually stepped on a trap carry an entry). Decremented each frame.
        // Cooldown prevents a single trap from re-triggering on the same enemy every turn
        // (e.g. a stun-trap applied every frame would be unfair). 0 = ready to trigger.
        // Default null = no entries (no cooldowns active). Allocated on first trigger.
        public System.Collections.Generic.Dictionary<int, int[]> EnemyTrapCooldownTick = new System.Collections.Generic.Dictionary<int, int[]>(64);
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

        // ==================== 跳斩/冲锋敌人组件 (Leap / Jump Attack, SOA) ====================
        // EnemyLeaperArchetype: 0 = no leap ability. >0 = leaper variant (1=Leaping Spider short,
        //   2=Mountain Troll long). Determines default leap distance/damage/radius when 0.
        //   Decoupled from MonsterConfig class so a single bool/int flag can be set from any
        //   system (WaveSpawningSystem reads monsterConfig.Type and writes this int).
        public int[] EnemyLeaperArchetype = new int[MAX_ENTITIES];
        // EnemyLeapDistance: max world-unit distance the leaper will travel in one leap.
        //   0 = not a leaper (zero-overhead default).
        public float[] EnemyLeapDistance = new float[MAX_ENTITIES];
        // EnemyLeapCooldown: turns until next leap is available (0 = ready, >0 = cooling down).
        //   -1 = no leap ability. Decremented each frame in EnemyMovementSystem; reset to ref after leap.
        public float[] EnemyLeapCooldown = new float[MAX_ENTITIES];
        // EnemyLeapCooldownRef: reference cooldown value (used to reset cooldown after a leap completes)
        public float[] EnemyLeapCooldownRef = new float[MAX_ENTITIES];
        // EnemyLeapDuration: total frames the leap animation takes (parabolic interpolation window)
        public float[] EnemyLeapDuration = new float[MAX_ENTITIES];
        // EnemyLeapStartX/Y: position captured at leap trigger (lerp from)
        public float[] EnemyLeapStartX = new float[MAX_ENTITIES];
        public float[] EnemyLeapStartY = new float[MAX_ENTITIES];
        // EnemyLeapTargetX/Y: landing position (lerp to)
        public float[] EnemyLeapTargetX = new float[MAX_ENTITIES];
        public float[] EnemyLeapTargetY = new float[MAX_ENTITIES];
        // EnemyLeapElapsed: frames since leap started (0..EnemyLeapDuration)
        public float[] EnemyLeapElapsed = new float[MAX_ENTITIES];
        // EnemyLeapDamage: AoE damage dealt on landing
        public float[] EnemyLeapDamage = new float[MAX_ENTITIES];
        // EnemyLeapRadius: AoE radius for landing damage
        public float[] EnemyLeapRadius = new float[MAX_ENTITIES];
        // EnemyLeapStunDuration: turns of stun applied to targets hit by landing AoE (0 = no stun)
        public float[] EnemyLeapStunDuration = new float[MAX_ENTITIES];

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

        // ==================== 召唤阵组件 (Summon Circle — Anti-Summon Tower Support) ====================
        // EnemyInSummonCircleX / EnemyInSummonCircleY: world position of the summon circle this
        // enemy currently belongs to (set at spawn by NecromancerSystem when reanimating a corpse).
        // 0/0 (or radius 0) means the enemy is NOT inside any summon circle → anti-summon towers
        // skip the bonus path entirely. We use a separate (X,Y,Radius) tuple rather than reading
        // the necromancer's position at attack time, because:
        //   (a) the necromancer may have moved (or died) by the time the minion is attacked,
        //   (b) the summon circle is conceptually a per-summon spatial constraint, not a global
        //       property of the caster, and
        //   (c) it keeps the attack path O(1) — one read per field, zero branching until bonus.
        public float[] EnemyInSummonCircleX = new float[MAX_ENTITIES];
        public float[] EnemyInSummonCircleY = new float[MAX_ENTITIES];
        // EnemyInSummonCircleRadius: radius of the summon circle (0 = no circle, fast path).
        public float[] EnemyInSummonCircleRadius = new float[MAX_ENTITIES];

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

        // ==================== LastStand / DeathRattle (HP-Threshold Trigger) ====================
        // EnemyLastStandHpFraction: HP fraction (0-1) at which the enemy enters LastStand mode.
        // Default 0 = disabled (no LastStand trigger for this enemy).
        // Example: 0.1 = activate LastStand when HP drops below 10% of max.
        public float[] EnemyLastStandHpFraction = new float[MAX_ENTITIES];
        // EnemyLastStandActive: true once LastStand has been triggered (permanent flag, like Enrage).
        // When true, the enemy's speed is boosted by EnemyLastStandSpeedMult and damage by EnemyLastStandDamageMult.
        public bool[] EnemyLastStandActive = new bool[MAX_ENTITIES];
        // EnemyLastStandSpeedMult: speed multiplier applied when LastStand activates.
        // Example: 1.5 = +50% move speed during LastStand.
        public float[] EnemyLastStandSpeedMult = new float[MAX_ENTITIES];
        // EnemyLastStandDamageMult: damage multiplier applied when LastStand activates.
        // Example: 2.0 = double damage during LastStand.
        public float[] EnemyLastStandDamageMult = new float[MAX_ENTITIES];

        // ==================== Boss Phase Skill / Speed / Damage (Round 111 Direction 1) ====================
        // Per-enemy per-phase structured config. The pre-Round-111 implementation stored only a
        // CSV string of thresholds in EnemyPhaseThresholds — the ability/speed/damage info from
        // monsterConfig.Phases was silently dropped at spawn. Round 111 makes those fields
        // queryable so EnemyAISystem can (a) trigger the phase's AbilityId via EnemyAbilitySystem,
        // (b) apply SpeedMult / DamageMult one-shot, (c) remember which phases have already
        // fired via a 4-bit fired mask. Hard-cap is 4 phases per boss (matches the JSON loader
        // "up to 4 phases" assumption) — bosses with more phases are silently truncated.
        public const int BOSS_PHASE_MAX = 4;
        // EnemyPhaseCount: number of phases configured for this enemy (0 = no phases).
        public int[] EnemyPhaseCount = new int[MAX_ENTITIES];
        // EnemyPhaseThresholdsFlat[phase, enemyId]: HP fraction (0-1) at which this phase activates.
        // Indexed as EnemyPhaseThresholdsFlat[phase * MAX_ENTITIES + enemyId] for cache locality.
        // Note: named "Flat" to avoid clashing with the pre-existing EnemyPhaseThresholds string[]
        // (kept for backwards-compat with the CSV parser used by legacy monster configs).
        public float[] EnemyPhaseThresholdsFlat = new float[BOSS_PHASE_MAX * MAX_ENTITIES];
        // EnemyPhaseSpeedMults[phase, enemyId]: speed multiplier applied on phase entry (1.0 = no change).
        public float[] EnemyPhaseSpeedMults = new float[BOSS_PHASE_MAX * MAX_ENTITIES];
        // EnemyPhaseDamageMults[phase, enemyId]: damage multiplier applied on phase entry (1.0 = no change).
        public float[] EnemyPhaseDamageMults = new float[BOSS_PHASE_MAX * MAX_ENTITIES];
        // EnemyPhaseAbilityIdsFlat[phase, enemyId]: per-(phase,enemy) abilityId to trigger
        // on phase entry (e.g. "boss_summon", "boss_enrage", null, "boss_explode"). Null is no-op.
        // Pre-split at spawn time from the original CSV to avoid per-frame string.Split allocations
        // (Round 111 Direction 1 perf fix — Split was the prime cause of the 26% bench regression).
        public string[,] EnemyPhaseAbilityIdsFlat = new string[BOSS_PHASE_MAX, MAX_ENTITIES];
        // EnemyPhaseFiredMask: 4-bit bitmask. Bit (1 << phase) is set when the phase has fired
        // its one-shot ability + multipliers. Prevents re-firing on subsequent HP recovery.
        public int[] EnemyPhaseFiredMask = new int[MAX_ENTITIES];

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
        // ==================== 元素抗性 (Elemental Resistance — fractional reduction for Fire/Ice/Lightning) ====================
        // EnemyFireResist: 0-1, fraction of Fire damage reduced (0 = take full, 0.4 = take 60%, 1.0 = immune).
        // EnemyIceResist: 0-1, fraction of Ice damage reduced.
        // EnemyLightningResist: 0-1, fraction of Lightning damage reduced.
        // Distinct from EnemyDamageImmunityMask (binary 0% or 100%) and EnemyMagicResist (Magic only).
        // Applied in TowerAttackSystem damage application chain in the same branch order:
        // True → Fire → Ice → Lightning → Magic → Physical (default).
        public float[] EnemyFireResist = new float[MAX_ENTITIES];
        public float[] EnemyIceResist = new float[MAX_ENTITIES];
        public float[] EnemyLightningResist = new float[MAX_ENTITIES];
        // EnemyIsUnstoppable: total CC immunity flag. When true, enemy ignores ALL crowd control
        // (stun, freeze, slow, fear, knockback, pull, charm, taunt). Boss-level CC immunity.
        public bool[] EnemyIsUnstoppable = new bool[MAX_ENTITIES];
        // EnemyFearResistance: 0-1, reduces fear duration fraction (1 = complete fear immunity)
        public float[] EnemyFearResistance = new float[MAX_ENTITIES];

        // ==================== 穿透抗性 (Pierce Resistance — anti-pierce-tower) ====================
        // EnemyPierceResist: 0-1, fraction of piercing damage that is ignored (0 = no resist, 0.75 = 75% pierce damage blocked).
        // Applied in ProjectileSystem.ResolveHit() to projectiles with pierceRemaining > 0.
        public float[] EnemyPierceResist = new float[MAX_ENTITIES];
        // EnemyIsPierceImmune: binary pierce immunity flag. When true, piercing projectiles deal 0 damage to this enemy.
        // Used for boss-type "armored core" enemies that completely shut down pierce tower strategies.
        public bool[] EnemyIsPierceImmune = new bool[MAX_ENTITIES];
        // EnemyCritResistance: 0-1, fraction of incoming crit chance that is suppressed.
        // Effective crit chance = towerCritChance * (1 - EnemyCritResistance). 0 = full crit chance, 0.5 = crit halved, 1.0 = cannot crit.
        // Applied in TowerAttackSystem and PlayerTowerAttackSystem at the crit roll point.
        // Default 0 for normal enemies; Boss/Elite monsters typically 0.5 to balance crit-sniper builds.
        public float[] EnemyCritResistance = new float[MAX_ENTITIES];
        // EnemyDeflectChance: 0-1, probability that the enemy deflects an incoming projectile each hit.
        // Applied in ProjectileSystem.ResolveHit at the very top — on a successful deflect roll, the projectile
        // deals 0 damage and exits early (no pierce immunity / thorns / fragment side-effects are triggered).
        // Default 0 for normal enemies; Boss-tier / fast elites typically 0.15-0.30 to add visual punch
        // and force players to combine high-damage hits (sniper / mortar) with reliable follow-up towers.
        public float[] EnemyDeflectChance = new float[MAX_ENTITIES];
        // EnemyRecentDamageSum: rolling sum of damage taken within the saturation window. Reset to 0 when
        // (currentFrame - EnemyRecentDamageFrame) > windowFrames. Read+updated on every damage event
        // (TowerAttackSystem hot path + PlayerTowerAttackSystem hot path). Default 0 for all entities
        // (lazy use — most enemies will never exceed the threshold; Boss/Elite benefit most from saturation).
        public float[] EnemyRecentDamageSum = new float[MAX_ENTITIES];
        // EnemyRecentDamageFrame: last frame at which EnemyRecentDamageSum was touched (set/added). Combined
        // with CurrentFrame, used to lazily expire the rolling window. 0 = uninitialized (no recent damage).
        public int[] EnemyRecentDamageFrame = new int[MAX_ENTITIES];

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

        // ==================== 敌人法力池（SOA，Round 101 方向 10）====================
        // EnemyMaxMana: maximum mana pool for this enemy (0 = no mana, drain is a no-op).
        // Default 0 = most enemies have no mana; only Mana-Wielder monsters populate this.
        // Towers with ManaDrainPct > 0 drain from this pool on attack hits.
        public float[] EnemyMaxMana = new float[MAX_ENTITIES];
        // EnemyCurrentMana: current mana of this enemy. Decremented by tower drain;
        // can be refilled by enemy self-casts / external sources (future extension).
        public float[] EnemyCurrentMana = new float[MAX_ENTITIES];

        // ==================== 敌人吸血组件（SOA）====================
        // EnemyLifestealRatio: fraction of damage dealt that is healed back (0.3 = 30% lifesteal)
        public float[] EnemyLifestealRatio = new float[MAX_ENTITIES];
        // EnemyLifestealCap: maximum heal per attack event (prevents burst healing)
        public float[] EnemyLifestealCap = new float[MAX_ENTITIES];
        // EnemyLifestealActive: whether lifesteal is currently active (enemies can toggle it)
        public bool[] EnemyLifestealActive = new bool[MAX_ENTITIES];

        // ==================== 敌人属性吸取组件（SOA）====================
        // EnemyDrainRatio: max fraction of tower damage that can be drained (0-1). 0 = no drain.
        // Example: 0.5 = tower damage can be reduced by up to 50% (cap).
        public float[] EnemyDrainRatio = new float[MAX_ENTITIES];
        // EnemyDrainRadius: world-unit radius within which the enemy can drain a nearby tower.
        public float[] EnemyDrainRadius = new float[MAX_ENTITIES];
        // EnemyDrainRate: fraction of base tower damage drained per second (0-1).
        // Example: 0.1 = 10% of base damage stolen per second until cap is reached.
        public float[] EnemyDrainRate = new float[MAX_ENTITIES];
        // EnemyDrainClaimedTower: tower id currently being drained by this enemy, or -1 if idle.
        // Per-enemy slot so the enemy can track "I am draining tower X" independently of the
        // tower-side TowerDrainedByEnemy[]. Initialize in AddEnemy to -1.
        public int[] EnemyDrainClaimedTower = new int[MAX_ENTITIES];

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
        // ── Round 83: Elemental Exposure (Direction 5) ──
        // EnemyExposureMask: bit-mask of elements that "tagged" the enemy most recently.
        // When an enemy is hit by an element of a DIFFERENT bit (or by a non-element attack)
        // while this mask is set and EnemyExposureTimer > 0, the incoming damage is multiplied
        // by (1 + ExposureBonusPct). Default ElementType.None = no exposure active.
        public ElementType[] EnemyExposureMask = new ElementType[MAX_ENTITIES];
        // EnemyExposureTimer: remaining seconds of the exposure vulnerability window.
        // Default 0f = no window active. Refreshed to ExposureDuration when EnemyElementStatus
        // gains a new bit; ticked down each frame by ElementalReactionSystem.Update.
        public float[] EnemyExposureTimer = new float[MAX_ENTITIES];

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

        // ==================== 死亡标记 / 处决 (Death Mark / Execute Threshold) ====================
        // EnemyMarked: when true, the enemy is in "death mark" state — incoming damage gets
        // a bonus (EnemyMarkedDamageBonus) and the killing blow grants bonus gold.
        // EnemyMarkedThreshold: HP fraction (0-1) below which the enemy auto-marks.
        //   Default 0.15 = mark when HP < 15% of max (typical Diablo execute threshold).
        // EnemyMarkedDamageBonus: multiplier added to incoming damage when marked (e.g. 0.5 = +50%).
        //   Default 0.5 = balanced: punishing but not deleting bosses in one hit.
        public bool[] EnemyMarked = new bool[MAX_ENTITIES];
        public float[] EnemyMarkedThreshold = new float[MAX_ENTITIES];
        public float[] EnemyMarkedDamageBonus = new float[MAX_ENTITIES];

        // ==================== 目标处决 (Execute Threshold) ====================
        // EnemyExecuteThreshold: HP fraction (0-1) below which the enemy becomes "executable".
        //   0 = disabled (default; no execute effect). Set to e.g. 0.20f to designate an enemy
        //   as vulnerable to execute when HP drops under 20% of max.
        // EnemyExecuteBonusGold: flat gold awarded (added on top of normal kill reward) when this
        //   enemy is killed while executable. Set to 0 to opt out, e.g. 25f for a 25-gold bonus.
        // EnemyExecuteBonusMana: flat mana awarded to the killer on execute kill. Same semantics.
        // EnemyExecuted: one-shot guard — set to true the first time the execute bonus is paid
        //   so re-marks / re-checks don't double-pay. Reset in DestroyEntity.
        // Round 105 Direction 8: Execute bonus rewards high-damage "finisher" plays. Pairs with
        // the existing Death Mark system to give assassination-style towers a payoff window.
        public float[] EnemyExecuteThreshold = new float[MAX_ENTITIES];
        public float[] EnemyExecuteBonusGold = new float[MAX_ENTITIES];
        public float[] EnemyExecuteBonusMana = new float[MAX_ENTITIES];
        public bool[] EnemyExecuted = new bool[MAX_ENTITIES];

        // ==================== Target Mark (目标标记叠加/衰减, Round 107 Direction 6) ====================
        // EnemyMarkStacks: current stack count of the mark debuff on this enemy.
        // Each tower hit that opts into the mark subsystem adds +1 (or +stacks-per-hit).
        // 0 = no mark active. Default 0 (no opt-in).
        public int[] EnemyMarkStacks = new int[MAX_ENTITIES];
        // EnemyMarkDecayTimer: seconds remaining before the stack count decays by 1.
        // Resets to EnemyMarkDecayInterval each time AddMark() is called. When it reaches 0,
        // decrement EnemyMarkStacks by 1 and reset timer. 0 = no decay timer (only refreshed by hits).
        public float[] EnemyMarkDecayTimer = new float[MAX_ENTITIES];
        // EnemyMarkMaxThreshold: stack count at which the mark "triggers" its payoff effect
        // (e.g., +50% damage taken, vulnerability to execute). 0 = no payoff (mark is pure visual).
        // Towers/enemies opt in by setting this > 0; default 0 keeps all enemies backward-compatible.
        public int[] EnemyMarkMaxThreshold = new int[MAX_ENTITIES];

        // ==================== 诱饵 (Decoy) ====================
        // EnemyIsDecoy: when true, this enemy is a non-aggressive target dummy spawned by the player
        //   (e.g. a Hologram Decoy tower). Decoys do not move, do not attack, and cannot use abilities.
        //   They exist solely to draw enemy aggro and absorb damage. Lifetime is finite
        //   (EnemyDecoyLifetime) so the field doesn't stay forever.
        // EnemyDecoyLifetime: configured maximum lifetime in seconds when this enemy is a decoy.
        //   0 = no decoy (default; normal enemy). WaveSpawningSystem / Hologram-tower spawn sets this.
        // EnemyDecoyLifetimeLeft: remaining lifetime in seconds. Decremented each frame in
        //   EnemyAISystem; when <= 0 the enemy is auto-queued for death (no gold reward).
        public bool[] EnemyIsDecoy = new bool[MAX_ENTITIES];
        public float[] EnemyDecoyLifetime = new float[MAX_ENTITIES];
        public float[] EnemyDecoyLifetimeLeft = new float[MAX_ENTITIES];

        // ==================== 仇恨脱战范围 (Aggro Leash / Disengage Range) ====================
        // EnemyAggroRange: world-units radius around the player base within which this enemy
        //   will switch from path-following to "aggro chase" (leashed) state. 0 = disabled
        //   (default; only monsters with explicit aggro config opt in).
        // EnemyLeashRange: world-units distance from player base beyond which the enemy
        //   breaks aggro and returns to its captured return point. Typically > AggroRange.
        // EnemyIsLeashed: true while the enemy is in active aggro-chase state.
        // EnemyLeashReturnX/Y: world position captured at the moment aggro triggered — the
        //   enemy path-resumes toward this point once it leaves LeashRange.
        public float[] EnemyAggroRange = new float[MAX_ENTITIES];
        public float[] EnemyLeashRange = new float[MAX_ENTITIES];
        public bool[] EnemyIsLeashed = new bool[MAX_ENTITIES];
        public float[] EnemyLeashReturnX = new float[MAX_ENTITIES];
        public float[] EnemyLeashReturnY = new float[MAX_ENTITIES];

        // ==================== 塔嘲讽目标 (Taunt Target) ====================
        // EnemyTauntedByTowerId: tower ID this enemy is currently forced to attack (-1 = not
        //   taunted). Set per-frame by the TauntSystem for each TowerIsTaunt tower in range;
        //   enemies pick the *closest* taunting tower so multiple taunt towers resolve to a
        //   sensible single target. Cleared to -1 when the taunt tower is destroyed/sold/loses
        //   range. Default -1 (no taunt = zero overhead — Movement/AITargeting skip the field).
        public int[] EnemyTauntedByTowerId = new int[MAX_ENTITIES];

        // ==================== 自由游荡敌人 (Free-Roam Enemies, Round 84 Direction 6) ====================
        // EnemyIsFreeRoam: when true, the enemy is NOT path-bound. It wanders the map freely
        //   and attacks the nearest tower/player it can reach. Set by WaveSpawningSystem
        //   when monsterConfig.Type == "FreeRoam". Default false (path-bound = zero overhead,
        //   EnemyMovementSystem / EnemyAISystem check this field as an early-exit gate).
        // EnemyWanderTargetX/Y: current wander target cell (in map coordinates). Updated each
        //   frame by WanderRoamSystem; enemy walks toward this point until close, then picks
        //   a new random cell. (0,0) is the default (origin) — never used in practice because
        //   the first frame after spawn re-rolls the target via EnemyWanderRerollTimer.
        // EnemyWanderRerollTimer: counts down each frame; when <= 0, the wander target is
        //   re-rolled. 0 = expired (reroll this frame). Default 0f so the very first frame
        //   picks a fresh target. This bounds the per-frame wander logic to O(1) per free
        //   enemy (no random calls in the hot inner loop).
        public bool[] EnemyIsFreeRoam = new bool[MAX_ENTITIES];
        public float[] EnemyWanderTargetX = new float[MAX_ENTITIES];
        public float[] EnemyWanderTargetY = new float[MAX_ENTITIES];
        public float[] EnemyWanderRerollTimer = new float[MAX_ENTITIES];

        // ==================== 放逐 (Banish) ====================
        // EnemyIsBanished: when true, the enemy is removed from the active battlefield for
        //   `EnemyBanishDurationLeft` frames. During banish, the enemy cannot move, cannot
        //   act, and (by design) remains in place at its current position. When the timer
        //   expires, the enemy resumes its previous AI/movement.
        //   0 = disabled (default). Towers/skills that apply banish set this to true and
        //   the duration to N frames. MovementSystem decrements the timer and clears the flag.
        // EnemyBanishDurationLeft: remaining banish frames. Default 0 = not banished.
        // EnemyBanishOriginalX/Y: the position captured at the moment banish was applied
        //   (frozen position; reserved for future "return-to-origin" semantics).
        public bool[] EnemyIsBanished = new bool[MAX_ENTITIES];
        public float[] EnemyBanishDurationLeft = new float[MAX_ENTITIES];
        public float[] EnemyBanishOriginalX = new float[MAX_ENTITIES];
        public float[] EnemyBanishOriginalY = new float[MAX_ENTITIES];

        // ==================== 失衡条 / 破防 (Stagger / Posture) ====================
        // EnemyStaggerMeter: 失衡值累加器。受到重击/暴击/特定伤害类型时按权重增加；
        //   达到 EnemyStaggerMax 时触发 N 帧硬直（EnemyIsStaggered = true）后清零。
        //   与 Stun 的区别：Stun 是概率性触发，Stagger 是确定性累计值。
        //   0 = 不受失衡影响（默认小怪）。Boss 等大型敌人按配置累加。
        // EnemyStaggerMax: 满失衡阈值。0 = 永不失衡（默认小怪）。Boss 典型 100。
        // EnemyStaggerDurationLeft: 硬直剩余帧数（>= 1 表示正在硬直）。
        // EnemyStaggerImmuneTimer: 硬直结束后免疫期剩余秒数（防无限失衡连击，默认 10 秒）。
        // EnemyIsStaggered: 失衡状态标志。为 true 时 AI/Movement 跳过敌人。
        public float[] EnemyStaggerMeter = new float[MAX_ENTITIES];
        public float[] EnemyStaggerMax = new float[MAX_ENTITIES];
        public float[] EnemyStaggerDurationLeft = new float[MAX_ENTITIES];
        public float[] EnemyStaggerImmuneTimer = new float[MAX_ENTITIES];
        public bool[] EnemyIsStaggered = new bool[MAX_ENTITIES];

        // ==================== 踩踏 / Boss 步伤 (Trample) ====================
        // EnemyTrampleRadius: 每帧移动后对周围该半径（世界单位）内的塔造成步伤。
        //   0 = 不踩踏（默认小怪）。大型 Boss 典型 2.5-4。
        // EnemyTrampleDamagePerStep: 每帧移动对范围内塔造成的伤害。
        //   0 = 不开 Trample。反相关：走得越慢踩得越重。
        // EnemyTrampleKnockback: 步伤是否附带击退。true = 目标塔被推 0.5 格（仅做位移标志，
        //   实际塔位置不变以便不让其下墙；表示为施加一个 -1HP "knockback event" 给塔）。
        //   简化：本轮只对塔造成伤害，knockback 留扩展位。
        public float[] EnemyTrampleRadius = new float[MAX_ENTITIES];
        public float[] EnemyTrampleDamagePerStep = new float[MAX_ENTITIES];
        public bool[] EnemyTrampleKnockback = new bool[MAX_ENTITIES];

        // ==================== 锁链 / 链接 (Tether — 敌人之间互相绑定) ====================
        // EnemyTetherPartnerId: 锁链另一端的敌人 id（0 = 无锁链，因为 default(int) = 0，
        //   且 ResolveTetherEnforcement 用 `partnerId <= enemyId` 早退去重）。
        //   双向存储：A.partner = B 且 B.partner = A。
        // EnemyTetherMaxLength: 锁链最大长度（世界单位）。两端距离超过此值时，
        //   (a) 两端 EnemyTetherSlowFactor 被设为 0.5（减速 50%），(b) 远端被朝近端拉回。
        //   0 = 关闭锁链（默认小怪）。Boss/特殊组合敌人典型 6-10。
        // EnemyTetherDamageSharePct: 锁链中任一端受到伤害时，将该比例的伤害传染给另一端。
        //   0 = 不传染（默认）。例如 0.25 = 25% 伤害分享。
        // EnemyTetherStunSharePct: 锁链中任一端被眩晕时，按此概率传染眩晕给另一端。
        //   0 = 不传染。0.5 = 50% 概率传染。
        // 设计原则：Tether 是被动的"绑定"关系（无 Tether 主动技能实体），
        //   通过配置开启即可生效。Trample/Banished/Staggered 自动跳过锁链互拉。
        public int[] EnemyTetherPartnerId = new int[MAX_ENTITIES];
        public float[] EnemyTetherMaxLength = new float[MAX_ENTITIES];
        public float[] EnemyTetherDamageSharePct = new float[MAX_ENTITIES];
        public float[] EnemyTetherStunSharePct = new float[MAX_ENTITIES];
        // EnemyTetherSlowFactor: 锁链减速乘数。0.5 = 50% 移速，1.0 = 无减速。
        //   每帧由 EnemyMovementSystem.ResolveTetherEnforcement() 写入，
        //   下一帧 movement 阶段被消费（乘到 moveSpeed 上）。
        //   ⚠️ 初始化为 1f（不是 CLR 默认 0f），否则会冻住所有未配置 tether 的敌人。
        public float[] EnemyTetherSlowFactor = Enumerable.Repeat(1f, MAX_ENTITIES).ToArray();

        // ==================== 敌人施法可打断 (Interruptible Channeling) ====================
        // EnemyIsChanneling: 敌人正在施法中（cast time > 0）。施法期间敌人无法移动/换技能。
        //   与 Stun/Banish 等价的行为：Movement 跳过，但占用"槽位"，可被外部打断。
        //   满 Stagger 时（Stagger 联动）自动打断 channel。
        // EnemyChannelTimer: 施法剩余帧数（>0 表示正在施法）。每帧 -1，到 0 时 ExecuteAbility 入队。
        // EnemyChannelAbilityId: 正在施法的 ability id（string）。当 channel 完成时通过 lookup
        //   还原 EnemyAbilityDef 并入队到 AbilityEvent。
        //   （注：另有一个独立的 channel 状态字段组；本字段是已开始 channel 的 ability id。）
        // EnemyChannelInterruptible: 该 cast 是否可被外部打断（true = 可被 silence/stun 打断）。
        //   false 表示 channel 必须完成（最终 BOSS 终极技能）。默认 true。
        public bool[] EnemyIsChanneling = new bool[MAX_ENTITIES];
        public float[] EnemyChannelTimer = new float[MAX_ENTITIES];
        public string[] EnemyChannelAbilityId = new string[MAX_ENTITIES];
        public bool[] EnemyChannelInterruptible = new bool[MAX_ENTITIES];

        // ==================== 敌怪阵营 / 内斗 (Faction / Infighting) ====================
        // EnemyFactionId: identifier of the enemy's faction. 0 = no faction (immune to infighting).
        //   Enemies sharing a non-zero FactionId AND in close proximity will damage each other
        //   ("挤死小怪" effect). WaveSpawningSystem opts in by calling SetFactionId(id, N).
        //   Per WaveSpawningSystem convention, FactionId=1 typically means "Swarm" archetype.
        // EnemyInfightCooldown: per-enemy cooldown in seconds. Decrements every frame by deltaTime.
        //   When > 0, the enemy cannot trigger or be triggered for new infight damage.
        //   Reset to InfightCooldownSec (default 0.5s) after each successful infight trigger.
        //   Default 0f = ready to fight on first eligible frame.
        public int[] EnemyFactionId = new int[MAX_ENTITIES];
        public float[] EnemyInfightCooldown = new float[MAX_ENTITIES];

        // FactionInfightEnabled: single int gate for the O(N) scan + O(N) cooldown-decrement
        // loop in EnemyAISystem.ResolveFactionInfighting. Default 0 = disabled (zero overhead).
        // WaveSpawningSystem flips to 1 the first time it spawns an enemy with FactionId > 0,
        // and never reverts. Using a single int (not bool[]) so the check is 1 cache-line read
        // rather than 100K, and there's no per-frame O(N) early-out scan needed.
        public int FactionInfightEnabled = 0;

        #endregion

        // ==================== 敌人组件访问 ====================

        // ── O(1) enemy affix flag helpers ─────────────────────────────────
        public bool HasAffix(int enemyId, BuffType affix)
        {
            if (!IsValidEntity(enemyId)) return false;
            return (EnemyAffixFlags[enemyId] & affix) != 0;
        }

        public int AddEnemy(float startX, float startY, float moveSpeed, float health, float maxHealth, float damage, int goldReward, int waveNumber, string fullName = null, float armor = 0f, float shield = 0f, float magicResist = 0f, float fireResist = 0f, float iceResist = 0f, float lightningResist = 0f)
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
            // Elemental resistance (Round 117): fractional reduction for Fire/Ice/Lightning damage.
            // Clamp to [0, 1] — values >1 would imply healing from elemental damage, which is out of scope.
            EnemyFireResist[entityId]      = fireResist      < 0f ? 0f : (fireResist      > 1f ? 1f : fireResist);
            EnemyIceResist[entityId]       = iceResist       < 0f ? 0f : (iceResist       > 1f ? 1f : iceResist);
            EnemyLightningResist[entityId] = lightningResist < 0f ? 0f : (lightningResist > 1f ? 1f : lightningResist);
            EnemyDamageImmunityMask[entityId] = 0;  // default: no damage immunities
            // Pierce Resistance: default 0 resist, false immune (no pierce mitigation)
            EnemyPierceResist[entityId] = 0f;
            EnemyIsPierceImmune[entityId] = false;
            // Crit Resistance: default 0 (full crit chance) — only Boss/Elite monsters get a non-zero value via SetCritResistance()
            EnemyCritResistance[entityId] = 0f;
            // Deflect Chance: default 0 (projectiles always hit) — only Boss/Elite monsters get a non-zero value via SetDeflectChance()
            EnemyDeflectChance[entityId] = 0f;
            // Damage Saturation (Round 92): default 0 rolling sum + 0 last-touched frame. Lazily used by
            // TowerAttackSystem and PlayerTowerAttackSystem — the (currentFrame - lastFrame) > window
            // check naturally expires the rolling window for any enemy that hasn't been hit recently.
            // Initialized explicitly so subsequent reads on freshly-spawned entities never see stale
            // data left over from prior spawns of the same slot (e.g. swap-and-pop reuse).
            EnemyRecentDamageSum[entityId] = 0f;
            EnemyRecentDamageFrame[entityId] = 0;
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
            // Frost Zone slow: default 1.0 (no frost zone touching this enemy) — set explicitly
            // because float[] default is 0f and 0f multiplied into moveSpeed would freeze the
            // enemy on spawn. FrostZoneSystem rewrites this every frame from a fresh 1.0.
            EnemyFrostZoneSlowMultiplier[entityId] = 1f;
            // Path Tile Cost (Round 89): default 1.0f (no path or neutral node = no effect).
            // Initialized explicitly because float[] default is 0f and 0f multiplied into
            // moveSpeed would freeze the enemy. Recomputed every frame in EnemyMovementSystem.
            EnemyPathTerrainSpeedMult[entityId] = 1f;
            EnemyPathTerrainDmgMult[entityId] = 1f;
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
            // Deployable trap cooldown: per-tower cooldown tracking which trap towers have
            // already triggered on this enemy. Prevents the same trap from triggering
            // multiple times on the same enemy in a single frame / N frames.
            // Format: Dictionary<enemyId, int[trapId]>. We use a flat int[] approach keyed
            // by trap tower id — bit index = trap entity id. 0 = no cooldown, >0 = frames
            // remaining before this trap can trigger again on this enemy.
            // Stored sparsely to keep memory bounded for 10K enemies × MAX_ENTITIES.
            // Dictionary<int, int[]> reused from existing pattern (e.g. EnemyIsBeingPulled).
            EnemyTrapCooldownTick = null; // lazily allocated by DeployableTrapSystem on first use
            // Stat drain: default 0/0/0 = no drain ability. WaveSpawningSystem overrides
            // per archetype if the monster config specifies drain ratio/radius/rate.
            // DrainRatio = max fraction of tower damage that can be drained (0-1, e.g. 0.5 = 50% cap).
            // DrainRadius = world-unit radius within which the enemy can drain a tower.
            // DrainRate = fraction of base tower damage drained per second (e.g. 0.1 = 10%/sec).
            EnemyDrainRatio[entityId] = 0f;
            EnemyDrainRadius[entityId] = 0f;
            EnemyDrainRate[entityId] = 0f;
            // Leap / Jump Attack: default 0/-1 = no leap ability. WaveSpawningSystem overrides
            // per archetype if the monster config Type indicates a leaper ("Leaper", "Troll").
            // All other leap fields default to 0f so the no-leaper hot path is branch-free.
            EnemyLeaperArchetype[entityId] = 0;
            EnemyLeapDistance[entityId] = 0f;
            EnemyLeapCooldown[entityId] = -1f;
            EnemyLeapCooldownRef[entityId] = -1f;
            EnemyLeapDuration[entityId] = 0f;
            EnemyLeapStartX[entityId] = 0f;
            EnemyLeapStartY[entityId] = 0f;
            EnemyLeapTargetX[entityId] = 0f;
            EnemyLeapTargetY[entityId] = 0f;
            EnemyLeapElapsed[entityId] = 0f;
            EnemyLeapDamage[entityId] = 0f;
            EnemyLeapRadius[entityId] = 0f;
            EnemyLeapStunDuration[entityId] = 0f;
            // EnemyDrainClaimedTower: -1 = no active drain claim. WaveSpawningSystem leaves
            // this at -1; the stat-drain system sets it to a tower id when the enemy acquires
            // a target and clears it when releasing (out of range, target destroyed, etc).
            EnemyDrainClaimedTower[entityId] = -1;

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
            // Mana Pool (Round 101 Direction 10): default 0 (no mana, drain no-ops)
            // Designers can populate via EnemyTypeEntry.MaxMana in the wave spawn config.
            EnemyMaxMana[entityId] = ManaDrainConfig.DefaultEnemyMaxMana;
            EnemyCurrentMana[entityId] = ManaDrainConfig.DefaultEnemyMaxMana;

            // Lifesteal: default 0 (no lifesteal)
            EnemyLifestealRatio[entityId] = 0f;
            EnemyLifestealCap[entityId] = 0f;
            EnemyLifestealActive[entityId] = false;

            // Protector: default false (no protector ability)
            EnemyIsProtector[entityId] = false;
            EnemyProtectRadius[entityId] = 0f;
            EnemyProtectDamageTransfer[entityId] = 0f;
            EnemyProtectMaxTargets[entityId] = 0;
            // Death Mark / Execute: default unmarked; threshold 15% HP, +50% dmg bonus when marked
            // Combat systems (PlayerTowerAttackSystem) auto-mark when HP < threshold per-frame.
            EnemyMarked[entityId] = false;
            EnemyMarkedThreshold[entityId] = 0.15f;
            EnemyMarkedDamageBonus[entityId] = 0.5f;
            // Execute bonus (Round 105 Direction 8): default 0 = opt-out. Set EnemyExecuteThreshold
            // to a positive HP fraction to designate an enemy as "executable" with the configured
            // gold/mana bonus paid out on kill. The one-shot EnemyExecuted flag prevents double-pay.
            EnemyExecuteThreshold[entityId] = 0f;
            EnemyExecuteBonusGold[entityId] = 0f;
            EnemyExecuteBonusMana[entityId] = 0f;
            EnemyExecuted[entityId] = false;
            // Round 107 Direction 6 — Target Mark: opt-in via EnemyMarkMaxThreshold > 0.
            // 0 = no mark subsystem participation. Reset on entity add to prevent ID-reuse leakage.
            EnemyMarkStacks[entityId] = 0;
            EnemyMarkDecayTimer[entityId] = 0f;
            EnemyMarkMaxThreshold[entityId] = 0;
            // Decoy: default not a decoy. WaveSpawningSystem / Hologram-tower spawn opts in by
            // setting EnemyIsDecoy = true and EnemyDecoyLifetime = N (seconds).
            EnemyIsDecoy[entityId] = false;
            EnemyDecoyLifetime[entityId] = 0f;
            EnemyDecoyLifetimeLeft[entityId] = 0f;

            // Aggro Leash: default 0 range = opt-out. Monster configs that want aggro behavior
            // set EnemyAggroRange (e.g. 4f) and EnemyLeashRange (e.g. 10f) via WaveSpawningSystem.
            EnemyAggroRange[entityId] = 0f;
            EnemyLeashRange[entityId] = 0f;
            EnemyIsLeashed[entityId] = false;
            EnemyLeashReturnX[entityId] = 0f;
            EnemyLeashReturnY[entityId] = 0f;
            // Taunt target: default -1 = not taunted (TauntSystem assigns a tower id when in range)
            EnemyTauntedByTowerId[entityId] = -1;
            // Free-Roam (Round 84): default NOT free-roam. WaveSpawningSystem opts in by
            // setting EnemyIsFreeRoam = true for monsterType "FreeRoam" archetypes.
            EnemyIsFreeRoam[entityId] = false;
            EnemyWanderTargetX[entityId] = 0f;
            EnemyWanderTargetY[entityId] = 0f;
            EnemyWanderRerollTimer[entityId] = 0f; // 0 = reroll on first frame after spawn
            // Banish fields (default: not banished)
            EnemyIsBanished[entityId] = false;
            EnemyBanishDurationLeft[entityId] = 0f;
            EnemyBanishOriginalX[entityId] = 0f;
            EnemyBanishOriginalY[entityId] = 0f;
            // Channeling fields (default: not channeling, 0 timer, no ability, interruptible=true)
            EnemyIsChanneling[entityId] = false;
            EnemyChannelTimer[entityId] = 0f;
            EnemyChannelAbilityId[entityId] = null;
            EnemyChannelInterruptible[entityId] = true;
            // Faction / Infighting (Round 90): default 0 = no faction (immune to infighting).
            // WaveSpawningSystem opts in by calling SetFactionId(id, N) where N > 0.
            // EnemyInfightCooldown: per-enemy cooldown timer (seconds) that prevents re-triggering
            // infight damage within InfightCooldownSec after the last trigger. Default 0f = ready.
            EnemyFactionId[entityId] = 0;
            EnemyInfightCooldown[entityId] = 0f;

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
        /// Sets the damage immunity bit mask for an enemy. Bits correspond to DamageType
        /// (Physical=1, Magic=2, Fire=4, Ice=8, Lightning=16). When set, the enemy takes
        /// 0 damage from that type. True damage (DamageType.True) bypasses this mask.
        /// Used by WaveSpawningSystem to apply monster JSON DamageImmunities config.
        /// </summary>
        public void SetDamageImmunityMask(int enemyId, int mask)
        {
            if (!IsValidEntity(enemyId)) return;
            EnemyDamageImmunityMask[enemyId] = mask;
        }

        /// <summary>
        /// Sets the fractional elemental resistances (Fire / Ice / Lightning) for an enemy.
        /// Each value is in [0, 1]: 0 = no resist (take full), 1 = full resist (effectively immune).
        /// Out-of-range inputs are clamped to [0, 1] — values >1 would imply healing from elemental
        /// damage, which is out of scope; values <0 are treated as 0. Parallel-safe (read-modify-write
        /// is single-threaded because callers only invoke this in serial spawn phase).
        /// Used by WaveSpawningSystem to apply monster JSON elemental resistance config.
        /// </summary>
        public void SetElementalResist(int enemyId, float fireResist, float iceResist, float lightningResist)
        {
            if (!IsValidEntity(enemyId)) return;
            EnemyFireResist[enemyId]      = fireResist      < 0f ? 0f : (fireResist      > 1f ? 1f : fireResist);
            EnemyIceResist[enemyId]       = iceResist       < 0f ? 0f : (iceResist       > 1f ? 1f : iceResist);
            EnemyLightningResist[enemyId] = lightningResist < 0f ? 0f : (lightningResist > 1f ? 1f : lightningResist);
        }

        /// <summary>
        /// Returns the elemental resistance (0-1) for the given damage type on the given enemy.
        /// Returns 0 for True damage (always bypasses resistance) and for any non-elemental
        /// type (Physical, Magic). Out-of-bounds enemyId returns 0.
        /// </summary>
        public float GetElementResist(int enemyId, DamageType type)
        {
            if (!IsValidEntity(enemyId)) return 0f;
            switch (type)
            {
                case DamageType.Fire:      return EnemyFireResist[enemyId];
                case DamageType.Ice:       return EnemyIceResist[enemyId];
                case DamageType.Lightning: return EnemyLightningResist[enemyId];
                default:                   return 0f;  // Physical/Magic/True all bypass elemental resist
            }
        }

        /// <summary>
        /// Sets the LastStand / DeathRattle configuration for an enemy. When HP drops below
        /// hpFraction * maxHP, the enemy enters LastStand mode (EnemyLastStandActive=true)
        /// and its speed/damage are boosted by speedMult and damageMult respectively.
        /// Pass hpFraction=0 to disable LastStand for this enemy.
        /// Used by WaveSpawningSystem to apply monster JSON LastStand config.
        /// </summary>
        public void SetLastStandConfig(int enemyId, float hpFraction, float speedMult, float damageMult)
        {
            if (!IsValidEntity(enemyId)) return;
            EnemyLastStandHpFraction[enemyId] = hpFraction;
            EnemyLastStandSpeedMult[enemyId] = speedMult;
            EnemyLastStandDamageMult[enemyId] = damageMult;
            // Active starts false; transitions to true on HP threshold crossing in EnemyAISystem
            EnemyLastStandActive[enemyId] = false;
        }

        /// <summary>
        /// Configures piercing-damage resistance for an enemy. Pierce-resist reduces (or nullifies) damage
        /// from projectiles that have pierceCount > 0 in ProjectileSystem.
        /// </summary>
        /// <param name="enemyId">Target enemy entity ID</param>
        /// <param name="pierceResist">0-1, fraction of piercing damage ignored (0 = full damage, 0.75 = 75% blocked)</param>
        /// <param name="pierceImmune">If true, piercing projectiles deal zero damage to this enemy</param>
        public void SetPierceResist(int enemyId, float pierceResist, bool pierceImmune)
        {
            if (!IsValidEntity(enemyId)) return;
            EnemyPierceResist[enemyId] = pierceResist;
            EnemyIsPierceImmune[enemyId] = pierceImmune;
        }

        /// <summary>
        /// Configures crit resistance for an enemy. Effective crit chance = towerCritChance * (1 - critResistance).
        /// Used by Boss/Elite monsters to dampen crit-sniper tower builds (default 0.5 → crit chance halved).
        /// </summary>
        /// <param name="enemyId">Target enemy entity ID</param>
        /// <param name="critResistance">0-1, fraction of incoming crit chance suppressed (0 = full crit, 1.0 = no crits ever)</param>
        public void SetCritResistance(int enemyId, float critResistance)
        {
            if (!IsValidEntity(enemyId)) return;
            // Clamp to [0,1] for safety; negative or >1 values would invert crit behavior unpredictably.
            EnemyCritResistance[enemyId] = System.Math.Clamp(critResistance, 0f, 1f);
        }

        /// <summary>
        /// Configures projectile deflection probability for an enemy. Each incoming projectile that
        /// hits the enemy rolls a uniform RNG; on success the projectile deals 0 damage and exits
        /// (no pierce / thorns / fragment side-effects are triggered).
        /// Used by fast / Boss-tier monsters to add visual punch and force reliable follow-up towers.
        /// </summary>
        /// <param name="enemyId">Target enemy entity ID</param>
        /// <param name="deflectChance">0-1, probability of deflecting an incoming projectile (0 = never deflect, 1.0 = always deflect)</param>
        public void SetDeflectChance(int enemyId, float deflectChance)
        {
            if (!IsValidEntity(enemyId)) return;
            // Clamp to [0,1] for safety; >1 would imply damage block probability > 1 (nonsense).
            EnemyDeflectChance[enemyId] = System.Math.Clamp(deflectChance, 0f, 1f);
        }

        /// <summary>
        /// Configures the enemy's faction identifier for infighting ("挤死小怪").
        /// Enemies sharing a non-zero FactionId will damage each other when in close proximity.
        /// 0 (default) means "no faction" — the enemy is immune to infighting.
        /// </summary>
        /// <param name="enemyId">Target enemy entity ID</param>
        /// <param name="factionId">0 = opt out, >0 = share this faction with allies</param>
        public void SetFactionId(int enemyId, int factionId)
        {
            if (!IsValidEntity(enemyId)) return;
            // Clamp negative to 0 (opt-out semantics)
            EnemyFactionId[enemyId] = System.Math.Max(0, factionId);
        }

        /// <summary>
        /// Sets the infight cooldown timer. 0 = ready; >0 = in cooldown (seconds remaining).
        /// Called by EnemyAISystem.InfightCheck() after triggering damage to both ends.
        /// </summary>
        public void SetInfightCooldown(int enemyId, float cooldown)
        {
            if (!IsValidEntity(enemyId)) return;
            // Clamp negative to 0
            EnemyInfightCooldown[enemyId] = System.Math.Max(0f, cooldown);
        }

        /// <summary>
        /// Tags an enemy as belonging to a summon circle at (x,y) with given radius. 0/0 or
        /// radius 0 means "no circle" (fast path). Called by NecromancerSystem when spawning a
        /// reanimated minion, so anti-summon towers can compute the bonus.
        /// </summary>
        public void SetSummonCircle(int enemyId, float x, float y, float radius)
        {
            if (!IsValidEntity(enemyId)) return;
            EnemyInSummonCircleX[enemyId] = x;
            EnemyInSummonCircleY[enemyId] = y;
            EnemyInSummonCircleRadius[enemyId] = System.Math.Max(0f, radius);
        }

        /// <summary>Clears the summon circle tag from an enemy (radius = 0 = fast path).</summary>
        public void ClearSummonCircle(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return;
            EnemyInSummonCircleX[enemyId] = 0f;
            EnemyInSummonCircleY[enemyId] = 0f;
            EnemyInSummonCircleRadius[enemyId] = 0f;
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

            // ── Path Tile Cost (Round 89) — apply waypoint terrain dmg-taken mult (Snow) ──
            // Default 1.0f (no effect). Only >1.0 (Snow) is expected; the call is cheap
            // (single array index + multiply) and runs on every damage event including
            // shield and DoT routes. Skips below 0.01f to avoid amplifying near-zero edge
            // cases (e.g. absorb ticks).
            float pathDmgMult = EnemyPathTerrainDmgMult[enemyId];
            if (pathDmgMult > 1.0001f)
            {
                damage *= pathDmgMult;
            }

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

        /// <summary>
        /// Polymorph CC: turns the enemy into a harmless form (变羊/变小鸡) for `duration` turns.
        /// Mirrors ApplySlow's safety: respects EnemyIsUnstoppable, applies damage-taken multiplier
        /// (default 1.0x → caller can pass 1.5 for 50% extra damage taken while polymorphed).
        /// While polymorphed, the enemy cannot attack (BT short-circuited in EnemyAISystem) and
        /// becomes a sitting duck — defensive, fully reversible, fully stack-friendly.
        /// </summary>
        public void ApplyPolymorph(int enemyId, int duration, float damageTakenMultiplier = 1f)
        {
            if (!IsValidEntity(enemyId)) return;
            if (duration <= 0) return;
            // Check total CC immunity (unstoppable enemies ignore polymorph too)
            if (EnemyIsUnstoppable[enemyId]) return;
            // Per-type CC immunity (Round 97): Polymorph bit blocks this CC type
            if (IsCCImmuneTo(enemyId, CCImmunityConfig.Mask_Polymorph)) return;
            // Clamp multiplier to a sane band (0.5x..3x) — protects against config typos
            if (damageTakenMultiplier < 0.5f) damageTakenMultiplier = 0.5f;
            if (damageTakenMultiplier > 3f) damageTakenMultiplier = 3f;

            // Refresh-or-set semantics: take the longer remaining duration (no double-dipping)
            if (duration > EnemyPolymorphDurationLeft[enemyId])
                EnemyPolymorphDurationLeft[enemyId] = duration;
            // Only overwrite the multiplier if the new one is stronger (stacking friendly)
            if (damageTakenMultiplier > EnemyPolymorphDamageTakenMultiplier[enemyId])
                EnemyPolymorphDamageTakenMultiplier[enemyId] = damageTakenMultiplier;
            // Flip the flag last (idempotent: setting true on already-true is a no-op semantically)
            EnemyIsPolymorphed[enemyId] = true;
        }

        /// <summary>
        /// Adds stagger (posture) damage to an enemy. When the meter reaches EnemyStaggerMax
        /// the enemy enters the Staggered state for `staggerDuration` frames (forced hard CC).
        /// After the stagger ends, EnemyStaggerImmuneTimer runs for `immuneSeconds` (default 10s)
        /// and prevents immediate re-stagger. Returns true if the meter just crossed the threshold
        /// and the enemy was knocked into Staggered this call.
        ///
        /// Enemies with EnemyStaggerMax <= 0 are immune to stagger (default small enemies).
        /// Enemies in Unstoppable state also ignore stagger.
        /// </summary>
        public bool AddStaggerDamage(int enemyId, float amount, int staggerDuration, float immuneSeconds = 10f)
        {
            if (!IsValidEntity(enemyId)) return false;
            if (amount <= 0f) return false;
            if (EnemyStaggerMax[enemyId] <= 0f) return false;     // 永不失衡（普通小怪）
            if (EnemyIsUnstoppable[enemyId]) return false;        // 霸体免疫
            // Per-type CC immunity (Round 97): Stagger bit blocks this CC type
            if (IsCCImmuneTo(enemyId, CCImmunityConfig.Mask_Stagger)) return false;
            if (EnemyIsStaggered[enemyId]) return false;          // 已在硬直中
            if (EnemyStaggerImmuneTimer[enemyId] > 0f) return false; // 刚硬直过，免疫期

            EnemyStaggerMeter[enemyId] += amount;
            if (EnemyStaggerMeter[enemyId] >= EnemyStaggerMax[enemyId])
            {
                // 触发硬直：清零 + 设置硬直状态 + 启动免疫期
                EnemyStaggerMeter[enemyId] = 0f;
                EnemyIsStaggered[enemyId] = true;
                EnemyStaggerDurationLeft[enemyId] = staggerDuration > 0 ? staggerDuration : 180;
                EnemyStaggerImmuneTimer[enemyId] = immuneSeconds;
                // Stagger联动：满失衡条强制打断当前 channel（无论 Interruptible 标志）。
                // The hard CC of stagger overrides Interruptible since stagger is a complete
                // state replacement, not a silence/stun. No cooldown refund here because the
                // 10s post-stagger immune period already prevents immediate re-cast.
                EnemyIsChanneling[enemyId] = false;
                EnemyChannelTimer[enemyId] = 0f;
                EnemyChannelAbilityId[enemyId] = null;
                EnemyChannelInterruptible[enemyId] = true;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Per-frame stagger tick called by EnemyMovementSystem: decrements the active stagger
        /// duration timer (clears EnemyIsStaggered when it hits 0) and the post-stagger
        /// immunity timer. The two timers are decoupled — the stagger ends first, then
        /// the immunity period runs.
        /// </summary>
        public void TickStagger(int enemyId, float deltaTime = 1f)
        {
            if (!IsValidEntity(enemyId)) return;
            if (EnemyIsStaggered[enemyId])
            {
                if (EnemyStaggerDurationLeft[enemyId] > 0f)
                {
                    EnemyStaggerDurationLeft[enemyId] -= 1f;
                    if (EnemyStaggerDurationLeft[enemyId] <= 0f)
                    {
                        EnemyStaggerDurationLeft[enemyId] = 0f;
                        EnemyIsStaggered[enemyId] = false; // 硬直结束
                    }
                }
                else
                {
                    // 防御：若 IsStaggered 为 true 但 duration 漏设为 0，立即清除
                    EnemyIsStaggered[enemyId] = false;
                }
                return; // 硬直期间不递减 immune
            }
            // 非硬直状态：递减免疫期
            if (EnemyStaggerImmuneTimer[enemyId] > 0f)
            {
                EnemyStaggerImmuneTimer[enemyId] -= deltaTime;
                if (EnemyStaggerImmuneTimer[enemyId] <= 0f)
                {
                    EnemyStaggerImmuneTimer[enemyId] = 0f;
                }
            }
        }

        /// <summary>Clears stagger and restores the enemy to normal state. Used on entity destroy / wave end.</summary>
        public void ClearStagger(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return;
            EnemyStaggerMeter[enemyId] = 0f;
            EnemyStaggerMax[enemyId] = 0f;
            EnemyStaggerDurationLeft[enemyId] = 0f;
            EnemyStaggerImmuneTimer[enemyId] = 0f;
            EnemyIsStaggered[enemyId] = false;
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
            // Per-type CC immunity (Round 97): bit set in EnemyCCImmuneMask blocks this CC type
            if (IsCCImmuneTo(enemyId, CCImmunityConfig.Mask_Stun)) return;
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
            // Per-type CC immunity (Round 97): Freeze bit blocks this CC type
            if (IsCCImmuneTo(enemyId, CCImmunityConfig.Mask_Freeze)) return;
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

        /// <summary>
        /// Returns true if the enemy is currently immune to a given CC type (Round 97 Direction 3).
        /// Checks both the per-type bit in <c>EnemyCCImmuneMask</c> and the global
        /// <c>EnemyIsUnstoppable</c> flag (full immunity). Pass CCImmunityConfig.Mask_* constants
        /// (Mask_Slow, Mask_Stun, etc.). Safe to call with any int — bit-AND tolerates unknown bits.
        /// </summary>
        public bool IsCCImmuneTo(int enemyId, int ccMask)
        {
            if (!IsValidEntity(enemyId)) return false;
            if (EnemyIsUnstoppable[enemyId]) return true;       // total CC immunity
            if ((EnemyCCImmuneMask[enemyId] & ccMask) != 0) return true; // per-type immunity
            return false;
        }

        /// <summary>Overwrite the per-enemy CC immunity bitmask (Round 97). OR-merge in bits via SetCCImmuneBit.</summary>
        public void SetCCImmuneMask(int enemyId, int mask)
        {
            if (!IsValidEntity(enemyId)) return;
            EnemyCCImmuneMask[enemyId] = mask;
        }

        /// <summary>OR a single CC immunity bit onto the existing mask (idempotent for already-set bits).</summary>
        public void SetCCImmuneBit(int enemyId, int ccMask)
        {
            if (!IsValidEntity(enemyId)) return;
            EnemyCCImmuneMask[enemyId] |= ccMask;
        }

        /// <summary>Clear a single CC immunity bit (or all bits if ccMask == 0).</summary>
        public void ClearCCImmuneBit(int enemyId, int ccMask)
        {
            if (!IsValidEntity(enemyId)) return;
            EnemyCCImmuneMask[enemyId] &= ~ccMask;
        }

        /// <summary>Applies slow to the enemy. factor is a speed multiplier (e.g. 0.5 = 50% speed). Duration in turns tracked by EnemySlowDurationLeft.</summary>
        public void ApplyEnemySlow(int enemyId, float factor, int duration)
        {
            if (!IsValidEntity(enemyId)) return;
            if (factor <= 0f || factor >= 1f) return;
            // Per-type CC immunity (Round 97): Slow bit blocks this CC type
            if (IsCCImmuneTo(enemyId, CCImmunityConfig.Mask_Slow)) return;
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
