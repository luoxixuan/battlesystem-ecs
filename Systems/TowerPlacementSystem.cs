using System;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 塔建造系统 - 负责在地图上放置防御塔
    /// </summary>
    public class TowerPlacementSystem
    {
        private ComponentStore store;
        private IRenderer logger;

        public TowerPlacementSystem(ComponentStore store, IRenderer logger)
        {
            this.store = store;
            this.logger = logger;
        }

        /// <summary>
        /// 在指定位置建造塔
        /// </summary>
        public int PlaceTower(int x, int y, string type, float damage, int range, float speed, float cost)
        {
            // 1. 检查位置是否有效 (简单范围检查)
            if (x < 0 || x >= 10 || y < 0 || y >= 20)
            {
                logger.Log("[TOWER] 建造失败: 坐标超出地图范围");
                return -1;
            }

            // 2. 检查该位置是否已经有塔
            for (int i = 0; i < store.NextEntityId; i++)
            {
                if (i < store.TowerActive.Length && store.TowerActive[i] && store.PositionX[i] == x && store.PositionY[i] == y)
                {
                    logger.Log($"[TOWER] 建造失败: 坐标 ({x},{y}) 已有塔存在");
                    return -1;
                }
            }

            // 3. 创建塔实体（使用 CreateEntity 而不是 NextEntityId — Bug #4）
            int towerId = store.CreateEntity();

            store.AddPosition(towerId, x, y);
            store.AddTower(towerId, type, damage, range, speed, 1, cost);

            logger.Log($"[TOWER] 建造成功: {type} 塔于 ({x},{y}), 攻击力: {damage}, 射程: {range}, ID: {towerId}");
            return towerId;
        }
    }
}
