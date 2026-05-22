using System;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// SOA (Struct of Arrays) 玩家升级系统
    /// 直接访问 ComponentStore 的数组，无字典查询，无 struct 复制
    /// 性能提升：10-100 倍
    /// </summary>
    public class UpgradeSystem
    {
        private static readonly System.Random _sharedRandom = System.Random.Shared;

        private Core.ComponentStore store;
        private IRenderer renderer;
        private int playerId;
        private GameConfig gameConfig;
        private TowerUpgradeSystem towerUpgradeSystem;

        public UpgradeSystem(Core.ComponentStore store, IRenderer renderer, int playerId, GameConfig gameConfig)
        {
            this.store = store;
            this.renderer = renderer;
            this.playerId = playerId;
            this.gameConfig = gameConfig;
            this.towerUpgradeSystem = new TowerUpgradeSystem(store, renderer, gameConfig);
        }

        public void Update()
        {
            float gold = store.GetPlayerGold(playerId);
            float threshold = store.GetPlayerUpgradeThreshold(playerId);

            if (gold >= threshold)
            {
                ProcessUpgrade();
            }
        }

        private void ProcessUpgrade()
        {
            int level = store.GetPlayerLevel(playerId);
            float attackDamage = store.GetPlayerAttackDamage(playerId);
            float attackRange = store.GetPlayerAttackRange(playerId);
            float threshold = store.GetPlayerUpgradeThreshold(playerId);

            // Upgrade player
            level++;
            attackDamage += 5f;
            attackRange += 1f;
            threshold *= 1.5f;

            store.SetPlayerLevel(playerId, level);
            store.SetPlayerAttackDamage(playerId, attackDamage);
            store.SetPlayerAttackRange(playerId, attackRange);
            store.SetPlayerUpgradeThreshold(playerId, threshold);

            renderer.Log($"[UPGRADE] Player upgraded to level {level}!");
            renderer.Log($"[UPGRADE] Attack damage increased to {attackDamage}");
            renderer.Log($"[UPGRADE] Attack range increased to {attackRange} grids");
            renderer.Log($"[UPGRADE] Next upgrade needs {threshold:F1} gold");

            RandomlyGainBuff();
        }

        private void RandomlyGainBuff()
        {
            var buffs = gameConfig.GetUpgradeBuffs();
            if (buffs == null || buffs.Count == 0) return;
            int randomIndex = _sharedRandom.Next(buffs.Count);
            string newBuff = buffs[randomIndex];

            var playerBuffs = store.GetPlayerBuffs(playerId);
            if (!playerBuffs.Contains(newBuff))
            {
                store.AddPlayerBuff(playerId, newBuff);
                // Sync to bit flags for O(1) hot-path queries
                if (newBuff == "Attack+10%")
                    store.AddBuff(playerId, BuffType.AttackBoost);
                else if (newBuff == "Crit Rate+5%")
                    store.AddBuff(playerId, BuffType.CritRateBoost);
                else if (newBuff == "Defense+10%")
                    store.AddBuff(playerId, BuffType.DefenseBoost);
                Console.WriteLine($"[BUFF] Gained new buff: {newBuff}!");
            }
        }

        /// <summary>
        /// Upgrade the specified tower using TowerUpgradeSystem.
        /// Returns true if upgrade succeeded.
        /// </summary>
        public bool UpgradeTower(int towerId)
        {
            return towerUpgradeSystem.UpgradeTower(towerId);
        }
    }
}
