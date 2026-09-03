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
        internal PhaseContext GameplayPhaseContext { get; set; } = PhaseContext.Unbound;
        // 同一 store 上的伤害与资源提交必须串行，避免跨 resolver 回滚已提交状态。
        internal object GameplayCommitLock { get; } = new object();
        #region Constants & Helpers
        public const int MAX_ENTITIES = 100000;

        // Round 103 — Buff Share cache invalidation hook.
        // When a tower entity is destroyed (or its slot recycled via AddTower),
        // TowerSynergySystem's per-frame base-attack-speed cache must drop the
        // corresponding entry to avoid restoring a stale base speed onto a
        // different tower that happens to land on the same entityId.
        // Systems holding per-entity caches subscribe via += / -=
        public static event Action<int> OnTowerEntityInvalidated;
        internal static void RaiseTowerEntityInvalidated(int entityId)
        {
            var h = OnTowerEntityInvalidated;
            if (h != null) h(entityId);
        }
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

        // Cross-frame identity. A recycled slot always receives a new generation.
        private readonly int[] _entityGenerations = new int[MAX_ENTITIES];
        public BattleSystemECS.Core.GAS.EntityHandle GetEntityHandle(int entityId)
        {
            if (!IsValidEntity(entityId) || _entityGenerations[entityId] == 0)
                return default(BattleSystemECS.Core.GAS.EntityHandle);
            return new BattleSystemECS.Core.GAS.EntityHandle(entityId, _entityGenerations[entityId]);
        }
        public bool TryResolve(BattleSystemECS.Core.GAS.EntityHandle handle, out int entityId, out BattleSystemECS.Core.GAS.HandleResolveFailure failure)
        {
            entityId = handle.Index;
            if (!handle.IsValid) { failure = BattleSystemECS.Core.GAS.HandleResolveFailure.InvalidIndex; return false; }
            if (!IsValidEntity(handle.Index)) { failure = BattleSystemECS.Core.GAS.HandleResolveFailure.InvalidIndex; return false; }
            if (_entityGenerations[handle.Index] != handle.Generation) { failure = BattleSystemECS.Core.GAS.HandleResolveFailure.StaleGeneration; return false; }
            if (!PositionActive[handle.Index] && !EnemyActive[handle.Index] && !TowerActive[handle.Index]) { failure = BattleSystemECS.Core.GAS.HandleResolveFailure.Inactive; return false; }
            if (EnemyActive[handle.Index] && IsEnemyPendingDeath(handle.Index)) { failure = BattleSystemECS.Core.GAS.HandleResolveFailure.Inactive; return false; }
            failure = BattleSystemECS.Core.GAS.HandleResolveFailure.None;
            return true;
        }
        public bool IsEnemyPendingDeath(int enemyId) => (uint)enemyId < MAX_ENTITIES && Volatile.Read(ref _enemyDeathPending[enemyId]) != 0;

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
        private readonly struct DeathEntry { public readonly int EnemyId, PlayerId, Generation; public readonly long Sequence; public readonly GAS.EntityHandle Source; public DeathEntry(int enemyId, int playerId, int generation, long sequence, GAS.EntityHandle source) { EnemyId = enemyId; PlayerId = playerId; Generation = generation; Sequence = sequence; Source = source; } }
        private ConcurrentBag<DeathEntry>[] _deathQueue = new ConcurrentBag<DeathEntry>[2];
        private int _deathQueueIdx = 0;

        // Tower kill queue: (enemyId, playerId, towerId) — parallel-safe
        private ConcurrentBag<(int, int, int)>[] _towerKillQueue = new ConcurrentBag<(int, int, int)>[2];
        private int _towerKillQueueIdx = 0;

        private bool _deathQueueResolved = false;
        private bool _deathResolveBlocked;
        private readonly int[] _enemyDeathPending = new int[MAX_ENTITIES];
        private long _deathEnqueueCount;
        private int _frameSequence;
        public long DeathEnqueueCount => Interlocked.Read(ref _deathEnqueueCount);
        public long DeathResolveCount { get; private set; }

        // Combo kill callback — fired once per killed enemy during ResolveEnemiesKilledThisFrame.
        // Safe for serial use only (called from the resolve loop inside a foreach).
        private readonly object _enemyKilledSubscriberSync = new object();
        private Action<int, int> _onEnemyKilled;
        private Action<int, int>[] _enemyKilledSubscribers = Array.Empty<Action<int, int>>();
        public event Action<int, int> OnEnemyKilled
        {
            add { lock (_enemyKilledSubscriberSync) { _onEnemyKilled += value; RebuildEnemyKilledSubscribers(); } }
            remove { lock (_enemyKilledSubscriberSync) { _onEnemyKilled -= value; RebuildEnemyKilledSubscribers(); } }
        }
        internal int EnemyKilledSubscriberCount => _enemyKilledSubscribers.Length;
        // Tower kill callback — fired when a tower scores the killing blow.
        // Parameters: (enemyId, playerId, towerId). Thread-safe, serial context.
        private readonly object _towerKillSubscriberSync = new object();
        private Action<int, int, int> _onTowerKill;
        private Action<int, int, int>[] _towerKillSubscribers = Array.Empty<Action<int, int, int>>();
        public event Action<int, int, int> OnTowerKill
        {
            add { lock (_towerKillSubscriberSync) { _onTowerKill += value; RebuildTowerKillSubscribers(); } }
            remove { lock (_towerKillSubscriberSync) { _onTowerKill -= value; RebuildTowerKillSubscribers(); } }
        }
        internal int TowerKillSubscriberCount => _towerKillSubscribers.Length;

        private void RebuildEnemyKilledSubscribers()
        {
            if (_onEnemyKilled == null) { Volatile.Write(ref _enemyKilledSubscribers, Array.Empty<Action<int, int>>()); return; }
            Delegate[] invocation = _onEnemyKilled.GetInvocationList();
            var subscribers = new Action<int, int>[invocation.Length];
            for (int i = 0; i < invocation.Length; i++) subscribers[i] = (Action<int, int>)invocation[i];
            Volatile.Write(ref _enemyKilledSubscribers, subscribers);
        }

        private void RebuildTowerKillSubscribers()
        {
            if (_onTowerKill == null) { Volatile.Write(ref _towerKillSubscribers, Array.Empty<Action<int, int, int>>()); return; }
            Delegate[] invocation = _onTowerKill.GetInvocationList();
            var subscribers = new Action<int, int, int>[invocation.Length];
            for (int i = 0; i < invocation.Length; i++) subscribers[i] = (Action<int, int, int>)invocation[i];
            Volatile.Write(ref _towerKillSubscribers, subscribers);
        }

        internal void NotifyTowerKillSubscribers(int enemyId, int playerId, int towerId)
        {
            Exception callbackFailure = null;
            Action<int, int, int>[] subscribers = Volatile.Read(ref _towerKillSubscribers);
            for (int i = 0; i < subscribers.Length; i++)
            {
                try { subscribers[i](enemyId, playerId, towerId); }
                catch (Exception ex) { if (callbackFailure == null) callbackFailure = ex; }
            }
            if (callbackFailure != null) throw callbackFailure;
        }

        public void BeginFrame()
        {
            if (_deathResolveBlocked)
            {
                // Keep the blocked death batch in place; only clear transient facts
                // before retrying its required KillConfirmed publication.
                _deathResolveBlocked = false;
                DamageResolver.Events.Clear();
                ResourceResolver.Events.Clear();
                DamageResolver.BeginFrame();
                ResourceResolver.BeginFrame();
                return;
            }
            // 检测帧生命周期错误：上一帧未完成死亡结算就再次 BeginFrame。
            if (!_deathQueue[_deathQueueIdx].IsEmpty && !_deathQueueResolved)
            {
                throw new InvalidOperationException(
                    "BeginFrame() called but ResolveEnemiesKilledThisFrame() was not called " +
                    "for the previous frame. Deaths may have been discarded.");
            }
            // A callback may have queued a cascade into the current write bag after the
            // prior batch committed. Preserve that bag across BeginFrame for retry.
            if (_deathQueue[_deathQueueIdx].IsEmpty)
            {
                _deathQueueIdx = 1 - _deathQueueIdx;
                _deathQueue[_deathQueueIdx].Clear();
            }
            _deathQueueResolved = false;
            DamageResolver.Events.Clear();
            ResourceResolver.Events.Clear();
            DamageResolver.BeginFrame();
            DamageResolver.ResetDiagnostics();
            ResourceResolver.BeginFrame();
            // Shield-break queue is per-frame by contract: ApplyEnemyDamage appends on every
            // elemental-shield break, and the drain lives in ElementalReactionSystem — which is
            // never constructed (no `new ElementalReactionSystem(` anywhere), so nothing ever
            // called its Clear(). Shipped data reaches this path (monster_shield.json and
            // monster_enforcer.json carry Shield + ShieldElement, wired at
            // WaveSpawningSystem:993-1001), so the list grew without bound for the whole
            // session. Clearing at frame start keeps the documented per-frame semantics: a
            // consumer wired later still sees everything appended during its own frame.
            _pendingShieldBreaks.Clear();
            // Round 171 Direction 4 — reset curse debuff accumulators so that curse auras
            // (CurseAuraSystem +=) and BlightedGround (CorpseEffectSystem +=) both build
            // up fresh each frame. Without this reset, repeated += calls would compound
            // frame-over-frame, making the debuffs grow unboundedly.
            // The cost is O(MAX_ENTITIES) = 100K float writes × 4 fields = 400K writes/frame.
            // For 10K enemies and a hot benchmark, this is sub-millisecond on modern CPUs.
            // Only iterate active enemies to avoid touching 90K dead slots (perf budget).
            var activeEnemies = _activeEnemyIds;
            for (int i = 0; i < activeEnemies.Count; i++)
            {
                int eid = activeEnemies[i];
                EnemyCurseDmgReduction[eid] = 0f;
                EnemyCurseSpeedReduction[eid] = 0f;
                EnemyCurseArmorReduction[eid] = 0f;
                EnemyCurseDmgTakenIncrease[eid] = 0f;
            }
            // Round 173 Direction 1 — reset Shrine "this frame" cache arrays on every
            // active tower. TowerShrineSystem then += accumulates into these during the
            // combat phase; the next frame's BeginFrame() wipes them so downstream
            // consumers always see "this frame's contribution" (no drift). O(active_towers)
            // worst case × 4 fields = 800 writes/frame for the 200-tower cap.
            var activeTowers = _activeTowerIds;
            for (int i = 0; i < activeTowers.Count; i++)
            {
                int tid = activeTowers[i];
                TowerShrineCachedGoldBonus[tid] = 0f;
                TowerShrineCachedManaRegen[tid] = 0f;
                TowerShrineCachedDmgBonus[tid] = 0f;
                TowerShrineCachedAtkSpdBonus[tid] = 0f;
                // Round 177 Direction 2 — Beacon per-frame damage/atk-spd bonus reset.
                // TowerBeaconSystem then += accumulates into these during the combat
                // phase; the next frame's BeginFrame() wipes them so downstream consumers
                // always see "this frame's contribution" (no drift). O(active_towers) × 2
                // fields = 400 writes/frame for the 200-tower cap.
                TowerBeaconCachedDmgBonus[tid] = 0f;
                TowerBeaconCachedAtkSpdBonus[tid] = 0f;
                // Round 175 Direction 9 — Smokescreen per-frame miss chance reset.
                // CorpseEffectSystem.ApplyContinuousEffect runs each frame for active
                // smokescreen zones and writes the miss chance into this array. Without
                // a per-frame wipe the value would carry over (so a tower that left the
                // smoke would keep missing). Note: this is O(active_towers) per frame;
                // for the 200-tower cap this is 200 writes/frame — negligible.
                TowerSmokeMissChance[tid] = 0f;
                // Round 183 Direction 8 — Scorched Earth per-frame vision reduction reset.
                // CorpseEffectSystem writes max(zone.VisionReduction, existing) into this
                // array each frame for active ScorchedEarth zones. Without the per-frame
                // wipe, a tower that walked out of the fire would keep its range penalty
                // for the rest of the game. Same O(active_towers) cost as smoke.
                TowerVisionReduction[tid] = 0f;
                // Round 186 Direction 2 — Sapper per-frame slow multiplier reset.
                // SapperSystem runs in the AI phase and writes the cumulative atk-spd
                // slow from all active sappers targeting this tower into this array. The
                // next frame's BeginFrame() wipes the value to 0 so the SapperSystem
                // must re-derive the current slow from the live sapper set (no drift if
                // a sapper dies, target retargets, or the slow is otherwise cancelled).
                // O(active_towers) per frame; for the 200-tower cap this is one extra
                // write per tower per frame — negligible.
                TowerSapperSlowMult[tid] = 0f;
                // Round 187 Direction 4 — Rally per-frame atk-spd bonus reset.
                // RallySystem runs in SkillBuffGroup and writes the additive atk-spd
                // bonus from the active PlayerRallyActive players into this array. The
                // next frame's BeginFrame() wipes the value to 0 so RallySystem must
                // re-derive the current rally bonus from the live player set (no drift
                // if a rally expires or is overridden). O(active_towers) per frame; for
                // the 200-tower cap this is one extra write per tower per frame — same
                // negligible cost as Sapper/Smokescreen resets above.
                TowerRallyAtkSpdBonus[tid] = 0f;
            }
            CurrentFrame++;
            _frameSequence = 0;
        }

        /// <summary>
        /// Queue an enemy death from a parallel context. Thread-safe.
        /// Must be matched with a later call to ResolveEnemiesKilledThisFrame().
        /// </summary>
        public void QueueEnemyDeath(int enemyId, int playerId, long sequence = 0L, GAS.EntityHandle source = default(GAS.EntityHandle))
        {
            // H-11 fix: validate IDs are within valid range before queueing
            if (!IsValidEntity(enemyId)) return;
            if (!IsValidPlayer(playerId)) return;
            if (Interlocked.CompareExchange(ref _enemyDeathPending[enemyId], 1, 0) != 0) return;
            Interlocked.Increment(ref _deathEnqueueCount);
            _deathQueue[_deathQueueIdx].Add(new DeathEntry(enemyId, playerId, GetEntityHandle(enemyId).Generation, sequence, source));
        }

        internal long AllocateGameplaySequence(int targetId)
        {
            int ordinal = Interlocked.Increment(ref _frameSequence);
            return ((long)CurrentFrame << 32) | (uint)ordinal;
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
            Exception callbackFailure = null;
            foreach (var (enemyId, playerId, towerId) in _towerKillQueue[readIdx])
            {
                try { NotifyTowerKillSubscribers(enemyId, playerId, towerId); }
                catch (Exception ex) { if (callbackFailure == null) callbackFailure = ex; }
            }
            _towerKillQueue[readIdx].Clear();
            if (callbackFailure != null) throw callbackFailure;
        }

        /// <summary>
        /// Serially process all queued enemy deaths this frame.
        /// Call once per turn AFTER all parallel systems have run.
        /// </summary>
        public void ResolveEnemiesKilledThisFrame()
        {
            PrepareEnemiesKilledThisFrame();
            DispatchPreparedEnemyDeaths();
        }

        private int CountPendingDeathResourceFacts()
        {
            int count = 0;
            foreach (var entry in _deathQueue[_deathQueueIdx])
            {
                int enemyId = entry.EnemyId;
                if (!IsValidEntity(enemyId) || !EnemyActive[enemyId] || GetEntityHandle(enemyId).Generation != entry.Generation) continue;
                int playerId = entry.PlayerId;
                float gold = (EnemyHasStolenGold[enemyId] ? EnemyGoldOnReturn[enemyId] : EnemyGoldReward[enemyId]) * _goldKillMultiplier * _allIncomeMultKill * PlayerComboGoldMult[playerId];
                if (EnemyIsBounty[enemyId]) gold *= EnemyBountyGoldMult[enemyId];
                int kills = PlayerWaveKillCount[playerId];
                gold *= Math.Max(_waveGoldDecayFloor, 1f - kills * _waveGoldDecayRate);
                if (gold != 0f) count++;
                if (_goldOnEliteKill > 0f && EnemyIsElite[enemyId]) count++;
                if (EnemyMarked[enemyId] && gold * EnemyMarkedDamageBonus[enemyId] != 0f) count++;
                if (!EnemyExecuted[enemyId] && EnemyExecuteThreshold[enemyId] > 0f)
                {
                    if (EnemyExecuteBonusGold[enemyId] > 0f) count++;
                    if (EnemyExecuteBonusMana[enemyId] > 0f) count++;
                }
            }
            return count;
        }

        private int _preparedDeathReadIdx=-1;
        private int _preparedDeathWriteIdx=-1;
        private GameplayEventQueue.GameplayEventReservation _deathReservation;

        internal void PrepareEnemiesKilledThisFrame()
        {
            if(_preparedDeathReadIdx>=0)throw new InvalidOperationException("Prepared death callbacks must be dispatched before preparing another batch.");
            int pendingDeaths = CountPendingDeathKillFacts();
            if (pendingDeaths > 0)
            {
                _deathReservation = GameplayEventQueue.TryReserveAtomic(DamageResolver.Events, pendingDeaths,
                    ResourceResolver.Events, CountPendingDeathResourceFacts());
                if (_deathReservation == null)
                {
                    DamageResolver.MarkEventPublicationFailure(true);
                    _deathResolveBlocked = true;
                    return;
                }
            }
            _preparedDeathReadIdx=_deathQueueIdx;
            _preparedDeathWriteIdx=1-_deathQueueIdx;
            _deathQueueIdx=_preparedDeathWriteIdx;
        }

        private int CountPendingDeathKillFacts()
        {
            int count = 0;
            foreach (var entry in _deathQueue[_deathQueueIdx])
                if (IsValidEntity(entry.EnemyId) && EnemyActive[entry.EnemyId] &&
                    GetEntityHandle(entry.EnemyId).Generation == entry.Generation) count++;
            return count;
        }

        internal void DispatchPreparedEnemyDeaths()
        {
            if(_preparedDeathReadIdx<0)
            {
                if (_deathResolveBlocked) return;
                throw new InvalidOperationException("Death callbacks require a prepared death batch.");
            }
            int readIdx=_preparedDeathReadIdx;
            int writeIdx=_preparedDeathWriteIdx;
            foreach (var entry in _deathQueue[readIdx])
            {
                int enemyId = entry.EnemyId; int playerId = entry.PlayerId;
                if (GetEntityHandle(enemyId).Generation != entry.Generation) continue;
                Volatile.Write(ref _enemyDeathPending[enemyId], 0);
                if (!EnemyActive[enemyId]) continue; // already destroyed this frame
                EntityHandle oldTarget = new EntityHandle(enemyId, entry.Generation);
                EntityHandle oldSource = entry.Source.IsValid ? entry.Source : GetEntityHandle(playerId);
                TotalKills++;
                DeathResolveCount++;

                // Gold reward logic:
                // - Thief that escaped (HasStolenGold): no gold reward, but if killed later -> GoldOnReturn bonus
                // - Thief killed before escaping: normal gold reward (IsThief but HasStolenGold=false)
                // - Normal enemy: normal gold reward
                float goldReward;
                long killSequence = entry.Sequence == 0L ? ((long)CurrentFrame << 32) | (uint)enemyId : entry.Sequence;
                void AddLifecycleGold(float amount)
                {
                    if (amount != 0f) ResourceResolver.StageLifecycleGold(playerId, amount, oldSource, killSequence, playerId, _deathReservation);
                }
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
                // Bounty enemies (IsBounty=true) pay EnemyBountyGoldMult × base reward. Multiplied
                // on top of _goldKillMultiplier / _allIncomeMultKill / PlayerComboGoldMult (commutative
                // with the post-multiplier decay / elite / mark / execute bonuses that follow). The
                // multiplier is the entire value proposition. Non-bounty enemies pay only a
                // one-bool read + branch (inert fast path).
                if (EnemyIsBounty[enemyId])
                {
                    goldReward *= EnemyBountyGoldMult[enemyId];
                }
                // ── Decaying Wave Bounty: subsequent kills in the same wave pay less. ──
                // Formula: mult = max(DecayFloor, 1.0 - kills * DecayRate)
                // DecayRate=0.02 → 5 kills = 90%, 10 = 80%, 20 = 60%, floor at 0.3 (30%) after 35 kills.
                // Counts kill BEFORE the decay is applied so the first kill pays 100% gold.
                int killsThisWave = PlayerWaveKillCount[playerId];
                float decayMult = Math.Max(_waveGoldDecayFloor, 1.0f - killsThisWave * _waveGoldDecayRate);
                goldReward *= decayMult;
                AddLifecycleGold(goldReward);
                // Bump per-player kill counter AFTER the gold has been calculated and awarded.
                // Capped at int.MaxValue-1 to avoid overflow on absurd kill counts (e.g. long benchmarks).
                if (PlayerWaveKillCount[playerId] < int.MaxValue - 1)
                {
                    PlayerWaveKillCount[playerId]++;
                }
                if (_goldOnEliteKill > 0f && EnemyIsElite[enemyId])
                    AddLifecycleGold(_goldOnEliteKill);
                // Death Mark / Execute bonus gold: +50% extra gold for executing a marked enemy.
                // Self-balancing — only triggers once per enemy (on death), so no chain exploits.
                if (EnemyMarked[enemyId])
                {
                    float markBonus = goldReward * EnemyMarkedDamageBonus[enemyId];
                    AddLifecycleGold(markBonus);
                }
                // ── Execute bonus (Round 105 Direction 8) ──────────────────────────
                // Per-enemy HP-fraction threshold (EnemyExecuteThreshold > 0) opts the enemy in
                // to the execute-finisher economy: when killed, pay a flat gold + mana bonus to
                // the player. The EnemyExecuted one-shot guard ensures re-marks / re-checks
                // never double-pay. Stacks with the Death Mark bonus above (both apply).
                // Default EnemyExecuteThreshold = 0 → opt-out, backward compatible.
                if (!EnemyExecuted[enemyId] && EnemyExecuteThreshold[enemyId] > 0f)
                {
                    float execGold = EnemyExecuteBonusGold[enemyId];
                    if (execGold > 0f)
                    {
                        AddLifecycleGold(execGold);
                    }
                    float execMana = EnemyExecuteBonusMana[enemyId];
                    if (execMana > 0f)
                    {
                        // Delegate to SetPlayerMana for the clamp — matches the codebase
                        // pattern used everywhere else for safe mana writes. Note that when
                        // PlayerMaxMana is 0 (uninitialized player), SetPlayerMana clamps to 0;
                        // this is the established convention across the codebase.
                        ResourceResolver.StageLifecycleMana(playerId, execMana, oldSource, killSequence, playerId, _deathReservation);
                    }
                    EnemyExecuted[enemyId] = true;
                }
                // Stage required facts in the reservation. They remain invisible until
                // callbacks and entity destruction complete for the entire batch.
                _deathReservation.StageFirst(new GameplayEvent(
                    GameplayEventType.KillConfirmed, oldSource, oldTarget,
                    killSequence, ownerPlayerId: playerId));
            }
            // All required facts for the batch now exist. Public callbacks may re-enter
            // producers, but cannot starve a later death in this same commit.
            Exception callbackFailure = null;
            foreach (var entry in _deathQueue[readIdx])
            {
                int enemyId = entry.EnemyId;
                if (GetEntityHandle(enemyId).Generation != entry.Generation || !EnemyActive[enemyId]) continue;
                Action<int, int>[] subscribers = Volatile.Read(ref _enemyKilledSubscribers);
                for (int i = 0; i < subscribers.Length; i++)
                {
                    try { subscribers[i](enemyId, entry.PlayerId); }
                    catch (Exception ex) { if (callbackFailure == null) callbackFailure = ex; }
                }
                try { ResolveTowerKillsThisFrame(); }
                catch (Exception ex) { if (callbackFailure == null) callbackFailure = ex; }
                DestroyEntity(enemyId);
            }
            _deathReservation?.Commit();
            _deathReservation?.Dispose();
            _deathReservation = null;
            _deathQueue[readIdx].Clear();
            _deathQueueResolved = true;
            _preparedDeathReadIdx=-1;
            _preparedDeathWriteIdx=-1;
            if (callbackFailure != null) throw callbackFailure;
        }

        public ComponentStore()
        {
            ResourceResolver = new GAS.ResourceResolver(this);
            DamageResolver = new GAS.DamageResolver(this);
            GameplayEffectsRuntime = new GAS.GameplayEffectRuntime(this);
            GameplayTriggersRuntime = new GAS.GameplayTriggerRuntime(this, GameplayEffectsRuntime);
            // Initialize ping-pong death queue buffers
            _deathQueue[0] = new ConcurrentBag<DeathEntry>();
            _deathQueue[1] = new ConcurrentBag<DeathEntry>();
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
            // Round 111 Direction 1 — initialize per-(phase,enemy) speed/damage multipliers
            // to 1f (no change). The default 0f would be a "neutral" sentinel but explicitly
            // setting 1f makes the values queryable from the very first frame without any
            // special-casing. Threshold defaults to 0f (no trigger) and fired mask to 0.
            for (int ph = 0; ph < BOSS_PHASE_MAX; ph++)
            {
                int baseIdx = ph * MAX_ENTITIES;
                for (int i = 0; i < MAX_ENTITIES; i++)
                {
                    EnemyPhaseSpeedMults[baseIdx + i] = 1f;
                    EnemyPhaseDamageMults[baseIdx + i] = 1f;
                    // Round 119 Dir 3 — initialise minion-summon defaults across the whole SOA
                    // range. -1 = "no minion type set" and 0 = "no count set" — both sentinel
                    // values that the per-(phase,enemy) SetEnemyPhaseMinion() will overwrite
                    // when a boss actually declares a minion in its MonsterConfig.
                    EnemyPhaseMinionTypeIdFlat[baseIdx + i] = -1;
                    EnemyPhaseMinionCountsFlat[baseIdx + i] = 0;
                    // Round 137 Dir 6 — initialise per-(phase,enemy) boss element affinity to
                    // 0 (None). SetEnemyPhaseElementAffinity() will overwrite on boss spawn.
                    EnemyPhaseElementAffinityFlat[baseIdx + i] = 0;
                }
            }
            // Initialize player buffs
            for (int i = 0; i < MAX_PLAYERS; i++)
            {
                PlayerDamageType[i] = DamageType.Physical;
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
                    _entityGenerations[entityId] = NextGeneration(_entityGenerations[entityId]);
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
            _entityGenerations[entityId2] = 1;
            EnemyActionEnum[entityId2] = EnemyActionType.None;
            // Newly allocated IDs start with default float[] = 0f; set to 1f so that
            // EnemyAISystem attack methods multiply correctly (stealth_mult=1f means no bonus).
            EnemyStealthMultiplier[entityId2] = 1f;
            return entityId2;
        }

        private static int NextGeneration(int current)
        {
            return current == int.MaxValue || current <= 0 ? 1 : current + 1;
        }

        public void DestroyEntity(int entityId)
        {
            if (!IsValidEntity(entityId)) return;
            Volatile.Write(ref _enemyDeathPending[entityId], 0);
            // Path modifiers share entity slots; remove the index entry before recycling an active slot.
            if (PathModifierActive[entityId]) DeactivatePathModifier(entityId);
            ClearComputedAttributes(entityId);
            // ── Phase 1: determine archetype ────────────────────────────────────────
            bool wasEnemy = EnemyActive[entityId];
            bool wasTower = TowerActive[entityId];
            // Round 100 — capture whether this tower was a palisade BEFORE reset, so we can
            // decrement ActivePalisadeCount when the entity is destroyed.
            bool wasPalisade = wasTower && TowerIsPalisade[entityId];
            // Round 139 — Per-Type Placement Cap: snapshot the tower type BEFORE reset.
            // (TowerType[entityId] is zeroed in the recycle block below; the per-type counter
            // decrement in phase 4 needs the original value.)
            int rtType = wasTower ? (int)TowerType[entityId] : -1;

            // ── Phase 2: shared state cleanup ─────────────────────────────────────
            // GAS slot counts must be zeroed here (not only in ResetPlayerAbilities): entity
            // IDs are recycled through freeEntityIds, so a non-zero count would let the next
            // occupant of this ID inherit the previous one's active effects (SourceEntityId
            // included). The live path is BuffSystem: it ticks enemy DoT driven by
            // GetEffectCount, and ApplyDot is reached in production from TowerAttackSystem's
            // Firewall branch. Zeroing the count is sufficient — slot contents are never read
            // past the count.
            // Note: the ActiveEffectCount half is what actually fires; the AbilityCount half is
            // defense-in-depth, since AddAbility's only production caller targets playerId and
            // player ids (0..MAX_PLAYERS) never enter freeEntityIds.
            GameplayEffectsRuntime.CleanupEntity(entityId);
            GameplayTriggersRuntime.CleanupEntity(entityId);
            RemoveAllGameplayEffects(entityId);
            PositionActive[entityId] = false;
            AbilityCount[entityId] = 0;
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
                EnemyAttackInterval[entityId] = 0f;
                EnemyAttackCooldownLeft[entityId] = 0f;
                EnemyArmor[entityId] = 0f;
                EnemyStunFlag[entityId] = false;
                EnemyStunDurationLeft[entityId] = 0f;
                // Root CC (Round 136 Direction 2): reset on entity destroy to avoid ID-reuse leakage
                EnemyRootDurationLeft[entityId] = 0f;
                // CC Immunity (Round 97): reset mask on entity destroy to avoid ID-reuse leakage
                EnemyCCImmuneMask[entityId] = 0;
                // Mana Pool (Round 101 Direction 10): reset both fields to avoid ID-reuse leakage
                // (an ID recycled from a high-mana boss to a no-mana peon would otherwise carry stale mana)
                EnemyMaxMana[entityId] = 0f;
                EnemyCurrentMana[entityId] = 0f;
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
                // Elemental status reset (recycled slot must not leak element bits/timers).
                // These two are written in production by ApplyEnemyDamage's shield-break path
                // and by TowerAttackSystem's enchant path, but their only decay/clear logic
                // lives in ElementalReactionSystem — which is never constructed. Without a
                // reset here, a recycled id keeps the previous occupant's element bits forever,
                // and TowerAttackSystem's Elemental Affinity bonus (a live reader) then grants
                // an undeserved damage multiplier against the new enemy. 4 timer slots/enemy.
                EnemyElementStatus[entityId] = ElementType.None;
                int elemBase = entityId * 4;
                EnemyElementTimer[elemBase] = 0f;
                EnemyElementTimer[elemBase + 1] = 0f;
                EnemyElementTimer[elemBase + 2] = 0f;
                EnemyElementTimer[elemBase + 3] = 0f;
                EnemyThornsRatio[entityId] = 0f;
                // Round 176 Direction 7 — Siege reset (recycled slot must not leak siege
                // armor/slow state — a freshly-spawned enemy must start as a normal enemy,
                // never inherit +80% damage reduction / 50% slow from a prior slot occupant)
                EnemyIsSiege[entityId] = false;
                EnemySiegeArmorBonus[entityId] = 0f;
                EnemySiegeSpeedMult[entityId] = 1f;
                // Round 174 Direction 8 — Stalker reset (recycled slot must not leak stealth
                // state — a freshly-spawned enemy must start hidden + fresh ambush, never
                // inherit a revealed/ambush-consumed state from the prior slot occupant)
                EnemyIsStalker[entityId] = false;
                EnemyStalkRevealed[entityId] = false;
                EnemyStalkRevealRadius[entityId] = 0f;
                EnemyStalkAmbushMult[entityId] = 1f;
                EnemyStalkConsumed[entityId] = false;
                // Round 181 Direction 9 — Phaser reset (recycled slot must not leak phase
                // state — a freshly-spawned enemy must start in the vulnerable gap, never
                // inherit an active phase window or advanced cycle timer from the prior
                // slot occupant)
                EnemyIsPhaser[entityId] = false;
                EnemyPhaserInterval[entityId] = 0f;
                EnemyPhaserDurationLeft[entityId] = 0f;
                EnemyPhaserPhaseActive[entityId] = false;
                EnemyPhaserCycleTimer[entityId] = 0f;
                EnemyPhaserPhaseDuration[entityId] = 0f;
                // Round 182 Direction 6 — Blinker reset (recycled slot must not leak blink
                // state — a freshly-spawned enemy must start in the between-blinks gap,
                // never inherit an advanced timer or post-blink i-frames from the prior
                // slot occupant)
                EnemyIsBlinker[entityId] = false;
                EnemyBlinkInterval[entityId] = 0f;
                EnemyBlinkTimer[entityId] = 0f;
                EnemyBlinkDistance[entityId] = 0f;
                EnemyBlinkIFramesLeft[entityId] = 0f;
                // Round 186 Direction 2 — Sapper reset (recycled slot must not leak sapper
                // state — a freshly-spawned enemy must start as a non-sapper, never inherit
                // a target tower id or accumulated slow stacks from the prior slot occupant)
                EnemyIsSapper[entityId] = false;
                EnemySapperTargetTowerId[entityId] = -1;
                EnemySapperAttackTimer[entityId] = 0f;
                EnemySapperDamage[entityId] = 0f;
                EnemySapperAttackInterval[entityId] = 0f;
                EnemySapperAtkSpdSlow[entityId] = 0f;
                EnemySapperAtkSpdSlowPerStack[entityId] = 0f;
                EnemySapperMaxSlowStacks[entityId] = 0;
                EnemySapperRange[entityId] = 0f;
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
                // Round 124 Dir 1 — Boss Path Trail AoE: default = no trail (WaveSpawningSystem
                // overrides per archetype if the monster config specifies BossTrail* fields).
                EnemyIsBossTrail[entityId] = false;
                EnemyBossTrailRadius[entityId] = 0f;
                EnemyBossTrailDamage[entityId] = 0f;
                EnemyBossTrailSlow[entityId] = 0f;
                EnemyBossTrailProgressInterval[entityId] = 0f;
                EnemyBossTrailLastTriggerProgress[entityId] = 0f;
                EnemyPathSegmentStartIndex[entityId] = 0;
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
                // Elemental Resistance (Round 117): reset to 0 on destroy to prevent ID-reuse leakage
                EnemyFireResist[entityId] = 0f;
                EnemyIceResist[entityId] = 0f;
                EnemyLightningResist[entityId] = 0f;
                EnemyIsUnstoppable[entityId] = false;
                // I-frames (Round 118): reset remaining invuln frames and per-monster config
                EnemyInvulnFramesLeft[entityId] = 0;
                EnemyInvulnOnHitFrames[entityId] = 0;
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
                // Round 115 — Summon Circle: default to (0,0,0) = no circle (fast path)
                EnemyInSummonCircleX[entityId] = 0f;
                EnemyInSummonCircleY[entityId] = 0f;
                EnemyInSummonCircleRadius[entityId] = 0f;
                // Bleed/rupture debuff fields (applied by Slash/Pierce towers)
                EnemyBleedStacks[entityId] = 0f;
                EnemyBleedDamagePerStack[entityId] = 0f;
                EnemyBleedTimer[entityId] = 0f;
                EnemyBleedMaxStacks[entityId] = 0f;
                EnemyBleedResistance[entityId] = 0f;
                EnemyBleedDurationLeft[entityId] = 0f;
                // Round 170 Direction 6 — Frostbite (non-stacking %-maxHP DoT)
                EnemyFrostbiteMaxHpPct[entityId] = 0f;
                EnemyFrostbiteDurationLeft[entityId] = 0f;
                EnemyFrostbiteTimer[entityId] = 0f;
                EnemyFrostbiteResistance[entityId] = 0f;
                // Boss phase / enrage fields
                EnemyBossPhase[entityId] = 0;
                EnemyPhaseThresholds[entityId] = null;
                EnemyEnrageTimer[entityId] = 0f;
                EnemyIsEnraged[entityId] = false;
                // Round 111 Direction 1 — Boss phase structured fields (speed/damage/fired mask)
                EnemyPhaseCount[entityId] = 0;
                EnemyPhaseFiredMask[entityId] = 0;
                for (int ph = 0; ph < BOSS_PHASE_MAX; ph++)
                {
                    EnemyPhaseAbilityIdsFlat[ph, entityId] = null;
                }
                for (int ph = 0; ph < BOSS_PHASE_MAX; ph++)
                {
                    int idx = ph * MAX_ENTITIES + entityId;
                    EnemyPhaseThresholdsFlat[idx] = 0f;
                    EnemyPhaseSpeedMults[idx] = 1f;
                    EnemyPhaseDamageMults[idx] = 1f;
                    // Round 119 Dir 3 — reset per-phase minion summon fields. typeId -1 / count 0
                    // is the canonical "no summon" state; a recycled enemyId would otherwise carry
                    // the previous boss's summon config into a freshly-spawned unit.
                    EnemyPhaseMinionTypeIdFlat[idx] = -1;
                    EnemyPhaseMinionCountsFlat[idx] = 0;
                    // Round 137 Dir 6 — reset per-phase boss element affinity. 0 = None = no
                    // themed bonus; a recycled enemyId would otherwise carry the previous
                    // boss's element affinity into a freshly-spawned unit.
                    EnemyPhaseElementAffinityFlat[idx] = 0;
                }
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
                // Execute bonus (Round 105 Direction 8): reset to opt-out defaults on entity destroy
                // to prevent ID-reuse leakage (a recycled ID carrying stale threshold/bonus/flag).
                EnemyExecuteThreshold[entityId] = 0f;
                EnemyExecuteBonusGold[entityId] = 0f;
                EnemyExecuteBonusMana[entityId] = 0f;
                EnemyExecuted[entityId] = false;
                // Round 132 Dir 8 — Execute Immunity: reset on destroy to prevent ID-reuse leakage
                // (a recycled ID carrying stale Boss-floor / execute-immune flags).
                EnemyMinHealthFloor[entityId] = 0f;
                EnemyExecuteImmune[entityId] = false;
                // Round 107 Direction 6 — Target Mark: reset on entity destroy to prevent
                // ID-reuse leakage (a recycled ID carrying stale stacks/decay-timer/threshold).
                EnemyMarkStacks[entityId] = 0;
                EnemyMarkDecayTimer[entityId] = 0f;
                EnemyMarkMaxThreshold[entityId] = 0;
                // Round 200 Direction 5 — Death Mark: reset on entity destroy to prevent
                // ID-reuse leakage (a recycled ID carrying stale stacks/timer/maxStacks/bonus).
                EnemyDeathMarkStacks[entityId] = 0;
                EnemyDeathMarkTimer[entityId] = 0f;
                EnemyDeathMarkMaxStacks[entityId] = 0;
                EnemyDeathMarkBonusPerStack[entityId] = 0f;
                // Direction 2 — Elemental Terrain Zone: reset on entity destroy to prevent ID-reuse
                // leakage (a recycled ID carrying stale elemental stacks / aggregate slow+DPS).
                EnemyTerrainZoneFireStacks[entityId] = 0;
                EnemyTerrainZoneIceStacks[entityId] = 0;
                EnemyTerrainZoneToxicStacks[entityId] = 0;
                EnemyTerrainZoneHolyStacks[entityId] = 0;
                EnemyTerrainZoneSlowTotal[entityId] = 0f;
                EnemyTerrainZoneDpsTotal[entityId] = 0f;
                EnemyInTerrainZone[entityId] = 0;
                // Round 142 方向5 — Aggro / Focus Fire: reset on entity destroy to prevent
                // ID-reuse leakage (a recycled ID carrying stale focus tower id / duration).
                EnemyFocusTowerId[entityId] = -1;
                EnemyFocusDurationLeft[entityId] = 0f;
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
                // Round 139 — Per-Type Placement Cap: the type snapshot was already taken
                // in phase 1 (rtType) before this reset block runs.
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
                // Round 124 — Disarm CC fields reset (recycled slot must start with no disarm)
                TowerDisarmChance[entityId] = 0f;
                TowerDisarmDuration[entityId] = 0f;
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
                // Chrono / patrol / selection fields. These are the only 10 of the 150 fields
                // that RemoveTower clears and this branch did not, which AddTower also does not
                // re-initialize — i.e. the entire genuine ID-reuse surface on the tower side
                // (the other 140 are re-initialized by AddTower, so clearing them here would be
                // 140 wasted writes per tower destruction; do NOT delegate to RemoveTower).
                // Defense-in-depth today: their writers in TowerPlacementSystem are gated on
                // tc.IsChronoTower / tc.IsMobile, and no shipped Data/Towers/*.json sets either
                // key, so they are never written non-default in production yet. Clearing them
                // now means wiring a chrono or patrol tower later cannot resurrect stale state
                // on a recycled slot. TowerSelected's SelectTower/DeselectTower likewise have
                // no production callers.
                TowerIsChronoTower[entityId] = false;
                TowerTimeFieldRadius[entityId] = 0f;
                TowerTimeScale[entityId] = 0f;
                TowerIsMobile[entityId] = false;
                TowerMoveSpeed[entityId] = 0f;
                TowerPatrolPathId[entityId] = -1;
                TowerPatrolWaypointIndex[entityId] = 0;
                TowerPatrolDirection[entityId] = 1;
                // 1f, not 0f: this is a multiplier on attack speed (TowerPlacementSystem:583
                // defaults it to 0.75 = 75% speed for patrol towers). Zeroing it would leave a
                // recycled slot unable to attack. Matches RemoveTower's value.
                TowerPatrolAttackSpeedPenalty[entityId] = 1f;
                TowerSelected[entityId] = false;
                // Round 98 — Windup fields reset (recycled slot must start with no windup)
                TowerWindupFrames[entityId] = 0;
                TowerWindupCountdown[entityId] = 0;
                // Round 101 — Mana Drain reset (recycled slot must start with no drain)
                TowerManaDrainPct[entityId] = 0f;
                TowerManaDrainCap[entityId] = 0f;
                // Round 103 — Buff Share fields reset (recycled slot must start with no sharing)
                TowerBuffShareRadius[entityId] = 0f;
                TowerBuffShareMask[entityId] = 0;
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
                // Scatter/multicast fields (ProjectileCount reset to 1 matches AddTower default — legacy single-shot path)
                TowerProjectileCount[entityId] = 1;
                TowerScatterAngle[entityId] = 0f;
                // Round 114 — Lead Aim: recycled slot starts at 0 (no lead, zero-overhead fast path)
                TowerLeadAimFactor[entityId] = 0f;
                // Round 116 — Enchantment: recycled slot starts at 0 (no enchantment, fast path)
                TowerEnchantedElement[entityId] = 0;
                TowerEnchantBonus[entityId] = 0f;
                TowerEnchantDuration[entityId] = 0f;
                TowerEnchantExpiresAtTurn[entityId] = -1;
                // Shotgun pellet fields (reset on recycle so stale values don't leak)
                TowerPelletDamageMult[entityId] = 1f;
                TowerPelletConeRadius[entityId] = 0f;
                // Round 201 Direction 1 — Multi-Strike reset (recycled slot must start
                // without multi-strike — no extras; zero-overhead single-target path).
                TowerMultiStrikeCount[entityId] = 0;
                TowerMultiStrikeRange[entityId] = 0f;
                TowerMultiStrikeDamageMult[entityId] = 1f;
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
                // Round 106 — Mine fields reset (recycled slot must not leak mine state)
                TowerIsMine[entityId] = false;
                MineTriggerRadius[entityId] = 0f;
                MineArmTime[entityId] = 0f;
                MineArmProgress[entityId] = 0f;
                MineDamage[entityId] = 0f;
                MineExplosionRadius[entityId] = 0f;
                MineMaxStacks[entityId] = 1;
                MineStacksRemaining[entityId] = 0;
                MineTriggeredThisFrame[entityId] = false;
                // Round 172 — Chain Detonation reset (recycled slot must not leak chain state)
                MineCanChain[entityId] = false;
                MineChainRadius[entityId] = 0f;
                MineChainDamageMult[entityId] = 0f;
                MineChainDepth[entityId] = 0;
                // Round 173 — Shrine Tower reset (recycled slot must not leak shrine state)
                TowerIsShrine[entityId] = false;
                TowerShrineAuraType[entityId] = 0;
                TowerShrineRadius[entityId] = 0f;
                TowerShrinePotency[entityId] = 0f;
                // Round 173 — Shrine per-frame caches reset (no carry-over from recycled slot)
                TowerShrineCachedGoldBonus[entityId] = 0f;
                TowerShrineCachedManaRegen[entityId] = 0f;
                TowerShrineCachedDmgBonus[entityId] = 0f;
                TowerShrineCachedAtkSpdBonus[entityId] = 0f;
                // Round 177 Direction 2 — Beacon Tower reset (recycled slot must not leak beacon state)
                TowerIsBeacon[entityId] = false;
                TowerBeaconRadius[entityId] = 0f;
                TowerBeaconDmgBonus[entityId] = 0f;
                TowerBeaconAtkSpdBonus[entityId] = 0f;
                // Round 177 — Beacon per-frame caches reset (no carry-over from recycled slot)
                TowerBeaconCachedDmgBonus[entityId] = 0f;
                TowerBeaconCachedAtkSpdBonus[entityId] = 0f;
                // Round 175 Direction 9 — Smokescreen per-frame miss chance (recycled slot
                // must not carry the previous occupant's stale smoke miss chance — would
                // cause a freshly-placed tower to inherit a phantom miss debuff).
                TowerSmokeMissChance[entityId] = 0f;
                // Round 183 Direction 8 — Scorched Earth per-frame vision reduction
                // (recycled slot must not carry the previous occupant's stale vision
                // penalty — would cause a freshly-placed tower to inherit a phantom
                // range debuff).
                TowerVisionReduction[entityId] = 0f;
                // Round 174 Direction 4 — Backstab fields reset (recycled slot must not
                // carry the previous occupant's rogue config — would cause a freshly-
                // placed non-rogue tower to inherit a phantom 2.0x backstab bonus).
                TowerBackstabDamageMult[entityId] = 1.0f;
                TowerBackstabAngleDeg[entityId] = 0f;
                // Round 145 Direction 3 — Per-Tower Modifier reset (recycled slot must not
                // carry the previous occupant's modifier — would cause a freshly-placed
                // tower to inherit a random modifier it never rolled)
                TowerModifierId[entityId] = -1;
                TowerModifierMagnitude[entityId] = 0f;
                TowerModifierRarity[entityId] = 0;
                // Round 178 Direction 6 — Pre-fight Buff tower cache: reset to 1f (no change
                // fast path) so a freshly-placed tower does not inherit the previous
                // occupant's wave-scoped buff multiplier. 1f is the sentinel "no buff"
                // value read by TowerAttackSystem (gated by `if (preFightDmgMult != 1f)`).
                TowerPreFightDamageMult[entityId] = 1f;
                TowerPreFightSpeedMult[entityId] = 1f;
            }

            // ── Phase 4: recycle ID ────────────────────────────────────────────────
            // Round 139 — Per-Type Placement Cap: now that all TowerType fields are reset, drop
            // the per-player per-type counter. Done at phase-end (not inside the reset block) so
            // we don't need to read the player-id anywhere except this single moment. For now,
            // the only player with active towers is player 0; future multi-player would require
            // storing owner on the entity (out of scope).
            if (wasTower && rtType >= 0 && rtType < MAX_TOWER_TYPES)
            {
                int ownerIdx = 0; // single-player; TODO multi-player owner lookup
                int capBase = ownerIdx * MAX_TOWER_TYPES + rtType;
                if (PlayerTowersOfType[capBase] > 0) PlayerTowersOfType[capBase]--;
                if (PlayerTowerCount[ownerIdx] > 0) PlayerTowerCount[ownerIdx]--;
            }
            // Round 142 方向5 — Aggro / Focus Fire: when a tower is destroyed, clear any
            // focus assignment that pointed at it. Sweep is O(n_active_enemies) which is
            // acceptable: tower destruction is rare (sale / destruction event), and the
            // sweep is a single int compare per enemy. Alternative lazy-clear (check
            // TowerActive[] in AggroSystem tick) is O(n_enemies) every frame instead of
            // only on tower destruction — so eager-clear here is cheaper in aggregate.
            if (wasTower)
            {
                var activeEnemies = ActiveEnemyIds;
                for (int ei = 0; ei < activeEnemies.Count; ei++)
                {
                    int eid = activeEnemies[ei];
                    if (EnemyActive[eid] && EnemyFocusTowerId[eid] == entityId)
                    {
                        EnemyFocusTowerId[eid] = -1;
                        EnemyFocusDurationLeft[eid] = 0f;
                    }
                }
            }
            freeEntityIds.Push(entityId);
            // Round 103 — Buff Share: notify per-system caches to drop stale base-speed entries
            // for the recycled entityId (Claude bug scan fix #2: stale cache on ID reuse).
            if (wasTower)
                RaiseTowerEntityInvalidated(entityId);
        }

        public int NextEntityId => nextEntityId;
        public bool HasEntityCapacity => nextEntityId < MAX_ENTITIES || !freeEntityIds.IsEmpty;
        public int AvailableEntityCapacity => Math.Max(0, MAX_ENTITIES - Volatile.Read(ref nextEntityId)) + freeEntityIds.Count;

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
            return _activeEnemyIds.AsSpan();
        }

        /// <summary>
        /// Zero-allocation read-only span access to active tower IDs.
        /// </summary>
        public ReadOnlySpan<int> GetActiveTowerSpan()
        {
            return _activeTowerIds.AsSpan();
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
            DoomClockTimer = null!; DoomClockDuration = null!; DoomClockWavesCleared = null!;
            DoomClockCycleCount = null!; DoomClockFinalScore = null!; DoomClockActive = null!;
            EnemiesLeakedThisWave = null!; AdaptiveDifficultyLevel = null!; AdaptiveDifficultyScore = null!;
            ResourceNodeX = null!; ResourceNodeY = null!; ResourceNodeOwner = null!; ResourceNodeType = null!;
            ResourceNodeActive = null!; ResourceNodeProductionRate = null!;
            ResourceNodeHealth = null!; ResourceNodeMaxHealth = null!;
            ResourceNodeAccumulated = null!; ResourceNodeCaptureProgress = null!; ResourceNodeTowerId = null!;
            ResourceNodeRegenTimer = null!; ResourceNodeRegenDelay = null!; ResourceNodeDepleted = null!;
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
            EnemyFrostbiteMaxHpPct = null!; EnemyFrostbiteDurationLeft = null!; EnemyFrostbiteTimer = null!; EnemyFrostbiteResistance = null!;
            EnemyStunFlag = null!; EnemyStunDurationLeft = null!; EnemySlowFactor = null!;
            EnemyRootDurationLeft = null!; // Round 136 Direction 2: AOE root CC field
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
            EnemyInSummonCircleX = null!; EnemyInSummonCircleY = null!; EnemyInSummonCircleRadius = null!;
            SummonedUnitActive = null!; SummonedUnitType = null!;
            SummonedUnitHealth = null!; SummonedUnitMaxHealth = null!;
            SummonedUnitDamage = null!; SummonedUnitMoveSpeed = null!;
            SummonedUnitAttackRange = null!; SummonedUnitAttackSpeed = null!; SummonedUnitAttackTimer = null!;
            SummonedUnitDuration = null!; SummonedUnitOwnerId = null!;
            SummonedUnitTargetId = null!; SummonedUnitGoldReward = null!;
            EnemyBossPhase = null!; EnemyPhaseThresholds = null!;
            EnemyEnrageTimer = null!; EnemyIsEnraged = null!;
            // Round 134 Direction 3 — Boss HP regen arrays
            EnemyHealthRegenPerSec = null!; EnemyHealthRegenMult = null!;
            // Round 111 Direction 1 — Boss phase structured fields
            EnemyPhaseCount = null!; EnemyPhaseAbilityIdsFlat = null!; EnemyPhaseFiredMask = null!;
            EnemyPhaseThresholdsFlat = null!; EnemyPhaseSpeedMults = null!; EnemyPhaseDamageMults = null!;
            // Round 137 Dir 6 — Themed Boss Summon per-(phase,enemy) element affinity
            EnemyPhaseElementAffinityFlat = null!;
            // Round 107 Direction 6 — Target Mark Clear registration
            EnemyMarkStacks = null!; EnemyMarkDecayTimer = null!; EnemyMarkMaxThreshold = null!;
            // Round 200 Direction 5 — Death Mark Clear registration
            EnemyDeathMarkStacks = null!; EnemyDeathMarkTimer = null!; EnemyDeathMarkMaxStacks = null!;
            EnemyDeathMarkBonusPerStack = null!;
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
            EnemyAttackInterval = null!; EnemyAttackCooldownLeft = null!;
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
            // Round 124 — Disarm CC fields (per-tower chance + duration)
            TowerDisarmChance = null!; TowerDisarmDuration = null!;
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
            // Round 200 Direction 5 — Death Mark tower Clear registration
            TowerIsDeathMarkTower = null!; TowerDeathMarkChance = null!; TowerDeathMarkStacksPerHit = null!;
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
