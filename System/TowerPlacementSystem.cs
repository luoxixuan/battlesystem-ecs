using System;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 塔建造系统 - SOA (Struct of Arrays) 优化
    /// 管理塔的建造和放置
    /// </summary>
    public class TowerPlacementSystem
    {
        private Core.ComponentStore store;
        private IRenderer renderer;
        private GameConfig gameConfig;

        public TowerPlacementSystem(Core.ComponentStore store, IRenderer renderer, GameConfig gameConfig)
        {
            this.store = store;
            this.renderer = renderer;
            this.gameConfig = gameConfig;
        }

        /// <summary>
        /// 放置塔
        /// </summary>
        public bool PlaceTower(int x, int y, string towerType)
        {
            // 检查位置是否已被占用
            if (IsPositionOccupied(x, y))
            {
                renderer.Log($"[TOWER] 位置 ({x}, {y}) 已被占用，无法放置塔");
                return false;
            }

            // 检查玩家是否有足够金币
            float currentGold = store.GetPlayerGold(store.PlayerEntityId);
            var towerConfig = gameConfig.GetTowerConfig(towerType);
            if (towerConfig == null)
            {
                renderer.Log($"[TOWER] 塔类型 '{towerType}' 不存在");
                return false;
            }

            if (currentGold < towerConfig.Cost)
            {
                renderer.Log($"[TOWER] 金币不足，需要 {towerConfig.Cost} 金币，当前只有 {currentGold}");
                return false;
            }

            // 创建塔实体
            int towerId = store.NextEntityId;
            store.AddPosition(towerId, x, y);
            store.AddTower(towerId, towerType, towerConfig.Damage, towerConfig.Range, towerConfig.AttackSpeed, 1, towerConfig.Cost);
            store.SetEntityName(towerId, $"{towerType}T{x}Y{y}");

            // 扣除金币
            store.SetPlayerGold(store.PlayerEntityId, currentGold - towerConfig.Cost);

            renderer.Log($"[TOWER] 成功放置 {towerType} 在 ({x}, {y})，花费 {towerConfig.Cost} 金币");
            return true;
        }

        /// <summary>
        /// 升级塔
        /// </summary>
        public bool UpgradeTower(int towerId)
        {
            if (!store.IsTower(towerId))
            {
                renderer.Log($"[TOWER] 实体 {towerId} 不是塔");
                return false;
            }

            var towerConfig = gameConfig.GetTowerConfig(store.GetTowerType(towerId));
            if (towerConfig == null)
            {
                renderer.Log($"[TOWER] 塔类型 '{store.GetTowerType(towerId)}' 配置不存在");
                return false;
            }

            int currentLevel = store.GetTowerLevel(towerId);
            float upgradeCost = towerConfig.UpgradeCost * currentLevel;

            // 检查玩家是否有足够金币
            float currentGold = store.GetPlayerGold(store.PlayerEntityId);
            if (currentGold < upgradeCost)
            {
                renderer.Log($"[TOWER] 金币不足，升级需要 {upgradeCost} 金币，当前只有 {currentGold}");
                return false;
            }

            // 升级塔
            store.SetTowerLevel(towerId, currentLevel + 1);
            store.SetTowerAttackDamage(towerId, towerConfig.Damage * (currentLevel + 1));
            store.SetTowerAttackSpeed(towerId, towerConfig.AttackSpeed * (1 + currentLevel * 0.1f));
            store.SetTowerUpgradeCost(towerId, upgradeCost * 1.5f);

            // 扣除金币
            store.SetPlayerGold(store.PlayerEntityId, currentGold - upgradeCost);

            renderer.Log($"[TOWER] 塔升级到等级 {currentLevel + 1}，花费 {upgradeCost} 金币");
            return true;
        }

        /// <summary>
        /// 检查位置是否已被占用
        /// </summary>
        private bool IsPositionOccupied(int x, int y)
        {
            var allEntityIds = store.GetAllEntityIds();
            foreach (int entityId in allEntityIds)
            {
                if (store.PositionActive[entityId])
                {
                    float entityX = store.PositionX[entityId];
                    float entityY = store.PositionY[entityId];
                    if (Math.Abs(entityX - x) < 0.5f && Math.Abs(entityY - y) < 0.5f)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// 获取可建造位置
        /// </summary>
        public List<Vector2> GetAvailablePositions()
        {
            var availablePositions = new List<Vector2>();
            int mapWidth = 10;
            int mapHeight = 20;

            for (int x = 0; x < mapWidth; x++)
            {
                for (int y = 0; y < mapHeight; y++)
                {
                    if (!IsPositionOccupied(x, y))
                    {
                        availablePositions.Add(new Vector2(x, y));
                    }
                }
            }

            return availablePositions;
        }
    }
}