using BattleSystemECS.Core.GAS;

namespace BattleSystemECS.Core
{
    public partial class ComponentStore
    {
        public AttributeAggregator AttributeAggregator { get; } = new AttributeAggregator();
        public ResourceResolver ResourceResolver { get; }
        /// <summary>Compatibility rollback switch. Disabled preserves all legacy projections.</summary>
        public bool UseComputedAttributes { get; set; }

        public AttributeModifierHandle AddAttributeModifier(int entityId, ModifierDefinition definition, float capturedMagnitude = float.NaN)
        { return AttributeAggregator.AddModifier(entityId, definition, capturedMagnitude); }
        public bool RemoveAttributeModifier(int entityId, AttributeModifierHandle handle) => AttributeAggregator.RemoveModifier(entityId, handle);
        public float GetTowerAttackDamage(int towerId)
        {
            var baseValue = TowerAttackDamage[towerId];
            if (!UseComputedAttributes) return baseValue;
            AttributeAggregator.SetBase(towerId, new AttributeKey(8), baseValue);
            return AttributeAggregator.GetComputed(towerId, new AttributeKey(8), baseValue);
        }
    }
}
