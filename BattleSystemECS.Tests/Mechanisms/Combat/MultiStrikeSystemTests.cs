using BattleSystemECS.Tests.Infrastructure;
using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Mechanisms.Combat
{
    /// <summary>
    /// Tests for Round 201 Direction 1: Multi-Strike Projectile.
    /// 多段打击语义一律走 TowerAttackSystem 真实攻击路径验证：
    /// 放塔 → 设置 MultiStrike 字段 → RebuildSpatialGrid → SetTurn → Update，
    /// 然后断言各敌人的真实伤害。测试内不做字段写读回环。
    /// </summary>
    public class MultiStrikeSystemTests : BattleTestBase
    {
        private const float EnemyStartHealth = 1000f;

        /// <summary>
        /// 塔在 (0,0)，敌人在 y=spacing*(i+1) 的竖线上（enemies[0] 为最近主目标），
        /// 真实攻击一帧后返回各敌人 id。塔伤 10、射程 10、攻速 10（一帧必开火）。
        /// </summary>
        private int[] AttackWithMultiStrike(int multiStrikeCount, float msRange, float msMult,
            int enemyCount, float spacing, float towerDamage = 10f)
        {
            int towerId = Tower(0, 0, TowerType.Basic, t =>
            {
                t.Damage = towerDamage;
                t.Range = 10;
                t.Speed = 10f;
            });
            Store.TowerMultiStrikeCount[towerId] = multiStrikeCount;
            Store.TowerMultiStrikeRange[towerId] = msRange;
            Store.TowerMultiStrikeDamageMult[towerId] = msMult;

            var enemies = new int[enemyCount];
            for (int i = 0; i < enemyCount; i++)
            {
                int eid = Enemy(e => { e.Name = "Extra" + i; e.Health = EnemyStartHealth; });
                Store.PositionX[eid] = 0f;
                Store.PositionY[eid] = spacing * (i + 1);
                enemies[i] = eid;
            }

            Store.RebuildSpatialGrid();
            var attack = new TowerAttackSystem(Store, Renderer);
            attack.SetTurn(0);
            attack.Update(1f);
            return enemies;
        }

        // ── 1. Default state（真实 AddTower/PlaceTower 后的默认值）──────────

        [Fact]
        public void DefaultState_AllMultiStrikeFields_AreZeroOverheadDefaults()
        {
            int tid = Tower(0, 0);
            Assert.Equal(0, Store.TowerMultiStrikeCount[tid]);
            Assert.Equal(0f, Store.TowerMultiStrikeRange[tid]);
            Assert.Equal(1f, Store.TowerMultiStrikeDamageMult[tid]);
        }

        // ── 2. TowerConfig 默认值（不依赖反射，直接读配置对象）─────────────

        [Fact]
        public void TowerConfig_HasMultiStrikeFields_DefaultsAreZeroOverhead()
        {
            var config = new TowerConfig();
            Assert.Equal(0, config.MultiStrikeCount);
            Assert.Equal(0f, config.MultiStrikeRange);
            Assert.Equal(1f, config.MultiStrikeDamageMult);
        }

        // ── 3. Field reset after destroy ─────────────────────────────────

        [Fact]
        public void DestroyEntity_ResetsMultiStrikeFieldsToDefaults()
        {
            int tid = Tower(0, 0);
            Store.TowerMultiStrikeCount[tid] = 5;
            Store.TowerMultiStrikeRange[tid] = 7f;
            Store.TowerMultiStrikeDamageMult[tid] = 0.4f;

            Store.DestroyEntity(tid);

            Assert.Equal(0, Store.TowerMultiStrikeCount[tid]);
            Assert.Equal(0f, Store.TowerMultiStrikeRange[tid]);
            Assert.Equal(1f, Store.TowerMultiStrikeDamageMult[tid]);
        }

        // ── 真实攻击路径：额外命中 / range=0 回退 / 倍率 ─────────────────

        [Fact]
        public void Attack_RangeZero_FallsBackToTowerRange_AndDamageMultApplies()
        {
            // msRange=0 → 回退 TowerRange(10)：两个额外目标都在主目标附近且可被命中；
            // msMult=0.5 → 额外目标各吃 5 点，主目标吃满 10 点。
            int[] enemies = AttackWithMultiStrike(multiStrikeCount: 2, msRange: 0f, msMult: 0.5f,
                enemyCount: 3, spacing: 0.4f);

            Assert.Equal(EnemyStartHealth - 10f, Store.EnemyHealth[enemies[0]], 3);
            Assert.Equal(EnemyStartHealth - 5f, Store.EnemyHealth[enemies[1]], 3);
            Assert.Equal(EnemyStartHealth - 5f, Store.EnemyHealth[enemies[2]], 3);
        }

        [Fact]
        public void Attack_CustomRange_LimitsExtraTargetSelection()
        {
            // msRange=1：主目标 y=0.4；额外目标分别距主目标 0.4（命中）与 1.6（不命中）。
            int towerId = Tower(0, 0, TowerType.Basic, t =>
            {
                t.Damage = 10f;
                t.Range = 10;
                t.Speed = 10f;
            });
            Store.TowerMultiStrikeCount[towerId] = 5;
            Store.TowerMultiStrikeRange[towerId] = 1f;
            Store.TowerMultiStrikeDamageMult[towerId] = 1f;

            int primary = Enemy(e => { e.Name = "Primary"; e.Health = EnemyStartHealth; });
            int extraIn = Enemy(e => { e.Name = "ExtraIn"; e.Health = EnemyStartHealth; });
            int extraOut = Enemy(e => { e.Name = "ExtraOut"; e.Health = EnemyStartHealth; });
            Store.PositionX[primary] = 0f; Store.PositionY[primary] = 0.4f;
            Store.PositionX[extraIn] = 0f; Store.PositionY[extraIn] = 0.8f;
            Store.PositionX[extraOut] = 0f; Store.PositionY[extraOut] = 2.0f;

            Store.RebuildSpatialGrid();
            var attack = new TowerAttackSystem(Store, Renderer);
            attack.SetTurn(0);
            attack.Update(1f);

            Assert.Equal(EnemyStartHealth - 10f, Store.EnemyHealth[primary], 3);
            Assert.Equal(EnemyStartHealth - 10f, Store.EnemyHealth[extraIn], 3);
            Assert.Equal(EnemyStartHealth, Store.EnemyHealth[extraOut], 3);
        }

        [Fact]
        public void Attack_ExtraCount_CappedAtSixteen()
        {
            // count=100 但运行时最多 16 个额外目标：主目标 + 16 额外 = 17 个受伤，
            // 最远 3 个保持满血。
            int[] enemies = AttackWithMultiStrike(multiStrikeCount: 100, msRange: 100f, msMult: 0.5f,
                enemyCount: 20, spacing: 0.4f);

            int harmed = 0;
            foreach (int eid in enemies)
                if (Store.EnemyHealth[eid] < EnemyStartHealth) harmed++;
            Assert.Equal(17, harmed);
            Assert.Equal(EnemyStartHealth, Store.EnemyHealth[enemies[19]], 3);
            Assert.Equal(EnemyStartHealth - 5f, Store.EnemyHealth[enemies[1]], 3);
        }

        // ── 零 / 负 count：只打主目标 ─────────────────────────────────────

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Attack_ZeroOrNegativeCount_TakesSingleTargetOnly(int multiStrikeCount)
        {
            int[] enemies = AttackWithMultiStrike(multiStrikeCount, msRange: 100f, msMult: 0.5f,
                enemyCount: 3, spacing: 0.4f);

            Assert.Equal(EnemyStartHealth - 10f, Store.EnemyHealth[enemies[0]], 3);
            Assert.Equal(EnemyStartHealth, Store.EnemyHealth[enemies[1]], 3);
            Assert.Equal(EnemyStartHealth, Store.EnemyHealth[enemies[2]], 3);
        }

        // ── 非正倍率：额外目标回退为全伤（1f） ───────────────────────────

        [Theory]
        [InlineData(0f)]
        [InlineData(-0.5f)]
        public void Attack_NonPositiveDamageMult_FallsBackToFullDamage(float msMult)
        {
            int[] enemies = AttackWithMultiStrike(multiStrikeCount: 1, msRange: 100f, msMult: msMult,
                enemyCount: 2, spacing: 0.4f);

            Assert.Equal(EnemyStartHealth - 10f, Store.EnemyHealth[enemies[0]], 3);
            Assert.Equal(EnemyStartHealth - 10f, Store.EnemyHealth[enemies[1]], 3);
        }
    }
}
