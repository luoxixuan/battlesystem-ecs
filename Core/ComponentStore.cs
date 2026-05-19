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
        private const int MAX_PLAYERS = 10;
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
        public int[] PlayerCurrentLevel = new int[MAX_PLAYERS];
        public float[] PlayerGold = new float[MAX_PLAYERS];
        public float[] PlayerUpgradeThreshold = new float[MAX_PLAYERS];
        private float _goldKillMultiplier = 1.0f;
        public float GoldKillMultiplier { get => _goldKillMultiplier; set => _goldKillMultiplier = value; }
        public List<string>[] PlayerBuffs = new List<string>[MAX_PLAYERS];

        // Perf: bit-flag buff storage — O(1) lookup, no GC allocation per frame
        public BuffType[] PlayerBuffFlags = new BuffType[MAX_PLAYERS];

        // ==================== 科技树组件的 SOA 存储 ====================
        public int[] PlayerResearchPoints = new int[MAX_PLAYERS];
        public HashSet<string>[] PlayerUnlockedTechs = new HashSet<string>[MAX_PLAYERS];

        // ==================== 敌人组件的 SOA 存储 ====================
        public float[] EnemyHealth = new float[MAX_ENTITIES];
        public float[] EnemyMaxHealth = new float[MAX_ENTITIES];
        public float[] EnemyMoveSpeed = new float[MAX_ENTITIES];
        public float[] EnemyDamage = new float[MAX_ENTITIES];
        public int[] EnemyGoldReward = new int[MAX_ENTITIES];
        public int[] EnemyWaveNumber = new int[MAX_ENTITIES];
        public bool[] EnemyActive = new bool[MAX_ENTITIES];
        public float[] EnemyChargeParam = new float[MAX_ENTITIES]; // SOA: replaces ConcurrentDictionary in EnemyAISystem
        public int[] EnemySpawnFrame = new int[MAX_ENTITIES];
        // Armor: reduces incoming damage. Affected by attacker's armor penetration.
        public float[] EnemyArmor = new float[MAX_ENTITIES];

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
        public string[] TowerType = new string[MAX_ENTITIES];
        public float[] TowerAttackDamage = new float[MAX_ENTITIES];
        public int[] TowerRange = new int[MAX_ENTITIES];
        public float[] TowerAttackSpeed = new float[MAX_ENTITIES];
        public int[] TowerLevel = new int[MAX_ENTITIES];
        public float[] TowerUpgradeCost = new float[MAX_ENTITIES];
        public bool[] TowerActive = new bool[MAX_ENTITIES];
        public float[] TowerLastAttackTime = new float[MAX_ENTITIES];

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
        private int nextEntityId = 2; // 从 2 开始，1 是玩家
        public int CurrentFrame { get; private set; } = 0;

        // Expose as read-only references — zero allocation on read. All writes go through internal API (Add/Remove).
        // Caller responsibility: read-only access only. Consistent with ref-return patterns in ECS frameworks.
        public IReadOnlyList<int> ActiveEnemyIds => _activeEnemyIds;
        public IReadOnlyList<int> ActiveTowerIds => _activeTowerIds;

        // Spatial Grid — O(1) range query for TowerAttackSystem
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

        private bool _deathQueueResolved = false;

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
                PlayerGold[playerId] += EnemyGoldReward[enemyId] * _goldKillMultiplier;
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
            // 初始化玩家 buffs
            for (int i = 0; i < MAX_PLAYERS; i++)
            {
                PlayerBuffs[i] = new List<string>();
                PlayerUnlockedTechs[i] = new HashSet<string>();
                PlayerBuffFlags[i] = BuffType.None;
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
                    return entityId;
                }
            }
            int entityId2 = Interlocked.Increment(ref nextEntityId) - 1;
            if (entityId2 >= MAX_ENTITIES) return -1;
            EnemyActionEnum[entityId2] = EnemyActionType.None;
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
                EnemyBehaviorTree[entityId] = null;
                EnemyTypeName[entityId] = null;
                EnemyAIAction[entityId] = null;
                EnemyCastAbilityId[entityId] = null;
                EnemyActionEnum[entityId] = EnemyActionType.None;
                EnemyAIChargeCounter[entityId] = 0;
                EnemyAILastAttackTurn[entityId] = 0;
                EnemyArmor[entityId] = 0f;
            }

            if (wasTower)
            {
                lock (activeIdsLock) { _activeTowerIds.Remove(entityId); }
                TowerActive[entityId] = false;
                TowerType[entityId] = null;
                TowerAttackDamage[entityId] = 0f;
                TowerRange[entityId] = 0;
                TowerAttackSpeed[entityId] = 0f;
                TowerLevel[entityId] = 0;
                TowerUpgradeCost[entityId] = 0f;
                TowerLastAttackTime[entityId] = 0f;
            }

            // ── Phase 4: recycle ID ───────────────────────────────────────────────
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

        public void AddPlayer(int entityId, float attackRange, float attackSpeed, float attackDamage, int currentLevel)
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

        public int AddEnemy(float startX, float startY, float moveSpeed, float health, float maxHealth, float damage, int goldReward, int waveNumber, string fullName = null, float armor = 0f)
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
            EnemyDamage[entityId] = damage;
            EnemyGoldReward[entityId] = goldReward;
            EnemyWaveNumber[entityId] = waveNumber;
            EnemyActive[entityId] = true;
            EnemySpawnFrame[entityId] = CurrentFrame;
            EnemyArmor[entityId] = armor;

            // 缓存怪物类型名（如 "NormalL1W1E0" -> "Normal"），避免每帧解析
            if (fullName != null)
            {
                int sepIdx = fullName.IndexOf('L');
                EnemyTypeName[entityId] = (sepIdx > 0) ? fullName.Substring(0, sepIdx) : fullName;
            }

            // H-race fix: lock Add to match Remove in DestroyEntity which uses lock(activeIdsLock)
            lock (activeIdsLock) { _activeEnemyIds.Add(entityId); }
            return entityId;
        }

        public void AddTower(int entityId, string type, float damage, int range, float speed, int level, float cost)
        {
            if (entityId < 0 || entityId >= MAX_ENTITIES) return;
            TowerType[entityId] = type;
            TowerAttackDamage[entityId] = damage;
            TowerRange[entityId] = range;
            TowerAttackSpeed[entityId] = speed;
            TowerLevel[entityId] = level;
            TowerUpgradeCost[entityId] = cost;
            TowerActive[entityId] = true;
            TowerLastAttackTime[entityId] = 0f;
            // M-race fix: lock Add to match Remove in DestroyEntity which uses lock(activeIdsLock)
            lock (activeIdsLock) { _activeTowerIds.Add(entityId); }
        }

        public void RemoveTower(int entityId)
        {
            if (entityId < 0 || entityId >= MAX_ENTITIES) return;
            TowerActive[entityId] = false;
            lock (activeIdsLock) { _activeTowerIds.Remove(entityId); }
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

        public float GetEnemyMoveSpeed(int enemyId)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return 0f;
            return EnemyMoveSpeed[enemyId];
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
        /// Safe for concurrent read access across all systems within a frame.
        /// Falls back to a fresh allocation if the list is empty (test/standalone scenarios).
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

        public void SetPlayerCurrentHealth(int playerId, float currentHealth)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            PlayerCurrentHealth[playerId] = currentHealth;
        }

        public void DecreasePlayerHealth(int playerId, float damage)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            PlayerCurrentHealth[playerId] = System.Math.Max(0f, PlayerCurrentHealth[playerId] - damage);
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
    }
}
