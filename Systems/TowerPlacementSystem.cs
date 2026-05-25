using System;
using System.IO;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Tower placement system - handles tower construction, selling, and selection on the map.
    /// </summary>
    public class TowerPlacementSystem
    {
        private ComponentStore store;
        private IRenderer logger;
        private GameConfig gameConfig;

        // Sell ratio: fraction of upgrade cost refunded (0.5 = 50%)
        private float sellRatio = 0.5f;
        private float minSellRatio = 0.3f;
        private float sellRatioDecreasePerLevel = 0.05f;

        public TowerPlacementSystem(ComponentStore store, IRenderer logger)
        {
            this.store = store;
            this.logger = logger;
            LoadSellConfig();
        }

        /// <summary>
        /// Overload accepting GameConfig so debuff fields can be looked up from TowerConfig.
        /// </summary>
        public TowerPlacementSystem(ComponentStore store, IRenderer logger, GameConfig gameConfig)
        {
            this.store = store;
            this.logger = logger;
            this.gameConfig = gameConfig;
            LoadSellConfig();
        }

        private void LoadSellConfig()
        {
            string basePath = AppDomain.CurrentDomain.BaseDirectory;
            string configPath = Path.Combine(basePath, "Data", "Configs", "tower_placement.json");
            if (File.Exists(configPath))
            {
                try
                {
                    string json = File.ReadAllText(configPath);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("sellRatio", out var sr)) sellRatio = sr.GetSingle();
                    if (root.TryGetProperty("minSellRatio", out var msr)) minSellRatio = msr.GetSingle();
                    if (root.TryGetProperty("sellRatioDecreasePerLevel", out var srdpl)) sellRatioDecreasePerLevel = srdpl.GetSingle();
                }
                catch { /* use defaults */ }
            }
        }

        /// <summary>
        /// Calculate the effective sell ratio for a given tower level.
        /// Ratio decreases per level but never drops below minSellRatio.
        /// </summary>
        private float GetEffectiveSellRatio(int towerLevel)
        {
            float ratio = sellRatio - (towerLevel - 1) * sellRatioDecreasePerLevel;
            return Math.Max(ratio, minSellRatio);
        }

        /// <summary>
        /// Place a tower at the specified location (legacy overload, no debuff support).
        /// </summary>
        public int PlaceTower(int x, int y, string type, float damage, int range, float speed, float cost)
        {
            // 1. Check if position is valid
            if (x < 0 || x >= 10 || y < 0 || y >= 20)
            {
                logger.Log("[TOWER] PlaceTower failed: position out of map range");
                return -1;
            }

            // 2. Check if position already has a tower
            foreach (int tid in store.ActiveTowerIds)
            {
                if (store.PositionX[tid] == x && store.PositionY[tid] == y)
                {
                    logger.Log($"[TOWER] PlaceTower failed: position ({x},{y}) already has a tower");
                    return -1;
                }
            }

            // 3. Create tower entity
            int towerId = store.CreateEntity();
            if (towerId == -1)
            {
                logger.Log("[TOWER] PlaceTower failed: entity creation failed (entity pool exhausted)");
                return -1;
            }

            store.AddPosition(towerId, x, y);
            // Try to look up debuff params from gameConfig if available
            if (gameConfig != null)
            {
                var tc = gameConfig.GetTowerConfig(type);
                if (tc != null)
                {
                    // Read tower's configured upgrade path, default to "standard"
                    string upgradePath = tc.UpgradePath;
                    if (string.IsNullOrEmpty(upgradePath)) upgradePath = "standard";
                    store.AddTower(towerId, type, damage, range, speed, 1, cost, upgradePath,
                        tc.StunChance, tc.SlowAmount, tc.SlowDuration);
                    // Apply tower targeting mode from config
                    store.SetTowerTargetingMode(towerId, tc.TargetingMode);
                    // Apply ammo system if configured (0 = unlimited)
                    if (tc.MaxAmmo > 0)
                    {
                        store.TowerMaxAmmo[towerId] = tc.MaxAmmo;
                        store.TowerCurrentAmmo[towerId] = tc.MaxAmmo;
                        store.TowerReloadTime[towerId] = tc.ReloadTime;
                        store.TowerIsReloading[towerId] = false;
                    }
                    // Apply tower's innate special ability (e.g., chain_lightning for Tesla)
                    if (tc.SpecialAbility != null)
                    {
                        ApplyTowerSpecialAbility(store, towerId, tc.SpecialAbility);
                        logger.Log($"[TOWER] {tc.Name} 固有能力: {tc.SpecialAbility.AbilityType}");
                    }
                }
                else
                {
                    store.AddTower(towerId, type, damage, range, speed, 1, cost);
                }
            }
            else
            {
                store.AddTower(towerId, type, damage, range, speed, 1, cost);
            }

            logger.Log($"[TOWER] {type} placed at ({x},{y})");
            logger.Log($"[TOWER] Tower placed: {type} at ({x},{y}), damage: {damage}, range: {range}, ID: {towerId}");
            return towerId;
        }

        private void ApplyTowerSpecialAbility(ComponentStore store, int towerId, TowerSpecialAbility ability)
        {
            if (ability == null || string.IsNullOrEmpty(ability.AbilityType)) return;

            // Store all ability parameters for TowerAttackSystem to read
            store.TowerSpecialAbilityRadius[towerId] = ability.Radius;
            store.TowerSpecialAbilityDamageMult[towerId] = ability.DamageMultiplier;
            store.TowerSpecialAbilityDotDamage[towerId] = ability.DotDamagePerTick;
            store.TowerSpecialAbilityDotInterval[towerId] = ability.DotTickInterval > 0f ? ability.DotTickInterval : 1f;

            switch (ability.AbilityType.ToLowerInvariant())
            {
                case "chain_lightning":
                    store.TowerHasChainLightning[towerId] = true;
                    break;
                case "freeze_aoe":
                    store.TowerHasFreezeAoe[towerId] = true;
                    break;
                case "splash":
                case "splash_damage":
                    store.TowerSplashRadius[towerId] = ability.Radius;
                    // Apply falloff if specified (default 1.0 = no falloff)
                    store.TowerFalloffInnerRatio[towerId] = ability.FalloffInnerRatio > 0f ? ability.FalloffInnerRatio : 1.0f;
                    store.TowerFalloffOuterMult[towerId] = ability.FalloffOuterMult > 0f ? ability.FalloffOuterMult : 1.0f;
                    break;
            }
        }

        /// <summary>
        /// Sell a single tower and refund a portion of its upgrade cost.
        /// The tower must be selected first.
        /// </summary>
        /// <returns>Gold refunded, or 0 if sell failed.</returns>
        public float SellTower(int towerId, int playerId = 1)
        {
            if (towerId < 0 || towerId >= ComponentStore.MAX_ENTITIES || !store.TowerActive[towerId])
            {
                logger.Log($"[TOWER] 出售失败: 实体 {towerId} 不是激活的防御塔");
                return 0f;
            }

            int level = store.TowerLevel[towerId];
            float sellGold = store.TowerUpgradeCost[towerId] * GetEffectiveSellRatio(level);
            int goldInt = (int)sellGold;

            // Refund gold to player
            float currentGold = store.GetPlayerGold(playerId);
            store.SetPlayerGold(playerId, currentGold + sellGold);

            // Destroy tower entity (handles ActiveTowerIds removal and state cleanup)
            store.DestroyEntity(towerId);

            logger.Log($"[TOWER] 出售塔 #{towerId} (Lv.{level})，返还 {goldInt} 金币");
            return sellGold;
        }

        /// <summary>
        /// Sell all currently selected towers in a batch.
        /// </summary>
        /// <returns>Total gold refunded.</returns>
        public float SellSelectedTowers(int playerId = 1)
        {
            int[] selected = store.GetSelectedTowerIds();
            if (selected.Length == 0)
            {
                logger.Log("[TOWER] 批量出售: 没有选中的塔");
                return 0f;
            }

            // Lock around ActiveTowerIds modifications for batch safety
            float totalRefunded = 0f;
            foreach (int tid in selected)
            {
                // SellTower internally calls DestroyEntity which locks activeIdsLock
                totalRefunded += SellTower(tid, playerId);
            }

            logger.Log($"[TOWER] 批量出售完成: {selected.Length} 塔，共返还 {(int)totalRefunded} 金币");
            return totalRefunded;
        }
    }
}
