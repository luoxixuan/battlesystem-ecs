#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BattleSystemECS.Core;
using BattleSystemECS.Components;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Beam Tower System — continuous DPS beam attacks with chain lightning.
    /// 
    /// Unlike projectile towers (which fire discrete shots with cooldowns), beam towers
    /// apply continuous damage every frame they have a valid target. This gives them
    /// higher sustained DPS at the cost of no burst potential.
    /// 
    /// Beam chain: a beam tower with chain count > 0 will chain damage to nearby enemies
    /// at reduced intensity per hop, similar to Tesla coil mechanics.
    /// 
    /// Two-phase model:
    ///   SetTurn:  cache active beam tower list, prepare chain data structures
    ///   Update:   apply DPS damage per frame, resolve chain hops
    /// </summary>
    public class BeamTowerSystem
    {
        private ComponentStore store;
        private int _turn = 0;

        // Damage per second scale factor: converts DPS to per-frame damage
        // Assumes 60 FPS — deltaTime-adjusted in Update
        private const float DPS_TO_FRAME_SCALE = 1f / 60f;

        // Ping-pong double-buffer for beam damage events (collected parallel, applied serial)
        private List<(int enemyId, float damage, int playerId, int towerId)>[] _beamDamageQueue = new List<(int, float, int, int)>[2];
        private readonly object _beamDamageQueueLock = new object();
        private int _beamDamageQueueIdx = 0;

        // Chain damage queue: (chainId, enemyId, damage, playerId, towerId)
        // chainId = -1: non-chain direct damage
        // chainId =  0: primary beam target (already in _beamDamageQueue)
        // chainId =  1..N: chain hop damage
        private List<(int chainId, int enemyId, float damage, int playerId, int towerId)>[] _chainBeamQueue = new List<(int, int, float, int, int)>[2];
        private readonly object _chainBeamQueueLock = new object();
        private int _chainBeamQueueIdx = 0;

        // Reusable candidate buffer for chain target search
        private int[] _chainCandidates = Array.Empty<int>();
        private const int MAX_CHAIN_CANDIDATES = 64;

        public BeamTowerSystem(ComponentStore store)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));

            // Initialize ping-pong buffers
            for (int i = 0; i < 2; i++)
            {
                _beamDamageQueue[i] = new List<(int, float, int, int)>(1024);
                _chainBeamQueue[i] = new List<(int, int, float, int, int)>(512);
            }
        }

        public void SetTurn(int turn)
        {
            _turn = turn;

            // Swap ping-pong buffers
            _beamDamageQueueIdx ^= 1;
            _chainBeamQueueIdx ^= 1;

            // Clear current buffers
            var dmgBuf = _beamDamageQueue[_beamDamageQueueIdx];
            var chainBuf = _chainBeamQueue[_chainBeamQueueIdx];
            if (dmgBuf.Count > 0) dmgBuf.Clear();
            if (chainBuf.Count > 0) chainBuf.Clear();

            // Ensure chain candidate buffer is allocated
            if (_chainCandidates.Length == 0)
                _chainCandidates = new int[MAX_CHAIN_CANDIDATES];
        }

        /// <summary>
        /// Returns true if the given tower is a beam tower.
        /// </summary>
        public bool IsBeamTower(int towerId)
        {
            return store.TowerIsBeam[towerId] && store.TowerActive[towerId];
        }

        /// <summary>
        /// Update all beam towers — apply continuous DPS damage per frame.
        /// Called from CombatGroup during WavePhase.
        /// </summary>
        public void Update(float deltaTime)
        {
            var towerIds = store.ActiveTowerIds;
            var dmgBuf = _beamDamageQueue[_beamDamageQueueIdx];
            var chainBuf = _chainBeamQueue[_chainBeamQueueIdx];
            var dmgLock = _beamDamageQueueLock;
            var chainLock = _chainBeamQueueLock;

            // DPS scale factor: damage per frame = DPS * deltaTime
            float dpsScale = deltaTime * DPS_TO_FRAME_SCALE;

            Parallel.For(0, towerIds.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, ti =>
            {
                int towerId = towerIds[ti];

                // Skip non-beam towers
                if (!store.TowerIsBeam[towerId]) return;
                if (!store.TowerActive[towerId]) return;

                // Silence check: skip if tower is silenced
                if (store.TowerIsSilenced[towerId]) return;

                // Construction check: skip towers under construction
                if (store.TowerIsConstructing[towerId]) return;

                // Get beam parameters
                float beamDps = store.TowerBeamDPS[towerId];
                if (beamDps <= 0f) return;  // no beam damage

                int chainCount = store.TowerBeamChainCount[towerId];
                float chainDecay = store.TowerBeamChainDecay[towerId];
                if (chainDecay <= 0f) chainDecay = 1f;  // safe default

                float towerX = store.PositionX[towerId];
                float towerY = store.PositionY[towerId];

                // Query enemies in beam range
                int beamRange = (int)store.TowerBeamMaxRange[towerId];
                if (beamRange <= 0) beamRange = store.TowerRange[towerId];  // fallback to tower range
                if (beamRange <= 0) return;

                int candidateCount = 0;
                store.SpatialGrid.GetEnemiesInRange(store, towerX, towerY, beamRange, _chainCandidates, ref candidateCount);

                if (candidateCount == 0) return;

                // Find primary target: nearest enemy (closest to tower, not furthest along path)
                float bestDistSq = float.MaxValue;
                int primaryTarget = -1;
                for (int ci = 0; ci < candidateCount; ci++)
                {
                    int enemyId = _chainCandidates[ci];
                    if (!store.EnemyActive[enemyId]) continue;
                    if (store.EnemyIsBurrowed[enemyId]) continue;

                    // Skip phased enemies unless tower is anti-phase (not yet implemented)
                    // Skip phased enemies (only relevant when anti-phase towers are implemented)
                    // if (store.EnemyIsPhased[enemyId]) continue;

                    float ex = store.PositionX[enemyId];
                    float ey = store.PositionY[enemyId];
                    float dx = ex - towerX;
                    float dy = ey - towerY;
                    float distSq = dx * dx + dy * dy;

                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        primaryTarget = enemyId;
                    }
                }

                if (primaryTarget == -1) return;

                // Apply beam DPS to primary target (continuous — no attack interval)
                float primaryDamage = beamDps * dpsScale;
                lock (dmgLock)
                {
                    dmgBuf.Add((primaryTarget, primaryDamage, store.PlayerEntityId, towerId));
                    // Also record for chain resolution
                    chainBuf.Add((0, primaryTarget, primaryDamage, store.PlayerEntityId, towerId));
                }

                // Resolve chain damage if chain count > 0
                if (chainCount > 0)
                {
                    ResolveBeamChain(
                        primaryTarget, towerX, towerY,
                        primaryDamage, chainCount, chainDecay,
                        beamRange, store.PlayerEntityId, towerId,
                        chainBuf, chainLock
                    );
                }
            });
        }

        /// <summary>
        /// Resolve chain hops for a beam tower — finds nearby enemies and applies decaying chain damage.
        /// </summary>
        private void ResolveBeamChain(
            int primaryTarget, float towerX, float towerY,
            float primaryDamage, int chainCount, float chainDecay,
            int beamRange, int playerId, int towerId,
            List<(int chainId, int enemyId, float damage, int playerId, int towerId)> chainBuf,
            object chainLock)
        {
            // Track already-hit enemies per chain to prevent double-tap
            var visited = new int[chainCount + 1];
            visited[0] = primaryTarget;
            int visitedCount = 1;

            int currentTarget = primaryTarget;
            float currentDamage = primaryDamage * chainDecay;

            // BFS-style chain: find nearest unvisited enemy to current target
            for (int hop = 1; hop <= chainCount; hop++)
            {
                float cx = store.PositionX[currentTarget];
                float cy = store.PositionY[currentTarget];

                // Find nearest enemy not yet visited within beam range
                float bestDistSq = float.MaxValue;
                int nextTarget = -1;

                int candCount = 0;
                store.SpatialGrid.GetEnemiesInRange(store, cx, cy, beamRange, _chainCandidates, ref candCount);

                for (int ci = 0; ci < candCount; ci++)
                {
                    int enemyId = _chainCandidates[ci];
                    if (!store.EnemyActive[enemyId]) continue;
                    if (store.EnemyIsBurrowed[enemyId]) continue;
                    // Skip phased enemies (only relevant when anti-phase towers are implemented)
                    // if (store.EnemyIsPhased[enemyId]) continue;

                    // Skip already visited
                    bool alreadyVisited = false;
                    for (int v = 0; v < visitedCount; v++)
                    {
                        if (visited[v] == enemyId)
                        {
                            alreadyVisited = true;
                            break;
                        }
                    }
                    if (alreadyVisited) continue;

                    float ex = store.PositionX[enemyId];
                    float ey = store.PositionY[enemyId];
                    float dx = ex - cx;
                    float dy = ey - cy;
                    float distSq = dx * dx + dy * dy;

                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        nextTarget = enemyId;
                    }
                }

                if (nextTarget == -1) break;  // no valid chain target

                visited[visitedCount++] = nextTarget;

                float chainDamage = currentDamage * chainDecay;
                lock (chainLock)
                {
                    chainBuf.Add((hop, nextTarget, chainDamage, playerId, towerId));
                }

                currentTarget = nextTarget;
                currentDamage = chainDamage;
            }
        }
    }
}