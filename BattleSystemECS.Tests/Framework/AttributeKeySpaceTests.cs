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
    }
}
