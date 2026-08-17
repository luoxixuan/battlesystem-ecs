using System.Collections.Generic;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;
using BattleSystemECS.Tests.Infrastructure;

namespace BattleSystemECS.Tests.Mechanisms.Combat
{
    /// <summary>
    /// Tests for Round 143 Direction 1: Tower-vs-Enemy Type Effectiveness Matrix.
    /// 矩阵查询（GetEffectivenessMultiplier / LoadTowerEffectiveness）是生产私有成员，
    /// 测试通过公开入口 TowerAttackSystem.SetGameConfig 注入矩阵后走真实攻击路径验证
    /// 倍率是否真实作用于伤害，不再自建 Dictionary 语义回环。
    /// </summary>
    public class TowerEffectivenessTests : BattleTestBase
    {
        private const float TowerDamage = 10f;
        private const float EnemyStartHealth = 1000f;

        /// <summary>用 AOE 塔（typeIndex=1）真实攻击名为 enemyName 的敌人一次。</summary>
        private int AttackWithConfig(GameConfig? config, string enemyName)
        {
            int towerId = Tower(0, 0, TowerType.AOE, t =>
            {
                t.Damage = TowerDamage;
                t.Range = 10;
                t.Speed = 10f;
            });

            int enemyId = Enemy(e => { e.Name = enemyName; e.Health = EnemyStartHealth; });
            Store.PositionX[enemyId] = 0f;
            Store.PositionY[enemyId] = 1f;

            Store.RebuildSpatialGrid();
            var attack = new TowerAttackSystem(Store, Renderer);
            attack.SetGameConfig(config); // 唯一公开注入入口
            attack.SetTurn(0);
            attack.Update(1f);
            return enemyId;
        }

        [Fact]
        public void Attack_ConfiguredMatrix_ScalesDamageByMultiplier()
        {
            Config.TowerEffectivenessMatrix["1|Swarm"] = 1.30f;
            Config.TowerEffectivenessEntryCount = 1;
            float expectedMult = Config.TowerEffectivenessMatrix["1|Swarm"];

            int enemyId = AttackWithConfig(Config, "Swarm");

            // 期望伤害从注入塔伤与读取到的矩阵倍率推导。
            Assert.Equal(EnemyStartHealth - TowerDamage * expectedMult, Store.EnemyHealth[enemyId], 3);
        }

        [Fact]
        public void Attack_MissingMatrixKey_DefaultsToMultiplierOne()
        {
            Config.TowerEffectivenessMatrix["1|Swarm"] = 1.30f;
            Config.TowerEffectivenessEntryCount = 1;

            int enemyId = AttackWithConfig(Config, "Tank"); // 矩阵中没有 1|Tank

            Assert.Equal(EnemyStartHealth - TowerDamage, Store.EnemyHealth[enemyId], 3);
        }

        [Theory]
        [InlineData(true)]  // 空矩阵
        [InlineData(false)] // null 配置
        public void Attack_EmptyOrNullConfig_DefaultsToMultiplierOne(bool useEmptyConfig)
        {
            GameConfig? cfg = useEmptyConfig ? Config : null;

            int enemyId = AttackWithConfig(cfg, "Swarm");

            Assert.Equal(EnemyStartHealth - TowerDamage, Store.EnemyHealth[enemyId], 3);
        }

        [Fact]
        public void ComponentStore_TracksEnemyTypeName_ForLookup()
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 100f, 5f, 10, 1, "Swarm Spider");
            // Round 137 convention: the prefix before " <Suffix>" is the type name.
            // "Swarm Spider" with no separator → entire string is the type name.
            Assert.Equal("Swarm Spider", Store.GetEnemyTypeName(eid));
        }

        [Fact]
        public void ComponentStore_DefaultEnemyTypeName_IsEmpty()
        {
            // Default-init slots are null/empty (not AddEnemy'd yet)
            Assert.Equal("", Store.GetEnemyTypeName(0));
        }
    }
}
