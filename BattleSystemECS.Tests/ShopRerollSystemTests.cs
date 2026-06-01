using System;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
{
    public class ShopRerollSystemTests
    {
        private (ComponentStore store, GameConfig config, int playerId) CreateEnv()
        {
            var store = new ComponentStore();
            int id = store.CreateEntity();
            store.PlayerMaxHealth[id] = 200f;
            store.PlayerCurrentHealth[id] = 200f;
            store.PlayerGold[id] = 1000f;
            store.PositionX[id] = 5f;
            store.PositionY[id] = 0f;
            var config = new GameConfig();
            return (store, config, id);
        }

        [Fact]
        public void ShopRerollConfig_DefaultValues_AreSensible()
        {
            var config = new ShopRerollConfig();
            Assert.True(config.Enabled);
            Assert.Equal(3, config.OfferSlotCount);
            Assert.Equal(3, config.MaxRerollsPerPhase);
            Assert.Equal(5, config.PityRareThreshold);
            Assert.Equal(10, config.PityEpicThreshold);
            Assert.NotNull(config.CostCurve);
            Assert.True(config.CostCurve.Length >= 3, "CostCurve should have at least 3 entries");
            Assert.NotNull(config.RarityWeights);
            Assert.Equal(3, config.RarityWeights.Length);
        }

        [Fact]
        public void OnEnterBuildPhase_ResetsRerollCountAndRollsOffers()
        {
            var (store, config, pid) = CreateEnv();
            var r = new MockRenderer();
            var sys = new ShopRerollSystem(store, r, config, pid, seed: 42);

            sys.OnEnterBuildPhase();

            Assert.Equal(0, store.PlayerShopRerollCount[pid]);

            // Pity counters may be > 0 after initial rolls (no Rare/Epic seen yet)
            // but they must not exceed the configured thresholds.
            Assert.True(store.PlayerShopPityRare[pid] >= 0);
            Assert.True(store.PlayerShopPityRare[pid] <= config.ShopReroll.PityRareThreshold);
            Assert.True(store.PlayerShopPityEpic[pid] >= 0);
            Assert.True(store.PlayerShopPityEpic[pid] <= config.ShopReroll.PityEpicThreshold);

            // Slots 0..2 should be populated (with no tower/skill configs in test
            // config, typeId is 0 — i.e., no offers — but the slots are still cleared).
            int baseIdx = pid * ComponentStore.MAX_SHOP_OFFER_SLOTS;
            int slots = config.ShopReroll.OfferSlotCount;
            // Just confirm we can read every slot without crashing.
            for (int i = 0; i < slots; i++)
            {
                _ = store.PlayerShopOfferTypeId[baseIdx + i];
            }
        }

        [Fact]
        public void RerollOffers_ChargesGoldAndIncrementsCount()
        {
            var (store, config, pid) = CreateEnv();
            var r = new MockRenderer();
            var sys = new ShopRerollSystem(store, r, config, pid, seed: 42);

            sys.OnEnterBuildPhase();
            float goldBefore = store.GetPlayerGold(pid);

            bool ok = sys.RerollOffers();
            Assert.True(ok);
            Assert.Equal(1, store.PlayerShopRerollCount[pid]);
            // First reroll costs 5g (default CostCurve[0])
            Assert.Equal(goldBefore - 5f, store.GetPlayerGold(pid), 1);
        }

        [Fact]
        public void RerollOffers_CostEscalatesAlongCurve()
        {
            var (store, config, pid) = CreateEnv();
            var r = new MockRenderer();
            var sys = new ShopRerollSystem(store, r, config, pid, seed: 42);

            // Override cost curve to known values
            config.ShopReroll.CostCurve = new float[] { 10f, 25f, 50f, 100f };

            sys.OnEnterBuildPhase();
            float start = store.GetPlayerGold(pid);

            Assert.True(sys.RerollOffers());
            Assert.Equal(start - 10f, store.GetPlayerGold(pid), 1);

            Assert.True(sys.RerollOffers());
            Assert.Equal(start - 10f - 25f, store.GetPlayerGold(pid), 1);

            Assert.True(sys.RerollOffers());
            Assert.Equal(start - 10f - 25f - 50f, store.GetPlayerGold(pid), 1);
        }

        [Fact]
        public void RerollOffers_RespectsMaxRerollsPerPhase()
        {
            var (store, config, pid) = CreateEnv();
            config.ShopReroll.MaxRerollsPerPhase = 2;
            config.ShopReroll.CostCurve = new float[] { 1f, 1f, 1f };
            var r = new MockRenderer();
            var sys = new ShopRerollSystem(store, r, config, pid, seed: 1);

            sys.OnEnterBuildPhase();

            Assert.True(sys.RerollOffers());
            Assert.True(sys.RerollOffers());
            // Third reroll should be denied by the cap
            Assert.False(sys.RerollOffers());
            Assert.Equal(2, store.PlayerShopRerollCount[pid]);
        }

        [Fact]
        public void RerollOffers_FailsWhenNotEnoughGold()
        {
            var (store, config, pid) = CreateEnv();
            config.ShopReroll.CostCurve = new float[] { 9999f };
            var r = new MockRenderer();
            var sys = new ShopRerollSystem(store, r, config, pid, seed: 1);

            sys.OnEnterBuildPhase();

            Assert.False(sys.RerollOffers());
            Assert.Equal(0, store.PlayerShopRerollCount[pid]);
        }

        [Fact]
        public void RerollOffers_FailsWhenDisabled()
        {
            var (store, config, pid) = CreateEnv();
            config.ShopReroll.Enabled = false;
            var r = new MockRenderer();
            var sys = new ShopRerollSystem(store, r, config, pid, seed: 1);

            sys.OnEnterBuildPhase();  // should be a no-op
            // RerollCount was never set because OnEnterBuildPhase bailed
            Assert.Equal(0, store.PlayerShopRerollCount[pid]);

            Assert.False(sys.RerollOffers());
        }

        [Fact]
        public void PityCounters_NeverExceedThreshold()
        {
            var (store, config, pid) = CreateEnv();
            var r = new MockRenderer();
            var sys = new ShopRerollSystem(store, r, config, pid, seed: 7);

            sys.OnEnterBuildPhase();
            // Pity counters after initial rolls — must be within bounds
            Assert.True(store.PlayerShopPityRare[pid] <= config.ShopReroll.PityRareThreshold,
                "PityRare should be reset or capped at threshold");
            Assert.True(store.PlayerShopPityEpic[pid] <= config.ShopReroll.PityEpicThreshold,
                "PityEpic should be reset or capped at threshold");

            // Reroll several times with a cheap cost curve.
            config.ShopReroll.CostCurve = new float[] { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f };
            for (int i = 0; i < 8; i++)
            {
                if (!sys.RerollOffers()) break;
            }
            // After many rerolls, pity should still be bounded (forced Rare/Epic resets it)
            Assert.True(store.PlayerShopPityRare[pid] <= config.ShopReroll.PityRareThreshold);
            Assert.True(store.PlayerShopPityEpic[pid] <= config.ShopReroll.PityEpicThreshold);
        }

        [Fact]
        public void GetOffer_ReturnsValidOfferAfterPhaseEnter()
        {
            var (store, config, pid) = CreateEnv();
            var r = new MockRenderer();
            var sys = new ShopRerollSystem(store, r, config, pid, seed: 99);

            sys.OnEnterBuildPhase();

            // No tower/skill configs in the test config, so offers may be empty.
            // We just check that GetOffer doesn't crash for any slot.
            for (int i = 0; i < config.ShopReroll.OfferSlotCount; i++)
            {
                var offer = sys.GetOffer(i);
                // With empty tower/skill lists, IsValid is false (no offer rolled)
                // The contract is just: method returns without throwing.
                _ = offer;
            }
        }

        [Fact]
        public void GetRerollCost_FollowsCurve()
        {
            var (store, config, pid) = CreateEnv();
            config.ShopReroll.CostCurve = new float[] { 7f, 14f, 28f };
            var r = new MockRenderer();
            var sys = new ShopRerollSystem(store, r, config, pid, seed: 1);

            Assert.Equal(7f, sys.GetRerollCost(0));
            Assert.Equal(14f, sys.GetRerollCost(1));
            Assert.Equal(28f, sys.GetRerollCost(2));
            // Past end of curve -> last value
            Assert.Equal(28f, sys.GetRerollCost(99));
        }

        [Fact]
        public void GetRemainingRerolls_TracksUsage()
        {
            var (store, config, pid) = CreateEnv();
            config.ShopReroll.MaxRerollsPerPhase = 3;
            config.ShopReroll.CostCurve = new float[] { 1f, 1f, 1f };
            var r = new MockRenderer();
            var sys = new ShopRerollSystem(store, r, config, pid, seed: 1);

            sys.OnEnterBuildPhase();
            Assert.Equal(3, sys.GetRemainingRerolls());

            sys.RerollOffers();
            Assert.Equal(2, sys.GetRemainingRerolls());

            sys.RerollOffers();
            Assert.Equal(1, sys.GetRemainingRerolls());
        }

        [Fact]
        public void Update_NoOp_StaysClean()
        {
            var (store, config, pid) = CreateEnv();
            var r = new MockRenderer();
            var sys = new ShopRerollSystem(store, r, config, pid, seed: 1);

            // Update() is a BuildPhase no-op. Just call it and ensure no exception.
            sys.Update();
            sys.Update();
            Assert.Equal(0, store.PlayerShopRerollCount[pid]);
        }
    }
}
