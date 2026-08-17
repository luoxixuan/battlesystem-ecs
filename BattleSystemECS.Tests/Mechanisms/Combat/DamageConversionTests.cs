using BattleSystemECS.Tests.Infrastructure;
using System;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;
using BattleSystemECS.Components;

namespace BattleSystemECS.Tests.Mechanisms.Combat
{
    /// <summary>
    /// Tests for Round 102 Direction 7: Damage Conversion (player → split into original + converted type).
    /// 所有伤害断言都来自真实 PlayerTowerAttackSystem 攻击链路；
    /// 同构的“比例/抗性/免疫/钳制/快路径”场景合并为理论驱动。
    /// </summary>
    public class DamageConversionTests : BattleTestBase
    {
        private const int PlayerId = 0;

        private int NewEnemy(
            float attackDamage = 100f,
            float enemyArmor = 0f,
            float enemyMagicResist = 0f,
            int immunityMask = 0,
            float enemyMaxHp = 1000f,
            DamageType playerDamageType = DamageType.Physical)
        {
            // Player at (0,0) so all enemies in range are hit. PlayerTowerAttackSystem
            // requires enemyY > playerY (enemies "below" the player) — place the enemy
            // at (0, 0.1) so it is just south of the player and in range.
            Player(p =>
            {
                p.AttackRange = 10f;
                p.AttackSpeed = 1f;
                p.AttackDamage = attackDamage;
                p.Level = 1;
                p.BaseLives = 10;
            });
            Store.PlayerDamageType[PlayerId] = playerDamageType;
            int e = Enemy(e =>
            {
                e.X = 0f;
                e.Y = 0.1f;
                e.MoveSpeed = 1f;
                e.Health = enemyMaxHp;
                e.Damage = 0f;
                e.GoldReward = 1;
                e.WaveNumber = 1;
                e.Name = "Test";
            });
            Store.EnemyArmor[e] = enemyArmor;
            Store.EnemyMagicResist[e] = enemyMagicResist;
            Store.EnemyDamageImmunityMask[e] = immunityMask;
            return e;
        }

        /// <summary>驱动一次真实玩家攻击，返回敌人实际承受的总伤害。</summary>
        private float DriveAttack(int enemyId, float conversionRatio, DamageType convertedType)
        {
            Config.PlayerDamageConversionRatio = conversionRatio;
            Config.PlayerConvertedDamageType = convertedType;
            var sys = new PlayerTowerAttackSystem(Store, Renderer, PlayerId, Config);
            sys.SetTurn(0);

            float preHealth = Store.EnemyHealth[enemyId];
            sys.Update();
            return preHealth - Store.EnemyHealth[enemyId];
        }

        // ─── Config constants：只断言相对不变量 ────────────────────────

        [Fact]
        public void DamageConversionConfig_HasSaneRelativeInvariants()
        {
            Assert.True(DamageConversionConfig.ConversionDefaultCap > 0f);
            Assert.True(DamageConversionConfig.ConversionDefaultCap <= 1f);
            Assert.True(DamageConversionConfig.MinMeaningfulRatio > 0f);
            Assert.True(DamageConversionConfig.MinMeaningfulRatio < DamageConversionConfig.ConversionDefaultCap);
        }

        // ─── 真实攻击路径：比例 / 抗性 / 免疫 / 钳制 / 快路径 ───────────

        [Fact]
        public void NoConversion_DefaultBehavior_AppliesPhysicalArmorOnce()
        {
            // 默认 ratio=0：快路径只按 Physical 结算一次。100 × (1 - 0.5) = 50。
            int eid = NewEnemy(enemyArmor: 0.5f, attackDamage: 100f);
            float damage = DriveAttack(eid, conversionRatio: 0f, convertedType: DamageType.Magic);
            Assert.Equal(50f, damage, 3);
        }

        [Fact]
        public void Conversion_HalfSplit_NoResist_DealsFullDamage()
        {
            // 50% 转化：50 Phys + 50 Magic，双方抗性都为 0 → 总伤害 100。
            int eid = NewEnemy(attackDamage: 100f, enemyArmor: 0f, enemyMagicResist: 0f);
            float damage = DriveAttack(eid, conversionRatio: 0.5f, convertedType: DamageType.Magic);
            Assert.Equal(100f, damage, 3);
        }

        [Fact]
        public void Conversion_EachPortionUsesOwnResistance()
        {
            // 50% 转化：Phys 部分受 0.5 护甲（25），Magic 部分受 0.5 魔抗（25）→ 总 50。
            int eid = NewEnemy(attackDamage: 100f, enemyArmor: 0.5f, enemyMagicResist: 0.5f);
            float damage = DriveAttack(eid, conversionRatio: 0.5f, convertedType: DamageType.Magic);
            Assert.Equal(50f, damage, 3);
        }

        [Fact]
        public void Conversion_PhysicalImmunity_StillDealsConvertedMagicPortion()
        {
            // 50% 转化：Phys 部分被免疫清零，Magic 部分 50 全数命中 → 总 50。
            int eid = NewEnemy(
                attackDamage: 100f,
                enemyArmor: 0f,
                enemyMagicResist: 0f,
                immunityMask: (int)DamageType.Physical);
            float damage = DriveAttack(eid, conversionRatio: 0.5f, convertedType: DamageType.Magic);
            Assert.Equal(50f, damage, 3);
        }

        [Fact]
        public void Conversion_AboveCap_IsClampedToConfiguredCap()
        {
            // 注入 0.9 超上限；实际比例被钳到读取到的 ConversionDefaultCap。
            // Magic 部分受 0.5 魔抗，期望 = 100×(1-cap) + 100×cap×0.5（从配置常量推导）。
            int eid = NewEnemy(attackDamage: 100f, enemyArmor: 0f, enemyMagicResist: 0.5f);
            float damage = DriveAttack(eid, conversionRatio: 0.9f, convertedType: DamageType.Magic);

            float cap = DamageConversionConfig.ConversionDefaultCap;
            float expected = 100f * (1f - cap) + 100f * cap * 0.5f;
            Assert.Equal(expected, damage, 3);
        }

        [Fact]
        public void Conversion_BelowMinMeaningfulRatio_TakesUnsplitFastPath()
        {
            // 0.005 低于 MinMeaningfulRatio → 不拆分，整段按 Physical 结算 = 100。
            int eid = NewEnemy(attackDamage: 100f, enemyArmor: 0f, enemyMagicResist: 0f);
            float damage = DriveAttack(eid, conversionRatio: 0.005f, convertedType: DamageType.Magic);
            Assert.Equal(100f, damage, 3);
        }
    }
}
