using System;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    public class GoldRewardSystem
    {
        private EntityManager em;
        private IRenderer renderer;
        private Entity playerEntity;

        public GoldRewardSystem(EntityManager entityManager, IRenderer renderer, int playerId)
        {
            this.em = entityManager;
            this.renderer = renderer;
            this.playerEntity = new Entity(playerId);
        }

        public void Update()
        {
            if (!em.HasComponent<GoldComponent>(playerEntity))
                return;

            var gold = em.GetComponent<GoldComponent>(playerEntity);
            var upgrade = em.GetComponent<UpgradeComponent>(playerEntity);

            if (gold.Amount >= upgrade.NextUpgradeThreshold)
            {
                renderer.Log("[GOLD] Gold threshold reached: " + gold.Amount + " / " + upgrade.NextUpgradeThreshold);
            }
            else
            {
                renderer.Log("[UPGRADE] Current gold: " + gold.Amount + " / " + upgrade.NextUpgradeThreshold + " (next upgrade)");
            }
        }
    }
}
