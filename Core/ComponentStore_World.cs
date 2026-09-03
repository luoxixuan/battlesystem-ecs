using System;
using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;

namespace BattleSystemECS.Core
{
    public partial class ComponentStore
    {
        #region World / Environment Components
        // ── Path Tile Cost (Round 89) ──
        // PathNodeTerrain: per-waypoint terrain tag, indexed by the waypoint's position
        // within its path. 0=neutral (no effect). 1=Slow (-25% speed). 2=Boost (+25% speed).
        // 3=Snow (+15% damage taken). 4=Heal (+2% maxHp/s). 5=Wall (enemies block here).
        // One global array — all 4 players share the same default path tables, so the
        // per-path terrain config applies identically to every player. Set once at
        // game start (or per-level) and read every frame by EnemyMovementSystem.
        // All values default to 0 (neutral) → zero behavioral change unless the config
        // loader populates non-zero tags.
        public int[] PathNodeTerrain = new int[MAX_PATH_NODES];
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
        // ── DoomClock objective (Round 110 Direction 10) ─────────────────────────
        // DoomClockTimer: remaining countdown seconds. Decrements during WavePhase.
        public float[] DoomClockTimer = new float[MAX_PLAYERS];
        // DoomClockDuration: total duration in seconds (snapshot from LevelConfig).
        public float[] DoomClockDuration = new float[MAX_PLAYERS];
        // DoomClockWavesCleared: count of waves fully cleared since start of run.
        public int[] DoomClockWavesCleared = new int[MAX_PLAYERS];
        // DoomClockCycleCount: how many times the wave pool has been cycled (0-based).
        // Cycle 0 = first pass through DoomClockInitialWaves. Cycle 1 = second pass with scaling.
        public int[] DoomClockCycleCount = new int[MAX_PLAYERS];
        // DoomClockFinalScore: snapshot of computed final score at game end (win only).
        // 0 while run is ongoing. Set by DoomClockSystem.ComputeFinalScore() on win.
        public int[] DoomClockFinalScore = new int[MAX_PLAYERS];
        // DoomClockActive: true while the DoomClock run is in progress (between start and end).
        // Set true in ObjectiveSystem.InitializeFromLevel when ObjectiveType==DoomClock.
        // Set false in ObjectiveSystem.CheckObjective when the run ends (win or lose).
        public bool[] DoomClockActive = new bool[MAX_PLAYERS];

        // ==================== Adaptive Difficulty System 组件（SOA） ====================
        // EnemiesLeakedThisWave: track leaks during current wave (used to compute difficulty adjustment)
        public int[] EnemiesLeakedThisWave = new int[MAX_PLAYERS];
        // AdaptiveDifficultyLevel: difficulty multiplier applied to enemy health/damage/speed (1.0 = normal, >1 = harder)
        public float[] AdaptiveDifficultyLevel = new float[MAX_PLAYERS];
        // AdaptiveDifficultyScore: cumulative performance score (higher = better player performance)
        public float[] AdaptiveDifficultyScore = new float[MAX_PLAYERS];

        // ==================== Threat Score System 组件 (Round 99) ====================
        // PlayerRecentDPS: exponentially-decaying average of player damage output (per player)
        // Used by WaveSpawningSystem to scale enemy HP: high DPS → tougher enemies.
        // 衰减由 ThreatAggregate 帧节点在死亡后提交完成后逐帧执行，因此
        // spawn-time lookup is O(1) and hot-path cost is one float mul + max(0).
        public float[] PlayerRecentDPS = new float[MAX_PLAYERS];
        // PlayerDPSAccumulator: per-frame raw damage dealt (decayed into PlayerRecentDPS)
        // Reset to 0 every frame; pre-decay the running average then add this frame's damage.
        public float[] PlayerDPSAccumulator = new float[MAX_PLAYERS];

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
        // ResourceNodeDepleted: true if the node was destroyed and is waiting to respawn.
        // ResourceNodeRegenTimer: seconds remaining until the depleted node respawns at full HP.
        // Initialized in InitializeFromLevel when RegenDelay > 0; default 0/0 = no regen (legacy).
        public float[] ResourceNodeRegenTimer = new float[MAX_RESOURCE_NODES];
        public float[] ResourceNodeRegenDelay = new float[MAX_RESOURCE_NODES];
        public bool[] ResourceNodeDepleted = new bool[MAX_RESOURCE_NODES];
        // Count of live nodes (maintained by ResourceNodeSystem)
        public int ActiveResourceNodeCount = 0;
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

        // GlobalFogDensity: global fog density multiplier applied to all tower vision radii (1.0 = normal, <1.0 = reduced visibility)
        // WeatherSystem / DayNightSystem can modify this to simulate fog/night effects
        public float[] GlobalFogDensity = new float[MAX_PLAYERS];
        // RandomEventParam: event-specific parameter (e.g. gold amount for SupplyDrop, spawn count for Ambush)
        public float[] RandomEventParam = new float[MAX_PLAYERS];
        // RandomEventParam2: second event-specific parameter
        public float[] RandomEventParam2 = new float[MAX_PLAYERS];

        // ==================== Wind / Air Push System 组件（SOA）====================
        // Global wind: constant wind direction and strength affecting all enemies on the map.
        // WindDirection: angle in radians (0 = East, PI/2 = North, PI = West, 3PI/2 = South)
        public float[] GlobalWindDirection = new float[MAX_PLAYERS];
        // GlobalWindStrength: multiplier applied to enemy movement per frame (0.0-2.0, 1.0 = no wind)
        // Values < 1.0 slow enemies down, values > 1.0 speed them up.
        public float[] GlobalWindStrength = new float[MAX_PLAYERS];
        // GlobalWindActive: true when global wind is currently applied
        public bool[] GlobalWindActive = new bool[MAX_PLAYERS];
        // GlobalWindDuration: remaining seconds for time-limited wind (-1 = permanent)
        public float[] GlobalWindDuration = new float[MAX_PLAYERS];
        // GlobalWindGustTimer: countdown to next gust event (for gusty wind patterns)
        public float[] GlobalWindGustTimer = new float[MAX_PLAYERS];
        // GlobalWindGustStrength: bonus strength applied during a gust (0 = no gust)
        public float[] GlobalWindGustStrength = new float[MAX_PLAYERS];
        // GlobalWindGustInterval: seconds between gust events (for gusty pattern)
        public float[] GlobalWindGustInterval = new float[MAX_PLAYERS];

