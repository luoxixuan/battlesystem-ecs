using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using Xunit;
using System;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class GameplayAbilityRuntimeTests
    {
        [Fact]
        public void TryActivateDoesNotMutateUntilAbilityCommit()
        {
            var store = new ComponentStore();
            int player = 0;
            store.AddPlayer(player, 10f, 1f, 1f, 1);
            var def = new GameplayAbilityDef("runtime", "", 3f, 0f, -1, 1f, AbilityActivation.Instant, AreaShapeType.Single, 1);
            Assert.True(store.TryAddAbility(player, def));

            Assert.True(GameplayAbilityRuntime.TryActivate(store, player, 0, out var pending));
            Assert.Equal(0f, pending.CurrentCooldown);
            Assert.Equal(0f, store.GetAbility(player, 0).CurrentCooldown);
            Assert.True(GameplayAbilityRuntime.AbilityCommit(store, player, 0));
            Assert.Equal(def.Cooldown, store.GetAbility(player, 0).CurrentCooldown);
        }

        [Fact]
        public void AbilityCommitRejectsCooldownSlot()
        {
            var store = new ComponentStore();
            int player = 0;
            store.AddPlayer(player, 10f, 1f, 1f, 1);
            var def = new GameplayAbilityDef("runtime", "", 3f, 0f, -1, 1f, AbilityActivation.Instant, AreaShapeType.Single, 1);
            Assert.True(store.TryAddAbility(player, def));
            Assert.True(GameplayAbilityRuntime.AbilityCommit(store, player, 0));
            Assert.False(GameplayAbilityRuntime.AbilityCommit(store, player, 0));
            Assert.Equal(def.Cooldown, store.GetAbility(player, 0).CurrentCooldown);
        }

        [Fact]
        public void CatalogActivationCommitsDamageAndPublishesAbilityEvent()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 20f, 1f, 1f, 1);
            int enemy = store.AddEnemy(0, 0, 1, 10f, 10f, 1, 1, 1);
            var targeting = new TargetingDefinition(new TargetingId(0), TargetingShape.Single, 10, 1, 1, 1);
            var execution = new ExecutionDefinition(new ExecutionId(0), EffectPayloadKind.Damage, 3f, new TagId(0));
            var ability = new AbilityDefinition(new AbilityId(0), "typed", targeting, ClockId.Combat, 2f,
                GameplayPhaseMask.Wave, Array.Empty<EffectId>(), Array.Empty<ModifierDefinition>(),
                CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer, executions: new[] { execution.Id });
            var catalog = new GameplayCatalog(new[] { ability }, new[] { targeting }, Array.Empty<GameplayEffectDefinition>(),
                new[] { execution }, Array.Empty<TriggerDefinition>(), Array.Empty<ModifierDefinition>(),
                new System.Collections.Generic.Dictionary<string, AbilityId> { ["typed"] = ability.Id });
            var timers = new float[1];
            var result = GameplayAbilityRuntime.Activate(store, catalog, timers,
                new AbilityActivationRequest(0, 0, 0f, enemy, ability: ability.Id));
            Assert.True(result.Accepted);
            Assert.Equal(1, result.AppliedEffects);
            Assert.Equal(2f, timers[0]);
            bool published = false;
            for (int i = 0; i < store.DamageResolver.Events.Count; i++)
                if (store.DamageResolver.Events.Get(i).Type == GameplayEventType.AbilityActivated) published = true;
            Assert.True(published);
        }

        [Fact]
        public void CatalogActivationRejectsUnmappedEffectReference()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 10f, 1f, 1f, 1);
            var targeting = new TargetingDefinition(new TargetingId(0), TargetingShape.Single, 1, 1, 1, 1);
            var ability = new AbilityDefinition(new AbilityId(0), "typed", targeting, ClockId.Combat, 1f,
                GameplayPhaseMask.Wave, new[] { new EffectId(4) }, Array.Empty<ModifierDefinition>(),
                CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer);
            var catalog = new GameplayCatalog(new[] { ability }, new[] { targeting }, Array.Empty<GameplayEffectDefinition>(),
                Array.Empty<ExecutionDefinition>(), Array.Empty<TriggerDefinition>(), Array.Empty<ModifierDefinition>(),
                new System.Collections.Generic.Dictionary<string, AbilityId>());
            var result = GameplayAbilityRuntime.Activate(store, catalog, new float[1],
                new AbilityActivationRequest(0, 0, 1f, 0, ability: ability.Id));
            Assert.False(result.Accepted);
            Assert.Equal(AbilityActivationRejectReason.InvalidRequest, result.Reason);
        }

        [Fact]
        public void CatalogActivationCommitsHealPayload()
        {
            var store = PlayerStore();
            store.PlayerCurrentHealth[0] = 4f;
            var result = Activate(store, Catalog(Execution(EffectPayloadKind.Heal, 3f, ExecutionOperation.ApplyHeal)), 0);
            Assert.True(result.Accepted);
            Assert.Equal(7f, store.PlayerCurrentHealth[0]);
        }

        [Fact]
        public void CatalogActivationCommitsShieldPayload()
        {
            var store = PlayerStore();
            var result = Activate(store, Catalog(Execution(EffectPayloadKind.Shield, 5f, ExecutionOperation.ApplyShield)), 0);
            Assert.True(result.Accepted);
            Assert.Equal(5f, store.PlayerShield[0]);
        }

        [Fact]
        public void CatalogActivationCommitsSlowPayload()
        {
            var store = PlayerStore();
            int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            var result = Activate(store, Catalog(Execution(EffectPayloadKind.Slow, 0.5f,
                ExecutionOperation.ApplySlow, duration: 3f), TargetingShape.Slow), enemy);
            Assert.True(result.Accepted);
            Assert.Equal(0.5f, store.EnemySlowFactor[enemy]);
            Assert.Equal(3f, store.EnemySlowDurationLeft[enemy]);
        }

        [Fact]
        public void CatalogActivationCommitsCrowdControlPayload()
        {
            var store = PlayerStore();
            int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            var result = Activate(store, Catalog(Execution(EffectPayloadKind.CrowdControl, 2f,
                ExecutionOperation.ApplyCrowdControl), TargetingShape.AoeStun), enemy);
            Assert.True(result.Accepted);
            Assert.Equal(2f, store.EnemyStunDurationLeft[enemy]);
        }

        [Theory]
        [InlineData(EffectPayloadKind.Resurrect, ExecutionOperation.Resurrect)]
        [InlineData(EffectPayloadKind.Resource, ExecutionOperation.RestoreSnapshot)]
        public void CatalogActivationCommitsDomainPayloadThroughTypedHandler(EffectPayloadKind payload, ExecutionOperation operation)
        {
            var store = PlayerStore();
            var handler = new RecordingPayloadHandler(payload);
            var result = GameplayAbilityRuntime.Activate(store, Catalog(Execution(payload, 1f, operation)), new float[1],
                Request(0), handler);
            Assert.True(result.Accepted);
            Assert.Equal(1, handler.CommitCount);
            Assert.Equal(payload, handler.LastPayload);
        }

        [Fact]
        public void CatalogActivationCommitsCatalogCostWithPayloadAndCooldown()
        {
            var store = PlayerStore();
            store.PlayerMana[0] = 8f;
            var timers = new float[1];
            var catalog = Catalog(Execution(EffectPayloadKind.Shield, 2f, ExecutionOperation.ApplyShield),
                costs: new[] { new CostDefinition(new AttributeKey(7), 3f) });
            var result = GameplayAbilityRuntime.Activate(store, catalog, timers, Request(0));
            Assert.True(result.Accepted);
            Assert.Equal(5f, store.PlayerMana[0]);
            Assert.Equal(2f, store.PlayerShield[0]);
            Assert.Equal(2f, timers[0]);
        }

        [Fact]
        public void CatalogActivationRejectsInsufficientCostWithoutPartialState()
        {
            var store = PlayerStore();
            store.PlayerMana[0] = 2f;
            var timers = new float[1];
            var catalog = Catalog(Execution(EffectPayloadKind.Shield, 2f, ExecutionOperation.ApplyShield),
                costs: new[] { new CostDefinition(new AttributeKey(7), 3f) });
            var result = GameplayAbilityRuntime.Activate(store, catalog, timers, Request(0));
            Assert.False(result.Accepted);
            Assert.Equal(AbilityActivationRejectReason.Cost, result.Reason);
            Assert.Equal(2f, store.PlayerMana[0]);
            Assert.Equal(0f, store.PlayerShield[0]);
            Assert.Equal(0f, timers[0]);
        }

        [Fact]
        public void CatalogActivationRejectsInvalidTargetWithoutPartialState()
        {
            var store = PlayerStore();
            store.PlayerMana[0] = 5f;
            var timers = new float[1];
            var catalog = Catalog(Execution(EffectPayloadKind.Shield, 2f, ExecutionOperation.ApplyShield),
                costs: new[] { new CostDefinition(new AttributeKey(7), 1f) });
            var result = GameplayAbilityRuntime.Activate(store, catalog, timers, Request(ComponentStore.MAX_ENTITIES - 1));
            Assert.False(result.Accepted);
            Assert.Equal(AbilityActivationRejectReason.NoTarget, result.Reason);
            Assert.Equal(5f, store.PlayerMana[0]);
            Assert.Equal(0f, timers[0]);
        }

        [Fact]
        public void CatalogActivationRejectsUnknownExecutionWithoutCommittingEarlierPayloadOrCost()
        {
            var store = PlayerStore();
            store.PlayerMana[0] = 5f;
            var timers = new float[1];
            var executions = new[]
            {
                Execution(EffectPayloadKind.Shield, 2f, ExecutionOperation.ApplyShield, id: 0),
                Execution((EffectPayloadKind)999, 1f, ExecutionOperation.Default, id: 1)
            };
            var catalog = Catalog(executions, new[] { new CostDefinition(new AttributeKey(7), 1f) });
            var result = GameplayAbilityRuntime.Activate(store, catalog, timers, Request(0));
            Assert.False(result.Accepted);
            Assert.Equal(AbilityActivationRejectReason.UnsupportedDefinition, result.Reason);
            Assert.Equal(5f, store.PlayerMana[0]);
            Assert.Equal(0f, store.PlayerShield[0]);
            Assert.Equal(0f, timers[0]);
        }

        [Fact]
        public void CatalogActivationDoesNotInvokePlannedHandlerWhenLaterExecutionIsUnknown()
        {
            var store = PlayerStore();
            var timers = new float[1];
            var handler = new RecordingPayloadHandler(EffectPayloadKind.Resurrect);
            var executions = new[]
            {
                Execution(EffectPayloadKind.Resurrect, 1f, ExecutionOperation.Resurrect, id: 0),
                Execution((EffectPayloadKind)999, 1f, ExecutionOperation.Default, id: 1)
            };
            var result = GameplayAbilityRuntime.Activate(store, Catalog(executions), timers, Request(0), handler);
            Assert.False(result.Accepted);
            Assert.Equal(AbilityActivationRejectReason.UnsupportedDefinition, result.Reason);
            Assert.Equal(0, handler.CommitCount);
            Assert.Equal(0f, timers[0]);
        }

        [Fact]
        public void CatalogActivationDoesNotQueueDamageOrEventWhenLaterExecutionIsUnknown()
        {
            var store = PlayerStore();
            int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            var executions = new[]
            {
                Execution(EffectPayloadKind.Damage, 3f, ExecutionOperation.ApplyDamage, id: 0),
                Execution((EffectPayloadKind)999, 1f, ExecutionOperation.Default, id: 1)
            };
            var result = GameplayAbilityRuntime.Activate(store, Catalog(executions), new float[1], Request(enemy));
            Assert.False(result.Accepted);
            Assert.Equal(10f, store.EnemyHealth[enemy]);
            Assert.Equal(0, store.DamageResolver.PendingRequestCount);
            Assert.Equal(0, store.DamageResolver.Events.Count);
        }

        [Fact]
        public void CatalogActivationDoesNotApplyEarlierEffectWhenLaterEffectReferenceIsMissing()
        {
            var store = PlayerStore();
            var effect = new GameplayEffectDefinition(new EffectId(0), EffectType.Duration,
                Array.Empty<ModifierDefinition>(), 3f, ClockId.Combat, StackingBehavior.None, 1,
                RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent,
                new TagId(0), default(PeriodicSpec), Array.Empty<ExecutionId>());
            var targeting = new TargetingDefinition(new TargetingId(0), TargetingShape.Single, 1, 1, 1, 1);
            var ability = new AbilityDefinition(new AbilityId(0), "effects", targeting, ClockId.Combat, 2f,
                GameplayPhaseMask.Wave, new[] { effect.Id, new EffectId(1) }, Array.Empty<ModifierDefinition>(),
                CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer);
            var catalog = new GameplayCatalog(new[] { ability }, new[] { targeting }, new[] { effect },
                Array.Empty<ExecutionDefinition>(), Array.Empty<TriggerDefinition>(), Array.Empty<ModifierDefinition>(),
                new System.Collections.Generic.Dictionary<string, AbilityId>());
            var result = GameplayAbilityRuntime.Activate(store, catalog, new float[1], Request(0));
            Assert.False(result.Accepted);
            Assert.Equal(0, store.GetEffectCount(0));
            Assert.Equal(0, store.GameplayEffectsRuntime.Events.Count);
        }

        private static ComponentStore PlayerStore()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 10f, 1f, 1f, 1);
            store.PlayerMaxHealth[0] = 10f;
            store.PlayerMaxMana[0] = 10f;
            return store;
        }

        private static ExecutionDefinition Execution(EffectPayloadKind payload, float magnitude,
            ExecutionOperation operation, float duration = 0f, int id = 0) =>
            new ExecutionDefinition(new ExecutionId(id), payload, magnitude, new TagId(0),
                duration: duration, operation: operation);

        private static GameplayCatalog Catalog(ExecutionDefinition execution, TargetingShape shape = TargetingShape.Single,
            CostDefinition[]? costs = null) => Catalog(new[] { execution }, costs, shape);

        private static GameplayCatalog Catalog(ExecutionDefinition[] executions, CostDefinition[]? costs = null,
            TargetingShape shape = TargetingShape.Single)
        {
            var targeting = new TargetingDefinition(new TargetingId(0), shape, 10, 1, 1, 1, radius: 10f);
            var ids = new ExecutionId[executions.Length];
            for (int i = 0; i < ids.Length; i++) ids[i] = executions[i].Id;
            var ability = new AbilityDefinition(new AbilityId(0), "typed", targeting, ClockId.Combat, 2f,
                GameplayPhaseMask.Wave, Array.Empty<EffectId>(), Array.Empty<ModifierDefinition>(),
                CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer, executions: ids,
                costs: costs ?? Array.Empty<CostDefinition>());
            return new GameplayCatalog(new[] { ability }, new[] { targeting }, Array.Empty<GameplayEffectDefinition>(),
                executions, Array.Empty<TriggerDefinition>(), Array.Empty<ModifierDefinition>(),
                new System.Collections.Generic.Dictionary<string, AbilityId> { ["typed"] = ability.Id });
        }

        private static AbilityActivationRequest Request(int targetId) =>
            new AbilityActivationRequest(0, 0, 0f, targetId, new AbilityId(0));

        private static AbilityActivationResult Activate(ComponentStore store, GameplayCatalog catalog, int targetId) =>
            GameplayAbilityRuntime.Activate(store, catalog, new float[1], Request(targetId));

        private sealed class RecordingPayloadHandler : IAbilityPayloadHandler
        {
            private readonly EffectPayloadKind _supported;
            public int CommitCount { get; private set; }
            public EffectPayloadKind LastPayload { get; private set; }
            public RecordingPayloadHandler(EffectPayloadKind supported) { _supported = supported; }
            public bool CanCommit(AbilityPayloadContext context) => context.Execution.Payload == _supported;
            public int Commit(AbilityPayloadContext context)
            {
                CommitCount++;
                LastPayload = context.Execution.Payload;
                return 1;
            }
        }
    }
}
