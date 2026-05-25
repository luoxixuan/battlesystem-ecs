using System;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Tower merge/fusion system - allows players to combine two same-type towers
    /// into a higher-tier tower with increased stats.
    /// </summary>
    public class TowerMergeSystem
    {
        private readonly ComponentStore store;
        private readonly IRenderer logger;
        private readonly GameConfig config;

        // Minimum combined level sum for fusion to be allowed
        private const int MIN_LEVEL_SUM = 3;

        public TowerMergeSystem(ComponentStore store, IRenderer logger, GameConfig config)
        {
            this.store = store;
            this.logger = logger;
            this.config = config;
        }

        /// <summary>
        /// Attempt to merge two towers into one higher-tier tower.
        /// The merged tower replaces tower1; tower2 is destroyed.
        /// </summary>
        /// <param name="tower1Id">First tower (will become the merged result)</param>
        /// <param name="tower2Id">Second tower (will be destroyed)</param>
        /// <returns>True if merge succeeded, false otherwise</returns>
        public bool MergeTowers(int tower1Id, int tower2Id)
        {
            // Validate both towers are active
            if (tower1Id < 0 || tower1Id >= ComponentStore.MAX_ENTITIES || !store.TowerActive[tower1Id])
            {
                logger.Log($"[MERGE] Merge failed: tower1 ({tower1Id}) is not a valid active tower");
                return false;
            }
            if (tower2Id < 0 || tower2Id >= ComponentStore.MAX_ENTITIES || !store.TowerActive[tower2Id])
            {
                logger.Log($"[MERGE] Merge failed: tower2 ({tower2Id}) is not a valid active tower");
                return false;
            }
            if (tower1Id == tower2Id)
            {
                logger.Log($"[MERGE] Merge failed: cannot merge a tower with itself");
                return false;
            }

            // Check same type
            string type1 = store.TowerType[tower1Id];
            string type2 = store.TowerType[tower2Id];
            if (type1 != type2)
            {
                logger.Log($"[MERGE] Merge failed: towers must be the same type (got {type1} and {type2})");
                return false;
            }

            // Check level sum threshold
            int level1 = store.TowerLevel[tower1Id];
            int level2 = store.TowerLevel[tower2Id];
            int levelSum = level1 + level2;
            if (levelSum < MIN_LEVEL_SUM)
            {
                logger.Log($"[MERGE] Merge failed: combined level sum {levelSum} < required {MIN_LEVEL_SUM}");
                return false;
            }

            // Fuse: tower1 gets combined level, tower2 is destroyed
            int newLevel = levelSum;
            float newDamage = store.TowerAttackDamage[tower1Id] + store.TowerAttackDamage[tower2Id];
            int newRange = Math.Max(store.TowerRange[tower1Id], store.TowerRange[tower2Id]);
            float newSpeed = (store.TowerAttackSpeed[tower1Id] + store.TowerAttackSpeed[tower2Id]) * 0.5f;

            // Update tower1 with fused stats
            store.TowerLevel[tower1Id] = newLevel;
            store.TowerAttackDamage[tower1Id] = newDamage;
            store.TowerRange[tower1Id] = newRange;
            store.TowerAttackSpeed[tower1Id] = newSpeed;
            store.TowerFusionTier[tower1Id] += 1;

            logger.Log($"[MERGE] Tower {type1} merged: Lv.{newLevel} (Tier {store.TowerFusionTier[tower1Id]}), " +
                       $"damage={newDamage:F1}, range={newRange}, speed={newSpeed:F2}");

            // Destroy tower2
            store.DestroyEntity(tower2Id);
            return true;
        }
    }
}