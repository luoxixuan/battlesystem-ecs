using System;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 塔升级系统 - 负责处理防御塔的升级逻辑与金币消耗
    /// </summary>
    public class TowerUpgradeSystem
    {
        private ComponentStore store;
        private IRenderer logger;

        public TowerUpgradeSystem(ComponentStore store, IRenderer logger)
        {
            this.store = store;
            this.logger = logger;
        }

        /// <summary>
        /// 尝试升级指定的塔
        /// </summary>
        public bool UpgradeTower(int towerId)
        {
            if (towerId < 0 || towerId >= 100000 || !store.TowerActive[towerId])
            {
                logger.Log($"[UPGRADE] 升级失败: 实体 {towerId} 不是激活的防御塔");
                return false;
            }

            int playerId = store.PlayerEntityId;
            float currentGold = store.GetPlayerGold(playerId);
            float upgradeCost = store.TowerUpgradeCost[towerId];

            // 1. 检查金币是否足够
            if (currentGold < upgradeCost)
            {
                logger.Log($"[UPGRADE] 升级失败: 金币不足 (当前: {currentGold}, 需要: {upgradeCost})");
                return false;
            }

            // 2. 扣除金币
            store.SetPlayerGold(playerId, currentGold - upgradeCost);

            // 3. 提升属性 (升级逻辑)
            int oldLevel = store.TowerLevel[towerId];
            store.TowerLevel[towerId]++;
            
            // 属性提升：攻击力+20%，射程+1，成本增加50%
            store.TowerAttackDamage[towerId] *= 1.2f;
            store.TowerRange[towerId] += 1;
            store.TowerUpgradeCost[towerId] *= 1.5f;

            logger.Log($"[UPGRADE] 塔 {towerId} 升级成功! Lv.{oldLevel} -> Lv.{store.TowerLevel[towerId]}");
            logger.Log($"[UPGRADE] 新属性 -> 攻击力: {store.TowerAttackDamage[towerId]:F1}, 射程: {store.TowerRange[towerId]}, 下次升级成本: {store.TowerUpgradeCost[towerId]:F1}");
            logger.Log($"[UPGRADE] 剩余金币: {store.GetPlayerGold(playerId):F1}");

            return true;
        }
    }
}