        // Local wind sources: tower-created wind zones with position and radius.
        // MAX_WIND_SOURCES: circular buffer size for tower wind effects.
        public const int MAX_WIND_SOURCES = 200;
        public bool[] WindSourceActive = new bool[MAX_WIND_SOURCES];
        public float[] WindSourceX = new float[MAX_WIND_SOURCES];
        public float[] WindSourceY = new float[MAX_WIND_SOURCES];
        public float[] WindSourceRadius = new float[MAX_WIND_SOURCES];       // influence radius
        public float[] WindSourceDirection = new float[MAX_WIND_SOURCES];   // angle in radians
        public float[] WindSourceStrength = new float[MAX_WIND_SOURCES];    // push force multiplier
        public float[] WindSourceDuration = new float[MAX_WIND_SOURCES];    // seconds remaining
        public int[] WindSourceOwnerPlayer = new int[MAX_WIND_SOURCES];      // player who owns this wind source
        public int[] WindSourceTowerId = new int[MAX_WIND_SOURCES];          // tower that created this wind (-1 = environmental)
        private int _nextWindSourceId = 0;
        private int _activeWindSourceCount = 0;
        // ==================== Pull / Vacuum / Gravity Well 组件（SOA）====================
        // Global pull: central gravity well that attracts all enemies toward a point.
        public float[] GlobalPullCenterX = new float[MAX_PLAYERS];
        public float[] GlobalPullCenterY = new float[MAX_PLAYERS];
        public float[] GlobalPullStrength = new float[MAX_PLAYERS];
        public float[] GlobalPullDuration = new float[MAX_PLAYERS];
        public bool[] GlobalPullActive = new bool[MAX_PLAYERS];
        // Local pull sources: tower-created vacuum effects with position and radius.
        // MAX_PULL_SOURCES: circular buffer size for tower pull effects.
        public const int MAX_PULL_SOURCES = 200;
        public bool[] PullSourceActive = new bool[MAX_PULL_SOURCES];
        public float[] PullSourceX = new float[MAX_PULL_SOURCES];
        public float[] PullSourceY = new float[MAX_PULL_SOURCES];
        public float[] PullSourceRadius = new float[MAX_PULL_SOURCES];
        public float[] PullSourceStrength = new float[MAX_PULL_SOURCES];
        public float[] PullSourceDuration = new float[MAX_PULL_SOURCES];
        public int[] PullSourceOwnerPlayer = new int[MAX_PULL_SOURCES];
        public int[] PullSourceTowerId = new int[MAX_PULL_SOURCES];
        private int _nextPullSourceId = 0;
        private int _activePullSourceCount = 0;
        // ==================== Ascension/Difficulty Modifier 组件 ====================
        // AscensionModifierStacks: tracks stack count for each ascension modifier (up to 64 unique modifiers)
        public int[] AscensionModifierStacks = new int[64];
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
        // PickupRarity: rarity tier (0=Common, 1=Uncommon, 2=Rare, 3=Epic, 4=Legendary). Default 0 = Common.
        // Used for visual filtering and future per-rarity bonus logic. Slot recycled → default 0 (Common).
        public byte[] PickupRarity = new byte[MAX_ENTITIES];
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
        // ==================== 路障/墙体组件（Obstacle）====================
        // 路障是可被敌人攻击的放置物（冰墙、地雷等）
        public const int MAX_OBSTACLES = 5000;
        public bool[] ObstacleActive = new bool[MAX_OBSTACLES];
        public float[] ObstacleHealth = new float[MAX_OBSTACLES];
        public float[] ObstacleMaxHealth = new float[MAX_OBSTACLES];
        public float[] ObstacleX = new float[MAX_OBSTACLES];
        public float[] ObstacleY = new float[MAX_OBSTACLES];
        public int[] ObstacleType = new int[MAX_OBSTACLES];  // index into ObstacleDefs[]
        // Destructible on-destroy hook (Round 95 Direction 5): 0=None, 1=Gold, 2=Explosion
        public int[] ObstacleOnDestroyEffect = new int[MAX_OBSTACLES];
        // Magnitude of the on-destroy effect: Gold=gold amount, Explosion=% maxHp as damage
        public float[] ObstacleOnDestroyValue = new float[MAX_OBSTACLES];

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
        // Round 171 Direction 4 — Blighted Ground debuff fields (per-zone, additive into
        // EnemyCurseArmorReduction / EnemyCurseSpeedReduction when enemy in range).
        public float[] CorpseEffectArmorReduction = new float[MAX_CORPSE_EFFECTS];
        public float[] CorpseEffectSpeedReduction = new float[MAX_CORPSE_EFFECTS];
        // Round 175 Direction 9 — Smokescreen miss chance (per-zone, applied to towers in
        // range via TowerSmokeMissChance[]; consumed by TowerAttackSystem).
        public float[] CorpseEffectMissChance = new float[MAX_CORPSE_EFFECTS];
        // Round 175 Direction 9 — Smokescreen enemy move-speed boost (per-zone, multiplicative
        // into EnemyTerrainMoveSpeedMult[]; 1.0 = no boost, 1.2 = +20% speed).
        public float[] CorpseEffectEnemySpeedBoost = new float[MAX_CORPSE_EFFECTS];
        // Round 183 Direction 8 — Scorched Earth (CorpseEffectType=10) DoT + tower vision
        // reduction. DamageType: 0=Physical, 1=Fire (default 0 keeps all other zones unchanged).
        public int[] CorpseEffectDamageType = new int[MAX_CORPSE_EFFECTS];
        // Vision reduction applied to towers inside the zone (0..1, e.g. 0.5 = -50% range).
        // Mirrored into TowerVisionReduction[] (tower-side per-frame cache, max-merge across
        // overlapping zones) and consumed by TowerAttackSystem. Default 0 = inert.
        public float[] CorpseEffectVisionReduction = new float[MAX_CORPSE_EFFECTS];
        private List<int> _activeCorpseEffectIds = new List<int>();
        private int _nextCorpseEffectId = 0;

