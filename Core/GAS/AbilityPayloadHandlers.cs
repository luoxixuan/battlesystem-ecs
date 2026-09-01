using System;

namespace BattleSystemECS.Core.GAS
{
    /// <summary>Authoritative production support table for typed execution definitions.</summary>
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

    /// <summary>Fixed production composition for domain payload adapters.</summary>
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

        private int Find(ExecutionDefinition execution)
        {
            for (int i = 0; i < _handlers.Length; i++)
                if (_handlers[i].Supports(execution)) return i;
            return -1;
        }
    }
}
