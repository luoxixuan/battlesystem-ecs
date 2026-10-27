using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class AttributeKeySpaceTests : BattleTestBase
    {
        [Fact]
        public void SyncComputedAttributeBasesWritesAttackDamageOnKey0ForTowerEnemyAndPlayer()
        {
            int playerId = Player(p => p.AttackDamage = 12f);
            int enemyId = Enemy(e => e.Damage = 7f);
            int towerId = RawTower(1, 1, damage: 9f);

            Store.UseComputedAttributes = true;
            Store.ApplyComputedAttributeModeAtFrameBoundary();
            Store.SyncComputedAttributeBases();

            Assert.Equal(9f, Store.AttributeAggregator.GetComputed(towerId, CatalogRegistries.AttackDamage, -1f), 3);
            Assert.Equal(7f, Store.AttributeAggregator.GetComputed(enemyId, CatalogRegistries.AttackDamage, -1f), 3);
            Assert.Equal(12f, Store.AttributeAggregator.GetComputed(playerId, CatalogRegistries.AttackDamage, -1f), 3);

            // key 8 未 SetBase，投影读取时显式以 1f 为 base，因此无 modifier 时倍率为 1。
            Assert.Equal(1f, Store.AttributeAggregator.GetComputed(towerId, CatalogRegistries.DamageOutputMultiplier, 1f), 3);
            Assert.Equal(1f, Store.AttributeAggregator.GetComputed(enemyId, CatalogRegistries.DamageOutputMultiplier, 1f), 3);
            Assert.Equal(1f, Store.AttributeAggregator.GetComputed(playerId, CatalogRegistries.DamageOutputMultiplier, 1f), 3);

            Store.AddAttributeModifier(towerId,
                new ModifierDefinition(CatalogRegistries.DamageOutputMultiplier, AttributeModifierOp.Add, 0.5f));
            Assert.Equal(9f * 1.5f, Store.GetTowerAttackDamage(towerId), 3);
            Assert.Equal(7f, Store.GetEnemyAttackDamageProjection(enemyId), 3);
            Assert.Equal(12f, Store.GetPlayerAttackDamageProjection(playerId), 3);
        }

        [Fact]
        public void EnemyAttackBaseSyncsOnlyAfterSetterAndAtSyncBoundary()
        {
            int enemyId = Enemy(e => e.Damage = 7f);
            Store.UseComputedAttributes = true;
            Store.ApplyComputedAttributeModeAtFrameBoundary();
            Store.SyncComputedAttributeBases();
            Assert.Equal(7f, Store.AttributeAggregator.GetComputed(enemyId, CatalogRegistries.AttackDamage, -1f), 3);

            // 写入点标脏，但聚合器仍是上次同步的快照，直到下一次同步边界
            Store.SetEnemyAttackDamage(enemyId, 11f);
            Assert.Equal(7f, Store.AttributeAggregator.GetComputed(enemyId, CatalogRegistries.AttackDamage, -1f), 3);
            Store.SyncComputedAttributeBases();
            Assert.Equal(11f, Store.AttributeAggregator.GetComputed(enemyId, CatalogRegistries.AttackDamage, -1f), 3);

            // 清空聚合器条目后，即使值未变，下次同步也会重新推入基值
            Store.ClearComputedAttributes(enemyId);
            Store.SyncComputedAttributeBases();
            Assert.Equal(11f, Store.AttributeAggregator.GetComputed(enemyId, CatalogRegistries.AttackDamage, -1f), 3);
        }

#if DEBUG
        [Fact]
        public void DirectEnemyDamageWriteIsCaughtByDebugSyncCheck()
        {
            int enemyId = Enemy(e => e.Damage = 7f);
            Store.UseComputedAttributes = true;
            Store.ApplyComputedAttributeModeAtFrameBoundary();
            Store.SyncComputedAttributeBases();

            // 绕过 SetEnemyAttackDamage 的直接写入不会标脏：Debug 构建同步时必须报错而非静默陈旧
            Store.EnemyDamage[enemyId] = 13f;
            Assert.Throws<System.InvalidOperationException>(() => Store.SyncComputedAttributeBases());
        }
#endif
    }
}
