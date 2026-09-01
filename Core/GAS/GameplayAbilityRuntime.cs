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
        public readonly int OwnerPlayerId;
        public AbilityActivationRequest(int ownerId, int slot, float cooldown, int targetId = -1,
            AbilityId ability = default(AbilityId), EffectId effect = default(EffectId), TriggerId trigger = default(TriggerId), float cost = 0f)
            : this(ownerId, slot, cooldown, targetId, ability, effect, trigger, cost, float.NaN, ownerId) { }
        public AbilityActivationRequest(int ownerId, int slot, float cooldown, int targetId,
            AbilityId ability, EffectId effect, TriggerId trigger, float cost, float magnitudeOverride, int ownerPlayerId = -1)
        { OwnerId = ownerId; Slot = slot; Cooldown = cooldown; TargetId = targetId; Ability = ability; Effect = effect; Trigger = trigger; Cost = cost; MagnitudeOverride = magnitudeOverride; OwnerPlayerId = ownerPlayerId < 0 ? ownerId : ownerPlayerId; }
        public AbilityActivationRequest(int ownerId, int slot, float cooldown, int targetId,
            AbilityId ability, float magnitudeOverride)
            : this(ownerId, slot, cooldown, targetId, ability, default(EffectId), default(TriggerId), 0f, magnitudeOverride, ownerId) { }
        public AbilityActivationRequest(int ownerId, int slot, float cooldown, int targetId,
            AbilityId ability, float magnitudeOverride, int ownerPlayerId)
            : this(ownerId, slot, cooldown, targetId, ability, default(EffectId), default(TriggerId), 0f, magnitudeOverride, ownerPlayerId) { }
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

    public readonly struct AbilityPayloadContext
    {
        public readonly ComponentStore Store;
        public readonly AbilityDefinition Ability;
        public readonly ExecutionDefinition Execution;
        public readonly AbilityActivationRequest Request;
        public readonly EntityHandle Source;
        public readonly EntityHandle Target;
        public readonly float Magnitude;
        public AbilityPayloadContext(ComponentStore store, AbilityDefinition ability, ExecutionDefinition execution,
            AbilityActivationRequest request, EntityHandle source, EntityHandle target, float magnitude)
        { Store = store; Ability = ability; Execution = execution; Request = request; Source = source; Target = target; Magnitude = magnitude; }
    }

    /// <summary>
    /// Domain extension for payloads that need services outside the GAS store.
    /// CanCommit is a read-only planning pass; once it returns true, Commit must not reject.
    /// </summary>
    public interface IAbilityPayloadHandler
    {
        bool CanCommit(AbilityPayloadContext context);
        int Commit(AbilityPayloadContext context);
    }

    /// <summary>
    /// Single writer for ability-slot activation state. Legacy systems may inspect
    /// definitions, but cooldown ownership stays in the ECS ability store through
    /// this boundary.
    /// </summary>
    public static class GameplayAbilityRuntime
    {
        /// <summary>Catalog-backed activation boundary used by domain adapters.</summary>
        public static AbilityActivationResult Activate(ComponentStore store, GameplayCatalog catalog, float[] cooldowns,
            AbilityActivationRequest request, IAbilityPayloadHandler payloadHandler = null)
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
            var validation = ValidatePlan(store, catalog, ability, request, source, target, payloadHandler);
            if (validation != AbilityActivationRejectReason.None) return Reject(request, validation);
            int applied = CommitPlan(store, catalog, ability, request, source, target, payloadHandler);
            if (applied < 0) return Reject(request, AbilityActivationRejectReason.InvalidRequest);
            cooldowns[request.Slot] = Math.Max(0f, ability.Cooldown);
            PublishActivation(store, request, source, target, targetId);
            return new AbilityActivationResult(true, request.OwnerId, request.Slot, appliedEffects: applied);
        }

        /// <summary>Catalog-backed activation for ECS ability slots.</summary>
        public static AbilityActivationResult Activate(ComponentStore store, GameplayCatalog catalog, int entityId, int slot,
            AbilityActivationRequest request, IAbilityPayloadHandler payloadHandler = null)
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
            var validation = ValidatePlan(store, catalog, ability, request, source, target, payloadHandler);
            if (validation != AbilityActivationRejectReason.None) return Reject(request, validation);
            int applied = CommitPlan(store, catalog, ability, request, source, target, payloadHandler);
            if (applied < 0) return Reject(request, AbilityActivationRejectReason.InvalidRequest);
            var instance = store.GetAbility(entityId, slot);
            instance.Activate();
            store.SetAbility(entityId, slot, instance);
            PublishActivation(store, request, source, target, targetId);
            return new AbilityActivationResult(true, request.OwnerId, slot, appliedEffects: applied);
        }

        private static AbilityActivationRejectReason ValidatePlan(ComponentStore store, GameplayCatalog catalog,
            AbilityDefinition ability, AbilityActivationRequest request, EntityHandle source, EntityHandle target,
            IAbilityPayloadHandler payloadHandler)
        {
            if (!ValidateCosts(store, ability, request, source)) return AbilityActivationRejectReason.Cost;
            for (int i = 0; i < ability.Effects.Count; i++)
                if (!catalog.TryGetEffect(ability.Effects[i], out var effect) ||
                    !store.GameplayEffectsRuntime.CanApplyDefinition(effect, target.Index))
                    return AbilityActivationRejectReason.InvalidRequest;
            for (int i = 0; i < ability.Executions.Count; i++)
            {
                if (!catalog.TryGetExecution(ability.Executions[i], out var execution))
                    return AbilityActivationRejectReason.UnsupportedDefinition;
                float magnitude = ResolveMagnitude(store, execution, request.MagnitudeOverride, source.Index);
                var context = new AbilityPayloadContext(store, ability, execution, request, source, target, magnitude);
                if (payloadHandler != null && payloadHandler.CanCommit(context)) continue;
                if (!CanCommitBuiltIn(context)) return AbilityActivationRejectReason.UnsupportedDefinition;
            }
            if (!ValidateCapacityPlan(store, catalog, ability, request, target, payloadHandler))
                return AbilityActivationRejectReason.InvalidRequest;
            return AbilityActivationRejectReason.None;
        }

        private static bool ValidateCapacityPlan(ComponentStore store, GameplayCatalog catalog, AbilityDefinition ability,
            AbilityActivationRequest request, EntityHandle target, IAbilityPayloadHandler payloadHandler)
        {
            int runtimeSlots = 0, modifiers = 0, effectEvents = ability.Effects.Count;
            int damageRequests = 0, damageEvents = 1;
            int resourceRequests = 0, resourceEvents = 0;
            for (int i = 0; i < ability.Effects.Count; i++)
            {
                catalog.TryGetEffect(ability.Effects[i], out var effect);
                if (effect.Type != EffectType.Instant) runtimeSlots++;
                modifiers += effect.Modifiers.Count;
            }
            for (int i = 0; i < ability.Executions.Count; i++)
            {
                catalog.TryGetExecution(ability.Executions[i], out var execution);
                float magnitude = ResolveMagnitude(store, execution, request.MagnitudeOverride, request.OwnerId);
                var context = new AbilityPayloadContext(store, ability, execution, request,
                    store.GetEntityHandle(request.OwnerId), target, magnitude);
                if (payloadHandler != null && payloadHandler.CanCommit(context)) continue;
                switch (execution.Payload)
                {
                    case EffectPayloadKind.Damage: damageRequests++; damageEvents += 3; break;
                    case EffectPayloadKind.Heal: resourceRequests++; resourceEvents++; break;
                    case EffectPayloadKind.Shield: resourceEvents++; break;
                    case EffectPayloadKind.GameplayEvent: damageEvents++; break;
                }
            }
            for (int i = 0; i < ability.Costs.Count; i++)
                if (EffectiveCost(ability, request, i) != 0f) { resourceRequests++; resourceEvents++; }
            bool effectsOk = store.GameplayEffectsRuntime.CanApplyPlan(target.Index, runtimeSlots, modifiers, effectEvents);
            bool damageOk = store.DamageResolver.CanAccept(damageRequests, damageEvents);
            bool resourceOk = store.ResourceResolver.CanAccept(resourceRequests, resourceEvents);
            return effectsOk && damageOk && resourceOk;
        }

        private static int CommitPlan(ComponentStore store, GameplayCatalog catalog, AbilityDefinition ability,
            AbilityActivationRequest request, EntityHandle source, EntityHandle target, IAbilityPayloadHandler payloadHandler)
        {
            int applied = 0;
            for (int i = 0; i < ability.Effects.Count; i++)
            {
                catalog.TryGetEffect(ability.Effects[i], out var effect);
                if (!store.GameplayEffectsRuntime.TryApply(effect.Id, effect, source, target, out _, ownerPlayerId: request.OwnerPlayerId))
                    return -1;
                applied++;
            }
            for (int i = 0; i < ability.Executions.Count; i++)
            {
                catalog.TryGetExecution(ability.Executions[i], out var execution);
                float magnitude = ResolveMagnitude(store, execution, request.MagnitudeOverride, source.Index);
                var context = new AbilityPayloadContext(store, ability, execution, request, source, target, magnitude);
                if (payloadHandler != null && payloadHandler.CanCommit(context)) applied += Math.Max(0, payloadHandler.Commit(context));
                else if (!CommitBuiltIn(context)) return -1;
                else applied++;
            }
            if (!CommitCosts(store, ability, request, source)) return -1;
            return applied;
        }

        private static bool CanCommitBuiltIn(AbilityPayloadContext context)
        {
            var execution = context.Execution;
            float magnitude = context.Magnitude;
            if (float.IsNaN(magnitude) || float.IsInfinity(magnitude)) return false;
            int targetId = context.Target.Index;
            bool player = (uint)targetId < ComponentStore.MAX_PLAYERS && context.Store.PositionActive[targetId];
            bool enemy = ComponentStore.IsValidEntity(targetId) && context.Store.EnemyActive[targetId] &&
                context.Store.EnemyHealth[targetId] > 0f && !context.Store.IsEnemyPendingDeath(targetId);
            switch (execution.Payload)
            {
                case EffectPayloadKind.Damage:
                    return Matches(execution.Operation, ExecutionOperation.ApplyDamage) && magnitude > 0f && enemy &&
                        context.Request.OwnerPlayerId >= 0 &&
                        context.Request.OwnerPlayerId < ComponentStore.MAX_PLAYERS;
                case EffectPayloadKind.Heal:
                    return Matches(execution.Operation, ExecutionOperation.ApplyHeal) && magnitude > 0f && (player || enemy);
                case EffectPayloadKind.Shield:
                    return Matches(execution.Operation, ExecutionOperation.ApplyShield) && magnitude > 0f &&
                        execution.Duration >= 0f && context.Ability.Clock == ClockId.Combat && player;
                case EffectPayloadKind.Slow:
                    return Matches(execution.Operation, ExecutionOperation.ApplySlow) && magnitude > 0f && magnitude < 1f &&
                        execution.Duration > 0f && (player || enemy);
                case EffectPayloadKind.CrowdControl:
                    return Matches(execution.Operation, ExecutionOperation.ApplyCrowdControl) && magnitude > 0f && (player || enemy);
                case EffectPayloadKind.GameplayEvent:
                    return execution.Operation == ExecutionOperation.Default;
                default:
                    return false;
            }
        }

        private static bool CommitBuiltIn(AbilityPayloadContext context)
        {
            var store = context.Store;
            int targetId = context.Target.Index;
            long sequence = store.AllocateGameplaySequence(targetId);
            switch (context.Execution.Payload)
            {
                case EffectPayloadKind.Damage:
                    if (!store.EnemyActive[targetId] || store.EnemyHealth[targetId] <= 0f ||
                        store.IsEnemyPendingDeath(targetId) || store.EnemyIsInvulnerable[targetId])
                        return true;
                    DamageAmountStage stage = context.Execution.Stage == DamageAmountStage.LegacyMultiplier
                        ? DamageAmountStage.Raw : context.Execution.Stage;
                    return store.DamageResolver.TryApply(new DamageRequest(context.Source, context.Target, context.Magnitude,
                        DamageType.True, ElementType.None, DamageFlags.None, stage,
                        DamageCommitBoundary.GameplayResolve, sequence, ability: context.Ability.Id,
                        effect: context.Request.Effect, ownerPlayerId: context.Request.OwnerPlayerId)).Accepted;
                case EffectPayloadKind.Heal:
                    return store.ResourceResolver.TryApply(new HealRequest(context.Source, context.Target,
                        context.Magnitude, sequence, context.Request.OwnerPlayerId)).Accepted;
                case EffectPayloadKind.Shield:
                    return store.ResourceResolver.TryApply(new ShieldRequest(context.Source, context.Target,
                        context.Magnitude, context.Execution.Duration, context.Ability.Clock, sequence), context.Request.OwnerPlayerId).Accepted;
                case EffectPayloadKind.Slow:
                    int slowDuration = Math.Max(1, (int)Math.Ceiling(context.Execution.Duration));
                    if (store.EnemyActive[targetId]) store.ApplyEnemySlow(targetId, context.Magnitude, slowDuration);
                    else store.ApplyPlayerSlow(targetId, context.Magnitude, slowDuration);
                    return true;
                case EffectPayloadKind.CrowdControl:
                    int duration = Math.Max(1, (int)Math.Ceiling(context.Magnitude));
                    if (store.EnemyActive[targetId])
                    {
                        if (context.Ability.Targeting.Shape == TargetingShape.AoeRoot) store.ApplyEnemyRoot(targetId, duration);
                        else if (context.Ability.Targeting.Shape == TargetingShape.AoeKnockback) store.ApplyEnemyKnockback(targetId, context.Magnitude);
                        else store.ApplyEnemyStun(targetId, duration);
                    }
                    else store.ApplyPlayerStun(targetId, duration);
                    return true;
                case EffectPayloadKind.GameplayEvent:
                    return store.DamageResolver.Events.TryPublish(new GameplayEvent(GameplayEventType.EffectApplied,
                        context.Source, context.Target, sequence, ownerPlayerId: context.Request.OwnerPlayerId), true);
                default:
                    return false;
            }
        }

        private static bool ValidateCosts(ComponentStore store, AbilityDefinition ability,
            AbilityActivationRequest request, EntityHandle source)
        {
            for (int i = 0; i < ability.Costs.Count; i++)
            {
                float amount = EffectiveCost(ability, request, i);
                if (amount < 0f || float.IsNaN(amount) || float.IsInfinity(amount) ||
                    !TryGetResource(store, source.Index, ability.Costs[i].Resource, out float available)) return false;
                float sameResource = 0f;
                for (int j = 0; j <= i; j++)
                    if (ability.Costs[j].Resource.Equals(ability.Costs[i].Resource)) sameResource += EffectiveCost(ability, request, j);
                if (available < sameResource) return false;
            }
            return true;
        }

        private static bool CommitCosts(ComponentStore store, AbilityDefinition ability,
            AbilityActivationRequest request, EntityHandle source)
        {
            for (int i = 0; i < ability.Costs.Count; i++)
            {
                float amount = EffectiveCost(ability, request, i);
                if (amount == 0f) continue;
                if (!store.ResourceResolver.TryApply(new ResourceRequest(source, source, ability.Costs[i].Resource,
                    -amount, store.AllocateGameplaySequence(source.Index), request.OwnerPlayerId)).Accepted) return false;
            }
            return true;
        }

        private static float EffectiveCost(AbilityDefinition ability, AbilityActivationRequest request, int index) =>
            request.Cost > 0f && ability.Costs.Count == 1 && index == 0 ? request.Cost : ability.Costs[index].Amount;

        private static bool TryGetResource(ComponentStore store, int entityId, AttributeKey key, out float value)
        {
            value = 0f;
            bool player = (uint)entityId < ComponentStore.MAX_PLAYERS && store.PositionActive[entityId];
            bool enemy = ComponentStore.IsValidEntity(entityId) && store.EnemyActive[entityId];
            if (!player && !enemy) return false;
            switch (key.Value)
            {
                case 2: value = player ? store.PlayerMaxHealth[entityId] : store.EnemyMaxHealth[entityId]; return true;
                case 3: value = player ? store.PlayerCurrentHealth[entityId] : store.EnemyHealth[entityId]; return true;
                case 4: if (!player) return false; value = store.PlayerGold[entityId]; return true;
                case 7: value = player ? store.PlayerMana[entityId] : store.EnemyCurrentMana[entityId]; return true;
                case 9: value = player ? store.PlayerShield[entityId] : store.EnemyShield[entityId]; return true;
                default: return false;
            }
        }

        private static bool Matches(ExecutionOperation actual, ExecutionOperation expected) =>
            actual == ExecutionOperation.Default || actual == expected;

        private static void PublishActivation(ComponentStore store, AbilityActivationRequest request,
            EntityHandle source, EntityHandle target, int targetId) =>
            store.DamageResolver.Events.TryPublish(new GameplayEvent(GameplayEventType.AbilityActivated, source, target,
                store.AllocateGameplaySequence(targetId), ownerPlayerId: request.OwnerPlayerId), true);
        public static AbilityActivationResult ActivateHealTargets(ComponentStore store, GameplayCatalog catalog,
            float[] cooldowns, AbilityActivationRequest request, IReadOnlyList<int> targetIds,
            IReadOnlyList<float> magnitudes)
        {
            if (store == null || catalog == null || cooldowns == null || targetIds == null || magnitudes == null ||
                targetIds.Count != magnitudes.Count || request.Slot < 0 || request.Slot >= cooldowns.Length)
                return Reject(request, AbilityActivationRejectReason.InvalidRequest);
            if (targetIds.Count == 0) return Reject(request, AbilityActivationRejectReason.NoTarget);
            if (!catalog.TryGetAbility(request.Ability, out var ability) || ability.Effects.Count != 0)
                return Reject(request, AbilityActivationRejectReason.UnsupportedDefinition);
            var ready = TryActivate(cooldowns, request);
            if (!ready.Accepted) return ready;
            for (int i = 0; i < ability.Executions.Count; i++)
                if (!catalog.TryGetExecution(ability.Executions[i], out var execution) || execution.Payload != EffectPayloadKind.Heal)
                    return Reject(request, AbilityActivationRejectReason.UnsupportedDefinition);
            var source = store.GetEntityHandle(request.OwnerId);
            if (!source.IsValid) return Reject(request, AbilityActivationRejectReason.InvalidRequest);
            if (!ValidateCosts(store, ability, request, source)) return Reject(request, AbilityActivationRejectReason.Cost);
            for (int i = 0; i < targetIds.Count; i++)
                if (!store.GetEntityHandle(targetIds[i]).IsValid || !store.EnemyActive[targetIds[i]] ||
                    float.IsNaN(magnitudes[i]) || float.IsInfinity(magnitudes[i]) || magnitudes[i] <= 0f)
                    return Reject(request, AbilityActivationRejectReason.NoTarget);
            int costRequests = 0;
            for (int i = 0; i < ability.Costs.Count; i++)
                if (EffectiveCost(ability, request, i) != 0f) costRequests++;
            int resourceRequests = targetIds.Count + costRequests;
            if (!store.ResourceResolver.CanAccept(resourceRequests, resourceRequests) ||
                !store.DamageResolver.CanAccept(0, 1))
                return Reject(request, AbilityActivationRejectReason.InvalidRequest);
            for (int i = 0; i < targetIds.Count; i++)
                if (!store.ResourceResolver.TryApply(new HealRequest(source, store.GetEntityHandle(targetIds[i]), magnitudes[i],
                    store.AllocateGameplaySequence(targetIds[i]), request.OwnerPlayerId)).Accepted)
                    throw new InvalidOperationException("prevalidated multi-target heal was rejected during commit");
            if (!CommitCosts(store, ability, request, source))
                throw new InvalidOperationException("prevalidated multi-target cost was rejected during commit");
            cooldowns[request.Slot] = Math.Max(0f, ability.Cooldown);
            var target = store.GetEntityHandle(targetIds[0]);
            PublishActivation(store, request, source, target, targetIds[0]);
            return new AbilityActivationResult(true, request.OwnerId, request.Slot, appliedEffects: targetIds.Count);
        }

        private static bool Contains(IReadOnlyList<EffectId> ids, EffectId id) { for (int i = 0; i < ids.Count; i++) if (ids[i].Value == id.Value) return true; return false; }
        private static bool Contains(IReadOnlyList<TriggerId> ids, TriggerId id) { for (int i = 0; i < ids.Count; i++) if (ids[i].Value == id.Value) return true; return false; }
        private static AbilityActivationResult Reject(AbilityActivationRequest request, AbilityActivationRejectReason reason) => new AbilityActivationResult(false, request.OwnerId, request.Slot, reason);

        private static float ResolveMagnitude(ComponentStore store, ExecutionDefinition execution, float requested, int sourceId)
        {
            if (!float.IsNaN(requested)) return requested;
            if (execution.MagnitudeSource != MagnitudeSource.Multiplier) return execution.Magnitude;
            float basis = store.EnemyActive[sourceId] ? store.EnemyDamage[sourceId]
                : store.TowerActive[sourceId] ? store.TowerAttackDamage[sourceId]
                : sourceId == store.PlayerEntityId ? store.GetPlayerAttackDamageProjection(sourceId)
                : 0f;
            return Math.Max(0f, basis * execution.Magnitude);
        }

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
