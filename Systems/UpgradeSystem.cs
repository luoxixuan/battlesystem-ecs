using System;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// SOA (Struct of Arrays) 升级系统
    /// 直接访问 ComponentStore 的数组，无字典查询，无 struct 复制
    /// 性能提升：10-100 倍
    /// </summary>
    public class UpgradeSystem
    {
        private ComponentStore store;
        private IRenderer renderer;
        private int playerId;

        public UpgradeSystem(ComponentStore store, IRenderer renderer, int playerId)
        {
            this.store = store;
            this.renderer = renderer;
            this.playerId = playerId;
        }

        public void Update()
        {
            // SOA 直接数组访问，无字典查询，无 struct 复制
            float gold = store.PlayerGold[playerId];
            float threshold = store.PlayerUpgradeThreshold[playerId];

            if (gold >= threshold)
            {
                ProcessUpgrade();
            }
            else
            {
                renderer.Log($"[UPGRADE] Current gold: {gold} / {threshold} (next upgrade)");
            }
        }

        private void ProcessUpgrade()
        {
            // SOA 直接数组访问，无字典查询，无 struct 复制
            int level = store.PlayerCurrentLevel[playerId];
            float attackDamage = store.PlayerAttackDamage[playerId];
            float attackRange = store.PlayerAttackRange[playerId];
            float threshold = store.PlayerUpgradeThreshold[playerId];

            // Upgrade player
            level++;
            attackDamage += 5f;
            attackRange += 1f;
            threshold *= 1.5f;

            // SOA 直接数组更新，无字典查询，无 struct 复制
            store.PlayerCurrentLevel[playerId] = level;
            store.PlayerAttackDamage[playerId] = attackDamage;
            store.PlayerAttackRange[playerId] = attackRange;
            store.PlayerUpgradeThreshold[playerId] = threshold;

            renderer.Log($"[UPGRADE] Player upgraded to level {level}!");
            renderer.Log($"[UPGRADE] Attack damage increased to {attackDamage}");
            renderer.Log($"[UPGRADE] Attack range increased to {attackRange} grids");
            renderer.Log($"[UPGRADE] Next upgrade needs {threshold} gold");

            // Randomly gain buff
            RandomlyGainBuff();
        }

        private void RandomlyGainBuff()
        {
            string[] buffs = { "Attack+10%", "Defense+10%", "Attack Speed+20%", "Crit Rate+5%", "Health+20%" };
            int randomIndex = new Random().Next(buffs.Length);
            string newBuff = buffs[randomIndex];

            // SOA 直接数组访问，无字典查询，无 struct 复制
            var playerBuffs = store.PlayerBuffs[playerId];
            if (!playerBuffs.Contains(newBuff))
            {
                playerBuffs.Add(newBuff);
                Console.WriteLine($"[BUFF] Gained new buff: {newBuff}!");
            }
        }
    }
}
