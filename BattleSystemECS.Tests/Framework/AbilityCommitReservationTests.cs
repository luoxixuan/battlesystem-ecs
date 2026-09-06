using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    /// <summary>
    /// P2 / F11：入队预留、Spend 原子支付。Commit 先复查，再 Spend，再 CommitPlan；Plan 失败退款。
    /// 防假绿：同帧两技能超法力断言第二技能在入队时 Cost（看 PeekCost），
    /// 不是只看「第二份载荷没交上」——没有预留表时顺序 Spend 也会挡住第二份载荷。
    /// </summary>
    public sealed class AbilityCommitReservationTests
    {
        [Fact]
        public void SameFrameTwoAbilitiesOverMana_SecondRejectedAtEnqueue_FirstPayloadOnly()
        {
            var store = PlayerStore();
            store.DeferAbilityAndEffectCommit = true;
            store.PlayerMana[0] = 10f;
            int firstEnemy = store.AddEnemy(0, 0, 1f, 100f, 100f, 1f, 1, 1);
            int secondEnemy = store.AddEnemy(1, 0, 1f, 100f, 100f, 1f, 1, 1);
            var catalog = TwoDamageAbilities(6f, 6f);
            var cooldowns = new float[2];

            var first = GameplayAbilityRuntime.Activate(store, catalog, cooldowns,
                new AbilityActivationRequest(0, 0, 0f, firstEnemy, new AbilityId(0)));
            Assert.True(first.Accepted);
            Assert.Equal(1, store.AbilityRequests.Count);
            Assert.Equal(6f, store.AbilityCommitReservation.PeekCost(0, new AttributeKey(7)));

            var second = GameplayAbilityRuntime.Activate(store, catalog, cooldowns,
                new AbilityActivationRequest(0, 1, 0f, secondEnemy, new AbilityId(1)));
            Assert.False(second.Accepted);
            Assert.Equal(AbilityActivationRejectReason.Cost, second.Reason);
            Assert.Equal(1, store.AbilityRequests.Count);
            Assert.Equal(10f, store.PlayerMana[0]);
            Assert.Equal(100f, store.EnemyHealth[firstEnemy]);
            Assert.Equal(100f, store.EnemyHealth[secondEnemy]);

            Exception? thrown = Record.Exception(() => GameplayAbilityRuntime.CommitQueuedAbilities(store));
            Assert.Null(thrown);
            Assert.Equal(4f, store.PlayerMana[0]);
            Assert.Equal(95f, store.EnemyHealth[firstEnemy]);
            Assert.Equal(100f, store.EnemyHealth[secondEnemy]);
            Assert.True(store.AbilityCommitReservation.IsEmpty);
            Assert.Equal(0, store.AbilityRequests.Count);
            Assert.Contains(Enumerable.Range(0, store.DamageResolver.Events.Count),
                i => store.DamageResolver.Events.Get(i).Type == GameplayEventType.AbilityActivated);
            Assert.DoesNotContain(Enumerable.Range(0, store.DamageResolver.Events.Count),
                i => store.DamageResolver.Events.Get(i).Type == GameplayEventType.AbilityCancelled);
        }

        [Fact]
        public void SingleAbilityOverMana_RejectedAtEnqueue_ManaUnchanged()
        {
            var store = PlayerStore();
            store.PlayerMana[0] = 4f;
            int enemy = store.AddEnemy(0, 0, 1f, 100f, 100f, 1f, 1, 1);
            var catalog = Catalog(Execution(EffectPayloadKind.Damage, 5f, ExecutionOperation.ApplyDamage),
                costs: new[] { new CostDefinition(new AttributeKey(7), 6f) });

            var result = GameplayAbilityRuntime.Activate(store, catalog, new float[1], Request(enemy));

            Assert.False(result.Accepted);
            Assert.Equal(AbilityActivationRejectReason.Cost, result.Reason);
            Assert.Equal(4f, store.PlayerMana[0]);
            Assert.Equal(100f, store.EnemyHealth[enemy]);
            Assert.Equal(0, store.AbilityRequests.Count);
            Assert.True(store.AbilityCommitReservation.IsEmpty);
        }

        [Fact]
        public void QueuedAbilityManaStolenBeforeCommit_AbilityCancelled_NoPayloadOrSpend()
        {
            var store = PlayerStore();
            store.DeferAbilityAndEffectCommit = true;
            store.PlayerMana[0] = 10f;
            int enemy = store.AddEnemy(0, 0, 1f, 100f, 100f, 1f, 1, 1);
            var catalog = Catalog(Execution(EffectPayloadKind.Damage, 5f, ExecutionOperation.ApplyDamage),
                costs: new[] { new CostDefinition(new AttributeKey(7), 6f) });
            var cooldowns = new float[1];
            Assert.True(GameplayAbilityRuntime.Activate(store, catalog, cooldowns, Request(enemy)).Accepted);
            Assert.Equal(1, store.AbilityRequests.Count);
            store.PlayerMana[0] = 1f;

            AbilityActivationResult committed = default;
            Exception? thrown = Record.Exception(() =>
                committed = GameplayAbilityRuntime.CommitQueuedAbilities(store));

            Assert.Null(thrown);
            Assert.False(committed.Accepted);
            Assert.Equal(AbilityActivationRejectReason.Cost, committed.Reason);
            Assert.Equal(1f, store.PlayerMana[0]);
            Assert.Equal(100f, store.EnemyHealth[enemy]);
            Assert.Equal(0f, cooldowns[0]);
            Assert.Contains(Enumerable.Range(0, store.DamageResolver.Events.Count),
                i => store.DamageResolver.Events.Get(i).Type == GameplayEventType.AbilityCancelled &&
                     store.DamageResolver.Events.Get(i).Reason == (int)AbilityActivationRejectReason.Cost);
            Assert.DoesNotContain(Enumerable.Range(0, store.DamageResolver.Events.Count),
                i => store.DamageResolver.Events.Get(i).Type == GameplayEventType.AbilityActivated);
            Assert.True(store.AbilityCommitReservation.IsEmpty);
        }

        [Fact]
        public void QueuedAbilityCooldownStolenBeforeCommit_AbilityCancelled_NoPayload()
        {
            var store = PlayerStore();
            store.DeferAbilityAndEffectCommit = true;
            int enemy = store.AddEnemy(0, 0, 1f, 100f, 100f, 1f, 1, 1);
            var catalog = Catalog(Execution(EffectPayloadKind.Damage, 5f, ExecutionOperation.ApplyDamage));
            var cooldowns = new float[1];
            Assert.True(GameplayAbilityRuntime.Activate(store, catalog, cooldowns, Request(enemy)).Accepted);
            cooldowns[0] = 1f;

            AbilityActivationResult committed = default;
            Exception? thrown = Record.Exception(() =>
                committed = GameplayAbilityRuntime.CommitQueuedAbilities(store));

            Assert.Null(thrown);
            Assert.False(committed.Accepted);
            Assert.Equal(AbilityActivationRejectReason.Cooldown, committed.Reason);
            Assert.Equal(100f, store.EnemyHealth[enemy]);
            Assert.Contains(Enumerable.Range(0, store.DamageResolver.Events.Count),
                i => store.DamageResolver.Events.Get(i).Type == GameplayEventType.AbilityCancelled &&
                     store.DamageResolver.Events.Get(i).Reason == (int)AbilityActivationRejectReason.Cooldown);
        }

        [Fact]
        public void SameFrameTwoEffectsReserveEffectRequests_SecondQueueOverflow()
        {
            var store = PlayerStore();
            store.DeferAbilityAndEffectCommit = true;
            var handle = store.GetEntityHandle(0);
            var context = new BattleSystemECS.Core.GAS.ExecutionContext(handle, handle, default, default, ClockId.Combat, 1L);
            int fill = store.EffectRequests.Capacity - 1;
            for (int i = 0; i < fill; i++)
                Assert.True(store.EffectRequests.TryAdd(new EffectRequest(handle, handle, new EffectId(0), 1,
                    ClockId.Combat, context)));
            var catalog = TwoDurationAbilities();
            var cooldowns = new float[2];

            var first = GameplayAbilityRuntime.Activate(store, catalog, cooldowns,
                new AbilityActivationRequest(0, 0, 0f, 0, new AbilityId(0)));
            Assert.True(first.Accepted, first.Reason.ToString());
            Assert.Equal(1, store.AbilityRequests.Count);
            Assert.True(store.AbilityCommitReservation.EffectRequests >= 1);

            var second = GameplayAbilityRuntime.Activate(store, catalog, cooldowns,
                new AbilityActivationRequest(0, 1, 0f, 0, new AbilityId(1)));
            Assert.False(second.Accepted);
            Assert.Equal(AbilityActivationRejectReason.QueueOverflow, second.Reason);
            Assert.Equal(1, store.AbilityRequests.Count);

            Exception? thrown = Record.Exception(() => GameplayAbilityRuntime.CommitQueuedAbilities(store));
            Assert.Null(thrown);
            Assert.True(store.AbilityCommitReservation.IsEmpty);
        }

        [Fact]
        public void SameFrameTwoModifiersReserveSlots_SecondQueueOverflow()
        {
            var store = PlayerStore();
            store.DeferAbilityAndEffectCommit = true;
            var source = store.GetEntityHandle(0);
            var fillerMods = new ModifierDefinition[store.GameplayEffectsRuntime.ModifierCapacity - 1];
            var one = new ModifierDefinition(new AttributeKey(8), AttributeModifierOp.Add, 0.01f);
            for (int i = 0; i < fillerMods.Length; i++) fillerMods[i] = one;
            var existing = DurationEffect(50, fillerMods);
            Assert.True(store.GameplayEffectsRuntime.TryApply(existing.Id, existing, source, source, out _));
            int before = store.GetEffectCount(0);
            var catalog = TwoDurationAbilities(one);
            var cooldowns = new float[2];

            var first = GameplayAbilityRuntime.Activate(store, catalog, cooldowns,
                new AbilityActivationRequest(0, 0, 0f, 0, new AbilityId(0)));
            Assert.True(first.Accepted, first.Reason.ToString());

            var second = GameplayAbilityRuntime.Activate(store, catalog, cooldowns,
                new AbilityActivationRequest(0, 1, 0f, 0, new AbilityId(1)));
            Assert.False(second.Accepted);
            Assert.Equal(AbilityActivationRejectReason.QueueOverflow, second.Reason);
            Assert.Equal(before, store.GetEffectCount(0));

            Exception? thrown = Record.Exception(() => GameplayAbilityRuntime.CommitQueuedAbilities(store));
            Assert.Null(thrown);
        }

        [Fact]
        public void RejectQueuedAbilities_ReleasesReservation()
        {
            var store = PlayerStore();
            store.DeferAbilityAndEffectCommit = true;
            store.PlayerMana[0] = 10f;
            int enemy = store.AddEnemy(0, 0, 1f, 100f, 100f, 1f, 1, 1);
            var catalog = Catalog(Execution(EffectPayloadKind.Damage, 5f, ExecutionOperation.ApplyDamage),
                costs: new[] { new CostDefinition(new AttributeKey(7), 6f) });
            Assert.True(GameplayAbilityRuntime.Activate(store, catalog, new float[1], Request(enemy)).Accepted);
            Assert.Equal(6f, store.AbilityCommitReservation.PeekCost(0, new AttributeKey(7)));

            GameplayAbilityRuntime.RejectQueuedAbilities(store);

            Assert.True(store.AbilityCommitReservation.IsEmpty);
            Assert.Equal(0f, store.AbilityCommitReservation.PeekCost(0, new AttributeKey(7)));
            Assert.Equal(0, store.AbilityRequests.Count);
            Assert.Equal(10f, store.PlayerMana[0]);
        }

        [Fact]
        public void BeginFrame_ClearsUnconsumedQueueAndReservation()
        {
            var store = PlayerStore();
            store.DeferAbilityAndEffectCommit = true;
            store.PlayerMana[0] = 10f;
            int enemy = store.AddEnemy(0, 0, 1f, 100f, 100f, 1f, 1, 1);
            var catalog = Catalog(Execution(EffectPayloadKind.Damage, 5f, ExecutionOperation.ApplyDamage),
                costs: new[] { new CostDefinition(new AttributeKey(7), 6f) });
            Assert.True(GameplayAbilityRuntime.Activate(store, catalog, new float[1], Request(enemy)).Accepted);
            Assert.False(store.AbilityCommitReservation.IsEmpty);
            int unconsumedBefore = store.UnconsumedAbilityRequests;

            store.BeginFrame();

            Assert.True(store.AbilityCommitReservation.IsEmpty);
            Assert.Equal(0f, store.AbilityCommitReservation.PeekCost(0, new AttributeKey(7)));
            Assert.Equal(0, store.AbilityRequests.Count);
            Assert.Equal(unconsumedBefore + 1, store.UnconsumedAbilityRequests);
        }

        [Fact]
        public void HandlerCommitFailureAfterSpend_RefundsManaAndCancels()
        {
            var store = PlayerStore();
            store.DeferAbilityAndEffectCommit = true;
            store.ResourceResolver.EnableDeferred(true);
            store.PlayerMana[0] = 10f;
            int enemy = store.AddEnemy(0, 0, 1f, 100f, 100f, 1f, 1, 1);
            var catalog = Catalog(Execution(EffectPayloadKind.Damage, 5f, ExecutionOperation.ApplyDamage),
                costs: new[] { new CostDefinition(new AttributeKey(7), 6f) });
            var handler = new FailCommitHandler();
            Assert.True(GameplayAbilityRuntime.Activate(store, catalog, new float[1], Request(enemy), handler).Accepted);

            AbilityActivationResult committed = default;
            Exception? thrown = Record.Exception(() =>
                committed = GameplayAbilityRuntime.CommitQueuedAbilities(store));

            Assert.Null(thrown);
            Assert.False(committed.Accepted);
            Assert.Equal(AbilityActivationRejectReason.UnsupportedDefinition, committed.Reason);
            Assert.Equal(10f, store.PlayerMana[0]);
            Assert.Equal(0, store.ResourceResolver.PendingRequestCount);
            Assert.Equal(100f, store.EnemyHealth[enemy]);
            Assert.Contains(Enumerable.Range(0, store.DamageResolver.Events.Count),
                i => store.DamageResolver.Events.Get(i).Type == GameplayEventType.AbilityCancelled &&
                     store.DamageResolver.Events.Get(i).Reason == (int)AbilityActivationRejectReason.UnsupportedDefinition);
            Assert.DoesNotContain(Enumerable.Range(0, store.DamageResolver.Events.Count),
                i => store.DamageResolver.Events.Get(i).Type == GameplayEventType.AbilityActivated);
        }

        [Fact]
        public void HandlerCommitFailure_TruncatesGrantedEffectRequests()
        {
            var store = PlayerStore();
            store.DeferAbilityAndEffectCommit = true;
            store.PlayerMana[0] = 10f;
            int enemy = store.AddEnemy(0, 0, 1f, 100f, 100f, 1f, 1, 1);
            var catalog = CatalogWithGrantedEffect(new[] { new CostDefinition(new AttributeKey(7), 6f) });
            var handler = new FailCommitHandler();
            Assert.True(GameplayAbilityRuntime.Activate(store, catalog, new float[1], Request(enemy), handler).Accepted);

            AbilityActivationResult committed = GameplayAbilityRuntime.CommitQueuedAbilities(store);

            Assert.False(committed.Accepted);
            Assert.Equal(0, store.EffectRequests.Count);
            store.GameplayEffectsRuntime.CommitQueuedEffects();
            Assert.Equal(0, store.GetEffectCount(enemy));
            Assert.Equal(10f, store.PlayerMana[0]);
        }

        [Fact]
        public void TwoCommitFailuresWithSameStickyRejection_MapEachTryApplyReason()
        {
            var store = PlayerStore();
            store.DeferAbilityAndEffectCommit = true;
            store.PlayerMana[0] = 0f;
            int first = store.AddEnemy(0, 0, 1f, 100f, 100f, 1f, 1, 1);
            int second = store.AddEnemy(1, 0, 1f, 100f, 100f, 1f, 1, 1);
            var catalog = TwoDamageAbilities(0f, 0f);
            var handler = new SpendFailHandler();
            var cooldowns = new float[2];
            Assert.True(GameplayAbilityRuntime.Activate(store, catalog, cooldowns,
                new AbilityActivationRequest(0, 0, 0f, first, new AbilityId(0)), handler).Accepted);
            Assert.True(GameplayAbilityRuntime.Activate(store, catalog, cooldowns,
                new AbilityActivationRequest(0, 1, 0f, second, new AbilityId(1)), handler).Accepted);

            AbilityActivationResult committed = GameplayAbilityRuntime.CommitQueuedAbilities(store);

            Assert.False(committed.Accepted);
            Assert.Equal(AbilityActivationRejectReason.Cost, committed.Reason);
            int cancelled = 0;
            for (int i = 0; i < store.DamageResolver.Events.Count; i++)
            {
                var ev = store.DamageResolver.Events.Get(i);
                if (ev.Type == GameplayEventType.AbilityCancelled &&
                    ev.Reason == (int)AbilityActivationRejectReason.Cost)
                    cancelled++;
            }
            Assert.Equal(2, cancelled);
        }

        [Fact]
        public void Spend_Insufficient_RejectsAtomically()
        {
            var store = PlayerStore();
            store.PlayerMana[0] = 3f;
            var handle = store.GetEntityHandle(0);
            var result = store.ResourceResolver.TryApply(new ResourceRequest(handle, handle, new AttributeKey(7),
                5f, ResourceOperation.Spend, 0, 11L, ownerPlayerId: 0));
            Assert.False(result.Accepted);
            Assert.Equal(ResourceRejectionReason.Insufficient, result.Reason);
            Assert.Equal(0f, result.Applied);
            Assert.Equal(3f, store.PlayerMana[0]);
        }

        [Fact]
        public void Spend_ExactAmount_AppliedEqualsNegativeRequest()
        {
            var store = PlayerStore();
            store.PlayerMana[0] = 7f;
            var handle = store.GetEntityHandle(0);
            var result = store.ResourceResolver.TryApply(new ResourceRequest(handle, handle, new AttributeKey(7),
                7f, ResourceOperation.Spend, 0, 12L, ownerPlayerId: 0));
            Assert.True(result.Accepted);
            Assert.Equal(-7f, result.Applied);
            Assert.Equal(0f, store.PlayerMana[0]);
        }

        [Fact]
        public void Spend_DoesNotEnterDeferredPending()
        {
            var store = PlayerStore();
            store.PlayerMana[0] = 8f;
            store.ResourceResolver.EnableDeferred(true);
            var handle = store.GetEntityHandle(0);
            var result = store.ResourceResolver.TryApply(new ResourceRequest(handle, handle, new AttributeKey(7),
                3f, ResourceOperation.Spend, 0, 13L, ownerPlayerId: 0));
            Assert.True(result.Accepted);
            Assert.False(result.Deferred);
            Assert.Equal(0, store.ResourceResolver.PendingRequestCount);
            Assert.Equal(5f, store.PlayerMana[0]);
        }

        [Fact]
        public void NegativeCurrentHealthAdd_ClampsToZeroAndStaysAccepted()
        {
            var store = PlayerStore();
            store.PlayerCurrentHealth[0] = 4f;
            store.PlayerMaxHealth[0] = 10f;
            var handle = store.GetEntityHandle(0);
            var result = store.ResourceResolver.TryApply(new ResourceRequest(handle, handle, new AttributeKey(3),
                -10f, 21L, ownerPlayerId: 0));
            Assert.True(result.Accepted);
            Assert.Equal(0f, store.PlayerCurrentHealth[0]);
            Assert.Equal(-4f, result.Applied);
        }

        private static ComponentStore PlayerStore()
        {
            var store = new ComponentStore();
            store.GameplayPhaseContext = new PhaseContext(PhaseContextKind.Wave);
            store.AddPlayer(0, 10f, 1f, 1f, 1);
            store.PlayerMaxHealth[0] = 10f;
            store.PlayerCurrentHealth[0] = 10f;
            store.PlayerMaxMana[0] = 20f;
            store.PlayerMana[0] = 10f;
            return store;
        }

        private static ExecutionDefinition Execution(EffectPayloadKind payload, float magnitude,
            ExecutionOperation operation, int id = 0) =>
            new ExecutionDefinition(new ExecutionId(id), payload, magnitude, new TagId(0),
                operation: operation);

        private static GameplayCatalog Catalog(ExecutionDefinition execution, CostDefinition[]? costs = null)
        {
            var targeting = new TargetingDefinition(new TargetingId(0), TargetingShape.Single, 10, 1, 1, 1);
            var ability = new AbilityDefinition(new AbilityId(0), "typed", targeting, ClockId.Combat, 2f,
                GameplayPhaseMask.Wave, Array.Empty<EffectId>(), Array.Empty<ModifierDefinition>(),
                CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer,
                executions: new[] { execution.Id }, costs: costs ?? Array.Empty<CostDefinition>());
            return new GameplayCatalog(new[] { ability }, new[] { targeting }, Array.Empty<GameplayEffectDefinition>(),
                new[] { execution }, Array.Empty<TriggerDefinition>(), Array.Empty<ModifierDefinition>(),
                new Dictionary<string, AbilityId> { ["typed"] = ability.Id });
        }

        private static GameplayCatalog CatalogWithGrantedEffect(CostDefinition[]? costs = null)
        {
            var targeting = new TargetingDefinition(new TargetingId(0), TargetingShape.Single, 10, 1, 1, 1);
            var effect = DurationEffect(0);
            var execution = Execution(EffectPayloadKind.Damage, 5f, ExecutionOperation.ApplyDamage);
            var ability = new AbilityDefinition(new AbilityId(0), "typed", targeting, ClockId.Combat, 2f,
                GameplayPhaseMask.Wave, new[] { effect.Id }, Array.Empty<ModifierDefinition>(),
                CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer,
                executions: new[] { execution.Id }, costs: costs ?? Array.Empty<CostDefinition>());
            return new GameplayCatalog(new[] { ability }, new[] { targeting }, new[] { effect },
                new[] { execution }, Array.Empty<TriggerDefinition>(), Array.Empty<ModifierDefinition>(),
                new Dictionary<string, AbilityId> { ["typed"] = ability.Id });
        }

        private static AbilityActivationRequest Request(int targetId) =>
            new AbilityActivationRequest(0, 0, 0f, targetId, new AbilityId(0));

        private sealed class FailCommitHandler : IAbilityPayloadHandler
        {
            public bool Supports(ExecutionDefinition execution) => true;
            public bool CanCommit(AbilityPayloadContext context) => true;
            public int Commit(AbilityPayloadContext context, out AbilityActivationRejectReason rejectReason)
            {
                rejectReason = AbilityActivationRejectReason.UnsupportedDefinition;
                return -1;
            }
            public void ContributeCommitCapacity(AbilityPayloadContext context,
                ref int resourceRequests, ref int resourceEvents, ref int damageRequests, ref int damageEvents)
            { }
        }

        private sealed class SpendFailHandler : IAbilityPayloadHandler
        {
            public bool Supports(ExecutionDefinition execution) => true;
            public bool CanCommit(AbilityPayloadContext context) => true;
            public int Commit(AbilityPayloadContext context, out AbilityActivationRejectReason rejectReason)
            {
                var source = context.Source;
                var spend = new ResourceRequest(source, source, new AttributeKey(7), 1f,
                    ResourceOperation.Spend, 0, context.Store.AllocateGameplaySequence(source.Index),
                    context.Request.OwnerPlayerId);
                var result = context.Store.ResourceResolver.TryApply(spend);
                if (result.Accepted)
                {
                    rejectReason = AbilityActivationRejectReason.None;
                    return 1;
                }
                rejectReason = GameplayAbilityRuntime.MapResourceReject(result.Reason);
                return -1;
            }
            public void ContributeCommitCapacity(AbilityPayloadContext context,
                ref int resourceRequests, ref int resourceEvents, ref int damageRequests, ref int damageEvents)
            { }
        }

        private static GameplayCatalog TwoDamageAbilities(float costA, float costB)
        {
            var targeting = new TargetingDefinition(new TargetingId(0), TargetingShape.Single, 10, 1, 1, 1);
            var execA = Execution(EffectPayloadKind.Damage, 5f, ExecutionOperation.ApplyDamage, 0);
            var execB = Execution(EffectPayloadKind.Damage, 5f, ExecutionOperation.ApplyDamage, 1);
            var a = new AbilityDefinition(new AbilityId(0), "a", targeting, ClockId.Combat, 1f,
                GameplayPhaseMask.Wave, Array.Empty<EffectId>(), Array.Empty<ModifierDefinition>(),
                CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer,
                executions: new[] { execA.Id }, costs: new[] { new CostDefinition(new AttributeKey(7), costA) });
            var b = new AbilityDefinition(new AbilityId(1), "b", targeting, ClockId.Combat, 1f,
                GameplayPhaseMask.Wave, Array.Empty<EffectId>(), Array.Empty<ModifierDefinition>(),
                CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer,
                executions: new[] { execB.Id }, costs: new[] { new CostDefinition(new AttributeKey(7), costB) });
            return new GameplayCatalog(new[] { a, b }, new[] { targeting }, Array.Empty<GameplayEffectDefinition>(),
                new[] { execA, execB }, Array.Empty<TriggerDefinition>(), Array.Empty<ModifierDefinition>(),
                new Dictionary<string, AbilityId> { ["a"] = a.Id, ["b"] = b.Id });
        }

        private static GameplayEffectDefinition DurationEffect(int id, ModifierDefinition[]? modifiers = null) =>
            new GameplayEffectDefinition(new EffectId(id), EffectType.Duration,
                modifiers ?? Array.Empty<ModifierDefinition>(),
                3f, 0f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist,
                EffectPayloadKind.GameplayEvent, new TagId(id), Array.Empty<ExecutionId>());

        private static GameplayCatalog TwoDurationAbilities(ModifierDefinition? extra = null)
        {
            var targeting = new TargetingDefinition(new TargetingId(0), TargetingShape.Single, 1, 1, 1, 1);
            var mods = extra.HasValue ? new[] { extra.Value } : Array.Empty<ModifierDefinition>();
            var e0 = DurationEffect(0, mods.Length == 0 ? null : mods);
            var e1 = DurationEffect(1, mods.Length == 0 ? null : mods);
            var a = new AbilityDefinition(new AbilityId(0), "e0", targeting, ClockId.Combat, 1f,
                GameplayPhaseMask.Wave, new[] { e0.Id }, Array.Empty<ModifierDefinition>(),
                CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer);
            var b = new AbilityDefinition(new AbilityId(1), "e1", targeting, ClockId.Combat, 1f,
                GameplayPhaseMask.Wave, new[] { e1.Id }, Array.Empty<ModifierDefinition>(),
                CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer);
            return new GameplayCatalog(new[] { a, b }, new[] { targeting }, new[] { e0, e1 },
                Array.Empty<ExecutionDefinition>(), Array.Empty<TriggerDefinition>(), Array.Empty<ModifierDefinition>(),
                new Dictionary<string, AbilityId> { ["e0"] = a.Id, ["e1"] = b.Id });
        }
    }
}
