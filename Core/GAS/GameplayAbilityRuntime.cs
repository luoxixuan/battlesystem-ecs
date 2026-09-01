using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Components;

namespace BattleSystemECS.Core.GAS
{
    public enum AbilityActivationRejectReason { None, InvalidRequest, Cooldown, NoTarget, PhaseNotAllowed, Cost, UnsupportedDefinition }

    public readonly struct AbilityActivationRequest
    {
        public readonly int OwnerId;
        public readonly int Slot;
        public readonly float Cooldown;
        public readonly int TargetId;
        public readonly AbilityId Ability;
        public readonly EffectId Effect;
        public readonly TriggerId Trigger;
        public readonly float Cost;
        public readonly float MagnitudeOverride;
        public AbilityActivationRequest(int ownerId, int slot, float cooldown, int targetId = -1,
            AbilityId ability = default(AbilityId), EffectId effect = default(EffectId), TriggerId trigger = default(TriggerId), float cost = 0f)
            : this(ownerId, slot, cooldown, targetId, ability, effect, trigger, cost, float.NaN) { }
        public AbilityActivationRequest(int ownerId, int slot, float cooldown, int targetId,
            AbilityId ability, EffectId effect, TriggerId trigger, float cost, float magnitudeOverride)
        { OwnerId = ownerId; Slot = slot; Cooldown = cooldown; TargetId = targetId; Ability = ability; Effect = effect; Trigger = trigger; Cost = cost; MagnitudeOverride = magnitudeOverride; }
        public AbilityActivationRequest(int ownerId, int slot, float cooldown, int targetId,
            AbilityId ability, float magnitudeOverride)
            : this(ownerId, slot, cooldown, targetId, ability, default(EffectId), default(TriggerId), 0f, magnitudeOverride) { }
    }

    public readonly struct AbilityActivationResult
    {
        public readonly bool Accepted;
        public readonly int OwnerId;
        public readonly int Slot;
        public readonly AbilityActivationRejectReason Reason;
        public readonly int AppliedEffects;
        public AbilityActivationResult(bool accepted, int ownerId, int slot, AbilityActivationRejectReason reason = AbilityActivationRejectReason.None, int appliedEffects = 0)
        { Accepted = accepted; OwnerId = ownerId; Slot = slot; Reason = reason; AppliedEffects = appliedEffects; }
    }

    /// <summary>
    /// Single writer for ability-slot activation state. Legacy systems may inspect
    /// definitions, but cooldown ownership stays in the ECS ability store through
    /// this boundary.
    /// </summary>
    public static class GameplayAbilityRuntime
    {
        /// <summary>Catalog-backed activation boundary used by domain adapters.</summary>
        public static AbilityActivationResult Activate(ComponentStore store, GameplayCatalog catalog, float[] cooldowns, AbilityActivationRequest request)
        {
            if (store == null || catalog == null || cooldowns == null || request.Slot < 0 || request.Slot >= cooldowns.Length)
                return Reject(request, AbilityActivationRejectReason.InvalidRequest);
            if (!catalog.TryGetAbility(request.Ability, out var ability)) return Reject(request, AbilityActivationRejectReason.InvalidRequest);
            if (request.Effect.Value != 0 && !Contains(ability.Effects, request.Effect)) return Reject(request, AbilityActivationRejectReason.InvalidRequest);
            if (request.Trigger.Value != 0 && !Contains(ability.TriggerRefs, request.Trigger)) return Reject(request, AbilityActivationRejectReason.InvalidRequest);
            var source = store.GetEntityHandle(request.OwnerId);
            int targetId = request.TargetId >= 0 ? request.TargetId : request.OwnerId;
            var target = store.GetEntityHandle(targetId);
            if (!source.IsValid) return Reject(request, AbilityActivationRejectReason.InvalidRequest);
            if (!target.IsValid) return Reject(request, AbilityActivationRejectReason.NoTarget);
            var ready = TryActivate(cooldowns, request);
            if (!ready.Accepted) return ready;
            int applied = 0;
            for (int i = 0; i < ability.Effects.Count; i++)
            {
                if (!catalog.TryGetEffect(ability.Effects[i], out var effect) ||
                    !store.GameplayEffectsRuntime.TryApply(effect.Id, effect, source, target, out _, ownerPlayerId: request.OwnerId))
                    return Reject(request, AbilityActivationRejectReason.InvalidRequest);
                applied++;
            }
            for (int i = 0; i < ability.Executions.Count; i++)
            {
                if (!catalog.TryGetExecution(ability.Executions[i], out var execution)) return Reject(request, AbilityActivationRejectReason.InvalidRequest);
                float magnitude = float.IsNaN(request.MagnitudeOverride) ? execution.Magnitude : request.MagnitudeOverride;
                long sequence = store.AllocateGameplaySequence(targetId);
                if (execution.Payload == EffectPayloadKind.Damage)
                {
                    var result = store.DamageResolver.TryApply(new DamageRequest(source, target, magnitude,
                        DamageType.True, ElementType.None, DamageFlags.None, execution.Stage, DamageCommitBoundary.GameplayResolve,
                        sequence, ability: ability.Id, effect: request.Effect, ownerPlayerId: request.OwnerId));
                    if (!result.Accepted) return Reject(request, AbilityActivationRejectReason.InvalidRequest);
                    applied++;
                }
                else if (execution.Payload == EffectPayloadKind.Heal)
                {
                    if (!store.ResourceResolver.TryApply(new HealRequest(source, target, magnitude, sequence, request.OwnerId)).Accepted)
                        return Reject(request, AbilityActivationRejectReason.InvalidRequest);
                    applied++;
                }
            }
            if (!AbilityCommit(cooldowns, request.Slot, ability.Cooldown)) return Reject(request, AbilityActivationRejectReason.Cooldown);
            store.DamageResolver.Events.TryPublish(new GameplayEvent(GameplayEventType.AbilityActivated, source, target,
                store.AllocateGameplaySequence(targetId), ownerPlayerId: request.OwnerId));
            return new AbilityActivationResult(true, request.OwnerId, request.Slot, appliedEffects: applied);
        }

