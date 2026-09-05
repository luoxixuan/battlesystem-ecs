using System;

namespace BattleSystemECS.Core.GAS
{
    /// <summary>类型化执行定义的生产支持表。</summary>
    public static class ProductionAbilityPayloadRegistry
    {
        public static bool Supports(ExecutionDefinition execution)
        {
            switch (execution.Payload)
            {
                case EffectPayloadKind.Damage: return Matches(execution.Operation, ExecutionOperation.ApplyDamage);
                case EffectPayloadKind.Heal: return Matches(execution.Operation, ExecutionOperation.ApplyHeal);
                case EffectPayloadKind.Shield: return Matches(execution.Operation, ExecutionOperation.ApplyShield);
                case EffectPayloadKind.Slow: return Matches(execution.Operation, ExecutionOperation.ApplySlow);
                case EffectPayloadKind.CrowdControl: return Matches(execution.Operation, ExecutionOperation.ApplyCrowdControl);
                case EffectPayloadKind.GameplayEvent: return execution.Operation == ExecutionOperation.Default;
                case EffectPayloadKind.Status:
                    return execution.Operation == ExecutionOperation.ApplyEnemyBuff ||
                           execution.Operation == ExecutionOperation.ApplyTowerSilence;
                case EffectPayloadKind.Dispel:
                    return execution.Operation == ExecutionOperation.RemoveDispellableEffects;
                case EffectPayloadKind.Freeze:
                    return execution.Operation == ExecutionOperation.ApplyFreeze;
                case EffectPayloadKind.Telegraph:
                    return execution.Operation == ExecutionOperation.QueueTelegraph;
                case EffectPayloadKind.Resurrect: return execution.Operation == ExecutionOperation.Resurrect;
                case EffectPayloadKind.Resource: return execution.Operation == ExecutionOperation.RestoreSnapshot;
                case EffectPayloadKind.WorldAction:
                    return execution.Operation == ExecutionOperation.SummonEnemy ||
                           execution.Operation == ExecutionOperation.PrepareStealth;
                default: return false;
            }
        }

        private static bool Matches(ExecutionOperation actual, ExecutionOperation expected) =>
            actual == ExecutionOperation.Default || actual == expected;
    }

    /// <summary>领域载荷适配器的固定生产组合。</summary>
    public sealed class AbilityPayloadHandlerChain : IAbilityPayloadHandler
    {
        private readonly IAbilityPayloadHandler[] _handlers;

        public AbilityPayloadHandlerChain(params IAbilityPayloadHandler[] handlers)
        {
            _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
            for (int i = 0; i < _handlers.Length; i++)
                if (_handlers[i] == null) throw new ArgumentException("payload handler chain contains null", nameof(handlers));
        }

        public bool Supports(ExecutionDefinition execution) => Find(execution) >= 0;
        public bool CanCommit(AbilityPayloadContext context)
        {
            int index = Find(context.Execution);
            return index >= 0 && _handlers[index].CanCommit(context);
        }

        public int Commit(AbilityPayloadContext context)
        {
            int index = Find(context.Execution);
            if (index < 0) throw new InvalidOperationException("payload was not selected during ability planning");
            return _handlers[index].Commit(context);
        }

        public void ContributeCommitCapacity(AbilityPayloadContext context,
            ref int resourceRequests, ref int resourceEvents, ref int damageRequests, ref int damageEvents)
        {
            int index = Find(context.Execution);
            if (index < 0) return;
            _handlers[index].ContributeCommitCapacity(context,
                ref resourceRequests, ref resourceEvents, ref damageRequests, ref damageEvents);
        }

        private int Find(ExecutionDefinition execution)
        {
            for (int i = 0; i < _handlers.Length; i++)
                if (_handlers[i].Supports(execution)) return i;
            return -1;
        }
    }
}
