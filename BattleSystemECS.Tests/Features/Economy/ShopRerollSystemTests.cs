using BattleSystemECS.Tests.Infrastructure;
using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Features.Economy
{
    public class ShopRerollSystemTests : BattleTestBase
    {
        private (GameConfig config, int playerId) CreateEnv()
        {
            int id = Store.CreateEntity();
            Store.PlayerMaxHealth[id] = 200f;
            Store.PlayerCurrentHealth[id] = 200f;
            Store.PlayerGold[id] = 1000f;
            Store.PositionX[id] = 5f;
            Store.PositionY[id] = 0f;
            return (Config, id);
        }

        /// <summary>GetOffer 结构契约：有效 offer 必须能映射回注入配置池；无效 offer 的 typeId 必须为 0。</summary>
        private static void AssertOfferConsistentWithConfig(ShopOffer offer, GameConfig config)
        {
            if (!offer.IsValid)
            {
                Assert.Equal(0, offer.TypeId);
                return;
            }
            Assert.InRange(offer.RarityTier, 0, 2);
            if (offer.IsTower)
            {
                Assert.Contains(config.TowerTypes, t => (int)t.Type == offer.TypeId);
            }
            else
            {
                Assert.InRange(offer.TypeId, 1, config.Skills.Count);
            }
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
            var (config, pid) = CreateEnv();
            var sys = new ShopRerollSystem(Store, Renderer, config, pid, seed: 42);

            sys.OnEnterBuildPhase();

            Assert.Equal(0, Store.PlayerShopRerollCount[pid]);

            // Pity counters must stay within [0, threshold] after initial rolls.
            Assert.InRange(Store.PlayerShopPityRare[pid], 0, config.ShopReroll.PityRareThreshold);
            Assert.InRange(Store.PlayerShopPityEpic[pid], 0, config.ShopReroll.PityEpicThreshold);

            // 初始 roll 后每个槽位都必须满足 GetOffer 结构契约（有效→可映射，无效→typeId=0）。
            int slots = config.ShopReroll.OfferSlotCount;
            for (int i = 0; i < slots; i++)
            {
                AssertOfferConsistentWithConfig(sys.GetOffer(i), config);
            }
        }

        [Fact]
        public void RerollOffers_ChargesGoldAndIncrementsCount()
        {
            var (config, pid) = CreateEnv();
            var sys = new ShopRerollSystem(Store, Renderer, config, pid, seed: 42);

            sys.OnEnterBuildPhase();
            float goldBefore = Store.GetPlayerGold(pid);

            bool ok = sys.RerollOffers();
            Assert.True(ok);
            Assert.Equal(1, Store.PlayerShopRerollCount[pid]);
            // 首次 reroll 费用必须等于 CostCurve[0]（期望从读取的配置推导）。
            Assert.Equal(goldBefore - config.ShopReroll.CostCurve[0], Store.GetPlayerGold(pid), 1);
        }

        [Fact]
        public void RerollOffers_CostEscalatesAlongCurve()
        {
            var (config, pid) = CreateEnv();
            var sys = new ShopRerollSystem(Store, Renderer, config, pid, seed: 42);

            // Override cost curve to known values
            config.ShopReroll.CostCurve = new float[] { 10f, 25f, 50f, 100f };

            sys.OnEnterBuildPhase();
            float start = Store.GetPlayerGold(pid);

            Assert.True(sys.RerollOffers());
            Assert.Equal(start - 10f, Store.GetPlayerGold(pid), 1);

            Assert.True(sys.RerollOffers());
            Assert.Equal(start - 10f - 25f, Store.GetPlayerGold(pid), 1);

            Assert.True(sys.RerollOffers());
            Assert.Equal(start - 10f - 25f - 50f, Store.GetPlayerGold(pid), 1);
        }

        [Fact]
        public void RerollOffers_RespectsMaxRerollsPerPhase()
        {
            var (config, pid) = CreateEnv();
            config.ShopReroll.MaxRerollsPerPhase = 2;
            config.ShopReroll.CostCurve = new float[] { 1f, 1f, 1f };
            var sys = new ShopRerollSystem(Store, Renderer, config, pid, seed: 1);

            sys.OnEnterBuildPhase();

            Assert.True(sys.RerollOffers());
            Assert.True(sys.RerollOffers());
            // Third reroll should be denied by the cap
            Assert.False(sys.RerollOffers());
            Assert.Equal(2, Store.PlayerShopRerollCount[pid]);
        }

        [Fact]
        public void RerollOffers_FailsWhenNotEnoughGold()
        {
            var (config, pid) = CreateEnv();
            config.ShopReroll.CostCurve = new float[] { 9999f };
            var sys = new ShopRerollSystem(Store, Renderer, config, pid, seed: 1);

            sys.OnEnterBuildPhase();

            Assert.False(sys.RerollOffers());
            Assert.Equal(0, Store.PlayerShopRerollCount[pid]);
        }

        [Fact]
        public void RerollOffers_FailsWhenDisabled()
        {
            var (config, pid) = CreateEnv();
            config.ShopReroll.Enabled = false;
            var sys = new ShopRerollSystem(Store, Renderer, config, pid, seed: 1);

            sys.OnEnterBuildPhase();  // should be a no-op
            // RerollCount was never set because OnEnterBuildPhase bailed
            Assert.Equal(0, Store.PlayerShopRerollCount[pid]);

            Assert.False(sys.RerollOffers());
        }

        [Fact]
        public void PityCounters_NeverExceedThreshold()
        {
            var (config, pid) = CreateEnv();
            var sys = new ShopRerollSystem(Store, Renderer, config, pid, seed: 7);

            sys.OnEnterBuildPhase();
            // Pity counters after initial rolls — must be within bounds
            Assert.InRange(Store.PlayerShopPityRare[pid], 0, config.ShopReroll.PityRareThreshold);
            Assert.InRange(Store.PlayerShopPityEpic[pid], 0, config.ShopReroll.PityEpicThreshold);

            // Reroll several times with a cheap cost curve.
            config.ShopReroll.CostCurve = new float[] { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f };
            for (int i = 0; i < 8; i++)
            {
                if (!sys.RerollOffers()) break;
            }
            // After many rerolls, pity should still be bounded (forced Rare/Epic resets it)
            Assert.InRange(Store.PlayerShopPityRare[pid], 0, config.ShopReroll.PityRareThreshold);
            Assert.InRange(Store.PlayerShopPityEpic[pid], 0, config.ShopReroll.PityEpicThreshold);
        }

        [Fact]
        public void GetOffer_ReturnsValidOfferAfterPhaseEnter()
        {
            var (config, pid) = CreateEnv();
            // 显式注入 tower/skill 池，让 RollOffers 产生真实可映射的 typeId。
            config.TowerTypes.Add(new TowerConfig { Type = TowerType.AOE, Cost = 50f });
            config.Skills.Add(new SkillConfig { Name = "Fireball", ManaCost = 10f });
            var sys = new ShopRerollSystem(Store, Renderer, config, pid, seed: 99);

            sys.OnEnterBuildPhase();

            for (int i = 0; i < config.ShopReroll.OfferSlotCount; i++)
            {
                var offer = sys.GetOffer(i);
                Assert.True(offer.IsValid, $"slot {i} should be valid after a real roll");
                AssertOfferConsistentWithConfig(offer, config);
            }

            // 越界槽位契约：返回无效 offer。
            Assert.False(sys.GetOffer(-1).IsValid);
            Assert.False(sys.GetOffer(config.ShopReroll.OfferSlotCount).IsValid);
        }

        [Fact]
        public void GetRerollCost_FollowsCurve()
        {
            var (config, pid) = CreateEnv();
            config.ShopReroll.CostCurve = new float[] { 7f, 14f, 28f };
            var sys = new ShopRerollSystem(Store, Renderer, config, pid, seed: 1);

            Assert.Equal(7f, sys.GetRerollCost(0));
            Assert.Equal(14f, sys.GetRerollCost(1));
            Assert.Equal(28f, sys.GetRerollCost(2));
            // Past end of curve -> last value
            Assert.Equal(28f, sys.GetRerollCost(99));
        }

        [Fact]
        public void GetRemainingRerolls_TracksUsage()
        {
            var (config, pid) = CreateEnv();
            config.ShopReroll.MaxRerollsPerPhase = 3;
            config.ShopReroll.CostCurve = new float[] { 1f, 1f, 1f };
            var sys = new ShopRerollSystem(Store, Renderer, config, pid, seed: 1);

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
            var (config, pid) = CreateEnv();
            var sys = new ShopRerollSystem(Store, Renderer, config, pid, seed: 1);

            // Update() 是 BuildPhase 空实现：调用后阶段状态必须保持不变（reroll 计数仍为 0）。
            sys.Update();
            sys.Update();
            Assert.Equal(0, Store.PlayerShopRerollCount[pid]);
        }
    }
}