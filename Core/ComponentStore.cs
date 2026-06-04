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
    /// SOA (Struct of Arrays) component storage.
    /// Provides cache-friendly continuous memory layout for high-throughput ECS operations.
    /// </summary>
        public partial class ComponentStore : IDisposable
        {
            #region Constants & Helpers
        public const int MAX_ENTITIES = 100000;
        internal const int MAX_PLAYERS = 10;
        internal const int MAX_MORPHS = 4; // max morph modes per tower (2 default + 2 alt forms)
        // MAX_PATH_NODES: max waypoints supported per path. Largest default path has 5
        // waypoints; 32 leaves headroom for custom levels without breaking the SOA lookup.
        // Used by PathNodeTerrain[] and per-enemy path-terrain mult computation.
        internal const int MAX_PATH_NODES = 32;
        public int TotalKills = 0;

        // ── Performance counters (O(1) instead of O(N) per-frame pre-scans) ──
        /// <summary>Count of active enemies with TrampleRadius > 0 && TrampleDamagePerStep > 0.
        /// Maintained by AddEnemy/DestroyEntity. Read by EnemyMovementSystem to skip ResolveTrampleAoe.</summary>
        public int ActiveTramplerCount = 0;
        /// <summary>Count of active enemies with TetherMaxLength > 0.
        /// Maintained by AddEnemy/DestroyEntity. Read by EnemyMovementSystem/TowerAttackSystem.</summary>
        public int ActiveTetheredCount = 0;
        /// <summary>Count of active wisp across all players (PlayerWispType != None).
        /// Maintained by SpawnWisp/RemoveWisp. Read by WispSystem to skip Update.</summary>
        public int ActiveWispCount = 0;
        /// <summary>Count of active palisade towers (Round 100).
        /// Maintained by PlaceTower (++) and DestroyEntity (--).
        /// Read by EnemyMovementSystem to early-out the O(N×T) palisade collision loop
        /// when no palisades exist (the common case in standard tower comps).</summary>
        public int ActivePalisadeCount = 0;

        // Inline boundary check helpers — replaces 100+ manual checks with zero-overhead guards.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static bool IsValidEntity(int id) => (uint)id < MAX_ENTITIES;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static bool IsValidPlayer(int id) => (uint)id < MAX_PLAYERS;

        #endregion

        #region Position Components
        public float[] PositionX = new float[MAX_ENTITIES];
        public float[] PositionY = new float[MAX_ENTITIES];
        public bool[] PositionActive = new bool[MAX_ENTITIES];

        // ── Tile Occupancy Cache (Round 95 Direction 4) ─────────────────────
        // O(1) per-tile tower occupancy check. Backs PlaceTower / PreviewPlacement /
        // RelocateTower so they no longer scan ActiveTowerIds. Bounds default to 10×20
        // matching GameConfig.MapWidth/MapHeight; can be grown via ResizeTileOccupancy.
        // false = empty tile, true = tower present at (x, y).
        public const int TILE_GRID_DEFAULT_WIDTH = 10;
        public const int TILE_GRID_DEFAULT_HEIGHT = 20;
        public bool[,] TileOccupied = new bool[TILE_GRID_DEFAULT_WIDTH, TILE_GRID_DEFAULT_HEIGHT];
        public int TileOccupiedWidth = TILE_GRID_DEFAULT_WIDTH;
        public int TileOccupiedHeight = TILE_GRID_DEFAULT_HEIGHT;

        /// <summary>
        /// O(1) check whether the (x, y) tile currently holds a tower.
        /// Returns false for out-of-bounds coordinates (treat as unoccupied for
        /// callers that already pre-filter bounds; the placement path enforces
        /// bounds separately and returns -1 before reaching here).
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public bool IsTileOccupied(int x, int y)
        {
            if ((uint)x >= (uint)TileOccupiedWidth) return false;
            if ((uint)y >= (uint)TileOccupiedHeight) return false;
            return TileOccupied[x, y];
        }

        /// <summary>
        /// O(1) write to the tile occupancy cache. Used by PlaceTower (mark true
        /// on success) and DestroyEntity / RelocateTower (mark false on remove).
        /// Silently no-ops on out-of-bounds so callers don't have to guard twice.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void SetTileOccupied(int x, int y, bool occupied)
        {
            if ((uint)x >= (uint)TileOccupiedWidth) return;
            if ((uint)y >= (uint)TileOccupiedHeight) return;
            TileOccupied[x, y] = occupied;
        }

        /// <summary>
        /// Resize the occupancy grid to match a new map size. Clears all slots.
        /// Call once during game initialization after the map size is known
        /// (mirrors the SetMapSize pattern on the spatial grid).
        /// </summary>
        public void ResizeTileOccupancy(int width, int height)
        {
            if (width <= 0 || height <= 0) return;
            TileOccupied = new bool[width, height];
            TileOccupiedWidth = width;
            TileOccupiedHeight = height;
        }

        #endregion

        // ==================== 实体管理 ====================
        public int PlayerEntityId { get; private set; } = 1;
        private List<int> _activeEnemyIds = new List<int>();
        private List<int> _activeTowerIds = new List<int>();
        private List<int> _activeObstacleIds = new List<int>();
        // O(1) position lookup for swap-and-pop removal (avoids O(n) List.Remove)
        private int[] _enemyIndexInList = new int[MAX_ENTITIES];
        private int[] _towerIndexInList = new int[MAX_ENTITIES];
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
        public void AddActiveEnemyId(int id)
        {
            _activeEnemyIds.Add(id);
            _enemyIndexInList[id] = _activeEnemyIds.Count - 1;
        }
        public void AddActiveTowerId(int id)
        {
            _activeTowerIds.Add(id);
            _towerIndexInList[id] = _activeTowerIds.Count - 1;
        }

        // ── O(1) swap-and-pop removal helpers (avoids List.Remove O(n) scan) ──
        private void RemoveEnemyFromList(int entityId)
        {
            int idx = _enemyIndexInList[entityId];
            if (idx < 0) return;
            int lastIdx = _activeEnemyIds.Count - 1;
            int lastId = _activeEnemyIds[lastIdx];
            _activeEnemyIds[idx] = lastId;
            _enemyIndexInList[lastId] = idx;
            _activeEnemyIds.RemoveAt(lastIdx);
            _enemyIndexInList[entityId] = -1;
        }

        private void RemoveTowerFromList(int entityId)
        {
            int idx = _towerIndexInList[entityId];
            if (idx < 0) return;
            int lastIdx = _activeTowerIds.Count - 1;
            int lastId = _activeTowerIds[lastIdx];
            _activeTowerIds[idx] = lastId;
            _towerIndexInList[lastId] = idx;
            _activeTowerIds.RemoveAt(lastIdx);
            _towerIndexInList[entityId] = -1;
        }

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
            if (!IsValidEntity(enemyId)) return;
            if (!IsValidPlayer(playerId)) return;
            _deathQueue[_deathQueueIdx].Add((enemyId, playerId));
        }

        /// <summary>
        /// Queue a tower kill event from a parallel or serial context.
        /// The towerId is used by TowerExperienceSystem to grant XP.
        /// </summary>
        public void QueueTowerKill(int enemyId, int playerId, int towerId)
        {
            if (!IsValidEntity(enemyId)) return;
            if (!IsValidPlayer(playerId)) return;
            if (!IsValidEntity(towerId)) return;
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
                // ── Decaying Wave Bounty: subsequent kills in the same wave pay less. ──
                // Formula: mult = max(DecayFloor, 1.0 - kills * DecayRate)
                // DecayRate=0.02 → 5 kills = 90%, 10 = 80%, 20 = 60%, floor at 0.3 (30%) after 35 kills.
                // Counts kill BEFORE the decay is applied so the first kill pays 100% gold.
                int killsThisWave = PlayerWaveKillCount[playerId];
                float decayMult = Math.Max(_waveGoldDecayFloor, 1.0f - killsThisWave * _waveGoldDecayRate);
                goldReward *= decayMult;
                PlayerGold[playerId] += goldReward;
                // Bump per-player kill counter AFTER the gold has been calculated and awarded.
                // Capped at int.MaxValue-1 to avoid overflow on absurd kill counts (e.g. long benchmarks).
                if (PlayerWaveKillCount[playerId] < int.MaxValue - 1)
                {
                    PlayerWaveKillCount[playerId]++;
                }
                if (_goldOnEliteKill > 0f && EnemyIsElite[enemyId])
                    PlayerGold[playerId] += _goldOnEliteKill;
                // Death Mark / Execute bonus gold: +50% extra gold for executing a marked enemy.
                // Self-balancing — only triggers once per enemy (on death), so no chain exploits.
                if (EnemyMarked[enemyId])
                {
                    float markBonus = goldReward * EnemyMarkedDamageBonus[enemyId];
                    PlayerGold[playerId] += markBonus;
                }
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
            // Initialize O(1) swap-and-pop index arrays
            for (int i = 0; i < MAX_ENTITIES; i++)
                _enemyIndexInList[i] = _towerIndexInList[i] = -1;
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
            // Round 100 — capture whether this tower was a palisade BEFORE reset, so we can
            // decrement ActivePalisadeCount when the entity is destroyed.
            bool wasPalisade = wasTower && TowerIsPalisade[entityId];

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
                lock (activeIdsLock) { RemoveEnemyFromList(entityId); }
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
                // CC Immunity (Round 97): reset mask on entity destroy to avoid ID-reuse leakage
                EnemyCCImmuneMask[entityId] = 0;
                EnemySlowFactor[entityId] = 0f;
                EnemyTerrainMoveSpeedMult[entityId] = 1f;
                EnemyFrostZoneSlowMultiplier[entityId] = 1f;  // default: no frost zone, neutral 1x
                EnemyMoveSpeedBase[entityId] = 0f;
                EnemySlowDurationLeft[entityId] = 0f;
                // Polymorph CC fields (reset on entity destruction)
                EnemyIsPolymorphed[entityId] = false;
                EnemyPolymorphDurationLeft[entityId] = 0f;
                EnemyPolymorphDamageTakenMultiplier[entityId] = 1f;
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
                // Leap / Jump Attack fields (0/-1 = no leap ability, zero-overhead default)
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
                // Resistance fields
                EnemyStunResistance[entityId] = 0f;
                EnemyFreezeResistance[entityId] = 0f;
                EnemySlowResistance[entityId] = 0f;
                EnemyKnockbackResistance[entityId] = 0f;
                EnemyDamageResistance[entityId] = 0f;
                EnemyIsUnstoppable[entityId] = false;
                EnemyFearResistance[entityId] = 0f;
                // Curse debuff fields (applied by curse towers)
                EnemyCurseDmgReduction[entityId] = 0f;
                // Round 83: Elemental Exposure — reset to default (no exposure, no timer)
                EnemyExposureMask[entityId] = ElementType.None;
                EnemyExposureTimer[entityId] = 0f;
                EnemyCurseSpeedReduction[entityId] = 0f;
                EnemyCurseArmorReduction[entityId] = 0f;
                EnemyCurseDmgTakenIncrease[entityId] = 0f;
                // Healing reduction anti-heal debuffs
                EnemyHealingReduction[entityId] = 0f;
                EnemyHealingReductionDuration[entityId] = 0f;
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
                // LastStand / DeathRattle fields
                EnemyLastStandHpFraction[entityId] = 0f;
                EnemyLastStandActive[entityId] = false;
                EnemyLastStandSpeedMult[entityId] = 1f;
                EnemyLastStandDamageMult[entityId] = 1f;
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
                // Phase / ghost fields
                EnemyIsPhased[entityId] = false;
                EnemyPhaseDuration[entityId] = 0f;
                EnemyPhaseTimer[entityId] = 0f;
                EnemyPhaseCooldown[entityId] = 0f;
                // Death Mark / Execute fields (reset on entity destruction)
                EnemyMarked[entityId] = false;
                EnemyMarkedThreshold[entityId] = 0.15f;
                EnemyMarkedDamageBonus[entityId] = 0.5f;
                // Decoy fields (reset on entity destruction)
                EnemyIsDecoy[entityId] = false;
                EnemyDecoyLifetime[entityId] = 0f;
                EnemyDecoyLifetimeLeft[entityId] = 0f;
                // Free-Roam fields (Round 84): reset on entity destruction
                EnemyIsFreeRoam[entityId] = false;
                EnemyWanderTargetX[entityId] = 0f;
                EnemyWanderTargetY[entityId] = 0f;
                EnemyWanderRerollTimer[entityId] = 0f;
                // Banish fields (reset on entity destruction)
                EnemyIsBanished[entityId] = false;
                EnemyBanishDurationLeft[entityId] = 0f;
                EnemyBanishOriginalX[entityId] = 0f;
                EnemyBanishOriginalY[entityId] = 0f;
                // Stagger / Posture fields (reset on entity destruction)
                EnemyStaggerMeter[entityId] = 0f;
                EnemyStaggerMax[entityId] = 0f;
                EnemyStaggerDurationLeft[entityId] = 0f;
                EnemyStaggerImmuneTimer[entityId] = 0f;
                EnemyIsStaggered[entityId] = false;
                // Channeling fields (reset on entity destruction — kills interrupt channel)
                EnemyIsChanneling[entityId] = false;
                EnemyChannelTimer[entityId] = 0f;
                EnemyChannelAbilityId[entityId] = null;
                EnemyChannelInterruptible[entityId] = true;
                // Faction / Infighting (Round 90): reset on destruction (no leaked faction/cooldown)
                EnemyFactionId[entityId] = 0;
                EnemyInfightCooldown[entityId] = 0f;

                // ── Performance counter maintenance ──
                if (EnemyTrampleRadius[entityId] > 0f && EnemyTrampleDamagePerStep[entityId] > 0f)
                    ActiveTramplerCount = Math.Max(0, ActiveTramplerCount - 1);
                if (EnemyTetherMaxLength[entityId] > 0f)
                    ActiveTetheredCount = Math.Max(0, ActiveTetheredCount - 1);
            }

            if (wasTower)
            {
                lock (activeIdsLock) { RemoveTowerFromList(entityId); }
                // Round 95: release the tile this tower occupied so future
                // PlaceTower / RelocateTower can claim it again. Read position
                // BEFORE zeroing the position fields below.
                int tileX = (int)PositionX[entityId];
                int tileY = (int)PositionY[entityId];
                SetTileOccupied(tileX, tileY, false);
                TowerActive[entityId] = false;
                TowerIsAntiPhase[entityId] = false;
                TowerTargetingMode[entityId] = Components.TowerTargetingMode.Nearest;
                TowerType[entityId] = Components.TowerType.Basic;
                TowerAttackDamage[entityId] = 0f;
                TowerRange[entityId] = 0;
                TowerAttackSpeed[entityId] = 0f;
                TowerLevel[entityId] = 0;
                TowerUpgradeCost[entityId] = 0f;
                TowerTotalUpgradeSpent[entityId] = 0f;
                TowerUpgradePathId[entityId] = null;
                TowerFusionTier[entityId] = 0;
                TowerLastAttackTime[entityId] = 0f;
                TowerStunChance[entityId] = 0f;
                TowerSlowAmount[entityId] = 0f;
                TowerSlowDuration[entityId] = 0f;
                TowerPlaceTime[entityId] = 0f;
                TowerCanHitAir[entityId] = false;
                TowerCanHitGround[entityId] = false;
                // Aura tower fields (Round 96 keep, do not remove)
                TowerIsAuraTower[entityId] = false;
                TowerAuraRadius[entityId] = 0f;
                TowerAuraAttackSpeedBonus[entityId] = 0f;
                TowerAuraDamageBonus[entityId] = 0f;
                // Player-disabled flag (Round 96): default false on recycle
                TowerPlayerDisabled[entityId] = false;
                // Round 98 — Windup fields reset (recycled slot must start with no windup)
                TowerWindupFrames[entityId] = 0;
                TowerWindupCountdown[entityId] = 0;
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
                // Taunt tower fields
                TowerIsTaunt[entityId] = false;
                TowerTauntRadius[entityId] = 0f;
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
                // Shotgun pellet fields (reset on recycle so stale values don't leak)
                TowerPelletDamageMult[entityId] = 1f;
                TowerPelletConeRadius[entityId] = 0f;
                // Overcharge fields
                TowerIsOvercharged[entityId] = false;
                TowerOverchargeDuration[entityId] = 0f;
                TowerOverchargeCooldown[entityId] = 0f;
                TowerCanOvercharge[entityId] = false;
                // Player-disabled flag: false (active) on entity recycle so stale 'true' from a
                // previous owner doesn't carry over. ToggleTower() flips it back if needed.
                TowerPlayerDisabled[entityId] = false;
                // Round 100 — Palisade fields reset (recycled slot must not leak palisade state)
                TowerIsPalisade[entityId] = false;
                PalisadeStunFrames[entityId] = 0;
                PalisadeBlockRadius[entityId] = 0;
                PalisadeHP[entityId] = 0f;
                PalisadeMaxHP[entityId] = 0f;
                // Round 100 — Palisade frame-scratch fields reset (Claude bug scan fix #1):
                // the per-tower accumulator + destroy flag must not leak into a recycled slot.
                PalisadeContactDamageAccumulator[entityId] = 0f;
                PalisadeDestroyFlag[entityId] = false;
                // Maintain ActivePalisadeCount (Round 100) — decrement only if was palisade
                if (wasPalisade)
                    ActivePalisadeCount = Math.Max(0, ActivePalisadeCount - 1);
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
            if (!IsValidEntity(entityId)) return;

            PositionX[entityId] = x;
            PositionY[entityId] = y;
            PositionActive[entityId] = true;
        }

        public void SetPosition(int entityId, float x, float y)
        {
            if (!IsValidEntity(entityId)) return;

            PositionX[entityId] = x;
            PositionY[entityId] = y;
        }
        // ==================== 实体查询 ====================

        public bool IsEnemyActive(int entityId)
        {
            if (!IsValidEntity(entityId)) return false;
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

        /// <summary>
        /// Zero-allocation read-only span access to active enemy IDs. Safe — no mutable reference exposed.
        /// Prefer this over GetCachedActiveEnemyIds() in new code.
        /// </summary>
        public ReadOnlySpan<int> GetActiveEnemySpan()
        {
            return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_activeEnemyIds);
        }

        /// <summary>
        /// Zero-allocation read-only span access to active tower IDs.
        /// </summary>
        public ReadOnlySpan<int> GetActiveTowerSpan()
        {
            return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_activeTowerIds);
        }

        // ==================== IDisposable ====================

        /// <summary>
        /// Release large arrays to help GC reclaim memory in long-running server scenarios.
        /// Nullifies all SOA arrays — call only when the store is permanently done.
        /// </summary>
        public void Dispose()
        {
            PositionX = null!; PositionY = null!; PositionActive = null!;
            PlayerAttackRange = null!; PlayerAttackSpeed = null!; PlayerAttackDamage = null!;
            PlayerMaxHealth = null!; PlayerCurrentHealth = null!; PlayerArmor = null!;
            PlayerShield = null!; PlayerShieldDuration = null!; PlayerThornsRatio = null!;
            PlayerCurrentLevel = null!; PlayerDamageType = null!;
            PlayerGold = null!; PlayerUpgradeThreshold = null!;
            PlayerMana = null!; PlayerMaxMana = null!; PlayerManaRegen = null!; PlayerManaCost = null!;
            PlayerGlobalSkillUnlocked = null!; PlayerGlobalSkillCooldown = null!;
            PlayerGlobalSkillPressed = null!; PlayerGlobalSkillHotkey = null!;
            PlayerSkillResetOnKill = null!; PlayerSkillResetAmount = null!;
            PlayerBuffFlags = null!; PlayerStunDuration = null!;
            PlayerSlowFactor = null!; PlayerSlowDuration = null!;
            PlayerBaseLives = null!; PlayerMaxBaseLives = null!;
            CurrentWeather = null!; WeatherIntensity = null!; WeatherTimer = null!;
            GlobalDayNightPhase = null!; GlobalDayNightTimer = null!; GlobalDayNightCycleCount = null!;
            CurrentObjectiveType = null!;
            EscortNpcX = null!; EscortNpcY = null!; EscortNpcHealth = null!; EscortNpcMaxHealth = null!;
            EscortNpcActive = null!; EscortNpcSpeed = null!;
            ObjectiveTimer = null!; ObjectiveWavesRemaining = null!; ObjectiveTimeLimit = null!;
            ObjectiveWaveScore = null!; ObjectiveHealthScore = null!;
            EnemiesLeakedThisWave = null!; AdaptiveDifficultyLevel = null!; AdaptiveDifficultyScore = null!;
            ResourceNodeX = null!; ResourceNodeY = null!; ResourceNodeOwner = null!; ResourceNodeType = null!;
            ResourceNodeActive = null!; ResourceNodeProductionRate = null!;
            ResourceNodeHealth = null!; ResourceNodeMaxHealth = null!;
            ResourceNodeAccumulated = null!; ResourceNodeCaptureProgress = null!; ResourceNodeTowerId = null!;
            PathModifierX = null!; PathModifierY = null!; PathModifierRadius = null!;
            PathModifierActive = null!; PathModifierOwnerId = null!; PathModifierTargetPathId = null!;
            PathModifierTurnsRemaining = null!;
            GlobalTimeScale = null!; GlobalTimeScaleDuration = null!;
            RandomEventCooldown = null!; RandomEventActiveType = null!; RandomEventTimer = null!;
            RandomEventParam = null!; RandomEventParam2 = null!;
            TowerVisionRadius = null!; GlobalFogDensity = null!;
            AscensionModifierStacks = null!;
            PlayerResearchPoints = null!;
            PickupX = null!; PickupY = null!; PickupType = null!; PickupValue = null!;
            PickupOwnerId = null!; PickupActive = null!; PickupLifetime = null!; PickupRarity = null!;
            PlayerWaveIndex = null!; PlayerEnemiesRemaining = null!; PlayerIsWaveActive = null!;
            PlayerWaveTimer = null!; PlayerWaveCompleteGold = null!;
            CurrentWaveMutatorId = null!;
            PlayerComboCount = null!; PlayerComboTimer = null!; PlayerComboDamageMult = null!;
            PlayerComboKillStreak = null!; PlayerComboGoldMult = null!;
            PlayerBankedGold = null!; PlayerInterestRate = null!;
            EnemyHealth = null!; EnemyMaxHealth = null!; EnemyMoveSpeed = null!; EnemyDamage = null!;
            EnemyGoldReward = null!; EnemyWaveNumber = null!; EnemyActive = null!;
            EnemyChargeParam = null!; EnemyBuffDamageBonus = null!; EnemyBuffDurationLeft = null!;
            EnemySpawnFrame = null!; EnemyArmor = null!; EnemyMagicResist = null!; EnemyEvasion = null!;
            EnemyShield = null!; EnemyThornsRatio = null!;
            EnemyArmorShredStacks = null!; EnemyArmorShredDuration = null!;
            EnemyCurseDmgReduction = null!; EnemyCurseSpeedReduction = null!;
            EnemyCurseArmorReduction = null!; EnemyCurseDmgTakenIncrease = null!;
            EnemyDeflectChance = null!;
            EnemyBleedStacks = null!; EnemyBleedDamagePerStack = null!; EnemyBleedTimer = null!;
            EnemyBleedMaxStacks = null!; EnemyBleedResistance = null!; EnemyBleedDurationLeft = null!;
            EnemyStunFlag = null!; EnemyStunDurationLeft = null!; EnemySlowFactor = null!;
            EnemyTerrainMoveSpeedMult = null!; EnemyMoveSpeedBase = null!; EnemySlowDurationLeft = null!;
            EnemyFrostZoneSlowMultiplier = null!;
            EnemyIsPolymorphed = null!; EnemyPolymorphDurationLeft = null!; EnemyPolymorphDamageTakenMultiplier = null!;
            EnemyWoundThreshold = null!; EnemyWoundSlowRatio = null!; EnemyIsWounded = null!;
            EnemyKnockbackForceLeft = null!; EnemyIsElite = null!; EnemyIsFlying = null!;
            EnemyFlightHeight = null!; EnemyCanLand = null!; EnemyStealthMultiplier = null!;
            EnemyIsBurrowed = null!; EnemyBurrowTimer = null!; EnemyBurrowCooldown = null!;
            EnemyBurrowCooldownRef = null!; EnemyBurrowSpeedMult = null!;
            EnemyBurrowEmergeDamage = null!; EnemyBurrowRadius = null!;
            EnemyCanResurrect = null!; EnemyResurrectRange = null!; EnemyResurrectCooldown = null!;
            EnemyResurrectCooldownRef = null!; EnemyResurrectHpMult = null!;
            EnemyMaxResurrectCount = null!; EnemyResurrectCorpseAgeLimit = null!;
            EnemyIsReanimated = null!; EnemyOwnerId = null!;
            SummonedUnitActive = null!; SummonedUnitType = null!;
            SummonedUnitHealth = null!; SummonedUnitMaxHealth = null!;
            SummonedUnitDamage = null!; SummonedUnitMoveSpeed = null!;
            SummonedUnitAttackRange = null!; SummonedUnitAttackSpeed = null!; SummonedUnitAttackTimer = null!;
            SummonedUnitDuration = null!; SummonedUnitOwnerId = null!;
            SummonedUnitTargetId = null!; SummonedUnitGoldReward = null!;
            EnemyBossPhase = null!; EnemyPhaseThresholds = null!;
            EnemyEnrageTimer = null!; EnemyIsEnraged = null!;
            EnemyIsInvulnerable = null!; EnemyInvulnerablePhaseName = null!;
            EnemyFissionDefId = null!; EnemyFissionGeneration = null!;
            EnemyMorphDefId = null!; EnemyIsMorphed = null!; EnemyMorphTriggered = null!;
            EnemyCloneDefId = null!; EnemyCloneCooldown = null!; EnemyCloneTimer = null!;
            EnemyCloneCount = null!; EnemyIsClone = null!; EnemyCloneMasterId = null!;
            EnemyIsLifeLinker = null!; EnemyLifeLinkDefId = null!; EnemyLinkedEnemyId = null!;
            EnemyLifeLinkRatio = null!; EnemyLifeLinkCooldownLeft = null!; EnemyIsLinked = null!;
            EnemyPathId = null!; EnemyPathNodeIndex = null!;
            EnemyTeleportCooldown = null!; EnemyTeleportDestinationX = null!;
            EnemyTeleportDestinationY = null!; EnemyTeleportType = null!;
            EnemyLeaperArchetype = null!; EnemyLeapDistance = null!;
            EnemyLeapCooldown = null!; EnemyLeapCooldownRef = null!;
            EnemyLeapDuration = null!; EnemyLeapStartX = null!; EnemyLeapStartY = null!;
            EnemyLeapTargetX = null!; EnemyLeapTargetY = null!; EnemyLeapElapsed = null!;
            EnemyLeapDamage = null!; EnemyLeapRadius = null!; EnemyLeapStunDuration = null!;
            EnemyFearDurationLeft = null!; EnemyTauntTargetId = null!; EnemyCharmDurationLeft = null!;
            EnemyStunResistance = null!; EnemyFreezeResistance = null!; EnemySlowResistance = null!;
            EnemyKnockbackResistance = null!; EnemyDamageResistance = null!;
            EnemyIsVanguard = null!; EnemyVanguardCoverRange = null!; EnemyVanguardDmgTransfer = null!;
            EnemyVanguardCoverCount = null!;
            EnemyHealerHealAmount = null!; EnemyHealerHealInterval = null!; EnemyHealerHealTargetPriority = null!;
            EnemyCanStealGold = null!; EnemyStealAmount = null!; EnemyStolenGold = null!;
            EnemyGoldOnReturn = null!; EnemyHasStolenGold = null!;
            EnemyAffixFlags = null!; EnemyElementStatus = null!; EnemyElementTimer = null!;
            EnemyExposureMask = null!; EnemyExposureTimer = null!;
            NestDefId = null!; NestHealth = null!; NestMaxHealth = null!;
            NestSpawnTimer = null!; NestSpawnInterval = null!; NestMonsterTypeStr = null!;
            NestMaxAlive = null!; NestActiveCount = null!; NestOriginId = null!;
            EnemyAIAction = null!; EnemyAIChargeCounter = null!; EnemyAILastAttackTurn = null!;
            EnemyTypeName = null!; EnemyBehaviorTree = null!; EnemyActionEnum = null!;
            EnemyCastAbilityId = null!;
            EnemyMarked = null!; EnemyMarkedThreshold = null!; EnemyMarkedDamageBonus = null!;
            EnemyIsDecoy = null!; EnemyDecoyLifetime = null!; EnemyDecoyLifetimeLeft = null!;
            // Round 84 Direction 6: Free-Roam Enemies — opt-in via monsterConfig.Type == "FreeRoam"
            EnemyIsFreeRoam = null!; EnemyWanderTargetX = null!; EnemyWanderTargetY = null!;
            EnemyWanderRerollTimer = null!;
            // Faction / Infighting (Round 90): default opt-out (no faction)
            EnemyFactionId = null!; EnemyInfightCooldown = null!;
            // FactionInfightEnabled: lazy gate for the O(N) scan + O(N) cooldown-decrement loop
            // in EnemyAISystem.ResolveFactionInfighting. Default 0 = disabled (zero overhead).
            // WaveSpawningSystem flips to 1 when any spawned enemy has FactionId > 0.
            // We use a single int (not bool[]) so it's a 1-byte check, not 100K cache-line read.
            FactionInfightEnabled = 0;
            TowerTargetingMode = null!; TowerProjectileHoming = null!; TowerInterceptRate = null!;
            TowerDamageType = null!; TowerSelected = null!; TowerType = null!;
            TowerAttackDamage = null!; TowerRange = null!; TowerAttackSpeed = null!;
            TowerLevel = null!; TowerUpgradeCost = null!; TowerTotalUpgradeSpent = null!; TowerUpgradePathId = null!;
            TowerFusionTier = null!; TowerActive = null!; TowerLastAttackTime = null!;
            TowerStunChance = null!; TowerSlowAmount = null!; TowerSlowDuration = null!;
            TowerArmorPierceRatio = null!; TowerSplashRadius = null!;
            TowerArmorShredBonus = null!; TowerShieldBreakBonus = null!; TowerAccuracy = null!;
            TowerFalloffInnerRatio = null!; TowerFalloffOuterMult = null!;
            TowerCritChance = null!; TowerCritMultiplier = null!;
            TowerHasChainLightning = null!; TowerHasFreezeAoe = null!;
            TowerCanHitAir = null!; TowerCanHitGround = null!;
            TowerSpecialAbilityRadius = null!; TowerSpecialAbilityDamageMult = null!;
            TowerSpecialAbilityDotDamage = null!; TowerSpecialAbilityDotInterval = null!;
            TowerKnockbackForce = null!; TowerKnockbackRadius = null!;
            TowerPathHugOnly = null!;
            TowerIsLockOn = null!; TowerLockedTargetId = null!;
            TowerRequiresLOS = null!; TowerBlocksLOS = null!;
            TowerIsPhasing = null!;
            TowerProjectileCount = null!; TowerScatterAngle = null!;
            TowerBouncesRemaining = null!; TowerBounceRange = null!;
            TowerBounceDamageFalloff = null!; TowerBounceHitsRemaining = null!;
            TowerProjectilePierceCount = null!; TowerProjectilePierceDmgFalloff = null!;
            TowerPierceHitsRemaining = null!;
            TowerProjectileFragmentCount = null!; TowerProjectileFragmentRange = null!;
            TowerProjectileFragmentDmgMult = null!;
            TowerCurrentAmmo = null!; TowerMaxAmmo = null!; TowerReloadTime = null!;
            TowerReloadProgress = null!; TowerIsReloading = null!;
            TowerIsOvercharged = null!; TowerOverchargeDuration = null!;
            TowerOverchargeCooldown = null!; TowerCanOvercharge = null!;
            TowerSynergyId = null!; TowerSynergyMultiplier = null!;
            TowerSynergyTier = null!;
            TowerIsChronoTower = null!; TowerTimeFieldRadius = null!; TowerTimeScale = null!;
            EnemyTimeScale = null!;
            TowerIsAuraTower = null!; TowerAuraRadius = null!;
            TowerAuraAttackSpeedBonus = null!; TowerAuraDamageBonus = null!;
            TowerIsSilenced = null!; TowerSilenceTimer = null!; TowerSilenceSourceId = null!;
            TowerIsDispelled = null!; TowerDispelTimer = null!; TowerDispelImmunityTimer = null!;
            TowerIsCurseTower = null!; TowerCurseRadius = null!;
            TowerCurseDmgReduction = null!; TowerCurseSpeedReduction = null!;
            TowerCurseArmorReduction = null!; TowerCurseDmgTakenIncrease = null!;
            TowerIsPullTower = null!; TowerPullStrength = null!; TowerPullRadius = null!;
            TowerIsBleedTower = null!; TowerBleedStacksPerHit = null!; TowerBleedDmgPct = null!;
            TowerBleedTickInterval = null!; TowerBleedMaxStacks = null!; TowerBleedDuration = null!;
            TowerIsIncomeTower = null!; TowerGoldPerSecond = null!;
            TowerIsConstructing = null!; TowerConstructionProgress = null!; TowerConstructionTime = null!;
            TowerConstructionHP = null!; TowerConstructionMaxHP = null!;
            TowerIsVulnerableDuringConstruction = null!;
            TowerDemolishEffectRadius = null!; TowerDemolishDamage = null!; TowerDemolishEffectType = null!;
            TowerIsMarkedForDemolish = null!;
            TowerDemolishDotDamage = null!; TowerDemolishDotDuration = null!; TowerDemolishDotInterval = null!;
            TowerDemolishStunDuration = null!;
            TowerLinkPartnerId = null!; TowerLinkComboType = null!;
            TowerLinkCooldown = null!; TowerLinkDamageBonus = null!;
            TowerFacingAngle = null!; TowerTurnRate = null!;
            TowerExperience = null!; TowerMasteryLevel = null!; TowerKillCount = null!;
            TowerIsMobile = null!; TowerMoveSpeed = null!; TowerPatrolPathId = null!;
            TowerPatrolWaypointIndex = null!; TowerPatrolDirection = null!;
            TowerPatrolAttackSpeedPenalty = null!;
            ObstacleActive = null!; ObstacleHealth = null!; ObstacleMaxHealth = null!;
            ObstacleX = null!; ObstacleY = null!; ObstacleType = null!;
            HazardZoneActive = null!; HazardZoneX = null!; HazardZoneY = null!;
            HazardZoneRadius = null!; HazardZoneMaxRadius = null!;
            HazardZoneType = null!; HazardZoneDuration = null!;
            HazardZoneDamagePerSec = null!; HazardZoneOwnerTowerId = null!;
            CorpseEffectActive = null!; CorpseEffectX = null!; CorpseEffectY = null!;
            CorpseEffectType = null!; CorpseEffectRadius = null!; CorpseEffectDuration = null!;
            CorpseEffectDamagePerTick = null!; CorpseEffectSlowAmount = null!;
            CorpseEffectTickTimer = null!; CorpseEffectTickInterval = null!;
            CorpseX = null!; CorpseY = null!; CorpseMonsterType = null!;
            CorpseOwnerId = null!; CorpseHealth = null!; CorpseDeathTime = null!;
            CorpseActive = null!; CorpseReanimated = null!;
            SkillName = null!; SkillDamageMultiplier = null!;
            SkillAreaWidth = null!; SkillAreaHeight = null!;
            SkillAttackRange = null!; SkillCooldown = null!; SkillCurrentCooldown = null!;
            AbilityInstances = null!; AbilityCount = null!;
            ActiveEffects = null!; ActiveEffectCount = null!;
            _enemyIndexInList = null!; _towerIndexInList = null!;
        }

        // IDisposable pattern — prevent double-dispose
        private bool _disposed = false;

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
        }
    }
}