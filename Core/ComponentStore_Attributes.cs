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
        /// <summary>
        /// 玩家伤害权威入口：固定 ownerPlayerId=playerId，在提交时刻分配 sequence。
        /// 拒绝时不抛异常（与 Ability 两阶段合同不同）。
        /// </summary>
        internal bool ApplyPlayerDamageAuthority(int sourceId, int playerId, float amount, out float applied)
        {
            applied = 0f;
            var source = GetEntityHandle(sourceId);
            var target = GetEntityHandle(playerId);
            if (!source.IsValid || !target.IsValid) return false;
            var request = new PlayerDamageRequest(source, target, amount, AllocateGameplaySequence(playerId),
                ownerPlayerId: playerId);
            var result = ResourceResolver.TryApply(request);
            if (!result.Accepted) return false;
            applied = result.Applied;
            return true;
        }
        internal bool ApplyPlayerDamageAuthority(int sourceId, int playerId, float amount)
        {
            float applied;
            return ApplyPlayerDamageAuthority(sourceId, playerId, amount, out applied);
        }
        internal bool CanApplyPlayerDamageAuthority(int sourceId, int playerId, float amount)
        {
            var source = GetEntityHandle(sourceId);
            var target = GetEntityHandle(playerId);
            if (!source.IsValid || !target.IsValid) return false;
            if (!ResourceResolver.CanApplyPlayerDamage(new PlayerDamageRequest(source, target, amount, 0L,
                ownerPlayerId: playerId)))
                return false;
            // PlayerDamage 直接发布 1 或 2 个 critical 事实（致死多 DeathQueued），不进 pending。
            // 预检必须挡住队列溢出，否则调用方会在 TryApply 失败前先消耗 stealth 等一次性状态。
            return ResourceResolver.CanAccept(0, 2);
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
            else { AttributeAggregator.MarkAllDirty(); MarkAllEnemyAttackBasesDirty(); }
        }

        /// <summary>
        /// 敌人 AttackDamage 基值写入点标脏：写入点经 <see cref="SetEnemyAttackDamage"/> 置位本敌人槽
        /// （按槽独占写，Parallel.For 内同样安全）；attribute.aggregate 只把标脏敌人的基值推入聚合器，
        /// 仍在该节点统一快照，帧内可见时序与旧的全量同步一致。Debug 构建在同步后逐个核对基值，漏标即抛出。
        /// </summary>
        internal bool[] EnemyAttackBaseDirty = new bool[MAX_ENTITIES];

        /// <summary>写入敌人攻击力基值并标脏（生产代码不得直接写 EnemyDamage 数组）。</summary>
        public void SetEnemyAttackDamage(int enemyId, float damage)
        {
            EnemyDamage[enemyId] = damage;
            EnemyAttackBaseDirty[enemyId] = true;
        }

        private void MarkAllEnemyAttackBasesDirty()
        {
            for (int i = 0; i < ActiveEnemyIds.Count; i++) EnemyAttackBaseDirty[ActiveEnemyIds[i]] = true;
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
                AttributeAggregator.SetBase(towerId, CatalogRegistries.AttackDamage, TowerAttackDamage[towerId]);
            }
            for (int i = 0; i < ActiveEnemyIds.Count; i++)
            {
                int enemyId = ActiveEnemyIds[i];
                if (!EnemyAttackBaseDirty[enemyId]) continue;
                EnemyAttackBaseDirty[enemyId] = false;
                AttributeAggregator.SetBase(enemyId, CatalogRegistries.AttackDamage, EnemyDamage[enemyId]);
            }
            VerifyEnemyAttackBasesSynced();
            int playerId = PlayerEntityId;
            if ((uint)playerId < MAX_PLAYERS && PositionActive[playerId] && !EnemyActive[playerId])
            {
                AttributeAggregator.SetBase(playerId, CatalogRegistries.AttackDamage, GetPlayerAttackDamage(playerId));
                AttributeAggregator.SetBase(playerId, new AttributeKey(1), PlayerAttackRange[playerId]);
                AttributeAggregator.SetBase(playerId, new AttributeKey(5), PlayerPreFightCritBonus[playerId]);
                AttributeAggregator.SetBase(playerId, new AttributeKey(10), PlayerArmor[playerId]);
            }
        }
        internal void ClearComputedAttributes(int entityId)
        {
            AttributeAggregator.ClearEntity(entityId);
            // 聚合器基值已清空：若该槽仍是/将是敌人，下次同步需重新推入
            if (IsValidEntity(entityId)) EnemyAttackBaseDirty[entityId] = true;
        }

        /// <summary>Debug 构建：同步后核对每个活跃敌人的聚合器基值与 EnemyDamage 一致，捕获绕过 SetEnemyAttackDamage 的写入。</summary>
        [System.Diagnostics.Conditional("DEBUG")]
        private void VerifyEnemyAttackBasesSynced()
        {
            for (int i = 0; i < ActiveEnemyIds.Count; i++)
            {
                int enemyId = ActiveEnemyIds[i];
                if (!AttributeAggregator.BaseMatches(enemyId, CatalogRegistries.AttackDamage, EnemyDamage[enemyId]))
                    throw new InvalidOperationException($"EnemyDamage[{enemyId}] 被直接写入且未标脏；请改用 SetEnemyAttackDamage");
            }
        }
        public float GetTowerAttackDamage(int towerId)
        {
            var baseValue = TowerAttackDamage[towerId];
            if (!UseComputedAttributes) return baseValue;
            float attack = AttributeAggregator.GetComputed(towerId, CatalogRegistries.AttackDamage, baseValue);
            float multiplier = AttributeAggregator.GetComputed(towerId, CatalogRegistries.DamageOutputMultiplier, 1f);
            return attack * multiplier;
        }
        public float GetPlayerAttackDamageProjection(int playerId)
        {
            var baseValue = GetPlayerAttackDamage(playerId);
            if (!_useComputedAttributes) return baseValue;
            float attack = AttributeAggregator.GetComputed(playerId, CatalogRegistries.AttackDamage, baseValue);
            float multiplier = AttributeAggregator.GetComputed(playerId, CatalogRegistries.DamageOutputMultiplier, 1f);
            return attack * multiplier;
        }
        public float GetEnemyAttackDamageProjection(int enemyId)
        {
            var baseValue = EnemyDamage[enemyId];
            if (!_useComputedAttributes) return baseValue;
            float attack = AttributeAggregator.GetComputed(enemyId, CatalogRegistries.AttackDamage, baseValue);
            float multiplier = AttributeAggregator.GetComputed(enemyId, CatalogRegistries.DamageOutputMultiplier, 1f);
            return attack * multiplier;
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
