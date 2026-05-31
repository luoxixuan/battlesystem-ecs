using System;
using System.Collections.Generic;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 动态路径封锁系统 — Path Block System
    /// 管理可被敌人攻击破坏的路障/障碍物，放置在路径上以阻挡/改变敌人行进。
    /// 使用 ComponentStore 已有的 Obstacle 数组存储，提供放置、自动耗损、移除逻辑。
    /// 
    /// 帧序：Movement 阶段 Pathfinding 之后 EnemyMovement 之前
    /// </summary>
    public class PathBlockSystem
    {
        private ComponentStore store;

        // 默认路障属性（可覆盖）
        private const float DEFAULT_BLOCK_HP = 50f;
        private const float DEFAULT_ENEMY_DAMAGE = 10f; // 敌人对路障的默认伤害

        public PathBlockSystem(ComponentStore store)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>
        /// 放置一个路障到地图上。
        /// 使用 ComponentStore.AddObstacle 分配 slot。
        /// </summary>
        /// <param name="x">X 坐标（整数格点）</param>
        /// <param name="y">Y 坐标（整数格点）</param>
        /// <param name="maxHealth">路障最大生命值</param>
        /// <param name="typeId">路障类型（默认0 = 标准墙体）</param>
        /// <returns>路障 ID，-1 表示失败（slot 耗尽）</returns>
        public int PlaceBlock(float x, float y, float maxHealth = DEFAULT_BLOCK_HP, int typeId = 0)
        {
            // 找可用 obstacle slot
            int obstacleId = -1;
            for (int i = 0; i < ComponentStore.MAX_OBSTACLES; i++)
            {
                if (!store.ObstacleActive[i])
                {
                    obstacleId = i;
                    break;
                }
            }

            if (obstacleId < 0) return -1;

            store.AddObstacle(obstacleId, typeId, x, y, maxHealth);
            return obstacleId;
        }

        /// <summary>
        /// 移除路障（玩家拆除或敌人破坏后触发）。
        /// </summary>
        public void RemoveBlock(int obstacleId)
        {
            if (obstacleId < 0 || obstacleId >= ComponentStore.MAX_OBSTACLES) return;
            store.RemoveObstacle(obstacleId);
        }

        /// <summary>
        /// 查询某坐标是否存在路障。
        /// </summary>
        public bool IsBlocked(float x, float y)
        {
            var activeIds = store.ActiveObstacleIds;
            if (activeIds == null) return false;

            int ix = (int)x;
            int iy = (int)y;

            foreach (int oid in activeIds)
            {
                if (!store.ObstacleActive[oid]) continue;
                int ox = (int)store.ObstacleX[oid];
                int oy = (int)store.ObstacleY[oid];
                if (ox == ix && oy == iy)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 每帧更新：检查敌人是否接触到路障。
        /// 如果敌人与路障在同一格，敌人停止移动并对路障造成伤害（基于 EnemyDamage）。
        /// 路障 HP 耗尽后自动销毁。
        /// </summary>
        public void Update()
        {
            var activeEnemyIds = store.GetCachedActiveEnemyIds();
            if (activeEnemyIds == null || activeEnemyIds.Count == 0) return;

            var activeObstacleIds = store.ActiveObstacleIds;
            if (activeObstacleIds == null || activeObstacleIds.Count == 0) return;

            // 构建一个 (x,y) → obstacleId 的字典方便敌我查找
            // 因为 MAX_OBSTACLES 可能有 5000，但活跃路障通常很少，O(n*m) 是可以接受的
            foreach (int oid in activeObstacleIds)
            {
                if (!store.ObstacleActive[oid]) continue;

                float ox = store.ObstacleX[oid];
                float oy = store.ObstacleY[oid];

                // 检查是否有敌人在路障所在的整数格
                foreach (int eid in activeEnemyIds)
                {
                    if (!store.EnemyActive[eid]) continue;

                    float ex = store.PositionX[eid];
                    float ey = store.PositionY[eid];

                    // 敌人整数格坐标 == 路障整数格坐标
                    if ((int)ex == (int)ox && (int)ey == (int)oy)
                    {
                        // 敌人攻击路障
                        float enemyDmg = store.EnemyDamage[eid];
                        if (enemyDmg <= 0f) enemyDmg = DEFAULT_ENEMY_DAMAGE;

                        store.ObstacleHealth[oid] -= enemyDmg;

                        // 检查路障是否被摧毁
                        if (store.ObstacleHealth[oid] <= 0f)
                        {
                            store.RemoveObstacle(oid);
                        }

                        break; // 一个路障每帧只被一个敌人攻击
                    }
                }
            }
        }
    }
}
