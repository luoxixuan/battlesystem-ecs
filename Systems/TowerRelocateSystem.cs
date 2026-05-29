using System;
using System.IO;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Tower Relocation System — allows players to move already-placed towers to new positions
    /// without losing upgrade investment. Operates during BuildPhase only.
    /// 
    /// Relocation flow:
    ///   1. Validate new position is within map bounds
    ///   2. Validate new position is not occupied by another tower
    ///   3. Validate tower exists and is active
    ///   4. Deduct relocate cost from player gold
    ///   5. Update tower PositionX/PositionY
    /// </summary>
    public class TowerRelocateSystem
    {
        private ComponentStore store;
        private IRenderer logger;

        // Relocate cost configuration (loaded from tower_placement.json)
        private float baseRelocateCost = 50f;
        private float relocateCostDecreasePerLevel = 5f; // cost = base - (level-1)*decrease, min 20
        private float minRelocateCost = 20f;
        private bool allowInWavePhase = false; // whether relocation is allowed during combat

        public TowerRelocateSystem(ComponentStore store, IRenderer logger)
        {
            this.store = store;
            this.logger = logger;
            LoadConfig();
        }

        private void LoadConfig()
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
                    if (root.TryGetProperty("relocateBaseCost", out var brc)) baseRelocateCost = brc.GetSingle();
                    if (root.TryGetProperty("relocateCostDecreasePerLevel", out var rcdpl)) relocateCostDecreasePerLevel = rcdpl.GetSingle();
                    if (root.TryGetProperty("minRelocateCost", out var mrc)) minRelocateCost = mrc.GetSingle();
                    if (root.TryGetProperty("allowRelocateInWave", out var ariw)) allowInWavePhase = ariw.GetBoolean();
                }
                catch { /* use defaults */ }
            }
        }

        /// <summary>
        /// Calculate the relocate cost for a tower of a given level.
        /// Cost decreases with tower level (reflecting accumulated investment).
        /// </summary>
        private float GetRelocateCost(int towerLevel)
        {
            float cost = baseRelocateCost - (towerLevel - 1) * relocateCostDecreasePerLevel;
            return Math.Max(cost, minRelocateCost);
        }

        /// <summary>
        /// Check if a position is valid for tower relocation (within bounds and not occupied).
        /// </summary>
        public bool IsValidPosition(int x, int y)
        {
            // Check map bounds (using 10x50 from tower_placement.json)
            if (x < 0 || x >= 10 || y < 0 || y >= 50)
            {
                logger.Log($"[RELOCATE] Position ({x},{y}) out of map bounds");
                return false;
            }

            // Check if position is occupied by another tower
            foreach (int tid in store.ActiveTowerIds)
            {
                if ((int)store.PositionX[tid] == x && (int)store.PositionY[tid] == y)
                {
                    logger.Log($"[RELOCATE] Position ({x},{y}) is already occupied by tower #{tid}");
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Relocate a tower to a new position.
        /// </summary>
        /// <param name="towerId">Tower entity ID</param>
        /// <param name="newX">New X position</param>
        /// <param name="newY">New Y position</param>
        /// <param name="playerId">Player ID for gold deduction</param>
        /// <returns>True if relocation succeeded, false otherwise</returns>
        public bool RelocateTower(int towerId, int newX, int newY, int playerId = 1)
        {
            // Validate tower exists and is active
            if (towerId < 0 || towerId >= ComponentStore.MAX_ENTITIES || !store.TowerActive[towerId])
            {
                logger.Log($"[RELOCATE] 失败: 塔 #{towerId} 不存在或未激活");
                return false;
            }

            // Validate new position
            if (!IsValidPosition(newX, newY))
            {
                return false;
            }

            // Calculate and deduct relocate cost
            int level = store.TowerLevel[towerId];
            float cost = GetRelocateCost(level);
            float currentGold = store.GetPlayerGold(playerId);
            if (currentGold < cost)
            {
                logger.Log($"[RELOCATE] 金币不足: 需要 {cost}, 当前 {currentGold}");
                return false;
            }

            // Record old position for logging
            int oldX = (int)store.PositionX[towerId];
            int oldY = (int)store.PositionY[towerId];

            // Deduct gold
            store.SetPlayerGold(playerId, currentGold - cost);

            // Update position (reuse existing SetPosition which already validates bounds)
            store.SetPosition(towerId, newX, newY);

            logger.Log($"[RELOCATE] 塔 #{towerId} ({store.TowerType[towerId]}, Lv.{level}) 从 ({oldX},{oldY}) 移动到 ({newX},{newY})，花费 {cost} 金币");
            return true;
        }

        /// <summary>
        /// Update — called every frame during BuildPhase.
        /// Currently a no-op since relocation is explicitly triggered via RelocateTower().
        /// Reserved for future cooldown tracking or UI notification logic.
        /// </summary>
        public void Update()
        {
            // Future: relocate cooldown per tower, visual indicators, etc.
        }

        public void SetTurn(int turn)
        {
            // Future: per-tower relocate cooldown ticking
        }
    }
}