        /// <summary>Catalog-backed activation for ECS ability slots.</summary>
        public static AbilityActivationResult Activate(ComponentStore store, GameplayCatalog catalog, int entityId, int slot, AbilityActivationRequest request)
        {
            if (store == null || catalog == null || entityId < 0 || entityId >= ComponentStore.MAX_ENTITIES ||
                slot < 0 || slot >= store.AbilityCount[entityId])
                return Reject(request, AbilityActivationRejectReason.InvalidRequest);
            if (!catalog.TryGetAbility(request.Ability, out var ability))
                return Reject(request, AbilityActivationRejectReason.InvalidRequest);
            var source = store.GetEntityHandle(request.OwnerId);
            int targetId = request.TargetId >= 0 ? request.TargetId : request.OwnerId;
            var target = store.GetEntityHandle(targetId);
            if (!source.IsValid) return Reject(request, AbilityActivationRejectReason.InvalidRequest);
            if (!target.IsValid) return Reject(request, AbilityActivationRejectReason.NoTarget);
            if (!TryActivate(store, entityId, slot, out _))
                return new AbilityActivationResult(false, request.OwnerId, slot, AbilityActivationRejectReason.Cooldown);
            int applied = 0;
            for (int i = 0; i < ability.Effects.Count; i++)
            {
                if (!catalog.TryGetEffect(ability.Effects[i], out var effect) ||
                    !store.GameplayEffectsRuntime.TryApply(effect.Id, effect, source, target, out _, ownerPlayerId: request.OwnerId))
                    return Reject(request, AbilityActivationRejectReason.InvalidRequest);
                applied++;
            }
            for (int i = 0; i < ability.Executions.Count; i++)
            {
                if (!catalog.TryGetExecution(ability.Executions[i], out var execution)) return Reject(request, AbilityActivationRejectReason.InvalidRequest);
                long sequence = store.AllocateGameplaySequence(targetId);
                if (execution.Payload == EffectPayloadKind.Damage)
                {
                    if (!store.DamageResolver.TryApply(new DamageRequest(source, target, execution.Magnitude,
                        DamageType.True, ElementType.None, DamageFlags.None, execution.Stage, DamageCommitBoundary.GameplayResolve,
                        sequence, ability: ability.Id, effect: request.Effect, ownerPlayerId: request.OwnerId)).Accepted)
                        return Reject(request, AbilityActivationRejectReason.InvalidRequest);
                    applied++;
                }
                else if (execution.Payload == EffectPayloadKind.Heal)
                {
                    if (!store.ResourceResolver.TryApply(new HealRequest(source, target, execution.Magnitude, sequence, request.OwnerId)).Accepted)
                        return Reject(request, AbilityActivationRejectReason.InvalidRequest);
                    applied++;
                }
            }
            var instance = store.GetAbility(entityId, slot);
            instance.Activate();
            store.SetAbility(entityId, slot, instance);
            store.DamageResolver.Events.TryPublish(new GameplayEvent(GameplayEventType.AbilityActivated, source, target,
                store.AllocateGameplaySequence(targetId), ownerPlayerId: request.OwnerId));
            return new AbilityActivationResult(true, request.OwnerId, slot, appliedEffects: applied);
        }
        private static bool Contains(IReadOnlyList<EffectId> ids, EffectId id) { for (int i = 0; i < ids.Count; i++) if (ids[i].Value == id.Value) return true; return false; }
        private static bool Contains(IReadOnlyList<TriggerId> ids, TriggerId id) { for (int i = 0; i < ids.Count; i++) if (ids[i].Value == id.Value) return true; return false; }
        private static AbilityActivationResult Reject(AbilityActivationRequest request, AbilityActivationRejectReason reason) => new AbilityActivationResult(false, request.OwnerId, request.Slot, reason);

