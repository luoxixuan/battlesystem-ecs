using System;
using BattleSystemECS.Config;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Tower Experience System — grants XP to towers on enemy kills and manages mastery level progression.
    /// Subscribes to ComponentStore.OnEnemyKilled (thread-safe, called during serial death resolution).
    /// </summary>
    public class TowerExperienceSystem
    {
        private ComponentStore store;
        private GameConfig config;

        public TowerExperienceSystem(ComponentStore store, GameConfig config)
        {
            this.store = store;
            this.config = config;
        }

        /// <summary>
        /// Called when an enemy is killed. Awards XP to the tower that scored the kill.
        /// Thread-safe: called from serial death resolution context in ResolveEnemiesKilledThisFrame.
        /// </summary>
        public void HandleEnemyKilled(int enemyId, int playerId, int towerId)
        {
            if (towerId < 0 || towerId >= ComponentStore.MAX_ENTITIES) return;
            if (!store.TowerActive[towerId]) return;

            var mastery = config.TowerMastery;
            float xpGain = mastery.XPPerKill;
            if (store.EnemyIsElite[enemyId])
                xpGain += mastery.XPPerEliteKill;

            store.TowerExperience[towerId] += xpGain;
            store.TowerKillCount[towerId]++;

            // Check for mastery level-up
            int currentLevel = store.TowerMasteryLevel[towerId];
            int maxLevel = mastery.Levels.Count;
            if (currentLevel >= maxLevel) return; // already at max

            // Scan upward from current level to find a new threshold exceeded
            for (int lvl = currentLevel; lvl < maxLevel; lvl++)
            {
                float threshold = mastery.Levels[lvl].XPThreshold;
                if (store.TowerExperience[towerId] >= threshold)
                {
                    store.TowerMasteryLevel[towerId] = mastery.Levels[lvl].Level;
                }
            }
        }

        /// <summary>
        /// Get the total damage multiplier for a tower from mastery bonuses.
        /// Called by TowerAttackSystem when computing final damage.
        /// </summary>
        public float GetMasteryDamageMultiplier(int towerId)
        {
            if (towerId < 0 || towerId >= ComponentStore.MAX_ENTITIES) return 1f;
            int level = store.TowerMasteryLevel[towerId];
            if (level <= 1) return 1f;
            var mastery = config.TowerMastery;
            if (level > mastery.Levels.Count) return 1f;
            return 1f + mastery.Levels[level - 1].DamageBonus;
        }

        /// <summary>
        /// Get the attack speed multiplier bonus for a tower from mastery.
        /// </summary>
        public float GetMasteryAttackSpeedBonus(int towerId)
        {
            if (towerId < 0 || towerId >= ComponentStore.MAX_ENTITIES) return 0f;
            int level = store.TowerMasteryLevel[towerId];
            if (level <= 1) return 0f;
            var mastery = config.TowerMastery;
            if (level > mastery.Levels.Count) return 0f;
            return mastery.Levels[level - 1].AttackSpeedBonus;
        }

        /// <summary>
        /// Get the range bonus (flat tiles) for a tower from mastery.
        /// </summary>
        public int GetMasteryRangeBonus(int towerId)
        {
            if (towerId < 0 || towerId >= ComponentStore.MAX_ENTITIES) return 0;
            int level = store.TowerMasteryLevel[towerId];
            if (level <= 1) return 0;
            var mastery = config.TowerMastery;
            if (level > mastery.Levels.Count) return 0;
            return mastery.Levels[level - 1].RangeBonus;
        }
    }
}