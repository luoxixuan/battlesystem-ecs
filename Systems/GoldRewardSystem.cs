using System;
using BattleSystemECS.Components;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    public class GoldRewardSystem
    {
        private EntityManager entityManager;
        private IRenderer renderer;
        private int playerId;

        public GoldRewardSystem(EntityManager entityManager, IRenderer renderer, int playerId)
        {
            this.entityManager = entityManager;
            this.renderer = renderer;
            this.playerId = playerId;
        }

        public void Update()
        {
            var gold = entityManager.GetComponent<GoldComponent>(new Entity(playerId));
            var upgrade = entityManager.GetComponent<UpgradeComponent>(new Entity(playerId));

            if (gold.Amount >= upgrade.NextUpgradeThreshold)
            {
                renderer.Log("[GOLD] Gold threshold reached: " + gold.Amount + " / " + upgrade.NextUpgradeThreshold);
            }
            else
            {
                renderer.Log("[GOLD] Current gold: " + gold.Amount + " / " + upgrade.NextUpgradeThreshold + " (next upgrade)");
            }
        }
    }
}
