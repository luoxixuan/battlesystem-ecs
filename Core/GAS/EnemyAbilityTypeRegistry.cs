using System;

namespace BattleSystemECS.Core.GAS
{
    internal enum EnemyAbilityKind
    {
        Unknown,
        SelfHeal,
        HealAllies,
        AoeDamage,
        StunAoe,
        SlowAoe,
        SummonMinion,
        StealthAttack,
        BuffAllies,
        SilenceTower,
        DispelTower
    }

    internal enum EnemyAbilityDispatchMode
    {
        TypedCatalog,
        RuntimeAdapter,
        CompatibilityOnly
    }

    internal readonly struct EnemyAbilityTypeDescriptor
    {
        public EnemyAbilityTypeDescriptor(EnemyAbilityKind kind, string name,
            EnemyAbilityDispatchMode dispatchMode, TargetingShape targeting,
            ExecutionOperation operation, EffectPayloadKind? payload = null)
        {
            Kind = kind;
            Name = name;
            DispatchMode = dispatchMode;
            Targeting = targeting;
            Operation = operation;
            Payload = payload;
        }

        public EnemyAbilityKind Kind { get; }
        public string Name { get; }
        public EnemyAbilityDispatchMode DispatchMode { get; }
        public TargetingShape Targeting { get; }
        public ExecutionOperation Operation { get; }
        public EffectPayloadKind? Payload { get; }
    }

    internal static class EnemyAbilityTypeRegistry
    {
        private static readonly EnemyAbilityTypeDescriptor SelfHeal = Typed(
            EnemyAbilityKind.SelfHeal, "self_heal", TargetingShape.Heal,
            ExecutionOperation.ApplyHeal, EffectPayloadKind.Heal);
        private static readonly EnemyAbilityTypeDescriptor HealAllies = Typed(
            EnemyAbilityKind.HealAllies, "heal_allies", TargetingShape.Heal,
            ExecutionOperation.ApplyHeal, EffectPayloadKind.Heal);
        private static readonly EnemyAbilityTypeDescriptor AoeDamage = Typed(
            EnemyAbilityKind.AoeDamage, "aoe_damage", TargetingShape.Circle,
            ExecutionOperation.ApplyDamage, EffectPayloadKind.Damage);
        private static readonly EnemyAbilityTypeDescriptor StunAoe = Typed(
            EnemyAbilityKind.StunAoe, "stun_aoe", TargetingShape.AoeStun,
            ExecutionOperation.ApplyCrowdControl, EffectPayloadKind.CrowdControl);
        private static readonly EnemyAbilityTypeDescriptor SlowAoe = Typed(
            EnemyAbilityKind.SlowAoe, "slow_aoe", TargetingShape.Slow,
            ExecutionOperation.ApplySlow, EffectPayloadKind.Slow);
        private static readonly EnemyAbilityTypeDescriptor SummonMinion = Adapter(
            EnemyAbilityKind.SummonMinion, "summon_minion", ExecutionOperation.SummonEnemy);
        private static readonly EnemyAbilityTypeDescriptor StealthAttack = Adapter(
            EnemyAbilityKind.StealthAttack, "stealth_attack", ExecutionOperation.PrepareStealth);
        private static readonly EnemyAbilityTypeDescriptor BuffAllies = Typed(
            EnemyAbilityKind.BuffAllies, "buff_allies", TargetingShape.Circle,
            ExecutionOperation.ApplyEnemyBuff, EffectPayloadKind.Status);
        private static readonly EnemyAbilityTypeDescriptor SilenceTower = Typed(
            EnemyAbilityKind.SilenceTower, "silence_tower", TargetingShape.Circle,
            ExecutionOperation.ApplyTowerSilence, EffectPayloadKind.Status);
        private static readonly EnemyAbilityTypeDescriptor DispelTower = Typed(
            EnemyAbilityKind.DispelTower, "dispel_tower", TargetingShape.Circle,
            ExecutionOperation.RemoveDispellableEffects, EffectPayloadKind.Dispel);

        public static bool TryResolve(string abilityType, out EnemyAbilityTypeDescriptor descriptor)
        {
            if (string.Equals(abilityType, SelfHeal.Name, StringComparison.OrdinalIgnoreCase)) descriptor = SelfHeal;
            else if (string.Equals(abilityType, HealAllies.Name, StringComparison.OrdinalIgnoreCase)) descriptor = HealAllies;
            else if (string.Equals(abilityType, AoeDamage.Name, StringComparison.OrdinalIgnoreCase)) descriptor = AoeDamage;
            else if (string.Equals(abilityType, StunAoe.Name, StringComparison.OrdinalIgnoreCase)) descriptor = StunAoe;
            else if (string.Equals(abilityType, SlowAoe.Name, StringComparison.OrdinalIgnoreCase)) descriptor = SlowAoe;
            else if (string.Equals(abilityType, SummonMinion.Name, StringComparison.OrdinalIgnoreCase)) descriptor = SummonMinion;
            else if (string.Equals(abilityType, StealthAttack.Name, StringComparison.OrdinalIgnoreCase)) descriptor = StealthAttack;
            else if (string.Equals(abilityType, BuffAllies.Name, StringComparison.OrdinalIgnoreCase)) descriptor = BuffAllies;
            else if (string.Equals(abilityType, SilenceTower.Name, StringComparison.OrdinalIgnoreCase)) descriptor = SilenceTower;
            else if (string.Equals(abilityType, DispelTower.Name, StringComparison.OrdinalIgnoreCase)) descriptor = DispelTower;
            else
            {
                descriptor = default;
                return false;
            }
            return true;
        }

        private static EnemyAbilityTypeDescriptor Typed(EnemyAbilityKind kind, string name,
            TargetingShape targeting, ExecutionOperation operation, EffectPayloadKind payload)
            => new EnemyAbilityTypeDescriptor(kind, name, EnemyAbilityDispatchMode.TypedCatalog,
                targeting, operation, payload);

        private static EnemyAbilityTypeDescriptor Adapter(EnemyAbilityKind kind, string name,
            ExecutionOperation operation)
            => new EnemyAbilityTypeDescriptor(kind, name, EnemyAbilityDispatchMode.RuntimeAdapter,
                TargetingShape.Single, operation, EffectPayloadKind.WorldAction);

    }
}