        public static AbilityActivationResult TryActivate(float[] cooldowns, AbilityActivationRequest request)
        {
            var reason = cooldowns == null || request.Slot < 0 || request.Slot >= (cooldowns?.Length ?? 0)
                ? AbilityActivationRejectReason.InvalidRequest
                : cooldowns[request.Slot] > 0f ? AbilityActivationRejectReason.Cooldown : AbilityActivationRejectReason.None;
            return new AbilityActivationResult(reason == AbilityActivationRejectReason.None, request.OwnerId, request.Slot, reason);
        }

        public static AbilityActivationResult AbilityCommit(float[] cooldowns, AbilityActivationRequest request)
        {
            var ready = TryActivate(cooldowns, request);
            if (!ready.Accepted) return ready;
            bool accepted = AbilityCommit(cooldowns, request.Slot, request.Cooldown);
            return new AbilityActivationResult(accepted, request.OwnerId, request.Slot,
                accepted ? AbilityActivationRejectReason.None : AbilityActivationRejectReason.Cooldown);
        }

        public static bool TryActivate(float[] cooldowns, int index)
        {
            return cooldowns != null && index >= 0 && index < cooldowns.Length && cooldowns[index] <= 0f;
        }

        public static bool AbilityCommit(float[] cooldowns, int index, float cooldown)
        {
            if (!TryActivate(cooldowns, index)) return false;
            cooldowns[index] = System.Math.Max(0f, cooldown);
            return true;
        }

        public static bool TickCooldown(float[] cooldowns, int index, float deltaSeconds)
        {
            if (cooldowns == null || deltaSeconds <= 0f || index < 0 || index >= cooldowns.Length) return false;
            cooldowns[index] = System.Math.Max(0f, cooldowns[index] - deltaSeconds);
            return true;
        }

        public static bool TryActivate(ComponentStore store, int entityId, int slot, out AbilityInstance ability)
        {
            ability = default(AbilityInstance);
            if (store == null || entityId < 0 || entityId >= ComponentStore.MAX_ENTITIES ||
                slot < 0 || slot >= store.AbilityCount[entityId]) return false;
            ability = store.GetAbility(entityId, slot);
            return ability.CanActivate();
        }

        public static bool AbilityCommit(ComponentStore store, int entityId, int slot)
        {
            if (!TryActivate(store, entityId, slot, out var ability)) return false;
            ability.Activate();
            store.SetAbility(entityId, slot, ability);
            return true;
        }

        public static bool TickCooldown(ComponentStore store, int entityId, int slot, float deltaSeconds)
        {
            if (store == null || deltaSeconds <= 0f || entityId < 0 || entityId >= ComponentStore.MAX_ENTITIES ||
                slot < 0 || slot >= store.AbilityCount[entityId]) return false;
            var ability = store.GetAbility(entityId, slot);
            if (ability.CurrentCooldown <= 0f) return true;
            ability.CurrentCooldown = System.Math.Max(0f, ability.CurrentCooldown - deltaSeconds);
            store.SetAbility(entityId, slot, ability);
            return true;
        }
    }
}
