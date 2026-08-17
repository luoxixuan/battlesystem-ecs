using System;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Components;
using BattleSystemECS.Tests.Infrastructure;

namespace BattleSystemECS.Tests.Mechanisms.Combat
{
    /// <summary>
    /// Core damage formula tests — armor, magic resist, true damage, shield absorption.
    /// 公式在 TowerAttackSystem / PlayerTowerAttackSystem 的真实攻击路径中生效；
    /// 本文件不再在测试内复刻公式，只通过 ApplyEnemyDamage（存储层真实路径）
    /// 与 PlayerTowerAttackSystem（真实攻击链路）断言可观测结果。
    /// </summary>
    public class DamageFormulaTests : BattleTestBase
    {
        // ══════════════════════════════════════════════════════════════
        //  ApplyEnemyDamage：护盾吸收与直接伤害（存储层真实路径）
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void ApplyEnemyDamage_DirectDamage_ReducesHealth()
        {
            int eid = Enemy();

            Store.ApplyEnemyDamage(eid, 30f);
            Assert.Equal(70f, Store.EnemyHealth[eid]);
        }

        [Fact]
        public void ApplyEnemyDamage_ShieldAbsorbsFirst()
        {
            int eid = Enemy();
            Store.EnemyShield[eid] = 25f;

            Store.ApplyEnemyDamage(eid, 20f);
            Assert.Equal(100f, Store.EnemyHealth[eid]);
            Assert.Equal(5f, Store.EnemyShield[eid]);
        }

        [Fact]
        public void ApplyEnemyDamage_ShieldPartial()
        {
            int eid = Enemy();
            Store.EnemyShield[eid] = 10f;

            Store.ApplyEnemyDamage(eid, 40f);
            Assert.Equal(70f, Store.EnemyHealth[eid]);
            Assert.Equal(0f, Store.EnemyShield[eid]);
        }

        [Fact]
        public void ApplyEnemyDamage_ZeroDamage_NoEffect()
        {
            int eid = Enemy();
            Store.EnemyShield[eid] = 50f;

            Store.ApplyEnemyDamage(eid, 0f);
            Assert.Equal(100f, Store.EnemyHealth[eid]);
            Assert.Equal(50f, Store.EnemyShield[eid]);
        }

        [Fact]
        public void ApplyEnemyDamage_NegativeDamage_Ignored()
        {
            int eid = Enemy();

            Store.ApplyEnemyDamage(eid, -10f);
            Assert.Equal(100f, Store.EnemyHealth[eid]);
        }

        [Fact]
        public void ApplyEnemyDamage_ExactKill()
        {
            int eid = Enemy(e => e.Health = 50f);

            Store.ApplyEnemyDamage(eid, 50f);
            Assert.Equal(0f, Store.EnemyHealth[eid]);
        }

        [Fact]
        public void ApplyEnemyDamage_Overkill()
        {
            int eid = Enemy(e => e.Health = 30f);

            Store.ApplyEnemyDamage(eid, 100f);
            Assert.Equal(-70f, Store.EnemyHealth[eid]);
        }

        [Fact]
        public void ApplyEnemyDamage_IgnoresArmor_RawDamage()
        {
            int eid = Enemy();
            Store.EnemyArmor[eid] = 0.3f;

            // ApplyEnemyDamage 是原始伤害入口，不套护甲；护甲只在攻击系统公式中生效。
            Store.ApplyEnemyDamage(eid, 100f);
            Assert.Equal(0f, Store.EnemyHealth[eid]);
        }

        [Fact]
        public void Shield_BreakAndDamage_HandlesMultipleApplications()
        {
            int eid = Enemy();
            Store.EnemyShield[eid] = 30f;

            Store.ApplyEnemyDamage(eid, 20f);
            Assert.Equal(100f, Store.EnemyHealth[eid]);
            Assert.Equal(10f, Store.EnemyShield[eid]);

            Store.ApplyEnemyDamage(eid, 15f);
            Assert.Equal(95f, Store.EnemyHealth[eid]);
            Assert.Equal(0f, Store.EnemyShield[eid]);

            Store.ApplyEnemyDamage(eid, 20f);
            Assert.Equal(75f, Store.EnemyHealth[eid]);
        }

