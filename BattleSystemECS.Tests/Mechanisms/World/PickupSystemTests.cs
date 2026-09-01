using Xunit;
using BattleSystemECS.Systems;
using BattleSystemECS.Config;
using BattleSystemECS.Tests.Infrastructure;

namespace BattleSystemECS.Tests.Mechanisms.World
{
    public class PickupSystemTests : BattleTestBase
    {
        [Fact]
        public void Update_WithNoActiveEnemies_DoesNotCollectPickup()
        {
            Config.PickupDefs = new[]
            {
                new PickupDef { Type = "GoldPile", Value = 10f, LifetimeSeconds = 30f, CollectRadius = 1.5f }
            };
            int playerId = Player();
            var pickup = new PickupSystem(Store, Config, Renderer);

            pickup.SpawnPickup(0, 0f, 0f, playerId);
            pickup.Update(0.1f);

            Assert.Equal(0f, Store.GetPlayerGold(playerId));
            Assert.True(Store.PickupActive[0]);
        }

        [Fact]
        public void Update_CollectsPickupThroughActiveEnemyList()
        {
            Config.PickupDefs = new[]
            {
                new PickupDef { Type = "GoldPile", Value = 10f, LifetimeSeconds = 30f, CollectRadius = 1.5f }
            };
            int playerId = Player();
            Enemy(e => { e.X = 1f; e.Y = 1f; });
            var pickup = new PickupSystem(Store, Config, Renderer);

            pickup.SpawnPickup(0, 1f, 1f, playerId);
            pickup.Update(0.1f);

            Assert.Equal(10f, Store.GetPlayerGold(playerId));
            Assert.False(Store.PickupActive[0]);
        }
    }
}
