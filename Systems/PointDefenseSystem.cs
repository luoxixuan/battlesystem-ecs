using System;
using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 点防御系统 (Point Defense System) — 反制敌方飞行道具。
    /// 
    /// 工作原理：
    /// - 在 TowerAttackSystem 之前运行（Phase 5.5）
    /// - 通过 IEnemyProjectilePort 扫描范围内所有敌方弹道
    /// - 对每个敌方弹道，检测是否有 PointDefense 塔在射程内
    /// - 如果有，概率性击落（基于 PointDefense 塔的拦截率）
    /// - 通过 IEnemyProjectilePort 移除被击落的弹道
    /// 
    /// PointDefense 塔索敌模式 = Intercept，专门用于反制弹道而非攻击敌人。
    /// </summary>
    public class PointDefenseSystem
    {
        private readonly ComponentStore store;
        private readonly IRenderer logger;
        private global::BattleSystemECS.Content.Contracts.IEnemyProjectilePort enemyProjectileSystem;

        // 复用双缓冲队列收集拦截事件，串行通知 IEnemyProjectilePort 停用弹道。
        private List<int>[] _interceptQueue = new List<int>[2];
        private int _interceptQueueIdx = 0;

        public PointDefenseSystem(ComponentStore store, IRenderer logger)
        {
            this.store = store;
            this.logger = logger;
            _interceptQueue[0] = new List<int>(64);
            _interceptQueue[1] = new List<int>(64);
        }

        public void SetEnemyProjectileSystem(global::BattleSystemECS.Content.Contracts.IEnemyProjectilePort enemyProjectileSystem)
        {
            this.enemyProjectileSystem = enemyProjectileSystem;
        }

        public void SetTurn(int turn)
        {
            // nothing to cache per turn currently
        }

        /// <summary>
        /// Update: scan enemy projectiles and intercept those within point-defense tower range.
        /// Runs AFTER RebuildSpatialGrid so spatial queries are valid.
        /// Runs BEFORE TowerAttackSystem.Update so intercepts reduce incoming damage.
        /// </summary>
        public void Update(float deltaTime)
        {
            if (enemyProjectileSystem == null) return;

            var towerIds = store.ActiveTowerIds;
            for (int ti = 0; ti < towerIds.Count; ti++)
            {
                int towerId = towerIds[ti];
                // Only process PointDefense towers (TargetingMode == Intercept)
                if (store.TowerTargetingMode[towerId] != TowerTargetingMode.Intercept) continue;

                float tx = store.PositionX[towerId];
                float ty = store.PositionY[towerId];
                int range = store.TowerRange[towerId];
                int rangeSq = range * range;

                // Get intercept chance from tower config (0.0-1.0)
                float interceptRate = store.TowerInterceptRate[towerId];
                if (interceptRate <= 0f) interceptRate = 0.5f; // default 50%

                // Query spatial grid for enemy projectiles in range
                // IEnemyProjectilePort 返回世界坐标中的弹道位置。
                // We need to scan all active enemy projectiles in range
                // Since spatial grid tracks enemies not projectiles, we do a simple loop
                // for now (enemy projectiles are typically few — max 4096)
                ScanAndIntercept(towerId, tx, ty, rangeSq, interceptRate);
            }

            // 串行提交拦截结果，避免并行扫描阶段修改弹道状态。
            ResolveIntercepts();
        }

        private void ScanAndIntercept(int towerId, float tx, float ty, int rangeSq, float interceptRate)
        {
            // Scan enemy projectile slots — this is a simple linear scan of MAX_ENEMY_PROJ
            // In practice enemy projectiles are sparse and short-lived, so this is acceptable.
            // For future optimization: maintain a list of active enemy projectile IDs.
            var rng = store.Determinism;

            // 通过窄接口查询弹道，不读取 EnemyProjectileSystem 的内部数组。
            // For now we use a simplified approach: iterate all slots and check distance.
            // This will be optimized once we have the cross-system query API.
            // 
            // IEnemyProjectilePort 暴露受控的范围查询与停用操作。
            // GetActiveProjectileIds() and we check each one's distance from tower.
            // For this implementation, we'll add a public getter method.
            // 
            // 同进程实现仍必须遵守该接口边界。
            // read its arrays via a public accessor — but that breaks encapsulation.
            // 
            // 查询结果写入调用方提供的复用缓冲区。
            // Let's call it via the exposed system reference.
            var nearbyProjIds = new List<int>(64);
            enemyProjectileSystem.GetProjectilesInRange(tx, ty, rangeSq, nearbyProjIds);

            for (int pi = 0; pi < nearbyProjIds.Count; pi++)
            {
                int projId = nearbyProjIds[pi];
                // Roll intercept chance
                if (rng.NextDouble() < interceptRate)
                {
                    // Intercepted! Queue for destruction
                    lock (_interceptQueue)
                    {
                        _interceptQueue[_interceptQueueIdx].Add(projId);
                    }
                }
            }
        }

        private void ResolveIntercepts()
        {
            int readIdx = _interceptQueueIdx;
            int writeIdx = 1 - _interceptQueueIdx;
            _interceptQueueIdx = writeIdx;
            _interceptQueue[writeIdx].Clear();

            foreach (int projId in _interceptQueue[readIdx])
            {
                enemyProjectileSystem.DestroyProjectile(projId);
            }
        }
    }
}
