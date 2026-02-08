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
        private Core.ComponentStore store;
        private IRenderer renderer;
        private int playerId;

        public UpgradeSystem(Core.ComponentStore store, IRenderer renderer, int playerId)
        {
            this.store = store;
            this.renderer = renderer;
            this.playerId = playerId;
        }

        public void Update()
        {
            float gold = store.GetPlayerGold(playerId);
            float threshold = store.GetPlayerUpgradeThreshold(playerId);

            if (gold >= threshold)
            {
                ProcessUpgrade();
            }
            else
            {
                renderer.Log($"[UPGRADE] Current gold: {gold:F1} / {threshold:F1} (next upgrade)");
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
            string[] buffs = { "Attack+10%", "Defense+10%", "Attack Speed+20%", "Crit Rate+5%", "Health+20%" };
            int randomIndex = new Random().Next(buffs.Length);
            string newBuff = buffs[randomIndex];

            var playerBuffs = store.GetPlayerBuffs(playerId);
            if (!playerBuffs.Contains(newBuff))
            {
                store.AddPlayerBuff(playerId, newBuff);
                Console.WriteLine($"[BUFF] Gained new buff: {newBuff}!");
            }
        }
    }
}
