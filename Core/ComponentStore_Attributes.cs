using BattleSystemECS.Core.GAS;

namespace BattleSystemECS.Core
{
    public partial class ComponentStore
    {
        public AttributeAggregator AttributeAggregator { get; } = new AttributeAggregator();
        public ResourceResolver ResourceResolver { get; }
        /// <summary>Compatibility rollback switch. Disabled preserves all legacy projections.</summary>
        private bool _useComputedAttributes;
        private bool _requestedComputedAttributes;
        public bool UseComputedAttributes { get { return _useComputedAttributes; } set { _requestedComputedAttributes = value; } }
        internal void ApplyComputedAttributeModeAtFrameBoundary()
        {
            if (_useComputedAttributes == _requestedComputedAttributes) return;
            _useComputedAttributes = _requestedComputedAttributes;
            if (!_useComputedAttributes) AttributeAggregator.ClearAllComputed();
            else AttributeAggregator.MarkAllDirty();
        }

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
            for (int i = 0; i < MAX_PLAYERS; i++)
            {
                AttributeAggregator.SetBase(i, new AttributeKey(0), GetPlayerAttackDamage(i));
                AttributeAggregator.SetBase(i, new AttributeKey(1), PlayerAttackRange[i]);
                AttributeAggregator.SetBase(i, new AttributeKey(5), PlayerPreFightCritBonus[i]);
                AttributeAggregator.SetBase(i, new AttributeKey(10), PlayerArmor[i]);
            }
        }
        internal void ClearComputedAttributes(int entityId) => AttributeAggregator.ClearEntity(entityId);
        public float GetTowerAttackDamage(int towerId)
        {
            var baseValue = TowerAttackDamage[towerId];
            if (!UseComputedAttributes) return baseValue;
            return AttributeAggregator.GetComputed(towerId, new AttributeKey(8), baseValue);
        }
        public float GetPlayerAttackDamageProjection(int playerId)
        {
            var baseValue = GetPlayerAttackDamage(playerId);
            return !_useComputedAttributes ? baseValue : AttributeAggregator.GetComputed(playerId, new AttributeKey(0), baseValue);
        }
        public float GetPlayerCritRateProjection(int playerId, float legacyBase)
        {
            if (!_useComputedAttributes) return legacyBase;
            float baseValue = PlayerPreFightCritBonus[playerId];
            float computed = AttributeAggregator.GetComputed(playerId, new AttributeKey(5), baseValue);
            return legacyBase + (computed - baseValue);
        }
        public float GetPlayerArmorProjection(int playerId)
        { var baseValue = PlayerArmor[playerId]; return !_useComputedAttributes ? baseValue : AttributeAggregator.GetComputed(playerId, new AttributeKey(10), baseValue); }
    }
}
