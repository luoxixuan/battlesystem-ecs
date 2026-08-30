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
        internal void SyncComputedAttributeBases()
        {
            if (!UseComputedAttributes) return;
            for (int i = 0; i < ActiveTowerIds.Count; i++)
            {
                int towerId = ActiveTowerIds[i];
                AttributeAggregator.SetBase(towerId, new AttributeKey(8), TowerAttackDamage[towerId]);
            }
        }
        internal void ClearComputedAttributes(int entityId) => AttributeAggregator.ClearEntity(entityId);
        public float GetTowerAttackDamage(int towerId)
        {
            var baseValue = TowerAttackDamage[towerId];
            if (!UseComputedAttributes) return baseValue;
            return AttributeAggregator.GetComputed(towerId, new AttributeKey(8), baseValue);
        }
    }
}
