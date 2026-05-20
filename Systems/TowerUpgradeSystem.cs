using System;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 塔升级系统 - 负责处理防御塔的升级逻辑与金币消耗。
    /// 升级曲线由 GameConfig.TowerUpgradePaths 配置驱动，支持不同塔种差异化升级路径。
    /// </summary>
    public class TowerUpgradeSystem
    {
        private readonly ComponentStore store;
        private readonly IRenderer logger;
        private readonly GameConfig config;

        public TowerUpgradeSystem(ComponentStore store, IRenderer logger, GameConfig config)
        {
            this.store = store;
            this.logger = logger;
            this.config = config;
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

            // 3. 提升属性 — 使用配置驱动的升级曲线
            int oldLevel = store.TowerLevel[towerId];
            int newLevel = oldLevel + 1;
            store.TowerLevel[towerId] = newLevel;

            // 获取该塔的升级路径（默认 "standard"）
            string upgradePathId = store.TowerUpgradePathId[towerId];
            if (string.IsNullOrEmpty(upgradePathId))
                upgradePathId = "standard";

            var levelCfg = config.GetUpgradeLevelConfig(upgradePathId, newLevel);

            if (levelCfg != null)
            {
                // 应用配置乘数
                store.TowerAttackDamage[towerId] *= levelCfg.DamageMultiplier;
                store.TowerRange[towerId] += (int)levelCfg.RangeAdd;
                if (levelCfg.AttackSpeedMultiplier != 1.0f)
                    store.TowerAttackSpeed[towerId] *= levelCfg.AttackSpeedMultiplier;
                store.TowerUpgradeCost[towerId] *= levelCfg.CostMultiplier;
            }
            else
            {
                // Fallback: 原始硬编码逻辑（兼容无配置路径）
                store.TowerAttackDamage[towerId] *= 1.2f;
                store.TowerRange[towerId] += 1;
                store.TowerAttackSpeed[towerId] *= 1.0f;
                store.TowerUpgradeCost[towerId] *= 1.5f;
            }

            logger.Log($"[UPGRADE] 塔 {towerId} 升级成功! Lv.{oldLevel} -> Lv.{store.TowerLevel[towerId]} (path: {upgradePathId})");
            logger.Log($"[UPGRADE] 新属性 -> 攻击力: {store.TowerAttackDamage[towerId]:F1}, 射程: {store.TowerRange[towerId]}, 下次升级成本: {store.TowerUpgradeCost[towerId]:F1}");
            logger.Log($"[UPGRADE] 剩余金币: {store.GetPlayerGold(playerId):F1}");

            return true;
        }
    }
}
