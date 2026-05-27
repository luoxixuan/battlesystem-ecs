using System;
using System.Collections.Generic;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 弹道/飞行道具系统 — 管理 projectile 生命周期（生成、移动、命中结算）。
    /// 两阶段模式：串行 Update 中移动→命中检测→入 damage queue，帧末统一 apply。
    /// </summary>
    public class ProjectileSystem
    {
        private readonly ComponentStore store;
        private readonly IRenderer logger;
        private const int MAX_PROJ = 1024;

        // Projectile SOA fields
        private float[] _projX = new float[MAX_PROJ];
        private float[] _projY = new float[MAX_PROJ];
        private float[] _projVelX = new float[MAX_PROJ];
        private float[] _projVelY = new float[MAX_PROJ];
        private int[] _projTargetId = new int[MAX_PROJ];
        private float[] _projDamage = new float[MAX_PROJ];
        private int[] _projPlayerId = new int[MAX_PROJ];
        private int[] _projTowerId = new int[MAX_PROJ];
        private float[] _projSpeed = new float[MAX_PROJ];
        private bool[] _projActive = new bool[MAX_PROJ];
        // Homing flag: if true, projectile recalculates direction toward target each frame (turns mid-flight)
        private bool[] _projIsHoming = new bool[MAX_PROJ];
        private int _activeProjectileCount;

        // Ping-pong damage queue (same pattern as TowerAttackSystem)
        private List<(int enemyId, float damage, int playerId)>[] _damageQueue =
            new List<(int, float, int)>[2];
        private readonly object _damageQueueLock = new object();
        private int _damageQueueIdx;

        public ProjectileSystem(ComponentStore store, IRenderer logger)
        {
            this.store = store;
            this.logger = logger;
            _damageQueue[0] = new List<(int, float, int)>(256);
            _damageQueue[1] = new List<(int, float, int)>(256);
            for (int i = 0; i < MAX_PROJ; i++)
            {
                _projTargetId[i] = -1;
            }
        }

        /// <summary>
        /// Spawn a projectile from a tower toward a target enemy.
        /// </summary>
        public void Fire(int towerId, int targetId, float damage, int playerId, float speed, bool isHoming = false)
        {
            if (_activeProjectileCount >= MAX_PROJ) return;

            // Find free slot
            int projId = -1;
            for (int i = 0; i < MAX_PROJ; i++)
            {
                if (!_projActive[i]) { projId = i; break; }
            }
            if (projId < 0) return;

            _projX[projId] = store.PositionX[towerId];
            _projY[projId] = store.PositionY[towerId];
            _projTargetId[projId] = targetId;
            _projDamage[projId] = damage;
            _projPlayerId[projId] = playerId;
            _projTowerId[projId] = towerId;
            _projSpeed[projId] = speed;
            _projIsHoming[projId] = isHoming;
            _projVelX[projId] = 0f;
            _projVelY[projId] = 0f;
            _projActive[projId] = true;
            _activeProjectileCount++;
        }

        /// <summary>
        /// Serial update: move all active projectiles and resolve hits.
        /// </summary>
        public void Update(float deltaTime)
        {
            int resolvedHits = 0;
            int missedProjectiles = 0;

            for (int i = 0; i < MAX_PROJ; i++)
            {
                if (!_projActive[i]) continue;

                int targetId = _projTargetId[i];
                if (targetId >= 0 && store.EnemyActive[targetId])
                {
                    float tx = store.PositionX[targetId];
                    float ty = store.PositionY[targetId];
                    float dx = tx - _projX[i];
                    float dy = ty - _projY[i];
                    float distToTargetSq = dx * dx + dy * dy;

                    if (distToTargetSq > 0.01f)
                    {
                        float dist = MathF.Sqrt(distToTargetSq);
                        float nx = dx / dist;
                        float ny = dy / dist;
                        float speed = _projSpeed[i];
                        // Homing projectiles update direction every frame (turn mid-flight).
                        // Non-homing projectiles only get initial direction from Fire() — no mid-flight correction.
                        if (_projIsHoming[i])
                        {
                            _projVelX[i] = nx * speed;
                            _projVelY[i] = ny * speed;
                        }
                    }
                    else
                    {
                        // Already at target — resolve hit
                        ResolveHit(i);
                        _projActive[i] = false;
                        _activeProjectileCount--;
                        resolvedHits++;
                        continue;
                    }
                }
                else
                {
                    // Target lost (enemy died or invalid)
                    _projActive[i] = false;
                    _activeProjectileCount--;
                    missedProjectiles++;
                    continue;
                }

                // Move projectile
                _projX[i] += _projVelX[i] * deltaTime;
                _projY[i] += _projVelY[i] * deltaTime;

                // Check proximity to target (hit detection within 0.5 grid units)
                float tdx = store.PositionX[targetId] - _projX[i];
                float tdy = store.PositionY[targetId] - _projY[i];
                float proximitySq = tdx * tdx + tdy * tdy;
                float hitThresholdSq = 0.25f;

                if (proximitySq <= hitThresholdSq)
                {
                    ResolveHit(i);
                    _projActive[i] = false;
                    _activeProjectileCount--;
                    resolvedHits++;
                }
            }

            // Apply collected damage (ping-pong pattern)
            int readIdx = _damageQueueIdx;
            int writeIdx = 1 - _damageQueueIdx;
            _damageQueueIdx = writeIdx;
            _damageQueue[writeIdx].Clear();
            foreach (var (enemyId, damage, playerId) in _damageQueue[readIdx])
            {
                store.EnemyHealth[enemyId] -= damage;
                if (store.EnemyHealth[enemyId] <= 0f)
                {
                    store.QueueEnemyDeath(enemyId, playerId);
                }
            }
            _damageQueue[readIdx].Clear();
        }

        private void ResolveHit(int projId)
        {
            int targetId = _projTargetId[projId];
            float damage = _projDamage[projId];
            int playerId = _projPlayerId[projId];

            // Thorns reflect: enemy reflects a fraction of projectile damage
            float thornsRatio = store.EnemyThornsRatio[targetId];
            if (thornsRatio > 0f && damage > 0f)
            {
                lock (_damageQueueLock)
                {
                    // Thorns damage goes to player — use DecreasePlayerHealth
                    store.DecreasePlayerHealth(playerId, damage * thornsRatio);
                }
            }

            lock (_damageQueueLock)
            {
                _damageQueue[_damageQueueIdx].Add((targetId, damage, playerId));
            }
        }
    }
}