        // ==================== Direction 2 — Elemental Terrain Zones (Round 200) ====================
        // Player-spawned elemental terrain (Frozen Lake / Burning Ground / Toxic Swamp / Holy Sanctum).
        // Distinct from HazardZone (tower-spawned single-effect DoT) and map-baked HotZone (placement
        // bonus). Each zone has element type, per-stack slow + DoT, stacks up on enemies that linger
        // inside, and expires after Lifetime. Default 0 in every field keeps the system inert when no
        // zone is active.
        public const int MAX_TERRAIN_ZONES = 200;
        public bool[] TerrainZoneActive = new bool[MAX_TERRAIN_ZONES];
        public float[] TerrainZoneX = new float[MAX_TERRAIN_ZONES];
        public float[] TerrainZoneY = new float[MAX_TERRAIN_ZONES];
        public float[] TerrainZoneRadius = new float[MAX_TERRAIN_ZONES];
        public float[] TerrainZoneMaxRadius = new float[MAX_TERRAIN_ZONES];
        public float[] TerrainZoneBaseRadius = new float[MAX_TERRAIN_ZONES];  // starting radius (used for expand calc)
        public float[] TerrainZoneLifetime = new float[MAX_TERRAIN_ZONES];  // seconds remaining
        public float[] TerrainZoneTotalLifetime = new float[MAX_TERRAIN_ZONES];  // initial lifetime (for end-of-life scaling)
        public float[] TerrainZoneTickInterval = new float[MAX_TERRAIN_ZONES];
        public float[] TerrainZoneTickTimer = new float[MAX_TERRAIN_ZONES];  // accumulator
        public int[] TerrainZoneElement = new int[MAX_TERRAIN_ZONES];  // 0=Fire, 1=Ice, 2=Toxic, 3=Holy
        public float[] TerrainZoneBaseDps = new float[MAX_TERRAIN_ZONES];
        public float[] TerrainZoneSlowPerStack = new float[MAX_TERRAIN_ZONES];
        public int[] TerrainZoneMaxStacks = new int[MAX_TERRAIN_ZONES];
        public int[] TerrainZoneOwnerPlayerId = new int[MAX_TERRAIN_ZONES];  // -1 = none
        public bool[] TerrainZoneExpandOverTime = new bool[MAX_TERRAIN_ZONES];
        public string[] TerrainZoneId = new string[MAX_TERRAIN_ZONES];  // for tooltips / logs
        private List<int> _activeTerrainZoneIds = new List<int>();
        private int _nextTerrainZoneId = 0;

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
        public int AbilityPoolRejections;
        public int EffectPoolRejections;
        public event Action<int, bool> OnGasPoolRejected;
        public int[] AbilityCount = new int[MAX_ENTITIES]; // how many abilities this entity has

        // Legacy same-frame projection. ActiveGameplayEffectStore owns all runtime state.
        public AppliedEffect[] ActiveEffects = new AppliedEffect[MAX_ENTITIES * MAX_ACTIVE_EFFECTS_PER_ENTITY];
        public int[] ActiveEffectCount = new int[MAX_ENTITIES];
        private EffectHandle[] _activeEffectHandles = new EffectHandle[MAX_ENTITIES * MAX_ACTIVE_EFFECTS_PER_ENTITY];
        public readonly ActiveGameplayEffectStore GameplayEffects = new ActiveGameplayEffectStore(MAX_ENTITIES * MAX_ACTIVE_EFFECTS_PER_ENTITY);
        public GAS.GameplayEffectRuntime GameplayEffectsRuntime { get; }
        public GAS.GameplayTriggerRuntime GameplayTriggersRuntime { get; }
        internal TagContributionState TagState { get; } = new TagContributionState();
        public EffectPool GameplayEffectPool => GameplayEffects.Handles;
        #endregion

