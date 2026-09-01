using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Components;
using Xunit;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class GameplayAbilityRuntimeTests
    {
        [Fact]
        public void TryActivateDoesNotMutateUntilAbilityCommit()
        {
            var store = WaveStore();
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
            var store = WaveStore();
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
            var store = WaveStore();
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
            Assert.True(result.Accepted, result.Reason.ToString());
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
            var store = WaveStore();
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
        public void OptionalEffectAndTriggerDistinguishValidZeroExplicitInvalidZeroAndAbsent()
        {
            var store = PlayerStore();
            var effect = new GameplayEffectDefinition(new EffectId(0), EffectType.Duration,
                Array.Empty<ModifierDefinition>(), 2f, 0f, ClockId.Combat, StackingBehavior.None, 1,
                RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent,
                CatalogRegistries.SkillTag, Array.Empty<ExecutionId>());
            var targeting = new TargetingDefinition(new TargetingId(0), TargetingShape.Single, 1, 1, 1, 1);
            var trigger = new TriggerDefinition(new TriggerId(0), GameplayEventType.HitConfirmed,
                effect.Id, CatalogRegistries.SkillConsumer);
            var withZero = new AbilityDefinition(new AbilityId(0), "with-zero", targeting, ClockId.Combat, 1f,
                GameplayPhaseMask.Wave, new[] { effect.Id }, Array.Empty<ModifierDefinition>(),
                CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer,
                triggerRefs: new[] { trigger.Id });
            var withZeroCatalog = new GameplayCatalog(new[] { withZero }, new[] { targeting }, new[] { effect },
                Array.Empty<ExecutionDefinition>(), new[] { trigger }, Array.Empty<ModifierDefinition>(),
                new System.Collections.Generic.Dictionary<string, AbilityId>());
            var validZero = GameplayAbilityRuntime.Activate(store, withZeroCatalog, new float[1],
                new AbilityActivationRequest(0, 0, 1f, 0, withZero.Id,
                    effect: new EffectId(0), trigger: new TriggerId(0)));
            Assert.True(validZero.Accepted, $"reason={validZero.Reason}; effectRejections={store.GameplayEffectsRuntime.Rejections}");
            Assert.Equal(1, store.GetEffectCount(0));

            var execution = Execution(EffectPayloadKind.Shield, 2f, ExecutionOperation.ApplyShield);
            var noEffectCatalog = Catalog(execution);
            var invalidZero = GameplayAbilityRuntime.Activate(store, noEffectCatalog, new float[1],
                new AbilityActivationRequest(0, 0, 1f, 0, new AbilityId(0),
                    effect: new EffectId(0), trigger: new TriggerId(0)));
            Assert.False(invalidZero.Accepted);
            Assert.Equal(AbilityActivationRejectReason.InvalidRequest, invalidZero.Reason);

            var absent = GameplayAbilityRuntime.Activate(store, noEffectCatalog, new float[1],
                new AbilityActivationRequest(0, 0, 1f, 0, new AbilityId(0), effect: null));
            Assert.True(absent.Accepted);
        }

        [Fact]
        public void TowerSourceKeepsPlayerOwnerAndEnemyTargetAcrossPayloadFacts()
        {
            var store = WaveStore();
            store.AddPlayer(0, 10f, 1f, 10f, 1);
            int tower = store.CreateEntity();
            store.AddTower(tower, TowerType.Basic, 10f, 5, 1f, 1, 10f);
            int enemy = store.AddEnemy(1f, 0f, 0f, 100f, 100f, 0f, 0, 1);
            var targeting = new TargetingDefinition(new TargetingId(0), TargetingShape.Single, 5, 1, 1, 1,
                relation: RelationFilter.Enemies, maxTargetsMode: MaxTargetsPolicy.Fixed);
            var executions = new[]
            {
                Execution(EffectPayloadKind.Damage, 3f, ExecutionOperation.ApplyDamage, id: 0),
                Execution(EffectPayloadKind.GameplayEvent, 1f, ExecutionOperation.Default, id: 1)
            };
            var ability = new AbilityDefinition(new AbilityId(0), "tower-owned", targeting, ClockId.Combat, 1f,
                GameplayPhaseMask.Wave, Array.Empty<EffectId>(), Array.Empty<ModifierDefinition>(),
                CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer,
                executions: new[] { executions[0].Id, executions[1].Id });
            var catalog = new GameplayCatalog(new[] { ability }, new[] { targeting },
                Array.Empty<GameplayEffectDefinition>(), executions, Array.Empty<TriggerDefinition>(),
                Array.Empty<ModifierDefinition>(), new System.Collections.Generic.Dictionary<string, AbilityId>());

            var result = GameplayAbilityRuntime.Activate(store, catalog, new float[store.TowerActiveCooldown.Length],
                new AbilityActivationRequest(tower, tower, 1f, enemy, ability.Id, ownerPlayerId: 0));
            Assert.True(result.Accepted, result.Reason.ToString());
            Assert.True(store.DamageResolver.Events.Count >= 4);
            for (int i = 0; i < store.DamageResolver.Events.Count; i++)
            {
                var fact = store.DamageResolver.Events.Get(i);
                Assert.Equal(tower, fact.Source.Index);
                Assert.Equal(enemy, fact.Target.Index);
                Assert.Equal(0, fact.OwnerPlayerId);
            }
        }

        [Fact]
        public void CatalogActivationCommitsHealPayload()
        {
            var store = PlayerStore();
            store.PlayerCurrentHealth[0] = 4f;
            var result = Activate(store, Catalog(Execution(EffectPayloadKind.Heal, 3f, ExecutionOperation.ApplyHeal)), 0);
            Assert.True(result.Accepted, result.Reason.ToString());
            Assert.Equal(7f, store.PlayerCurrentHealth[0]);
        }

        [Fact]
        public void CatalogActivationCommitsShieldPayload()
        {
            var store = PlayerStore();
            var result = Activate(store, Catalog(Execution(EffectPayloadKind.Shield, 5f, ExecutionOperation.ApplyShield)), 0);
            Assert.True(result.Accepted, result.Reason.ToString());
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

        [Fact]
        public void CapacityPlanRejectsTwoEffectsBeforeFirstEffectOrCostCommits()
        {
            var store = PlayerStore();
            store.PlayerMana[0] = 5f;
            var source = store.GetEntityHandle(0);
            for (int i = 0; i < ComponentStore.MAX_ACTIVE_EFFECTS_PER_ENTITY - 1; i++)
            {
                var existing = DurationEffect(100 + i);
                Assert.True(store.GameplayEffectsRuntime.TryApply(existing.Id, existing, source, source, out _),
                    $"effect {i}, active={store.GetEffectCount(0)}, rejection={store.GameplayEffectsRuntime.Rejections}");
            }
            int effectCount = store.GetEffectCount(0);
            int effectEvents = store.GameplayEffectsRuntime.Events.Count;
            var first = DurationEffect(0);
            var second = DurationEffect(1);
            var targeting = new TargetingDefinition(new TargetingId(0), TargetingShape.Single, 1, 1, 1, 1);
            var ability = new AbilityDefinition(new AbilityId(0), "capacity", targeting, ClockId.Combat, 2f,
                GameplayPhaseMask.Wave, new[] { first.Id, second.Id }, Array.Empty<ModifierDefinition>(),
                CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer,
                costs: new[] { new CostDefinition(new AttributeKey(7), 1f) });
            var catalog = new GameplayCatalog(new[] { ability }, new[] { targeting }, new[] { first, second },
                Array.Empty<ExecutionDefinition>(), Array.Empty<TriggerDefinition>(), Array.Empty<ModifierDefinition>(),
                new Dictionary<string, AbilityId>());
            var cooldowns = new float[1];

            var result = GameplayAbilityRuntime.Activate(store, catalog, cooldowns, Request(0));

            Assert.False(result.Accepted);
            Assert.Equal(effectCount, store.GetEffectCount(0));
            Assert.Equal(effectEvents, store.GameplayEffectsRuntime.Events.Count);
            Assert.Equal(5f, store.PlayerMana[0]);
            Assert.Equal(0f, cooldowns[0]);
        }

        [Fact]
        public void CombinedHealAndCostCapacityRejectsWithoutPartialWrites()
        {
            var store = PlayerStore();
            store.PlayerCurrentHealth[0] = 2f;
            store.PlayerMana[0] = 5f;
            store.ResourceResolver.EnableDeferred(true);
            var handle = store.GetEntityHandle(0);
            for (int i = 0; i < ResourceResolver.MaxPendingRequests - 1; i++)
                Assert.True(store.ResourceResolver.TryApply(new ResourceRequest(handle, handle, new AttributeKey(7),
                    0f, store.AllocateGameplaySequence(0), 0)).Accepted);
            int pending = store.ResourceResolver.PendingRequestCount;
            var catalog = Catalog(Execution(EffectPayloadKind.Heal, 2f, ExecutionOperation.ApplyHeal),
                costs: new[] { new CostDefinition(new AttributeKey(7), 1f) });
            var cooldowns = new float[1];

            var result = GameplayAbilityRuntime.Activate(store, catalog, cooldowns, Request(0));

            Assert.False(result.Accepted);
            Assert.Equal(pending, store.ResourceResolver.PendingRequestCount);
            Assert.Equal(2f, store.PlayerCurrentHealth[0]);
            Assert.Equal(5f, store.PlayerMana[0]);
            Assert.Equal(0f, cooldowns[0]);
            Assert.Equal(0, store.DamageResolver.Events.Count);
        }

        [Fact]
        public void MultiTargetHealCommitsCostEventAndCooldownOnceAndRejectsSecondSameFrame()
        {
            var store = PlayerStore();
            store.PlayerMana[0] = 5f;
            int first = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            int second = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            store.EnemyHealth[first] = 3f;
            store.EnemyHealth[second] = 4f;
            store.ResourceResolver.EnableDeferred(true);
            var catalog = Catalog(Execution(EffectPayloadKind.Heal, 1f, ExecutionOperation.ApplyHeal),
                costs: new[] { new CostDefinition(new AttributeKey(7), 1f) });
            var cooldowns = new float[1];
            var request = Request(first);
            var targets = new[] { first, second };
            var magnitudes = new[] { 2f, 3f };

            var accepted = GameplayAbilityRuntime.ActivateHealTargets(store, catalog, cooldowns, request, targets, magnitudes);
            int pending = store.ResourceResolver.PendingRequestCount;
            int events = store.DamageResolver.Events.Count;
            var rejected = GameplayAbilityRuntime.ActivateHealTargets(store, catalog, cooldowns, request, targets, magnitudes);

            Assert.True(accepted.Accepted);
            Assert.Equal(targets.Length, accepted.AppliedEffects);
            Assert.Equal(3, pending);
            Assert.Equal(1, events);
            Assert.Equal(2f, cooldowns[0]);
            Assert.False(rejected.Accepted);
            Assert.Equal(AbilityActivationRejectReason.Cooldown, rejected.Reason);
            Assert.Equal(pending, store.ResourceResolver.PendingRequestCount);
            Assert.Equal(events, store.DamageResolver.Events.Count);
        }

        [Fact]
        public void ShieldDurationUsesCombatClockAndPublishesExpirationEvent()
        {
            var store = PlayerStore();
            const float configuredDuration = 5f;
            var execution = Execution(EffectPayloadKind.Shield, 3f, ExecutionOperation.ApplyShield,
                duration: configuredDuration);
            var result = Activate(store, Catalog(execution), 0);

            Assert.True(result.Accepted);
            Assert.Equal(3f, store.PlayerShield[0]);
            Assert.Equal(configuredDuration, store.PlayerShieldDuration[0]);
            Assert.Equal(GameplayEventType.ShieldChanged, store.ResourceResolver.Events.Get(0).Type);
            store.GameplayEffectsRuntime.Tick(configuredDuration, ClockId.Enemy);
            Assert.Equal(configuredDuration, store.PlayerShieldDuration[0]);
            store.GameplayEffectsRuntime.Tick(configuredDuration - 1f, ClockId.Combat);
            Assert.Equal(3f, store.PlayerShield[0]);
            store.GameplayEffectsRuntime.Tick(1f, ClockId.Combat);
            Assert.Equal(0f, store.PlayerShield[0]);
            Assert.Equal(0f, store.PlayerShieldDuration[0]);
            Assert.Equal(2, store.ResourceResolver.Events.Count);
            Assert.Equal(GameplayEventType.ShieldChanged, store.ResourceResolver.Events.Get(1).Type);
        }

        [Fact]
        public void ActivationEventCapacityRejectsBeforeShieldCostOrCooldown()
        {
            var store = PlayerStore();
            store.PlayerMana[0] = 5f;
            var handle = store.GetEntityHandle(0);
            var filler = new GameplayEvent(GameplayEventType.HitConfirmed, handle, handle, 1L);
            for (int i = 0; i < store.DamageResolver.Events.Capacity; i++)
                Assert.True(store.DamageResolver.Events.TryPublish(filler, true));
            var catalog = Catalog(Execution(EffectPayloadKind.Shield, 2f, ExecutionOperation.ApplyShield, duration: 5f),
                costs: new[] { new CostDefinition(new AttributeKey(7), 1f) });
            var cooldowns = new float[1];

            var result = GameplayAbilityRuntime.Activate(store, catalog, cooldowns, Request(0));

            Assert.False(result.Accepted);
            Assert.Equal(0f, store.PlayerShield[0]);
            Assert.Equal(5f, store.PlayerMana[0]);
            Assert.Equal(0f, cooldowns[0]);
            Assert.Equal(0, store.ResourceResolver.Events.Count);
        }

        [Fact]
        public void ModifierCapacityRejectsBeforeEffectOrCooldown()
        {
            var store = PlayerStore();
            var source = store.GetEntityHandle(0);
            var modifiers = new ModifierDefinition[store.GameplayEffectsRuntime.ModifierCapacity - 1];
            var modifier = new ModifierDefinition(new AttributeKey(8), AttributeModifierOp.Add, 0.01f);
            for (int i = 0; i < modifiers.Length; i++) modifiers[i] = modifier;
            var existing = DurationEffect(100, modifiers);
            Assert.True(store.GameplayEffectsRuntime.TryApply(existing.Id, existing, source, source, out _));
            int before = store.GetEffectCount(0);
            var requested = DurationEffect(0, new[] { modifier, modifier });
            var targeting = new TargetingDefinition(new TargetingId(0), TargetingShape.Single, 1, 1, 1, 1);
            var ability = new AbilityDefinition(new AbilityId(0), "modifiers", targeting, ClockId.Combat, 2f,
                GameplayPhaseMask.Wave, new[] { requested.Id }, Array.Empty<ModifierDefinition>(),
                CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer);
            var catalog = new GameplayCatalog(new[] { ability }, new[] { targeting }, new[] { requested },
                Array.Empty<ExecutionDefinition>(), Array.Empty<TriggerDefinition>(), Array.Empty<ModifierDefinition>(),
                new Dictionary<string, AbilityId>());
            var cooldowns = new float[1];

            var result = GameplayAbilityRuntime.Activate(store, catalog, cooldowns, Request(0));

            Assert.False(result.Accepted);
            Assert.Equal(before, store.GetEffectCount(0));
            Assert.Equal(0f, cooldowns[0]);
        }

        [Fact]
        public void MultiHitAbilityTreatsLaterHitAfterLethalCommitAsSuccessfulNoOp()
        {
            var store = PlayerStore();
            int enemy = store.AddEnemy(0, 0, 1f, 3f, 3f, 1f, 1, 1);
            var executions = new[]
            {
                Execution(EffectPayloadKind.Damage, 4f, ExecutionOperation.ApplyDamage, id: 0),
                Execution(EffectPayloadKind.Damage, 5f, ExecutionOperation.ApplyDamage, id: 1)
            };
            var cooldowns = new float[1];

            var result = GameplayAbilityRuntime.Activate(store, Catalog(executions), cooldowns, Request(enemy));

            Assert.True(result.Accepted);
            Assert.Equal(0f, store.EnemyHealth[enemy]);
            Assert.True(store.IsEnemyPendingDeath(enemy));
            Assert.Equal(2f, cooldowns[0]);
            Assert.Contains(Enumerable.Range(0, store.DamageResolver.Events.Count),
                i => store.DamageResolver.Events.Get(i).Type == GameplayEventType.AbilityActivated);
        }

        [Fact]
        public void MultiHitAbilityStillRejectsTargetThatWasPendingDeathBeforeActivation()
        {
            var store = PlayerStore();
            int enemy = store.AddEnemy(0, 0, 1f, 3f, 3f, 1f, 1, 1);
            store.QueueEnemyDeath(enemy, 0);
            var executions = new[]
            {
                Execution(EffectPayloadKind.Damage, 4f, ExecutionOperation.ApplyDamage, id: 0),
                Execution(EffectPayloadKind.Damage, 5f, ExecutionOperation.ApplyDamage, id: 1)
            };
            var cooldowns = new float[1];

            var result = GameplayAbilityRuntime.Activate(store, Catalog(executions), cooldowns, Request(enemy));

            Assert.False(result.Accepted);
            Assert.Equal(AbilityActivationRejectReason.InvalidRequest, result.Reason);
            Assert.Equal(3f, store.EnemyHealth[enemy]);
            Assert.Equal(0f, cooldowns[0]);
            Assert.DoesNotContain(Enumerable.Range(0, store.DamageResolver.Events.Count),
                i => store.DamageResolver.Events.Get(i).Type == GameplayEventType.AbilityActivated);
        }

        [Fact]
        public void PhaseRejectionDoesNotCommitCostCooldownPayloadOrEvent()
        {
            var store = PlayerStore();
            store.GameplayPhaseContext = new PhaseContext(PhaseContextKind.Build);
            store.PlayerMana[0] = 10f;
            var timers = new float[1];
            var catalog = Catalog(Execution(EffectPayloadKind.Shield, 3f, ExecutionOperation.ApplyShield),
                costs: new[] { new CostDefinition(new AttributeKey(7), 4f) });

            var result = GameplayAbilityRuntime.Activate(store, catalog, timers, Request(0));

            Assert.False(result.Accepted);
            Assert.Equal(AbilityActivationRejectReason.PhaseNotAllowed, result.Reason);
            Assert.Equal(10f, store.PlayerMana[0]);
            Assert.Equal(0f, store.PlayerShield[0]);
            Assert.Equal(0f, timers[0]);
            Assert.Equal(0, store.DamageResolver.Events.Count);
            Assert.Equal(0, store.ResourceResolver.Events.Count);
        }

        [Fact]
        public void AbilityTagsUseActiveGrantedTagsAndBlockedTagRejectsWithoutCommit()
        {
            var store = PlayerStore();
            var required = new TagId(2);
            var blocked = new TagId(3);
            var targeting = new TargetingDefinition(new TargetingId(0), TargetingShape.Shield, 0, 1, 1, 1,
                relation: RelationFilter.Self, maxTargetsMode: MaxTargetsPolicy.Fixed);
            var execution = Execution(EffectPayloadKind.Shield, 2f, ExecutionOperation.ApplyShield);
            var ability = new AbilityDefinition(new AbilityId(0), "tagged", targeting, ClockId.Combat, 2f,
                GameplayPhaseMask.Wave, Array.Empty<EffectId>(), Array.Empty<ModifierDefinition>(),
                CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer,
                executions: new[] { execution.Id }, requiredTags: new[] { required }, blockedTags: new[] { blocked });
            var catalog = new GameplayCatalog(new[] { ability }, new[] { targeting }, Array.Empty<GameplayEffectDefinition>(),
                new[] { execution }, Array.Empty<TriggerDefinition>(), Array.Empty<ModifierDefinition>(),
                new Dictionary<string, AbilityId>());
            var timers = new float[1];

            var missing = GameplayAbilityRuntime.Activate(store, catalog, timers, Request(0));
            Assert.Equal(AbilityActivationRejectReason.TagRequirementsNotMet, missing.Reason);
            var classificationOnly = new GameplayEffectDefinition(new EffectId(6), EffectType.Duration,
                Array.Empty<ModifierDefinition>(), 5f, 0f, ClockId.Combat, StackingBehavior.None, 1,
                RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent,
                required, Array.Empty<ExecutionId>());
            Assert.True(store.GameplayEffectsRuntime.TryApply(classificationOnly.Id, classificationOnly,
                store.GetEntityHandle(0), store.GetEntityHandle(0), out var classificationHandle, ownerPlayerId: 0));
            var classificationRejected = GameplayAbilityRuntime.Activate(store, catalog, timers, Request(0));
            Assert.Equal(AbilityActivationRejectReason.TagRequirementsNotMet, classificationRejected.Reason);
            Assert.True(store.GameplayEffectsRuntime.Remove(store.GetEntityHandle(0), classificationHandle));
            var grant = new GameplayEffectDefinition(new EffectId(7), EffectType.Duration,
                Array.Empty<ModifierDefinition>(), 5f, 0f, ClockId.Combat, StackingBehavior.None, 1,
                RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent,
                CatalogRegistries.SkillTag, Array.Empty<ExecutionId>(), grantedTags: new[] { required });
            Assert.True(store.GameplayEffectsRuntime.TryApply(grant.Id, grant, store.GetEntityHandle(0),
                store.GetEntityHandle(0), out var handle, ownerPlayerId: 0));
            var accepted = GameplayAbilityRuntime.Activate(store, catalog, timers, Request(0));
            Assert.True(accepted.Accepted, accepted.Reason.ToString());
            Assert.True(store.GameplayEffectsRuntime.Remove(store.GetEntityHandle(0), handle));
            timers[0] = 0f;
            var removed = GameplayAbilityRuntime.Activate(store, catalog, timers, Request(0));
            Assert.Equal(AbilityActivationRejectReason.TagRequirementsNotMet, removed.Reason);
            var block = new GameplayEffectDefinition(new EffectId(8), EffectType.Duration,
                Array.Empty<ModifierDefinition>(), 5f, 0f, ClockId.Combat, StackingBehavior.None, 1,
                RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent,
                CatalogRegistries.SkillTag, Array.Empty<ExecutionId>(), grantedTags: new[] { required, blocked });
            Assert.True(store.GameplayEffectsRuntime.TryApply(block.Id, block, store.GetEntityHandle(0),
                store.GetEntityHandle(0), out _, ownerPlayerId: 0));
            float shield = store.PlayerShield[0];
            var rejected = GameplayAbilityRuntime.Activate(store, catalog, timers, Request(0));
            Assert.Equal(AbilityActivationRejectReason.TagRequirementsNotMet, rejected.Reason);
            Assert.Equal(shield, store.PlayerShield[0]);
            Assert.Equal(0f, timers[0]);
        }

        [Fact]
        public void CombinedPhaseMaskAcceptsBuildAndWaveWhileUnboundRejects()
        {
            var store = PlayerStore();
            var targeting = new TargetingDefinition(new TargetingId(0), TargetingShape.Shield, 0, 1, 1, 1,
                relation: RelationFilter.Self, maxTargetsMode: MaxTargetsPolicy.Fixed);
            var execution = Execution(EffectPayloadKind.Shield, 1f, ExecutionOperation.ApplyShield);
            var ability = new AbilityDefinition(new AbilityId(0), "phase-combination", targeting, ClockId.Combat, 1f,
                GameplayPhaseMask.Build | GameplayPhaseMask.Wave, Array.Empty<EffectId>(), Array.Empty<ModifierDefinition>(),
                CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer, executions: new[] { execution.Id });
            var catalog = new GameplayCatalog(new[] { ability }, new[] { targeting }, Array.Empty<GameplayEffectDefinition>(),
                new[] { execution }, Array.Empty<TriggerDefinition>(), Array.Empty<ModifierDefinition>(),
                new Dictionary<string, AbilityId>());
            var timers = new float[1];
            store.GameplayPhaseContext = PhaseContext.Unbound;
            Assert.Equal(AbilityActivationRejectReason.PhaseNotAllowed,
                GameplayAbilityRuntime.Activate(store, catalog, timers, Request(0)).Reason);
            store.GameplayPhaseContext = new PhaseContext(PhaseContextKind.Build);
            Assert.True(GameplayAbilityRuntime.Activate(store, catalog, timers, Request(0)).Accepted);
            timers[0] = 0f;
            store.GameplayPhaseContext = new PhaseContext(PhaseContextKind.Wave);
            Assert.True(GameplayAbilityRuntime.Activate(store, catalog, timers, Request(0)).Accepted);
        }

        private static GameplayEffectDefinition DurationEffect(int id, ModifierDefinition[]? modifiers = null) =>
            new GameplayEffectDefinition(new EffectId(id), EffectType.Duration, modifiers ?? Array.Empty<ModifierDefinition>(),
                3f, 0f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist,
                EffectPayloadKind.GameplayEvent, new TagId(id), Array.Empty<ExecutionId>());

        private static ComponentStore WaveStore()
        {
            var store = new ComponentStore();
            store.GameplayPhaseContext = new PhaseContext(PhaseContextKind.Wave);
            return store;
        }

        private static ComponentStore PlayerStore()
        {
            var store = WaveStore();
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
            public bool Supports(ExecutionDefinition execution) => execution.Payload == _supported;
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
