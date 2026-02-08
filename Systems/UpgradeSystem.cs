using System;
using BattleSystemECS.Components;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    public class UpgradeSystem
    {
        private EntityManager entityManager;
        private IRenderer renderer;
        private int playerId;

        public UpgradeSystem(EntityManager entityManager, IRenderer renderer, int playerId)
        {
            this.entityManager = entityManager;
            this.renderer = renderer;
            this.playerId = playerId;
        }

        public void Update()
        {
            if (!entityManager.HasComponent<PlayerComponent>(new Entity(playerId)))
                return;

            if (!entityManager.HasComponent<GoldComponent>(new Entity(playerId)))
                return;

            if (!entityManager.HasComponent<UpgradeComponent>(new Entity(playerId)))
                return;

            var player = entityManager.GetComponent<PlayerComponent>(new Entity(playerId));
            var gold = entityManager.GetComponent<GoldComponent>(new Entity(playerId));
            var upgrade = entityManager.GetComponent<UpgradeComponent>(new Entity(playerId));

            if (gold.Amount >= upgrade.NextUpgradeThreshold)
            {
                ProcessUpgrade(player, gold, upgrade);
            }
            else
            {
                renderer.Log("[UPGRADE] Current gold: " + gold.Amount + " / " + upgrade.NextUpgradeThreshold + " (next upgrade)");
            }
        }

        private void ProcessUpgrade(PlayerComponent player, GoldComponent gold, UpgradeComponent upgrade)
        {
            player.CurrentLevel++;
            player.AttackDamage += 5f;
            player.AttackRange += 1f;
            upgrade.NextUpgradeThreshold *= 1.5f;

            entityManager.SetComponent(new Entity(playerId), player);
            entityManager.SetComponent(new Entity(playerId), upgrade);

            renderer.Log("[UPGRADE] Player upgraded to level " + player.CurrentLevel + "!");
            renderer.Log("[UPGRADE] Attack damage increased to " + player.AttackDamage);
            renderer.Log("[UPGRADE] Attack range increased to " + player.AttackRange + " grids");
            renderer.Log("[UPGRADE] Next upgrade needs " + upgrade.NextUpgradeThreshold + " gold");

            RandomlyGainBuff(upgrade);
        }

        private void RandomlyGainBuff(UpgradeComponent upgrade)
        {
            string[] buffs = { "Attack+10%", "Defense+10%", "Attack Speed+20%", "Crit Rate+5%", "Health+20%" };
            int randomIndex = new Random().Next(buffs.Length);
            string newBuff = buffs[randomIndex];

            if (!upgrade.Buffs.Contains(newBuff))
            {
                upgrade.Buffs.Add(newBuff);
                Console.WriteLine("[BUFF] Gained new buff: " + newBuff + "!");
            }
        }
    }
}