        // ==================== 路障管理 ====================
        public void AddObstacle(int obstacleId, int typeId, float x, float y, float maxHealth, int onDestroyEffect = 0, float onDestroyValue = 0f)
        {
            if (obstacleId < 0 || obstacleId >= MAX_OBSTACLES) return;
            ObstacleActive[obstacleId] = true;
            ObstacleType[obstacleId] = typeId;
            ObstacleX[obstacleId] = x;
            ObstacleY[obstacleId] = y;
            ObstacleHealth[obstacleId] = maxHealth;
            ObstacleMaxHealth[obstacleId] = maxHealth;
            // Destructible on-destroy effect (Round 95 Direction 5): 0=None, 1=Gold, 2=Explosion
            ObstacleOnDestroyEffect[obstacleId] = onDestroyEffect;
            ObstacleOnDestroyValue[obstacleId] = onDestroyValue;
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
            ObstacleOnDestroyEffect[obstacleId] = 0;
            ObstacleOnDestroyValue[obstacleId] = 0f;
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

        // ==================== Direction 2 — Elemental Terrain Zone 管理 API ====================
        /// <summary>
        /// Add a player-spawned elemental terrain zone. Returns zone id, or -1 if no free slots.
        /// Distinct from AddHazardZone (tower-spawned single-effect) — this carries element,
        /// per-stack slow, stacks up on enemies, and uses tick interval.
        /// </summary>
        public int AddTerrainZone(float x, float y, float radius, int element,
                                   float baseDps, float slowPerStack, int maxStacks,
                                   float lifetime, float tickInterval, bool expandOverTime,
                                   int ownerPlayerId = -1, string id = "")
        {
            int zoneId = -1;
            lock (activeIdsLock)
            {
                for (int i = 0; i < MAX_TERRAIN_ZONES; i++)
                {
                    int candidateId = (_nextTerrainZoneId + i) % MAX_TERRAIN_ZONES;
                    if (!TerrainZoneActive[candidateId])
                    {
                        zoneId = candidateId;
                        _nextTerrainZoneId = (candidateId + 1) % MAX_TERRAIN_ZONES;
                        break;
                    }
                }
            }
            if (zoneId < 0) return -1;

            TerrainZoneActive[zoneId] = true;
            TerrainZoneX[zoneId] = x;
            TerrainZoneY[zoneId] = y;
            TerrainZoneRadius[zoneId] = radius;
            TerrainZoneMaxRadius[zoneId] = radius * 1.5f;  // hard cap (expand-over-time ceiling)
            TerrainZoneBaseRadius[zoneId] = radius;
            TerrainZoneLifetime[zoneId] = lifetime;
            TerrainZoneTotalLifetime[zoneId] = lifetime;
            TerrainZoneTickInterval[zoneId] = tickInterval;
            TerrainZoneTickTimer[zoneId] = 0f;
            TerrainZoneElement[zoneId] = element;
            TerrainZoneBaseDps[zoneId] = baseDps;
            TerrainZoneSlowPerStack[zoneId] = slowPerStack;
            TerrainZoneMaxStacks[zoneId] = maxStacks;
            TerrainZoneOwnerPlayerId[zoneId] = ownerPlayerId;
            TerrainZoneExpandOverTime[zoneId] = expandOverTime;
            TerrainZoneId[zoneId] = id;
            _activeTerrainZoneIds.Add(zoneId);
            return zoneId;
        }

        /// <summary>Remove a terrain zone by id. Resets all fields to defaults.</summary>
        public void RemoveTerrainZone(int zoneId)
        {
            if (zoneId < 0 || zoneId >= MAX_TERRAIN_ZONES) return;
            if (!TerrainZoneActive[zoneId]) return;
            TerrainZoneActive[zoneId] = false;
            TerrainZoneX[zoneId] = 0f;
            TerrainZoneY[zoneId] = 0f;
            TerrainZoneRadius[zoneId] = 0f;
            TerrainZoneMaxRadius[zoneId] = 0f;
            TerrainZoneBaseRadius[zoneId] = 0f;
            TerrainZoneLifetime[zoneId] = 0f;
            TerrainZoneTotalLifetime[zoneId] = 0f;
            TerrainZoneTickInterval[zoneId] = 0f;
            TerrainZoneTickTimer[zoneId] = 0f;
            TerrainZoneElement[zoneId] = 0;
            TerrainZoneBaseDps[zoneId] = 0f;
            TerrainZoneSlowPerStack[zoneId] = 0f;
            TerrainZoneMaxStacks[zoneId] = 0;
            TerrainZoneOwnerPlayerId[zoneId] = -1;
            TerrainZoneExpandOverTime[zoneId] = false;
            TerrainZoneId[zoneId] = null;
            _activeTerrainZoneIds.Remove(zoneId);
        }

        /// <summary>List of currently active terrain zone ids (snapshot list, do not mutate).</summary>
        public List<int> GetCachedActiveTerrainZoneIds()
        {
            return _activeTerrainZoneIds;
        }

        // ==================== 尸体残留效果（CorpseEffect）管理 API ====================

        /// <summary>
        /// Queue a corpse ground effect at a position when an enemy dies.
        /// Called from EnemyFissionSystem or ResolveEnemiesKilledThisFrame.
        /// Returns zone ID or -1 if no free slots.
        /// </summary>
        public int AddCorpseEffect(float x, float y, int effectType, float radius, float duration, float damagePerTick = 0f, float slowAmount = 1f, float tickInterval = 1f, float armorReduction = 0f, float speedReduction = 0f, float missChance = 0f, float enemySpeedBoost = 1f, int damageType = 0, float visionReduction = 0f)
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
            // Round 171 Direction 4 — Blighted Ground debuff fields
            CorpseEffectArmorReduction[zoneId] = armorReduction;
            CorpseEffectSpeedReduction[zoneId] = speedReduction;
            // Round 175 Direction 9 — Smokescreen fields (ignored for other effect types
            // because their MissChance/EnemySpeedBoost default to 0/1f respectively).
            CorpseEffectMissChance[zoneId] = missChance;
            CorpseEffectEnemySpeedBoost[zoneId] = enemySpeedBoost;
            // Round 183 Direction 8 — Scorched Earth (damageType 0=Physical / 1=Fire,
            // visionReduction 0..1, both default 0 so existing zone types are inert).
            CorpseEffectDamageType[zoneId] = damageType;
            CorpseEffectVisionReduction[zoneId] = visionReduction;
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
            // Round 171 Direction 4 — Blighted Ground debuff fields
            CorpseEffectArmorReduction[zoneId] = 0f;
            CorpseEffectSpeedReduction[zoneId] = 0f;
            // Round 175 Direction 9 — Smokescreen fields
            CorpseEffectMissChance[zoneId] = 0f;
            CorpseEffectEnemySpeedBoost[zoneId] = 1f;
            // Round 183 Direction 8 — Scorched Earth fields (default 0 / 0 = inert)
            CorpseEffectDamageType[zoneId] = 0;
            CorpseEffectVisionReduction[zoneId] = 0f;
            _activeCorpseEffectIds.Remove(zoneId);
        }

        /// <summary>Get list of active corpse effect IDs. O(n) over active zones, zero GC.</summary>
        public List<int> GetCachedActiveCorpseEffectIds()
        {
            return _activeCorpseEffectIds;
        }

        // ==================== 磁吸立场（MagnetizeZone）====================
        // 持续 N 秒的地面磁吸圈：圈内敌人每帧被朝中心点拉扯 pullStrength 单位
        // 与 HazardZone/CorpseEffect 不同：MagnetizeZone 不造成伤害，只做位移控制
        // MagnetizeType: 0=Pull(朝中心), 1=Repel(背离中心), 2=Pull+Deflect(拉 + 弹丸偏转)
        // 默认 0f/0/0f 不开启（与现有 trample/cleave 一致的"按需启用"约定）
        public const int MAX_MAGNETIZE_ZONES = 64;
        public bool[] MagnetizeZoneActive = new bool[MAX_MAGNETIZE_ZONES];
        public float[] MagnetizeZoneX = new float[MAX_MAGNETIZE_ZONES];
        public float[] MagnetizeZoneY = new float[MAX_MAGNETIZE_ZONES];
        public float[] MagnetizeZoneRadius = new float[MAX_MAGNETIZE_ZONES];
        public float[] MagnetizeZoneDuration = new float[MAX_MAGNETIZE_ZONES];
        public float[] MagnetizeZonePullStrength = new float[MAX_MAGNETIZE_ZONES];
        public int[] MagnetizeZoneType = new int[MAX_MAGNETIZE_ZONES];  // 0=Pull, 1=Repel, 2=Pull+Deflect
        private List<int> _activeMagnetizeZoneIds = new List<int>();
        private int _nextMagnetizeZoneId = 0;

