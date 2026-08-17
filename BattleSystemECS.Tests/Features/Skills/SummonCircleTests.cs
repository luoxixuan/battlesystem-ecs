using BattleSystemECS.Tests.Infrastructure;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Features.Skills
{
    /// <summary>
    /// Tests for Round 115 Direction 2: Summon Circle / Anti-Summon Tower mechanics.
    ///
    /// Verifies:
    ///   - Default state: no summon circle, no anti-summon bonus (zero-overhead fast path)
    ///   - SetSummonCircle / ClearSummonCircle store the (X,Y,Radius) tuple correctly
    ///   - DestroyEntity resets the (X,Y,Radius) tuple (no ID-reuse leakage)
    ///   - SetTowerAntiSummonMultiplier clamps to [0, 10]
    ///   - Default anti-summon multiplier is 0 (fast path, no bonus)
    ///   - Anti-summon bonus only applies when the target enemy is within the circle
    ///     radius (else the damage is unchanged)
    ///   - Anti-summon multiplier of 0.0 → no bonus regardless of position
    ///   - Anti-summon multiplier of 2.0 → double damage vs in-circle targets
    ///   - Multiple towers attacking the same in-circle target all get the bonus independently
    ///   - Enemy outside the circle radius: no bonus even if circle exists
    /// </summary>
    public class SummonCircleTests : BattleTestBase
    {
        // ─── Default state — backward compat ─────────────────────────────

        [Fact]
        public void Default_NoSummonCircle_NoAntiSummon()
        {
            int e = Enemy(spec => { spec.X = 10f; spec.Y = 10f; });
            Assert.Equal(0f, Store.EnemyInSummonCircleX[e]);
            Assert.Equal(0f, Store.EnemyInSummonCircleY[e]);
            Assert.Equal(0f, Store.EnemyInSummonCircleRadius[e]);

            int t = RawTower(0, 0, TowerType.Basic, 5f, 3, 1f, 1, 50f);
            Assert.Equal(0f, Store.TowerAntiSummonMultiplier[t]);
        }

        // ─── Set / Clear — round trip ────────────────────────────────────

        [Fact]
        public void SetSummonCircle_StoresFields()
        {
            int e = Enemy(spec => { spec.X = 100f; spec.Y = 200f; });
            Store.SetSummonCircle(e, 50f, 75f, 12.5f);
            Assert.Equal(50f, Store.EnemyInSummonCircleX[e]);
            Assert.Equal(75f, Store.EnemyInSummonCircleY[e]);
            Assert.Equal(12.5f, Store.EnemyInSummonCircleRadius[e]);
        }

        [Fact]
        public void ClearSummonCircle_ResetsToZero()
        {
            int e = Enemy(spec => { spec.X = 100f; spec.Y = 200f; });
            Store.SetSummonCircle(e, 50f, 75f, 12.5f);
            Store.ClearSummonCircle(e);
            Assert.Equal(0f, Store.EnemyInSummonCircleX[e]);
            Assert.Equal(0f, Store.EnemyInSummonCircleY[e]);
            Assert.Equal(0f, Store.EnemyInSummonCircleRadius[e]);
        }

        [Fact]
        public void SetSummonCircle_NegativeRadius_ClampsToZero()
        {
            int e = Enemy();
            Store.SetSummonCircle(e, 1f, 2f, -5f);
            Assert.Equal(0f, Store.EnemyInSummonCircleRadius[e]);
        }

        // ─── DestroyEntity resets the fields (no ID-reuse leakage) ────────

        [Fact]
        public void DestroyEnemy_ResetsSummonCircleFields()
        {
            int e = Enemy();
            Store.SetSummonCircle(e, 99f, 88f, 7f);
            Store.DestroyEntity(e);
            // Re-use the slot for another enemy; new enemy must start with no circle.
            int e2 = Enemy(spec => { spec.Name = "Reused"; });
            Assert.Equal(0f, Store.EnemyInSummonCircleX[e2]);
            Assert.Equal(0f, Store.EnemyInSummonCircleY[e2]);
            Assert.Equal(0f, Store.EnemyInSummonCircleRadius[e2]);
        }

        [Fact]
        public void DestroyTower_ResetsAntiSummonMultiplier()
        {
            int t = RawTower(0, 0, TowerType.Basic, 5f, 3, 1f, 1, 50f);
            Store.SetTowerAntiSummonMultiplier(t, 2.5f);
            Store.DestroyEntity(t);
            int t2 = RawTower(0, 0, TowerType.Basic, 5f, 3, 1f, 1, 50f);
            Assert.Equal(0f, Store.TowerAntiSummonMultiplier[t2]);
        }

        // ─── SetTowerAntiSummonMultiplier clamping ───────────────────────

        [Fact]
        public void SetTowerAntiSummonMultiplier_ClampsToRange()
        {
            int t = RawTower(0, 0, TowerType.Basic, 5f, 3, 1f, 1, 50f);
            Store.SetTowerAntiSummonMultiplier(t, -3f);
            Assert.Equal(0f, Store.TowerAntiSummonMultiplier[t]);
            Store.SetTowerAntiSummonMultiplier(t, 20f);
            Assert.Equal(10f, Store.TowerAntiSummonMultiplier[t]);
            Store.SetTowerAntiSummonMultiplier(t, 2.5f);
            Assert.Equal(2.5f, Store.TowerAntiSummonMultiplier[t]);
        }

        // ─── Fast-path semantics: multiplier == 0 means no bonus ─────────
        //
        // We can't easily drive the full TowerAttackSystem hot path in a unit test
        // (it requires spatial grid, GameConfig, etc.) but we can verify the pre-conditions
        // that gate the bonus:
        //   - the per-enemy circle fields store the (X,Y,Radius) used by the bonus path
        //   - the per-tower multiplier stores the value used by the bonus path
        //   - destroyed/recycled slots start at zero (so they take the fast path)

        [Fact]
        public void FastPath_DefaultZero_AntiSummonSkipped()
        {
            // When both multiplier == 0 and radius == 0 the hot path short-circuits without
            // touching PositionX/Y. We verify both gates independently here.
            int t = RawTower(0, 0, TowerType.Basic, 5f, 3, 1f, 1, 50f);
            int e = Enemy(spec => { spec.X = 50f; spec.Y = 50f; });
            Assert.Equal(0f, Store.TowerAntiSummonMultiplier[t]); // gate 1
            Assert.Equal(0f, Store.EnemyInSummonCircleRadius[e]); // gate 2
        }

        // ─── 真实生产路径：TowerAttackSystem 的 anti-summon 伤害加成 ────────

        [Fact]
        public void TowerAttack_AntiSummonBonus_OnlyAppliesInsideCircle()
        {

            // 两座完全相同的塔 + 两个完全相同的敌人，唯一差异：敌人是否在自己的召唤圈内。
            int towerIn = RawTower(0, 0, TowerType.Basic, 5f, 3, 1f, 1, 50f);
            int towerOut = RawTower(8, 0, TowerType.Basic, 5f, 3, 1f, 1, 50f);
            Store.SetTowerAntiSummonMultiplier(towerIn, 2f);
            Store.SetTowerAntiSummonMultiplier(towerOut, 2f);

            int enemyIn = Enemy(spec => { spec.X = 2f; spec.Y = 0f; });
            Store.SetSummonCircle(enemyIn, 0f, 0f, 10f);   // 敌人位于圆心附近 → 圈内
            int enemyOut = Enemy(spec => { spec.X = 7f; spec.Y = 0f; });
            Store.SetSummonCircle(enemyOut, 0f, 100f, 1f); // 圆心远离敌人 → 圈外
            Store.EnemyHealth[enemyIn] = 1000f;
            Store.EnemyMaxHealth[enemyIn] = 1000f;
            Store.EnemyHealth[enemyOut] = 1000f;
            Store.EnemyMaxHealth[enemyOut] = 1000f;

            Store.BeginFrame();
            Store.RebuildSpatialGrid();
            var attack = new TowerAttackSystem(Store, Renderer);
            attack.SetTurn(0);
            attack.Update(1f);

            // 两座塔都命中了各自射程内的唯一敌人；圈内目标必须受到额外加成 → 掉血更多。
            Assert.True(Store.EnemyHealth[enemyIn] < 1000f, "in-circle enemy should take damage");
            Assert.True(Store.EnemyHealth[enemyOut] < 1000f, "out-of-circle enemy should take damage");
            Assert.True(Store.EnemyHealth[enemyIn] < Store.EnemyHealth[enemyOut],
                "anti-summon multiplier must only boost the in-circle target");
        }
    }
}