        // ══════════════════════════════════════════════════════════════
        //  真实攻击路径：PlayerTowerAttackSystem 应用护甲/魔抗/真伤
        // ══════════════════════════════════════════════════════════════

        /// <summary>驱动一次真实玩家攻击，返回敌人实际承受的伤害。</summary>
        private float DrivePlayerAttackAndGetDamage(float attackDamage, DamageType playerDamageType, float armor, float magicResist)
        {
            int pid = Player(p =>
            {
                p.AttackDamage = attackDamage;
                p.AttackRange = 10f;
            });
            Store.PlayerDamageType[pid] = playerDamageType;

            // 敌人放在玩家正下方 0.1 格，满足 PlayerTowerAttackSystem 的 enemyY > playerY 射界。
            int eid = Enemy(e =>
            {
                e.X = 0f;
                e.Y = 0.1f;
                e.Health = 1000f;
            });
            Store.EnemyArmor[eid] = armor;
            Store.EnemyMagicResist[eid] = magicResist;

            var attack = new BattleSystemECS.Systems.PlayerTowerAttackSystem(Store, Renderer, pid, Config);
            attack.SetTurn(0);
            float preHp = Store.EnemyHealth[eid];
            attack.Update();
            return preHp - Store.EnemyHealth[eid];
        }

        [Theory]
        [InlineData(DamageType.Physical, 0.3f, 70f)] // 30% 护甲 → 100 × 0.7 = 70
        [InlineData(DamageType.Physical, 0f, 100f)]  // 无护甲 → 全额
        [InlineData(DamageType.Magic, 0.5f, 50f)]    // 50% 魔抗 → 100 × 0.5 = 50
        [InlineData(DamageType.Magic, 0f, 100f)]     // 无魔抗 → 全额
        public void RealAttack_AppliesMatchingResistance(DamageType damageType, float resist, float expectedDamage)
        {
            // 同一攻击链路按伤害类型读取对应抗性，行为同构，合并为理论驱动。
            float damage = DrivePlayerAttackAndGetDamage(100f, damageType, armor: resist, magicResist: resist);
            Assert.Equal(expectedDamage, damage, 3);
        }

        [Fact]
        public void RealAttack_FullMagicResist_KeepsOnePercentFloor()
        {
            // 魔抗 100% 也保留 1% 最低伤害：100 × max(0.01, 0) = 1。
            float damage = DrivePlayerAttackAndGetDamage(100f, DamageType.Magic, armor: 0f, magicResist: 1f);
            Assert.Equal(1f, damage, 3);
        }

        [Fact]
        public void RealAttack_FullArmor_ClampsAt95PercentBeforeMitigation()
        {
            // 生产路径先把护甲钳到 0.95：100 × max(0.01, 1 - 0.95) = 5。
            float damage = DrivePlayerAttackAndGetDamage(100f, DamageType.Physical, armor: 1f, magicResist: 0f);
            Assert.Equal(5f, damage, 3);
        }

        [Fact]
        public void RealAttack_TrueDamage_BypassesArmorAndResist()
        {
            // 真伤不读护甲/魔抗数组，全额穿透。
            float damage = DrivePlayerAttackAndGetDamage(100f, DamageType.True, armor: 1f, magicResist: 1f);
            Assert.Equal(100f, damage, 3);
        }

        // ══════════════════════════════════════════════════════════════
        //  Weather / Day-Night 默认状态（只读默认值，不做纯回读往返）
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void Weather_DefaultIsClear()
        {
            int pid = Player();

            Assert.Equal(0, Store.GetCurrentWeather(pid)); // 0 = Clear
        }

        [Fact]
        public void DayNight_DefaultPhase()
        {
            int pid = Player();

            Assert.Equal(0, Store.GetDayNightPhase(pid)); // 0 = Day
        }
    }
}
