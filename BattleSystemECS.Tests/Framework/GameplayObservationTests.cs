using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class GameplayObservationTests
    {
        [Fact]
        public void CaptureReportsPeaksRejectionReasonsAndStaleHandlesWithoutMutation()
        {
            using var store = new ComponentStore();
            store.AddPlayer(0, 10f, 1f, 10f, 1);
            int target = store.AddEnemy(0f, 0f, 0f, 100f, 100f, 0f, 0, 1);
            EntityHandle source = store.GetEntityHandle(0);
            EntityHandle targetHandle = store.GetEntityHandle(target);

            store.DamageResolver.EnableDeferred(true);
            for (int i = 0; i < 3; i++)
            {
                var request = new DamageRequest(source, targetHandle, 1f, DamageType.True,
                    i + 1L, ownerPlayerId: 0);
                Assert.True(store.DamageResolver.TryApply(request).Deferred);
            }
            var invalidAmount = new DamageRequest(source, targetHandle, float.NaN,
                DamageType.True, 10L, ownerPlayerId: 0);
            Assert.Equal(DamageRejectionReason.NonFiniteAmount,
                store.DamageResolver.TryApply(invalidAmount).Reason);

            int recycled = store.AddEnemy(1f, 0f, 0f, 10f, 10f, 0f, 0, 1);
            EntityHandle staleTarget = store.GetEntityHandle(recycled);
            store.DestroyEntity(recycled);
            Assert.Equal(recycled, store.AddEnemy(1f, 0f, 0f, 10f, 10f, 0f, 0, 1));
            var staleRequest = new DamageRequest(source, staleTarget, 1f, DamageType.True,
                11L, ownerPlayerId: 0);
            Assert.Equal(DamageRejectionReason.InvalidTarget,
                store.DamageResolver.TryApply(staleRequest).Reason);

            store.ResourceResolver.EnableDeferred(true);
            var validResource = new ResourceRequest(source, source, new AttributeKey(7), 1f,
                20L, ownerPlayerId: 0);
            Assert.True(store.ResourceResolver.TryApply(validResource).Deferred);
            var unknownResource = new ResourceRequest(source, source, new AttributeKey(999), 1f,
                21L, ownerPlayerId: 0);
            Assert.Equal(ResourceRejectionReason.UnknownResource,
                store.ResourceResolver.TryApply(unknownResource).Reason);
            var staleResource = new ResourceRequest(source, staleTarget, new AttributeKey(7), 1f,
                22L, ownerPlayerId: 0);
            Assert.Equal(ResourceRejectionReason.InvalidTarget,
                store.ResourceResolver.TryApply(staleResource).Reason);

            Assert.True(store.GameplayEffectPool.TryAllocate(out EffectHandle first));
            Assert.True(store.GameplayEffectPool.TryAllocate(out EffectHandle second));
            Assert.True(store.GameplayEffectPool.Release(first));
            Assert.True(store.GameplayEffectPool.TryAllocate(out EffectHandle replacement));
            Assert.False(store.GameplayEffectPool.TryResolve(first, out _));

            GameplayObservationSnapshot before = GameplayObservation.Capture(store);
            GameplayObservationSnapshot after = GameplayObservation.Capture(store);

            Assert.Equal(1, before.SchemaVersion);
            Assert.Equal(3, before.DamagePending);
            Assert.Equal(3, before.DamagePendingPeak);
            Assert.Equal(2, before.DamageRejected);
            Assert.Equal(1, before.DamageStaleHandleRejections);
            Assert.Equal(1,
                before.DamageRejectionsByReason[(int)DamageRejectionReason.NonFiniteAmount]);
            Assert.Equal(1,
                before.DamageRejectionsByReason[(int)DamageRejectionReason.InvalidTarget]);
            Assert.Equal(1, before.ResourcePending);
            Assert.Equal(1, before.ResourcePendingPeak);
            Assert.Equal(2, before.ResourceRejected);
            Assert.Equal(1, before.ResourceStaleHandleRejections);
            Assert.Equal(1,
                before.ResourceRejectionsByReason[(int)ResourceRejectionReason.UnknownResource]);
            Assert.Equal(1,
                before.ResourceRejectionsByReason[(int)ResourceRejectionReason.InvalidTarget]);
            Assert.Equal(2, before.EffectPoolActive);
            Assert.Equal(2, before.EffectPoolPeakActive);
            Assert.Equal(1, before.EffectPoolStaleResolves);
            Assert.Equal(0, before.DamageEventPublicationFailures);
            Assert.Equal(0, before.ResourceEventPublicationFailures);
            Assert.Equal(before.StateDigest, after.StateDigest);
            Assert.Equal(before.GameplayEventSequenceDigest, after.GameplayEventSequenceDigest);
            Assert.Equal(before.GameplayEventPublishedCount, after.GameplayEventPublishedCount);
            Assert.Equal(before.DamagePending, after.DamagePending);
            Assert.Equal(before.EffectPoolActive, after.EffectPoolActive);
            Assert.True(store.GameplayEffectPool.Release(second));
            Assert.True(store.GameplayEffectPool.Release(replacement));
        }

        [Fact]
        public void CaptureUsesConsumedTriggerDefinitionsAndPublicationFailures()
        {
            using var store = new ComponentStore();
            store.AddPlayer(0, 20f, 1f, 0f, 1);
            int enemy = store.AddEnemy(0f, 0f, 0f, 10f, 10f, 0f, 0, 1);
            EntityHandle source = store.GetEntityHandle(0);
            EntityHandle target = store.GetEntityHandle(enemy);
            var trigger = new TriggerDefinition(new TriggerId(9900), GameplayEventType.HitConfirmed,
                new EffectId(9900), CatalogRegistries.SkillConsumer);
            var empty = new GameplayEventQueue(1);
            Assert.Equal(0, store.GameplayTriggersRuntime.Consume(empty, new[] { trigger }));

            for (int i = 0; i < store.DamageResolver.Events.Capacity; i++)
                Assert.True(store.DamageResolver.Events.TryPublish(new GameplayEvent(
                    GameplayEventType.HitConfirmed, source, target, i + 1L), true));
            DamageApplyResult damageResult = store.DamageResolver.TryApply(new DamageRequest(source, target, 1f,
                DamageType.True, 100L, ownerPlayerId: 0));
            Assert.False(damageResult.Accepted);
            Assert.Equal(DamageRejectionReason.RequestQueueOverflow, damageResult.Reason);

            for (int i = 0; i < store.ResourceResolver.Events.Capacity; i++)
                Assert.True(store.ResourceResolver.Events.TryPublish(new GameplayEvent(
                    GameplayEventType.ResourceChanged, source, source, i + 1L), true));
            ResourceApplyResult resourceResult = store.ResourceResolver.TryApply(new ResourceRequest(source, source,
                new AttributeKey(7), 1f, 200L, ownerPlayerId: 0));
            Assert.False(resourceResult.Accepted);
            Assert.Equal(ResourceRejectionReason.RequestQueueOverflow, resourceResult.Reason);

            GameplayObservationSnapshot observation = GameplayObservation.Capture(store);
            Assert.Equal(1, observation.TriggerDefinitions);
            Assert.Equal(1, observation.TriggerDefinitionPeak);
            Assert.Equal(0, observation.DamageEventPublicationFailures);
            Assert.Equal(0, observation.ResourceEventPublicationFailures);
            Assert.True(observation.DamageRejectionsByReason[(int)DamageRejectionReason.RequestQueueOverflow] > 0);
            Assert.True(observation.ResourceRejectionsByReason[(int)ResourceRejectionReason.RequestQueueOverflow] > 0);
        }
    }
}
