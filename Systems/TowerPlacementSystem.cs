using System;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Tower placement system - handles tower construction on the map.
    /// </summary>
    public class TowerPlacementSystem
    {
        private ComponentStore store;
        private IRenderer logger;
        private GameConfig gameConfig;

        public TowerPlacementSystem(ComponentStore store, IRenderer logger)
        {
            this.store = store;
            this.logger = logger;
        }

        /// <summary>
        /// Overload accepting GameConfig so debuff fields can be looked up from TowerConfig.
        /// </summary>
        public TowerPlacementSystem(ComponentStore store, IRenderer logger, GameConfig gameConfig)
        {
            this.store = store;
            this.logger = logger;
            this.gameConfig = gameConfig;
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
                    store.AddTower(towerId, type, damage, range, speed, 1, cost, "standard",
                        tc.StunChance, tc.SlowAmount, tc.SlowDuration);
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
                    break;
            }
        }
    }
}
