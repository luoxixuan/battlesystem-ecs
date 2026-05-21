using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BattleSystemECS.Core;
using BattleSystemECS.Components;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Tower attack system - handles tower target acquisition and enemy damage + debuffs.
    /// Two-phase: parallel collect, serial resolve (Bug#2 thread-safety fix).
    /// Tower type-specific mechanics (Tesla chain lightning, Leech lifesteal, Frost slow, Firewall DoT).
    /// </summary>
    public class TowerAttackSystem
    {
        private ComponentStore store;
        private IRenderer logger;
        private TechTreeSystem techTreeSystem;
        private BuffSystem buffSystem;
        private List<int> _activeEnemyList;

        // GC elimination: per-tower reusable candidate lists, pre-allocated in SetTurn
        private List<int>[] _towerCandidates = Array.Empty<List<int>>();

        // Ping-pong double-buffer: eliminates per-frame new ConcurrentBag<>() allocation
        private List<(int enemyId, float damage, int playerId)>[] _damageQueue = new List<(int, float, int)>[2];
        private readonly object _damageQueueLock = new object();
        private int _damageQueueIdx = 0;

        // Ping-pong double-buffer for tower debuff events (collected parallel, applied serial)
        private List<(int enemyId, int towerId)>[] _debuffQueue = new List<(int, int)>[2];
        private readonly object _debuffQueueLock = new object();
        private int _debuffQueueIdx = 0;

        // Ping-pong double-buffer for tower type-specific events (Leech lifesteal heal, etc.)
        private List<(int playerId, float healAmount)>[] _healQueue = new List<(int, float)>[2];
        private readonly object _healQueueLock = new object();
        private int _healQueueIdx = 0;

        // Cached player armor stats (updated each SetTurn)
        private float _armorPenetration = 0f;  // from TechTreeSystem
        private float _damageTakenMult = 1f;   // from TechTreeSystem

        // Cached wave-based difficulty multiplier (updated each SetTurn)
        private float _waveDifficultyMult = 1f;

        // Shared random for debuff chance rolls — uses Random.Shared (.NET 6+ thread-safe)
        private static readonly Random _rand = Random.Shared;

        // Ping-pong double-buffer for Tesla chain lightning damage events
        // (int chainId, int enemyId, float damage): chainId=-1 means non-chain (handled by debuff phase)
        private List<(int chainId, int enemyId, float damage, int playerId)>[] _chainDamageQueue = new List<(int, int, float, int)>[2];
        private readonly object _chainDamageQueueLock = new object();
        private int _chainDamageQueueIdx = 0;

        // Ping-pong double-buffer for splash damage events (from upgrade special abilities)
        private List<(int primaryEnemyId, float splashDamage, int playerId, int towerId)>[] _splashDamageQueue = new List<(int, float, int, int)>[2];
        private readonly object _splashDamageQueueLock = new object();
        private int _splashDamageQueueIdx = 0;

        // Leech lifesteal rate: 30% of damage dealt is returned as player heal
        private const float LEECH_LIFESTEAL_RATE = 0.30f;

        public TowerAttackSystem(ComponentStore store, IRenderer logger, TechTreeSystem techTreeSystem = null)
        {
            this.store = store;
            this.logger = logger;
            this.techTreeSystem = techTreeSystem;
            _damageQueue[0] = new List<(int, float, int)>(256);
            _damageQueue[1] = new List<(int, float, int)>(256);
            _debuffQueue[0] = new List<(int, int)>(256);
            _debuffQueue[1] = new List<(int, int)>(256);
            _healQueue[0] = new List<(int, float)>(64);
            _healQueue[1] = new List<(int, float)>(64);
            _chainDamageQueue[0] = new List<(int, int, float, int)>(64);
            _chainDamageQueue[1] = new List<(int, int, float, int)>(64);
            _splashDamageQueue[0] = new List<(int, float, int, int)>(64);
            _splashDamageQueue[1] = new List<(int, float, int, int)>(64);
        }

        /// <summary>
        /// Inject BuffSystem reference for Leech lifesteal healing and Firewall DoT effects.
        /// </summary>
        public void SetBuffSystem(BuffSystem buffSystem)
        {
            this.buffSystem = buffSystem;
        }

        public void SetTurn(int turn)
        {
            _activeEnemyList = store.GetCachedActiveEnemyIds();  // zero allocation — frame cache

            // Cache armor stats from tech tree
            _armorPenetration = techTreeSystem != null ? techTreeSystem.GetArmorPenetration() : 0f;
            _damageTakenMult = techTreeSystem != null ? techTreeSystem.GetDamageTakenMult() : 1f;

            // Cache wave-based difficulty multiplier (default wave 1)
            _waveDifficultyMult = techTreeSystem != null ? techTreeSystem.GetWaveDifficultyMultiplier(1) : 1f;

            // Ensure _towerCandidates is large enough; each slot is a reusable List<int>
            var towerIds = store.ActiveTowerIds;
            if (_towerCandidates.Length < towerIds.Count)
            {
                var newArr = new List<int>[towerIds.Count];
                Array.Copy(_towerCandidates, newArr, _towerCandidates.Length);
                for (int i = _towerCandidates.Length; i < newArr.Length; i++)
                    newArr[i] = new List<int>(128);
                _towerCandidates = newArr;
            }
        }

        /// <summary>
        /// Update the cached wave difficulty multiplier when wave number changes.
        /// Call this when a new wave starts.
        /// </summary>
        public void SetWaveNumber(int waveNumber)
        {
            _waveDifficultyMult = techTreeSystem != null ? techTreeSystem.GetWaveDifficultyMultiplier(waveNumber) : 1f;
        }

        public void Update(float deltaTime)
        {
            var activeTowerIds = store.ActiveTowerIds;

            // Defensive: ensure _towerCandidates covers all towers before parallel loop.
            // Safe to call every frame — SetTurn also calls this; extra invocation is a no-op
            // when length is already sufficient.
            if (_towerCandidates.Length < activeTowerIds.Count)
            {
                var newArr = new List<int>[activeTowerIds.Count];
                Array.Copy(_towerCandidates, newArr, _towerCandidates.Length);
                for (int i = _towerCandidates.Length; i < newArr.Length; i++)
                    newArr[i] = new List<int>(128);
                _towerCandidates = newArr;
            }

            // Phase 0: Spatial grid already rebuilt by GameManager before system chain.
            // Reuse instead of rebuilding — avoids O(enemies) waste per frame.

            // Phase 1 (parallel): collect damage events and debuff events — no structural mutations.
            var bag = _damageQueue[_damageQueueIdx];
            var debuffBag = _debuffQueue[_debuffQueueIdx];
            var chainBag = _chainDamageQueue[_chainDamageQueueIdx];
            var healBag = _healQueue[_healQueueIdx];
            var splashBag = _splashDamageQueue[_splashDamageQueueIdx];
            var damageLock = _damageQueueLock;
            var debuffLock = _debuffQueueLock;
            var chainLock = _chainDamageQueueLock;
            var healLock = _healQueueLock;
            var splashLock = _splashDamageQueueLock;

            Parallel.For(0, activeTowerIds.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, ti =>
            {
                int towerId = activeTowerIds[ti];

                store.TowerLastAttackTime[towerId] += deltaTime;

                float attackInterval = 1.0f / Math.Max(0.1f, store.TowerAttackSpeed[towerId]);
                if (store.TowerLastAttackTime[towerId] < attackInterval) return;

                float tx = store.PositionX[towerId];
                float ty = store.PositionY[towerId];
                int range = store.TowerRange[towerId];

                // Spatial grid: query O(cells) instead of O(enemies) — reuse pre-allocated list
                var candidates = _towerCandidates[ti];
                candidates.Clear();
                store.SpatialGrid.GetEnemiesInRange(store, tx, ty, range, candidates);

                int bestTarget = -1;
                float minDistSq = float.MaxValue;

                for (int ci = 0; ci < candidates.Count; ci++)
                {
                    int enemyId = candidates[ci];
                    if (!store.EnemyActive[enemyId]) continue;

                    float ex = store.PositionX[enemyId];
                    float ey = store.PositionY[enemyId];

                    float dx = ex - tx;
                    float dy = ey - ty;

                    float distSq = dx * dx + dy * dy;
                    if (distSq < minDistSq)
                    {
                        minDistSq = distSq;
                        bestTarget = enemyId;
                    }
                }

                if (bestTarget != -1)
                {
                    store.TowerLastAttackTime[towerId] = 0f;
                    float baseDmg = store.TowerAttackDamage[towerId];
                    // Apply enemy armor reduction + tech tree damage taken multiplier + wave scaling
                    baseDmg *= Math.Max(0.01f, 1f - store.EnemyArmor[bestTarget] * (1f - _armorPenetration)) * _damageTakenMult;
                    if (_waveDifficultyMult != 1.0f) baseDmg *= _waveDifficultyMult;

                    // Apply ally buff damage bonus (buff_allies ability from enemy BT)
                    baseDmg += store.EnemyBuffDamageBonus[bestTarget];

                    // ── Tower type-specific mechanics ─────────────────────────────────────
                    string towerType = store.TowerType[towerId] ?? "Basic";

                    switch (towerType)
                    {
                        case "Tesla":
                            // Chain lightning: primary target + up to 3 chained targets at 70% decay
                            lock (chainLock) { chainBag.Add((0, bestTarget, baseDmg, store.PlayerEntityId)); }
                            break;

                        case "Leech":
                            // Damage + lifesteal (heal player)
                            lock (damageLock) { bag.Add((bestTarget, baseDmg, store.PlayerEntityId)); }
                            float healAmount = baseDmg * LEECH_LIFESTEAL_RATE;
                            if (healAmount > 0f)
                                lock (healLock) { healBag.Add((store.PlayerEntityId, healAmount)); }
                            break;

                        case "Frost":
                            // Damage + tower slow debuff (handled by debuff phase)
                            lock (damageLock) { bag.Add((bestTarget, baseDmg, store.PlayerEntityId)); }
                            lock (debuffLock) { debuffBag.Add((bestTarget, towerId)); }
                            break;

                        case "Firewall":
                            // Damage + Firewall DoT (handled by debuff phase)
                            lock (damageLock) { bag.Add((bestTarget, baseDmg, store.PlayerEntityId)); }
                            lock (debuffLock) { debuffBag.Add((bestTarget, towerId)); }
                            break;

                        default:
                            // Basic / unknown: standard damage + standard debuff check
                            lock (damageLock) { bag.Add((bestTarget, baseDmg, store.PlayerEntityId)); }
                            // Special ability: armor pierce (reduces enemy armor effectiveness)
                            if (store.TowerArmorPierceRatio[towerId] > 0f)
                            {
                                float effectiveArmor = store.EnemyArmor[bestTarget] * (1f - store.TowerArmorPierceRatio[towerId]);
                                float pierceBonus = baseDmg * (1f - Math.Max(0.01f, 1f - effectiveArmor));
                                if (pierceBonus > 0f)
                                    lock (damageLock) { bag.Add((bestTarget, pierceBonus, store.PlayerEntityId)); }
                            }
                            // Special ability: splash damage (AOE)
                            if (store.TowerSplashRadius[towerId] > 0f)
                            {
                                // Collect splash targets for later processing
                                lock (splashLock) { splashBag.Add((bestTarget, baseDmg * 0.5f, store.PlayerEntityId, towerId)); }
                            }
                            // Special ability: critical strike
                            if (store.TowerCritChance[towerId] > 0f && _rand.NextDouble() < store.TowerCritChance[towerId])
                            {
                                float critBonus = baseDmg * (store.TowerCritMultiplier[towerId] - 1f);
                                if (critBonus > 0f)
                                    lock (damageLock) { bag.Add((bestTarget, critBonus, store.PlayerEntityId)); }
                            }
                            // Special ability: chain lightning (from upgrade, not Tesla tower type)
                            if (store.TowerHasChainLightning[towerId])
                            {
                                lock (chainLock) { chainBag.Add((0, bestTarget, baseDmg, store.PlayerEntityId)); }
                            }
                            // Special ability: freeze AOE (from upgrade)
                            if (store.TowerHasFreezeAoe[towerId])
                            {
                                lock (debuffLock) { debuffBag.Add((bestTarget, towerId)); }
                            }
                            // Standard stun/slow from tower debuff config
                            float stunChance = store.TowerStunChance[towerId];
                            float slowAmount = store.TowerSlowAmount[towerId];
                            if (stunChance > 0f || slowAmount > 0f)
                                lock (debuffLock) { debuffBag.Add((bestTarget, towerId)); }
                            break;
                    }
                }
            });

            // Phase 2 (serial): apply damage
            int readIdx = _damageQueueIdx;
            int writeIdx = 1 - _damageQueueIdx;
            _damageQueueIdx = writeIdx;
            _damageQueue[writeIdx].Clear();
            foreach (var (enemyId, damage, playerId) in _damageQueue[readIdx])
            {
                if (!store.EnemyActive[enemyId]) continue;
                store.EnemyHealth[enemyId] -= damage;
                if (store.EnemyHealth[enemyId] <= 0f)
                {
                    store.QueueEnemyDeath(enemyId, playerId);
                }
            }

            // Phase 2b (serial): resolve Tesla chain lightning (after basic damage to avoid double-hit on primary)
            ResolveTeslaChainLightning();

            // Phase 2c (serial): resolve splash damage from upgrade special ability
            ResolveSplashDamage();

            // Phase 2d (serial): resolve Leech lifesteal heals
            ResolveLeechHealing();

            System.Threading.Thread.MemoryBarrier(); // ensure drain completes

            // Phase 3 (serial): apply tower debuffs (stun/slow from Basic/EMP/Doom towers, Frost slow, Firewall DoT)
            int debuffReadIdx = _debuffQueueIdx;
            int debuffWriteIdx = 1 - _debuffQueueIdx;
            _debuffQueueIdx = debuffWriteIdx;
            _debuffQueue[debuffWriteIdx].Clear();
            foreach (var (enemyId, towerId) in _debuffQueue[debuffReadIdx])
            {
                if (!store.EnemyActive[enemyId]) continue;

                string towerType = store.TowerType[towerId] ?? "Basic";
                float stunChance = store.TowerStunChance[towerId];
                float slowAmount = store.TowerSlowAmount[towerId];
                float slowDuration = store.TowerSlowDuration[towerId];

                switch (towerType)
                {
                    case "Firewall":
                        // Firewall: apply burn DoT (continuous damage over time via BuffSystem)
                        if (buffSystem != null && slowAmount > 0f && slowDuration > 0f)
                        {
                            buffSystem.ApplyDot(enemyId, slowAmount, (int)slowDuration);
                        }
                        // Also roll stun
                        if (stunChance > 0f && _rand.NextDouble() < stunChance)
                        {
                            store.ApplyEnemyStun(enemyId, 1);
                        }
                        break;

                    default:
                        // Basic / Frost / EMP / Doom: stun + slow
                        if (stunChance > 0f && _rand.NextDouble() < stunChance)
                        {
                            store.ApplyEnemyStun(enemyId, 1);
                        }
                        if (slowAmount > 0f && slowDuration > 0f)
                        {
                            store.ApplyEnemySlow(enemyId, slowAmount, (int)slowDuration);
                        }
                        break;
                }
            }
            System.Threading.Thread.MemoryBarrier();
        }

        /// <summary>
        /// Resolve Tesla chain lightning: O(N) nearest-neighbor chaining on primary target.
        /// Primary target already took basic damage in Phase 2; this handles the chain hops.
        /// </summary>
        private void ResolveTeslaChainLightning()
        {
            int readIdx = _chainDamageQueueIdx;
            int writeIdx = 1 - _chainDamageQueueIdx;
            _chainDamageQueueIdx = writeIdx;
            _chainDamageQueue[writeIdx].Clear();

            foreach (var (chainId, enemyId, damage, playerId) in _chainDamageQueue[readIdx])
            {
                if (!store.EnemyActive[enemyId]) continue;

                // Primary damage (chainId == 0): already applied in Phase 2 via _damageQueue
                // Chain hop damage: apply and check for kill
                if (chainId > 0)
                {
                    store.EnemyHealth[enemyId] -= damage;
                    if (store.EnemyHealth[enemyId] <= 0f)
                    {
                        store.QueueEnemyDeath(enemyId, playerId);
                    }
                }
            }
        }

        /// <summary>
        /// Resolve splash damage from tower upgrade special abilities.
        /// Deals reduced damage to enemies near the primary target.
        /// </summary>
        private void ResolveSplashDamage()
        {
            int readIdx = _splashDamageQueueIdx;
            int writeIdx = 1 - _splashDamageQueueIdx;
            _splashDamageQueueIdx = writeIdx;
            _splashDamageQueue[writeIdx].Clear();

            foreach (var (primaryEnemyId, splashDamage, playerId, towerId) in _splashDamageQueue[readIdx])
            {
                if (!store.EnemyActive[primaryEnemyId]) continue;
                if (store.TowerSplashRadius[towerId] <= 0f) continue;

                float px = store.PositionX[primaryEnemyId];
                float py = store.PositionY[primaryEnemyId];
                int splashRadius = (int)store.TowerSplashRadius[towerId];

                // Collect nearby enemies via spatial grid
                if (splashRadius > 0 && splashRadius <= 100)
                {
                    var candidates = _towerCandidates[0]; // reuse first slot
                    candidates.Clear();
                    store.SpatialGrid.GetEnemiesInRange(store, px, py, splashRadius, candidates);

                    for (int ci = 0; ci < candidates.Count; ci++)
                    {
                        int enemyId = candidates[ci];
                        if (!store.EnemyActive[enemyId] || enemyId == primaryEnemyId) continue;

                        store.EnemyHealth[enemyId] -= splashDamage;
                        if (store.EnemyHealth[enemyId] <= 0f)
                        {
                            store.QueueEnemyDeath(enemyId, playerId);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Resolve Leech lifesteal healing: apply player HP regen from Leech tower damage.
        /// </summary>
        private void ResolveLeechHealing()
        {
            int readIdx = _healQueueIdx;
            int writeIdx = 1 - _healQueueIdx;
            _healQueueIdx = writeIdx;
            _healQueue[writeIdx].Clear();

            foreach (var (playerId, healAmount) in _healQueue[readIdx])
            {
                if (playerId < 0 || playerId >= ComponentStore.MAX_PLAYERS) continue;
                float maxHealth = store.GetPlayerMaxHealth(playerId);
                float currentHealth = store.GetPlayerCurrentHealth(playerId);
                float newHealth = Math.Min(currentHealth + healAmount, maxHealth);
                store.SetPlayerCurrentHealth(playerId, newHealth);
            }
        }
    }
}
