using System;
using System.Collections.Generic;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 敌方弹道系统 — 管理敌人发射的飞行道具（追踪玩家基地或指定目标）。
    /// 这些弹道从敌人飞向玩家阵地，需要 Point Defense 塔来拦截。
    /// 
    /// 两阶段模式：串行 Update 中移动→命中检测→玩家伤害，帧末统一 apply。
    /// </summary>
    public class EnemyProjectileSystem : global::BattleSystemECS.Content.Contracts.IEnemyProjectilePort
    {
        private readonly ComponentStore store;
        private const int MAX_ENEMY_PROJ = 4096;

        // EnemyProjectile SOA fields (parallel to PlayerProjectileSystem)
        private float[] _eprojX = new float[MAX_ENEMY_PROJ];
        private float[] _eprojY = new float[MAX_ENEMY_PROJ];
        private float[] _eprojVelX = new float[MAX_ENEMY_PROJ];
        private float[] _eprojVelY = new float[MAX_ENEMY_PROJ];
        // TargetId: -1 = aimed at player base (straight line), >= 0 = tracking target entity
        private int[] _eprojTargetId = new int[MAX_ENEMY_PROJ];
        private float[] _eprojDamage = new float[MAX_ENEMY_PROJ];
        // OwnerEnemyId: the enemy that fired this projectile
        private int[] _eprojOwnerEnemyId = new int[MAX_ENEMY_PROJ];
        private float[] _eprojSpeed = new float[MAX_ENEMY_PROJ];
        private bool[] _eprojActive = new bool[MAX_ENEMY_PROJ];
        private int _activeEnemyProjectileCount;

        // Ping-pong damage queue: (playerId, damage) — applied serial at frame end
        private List<(int playerId, float damage)>[] _damageQueue =
            new List<(int, float)>[2];
        private readonly object _damageQueueLock = new object();
        private int _damageQueueIdx;

        public EnemyProjectileSystem(ComponentStore store)
        {
            this.store = store;
            _damageQueue[0] = new List<(int, float)>(128);
            _damageQueue[1] = new List<(int, float)>(128);
            for (int i = 0; i < MAX_ENEMY_PROJ; i++)
            {
                _eprojTargetId[i] = -1;
                _eprojOwnerEnemyId[i] = -1;
            }
        }

        /// <summary>
        /// Fire an enemy projectile from a given enemy position toward a target.
        /// targetId = -1 means aimed at player base (straight-line flight).
        /// </summary>
        public void Fire(int ownerEnemyId, float startX, float startY, int targetId,
            float targetX, float targetY, float damage, float speed)
        {
            if (_activeEnemyProjectileCount >= MAX_ENEMY_PROJ) return;

            int projId = -1;
            for (int i = 0; i < MAX_ENEMY_PROJ; i++)
            {
                if (!_eprojActive[i]) { projId = i; break; }
            }
            if (projId < 0) return;

            _eprojX[projId] = startX;
            _eprojY[projId] = startY;
            _eprojTargetId[projId] = targetId;
            _eprojOwnerEnemyId[projId] = ownerEnemyId;
            _eprojDamage[projId] = damage;
            _eprojSpeed[projId] = speed;
            _eprojActive[projId] = true;
            _activeEnemyProjectileCount++;

            // Compute initial velocity direction
            float dx, dy;
            if (targetId >= 0)
            {
                // Tracking: aim at target's current position (recalculated each frame in Update)
                dx = targetX - startX;
                dy = targetY - startY;
            }
            else
            {
                // Straight-line: aimed at player base (base is always at fixed position)
                // Use a default base target direction or passed-in target coords
                dx = targetX - startX;
                dy = targetY - startY;
            }

            float distSq = dx * dx + dy * dy;
            if (distSq > 0.001f)
            {
                float dist = MathF.Sqrt(distSq);
                _eprojVelX[projId] = (dx / dist) * speed;
                _eprojVelY[projId] = (dy / dist) * speed;
            }
            else
            {
                _eprojVelX[projId] = 0f;
                _eprojVelY[projId] = speed; // default upward
            }
        }

        /// <summary>
        /// Serial update: move all enemy projectiles and resolve hits on player base.
        /// PointDefenseSystem calls this first, then PointDefenseSystem shoots down remaining.
        /// </summary>
        public void Update(float deltaTime)
        {
            int resolvedHits = 0;
            int missedProjectiles = 0;

            for (int i = 0; i < MAX_ENEMY_PROJ; i++)
            {
                if (!_eprojActive[i]) continue;

                int targetId = _eprojTargetId[i];

                // Retarget tracking projectiles each frame (they chase moving targets)
                if (targetId >= 0 && store.EnemyActive[targetId])
                {
                    float tx = store.PositionX[targetId];
                    float ty = store.PositionY[targetId];
                    float dx = tx - _eprojX[i];
                    float dy = ty - _eprojY[i];
                    float distSq = dx * dx + dy * dy;

                    if (distSq > 0.01f)
                    {
                        float dist = MathF.Sqrt(distSq);
                        float nx = dx / dist;
                        float ny = dy / dist;
                        float speed = _eprojSpeed[i];
                        _eprojVelX[i] = nx * speed;
                        _eprojVelY[i] = ny * speed;
                    }
                }
                else if (targetId >= 0)
                {
                    // Target died mid-flight — deactivate
                    _eprojActive[i] = false;
                    _activeEnemyProjectileCount--;
                    missedProjectiles++;
                    continue;
                }

                // Move projectile
                _eprojX[i] += _eprojVelX[i] * deltaTime;
                _eprojY[i] += _eprojVelY[i] * deltaTime;

                // Hit detection: check proximity to target (player base at fixed location)
                // Player base position is store.PositionX[1], store.PositionY[1]
                // For intercept mode, we just check distance to player base position
                float baseX = store.PositionX[1]; // player entity
                float baseY = store.PositionY[1];
                float dxBase = baseX - _eprojX[i];
                float dyBase = baseY - _eprojY[i];
                float proximitySq = dxBase * dxBase + dyBase * dyBase;
                float hitThresholdSq = 0.25f; // 0.5 grid units

                if (proximitySq <= hitThresholdSq)
                {
                    ResolveHitPlayer(i);
                    _eprojActive[i] = false;
                    _activeEnemyProjectileCount--;
                    resolvedHits++;
                }
                else if (_eprojX[i] < -50f || _eprojX[i] > 200f || _eprojY[i] < -50f || _eprojY[i] > 200f)
                {
                    // Out of bounds — deactivate
                    _eprojActive[i] = false;
                    _activeEnemyProjectileCount--;
                    missedProjectiles++;
                }
            }

            // Apply collected damage (ping-pong)
            int readIdx = _damageQueueIdx;
            int writeIdx = 1 - _damageQueueIdx;
            _damageQueueIdx = writeIdx;
            _damageQueue[writeIdx].Clear();
            foreach (var (playerId, damage) in _damageQueue[readIdx])
            {
                store.DecreasePlayerHealth(playerId, damage);
            }
            _damageQueue[readIdx].Clear();
        }

        private void ResolveHitPlayer(int projId)
        {
            int playerId = 1; // default player
            float damage = _eprojDamage[projId];

            lock (_damageQueueLock)
            {
                _damageQueue[_damageQueueIdx].Add((playerId, damage));
            }
        }

        /// <summary>
        /// Called by PointDefenseSystem to deactivate a projectile (it was intercepted).
        /// </summary>
        public void DestroyProjectile(int projId)
        {
            if (projId < 0 || projId >= MAX_ENEMY_PROJ) return;
            if (_eprojActive[projId])
            {
                _eprojActive[projId] = false;
                _activeEnemyProjectileCount--;
            }
        }

        /// <summary>
        /// Returns count of active enemy projectiles (for debugging/monitoring).
        /// </summary>
        public int GetActiveCount() => _activeEnemyProjectileCount;

        /// <summary>
        /// Get IDs of active projectiles within range of a given world position.
        /// Used by PointDefenseSystem for intercept queries.
        /// </summary>
        public void GetProjectilesInRange(float cx, float cy, int rangeSq, List<int> result)
        {
            result.Clear();
            for (int i = 0; i < MAX_ENEMY_PROJ; i++)
            {
                if (!_eprojActive[i]) continue;
                float dx = _eprojX[i] - cx;
                float dy = _eprojY[i] - cy;
                int distSq = (int)(dx * dx + dy * dy);
                if (distSq <= rangeSq)
                    result.Add(i);
            }
        }
    }
}
