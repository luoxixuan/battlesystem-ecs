using System;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Tower upgrade system - handles tower upgrade logic and special ability application.
    /// Upgrade curves driven by GameConfig.TowerUpgradePaths. Special abilities (armor pierce,
    /// splash damage, critical strike, chain lightning, freeze AOE) applied from upgrade config.
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
        /// Try to upgrade the specified tower.
        /// </summary>
        public bool UpgradeTower(int towerId)
        {
            if (towerId < 0 || towerId >= ComponentStore.MAX_ENTITIES || !store.TowerActive[towerId])
            {
                logger.Log($"[UPGRADE] 升级失败: 实体 {towerId} 不是激活的防御塔");
                return false;
            }

            int playerId = store.PlayerEntityId;
            float currentGold = store.GetPlayerGold(playerId);
            float upgradeCost = store.TowerUpgradeCost[towerId];

            // 1. Check gold
            if (currentGold < upgradeCost)
            {
                logger.Log($"[UPGRADE] 升级失败: 金币不足 (当前: {currentGold}, 需要: {upgradeCost})");
                return false;
            }

            // 2. Deduct gold
            store.SetPlayerGold(playerId, currentGold - upgradeCost);

            // 2b. Track total upgrade spend for salvage refund (Round 85 direction 4)
            store.TowerTotalUpgradeSpent[towerId] += upgradeCost;

            // 3. Apply upgrade curve
            int oldLevel = store.TowerLevel[towerId];
            int newLevel = oldLevel + 1;
            store.TowerLevel[towerId] = newLevel;

            // Get upgrade path (default "standard")
            string upgradePathId = store.TowerUpgradePathId[towerId];
            if (string.IsNullOrEmpty(upgradePathId))
                upgradePathId = "standard";

            var levelCfg = config.GetUpgradeLevelConfig(upgradePathId, newLevel);

            if (levelCfg != null)
            {
                // Apply config multipliers
                store.TowerAttackDamage[towerId] *= levelCfg.DamageMultiplier;
                store.TowerRange[towerId] += (int)levelCfg.RangeAdd;
                if (levelCfg.AttackSpeedMultiplier != 1.0f)
                    store.TowerAttackSpeed[towerId] *= levelCfg.AttackSpeedMultiplier;
                store.TowerUpgradeCost[towerId] *= levelCfg.CostMultiplier;

                // Apply special ability from upgrade config
                ApplySpecialAbility(towerId, levelCfg.SpecialAbility, levelCfg.SpecialAbilityParam);
            }
            else
            {
                // Fallback: original hardcoded logic
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

        /// <summary>
        /// Switch the tower's upgrade path to a different one.
        /// Re-applies the current upgrade level's curve from the new path (without changing level).
        /// Extra cost: +50% of current upgrade cost.
        /// </summary>
        /// <returns>True if switch succeeded, false otherwise.</returns>
        public bool SwitchUpgradePath(int towerId, string newPathId)
        {
            if (towerId < 0 || towerId >= ComponentStore.MAX_ENTITIES || !store.TowerActive[towerId])
            {
                logger.Log($"[UPGRADE] 路径切换失败: 实体 {towerId} 不是激活的防御塔");
                return false;
            }

            string currentPath = store.TowerUpgradePathId[towerId];
            if (string.IsNullOrEmpty(currentPath)) currentPath = "standard";

            // Validate the new path exists
            if (!config.TowerUpgradePaths.ContainsKey(newPathId))
            {
                logger.Log($"[UPGRADE] 路径切换失败: 未知路径 '{newPathId}'");
                return false;
            }

            // No-op if same path
            if (currentPath == newPathId)
            {
                logger.Log($"[UPGRADE] 塔 {towerId} 已在路径 '{newPathId}' 上，无需切换");
                return true;
            }

            int playerId = store.PlayerEntityId;
            float currentGold = store.GetPlayerGold(playerId);
            float switchCost = store.TowerUpgradeCost[towerId] * 0.5f; // +50%

            if (currentGold < switchCost)
            {
                logger.Log($"[UPGRADE] 路径切换失败: 金币不足 (当前: {currentGold:F0}, 需要: {switchCost:F0})");
                return false;
            }

            // Deduct cost
            store.SetPlayerGold(playerId, currentGold - switchCost);

            // Track switch cost for salvage refund (Round 85 direction 4)
            store.TowerTotalUpgradeSpent[towerId] += switchCost;

            // Record current level
            int level = store.TowerLevel[towerId];

            // Switch path
            store.TowerUpgradePathId[towerId] = newPathId;
            logger.Log($"[UPGRADE] 塔 {towerId} 切换路径: '{currentPath}' -> '{newPathId}' (Lv.{level}, 消耗 {switchCost:F0} 金)");

            // Re-apply current level's curve from the new path
            var levelCfg = config.GetUpgradeLevelConfig(newPathId, level);
            if (levelCfg != null)
            {
                // Apply all multipliers from the new path's current level
                if (levelCfg.DamageMultiplier != 1.0f)
                    store.TowerAttackDamage[towerId] *= levelCfg.DamageMultiplier;
                if (levelCfg.RangeAdd != 0f)
                    store.TowerRange[towerId] += (int)levelCfg.RangeAdd;
                if (levelCfg.AttackSpeedMultiplier != 1.0f)
                    store.TowerAttackSpeed[towerId] *= levelCfg.AttackSpeedMultiplier;
                logger.Log($"[UPGRADE] 新路径 Lv.{level} 属性 -> 伤害: {store.TowerAttackDamage[towerId]:F1}, 射程: {store.TowerRange[towerId]}, 攻速: {store.TowerAttackSpeed[towerId]:F3}");
            }
            else
            {
                logger.Log($"[UPGRADE] 新路径 Lv.{level} 无特殊加成");
            }

            return true;
        }

        /// <summary>
        /// Apply a special ability to the tower based on upgrade level config.
        /// </summary>
        private void ApplySpecialAbility(int towerId, TowerUpgradeAbility ability, float param)
        {
            switch (ability)
            {
                case TowerUpgradeAbility.ArmorPierce:
                    // param = armor pierce ratio (0-1), e.g. 0.5 = ignore 50% armor
                    store.TowerArmorPierceRatio[towerId] = Math.Max(0f, Math.Min(1f, param > 0f ? param : 0.5f));
                    logger.Log($"[UPGRADE] 获得护甲穿透: {store.TowerArmorPierceRatio[towerId]:P0}");
                    break;

                case TowerUpgradeAbility.SplashDamage:
                    // param = splash radius in tiles, default 1 (adjacent 3x3)
                    store.TowerSplashRadius[towerId] = param > 0f ? param : 1f;
                    logger.Log($"[UPGRADE] 获得范围伤害: 半径 {store.TowerSplashRadius[towerId]:F0} 格");
                    break;

                case TowerUpgradeAbility.CriticalStrike:
                    // param = crit chance (0-1) or crit multiplier if > 1
                    if (param > 1f)
                    {
                        // param is crit multiplier, use default 25% crit chance
                        store.TowerCritChance[towerId] = 0.25f;
                        store.TowerCritMultiplier[towerId] = param;
                    }
                    else
                    {
                        store.TowerCritChance[towerId] = Math.Max(0f, Math.Min(1f, param > 0f ? param : 0.20f));
                        store.TowerCritMultiplier[towerId] = 1.5f; // default 1.5x crit damage
                    }
                    logger.Log($"[UPGRADE] 获得暴击: {store.TowerCritChance[towerId]:P0} 几率, {store.TowerCritMultiplier[towerId]:F1}x 倍率");
                    break;

                case TowerUpgradeAbility.ChainLightning:
                    store.TowerHasChainLightning[towerId] = true;
                    logger.Log($"[UPGRADE] 获得链式闪电!");
                    break;

                case TowerUpgradeAbility.FreezeAoe:
                    store.TowerHasFreezeAoe[towerId] = true;
                    logger.Log($"[UPGRADE] 获得冰冻范围效果!");
                    break;
            }
        }
    }
}
