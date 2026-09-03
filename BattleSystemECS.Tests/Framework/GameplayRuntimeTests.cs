using System;
using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Systems;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class GameplayRuntimeTests
    {
        [Fact]
        public void PeriodicRuntimeSubmitsNonZeroDamageWithOwnerAndProvenance()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 10f, 1f, 1f, 1);
            store.PlayerMaxMana[0] = 100f;
            int sourceId = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            int targetId = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            var definition = new GameplayEffectDefinition(new EffectId(41), EffectType.Periodic, Array.Empty<ModifierDefinition>(), 3f, 1f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.Damage, default(TagId), Array.Empty<ExecutionId>(), periodicMagnitude: 2f);
            var source = store.GetEntityHandle(sourceId);
            var target = store.GetEntityHandle(targetId);
            Assert.True(store.GameplayEffectsRuntime.TryApply(definition.Id, definition, source, target, out _, ownerPlayerId: 0, provenanceId: 77));

            store.GameplayEffectsRuntime.Tick(1f, ClockId.Combat);

            Assert.True(store.EnemyHealth[targetId] < 10f);
            Assert.Contains(GameplayEventType.EffectApplied, Events(store.GameplayEffectsRuntime.Events));
            Assert.Contains(GameplayEventType.HitConfirmed, Events(store.DamageResolver.Events));
            var hit = store.DamageResolver.Events.Get(0);
            Assert.Equal(41, hit.EffectDefinition.Value);
            Assert.Equal(77L, hit.ProvenanceId);
        }

        [Fact]
        public void TriggerFourteenHitsLeavesRemainderAndAggregatesOneSourceStack()
        {
            var store = new ComponentStore();
            int sourceId = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            int targetId = store.AddEnemy(0, 0, 1f, 100f, 100f, 1f, 1, 1);
            var source = store.GetEntityHandle(sourceId);
            var target = store.GetEntityHandle(targetId);
            var effect = new GameplayEffectDefinition(new EffectId(900), EffectType.Duration,
                new[] { new ModifierDefinition(new AttributeKey(8), AttributeModifierOp.Add, 0.30f) }, 1f, 0f, ClockId.Combat,
                StackingBehavior.MaxStacksRefresh, 5, RefreshPolicy.StacksAndDuration, SourceDeathPolicy.Persist,
                EffectPayloadKind.GameplayEvent, default(TagId), Array.Empty<ExecutionId>(), stackKey: new TagId(901));
            var trigger = new TriggerDefinition(new TriggerId(900), GameplayEventType.HitConfirmed, effect.Id, CatalogRegistries.SkillConsumer,
                scope: TriggerScope.PerSource, threshold: 10, mode: TriggerMode.EveryN, preserveRemainder: true);
            var runtime = store.GameplayTriggersRuntime;
            runtime.RegisterEffect(effect);
            var events = new GameplayEventQueue(32);
            for (int i = 0; i < 14; i++) Assert.True(events.TryPublish(new GameplayEvent(GameplayEventType.HitConfirmed, source, target, i + 1L)));

            Assert.Equal(1, runtime.Consume(events, new[] { trigger }));
            Assert.Equal(4, runtime.GetCounter(trigger, source, target));
            Assert.Equal(1, store.GetEffectCount(sourceId));
            store.AttributeAggregator.SetBase(sourceId, new AttributeKey(8), 1f);
            store.AttributeAggregator.AggregateDirty();
            Assert.Equal(1.30f, store.AttributeAggregator.GetComputed(sourceId, new AttributeKey(8), 1f), 3);
        }

        [Fact]
        public void TriggerStackAddsModifierPerLayerAndHonorsMaximum()
        {
            var store = new ComponentStore();
            int sourceId = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            int targetId = store.AddEnemy(0, 0, 1f, 100f, 100f, 1f, 1, 1);
            var source = store.GetEntityHandle(sourceId); var target = store.GetEntityHandle(targetId);
            var effect = new GameplayEffectDefinition(new EffectId(901), EffectType.Duration, new[] { new ModifierDefinition(new AttributeKey(8), AttributeModifierOp.Add, 0.30f) }, 1f, 0f, ClockId.Combat, StackingBehavior.MaxStacksRefresh, 2, RefreshPolicy.StacksAndDuration, SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent, default(TagId), Array.Empty<ExecutionId>(), stackKey: new TagId(902));
            var trigger = new TriggerDefinition(new TriggerId(901), GameplayEventType.HitConfirmed, effect.Id, CatalogRegistries.SkillConsumer, scope: TriggerScope.PerSource, threshold: 1, mode: TriggerMode.EveryN);
            store.GameplayTriggersRuntime.RegisterEffect(effect);
            for (int i = 0; i < 3; i++)
            {
                var q = new GameplayEventQueue(2); Assert.True(q.TryPublish(new GameplayEvent(GameplayEventType.HitConfirmed, source, target, i + 1L)));
                store.GameplayTriggersRuntime.Consume(q, new[] { trigger });
            }
            Assert.True(store.TryGetActiveEffectAt(sourceId, 0, out var active, out _, out _));
            Assert.Equal(2, active.StackCount);
            store.AttributeAggregator.SetBase(sourceId, new AttributeKey(8), 1f); store.AttributeAggregator.AggregateDirty();
            Assert.Equal(1.60f, store.AttributeAggregator.GetComputed(sourceId, new AttributeKey(8), 1f), 3);
        }

        [Fact]
        public void RuntimePeriodicEffectTicksAndExpiresThroughFrameScheduler()
        {
            var store = new ComponentStore(); store.AddPlayer(0, 10f, 1f, 1f, 1);
            int source = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1); int target = store.AddEnemy(0, 0, 1f, 5f, 5f, 1f, 1, 1);
            var def = new GameplayEffectDefinition(new EffectId(77), EffectType.Periodic, Array.Empty<ModifierDefinition>(), 1f, 1f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.Damage, default(TagId), Array.Empty<ExecutionId>(), periodicMagnitude: 1f);
            Assert.True(store.GameplayEffectsRuntime.TryApply(def.Id, def, store.GetEntityHandle(source), store.GetEntityHandle(target), out _, ownerPlayerId: 0));
            var scheduler = new FrameScheduler(store, new Config.GameConfig()); scheduler.SkillBuff.Buff = null;
            scheduler.SealGraphComposition();
            scheduler.Tick(1f, 0);
            Assert.Equal(4f, store.EnemyHealth[target]); Assert.Equal(0, store.GetEffectCount(target)); Assert.Contains(GameplayEventType.HitConfirmed, Events(store.DamageResolver.Events)); Assert.Contains(GameplayEventType.EffectExpired, Events(store.GameplayEffectsRuntime.Events));
        }

        [Fact]
        public void PeriodicFirstTickAndCatchUpPoliciesHaveDistinctResults()
        {
            var store = new ComponentStore(); store.AddPlayer(0, 10f, 1f, 1f, 1); int source = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1); int immediateTarget = store.AddEnemy(0, 0, 1f, 20f, 20f, 1f, 1, 1); int nextTarget = store.AddEnemy(0, 0, 1f, 20f, 20f, 1f, 1, 1); int onePerFrameTarget = store.AddEnemy(0, 0, 1f, 20f, 20f, 1f, 1, 1);
            var immediateSpec = new PeriodicSpec(1f, new ExecutionId(78), EffectPayloadKind.Damage, MagnitudeSource.Constant, FirstTickPolicy.Immediate, CatchUpPolicy.CatchUpAll, magnitude: 1f);
            var nextSpec = new PeriodicSpec(1f, new ExecutionId(79), EffectPayloadKind.Damage, MagnitudeSource.Constant, FirstTickPolicy.NextInterval, CatchUpPolicy.CatchUpAll, magnitude: 1f);
            var oneSpec = new PeriodicSpec(1f, new ExecutionId(80), EffectPayloadKind.Damage, MagnitudeSource.Constant, FirstTickPolicy.NextInterval, CatchUpPolicy.OnePerFrame, magnitude: 1f);
            var immediate = new GameplayEffectDefinition(new EffectId(78), EffectType.Periodic, Array.Empty<ModifierDefinition>(), 5f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.Damage, default(TagId), immediateSpec, Array.Empty<ExecutionId>());
            var next = new GameplayEffectDefinition(new EffectId(79), EffectType.Periodic, Array.Empty<ModifierDefinition>(), 5f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.Damage, default(TagId), nextSpec, Array.Empty<ExecutionId>());
            var one = new GameplayEffectDefinition(new EffectId(80), EffectType.Periodic, Array.Empty<ModifierDefinition>(), 5f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.Damage, default(TagId), oneSpec, Array.Empty<ExecutionId>());
            Assert.True(store.GameplayEffectsRuntime.TryApply(immediate.Id, immediate, store.GetEntityHandle(source), store.GetEntityHandle(immediateTarget), out var immediateHandle, ownerPlayerId: 0));
            Assert.True(store.GameplayEffectsRuntime.TryApply(next.Id, next, store.GetEntityHandle(source), store.GetEntityHandle(nextTarget), out var nextHandle, ownerPlayerId: 0));
            Assert.True(store.GameplayEffectsRuntime.TryApply(one.Id, one, store.GetEntityHandle(source), store.GetEntityHandle(onePerFrameTarget), out var oneHandle, ownerPlayerId: 0));
            store.GameplayEffectsRuntime.Tick(0.1f, ClockId.Combat);
            Assert.Equal(19f, store.EnemyHealth[immediateTarget], 3); Assert.Equal(20f, store.EnemyHealth[nextTarget], 3);
            store.GameplayEffectsRuntime.Tick(3f, ClockId.Combat);
            Assert.Equal(16f, store.EnemyHealth[immediateTarget], 3);
            Assert.Equal(17f, store.EnemyHealth[nextTarget], 3);
            Assert.Equal(19f, store.EnemyHealth[onePerFrameTarget], 3);
            Assert.True(store.GameplayEffects.TryGet(immediateHandle, out var immediateActive, out _, out _));
            Assert.Equal(4, immediateActive.TicksProcessed);
            Assert.True(store.GameplayEffects.TryGet(nextHandle, out var nextActive, out _, out _));
            Assert.Equal(3, nextActive.TicksProcessed);
            Assert.True(store.GameplayEffects.TryGet(oneHandle, out var oneActive, out _, out _)); Assert.Equal(1, oneActive.TicksProcessed);
        }

        [Fact]
        // Bug 回归：已有 stack-key 的 refresh/stack 复用必须回写有效的 out handle，并保持层数语义。
        // EffectId/TagId 使用本测试隔离注入值，避免依赖生产 Catalog 内容。
        public void ReapplyingExistingStackKeyReturnsExistingEffectHandle()
        {
            var store = new ComponentStore();
            int source = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            int target = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            var sourceHandle = store.GetEntityHandle(source);
            var targetHandle = store.GetEntityHandle(target);

            var refresh = new GameplayEffectDefinition(new EffectId(880), EffectType.Duration,
                Array.Empty<ModifierDefinition>(), 2f, 0f, ClockId.Combat,
                StackingBehavior.DurationRefresh, 1, RefreshPolicy.Duration,
                SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent,
                default(TagId), Array.Empty<ExecutionId>(), stackKey: new TagId(881));
            Assert.True(store.GameplayEffectsRuntime.TryApply(refresh.Id, refresh, sourceHandle, targetHandle,
                out var first, ownerPlayerId: 0));
            Assert.True(first.IsValid);
            Assert.True(store.GameplayEffects.TryGet(first, out var initialRuntime, out _, out _));
            Assert.Equal(2f, initialRuntime.RemainingTime, 3);
            store.GameplayEffectsRuntime.Tick(0.5f, ClockId.Combat);
            Assert.True(store.GameplayEffects.TryGet(first, out var tickedRuntime, out _, out _));
            Assert.Equal(1.5f, tickedRuntime.RemainingTime, 3);
            Assert.True(store.GameplayEffectsRuntime.TryApply(refresh.Id, refresh, sourceHandle, targetHandle,
                out var refreshed, ownerPlayerId: 0));
            Assert.Equal(first, refreshed);
            Assert.True(refreshed.IsValid);
            Assert.True(store.GameplayEffects.TryGet(refreshed, out var refreshedRuntime, out _, out _));
            Assert.Equal(2f, refreshedRuntime.RemainingTime, 3);

            var stacked = new GameplayEffectDefinition(new EffectId(882), EffectType.Duration,
                Array.Empty<ModifierDefinition>(), 2f, 0f, ClockId.Combat,
                StackingBehavior.MaxStacksRefresh, 3, RefreshPolicy.StacksAndDuration,
                SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent,
                default(TagId), Array.Empty<ExecutionId>(), stackKey: new TagId(883));
            Assert.True(store.GameplayEffectsRuntime.TryApply(stacked.Id, stacked, sourceHandle, targetHandle,
                out var stackFirst, ownerPlayerId: 0));
            Assert.True(stackFirst.IsValid);
            Assert.True(store.GameplayEffects.TryGet(stackFirst, out var firstStacked, out _, out _));
            Assert.Equal(1, firstStacked.StackCount);
            Assert.True(store.GameplayEffectsRuntime.TryApply(stacked.Id, stacked, sourceHandle, targetHandle,
                out var stackUpdated, stackDelta: 1, ownerPlayerId: 0));
            Assert.Equal(stackFirst, stackUpdated);
            Assert.True(stackUpdated.IsValid);
            Assert.True(store.GameplayEffects.TryGet(stackUpdated, out var updatedStacked, out _, out _));
            Assert.Equal(2, updatedStacked.StackCount);
        }

        [Fact]
        public void SourceDeathRemovePolicyClearsEffectBeforeNextTick()
        {
            var store = new ComponentStore(); int source = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1); int target = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1); var def = new GameplayEffectDefinition(new EffectId(79), EffectType.Periodic, Array.Empty<ModifierDefinition>(), 4f, 1f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Remove, EffectPayloadKind.Damage, default(TagId), Array.Empty<ExecutionId>(), periodicMagnitude: 1f); Assert.True(store.GameplayEffectsRuntime.TryApply(def.Id, def, store.GetEntityHandle(source), store.GetEntityHandle(target), out _, ownerPlayerId: 0)); store.DestroyEntity(source); store.GameplayEffectsRuntime.Tick(1f, ClockId.Combat); Assert.Equal(0, store.GetEffectCount(target));
        }

        [Fact]
        public void TargetRecycleRejectsStaleEffect()
        {
            var store = new ComponentStore(); int source = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1); int target = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1); var old = store.GetEntityHandle(target); var def = new GameplayEffectDefinition(new EffectId(80), EffectType.Duration, Array.Empty<ModifierDefinition>(), 2f, 0f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent, default(TagId), Array.Empty<ExecutionId>()); Assert.True(store.GameplayEffectsRuntime.TryApply(def.Id, def, store.GetEntityHandle(source), old, out var h)); store.DestroyEntity(target); int recycled = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1); Assert.Equal(target, recycled); Assert.NotEqual(old.Generation, store.GetEntityHandle(recycled).Generation); Assert.False(store.GameplayEffects.TryGet(h, out _, out _, out _));
        }

        [Fact]
        // Bug 回归：PerSourceTarget 必须同时隔离 source/target generation。
        public void TriggerScopesKeepGenerationsAndTargetsSeparate()
        {
            var store = new ComponentStore();
            int sourceId = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            int targetId = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            var source = store.GetEntityHandle(sourceId);
            var target = store.GetEntityHandle(targetId);
            var marker = new GameplayEffectDefinition(new EffectId(81), EffectType.Duration,
                Array.Empty<ModifierDefinition>(), 1f, 0f, ClockId.Combat, StackingBehavior.None, 1,
                RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent,
                default(TagId), Array.Empty<ExecutionId>());
            var trigger = new TriggerDefinition(new TriggerId(81), GameplayEventType.HitConfirmed,
                marker.Id, CatalogRegistries.SkillConsumer, scope: TriggerScope.PerSourceTarget,
                threshold: 2, mode: TriggerMode.EveryN, preserveRemainder: true);
            Assert.True(store.GameplayTriggersRuntime.RegisterEffect(marker));
            var first = new GameplayEventQueue(2);
            Assert.True(first.TryPublish(new GameplayEvent(GameplayEventType.HitConfirmed, source, target, 1L)));
            Assert.True(first.TryPublish(new GameplayEvent(GameplayEventType.HitConfirmed, source, target, 2L)));
            Assert.Equal(1, store.GameplayTriggersRuntime.Consume(first, new[] { trigger }));
            Assert.Equal(1, store.GetEffectCount(sourceId));
            Assert.Equal(0, store.GameplayTriggersRuntime.GetCounter(trigger, source, target));
            store.DestroyEntity(sourceId);
            int recycledSource = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            var recycled = store.GetEntityHandle(recycledSource);
            var second = new GameplayEventQueue(1);
            Assert.True(second.TryPublish(new GameplayEvent(GameplayEventType.HitConfirmed, recycled, target, 3L)));
            store.GameplayTriggersRuntime.Consume(second, new[] { trigger });
            Assert.Equal(1, store.GameplayTriggersRuntime.GetCounter(trigger, recycled, target));
            Assert.NotEqual(source.Generation, recycled.Generation);
        }

        [Fact]
        // Bug 回归：标签匹配成功触发，标签不匹配必须拒绝。
        public void TriggerTagFilterMatchesAndRejects()
        {
            var store = new ComponentStore();
            int sourceId = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            int targetId = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            var marker = new GameplayEffectDefinition(new EffectId(82), EffectType.Duration,
                Array.Empty<ModifierDefinition>(), 1f, 0f, ClockId.Combat, StackingBehavior.None, 1,
                RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent,
                new TagId(4), Array.Empty<ExecutionId>());
            var trigger = new TriggerDefinition(new TriggerId(82), GameplayEventType.HitConfirmed,
                marker.Id, CatalogRegistries.SkillConsumer, new[] { new TagId(4) }, new TagId(4));
            Assert.True(store.GameplayTriggersRuntime.RegisterEffect(marker));
            var source = store.GetEntityHandle(sourceId); var target = store.GetEntityHandle(targetId);
            var matching = new GameplayEventQueue(1);
            Assert.True(matching.TryPublish(new GameplayEvent(GameplayEventType.HitConfirmed, source, target,
                default(EffectHandle), default(EffectId), 1L, tag: new TagId(4))));
            Assert.Equal(1, store.GameplayTriggersRuntime.Consume(matching, new[] { trigger }));
            Assert.Equal(1, store.GetEffectCount(sourceId));
            store.GameplayTriggersRuntime.ResetFrame();
            var mismatch = new GameplayEventQueue(1);
            Assert.True(mismatch.TryPublish(new GameplayEvent(GameplayEventType.HitConfirmed, source, target,
                default(EffectHandle), default(EffectId), 2L, tag: new TagId(5))));
            Assert.Equal(0, store.GameplayTriggersRuntime.Consume(mismatch, new[] { trigger }));
            Assert.Contains(GameplayEventType.EffectRejected, Events(store.GameplayTriggersRuntime.NextEvents));
        }

        [Fact]
        public void TriggerOverflowPublishesAbortAndNextFrameRecovers()
        {
            var store = new ComponentStore(); var runtime = new GameplayTriggerRuntime(store, store.GameplayEffectsRuntime, 1, 1); var q = new GameplayEventQueue(2); Assert.True(q.TryPublish(new GameplayEvent(GameplayEventType.HitConfirmed, default(EntityHandle), default(EntityHandle), 1))); Assert.True(q.TryPublish(new GameplayEvent(GameplayEventType.HitConfirmed, default(EntityHandle), default(EntityHandle), 2))); runtime.Consume(q, Array.Empty<TriggerDefinition>()); Assert.True(runtime.LoopAborts > 0); int aborts = runtime.LoopAborts; runtime.ResetFrame(); var recovered = new GameplayEventQueue(1); Assert.True(recovered.TryPublish(new GameplayEvent(GameplayEventType.HitConfirmed, default(EntityHandle), default(EntityHandle), 3))); Assert.Equal(0, runtime.Consume(recovered, Array.Empty<TriggerDefinition>(), clear: true)); Assert.Equal(aborts, runtime.LoopAborts); Assert.Equal(0, runtime.NextEvents.Count);
        }

        [Fact]
        // Bug 回归：KillConfirmed 只能在真实死亡解析后触发。
        public void KillConfirmedIsConsumedOnlyAfterDeathResolve()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 10f, 1f, 1f, 1);
            int enemyId = store.AddEnemy(0f, 0f, 1f, 1f, 1f, 1f, 1, 1);
            var marker = new GameplayEffectDefinition(new EffectId(810), EffectType.Duration,
                Array.Empty<ModifierDefinition>(), 1f, 0f, ClockId.Combat, StackingBehavior.None, 1,
                RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent,
                default(TagId), Array.Empty<ExecutionId>());
            var trigger = new TriggerDefinition(new TriggerId(810), GameplayEventType.KillConfirmed,
                marker.Id, CatalogRegistries.SkillConsumer, effectTarget: EffectTargetPolicy.Source);
            var queuedMarker = new GameplayEffectDefinition(new EffectId(811), EffectType.Duration,
                Array.Empty<ModifierDefinition>(), 1f, 0f, ClockId.Combat, StackingBehavior.None, 1,
                RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent,
                default(TagId), Array.Empty<ExecutionId>());
            var queuedTrigger = new TriggerDefinition(new TriggerId(811), GameplayEventType.DeathQueued,
                queuedMarker.Id, CatalogRegistries.SkillConsumer, effectTarget: EffectTargetPolicy.Source);
            var runtime = store.GameplayTriggersRuntime;
            Assert.True(runtime.RegisterEffect(marker));
            Assert.True(runtime.RegisterEffect(queuedMarker));
            Assert.Equal(0, runtime.ConsumeOnly(store.DamageResolver.Events, new[] { trigger },
                true, GameplayEventType.KillConfirmed, GameplayEventType.ResourceChanged));
            var player = store.GetEntityHandle(0);
            Assert.True(store.DamageResolver.TryApply(new DamageRequest(player,
                store.GetEntityHandle(enemyId), 2f, DamageType.True, 1L, ownerPlayerId: 0)).Accepted);
            store.ResolveEnemiesKilledThisFrame();
            Assert.Equal(1, runtime.ConsumeOnly(store.DamageResolver.Events, new[] { queuedTrigger },
                false, GameplayEventType.DeathQueued));
            Assert.Equal(1, store.GetEffectCount(0));
            Assert.Equal(1, runtime.ConsumeOnly(store.DamageResolver.Events, new[] { trigger },
                true, GameplayEventType.KillConfirmed, GameplayEventType.ResourceChanged));
            Assert.Equal(2, store.GetEffectCount(0));
        }

        [Fact]
        public void LegacyPeriodicIsNotDoubleTicked()
        {
            var store = new ComponentStore(); store.AddPlayer(0, 10f, 1f, 1f, 1); int enemy = store.AddEnemy(0, 0, 1f, 20f, 20f, 1f, 1, 1); var legacy = new BuffSystem(store, 0); legacy.ApplyDot(enemy, 1f, 2); var typed = new GameplayEffectDefinition(new EffectId(83), EffectType.Periodic, Array.Empty<ModifierDefinition>(), 2f, 1f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.Damage, default(TagId), Array.Empty<ExecutionId>(), periodicMagnitude: 1f); Assert.True(store.GameplayEffectsRuntime.TryApply(typed.Id, typed, store.GetEntityHandle(0), store.GetEntityHandle(enemy), out _, ownerPlayerId: 0)); var scheduler = new FrameScheduler(store, new Config.GameConfig()); scheduler.SkillBuff.Buff = legacy; scheduler.SealGraphComposition(); scheduler.Tick(1f, 0); Assert.Equal(18f, store.EnemyHealth[enemy], 3);
        }

        [Fact]
        public void UnsupportedPeriodicPayloadIsRejectedBeforeTick()
        {
            var store = new ComponentStore();
            int source = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            int target = store.AddEnemy(0, 0, 1f, 20f, 20f, 1f, 1, 1);
            var periodic = new PeriodicSpec(1f, new ExecutionId(7), EffectPayloadKind.CrowdControl, MagnitudeSource.Constant, FirstTickPolicy.NextInterval, CatchUpPolicy.CatchUpAll, magnitude: 2f);
            var def = new GameplayEffectDefinition(new EffectId(84), EffectType.Periodic, Array.Empty<ModifierDefinition>(), 3f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.CrowdControl, default(TagId), periodic, Array.Empty<ExecutionId>());
            Assert.False(store.GameplayEffectsRuntime.TryApply(def.Id, def, store.GetEntityHandle(source), store.GetEntityHandle(target), out _));
            Assert.Contains(GameplayEventType.EffectRejected, Events(store.GameplayEffectsRuntime.Events));
            Assert.Equal(0, store.GetEffectCount(target));
        }

        [Fact]
        public void EffectTagMatchingAndMismatchHaveOppositeResults()
        {
            var store = new ComponentStore();
            int source = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            int target = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            var effect = new GameplayEffectDefinition(new EffectId(85), EffectType.Instant, Array.Empty<ModifierDefinition>(), 0f, 0f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent, new TagId(5), Array.Empty<ExecutionId>());
            var trigger = new TriggerDefinition(new TriggerId(85), GameplayEventType.HitConfirmed, effect.Id, CatalogRegistries.SkillConsumer, effectTag: new TagId(5));
            var runtime = store.GameplayTriggersRuntime; runtime.RegisterEffect(effect);
            var matching = new GameplayEventQueue(2);
            Assert.True(matching.TryPublish(new GameplayEvent(GameplayEventType.HitConfirmed, store.GetEntityHandle(source), store.GetEntityHandle(target), default(EffectHandle), default(EffectId), DamageFlags.None, 1L, tag: new TagId(5))));
            Assert.Equal(1, runtime.Consume(matching, new[] { trigger }));
            var mismatch = new GameplayEventQueue(2);
            Assert.True(mismatch.TryPublish(new GameplayEvent(GameplayEventType.HitConfirmed, store.GetEntityHandle(source), store.GetEntityHandle(target), default(EffectHandle), default(EffectId), DamageFlags.None, 2L, tag: new TagId(4))));
            runtime.Consume(mismatch, new[] { trigger });
            Assert.Contains(GameplayEventType.EffectRejected, Events(runtime.NextEvents));
        }

        [Fact]
        public void RuntimeEventsAreClearedAtSchedulerFrameBoundary()
        {
            var store = new ComponentStore();
            int source = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            int target = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            var instant = new GameplayEffectDefinition(new EffectId(86), EffectType.Instant, Array.Empty<ModifierDefinition>(), 0f, 0f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent, default(TagId), Array.Empty<ExecutionId>());
            Assert.True(store.GameplayEffectsRuntime.TryApply(instant.Id, instant, store.GetEntityHandle(source), store.GetEntityHandle(target), out _));
            Assert.True(store.GameplayEffectsRuntime.Events.Count > 0);
            var scheduler = new FrameScheduler(store, new Config.GameConfig());
            scheduler.SealGraphComposition();
            scheduler.Tick(0f, 0);
            Assert.Equal(0, store.GameplayEffectsRuntime.Events.Count);
        }

        [Fact]
        public void RegisterEffectCapacityFailureIsObservableAndDoesNotRegister()
        {
            var store = new ComponentStore();
            var runtime = store.GameplayTriggersRuntime;
            for (int i = 0; i < runtime.DefinitionCapacity; i++)
            {
                var def = new GameplayEffectDefinition(new EffectId(10000 + i), EffectType.Duration, Array.Empty<ModifierDefinition>(), 1f, 0f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent, default(TagId), Array.Empty<ExecutionId>());
                Assert.True(runtime.RegisterEffect(def));
            }
            var rejected = new GameplayEffectDefinition(new EffectId(20000), EffectType.Duration, Array.Empty<ModifierDefinition>(), 1f, 0f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent, default(TagId), Array.Empty<ExecutionId>());
            Assert.False(runtime.RegisterEffect(rejected));
            Assert.True(runtime.Rejections > 0);
            Assert.Contains(GameplayEventType.EffectRejected, Events(runtime.NextEvents));
        }

        [Fact]
        public void SchedulerTicksAllDefinitionDrivenGameplayClocks()
        {
            var store = new ComponentStore();
            int source = store.AddEnemy(0f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            int target = store.AddEnemy(1f, 0f, 1f, 100f, 100f, 1f, 1, 1);
            var sourceHandle = store.GetEntityHandle(source);
            var targetHandle = store.GetEntityHandle(target);
            var clocks = new[] { ClockId.Combat, ClockId.Enemy, ClockId.RealTime, ClockId.Global };
            for (int i = 0; i < clocks.Length; i++)
            {
                var spec = new PeriodicSpec(1f, new ExecutionId(90 + i), EffectPayloadKind.Damage, MagnitudeSource.Constant, FirstTickPolicy.NextInterval, CatchUpPolicy.CatchUpAll, magnitude: 1f);
                var def = new GameplayEffectDefinition(new EffectId(90 + i), EffectType.Periodic, Array.Empty<ModifierDefinition>(), 2f, clocks[i], StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.Damage, default(TagId), spec, Array.Empty<ExecutionId>());
                Assert.True(store.GameplayEffectsRuntime.TryApply(def.Id, def, sourceHandle, targetHandle, out _, ownerPlayerId: 0));
            }
            var scheduler = new FrameScheduler(store, new Config.GameConfig());
            scheduler.SkillBuff.Buff = null;
            scheduler.SealGraphComposition();
            scheduler.Tick(1f, 0);
            Assert.Equal(96f, store.EnemyHealth[target], 3);
        }

        [Fact]
        public void PeriodicHealUsesHealResolverAndRaisesPlayerHealth()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 10f, 1f, 1f, 1);
            store.PlayerMaxHealth[0] = 10f;
            store.SetPlayerCurrentHealth(0, 5f);
            int source = store.AddEnemy(0f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            var spec = new PeriodicSpec(1f, new ExecutionId(95), EffectPayloadKind.Heal, MagnitudeSource.Constant, FirstTickPolicy.NextInterval, CatchUpPolicy.CatchUpAll, magnitude: 2f);
            var def = new GameplayEffectDefinition(new EffectId(95), EffectType.Periodic, Array.Empty<ModifierDefinition>(), 2f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.Heal, default(TagId), spec, Array.Empty<ExecutionId>());
            Assert.True(store.GameplayEffectsRuntime.TryApply(def.Id, def, store.GetEntityHandle(source), store.GetEntityHandle(0), out _ , ownerPlayerId: 0));
            store.GameplayEffectsRuntime.Tick(1f, ClockId.Combat);
            store.ResourceResolver.CommitBoundary(DamageCommitBoundary.GameplayResolve);
            Assert.Equal(7f, store.PlayerCurrentHealth[0], 3);
            Assert.Contains(GameplayEventType.HealApplied, Events(store.ResourceResolver.Events));
        }

        [Fact]
        public void TriggerRoundLimitPublishesDurableAbortAndResetsNextFrame()
        {
            var store = new ComponentStore();
            var runtime = new GameplayTriggerRuntime(store, store.GameplayEffectsRuntime, 4, 1);
            Assert.True(runtime.NextEvents.TryPublish(new GameplayEvent(GameplayEventType.HitConfirmed, default(EntityHandle), default(EntityHandle), 1L)));
            Assert.True(runtime.NextEvents.TryPublish(new GameplayEvent(GameplayEventType.HitConfirmed, default(EntityHandle), default(EntityHandle), 2L)));
            runtime.ConsumeNextRounds(Array.Empty<TriggerDefinition>());
            Assert.True(runtime.LoopAborts > 0);
            Assert.True(runtime.AbortEvents.Count > 0);
            runtime.ResetFrame();
            Assert.Equal(0, runtime.NextEvents.Count);
        }

        [Fact]
        public void SchedulerConsumesKillConfirmedAfterDestroyWithSourceTargetPolicy()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 10f, 1f, 1f, 1);
            int enemy = store.AddEnemy(0f, 0f, 1f, 1f, 1f, 1f, 1, 1);
            var killSpec = new PeriodicSpec(1f, new ExecutionId(97), EffectPayloadKind.Damage, MagnitudeSource.Constant, FirstTickPolicy.NextInterval, CatchUpPolicy.CatchUpAll, magnitude: 1f);
            var effect = new GameplayEffectDefinition(new EffectId(97), EffectType.Duration, Array.Empty<ModifierDefinition>(), 2f, 0f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent, default(TagId), Array.Empty<ExecutionId>());
            var killEffect = new GameplayEffectDefinition(new EffectId(970), EffectType.Periodic, Array.Empty<ModifierDefinition>(), 2f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.Damage, default(TagId), killSpec, Array.Empty<ExecutionId>());
            var trigger = new TriggerDefinition(new TriggerId(97), GameplayEventType.KillConfirmed, effect.Id, CatalogRegistries.SkillConsumer, threshold: 1, effectTarget: EffectTargetPolicy.Source);
            store.GameplayTriggersRuntime.RegisterEffect(effect);
            var scheduler = new FrameScheduler(store, new Config.GameConfig());
            scheduler.ConfigureGameplayRuntime(new[] { trigger });
            Assert.True(store.GameplayEffectsRuntime.TryApply(killEffect.Id, killEffect, store.GetEntityHandle(0), store.GetEntityHandle(enemy), out _, ownerPlayerId: 0));
            scheduler.SealGraphComposition();
            scheduler.Tick(1f, 0);
            Assert.False(store.EnemyActive[enemy]);
            Assert.Equal(1, store.GetEffectCount(0));
            Assert.Equal(0, store.GameplayTriggersRuntime.Rejections);
        }

        [Fact]
        public void SchedulerFeedsHealAppliedIntoGameplayTriggerCommit()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 10f, 1f, 1f, 1);
            store.PlayerMaxHealth[0] = 10f;
            store.SetPlayerCurrentHealth(0, 5f);
            int source = store.AddEnemy(0f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            var healSpec = new PeriodicSpec(1f, new ExecutionId(98), EffectPayloadKind.Heal, MagnitudeSource.Constant, FirstTickPolicy.NextInterval, CatchUpPolicy.CatchUpAll, magnitude: 2f);
            var heal = new GameplayEffectDefinition(new EffectId(98), EffectType.Periodic, Array.Empty<ModifierDefinition>(), 2f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.Heal, default(TagId), healSpec, Array.Empty<ExecutionId>());
            var marker = new GameplayEffectDefinition(new EffectId(99), EffectType.Duration, Array.Empty<ModifierDefinition>(), 2f, 0f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent, new TagId(9), Array.Empty<ExecutionId>());
            var trigger = new TriggerDefinition(new TriggerId(99), GameplayEventType.HealApplied, marker.Id, CatalogRegistries.SkillConsumer, effectTarget: EffectTargetPolicy.Source);
            store.GameplayTriggersRuntime.RegisterEffect(marker);
            Assert.True(store.GameplayEffectsRuntime.TryApply(heal.Id, heal, store.GetEntityHandle(source), store.GetEntityHandle(0), out _, ownerPlayerId: 0));
            var scheduler = new FrameScheduler(store, new Config.GameConfig());
            scheduler.ConfigureGameplayRuntime(new[] { trigger });
            scheduler.SealGraphComposition();
            scheduler.Tick(1f, 0);
            Assert.Equal(7f, store.PlayerCurrentHealth[0], 3);
            Assert.Equal(1, store.GetEffectCount(source));
        }

        [Fact]
        public void BuildPhaseRejectsRealTimeEnemyDamageWithoutLeavingDeathQueue()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 10f, 1f, 1f, 1);
            int enemy = store.AddEnemy(0f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            var spec = new PeriodicSpec(1f, new ExecutionId(100), EffectPayloadKind.Damage, MagnitudeSource.Constant, FirstTickPolicy.NextInterval, CatchUpPolicy.CatchUpAll, magnitude: 3f);
            var def = new GameplayEffectDefinition(new EffectId(100), EffectType.Periodic, Array.Empty<ModifierDefinition>(), 2f, ClockId.RealTime, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.Damage, default(TagId), spec, Array.Empty<ExecutionId>());
            Assert.True(store.GameplayEffectsRuntime.TryApply(def.Id, def, store.GetEntityHandle(0), store.GetEntityHandle(enemy), out _, ownerPlayerId: 0));
            var scheduler = new FrameScheduler(store, new Config.GameConfig()) { Phase = GameState.BuildPhase };
            scheduler.SkillBuff.Buff = null;
            scheduler.SealGraphComposition();
            scheduler.Tick(1f, 0);
            Assert.Equal(10f, store.EnemyHealth[enemy], 3);
            Assert.Equal(0, store.DamageResolver.PendingRequestCount);
            Assert.Equal(0, store.DeathEnqueueCount);
            scheduler.Tick(1f, 1);
            Assert.Equal(10f, store.EnemyHealth[enemy], 3);
        }

        [Fact]
        public void BuildPhaseAdvancesBuildClockAndRejectsEnemyDamageInFrame()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 10f, 1f, 1f, 1);
            int enemy = store.AddEnemy(0f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            var spec = new PeriodicSpec(1f, new ExecutionId(107), EffectPayloadKind.Damage, MagnitudeSource.Constant, FirstTickPolicy.NextInterval, CatchUpPolicy.CatchUpAll, magnitude: 2f);
            var def = new GameplayEffectDefinition(new EffectId(107), EffectType.Periodic, Array.Empty<ModifierDefinition>(), 2f, ClockId.Build, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.Damage, default(TagId), spec, Array.Empty<ExecutionId>());
            Assert.True(store.GameplayEffectsRuntime.TryApply(def.Id, def, store.GetEntityHandle(0), store.GetEntityHandle(enemy), out _ , ownerPlayerId: 0));
            var scheduler = new FrameScheduler(store, new Config.GameConfig()) { Phase = GameState.BuildPhase };
            scheduler.SkillBuff.Buff = null;
            scheduler.SealGraphComposition();
            scheduler.Tick(1f, 0);
            Assert.Equal(10f, store.EnemyHealth[enemy], 3);
            Assert.Equal(0, store.DamageResolver.PendingRequestCount);
            Assert.Equal(0, store.DeathEnqueueCount);
        }

        [Fact]
        public void TriggerDefinitionsProduceDistinctSequencesForOneInputEvent()
        {
            var store = new ComponentStore();
            int source = store.AddEnemy(0f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            int target = store.AddEnemy(1f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            var first = new GameplayEffectDefinition(new EffectId(101), EffectType.Instant, Array.Empty<ModifierDefinition>(), 0f, 0f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent, new TagId(1), Array.Empty<ExecutionId>());
            var second = new GameplayEffectDefinition(new EffectId(102), EffectType.Instant, Array.Empty<ModifierDefinition>(), 0f, 0f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent, new TagId(2), Array.Empty<ExecutionId>());
            var runtime = store.GameplayTriggersRuntime;
            runtime.RegisterEffect(first); runtime.RegisterEffect(second);
            var triggers = new[]
            {
                new TriggerDefinition(new TriggerId(101), GameplayEventType.HitConfirmed, first.Id, CatalogRegistries.SkillConsumer),
                new TriggerDefinition(new TriggerId(102), GameplayEventType.HitConfirmed, second.Id, CatalogRegistries.SkillConsumer)
            };
            var events = new GameplayEventQueue(4);
            Assert.True(events.TryPublish(new GameplayEvent(GameplayEventType.HitConfirmed, store.GetEntityHandle(source), store.GetEntityHandle(target), 1L)));
            Assert.Equal(2, runtime.Consume(events, triggers));
            Assert.Equal(2, runtime.NextEvents.Count);
            Assert.Equal(GameplayEventType.EffectApplied, runtime.NextEvents.Get(0).Type);
            Assert.Equal(GameplayEventType.EffectApplied, runtime.NextEvents.Get(1).Type);
            Assert.NotEqual(runtime.NextEvents.Get(0).Sequence, runtime.NextEvents.Get(1).Sequence);
        }

        [Fact]
        public void PerPlayerCounterUsesOwnerAcrossDifferentSources()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 10f, 1f, 1f, 1);
            int sourceA = store.AddEnemy(0f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            int sourceB = store.AddEnemy(1f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            int target = store.AddEnemy(2f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            var trigger = new TriggerDefinition(new TriggerId(106), GameplayEventType.HitConfirmed, new EffectId(999), CatalogRegistries.SkillConsumer, scope: TriggerScope.PerPlayer, threshold: 3, mode: TriggerMode.EveryN, preserveRemainder: true);
            var queue = new GameplayEventQueue(4);
            Assert.True(queue.TryPublish(new GameplayEvent(GameplayEventType.HitConfirmed, store.GetEntityHandle(sourceA), store.GetEntityHandle(target), 1L, ownerPlayerId: 0)));
            Assert.True(queue.TryPublish(new GameplayEvent(GameplayEventType.HitConfirmed, store.GetEntityHandle(sourceB), store.GetEntityHandle(target), 2L, ownerPlayerId: 0)));
            store.GameplayTriggersRuntime.Consume(queue, new[] { trigger });
            Assert.Equal(2, store.GameplayTriggersRuntime.GetCounter(trigger, store.GetEntityHandle(0), default(EntityHandle)));
            store.AddPlayer(1, 10f, 1f, 1f, 1);
            Assert.Equal(0, store.GameplayTriggersRuntime.GetCounter(trigger, store.GetEntityHandle(1), default(EntityHandle)));
        }

        [Fact]
        public void SkipMissedTicksOnceAndClearsAccumulatedDebt()
        {
            var store = new ComponentStore();
            int source = store.AddEnemy(0f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            int target = store.AddEnemy(1f, 0f, 1f, 20f, 20f, 1f, 1, 1);
            var spec = new PeriodicSpec(1f, new ExecutionId(103), EffectPayloadKind.Damage, MagnitudeSource.Constant, FirstTickPolicy.NextInterval, CatchUpPolicy.SkipMissed, magnitude: 2f);
            var def = new GameplayEffectDefinition(new EffectId(103), EffectType.Periodic, Array.Empty<ModifierDefinition>(), 10f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.Damage, default(TagId), spec, Array.Empty<ExecutionId>());
            Assert.Equal(DurationPolicy.Duration, def.DurationPolicy);
            Assert.True(store.GameplayEffectsRuntime.TryApply(def.Id, def, store.GetEntityHandle(source), store.GetEntityHandle(target), out var handle, ownerPlayerId: 0));
            Assert.True(store.GetCachedActiveEnemyIds().Count >= 2);
            Assert.Contains(target, store.GetCachedActiveEnemyIds());
            Assert.True(store.GameplayEffects.TryGet(handle, out var beforeTick, out var beforeDefinition, out _));
            Assert.Equal(DurationPolicy.Duration, beforeDefinition.DurationPolicy);
            Assert.Equal(10, beforeTick.TicksRemaining);
            Assert.Equal(ClockId.Combat, beforeTick.Clock);
            Assert.True(beforeTick.RuntimeOwned);
            store.GameplayEffectsRuntime.Tick(5f, ClockId.Combat);
            Assert.True(store.GameplayEffects.TryGet(handle, out var active, out var activeDefinition, out _));
            Assert.Equal(DurationPolicy.Duration, activeDefinition.DurationPolicy);
            Assert.Equal(1, active.TicksProcessed);
            Assert.Equal(9, active.TicksRemaining);
            Assert.Equal(18f, store.EnemyHealth[target], 3);
        }

        [Fact]
        public void InvalidPeriodicPeriodIsRejectedBeforeAllocatingEffectSlot()
        {
            var store = new ComponentStore();
            int sourceId = store.AddEnemy(0f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            int targetId = store.AddEnemy(1f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            var source = store.GetEntityHandle(sourceId);
            var target = store.GetEntityHandle(targetId);
            var spec = new PeriodicSpec(0f, new ExecutionId(105), EffectPayloadKind.Damage,
                MagnitudeSource.Constant, FirstTickPolicy.NextInterval, CatchUpPolicy.CatchUpAll,
                magnitude: 2f);
            var definition = new GameplayEffectDefinition(new EffectId(105), EffectType.Periodic,
                Array.Empty<ModifierDefinition>(), 5f, ClockId.Combat, StackingBehavior.None, 1,
                RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.Damage,
                default(TagId), spec, Array.Empty<ExecutionId>());

            bool applied = store.GameplayEffectsRuntime.TryApply(definition.Id, definition,
                source, target, out var handle);

            Assert.False(applied);
            Assert.False(handle.IsValid);
            Assert.Equal(0, store.GetEffectCount(targetId));
            Assert.Equal(1, store.GameplayEffectsRuntime.Rejections);
            Assert.Contains(GameplayEventType.EffectRejected, Events(store.GameplayEffectsRuntime.Events));
        }

        [Fact]
        public void EveryNWithoutRemainderDiscardsCrossingDebt()
        {
            var store = new ComponentStore();
            int sourceId = store.AddEnemy(0f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            int targetId = store.AddEnemy(1f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            var source = store.GetEntityHandle(sourceId);
            var target = store.GetEntityHandle(targetId);
            var effect = new GameplayEffectDefinition(new EffectId(106), EffectType.Instant,
                Array.Empty<ModifierDefinition>(), 0f, 0f, ClockId.Combat, StackingBehavior.None, 1,
                RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent,
                default(TagId), Array.Empty<ExecutionId>());
            var trigger = new TriggerDefinition(new TriggerId(106), GameplayEventType.HitConfirmed,
                effect.Id, CatalogRegistries.SkillConsumer, scope: TriggerScope.PerSource,
                threshold: 2, mode: TriggerMode.EveryN, preserveRemainder: false);
            Assert.True(store.GameplayTriggersRuntime.RegisterEffect(effect));
            var queue = new GameplayEventQueue(4);
            Assert.True(queue.TryPublish(new GameplayEvent(GameplayEventType.HitConfirmed, source, target, 1L)));
            Assert.True(queue.TryPublish(new GameplayEvent(GameplayEventType.HitConfirmed, source, target, 2L)));

            int fired = store.GameplayTriggersRuntime.Consume(queue, new[] { trigger });

            Assert.Equal(1, fired);
            Assert.Equal(0, store.GameplayTriggersRuntime.GetCounter(trigger, source, target));
        }

        [Fact]
        public void InvalidPeriodicDurationAndMagnitudeAreRejectedBeforeAllocation()
        {
            var store = new ComponentStore();
            int sourceId = store.AddEnemy(0f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            int targetId = store.AddEnemy(1f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            var source = store.GetEntityHandle(sourceId);
            var target = store.GetEntityHandle(targetId);
            var badMagnitude = new PeriodicSpec(1f, new ExecutionId(107), EffectPayloadKind.Damage,
                MagnitudeSource.Constant, FirstTickPolicy.NextInterval, CatchUpPolicy.CatchUpAll,
                magnitude: float.NaN);
            var badDuration = new GameplayEffectDefinition(new EffectId(107), EffectType.Periodic,
                Array.Empty<ModifierDefinition>(), -1f, ClockId.Combat, StackingBehavior.None, 1,
                RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.Damage,
                default(TagId), badMagnitude, Array.Empty<ExecutionId>());

            Assert.False(store.GameplayEffectsRuntime.TryApply(badDuration.Id, badDuration,
                source, target, out var durationHandle));
            Assert.False(durationHandle.IsValid);
            Assert.Equal(0, store.GetEffectCount(targetId));

            var badMagnitudeDefinition = new GameplayEffectDefinition(new EffectId(108), EffectType.Periodic,
                Array.Empty<ModifierDefinition>(), 2f, ClockId.Combat, StackingBehavior.None, 1,
                RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.Damage,
                default(TagId), badMagnitude, Array.Empty<ExecutionId>());
            Assert.False(store.GameplayEffectsRuntime.TryApply(badMagnitudeDefinition.Id,
                badMagnitudeDefinition, source, target, out var magnitudeHandle));
            Assert.False(magnitudeHandle.IsValid);
            Assert.Equal(0, store.GetEffectCount(targetId));
            Assert.True(store.GameplayEffectsRuntime.Rejections >= 2);
        }

        [Fact]
        // Bug 回归：Persist 效果在 source 销毁后仍结算，外部 stale request 仍必须拒绝。
        public void RuntimeOwnedPersistEffectTicksAfterSourceDeath()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 10f, 1f, 1f, 1);
            store.PlayerMaxMana[0] = 100f;
            int sourceId = store.AddEnemy(0f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            int damageTargetId = store.AddEnemy(1f, 0f, 1f, 20f, 20f, 1f, 1, 1);
            var source = store.GetEntityHandle(sourceId);
            var damageTarget = store.GetEntityHandle(damageTargetId);
            var damageSpec = new PeriodicSpec(1f, new ExecutionId(120), EffectPayloadKind.Damage,
                MagnitudeSource.Constant, FirstTickPolicy.NextInterval, CatchUpPolicy.CatchUpAll,
                magnitude: 3f);
            var damage = new GameplayEffectDefinition(new EffectId(120), EffectType.Periodic,
                Array.Empty<ModifierDefinition>(), 3f, ClockId.Combat, StackingBehavior.None, 1,
                RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.Damage,
                default(TagId), damageSpec, Array.Empty<ExecutionId>());
            var resourceSpec = new PeriodicSpec(1f, new ExecutionId(121), EffectPayloadKind.Resource,
                MagnitudeSource.Constant, FirstTickPolicy.NextInterval, CatchUpPolicy.CatchUpAll,
                magnitude: 5f, resource: new AttributeKey(7));
            var resource = new GameplayEffectDefinition(new EffectId(121), EffectType.Periodic,
                Array.Empty<ModifierDefinition>(), 3f, ClockId.Combat, StackingBehavior.None, 1,
                RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.Resource,
                default(TagId), resourceSpec, Array.Empty<ExecutionId>());
            Assert.True(store.GameplayEffectsRuntime.TryApply(damage.Id, damage, source,
                damageTarget, out _, ownerPlayerId: 0, provenanceId: 9001L));
            Assert.True(store.GameplayEffectsRuntime.TryApply(resource.Id, resource, source,
                store.GetEntityHandle(0), out _, ownerPlayerId: 0, provenanceId: 9002L));

            store.DestroyEntity(sourceId);
            float healthBeforeTick = store.EnemyHealth[damageTargetId];
            float manaBeforeTick = store.PlayerMana[0];
            store.GameplayEffectsRuntime.Tick(1f, ClockId.Combat);

            Assert.Equal(healthBeforeTick - 3f, store.EnemyHealth[damageTargetId], 3);
            Assert.Equal(manaBeforeTick + 5f, store.PlayerMana[0], 3);
            var hit = store.DamageResolver.Events.Get(0);
            Assert.Equal(source, hit.Source);
            Assert.Equal(9001L, hit.ProvenanceId);
            Assert.Equal(0, hit.OwnerPlayerId);
            var resourceEvent = store.ResourceResolver.Events.Get(0);
            Assert.Equal(source, resourceEvent.Source);
            Assert.Equal(0, resourceEvent.OwnerPlayerId);
            Assert.Equal(9002L, resourceEvent.ProvenanceId);

            float healthAfterPersistTick = store.EnemyHealth[damageTargetId];
            var staleRequest = new DamageRequest(source, damageTarget, 1f, DamageType.True,
                9003L, ownerPlayerId: 0);
            Assert.False(store.DamageResolver.TryApply(staleRequest).Accepted);
            Assert.Equal(healthAfterPersistTick, store.EnemyHealth[damageTargetId], 3);
        }

        [Fact]
        public void ExplicitTriggerCounterResetClearsOnlyWhenRequested()
        {
            var store = new ComponentStore();
            int source = store.AddEnemy(0f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            int target = store.AddEnemy(1f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            var trigger = new TriggerDefinition(new TriggerId(104), GameplayEventType.HitConfirmed, new EffectId(999), CatalogRegistries.SkillConsumer, threshold: 2, resetPolicy: TriggerResetPolicy.Explicit);
            var queue = new GameplayEventQueue(1);
            Assert.True(queue.TryPublish(new GameplayEvent(GameplayEventType.HitConfirmed, store.GetEntityHandle(source), store.GetEntityHandle(target), 1L)));
            store.GameplayTriggersRuntime.Consume(queue, new[] { trigger });
            Assert.Equal(1, store.GameplayTriggersRuntime.GetCounter(trigger, store.GetEntityHandle(source), store.GetEntityHandle(target)));
            store.GameplayTriggersRuntime.ResetFrame();
            Assert.Equal(1, store.GameplayTriggersRuntime.GetCounter(trigger, store.GetEntityHandle(source), store.GetEntityHandle(target)));
            store.GameplayTriggersRuntime.ResetCounters();
            Assert.Equal(0, store.GameplayTriggersRuntime.GetCounter(trigger, store.GetEntityHandle(source), store.GetEntityHandle(target)));
        }

        [Fact]
        // Bug 回归：空 trigger 配置不能消费真实 Resolver fact。
        public void EmptyTriggerConfigurationSkipsGameplayEventConsumption()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 10f, 1f, 1f, 1);
            int enemy = store.AddEnemy(0f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            var scheduler = new FrameScheduler(store, new Config.GameConfig());
            scheduler.ConfigureGameplayRuntime(Array.Empty<TriggerDefinition>());
            scheduler.SealGraphComposition();
            scheduler.Tick(0f, 0);
            Assert.True(store.DamageResolver.TryApply(new DamageRequest(store.GetEntityHandle(0),
                store.GetEntityHandle(enemy), 1f, DamageType.True, 1L, ownerPlayerId: 0)).Accepted);
            Assert.True(store.DamageResolver.Events.Count > 0);
            scheduler.Tick(0f, 1);
            Assert.Equal(0, store.GameplayTriggersRuntime.SeenCount);
            Assert.Equal(0, store.GameplayTriggersRuntime.NextEvents.Count);
        }

        [Fact]
        public void TriggerFrameBudgetIsSharedAcrossConsumesAndResetsNextFrame()
        {
            var store = new ComponentStore();
            int sourceId = store.AddEnemy(0f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            int targetId = store.AddEnemy(1f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            var runtime = new GameplayTriggerRuntime(store, store.GameplayEffectsRuntime, 8, 1);
            var queue = new GameplayEventQueue(2);
            var source = store.GetEntityHandle(sourceId);
            var target = store.GetEntityHandle(targetId);
            Assert.True(queue.TryPublish(new GameplayEvent(GameplayEventType.HitConfirmed, source, target, 1L)));
            runtime.Consume(queue, Array.Empty<TriggerDefinition>());
            var secondQueue = new GameplayEventQueue(1);
            Assert.True(secondQueue.TryPublish(new GameplayEvent(GameplayEventType.HitConfirmed, source, target, 2L)));
            runtime.Consume(secondQueue, Array.Empty<TriggerDefinition>());

            Assert.Equal(1, runtime.LoopAborts);
            runtime.ResetFrame();
            var recovered = new GameplayEventQueue(1);
            Assert.True(recovered.TryPublish(new GameplayEvent(GameplayEventType.HitConfirmed, source, target, 3L)));
            runtime.Consume(recovered, Array.Empty<TriggerDefinition>());
            Assert.Equal(1, runtime.LoopAborts);
        }

        [Fact]
        public void InvalidEffectDefinitionIsRejectedAtRegistration()
        {
            var store = new ComponentStore();
            var invalidSpec = new PeriodicSpec(0f, new ExecutionId(109), EffectPayloadKind.Damage,
                MagnitudeSource.Constant, FirstTickPolicy.NextInterval, CatchUpPolicy.CatchUpAll,
                magnitude: 2f);
            var definition = new GameplayEffectDefinition(new EffectId(109), EffectType.Periodic,
                Array.Empty<ModifierDefinition>(), 2f, ClockId.Combat, StackingBehavior.None, 1,
                RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.Damage,
                default(TagId), invalidSpec, Array.Empty<ExecutionId>());

            Assert.False(store.GameplayTriggersRuntime.RegisterEffect(definition));
            Assert.Equal(1, store.GameplayTriggersRuntime.Rejections);
            Assert.True(store.GameplayTriggersRuntime.NextEvents.Count > 0);
            Assert.Equal(GameplayEventType.EffectRejected,
                store.GameplayTriggersRuntime.NextEvents.Get(0).Type);
        }

        [Fact]
        public void SourceDeathTransfer_RebindsSourceToOwnerAndKeepsTicking()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 10f, 1f, 1f, 1);
            int source = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            int target = store.AddEnemy(1, 0, 1f, 20f, 20f, 1f, 1, 1);
            var def = new GameplayEffectDefinition(new EffectId(210), EffectType.Periodic, Array.Empty<ModifierDefinition>(),
                4f, 1f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Transfer,
                EffectPayloadKind.Damage, default(TagId), Array.Empty<ExecutionId>(), periodicMagnitude: 2f);
            Assert.True(store.GameplayEffectsRuntime.TryApply(def.Id, def, store.GetEntityHandle(source),
                store.GetEntityHandle(target), out _, ownerPlayerId: 0));
            store.DestroyEntity(source);
            store.GameplayEffectsRuntime.Tick(1f, ClockId.Combat);
            Assert.Equal(1, store.GetEffectCount(target));
            Assert.True(store.TryGetActiveEffectAt(target, 0, out var active, out _, out _));
            Assert.Equal(0, active.Source.Index);
            Assert.Equal(18f, store.EnemyHealth[target], 3);
        }

        [Fact]
        public void EffectBlockedTags_AreReadAtApplyAndReject()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 10f, 1f, 1f, 1);
            int target = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            var grant = new GameplayEffectDefinition(new EffectId(211), EffectType.Duration,
                Array.Empty<ModifierDefinition>(), 5f, 0f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None,
                SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent, new TagId(3), Array.Empty<ExecutionId>(),
                grantedTags: new[] { new TagId(3) });
            Assert.True(store.GameplayEffectsRuntime.TryApply(grant.Id, grant, store.GetEntityHandle(0),
                store.GetEntityHandle(target), out _));
            Assert.True(GameplayTagRuntime.HasTag(store, target, new TagId(3)));
            var blocked = new GameplayEffectDefinition(new EffectId(212), EffectType.Duration,
                Array.Empty<ModifierDefinition>(), 5f, 0f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None,
                SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent, default(TagId), Array.Empty<ExecutionId>(),
                blockedTags: new[] { new TagId(3) });
            Assert.False(store.GameplayEffectsRuntime.TryApply(blocked.Id, blocked, store.GetEntityHandle(0),
                store.GetEntityHandle(target), out _));
        }

        [Fact]
        public void TryAdopt_RejectsBlockedTagsAndNonPositivePeriodicMagnitude()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 10f, 1f, 1f, 1);
            int target = store.AddEnemy(0, 0, 1f, 20f, 20f, 1f, 1, 1);
            var grant = new GameplayEffectDefinition(new EffectId(214), EffectType.Duration,
                Array.Empty<ModifierDefinition>(), 5f, 0f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None,
                SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent, new TagId(4), Array.Empty<ExecutionId>(),
                grantedTags: new[] { new TagId(4) });
            Assert.True(store.GameplayEffectsRuntime.TryApply(grant.Id, grant, store.GetEntityHandle(0),
                store.GetEntityHandle(target), out _));

            var blocked = new GameplayEffectDefinition(new EffectId(215), EffectType.Periodic,
                Array.Empty<ModifierDefinition>(), 2f, 1f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None,
                SourceDeathPolicy.Persist, EffectPayloadKind.Damage, default(TagId), Array.Empty<ExecutionId>(),
                blockedTags: new[] { new TagId(4) }, periodicMagnitude: 1f);
            Assert.False(store.GameplayEffectsRuntime.TryAdopt(PeriodicApp(blocked, store, 0, target), 0, out _));
            Assert.Equal(8, LastRejectReason(store));
            Assert.Equal(1, store.GetEffectCount(target));

            var zero = new GameplayEffectDefinition(new EffectId(216), EffectType.Periodic,
                Array.Empty<ModifierDefinition>(), 2f, 1f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None,
                SourceDeathPolicy.Persist, EffectPayloadKind.Damage, default(TagId), Array.Empty<ExecutionId>(),
                periodicMagnitude: 0f);
            Assert.False(store.GameplayEffectsRuntime.TryAdopt(PeriodicApp(zero, store, 0, target), 0, out _));
            Assert.Equal(4, LastRejectReason(store));
        }

        [Fact]
        public void HasTag_UsesContributionCountsWithoutSlotScan()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 10f, 1f, 1f, 1);
            int target = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            var grant = new GameplayEffectDefinition(new EffectId(217), EffectType.Duration,
                Array.Empty<ModifierDefinition>(), 5f, 0f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None,
                SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent, default(TagId), Array.Empty<ExecutionId>(),
                grantedTags: new[] { new TagId(7) });
            Assert.True(store.GameplayEffectsRuntime.TryApply(grant.Id, grant, store.GetEntityHandle(0),
                store.GetEntityHandle(target), out _));
            Assert.True(GameplayTagRuntime.HasTag(store, target, new TagId(7)));
            store.TagState.ClearEntity(target);
            Assert.False(GameplayTagRuntime.HasTag(store, target, new TagId(7)));
            Assert.Equal(1, store.GetEffectCount(target));
        }

        [Fact]
        public void TryRestack_SameStackKeyDifferentName_DoesNotMerge()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 10f, 1f, 1f, 1);
            int target = store.AddEnemy(0, 0, 1f, 20f, 20f, 1f, 1, 1);
            var definition = new GameplayEffectDefinition(new EffectId(218), EffectType.Periodic,
                Array.Empty<ModifierDefinition>(), 4f, 1f, ClockId.Combat, StackingBehavior.MaxStacks, 3,
                RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.Damage, default(TagId),
                Array.Empty<ExecutionId>(), stackKey: new TagId(218), periodicMagnitude: 1f);
            Assert.True(store.GameplayEffectsRuntime.TryAdopt(PeriodicApp(definition, store, 0, target, "BurnA"), 0, out _));
            Assert.Equal(1, store.GetEffectCount(target));
            Assert.True(store.TryGetActiveEffectAt(target, 0, out var first, out _, out _));
            Assert.Equal(1, first.StackCount);
            Assert.True(store.GameplayEffectsRuntime.TryRestack(PeriodicApp(definition, store, 0, target, "BurnB"), 0, out _));
            Assert.Equal(2, store.GetEffectCount(target));
            Assert.True(store.TryGetActiveEffectAt(target, 0, out first, out _, out _));
            Assert.Equal(1, first.StackCount);
            Assert.True(store.GameplayEffectsRuntime.TryRestack(PeriodicApp(definition, store, 0, target, "BurnA"), 0, out _));
            Assert.Equal(2, store.GetEffectCount(target));
            Assert.True(store.TryGetActiveEffectAt(target, 0, out first, out _, out _));
            Assert.Equal(2, first.StackCount);
        }

        [Fact]
        public void PeriodicAttributeMagnitude_UsesSourceAttackProjection()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 10f, 1f, 5f, 1);
            int target = store.AddEnemy(0, 0, 1f, 50f, 50f, 1f, 1, 1);
            var spec = new PeriodicSpec(1f, new ExecutionId(213), EffectPayloadKind.Damage,
                MagnitudeSource.Attribute, FirstTickPolicy.NextInterval, CatchUpPolicy.CatchUpAll,
                magnitude: 1f, resource: CatalogRegistries.AttackDamage);
            var def = new GameplayEffectDefinition(new EffectId(213), EffectType.Periodic, Array.Empty<ModifierDefinition>(),
                2f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None, SourceDeathPolicy.Persist,
                EffectPayloadKind.Damage, default(TagId), spec, Array.Empty<ExecutionId>());
            Assert.True(store.GameplayEffectsRuntime.TryApply(def.Id, def, store.GetEntityHandle(0),
                store.GetEntityHandle(target), out _, ownerPlayerId: 0));
            float expected = store.GetPlayerAttackDamageProjection(0);
            Assert.True(expected > 0f);
            store.GameplayEffectsRuntime.Tick(1f, ClockId.Combat);
            Assert.Equal(50f - expected, store.EnemyHealth[target], 3);
        }

        private static HashSet<GameplayEventType> Events(GameplayEventQueue queue)
        {
            var result = new HashSet<GameplayEventType>(); for (int i = 0; i < queue.Count; i++) result.Add(queue.Get(i).Type); return result;
        }

        private static int LastRejectReason(ComponentStore store)
        {
            var queue = store.GameplayEffectsRuntime.Events;
            for (int i = queue.Count - 1; i >= 0; i--)
                if (queue.Get(i).Type == GameplayEventType.EffectRejected) return queue.Get(i).Reason;
            return -1;
        }

        private static GameplayEffectApplication PeriodicApp(GameplayEffectDefinition definition, ComponentStore store,
            int sourceId, int targetId, string name = "")
        {
            var runtime = new ActiveGameplayEffect(default(EffectHandle), definition.Id,
                store.GetEntityHandle(sourceId), store.GetEntityHandle(targetId), definition.Duration,
                2, definition.Periodic.HasValue ? definition.Periodic.Value.Magnitude : 0f, ClockId.Combat,
                FirstTickPolicy.NextInterval, CatchUpPolicy.CatchUpAll, SourceDeathPolicy.Persist);
            return new GameplayEffectApplication(definition,
                new LegacyEffectSnapshot(name, -1, AttributeModifierOp.Add, 1f), runtime);
        }
    }
}
