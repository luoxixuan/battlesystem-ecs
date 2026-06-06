using System;
using System.Collections.Generic;
using System.IO;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Mine / Trap Tower System — Round 106 Direction 2.
    /// Detonates proximity-triggered AoE damage when an enemy steps into a mine's
    /// trigger radius (after a short arming delay). Mines have a stack counter;
    /// each detonation consumes one stack. When the last stack is consumed the
    /// mine tower is destroyed (one-shot / multi-shot behavior).
    ///
    /// Lifecycle (per WavePhase tick, runs in SpatialGroup AFTER RebuildSpatialGrid):
    ///   1. Iterate active mine towers. Skip non-mine / un-armed / empty-stack.
    ///   2. Increment MineArmProgress. If still < MineArmTime, skip (not yet armed).
    ///   3. If MineTriggeredThisFrame is true, skip (one trigger per frame guard).
    ///   4. For each active enemy, distance-check vs MineTriggerRadius.
    ///   5. If at least one enemy is in range:
    ///        a. Reset MineTriggeredThisFrame flag will be done at end of frame
    ///           (begin frame should clear it; we use SetTurn to do that here for safety).
    ///        b. Decrement MineStacksRemaining.
    ///        c. Enqueue AoE damage to all enemies in MineExplosionRadius.
    ///   6. If MineStacksRemaining reaches 0, destroy the mine tower entity.
    ///   7. Clear MineTriggeredThisFrame at SetTurn (per-turn reset).
    ///
    /// Per-frame cost: O(active mines × enemies in trigger radius). The hot path
    /// skips with TowerIsMine==false (default), so non-mine towers incur zero cost.
    /// </summary>
    public class MineSystem
    {
        private readonly ComponentStore store;
        private readonly IRenderer logger;
        private readonly GameConfig gameConfig;
        private readonly int playerId;

        // Damage queue — pair of (enemyId, damage). MineSystem is serial (small N), so a
        // single ping-pong buffer is sufficient. Index 0/1 swap each frame to avoid
        // re-enqueue collisions during a single ResolveMineDamage pass.
        private readonly List<(int enemyId, float damage)>[] _damageQueue = new List<(int, float)>[2];
        private int _damageQueueIdx = 0;

        // Tower IDs pending destruction (MineStacksRemaining just hit 0).
        // Destroyed at the end of Update() in a single pass.
        private readonly List<int> _pendingDestroy = new List<int>(16);

        // ── Per-tower mine config (loaded from Data/Configs/mine_towers.json) ──
        public class MineDef
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public float TriggerRadius { get; set; } = 0f;
            public float ArmTime { get; set; } = 0f;
            public float Damage { get; set; } = 0f;
            public float ExplosionRadius { get; set; } = 0f;
            public int MaxStacks { get; set; } = 1;
            public float Cost { get; set; } = 0f;
        }

        private readonly Dictionary<int, MineDef> _mines = new Dictionary<int, MineDef>();

        public MineSystem(ComponentStore store, IRenderer logger, GameConfig gameConfig, int playerId = 0)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.gameConfig = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
            this.playerId = playerId;
            _damageQueue[0] = new List<(int, float)>(64);
            _damageQueue[1] = new List<(int, float)>(64);
            LoadMineConfigs();
        }

        /// <summary>
        /// Look up a mine config by id (1-based). Returns null if id is unknown —
        /// callers should fall back to MineConfig defaults.
        /// </summary>
        public MineDef GetMineDef(int mineId)
        {
            return _mines.TryGetValue(mineId, out var def) ? def : null;
        }

        /// <summary>
        /// Per-frame SetTurn — clear the per-frame trigger latch so mines can fire
        /// again next frame. Called by SpatialGroup.Execute before Update.
        /// </summary>
        public void SetTurn(int turn)
        {
            var activeTowerIds = store.ActiveTowerIds;
            for (int i = 0; i < activeTowerIds.Count; i++)
            {
                int tid = activeTowerIds[i];
                if (tid < 0 || tid >= ComponentStore.MAX_ENTITIES) continue;
                if (store.TowerIsMine[tid])
                    store.MineTriggeredThisFrame[tid] = false;
            }
        }

        /// <summary>
        /// Per-frame Update — arm mines, detect enemy proximity, enqueue AoE damage.
        /// Runs in SpatialGroup AFTER RebuildSpatialGrid so SpatialGrid queries are valid.
        /// </summary>
        public void Update(float deltaTime)
        {
            var activeTowerIds = store.ActiveTowerIds;
            if (activeTowerIds.Count == 0) return;

            // Reset the pending-destroy list at the start of the frame
            _pendingDestroy.Clear();

            // Reset per-frame trigger latch for all mines at the start of the frame.
            // This makes Update() self-contained: callers don't need to call SetTurn()
            // first (e.g. in unit tests). In the production hot path SetTurn() runs
            // just before Update() and would also clear it; clearing it here is a
            // no-op in that case (already false from previous SetTurn).
            for (int i = 0; i < activeTowerIds.Count; i++)
            {
                int tid = activeTowerIds[i];
                if (tid < 0 || tid >= ComponentStore.MAX_ENTITIES) continue;
                if (store.TowerIsMine[tid])
                    store.MineTriggeredThisFrame[tid] = false;
            }

            for (int i = 0; i < activeTowerIds.Count; i++)
            {
                int tid = activeTowerIds[i];
                if (tid < 0 || tid >= ComponentStore.MAX_ENTITIES) continue;
                if (!store.TowerIsMine[tid]) continue;
                if (!store.TowerActive[tid]) continue;
                if (store.MineStacksRemaining[tid] <= 0) continue;
                if (store.MineTriggeredThisFrame[tid]) continue;

                // 1) Advance arming progress
                store.MineArmProgress[tid] += deltaTime;
                if (store.MineArmProgress[tid] < store.MineArmTime[tid]) continue;

                // 2) Range check against active enemies (no SpatialGrid optimization
                //    because trigger radius is small + variable). O(active enemies)
                //    per mine is acceptable given mine counts are bounded.
                float mx = store.PositionX[tid];
                float my = store.PositionY[tid];
                float triggerSq = store.MineTriggerRadius[tid] * store.MineTriggerRadius[tid];

                int triggerEnemy = -1;
                var activeEnemyIds = store.ActiveEnemyIds;
                for (int e = 0; e < activeEnemyIds.Count; e++)
                {
                    int eid = activeEnemyIds[e];
                    if (eid < 0 || eid >= ComponentStore.MAX_ENTITIES) continue;
                    if (!store.EnemyActive[eid]) continue;
                    if (store.EnemyHealth[eid] <= 0f) continue;
                    float ex = store.PositionX[eid];
                    float ey = store.PositionY[eid];
                    float dx = ex - mx;
                    float dy = ey - my;
                    float dSq = dx * dx + dy * dy;
                    if (dSq <= triggerSq)
                    {
                        triggerEnemy = eid;
                        break;
                    }
                }

                if (triggerEnemy < 0) continue;

                // 3) Detonate. Latch the per-frame flag to prevent multi-fire this frame.
                store.MineTriggeredThisFrame[tid] = true;
                store.MineStacksRemaining[tid] -= 1;

                float damage = store.MineDamage[tid];
                float explosionRadius = store.MineExplosionRadius[tid];
                if (damage > 0f && explosionRadius > 0f)
                {
                    EnqueueExplosionDamage(mx, my, explosionRadius, damage);
                }

                // 4) Stack exhaustion → schedule destroy
                if (store.MineStacksRemaining[tid] <= 0)
                {
                    _pendingDestroy.Add(tid);
                }
            }

            // 5) Apply queued explosion damage (serial pass after parallel-unsafe enqueue).
            //    Note: MineSystem is already serial so the queue is a thin abstraction —
            //    we still ping-pong for consistency with BleedSystem.
            ResolveMineDamage();

            // 6) Destroy exhausted mines
            for (int i = 0; i < _pendingDestroy.Count; i++)
            {
                int tid = _pendingDestroy[i];
                if (tid < 0 || tid >= ComponentStore.MAX_ENTITIES) continue;
                if (!store.TowerActive[tid]) continue;
                // Round 139 — Per-Type Placement Cap: ComponentStore.DestroyEntity now handles
                // PlayerTowerCount / PlayerTowersOfType decrement. The explicit pre-decrement
                // here was removed to avoid double-decrementing (destroy would drop the counter
                // twice and drive it negative).
                store.DestroyEntity(tid);
            }
        }

        /// <summary>
        /// Enqueue AoE damage to every enemy within <paramref name="explosionRadius"/>
        /// of (mx, my). Damage type is Physical by default — designers can extend
        /// the system later to add a MineDamageType field if needed.
        /// </summary>
        private void EnqueueExplosionDamage(float mx, float my, float explosionRadius, float damage)
        {
            float explSq = explosionRadius * explosionRadius;
            var activeEnemyIds = store.ActiveEnemyIds;
            for (int e = 0; e < activeEnemyIds.Count; e++)
            {
                int eid = activeEnemyIds[e];
                if (eid < 0 || eid >= ComponentStore.MAX_ENTITIES) continue;
                if (!store.EnemyActive[eid]) continue;
                if (store.EnemyHealth[eid] <= 0f) continue;
                if (store.EnemyIsInvulnerable[eid]) continue;
                float ex = store.PositionX[eid];
                float ey = store.PositionY[eid];
                float dx = ex - mx;
                float dy = ey - my;
                float dSq = dx * dx + dy * dy;
                if (dSq <= explSq)
                {
                    _damageQueue[_damageQueueIdx].Add((eid, damage));
                }
            }
        }

        /// <summary>
        /// Serial pass: apply queued damage to enemies. Enemies whose HP drops to
        /// 0 are queued for death via store.QueueEnemyDeath — the FrameScheduler's
        /// post-phase ResolveEnemiesKilledThisFrame handles the actual destroy.
        /// </summary>
        private void ResolveMineDamage()
        {
            int readIdx = _damageQueueIdx;
            int writeIdx = 1 - _damageQueueIdx;
            _damageQueueIdx = writeIdx;
            _damageQueue[writeIdx].Clear();

            var readQueue = _damageQueue[readIdx];
            for (int i = 0; i < readQueue.Count; i++)
            {
                var (eid, dmg) = readQueue[i];
                if (eid < 0 || eid >= ComponentStore.MAX_ENTITIES) continue;
                if (!store.EnemyActive[eid]) continue;
                if (store.EnemyHealth[eid] <= 0f) continue;
                if (store.EnemyIsInvulnerable[eid]) continue;
                float newHp = store.EnemyHealth[eid] - dmg;
                if (newHp < 0f) newHp = 0f;
                store.EnemyHealth[eid] = newHp;
                if (newHp <= 0f)
                {
                    store.QueueEnemyDeath(eid, playerId);
                }
            }
            readQueue.Clear();
        }

        // ── Mine config loader ────────────────────────────────────────────

        private void LoadMineConfigs()
        {
            string basePath = AppDomain.CurrentDomain.BaseDirectory;
            string configPath = Path.Combine(basePath, "Data", "Configs", "mine_towers.json");
            if (!File.Exists(configPath))
            {
                // File missing — silently fall back to MineConfig defaults at PlaceTower time.
                return;
            }
            try
            {
                string json = File.ReadAllText(configPath);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("mines", out var minesElem) || minesElem.ValueKind != System.Text.Json.JsonValueKind.Array)
                    return;
                foreach (var elem in minesElem.EnumerateArray())
                {
                    int id = elem.TryGetProperty("id", out var idElem) ? idElem.GetInt32() : -1;
                    if (id < 0) continue;
                    var def = new MineDef
                    {
                        Id = id,
                        Name = elem.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        TriggerRadius = elem.TryGetProperty("triggerRadius", out var tr) ? tr.GetSingle() : MineConfig.DefaultTriggerRadius,
                        ArmTime = elem.TryGetProperty("armTime", out var at) ? at.GetSingle() : MineConfig.DefaultArmTime,
                        Damage = elem.TryGetProperty("damage", out var d) ? d.GetSingle() : MineConfig.DefaultDamage,
                        ExplosionRadius = elem.TryGetProperty("explosionRadius", out var er) ? er.GetSingle() : MineConfig.DefaultExplosionRadius,
                        MaxStacks = elem.TryGetProperty("maxStacks", out var ms) ? ms.GetInt32() : MineConfig.DefaultMaxStacks,
                        Cost = elem.TryGetProperty("cost", out var c) ? c.GetSingle() : MineConfig.DefaultCost,
                    };
                    _mines[id] = def;
                }
            }
            catch (Exception ex)
            {
                logger?.Log($"[MINE] LoadMineConfigs error: {ex.Message}");
            }
        }
    }
}