        /// <summary>Add a magnetize zone at the given position. Returns zone ID, or -1 if pool full.</summary>
        public int AddMagnetizeZone(float x, float y, float radius, float duration, float pullStrength, int zoneType = 0)
        {
            int zoneId = -1;
            lock (activeIdsLock)
            {
                for (int i = 0; i < MAX_MAGNETIZE_ZONES; i++)
                {
                    int candidateId = (_nextMagnetizeZoneId + i) % MAX_MAGNETIZE_ZONES;
                    if (!MagnetizeZoneActive[candidateId])
                    {
                        zoneId = candidateId;
                        _nextMagnetizeZoneId = (candidateId + 1) % MAX_MAGNETIZE_ZONES;
                        break;
                    }
                }
            }
            if (zoneId < 0) return -1;

            MagnetizeZoneActive[zoneId] = true;
            MagnetizeZoneX[zoneId] = x;
            MagnetizeZoneY[zoneId] = y;
            MagnetizeZoneRadius[zoneId] = radius;
            MagnetizeZoneDuration[zoneId] = duration;
            MagnetizeZonePullStrength[zoneId] = pullStrength;
            MagnetizeZoneType[zoneId] = zoneType;
            _activeMagnetizeZoneIds.Add(zoneId);
            return zoneId;
        }

        /// <summary>Remove a magnetize zone by ID. Safe to call on inactive slots.</summary>
        public void RemoveMagnetizeZone(int zoneId)
        {
            if (zoneId < 0 || zoneId >= MAX_MAGNETIZE_ZONES) return;
            if (!MagnetizeZoneActive[zoneId]) return;
            MagnetizeZoneActive[zoneId] = false;
            MagnetizeZoneX[zoneId] = 0f;
            MagnetizeZoneY[zoneId] = 0f;
            MagnetizeZoneRadius[zoneId] = 0f;
            MagnetizeZoneDuration[zoneId] = 0f;
            MagnetizeZonePullStrength[zoneId] = 0f;
            MagnetizeZoneType[zoneId] = 0;
            _activeMagnetizeZoneIds.Remove(zoneId);
        }

