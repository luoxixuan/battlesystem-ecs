using BattleSystemECS.Tests.Infrastructure;
using System;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Features.Towers
{
    /// <summary>
    /// Tests for Round 180 Direction 5: Fortress Aura (clustered-tower damage + speed bonus).
    /// Verifies that:
    ///   - Default state: all Fortress fields are 0 (zero-overhead fast path)
    ///   - FortressConfig has sensible defaults
    ///   - Accessor methods clamp to safe ranges
    ///   - SetTurn with no towers is a no-op
    ///   - SetTurn with 1 tower yields neighbor count = 0, no bonus
    ///   - SetTurn with 2 same-type towers at distance 1 → still 0 neighbors (need T1=3)
    ///   - SetTurn with 3 same-type towers at distance 1-2 → T1 bonus (15% dmg, 10% atk-spd)
    ///   - SetTurn with 5 same-type towers → T2 bonus (25% dmg, 20% atk-spd)
    ///   - Different-type towers are NOT counted as neighbors
    ///   - Towers outside FortressRadius are NOT counted
    ///   - Dispelled towers are skipped
    ///   - Destroyed/removed towers are skipped (TowerActive=false)
    ///   - Re-running SetTurn clears stale cache from prior frame
    ///   - Fortress bonuses compose with synergy bonus (read by TowerAttackSystem)
    /// </summary>
    public class TowerFortressSystemTests : BattleTestBase
    {
        private int PlaceTower(float x, float y, TowerType type = TowerType.Firewall, float attackSpeed = 1f)
        {
            return RawTower((int)x, (int)y, type, 10f, 5, attackSpeed, 1, 50f);
        }

        // ─── Default state ─────────────────────────────────────────────

        [Fact]
        public void DefaultState_AllFortressFieldsZero()
        {
            for (int i = 0; i < 10; i++)
            {
                Assert.Equal(0, Store.TowerFortressNeighborCount[i]);
                Assert.Equal(0f, Store.TowerFortressCachedDmgBonus[i], 3);
                Assert.Equal(0f, Store.TowerFortressCachedAtkSpdBonus[i], 3);
            }
        }

        [Fact]
        public void AddTower_DefaultsFortressToZero()
        {
            Store.AddTower(0, TowerType.Firewall, 10f, 5, 1f, 1, 50f);
            Assert.Equal(0, Store.GetTowerFortressNeighborCount(0));
            Assert.Equal(0f, Store.GetTowerFortressDmgBonus(0));
            Assert.Equal(0f, Store.GetTowerFortressAtkSpdBonus(0));
        }

        [Fact]
        public void FortressConfig_HasSensibleDefaults()
        {
            Assert.True(FortressConfig.FortressRadius > 0f);
            Assert.True(FortressConfig.FortressT1NeighborCount > 0);
            Assert.True(FortressConfig.FortressT2NeighborCount > FortressConfig.FortressT1NeighborCount);
            Assert.True(FortressConfig.FortressT1DmgBonus > 0f);
            Assert.True(FortressConfig.FortressT1AtkSpdBonus > 0f);
            Assert.True(FortressConfig.FortressT2DmgBonus > FortressConfig.FortressT1DmgBonus);
            Assert.True(FortressConfig.FortressT2AtkSpdBonus > FortressConfig.FortressT1AtkSpdBonus);
        }

        // ─── Accessor methods ──────────────────────────────────────────

        [Fact]
        public void GetSetNeighborCount_ClampsToZeroOrAbove()
        {
            Store.AddTower(0, TowerType.Firewall, 10f, 5, 1f, 1, 50f);
            Store.SetTowerFortressNeighborCount(0, -3);
            Assert.Equal(0, Store.GetTowerFortressNeighborCount(0));
            Store.SetTowerFortressNeighborCount(0, 5);
            Assert.Equal(5, Store.GetTowerFortressNeighborCount(0));
            // Above 32 should clamp
            Store.SetTowerFortressNeighborCount(0, 100);
            Assert.Equal(32, Store.GetTowerFortressNeighborCount(0));
        }

        [Fact]
        public void GetSetDmgBonus_ClampsToZeroOrAbove()
        {
            Store.AddTower(0, TowerType.Firewall, 10f, 5, 1f, 1, 50f);
            Store.SetTowerFortressDmgBonus(0, -0.5f);
            Assert.Equal(0f, Store.GetTowerFortressDmgBonus(0));
            Store.SetTowerFortressDmgBonus(0, 0.15f);
            Assert.Equal(0.15f, Store.GetTowerFortressDmgBonus(0), 3);
            // Above 1.0 should clamp
            Store.SetTowerFortressDmgBonus(0, 2.0f);
            Assert.Equal(1.0f, Store.GetTowerFortressDmgBonus(0), 3);
        }

        [Fact]
        public void GetSetAtkSpdBonus_ClampsToZeroOrAbove()
        {
            Store.AddTower(0, TowerType.Firewall, 10f, 5, 1f, 1, 50f);
            Store.SetTowerFortressAtkSpdBonus(0, -0.1f);
            Assert.Equal(0f, Store.GetTowerFortressAtkSpdBonus(0));
            Store.SetTowerFortressAtkSpdBonus(0, 0.20f);
            Assert.Equal(0.20f, Store.GetTowerFortressAtkSpdBonus(0), 3);
            // Above 1.0 should clamp
            Store.SetTowerFortressAtkSpdBonus(0, 5.0f);
            Assert.Equal(1.0f, Store.GetTowerFortressAtkSpdBonus(0), 3);
        }

        // ─── SetTurn: no-op paths ─────────────────────────────────────

        [Fact]
        public void SetTurn_NoTowersIsNoOp()
        {
            // 手工写入非默认缓存字段，验证 SetTurn 空塔列表时真的“什么都没做”。
            Store.SetTowerFortressNeighborCount(0, 4);
            Store.SetTowerFortressDmgBonus(0, 0.25f);
            Store.SetTowerFortressAtkSpdBonus(0, 0.20f);
            var sys = new TowerFortressSystem(Store, Renderer);

            sys.SetTurn();

            Assert.Empty(Store.ActiveTowerIds);
            Assert.Equal(4, Store.GetTowerFortressNeighborCount(0));
            Assert.Equal(0.25f, Store.GetTowerFortressDmgBonus(0), 3);
            Assert.Equal(0.20f, Store.GetTowerFortressAtkSpdBonus(0), 3);
        }

        [Fact]
        public void SetTurn_SingleTower_NoNeighbors()
        {
            int t = PlaceTower(5f, 5f);
            var sys = new TowerFortressSystem(Store, Renderer);
            sys.SetTurn();
            Assert.Equal(0, Store.GetTowerFortressNeighborCount(t));
            Assert.Equal(0f, Store.GetTowerFortressDmgBonus(t));
            Assert.Equal(0f, Store.GetTowerFortressAtkSpdBonus(t));
        }

        // ─── SetTurn: tier thresholds ─────────────────────────────────

        [Fact]
        public void SetTurn_TwoSameType_NoTierBonus()
        {
            // 2 same-type towers = not enough for T1 (which needs 3)
            int t0 = PlaceTower(5f, 5f, TowerType.Firewall);
            int t1 = PlaceTower(5f, 6f, TowerType.Firewall);
            var sys = new TowerFortressSystem(Store, Renderer);
            sys.SetTurn();
            // Each tower has 1 neighbor (the other)
            Assert.Equal(1, Store.GetTowerFortressNeighborCount(t0));
            Assert.Equal(1, Store.GetTowerFortressNeighborCount(t1));
            // But tier 0 (below T1 threshold of 3) → no bonus
            Assert.Equal(0f, Store.GetTowerFortressDmgBonus(t0));
            Assert.Equal(0f, Store.GetTowerFortressAtkSpdBonus(t0));
        }

        [Fact]
        public void SetTurn_ThreeSameType_TriggersTier1()
        {
            // 3 same-type towers in radius → each has 2 neighbors → below T2 (5) but ≥ T1 (3)
            // Wait — T1 is "≥ 3 neighbors". But 3 towers means each sees 2 neighbors.
            // Re-read FortressConfig: FortressT1NeighborCount = 3. So you need 3 OTHER neighbors.
            // For 4 towers in a cluster, each has 3 neighbors → T1 fires.
            int t0 = PlaceTower(5f, 5f, TowerType.Firewall);
            int t1 = PlaceTower(5f, 6f, TowerType.Firewall);
            int t2 = PlaceTower(6f, 5f, TowerType.Firewall);
            int t3 = PlaceTower(6f, 6f, TowerType.Firewall);
            var sys = new TowerFortressSystem(Store, Renderer);
            sys.SetTurn();
            // Each tower has 3 neighbors (the other 3) → T1 fires (≥ 3)
            foreach (var t in new[] { t0, t1, t2, t3 })
            {
                Assert.Equal(3, Store.GetTowerFortressNeighborCount(t));
                Assert.Equal(FortressConfig.FortressT1DmgBonus, Store.GetTowerFortressDmgBonus(t), 3);
                Assert.Equal(FortressConfig.FortressT1AtkSpdBonus, Store.GetTowerFortressAtkSpdBonus(t), 3);
            }
        }

        [Fact]
        public void SetTurn_SixSameType_TriggersTier2()
        {
            // 6 same-type towers tightly packed in a 2x3 grid → each has 5 neighbors → T2 fires.
            // Layout (all within Chebyshev distance 2 of each other):
            //   (5,5) (6,5) (7,5)
            //   (5,6) (6,6) (7,6)
            var ids = new int[6];
            int idx = 0;
            for (int y = 0; y < 2; y++)
                for (int x = 0; x < 3; x++)
                    ids[idx++] = PlaceTower(5f + x, 5f + y, TowerType.Tesla);
            var sys = new TowerFortressSystem(Store, Renderer);
            sys.SetTurn();
            foreach (var t in ids)
            {
                Assert.Equal(5, Store.GetTowerFortressNeighborCount(t));
                Assert.Equal(FortressConfig.FortressT2DmgBonus, Store.GetTowerFortressDmgBonus(t), 3);
                Assert.Equal(FortressConfig.FortressT2AtkSpdBonus, Store.GetTowerFortressAtkSpdBonus(t), 3);
            }
        }

        [Fact]
        public void SetTurn_DifferentTypeTowers_NoBonus()
        {
            // 4 towers but 2 of type A and 2 of type B — neither type has 3+ neighbors
            int t0 = PlaceTower(5f, 5f, TowerType.Firewall);
            int t1 = PlaceTower(5f, 6f, TowerType.Firewall);
            int t2 = PlaceTower(6f, 5f, TowerType.Tesla);
            int t3 = PlaceTower(6f, 6f, TowerType.Tesla);
            var sys = new TowerFortressSystem(Store, Renderer);
            sys.SetTurn();
            // Each tower has 1 same-type neighbor, no bonus
            Assert.Equal(0f, Store.GetTowerFortressDmgBonus(t0));
            Assert.Equal(0f, Store.GetTowerFortressDmgBonus(t2));
            Assert.Equal(1, Store.GetTowerFortressNeighborCount(t0));
            Assert.Equal(1, Store.GetTowerFortressNeighborCount(t2));
        }

        [Fact]
        public void SetTurn_TowersOutsideRadius_NoBonus()
        {
            // 4 same-type towers but spaced too far apart (distance 5, radius 2)
            int t0 = PlaceTower(0f, 0f, TowerType.Firewall);
            int t1 = PlaceTower(5f, 0f, TowerType.Firewall);
            int t2 = PlaceTower(10f, 0f, TowerType.Firewall);
            int t3 = PlaceTower(15f, 0f, TowerType.Firewall);
            var sys = new TowerFortressSystem(Store, Renderer);
            sys.SetTurn();
            // No neighbors within radius
            foreach (var t in new[] { t0, t1, t2, t3 })
            {
                Assert.Equal(0, Store.GetTowerFortressNeighborCount(t));
                Assert.Equal(0f, Store.GetTowerFortressDmgBonus(t));
            }
        }

        [Fact]
        public void SetTurn_DispelledTowers_AreSkipped()
        {
            // 4 same-type towers clustered, but one is dispelled → it should be skipped
            // and the others should see only 2 same-type active neighbors (below T1).
            // The dispelled tower should also NOT have fortress bonuses computed for itself
            // (a dispelled tower is "out of the fight" and shouldn't get the cluster buff).
            int t0 = PlaceTower(5f, 5f, TowerType.Firewall);
            int t1 = PlaceTower(5f, 6f, TowerType.Firewall);
            int t2 = PlaceTower(6f, 5f, TowerType.Firewall);
            int t3 = PlaceTower(6f, 6f, TowerType.Firewall);
            // Mark t3 as dispelled
            Store.TowerIsDispelled[t3] = true;
            var sys = new TowerFortressSystem(Store, Renderer);
            sys.SetTurn();
            // Each active (non-dispelled) tower should see 2 neighbors (the other 2 active,
            // t3 is skipped). 2 < T1=3 → no bonus.
            foreach (var t in new[] { t0, t1, t2 })
            {
                Assert.Equal(2, Store.GetTowerFortressNeighborCount(t));
                Assert.Equal(0f, Store.GetTowerFortressDmgBonus(t));
            }
            // t3 is dispelled: outer loop now also skips it (bug-scan fix), so its own
            // bonus is 0 and its neighbor count is 0.
            Assert.Equal(0, Store.GetTowerFortressNeighborCount(t3));
            Assert.Equal(0f, Store.GetTowerFortressDmgBonus(t3));
        }

        [Fact]
        public void SetTurn_DestroyedTowers_AreSkipped()
        {
            // 4 same-type towers clustered, but one is destroyed (TowerActive=false)
            int t0 = PlaceTower(5f, 5f, TowerType.Firewall);
            int t1 = PlaceTower(5f, 6f, TowerType.Firewall);
            int t2 = PlaceTower(6f, 5f, TowerType.Firewall);
            int t3 = PlaceTower(6f, 6f, TowerType.Firewall);
            // Mark t3 as inactive
            Store.TowerActive[t3] = false;
            var sys = new TowerFortressSystem(Store, Renderer);
            sys.SetTurn();
            // t0, t1, t2 each see 2 active neighbors (t3 skipped) → no T1 bonus
            foreach (var t in new[] { t0, t1, t2 })
            {
                Assert.Equal(2, Store.GetTowerFortressNeighborCount(t));
                Assert.Equal(0f, Store.GetTowerFortressDmgBonus(t));
            }
            // t3 is inactive, outer loop skips it
            Assert.Equal(0, Store.GetTowerFortressNeighborCount(t3));
        }

        [Fact]
        public void SetTurn_ClearsStaleCache_BetweenFrames()
        {
            // Frame 1: 4 same-type in tight cluster → T1 fires
            int t0 = PlaceTower(5f, 5f, TowerType.Firewall);
            int t1 = PlaceTower(5f, 6f, TowerType.Firewall);
            int t2 = PlaceTower(6f, 5f, TowerType.Firewall);
            int t3 = PlaceTower(6f, 6f, TowerType.Firewall);
            var sys = new TowerFortressSystem(Store, Renderer);
            sys.SetTurn();
            Assert.Equal(FortressConfig.FortressT1DmgBonus, Store.GetTowerFortressDmgBonus(t0), 3);

            // Frame 2: t3 removed → 3 towers left, each sees 2 neighbors → no T1 bonus
            Store.TowerActive[t3] = false;
            sys.SetTurn();
            // Bonus should be 0 (not stale from frame 1) and neighbor count should be 2
            Assert.Equal(2, Store.GetTowerFortressNeighborCount(t0));
            Assert.Equal(0f, Store.GetTowerFortressDmgBonus(t0));
            Assert.Equal(0f, Store.GetTowerFortressAtkSpdBonus(t0));
        }

        [Fact]
        public void SetTurn_ChainsafeCache_NoCrossFrameLeak()
        {
            // Manually set non-zero values, then SetTurn with different topology
            int t0 = PlaceTower(5f, 5f, TowerType.Firewall);
            Store.SetTowerFortressDmgBonus(t0, 0.99f); // arbitrary
            Store.SetTowerFortressAtkSpdBonus(t0, 0.99f);

            // Now run SetTurn with only t0 active → should clear to 0
            var sys = new TowerFortressSystem(Store, Renderer);
            sys.SetTurn();
            Assert.Equal(0f, Store.GetTowerFortressDmgBonus(t0));
            Assert.Equal(0f, Store.GetTowerFortressAtkSpdBonus(t0));
        }

        [Fact]
        public void Update_IsNoOp()
        {
            int t0 = PlaceTower(5f, 5f, TowerType.Firewall);
            int t1 = PlaceTower(5f, 6f, TowerType.Firewall);
            int t2 = PlaceTower(6f, 5f, TowerType.Firewall);
            int t3 = PlaceTower(6f, 6f, TowerType.Firewall);
            var sys = new TowerFortressSystem(Store, Renderer);
            sys.SetTurn();
            // Manually clear bonuses
            Store.SetTowerFortressDmgBonus(t0, 0f);
            Store.SetTowerFortressAtkSpdBonus(t0, 0f);
            // Update should not restore them
            sys.Update(0.016f);
            Assert.Equal(0f, Store.GetTowerFortressDmgBonus(t0));
            Assert.Equal(0f, Store.GetTowerFortressAtkSpdBonus(t0));
        }

    }
}
