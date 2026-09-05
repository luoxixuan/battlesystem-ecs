using System;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Content.Contracts;

namespace BattleSystemECS.Systems
{
    /// <summary>适配在 GAS 存储外实现的生产载荷。</summary>
    public sealed class ProductionAbilityPayloadHandler : IAbilityPayloadHandler
    {
        private readonly ComponentStore _store;
        private readonly IResurrectionPort _resurrection;
        private readonly ISnapshotRestorePort _snapshotRestore;

        public ProductionAbilityPayloadHandler(ComponentStore store, IResurrectionPort resurrection,
            ISnapshotRestorePort snapshotRestore)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _resurrection = resurrection ?? throw new ArgumentNullException(nameof(resurrection));
            _snapshotRestore = snapshotRestore ?? throw new ArgumentNullException(nameof(snapshotRestore));
        }

        public bool Supports(ExecutionDefinition execution) =>
            execution.Payload == EffectPayloadKind.Resurrect && execution.Operation == ExecutionOperation.Resurrect ||
            execution.Payload == EffectPayloadKind.Resource && execution.Operation == ExecutionOperation.RestoreSnapshot;

        public bool CanCommit(AbilityPayloadContext context)
        {
            if (!Supports(context.Execution) || !context.Source.IsValid) return false;
            int owner = context.Request.OwnerPlayerId;
            if ((uint)owner >= ComponentStore.MAX_PLAYERS || !_store.GetEntityHandle(owner).IsValid) return false;
            if (context.Execution.Payload == EffectPayloadKind.Resource)
                return context.Magnitude >= 0f && !float.IsNaN(context.Magnitude) && !float.IsInfinity(context.Magnitude) &&
                       _snapshotRestore.GetSampleCount(owner) > 0 && _store.ResourceResolver.CanAccept(3, 3);

            float radius = context.Ability.Targeting.Radius > 0f
                ? context.Ability.Targeting.Radius : context.Ability.Targeting.Range;
            float fraction = context.Magnitude > 0f ? context.Magnitude : 0.3f;
            return !float.IsNaN(fraction) && !float.IsInfinity(fraction) &&
                   _resurrection.CanMassResurrect(_store.PositionX[context.Source.Index],
                       _store.PositionY[context.Source.Index], radius);
        }

        public void ContributeCommitCapacity(AbilityPayloadContext context,
            ref int resourceRequests, ref int resourceEvents, ref int damageRequests, ref int damageEvents)
        {
            if (context.Execution.Payload == EffectPayloadKind.Resource)
            {
                resourceRequests += 3;
                resourceEvents += 3;
            }
        }

        public int Commit(AbilityPayloadContext context)
        {
            int owner = context.Request.OwnerPlayerId;
            if (context.Execution.Payload == EffectPayloadKind.Resurrect)
            {
                float radius = context.Ability.Targeting.Radius > 0f
                    ? context.Ability.Targeting.Radius : context.Ability.Targeting.Range;
                return _resurrection.MassResurrect(owner, _store.PositionX[context.Source.Index],
                    _store.PositionY[context.Source.Index], radius,
                    context.Magnitude > 0f ? context.Magnitude : 0.3f);
            }

            float restored = _snapshotRestore.RestoreFromSnapshot(context.Source.Index, owner, context.Magnitude);
            if (restored < 0f) return -1;
            return 1;
        }
    }
}
