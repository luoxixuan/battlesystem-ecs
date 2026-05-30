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
        #endregion

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

        public void AddAbility(int entityId, GameplayAbilityDef def) {
            if (!IsValidEntity(entityId)) return;
            int slot = AbilityCount[entityId];
            if (slot < MAX_ABILITIES_PER_ENTITY) { SetAbility(entityId, slot, new AbilityInstance(def)); AbilityCount[entityId]++; }
        }

        // Bug#9: Reset abilities for entity — clears all slots (used before re-initializing)
        public void ResetPlayerAbilities(int entityId) {
            if (!IsValidEntity(entityId)) return;
            AbilityCount[entityId] = 0;
            ActiveEffectCount[entityId] = 0;
        }

        public AppliedEffect GetEffect(int entityId, int slot) {
            if (!IsValidEntity(entityId)) return default;
            if (slot < 0 || slot >= MAX_ACTIVE_EFFECTS_PER_ENTITY) return default;
            return ActiveEffects[entityId * MAX_ACTIVE_EFFECTS_PER_ENTITY + slot];
        }

        public void SetEffect(int entityId, int slot, AppliedEffect eff) {
            if (!IsValidEntity(entityId)) return;
            if (slot < 0 || slot >= MAX_ACTIVE_EFFECTS_PER_ENTITY) return;
            ActiveEffects[entityId * MAX_ACTIVE_EFFECTS_PER_ENTITY + slot] = eff;
        }

        public int GetEffectCount(int entityId) {
            if (!IsValidEntity(entityId)) return 0;
            return ActiveEffectCount[entityId];
        }

        public void AddEffect(int entityId, AppliedEffect eff) {
            if (!IsValidEntity(entityId)) return;
            int slot = ActiveEffectCount[entityId];
            if (slot < MAX_ACTIVE_EFFECTS_PER_ENTITY) { SetEffect(entityId, slot, eff); ActiveEffectCount[entityId]++; }
        }

        public void SetEffectCount(int entityId, int count) {
            if (!IsValidEntity(entityId)) return;
            if (count < 0) count = 0;
            if (count > MAX_ACTIVE_EFFECTS_PER_ENTITY) count = MAX_ACTIVE_EFFECTS_PER_ENTITY;
            ActiveEffectCount[entityId] = count;
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
    }
}