        /// <summary>Get cached list of active magnetize zone IDs. O(1) — returns internal list reference.</summary>
        public List<int> GetCachedActiveMagnetizeZoneIds()
        {
            return _activeMagnetizeZoneIds;
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

        // ==================== 技能组件 SOA 访问方法 ====================

        public string GetSkillName(int playerId)
        {
            if (!IsValidPlayer(playerId)) return "";
            return SkillName[playerId];
        }

        public void SetSkillName(int playerId, string name)
        {
            if (!IsValidPlayer(playerId)) return;
            SkillName[playerId] = name;
        }

        public float GetSkillDamageMultiplier(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 1f;
            return SkillDamageMultiplier[playerId];
        }

        public void SetSkillDamageMultiplier(int playerId, float multiplier)
        {
            if (!IsValidPlayer(playerId)) return;
            SkillDamageMultiplier[playerId] = multiplier;
        }

        public int GetSkillAreaWidth(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 1;
            return SkillAreaWidth[playerId];
        }

        public void SetSkillAreaWidth(int playerId, int width)
        {
            if (!IsValidPlayer(playerId)) return;
            SkillAreaWidth[playerId] = width;
        }

        public int GetSkillAreaHeight(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 1;
            return SkillAreaHeight[playerId];
        }

        public void SetSkillAreaHeight(int playerId, int height)
        {
            if (!IsValidPlayer(playerId)) return;
            SkillAreaHeight[playerId] = height;
        }

        public int GetSkillAttackRange(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 1;
            return SkillAttackRange[playerId];
        }

        public void SetSkillAttackRange(int playerId, int range)
        {
            if (!IsValidPlayer(playerId)) return;
            SkillAttackRange[playerId] = range;
        }

        public float GetSkillCooldown(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 0f;
            return SkillCooldown[playerId];
        }

        public void SetSkillCooldown(int playerId, float cooldown)
        {
            if (!IsValidPlayer(playerId)) return;
            SkillCooldown[playerId] = cooldown;
        }

        public float GetSkillCurrentCooldown(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 0f;
            return SkillCurrentCooldown[playerId];
        }

        public void SetSkillCurrentCooldown(int playerId, float currentCooldown)
        {
            if (!IsValidPlayer(playerId)) return;
            SkillCurrentCooldown[playerId] = currentCooldown;
        }

        // ==================== GAS 组件访问方法 ====================

        public AbilityInstance GetAbility(int entityId, int slot) {
            if (!IsValidEntity(entityId)) return default;
            if (slot < 0 || slot >= MAX_ABILITIES_PER_ENTITY) return default;
            return AbilityInstances[entityId * MAX_ABILITIES_PER_ENTITY + slot];
        }

        public void SetAbility(int entityId, int slot, AbilityInstance inst) {
            if (!IsValidEntity(entityId)) return;
            if (slot < 0 || slot >= MAX_ABILITIES_PER_ENTITY) return;
            AbilityInstances[entityId * MAX_ABILITIES_PER_ENTITY + slot] = inst;
        }

        public void AddAbility(int entityId, GameplayAbilityDef def) { TryAddAbility(entityId, def); }
        public bool TryAddAbility(int entityId, GameplayAbilityDef def) {
            if (!IsValidEntity(entityId)) return false;
            int slot = AbilityCount[entityId];
            if (slot >= MAX_ABILITIES_PER_ENTITY) { AbilityPoolRejections++; OnGasPoolRejected?.Invoke(entityId, true); return false; }
            SetAbility(entityId, slot, new AbilityInstance(def)); AbilityCount[entityId]++; return true;
        }

        // Bug#9: Reset abilities for entity — clears all slots (used before re-initializing)
        public void ResetPlayerAbilities(int entityId) {
            if (!IsValidEntity(entityId)) return;
            RemoveAllGameplayEffects(entityId);
            AbilityCount[entityId] = 0;
        }

        // Legacy same-frame slot projection. Cross-frame callers must use handle-aware APIs.
        public AppliedEffect GetEffect(int entityId, int slot) {
            if (!TryGetActiveEffectAt(entityId, slot, out var runtime, out var definition, out var snapshot)) return default;
            var projection = LegacyEffectAdapter.ToProjection(runtime, definition, snapshot);
            ActiveEffects[entityId * MAX_ACTIVE_EFFECTS_PER_ENTITY + slot] = projection;
            return projection;
        }

        // Legacy same-frame slot projection. Cross-frame callers must use handle-aware APIs.
        public void SetEffect(int entityId, int slot, AppliedEffect eff) {
            if (!TryGetActiveEffectAt(entityId, slot, out var runtime, out _, out _)) return;
            runtime.RemainingTime = eff.RemainingTime;
            runtime.TickAccumulator = eff.TimeSinceLastTick;
            runtime.TicksRemaining = eff.TicksRemaining;
            runtime.StackCount = eff.StackCount;
            runtime.Clock = eff.Clock;
            runtime.FirstTick = eff.FirstTick;
            runtime.CatchUp = eff.CatchUp;
            runtime.SourceDeath = eff.SourceDeath;
            runtime.FirstTickPending = eff.FirstTickPending;
            TryUpdateActiveEffect(GetEntityHandle(entityId), runtime);
        }

        public int GetEffectCount(int entityId) {
            if (!IsValidEntity(entityId)) return 0;
            return ActiveEffectCount[entityId];
        }

        // Legacy same-frame compatibility entry; it still allocates a generation-owned handle.
        public void AddEffect(int entityId, AppliedEffect eff) { TryAddEffect(entityId, eff); }
        public bool TryAddEffect(int entityId, AppliedEffect eff) {
            var source = eff.Source.IsValid ? eff.Source : GetEntityHandle(eff.SourceEntityId);
            var target = eff.Target.IsValid ? eff.Target : GetEntityHandle(entityId);
            return TryAddGameplayEffect(entityId, LegacyEffectAdapter.FromProjection(eff, source, target), out _);
        }

        public bool TryAddGameplayEffect(int entityId, GameplayEffectApplication application, out EffectHandle handle)
        {
            handle = default(EffectHandle);
            if (!IsValidEntity(entityId)) return false;
            int slot = ActiveEffectCount[entityId];
            if (slot >= MAX_ACTIVE_EFFECTS_PER_ENTITY) { EffectPoolRejections++; OnGasPoolRejected?.Invoke(entityId, false); return false; }
            var target = GetEntityHandle(entityId);
            var runtime = application.Runtime;
            runtime.Target = target;
            application = new GameplayEffectApplication(application.Definition, application.LegacySnapshot, runtime);
            if (!GameplayEffects.TryAdd(application, out handle)) { EffectPoolRejections++; OnGasPoolRejected?.Invoke(entityId, false); return false; }
            int index = entityId * MAX_ACTIVE_EFFECTS_PER_ENTITY + slot;
            _activeEffectHandles[index] = handle;
            ActiveEffectCount[entityId] = slot + 1;
            if (GameplayEffects.TryGet(handle, out runtime, out var definition, out var snapshot))
                ActiveEffects[index] = LegacyEffectAdapter.ToProjection(runtime, definition, snapshot);
            TagState.AddGranted(entityId, application.Definition.GrantedTags);
            return true;
        }

        public bool TryGetEffect(EntityHandle target, EffectHandle handle, out AppliedEffect effect)
        {
            effect = default(AppliedEffect);
            if (!TryGetActiveEffect(target, handle, out var runtime, out var definition, out var snapshot)) return false;
            effect = LegacyEffectAdapter.ToProjection(runtime, definition, snapshot);
            return true;
        }

        public bool TrySetEffect(EntityHandle target, EffectHandle handle, AppliedEffect effect)
        {
            if (!TryGetActiveEffect(target, handle, out var runtime, out _, out _)) return false;
            runtime.RemainingTime = effect.RemainingTime;
            runtime.TickAccumulator = effect.TimeSinceLastTick;
            runtime.TicksRemaining = effect.TicksRemaining;
            runtime.StackCount = effect.StackCount;
            runtime.Clock = effect.Clock;
            runtime.FirstTick = effect.FirstTick;
            runtime.CatchUp = effect.CatchUp;
            runtime.SourceDeath = effect.SourceDeath;
            runtime.FirstTickPending = effect.FirstTickPending;
            return TryUpdateActiveEffect(target, runtime);
        }

        public bool TryRemoveEffect(EntityHandle target, EffectHandle handle, out AppliedEffect removed)
        {
            removed = default(AppliedEffect);
            if (!TryResolve(target, out int entityId, out _)) return false;
            int count = ActiveEffectCount[entityId];
            for (int slot = 0; slot < count; slot++)
            {
                if (!_activeEffectHandles[entityId * MAX_ACTIVE_EFFECTS_PER_ENTITY + slot].Equals(handle)) continue;
                if (!TryRemoveActiveEffectAt(entityId, slot, out var runtime, out var definition, out var snapshot)) return false;
                removed = LegacyEffectAdapter.ToProjection(runtime, definition, snapshot);
                return true;
            }
            return false;
        }

        public bool TryGetActiveEffectAt(int entityId, int slot, out ActiveGameplayEffect runtime, out GameplayEffectDefinition definition, out LegacyEffectSnapshot snapshot)
        {
            runtime = default(ActiveGameplayEffect); definition = default(GameplayEffectDefinition); snapshot = default(LegacyEffectSnapshot);
            if (!IsValidEntity(entityId) || slot < 0 || slot >= ActiveEffectCount[entityId]) return false;
            return GameplayEffects.TryGet(_activeEffectHandles[entityId * MAX_ACTIVE_EFFECTS_PER_ENTITY + slot], out runtime, out definition, out snapshot)
                && runtime.Target.Equals(GetEntityHandle(entityId));
        }

        public bool TryGetActiveEffect(EntityHandle target, EffectHandle handle, out ActiveGameplayEffect runtime, out GameplayEffectDefinition definition, out LegacyEffectSnapshot snapshot)
        {
            runtime = default(ActiveGameplayEffect); definition = default(GameplayEffectDefinition); snapshot = default(LegacyEffectSnapshot);
            if (!TryResolve(target, out int entityId, out _)) return false;
            int count = ActiveEffectCount[entityId];
            for (int slot = 0; slot < count; slot++)
                if (_activeEffectHandles[entityId * MAX_ACTIVE_EFFECTS_PER_ENTITY + slot].Equals(handle))
                    return GameplayEffects.TryGet(handle, out runtime, out definition, out snapshot) && runtime.Target.Equals(target);
            return false;
        }

        public bool TryUpdateActiveEffect(EntityHandle target, ActiveGameplayEffect runtime)
        {
            if (!TryResolve(target, out int entityId, out _) || !runtime.Target.Equals(target)) return false;
            int count = ActiveEffectCount[entityId];
            for (int slot = 0; slot < count; slot++)
            {
                int index = entityId * MAX_ACTIVE_EFFECTS_PER_ENTITY + slot;
                if (!_activeEffectHandles[index].Equals(runtime.Handle)) continue;
                if (!GameplayEffects.TryUpdate(runtime.Handle, runtime)) return false;
                if (GameplayEffects.TryGet(runtime.Handle, out runtime, out var definition, out var snapshot))
                    ActiveEffects[index] = LegacyEffectAdapter.ToProjection(runtime, definition, snapshot);
                return true;
            }
            return false;
        }

        public bool TryRemoveActiveEffectAt(int entityId, int slot, out ActiveGameplayEffect removed, out GameplayEffectDefinition definition, out LegacyEffectSnapshot snapshot)
        {
            removed = default(ActiveGameplayEffect); definition = default(GameplayEffectDefinition); snapshot = default(LegacyEffectSnapshot);
            if (!IsValidEntity(entityId) || slot < 0 || slot >= ActiveEffectCount[entityId]) return false;
            int baseIndex = entityId * MAX_ACTIVE_EFFECTS_PER_ENTITY;
            var handle = _activeEffectHandles[baseIndex + slot];
            if (!GameplayEffects.TryGet(handle, out removed, out definition, out snapshot)) return false;
            TagState.RemoveGranted(entityId, definition.GrantedTags);
            int lastSlot = ActiveEffectCount[entityId] - 1;
            if (slot != lastSlot)
            {
                _activeEffectHandles[baseIndex + slot] = _activeEffectHandles[baseIndex + lastSlot];
                ActiveEffects[baseIndex + slot] = ActiveEffects[baseIndex + lastSlot];
            }
            _activeEffectHandles[baseIndex + lastSlot] = default(EffectHandle);
            ActiveEffects[baseIndex + lastSlot] = default(AppliedEffect);
            ActiveEffectCount[entityId] = lastSlot;
            return GameplayEffects.Release(handle);
        }

        public void RemoveAllGameplayEffects(int entityId)
        {
            if (!IsValidEntity(entityId)) return;
            // 让 typed runtime 同步释放 modifier bookkeeping，再清理兼容槽位。
            GameplayEffectsRuntime.CleanupEntity(entityId);
            while (ActiveEffectCount[entityId] > 0)
                if (!TryRemoveActiveEffectAt(entityId, ActiveEffectCount[entityId] - 1, out _, out _, out _)) break;
            TagState.ClearEntity(entityId);
        }

        public void SetEffectCount(int entityId, int count) {
            if (!IsValidEntity(entityId)) return;
            if (count < 0) count = 0;
            if (count > MAX_ACTIVE_EFFECTS_PER_ENTITY) count = MAX_ACTIVE_EFFECTS_PER_ENTITY;
            while (ActiveEffectCount[entityId] > count)
                if (!TryRemoveActiveEffectAt(entityId, ActiveEffectCount[entityId] - 1, out _, out _, out _)) break;
        }

        // ==================== Wind Source 管理方法 ====================
        /// <summary>Add a wind source at the given position with specified parameters.</summary>
        public int AddWindSource(float x, float y, float radius, float direction, float strength, float duration, int ownerPlayer, int towerId = -1)
        {
            int sourceId = -1;
            lock (activeIdsLock)
            {
                for (int i = 0; i < MAX_WIND_SOURCES; i++)
                {
                    int candidateId = (_nextWindSourceId + i) % MAX_WIND_SOURCES;
                    if (!WindSourceActive[candidateId])
                    {
                        sourceId = candidateId;
                        _nextWindSourceId = (candidateId + 1) % MAX_WIND_SOURCES;
                        break;
                    }
                }
            }
            if (sourceId < 0) return -1; // no free slots

            WindSourceActive[sourceId] = true;
            WindSourceX[sourceId] = x;
            WindSourceY[sourceId] = y;
            WindSourceRadius[sourceId] = radius;
            WindSourceDirection[sourceId] = direction;
            WindSourceStrength[sourceId] = strength;
            WindSourceDuration[sourceId] = duration;
            WindSourceOwnerPlayer[sourceId] = ownerPlayer;
            WindSourceTowerId[sourceId] = towerId;
            _activeWindSourceCount++;
            return sourceId;
        }

        /// <summary>Remove a wind source by ID.</summary>
        public void RemoveWindSource(int sourceId)
        {
            if (sourceId < 0 || sourceId >= MAX_WIND_SOURCES) return;
            if (!WindSourceActive[sourceId]) return;
            WindSourceActive[sourceId] = false;
            WindSourceX[sourceId] = 0f;
            WindSourceY[sourceId] = 0f;
            WindSourceRadius[sourceId] = 0f;
            WindSourceDirection[sourceId] = 0f;
            WindSourceStrength[sourceId] = 0f;
            WindSourceDuration[sourceId] = 0f;
            WindSourceOwnerPlayer[sourceId] = -1;
            WindSourceTowerId[sourceId] = -1;
            _activeWindSourceCount--;
        }

        /// <summary>Get the count of active wind sources.</summary>
        public int GetActiveWindSourceCount() => _activeWindSourceCount;

        /// <summary>Check if a wind source is still active (has duration remaining).</summary>
        public bool IsWindSourceActive(int sourceId)
        {
            if (sourceId < 0 || sourceId >= MAX_WIND_SOURCES) return false;
            return WindSourceActive[sourceId];
        }

        /// <summary>
        /// Set global wind for a player. Overwrites any existing global wind.
        /// </summary>
        public void SetGlobalWind(int playerId, float direction, float strength, float duration, float gustInterval = 0f)
        {
            if (!IsValidPlayer(playerId)) return;
            GlobalWindDirection[playerId] = direction;
            GlobalWindStrength[playerId] = strength;
            GlobalWindActive[playerId] = true;
            GlobalWindDuration[playerId] = duration;
            GlobalWindGustInterval[playerId] = gustInterval;
            GlobalWindGustTimer[playerId] = gustInterval > 0f ? gustInterval : 0f;
            GlobalWindGustStrength[playerId] = 0f;
        }

        /// <summary>Clear global wind for a player.</summary>
        public void ClearGlobalWind(int playerId)
        {
            if (!IsValidPlayer(playerId)) return;
            GlobalWindActive[playerId] = false;
            GlobalWindStrength[playerId] = 0f;
            GlobalWindDuration[playerId] = 0f;
            GlobalWindGustTimer[playerId] = 0f;
            GlobalWindGustStrength[playerId] = 0f;
        }

        // ==================== Pull Source 管理方法 ====================
        /// <summary>Add a pull source at the given position with specified parameters.</summary>
        public int AddPullSource(float x, float y, float radius, float strength, float duration, int ownerPlayer, int towerId = -1)
        {
            int sourceId = -1;
            lock (activeIdsLock)
            {
                for (int i = 0; i < MAX_PULL_SOURCES; i++)
                {
                    int candidateId = (_nextPullSourceId + i) % MAX_PULL_SOURCES;
                    if (!PullSourceActive[candidateId])
                    {
                        sourceId = candidateId;
                        _nextPullSourceId = (candidateId + 1) % MAX_PULL_SOURCES;
                        break;
                    }
                }
            }

            if (sourceId < 0) return -1;

            PullSourceX[sourceId] = x;
            PullSourceY[sourceId] = y;
            PullSourceRadius[sourceId] = radius;
            PullSourceStrength[sourceId] = strength;
            PullSourceDuration[sourceId] = duration;
            PullSourceOwnerPlayer[sourceId] = ownerPlayer;
            PullSourceTowerId[sourceId] = towerId;
            PullSourceActive[sourceId] = true;
            _activePullSourceCount++;
            return sourceId;
        }

        /// <summary>Remove a pull source by ID.</summary>
        public void RemovePullSource(int sourceId)
        {
            if (sourceId < 0 || sourceId >= MAX_PULL_SOURCES) return;
            if (!PullSourceActive[sourceId]) return;
            PullSourceActive[sourceId] = false;
            PullSourceX[sourceId] = 0f;
            PullSourceY[sourceId] = 0f;
            PullSourceRadius[sourceId] = 0f;
            PullSourceStrength[sourceId] = 0f;
            PullSourceDuration[sourceId] = 0f;
            PullSourceOwnerPlayer[sourceId] = 0;
            PullSourceTowerId[sourceId] = -1;
            _activePullSourceCount--;
        }

        /// <summary>Get the count of active pull sources.</summary>
        public int GetActivePullSourceCount() => _activePullSourceCount;

        /// <summary>Check if a pull source is still active (has duration remaining).</summary>
        public bool IsPullSourceActive(int sourceId)
        {
            if (sourceId < 0 || sourceId >= MAX_PULL_SOURCES) return false;
            return PullSourceActive[sourceId];
        }

        /// <summary>
        /// Set global gravity well pull for a player. Overwrites any existing global pull.
        /// </summary>
        public void SetGlobalPull(int playerId, float centerX, float centerY, float strength, float duration)
        {
            if (!IsValidPlayer(playerId)) return;
            GlobalPullCenterX[playerId] = centerX;
            GlobalPullCenterY[playerId] = centerY;
            GlobalPullStrength[playerId] = strength;
            GlobalPullActive[playerId] = true;
            GlobalPullDuration[playerId] = duration;
        }

        /// <summary>Clear global pull for a player.</summary>
        public void ClearGlobalPull(int playerId)
        {
            if (!IsValidPlayer(playerId)) return;
            GlobalPullActive[playerId] = false;
            GlobalPullStrength[playerId] = 0f;
            GlobalPullDuration[playerId] = 0f;
            GlobalPullCenterX[playerId] = 0f;
            GlobalPullCenterY[playerId] = 0f;
        }

        // ==================== 科技树组件访问方法 ====================

        public int GetResearchPoints(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 0;
            return PlayerResearchPoints[playerId];
        }

        public void AddResearchPoints(int playerId, int amount)
        {
            if (!IsValidPlayer(playerId)) return;
            PlayerResearchPoints[playerId] += amount;
        }

        public bool IsTechUnlocked(int playerId, string nodeId)
        {
            if (!IsValidPlayer(playerId)) return false;
            return PlayerUnlockedTechs[playerId].Contains(nodeId);
        }

        public void UnlockTech(int playerId, string nodeId)
        {
            if (!IsValidPlayer(playerId)) return;
            PlayerUnlockedTechs[playerId].Add(nodeId);
        }

        public HashSet<string> GetUnlockedTechs(int playerId)
        {
            if (!IsValidPlayer(playerId)) return new HashSet<string>();
            // L-1 fix: return a defensive copy to prevent external mutation
            return new HashSet<string>(PlayerUnlockedTechs[playerId]);
        }

        // ── Path Tile Cost (Round 89) accessors ──
        // SetPathNodeTerrain: writes a terrain tag for a given waypoint index. Bounds-checked
        // against MAX_PATH_NODES so JSON loaders can pass arbitrary index 0..31 without
        // risking an index out of range. Out-of-range indices are silently dropped.
        public void SetPathNodeTerrain(int nodeIdx, int terrainTag)
        {
            if ((uint)nodeIdx >= (uint)MAX_PATH_NODES) return;
            PathNodeTerrain[nodeIdx] = terrainTag;
        }
        // GetPathNodeTerrain: read-only access. Out-of-range returns 0 (neutral) so a
        // misconfigured path can't accidentally crash the movement loop.
        public int GetPathNodeTerrain(int nodeIdx)
        {
            if ((uint)nodeIdx >= (uint)MAX_PATH_NODES) return 0;
            return PathNodeTerrain[nodeIdx];
        }
    }
}
