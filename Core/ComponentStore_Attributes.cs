using System;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Components;

namespace BattleSystemECS.Core
{
    public partial class ComponentStore
    {
        public AttributeAggregator AttributeAggregator { get; } = new AttributeAggregator();
        public ResourceResolver ResourceResolver { get; }
        public DamageResolver DamageResolver { get; }
        internal bool ApplyDamageAuthority(int sourceId, int targetId, float amount, int ownerPlayerId, DamageType damageType = DamageType.True, ElementType element = ElementType.None, DamageFlags flags = DamageFlags.None, DamageCommitBoundary boundary = DamageCommitBoundary.GameplayResolve, long parentSequence = 0L, DamageAmountStage stage = DamageAmountStage.PostMitigation, long provenanceId = 0L, int provenanceDepth = 0)
        {
            var source = GetEntityHandle(sourceId); var target = GetEntityHandle(targetId);
            if (!source.IsValid || !target.IsValid) return false;
            if (!EnemyActive[targetId] || !GetEntityHandle(targetId).Equals(target)) return false;
            var result = DamageResolver.TryApplyValidated(new DamageRequest(source, target, amount, damageType, element, flags, stage, boundary, AllocateGameplaySequence(targetId), parentSequence, ownerPlayerId: ownerPlayerId, provenanceId: provenanceId, provenanceDepth: provenanceDepth));
            return result.Accepted;
        }
        internal bool ApplyPlayerResourceAuthority(int sourceId, int playerId, AttributeKey resource, float delta, long sequence = 0L)
        {
            var source = GetEntityHandle(sourceId); var target = GetEntityHandle(playerId);
            if (!source.IsValid || !target.IsValid) return false;
            var result = ResourceResolver.TryApply(new ResourceRequest(source, target, resource, delta, ResourceOperation.Add, 0, sequence == 0L ? AllocateGameplaySequence(playerId) : sequence, ownerPlayerId: playerId));
            return result.Accepted;
        }
        internal bool ApplyEnemyResourceAuthority(int sourceId, int enemyId, AttributeKey resource, float delta, long sequence = 0L, int ownerPlayerId = 0)
        {
            var source = GetEntityHandle(sourceId); var target = GetEntityHandle(enemyId);
            if (!source.IsValid || !target.IsValid) return false;
            var result = ResourceResolver.TryApply(new ResourceRequest(source, target, resource, delta, ResourceOperation.Add, 0, sequence == 0L ? AllocateGameplaySequence(enemyId) : sequence, ownerPlayerId: ownerPlayerId));
            return result.Accepted;
        }
        internal bool SetPlayerResourceAuthority(int sourceId, int playerId, AttributeKey resource, float value, long sequence = 0L)
        {
            var source = GetEntityHandle(sourceId); var target = GetEntityHandle(playerId);
            if (!source.IsValid || !target.IsValid) return false;
            var result = ResourceResolver.TryApply(new ResourceRequest(source, target, resource, value, ResourceOperation.Set, 0, sequence == 0L ? AllocateGameplaySequence(playerId) : sequence, ownerPlayerId: playerId));
            return result.Accepted;
        }
        internal bool SetEnemyResourceAuthority(int sourceId, int enemyId, AttributeKey resource, float value, long sequence = 0L)
        {
            var source = GetEntityHandle(sourceId); var target = GetEntityHandle(enemyId);
            if (!source.IsValid || !target.IsValid) return false;
            var result = ResourceResolver.TryApply(new ResourceRequest(source, target, resource, value, ResourceOperation.Set, 0, sequence == 0L ? AllocateGameplaySequence(enemyId) : sequence, ownerPlayerId: 0));
            return result.Accepted;
        }
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
            for (int i = 0; i < ActiveEnemyIds.Count; i++)
            {
                int enemyId = ActiveEnemyIds[i];
                AttributeAggregator.SetBase(enemyId, CatalogRegistries.AttackDamage, EnemyDamage[enemyId]);
            }
            int playerId = PlayerEntityId;
            if ((uint)playerId < MAX_PLAYERS && PositionActive[playerId] && !EnemyActive[playerId])
            {
                AttributeAggregator.SetBase(playerId, new AttributeKey(0), GetPlayerAttackDamage(playerId));
                AttributeAggregator.SetBase(playerId, new AttributeKey(1), PlayerAttackRange[playerId]);
                AttributeAggregator.SetBase(playerId, new AttributeKey(5), PlayerPreFightCritBonus[playerId]);
                AttributeAggregator.SetBase(playerId, new AttributeKey(10), PlayerArmor[playerId]);
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
        public float GetEnemyAttackDamageProjection(int enemyId)
        {
            var baseValue = EnemyDamage[enemyId];
            return !_useComputedAttributes ? baseValue : AttributeAggregator.GetComputed(enemyId,
                CatalogRegistries.AttackDamage, baseValue);
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
