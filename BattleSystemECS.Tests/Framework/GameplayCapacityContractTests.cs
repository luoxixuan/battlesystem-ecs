using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class GameplayCapacityContractTests
    {
        [Fact]
        public void EventQueueReportsCriticalOverflowWithoutDroppingAcceptedEvent()
        {
            var queue = new GameplayEventQueue(1, 0);
            var acceptedSource = new EntityHandle(11, 2);
            var acceptedTarget = new EntityHandle(12, 3);
            var acceptedEffect = new EffectHandle(13, 4);
            var acceptedDefinition = new EffectId(14);
            var acceptedFlags = DamageFlags.IgnoreArmor | DamageFlags.Reflect;
            var acceptedTag = new TagId(15);
            var accepted = new GameplayEvent(GameplayEventType.HitConfirmed,
                acceptedSource, acceptedTarget, acceptedEffect, acceptedDefinition,
                101L, 91L, 7, 8, acceptedFlags, 81L, 2, acceptedTag, 1);
            var rejected = new GameplayEvent(GameplayEventType.DamageApplied,
                new EntityHandle(21, 4), new EntityHandle(22, 5), 202L, reason: 9);
            int overflowBefore = queue.OverflowCount;

            Assert.True(queue.TryPublish(accepted));
            Assert.False(queue.TryPublish(rejected, true));
            Assert.Equal(1, queue.Count);
            Assert.Equal(overflowBefore + 1, queue.OverflowCount);
            Assert.Equal(CommandRejection.CriticalCapacity, queue.LastRejection);
            GameplayEvent retained = queue.Get(0);
            Assert.Equal(accepted.Type, retained.Type);
            Assert.Equal(accepted.Source, retained.Source);
            Assert.Equal(accepted.Target, retained.Target);
            Assert.Equal(accepted.Effect, retained.Effect);
            Assert.Equal(accepted.EffectDefinition, retained.EffectDefinition);
            Assert.Equal(accepted.Flags, retained.Flags);
            Assert.Equal(accepted.Sequence, retained.Sequence);
            Assert.Equal(accepted.ParentSequence, retained.ParentSequence);
            Assert.Equal(accepted.Reason, retained.Reason);
            Assert.Equal(accepted.ProducerIndex, retained.ProducerIndex);
            Assert.Equal(accepted.ProvenanceId, retained.ProvenanceId);
            Assert.Equal(accepted.ProvenanceDepth, retained.ProvenanceDepth);
            Assert.Equal(accepted.Tag, retained.Tag);
            Assert.Equal(accepted.OwnerPlayerId, retained.OwnerPlayerId);
        }

        [Fact]
        public void CommandBufferReportsCapacityOverflowDeterministically()
        {
            using var store = new ComponentStore();
            int entity = store.AddEnemy(0f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            var handle = store.GetEntityHandle(entity);
            var buffer = new CommandBuffer<EffectRequest>(1);
            var contextTarget = new EntityHandle(handle.Index + 1, handle.Generation + 2);
            var acceptedContext = new BattleSystemECS.Core.GAS.ExecutionContext(handle, contextTarget,
                new AbilityId(3), new EffectId(4), ClockId.Combat, 101L,
                ownerPlayerId: 1, snapshot: 12.5f, provenanceId: 91L, provenanceDepth: 2);
            var activeEffect = new EffectHandle(17, 5);
            var accepted = new EffectRequest(handle, contextTarget, new EffectId(4), activeEffect,
                2, ClockId.Combat, acceptedContext);
            var rejectedHandle = new EntityHandle(handle.Index, handle.Generation + 1);
            var rejectedContext = new BattleSystemECS.Core.GAS.ExecutionContext(rejectedHandle, rejectedHandle,
                new AbilityId(4), new EffectId(2), ClockId.Enemy, 202L);
            var rejected = new EffectRequest(rejectedHandle, rejectedHandle, new EffectId(2), 3, ClockId.Enemy, rejectedContext);
            int overflowBefore = buffer.OverflowCount;

            Assert.True(buffer.TryAdd(accepted));
            Assert.False(buffer.TryAdd(rejected, true));
            Assert.Equal(1, buffer.Count);
            Assert.Equal(overflowBefore + 1, buffer.OverflowCount);
            Assert.Equal(CommandRejection.CriticalCapacity, buffer.LastRejection);
            EffectRequest retained = buffer.Get(0);
            Assert.Equal(accepted.Source, retained.Source);
            Assert.Equal(accepted.Target, retained.Target);
            Assert.Equal(accepted.Effect, retained.Effect);
            Assert.Equal(accepted.ActiveEffect, retained.ActiveEffect);
            Assert.Equal(accepted.StackDelta, retained.StackDelta);
            Assert.Equal(accepted.Clock, retained.Clock);
            Assert.Equal(accepted.Context.Source, retained.Context.Source);
            Assert.Equal(accepted.Context.Target, retained.Context.Target);
            Assert.Equal(accepted.Context.Ability, retained.Context.Ability);
            Assert.Equal(accepted.Context.Effect, retained.Context.Effect);
            Assert.Equal(accepted.Context.Clock, retained.Context.Clock);
            Assert.Equal(accepted.Context.Sequence, retained.Context.Sequence);
            Assert.Equal(accepted.Context.OwnerPlayerId, retained.Context.OwnerPlayerId);
            Assert.Equal(accepted.Context.Snapshot, retained.Context.Snapshot);
            Assert.Equal(accepted.Context.ProvenanceId, retained.Context.ProvenanceId);
            Assert.Equal(accepted.Context.ProvenanceDepth, retained.Context.ProvenanceDepth);
        }

        [Fact]
        public void TriggerFrameBudgetPublishesExactAbortAndRecoversOnNextFrame()
        {
            using var store = new ComponentStore();
            var runtime = new GameplayTriggerRuntime(store, store.GameplayEffectsRuntime, 4, 1);
            var events = new GameplayEventQueue(2);
            var first = new GameplayEvent(GameplayEventType.HitConfirmed,
                default(EntityHandle), default(EntityHandle), 101L);
            var overflow = new GameplayEvent(GameplayEventType.DamageApplied,
                default(EntityHandle), default(EntityHandle), 202L);
            Assert.True(events.TryPublish(first));
            Assert.True(events.TryPublish(overflow));

            int abortsBefore = runtime.LoopAborts;
            runtime.Consume(events, Array.Empty<TriggerDefinition>());

            Assert.Equal(abortsBefore + 1, runtime.LoopAborts);
            Assert.Equal(202L, runtime.LastAbortSequence);
            Assert.Equal(1, runtime.LastAbortReason);
            Assert.Equal(1, runtime.LastAbortRemaining);
            Assert.Equal(1, runtime.AbortEvents.Count);
            GameplayEvent abort = runtime.AbortEvents.Get(0);
            Assert.Equal(GameplayEventType.GameplayLoopAborted, abort.Type);
            Assert.Equal(202L, abort.Sequence);
            Assert.Equal(1, abort.Reason);

            runtime.ResetFrame();
            var recovered = new GameplayEventQueue(1);
            Assert.True(recovered.TryPublish(new GameplayEvent(GameplayEventType.HitConfirmed,
                default(EntityHandle), default(EntityHandle), 303L)));
            Assert.Equal(0, runtime.Consume(recovered, Array.Empty<TriggerDefinition>()));
            Assert.Equal(abortsBefore + 1, runtime.LoopAborts);
            Assert.Equal(0, runtime.LastAbortReason);
            Assert.Equal(0, runtime.LastAbortRemaining);
            Assert.Equal(0, runtime.AbortEvents.Count);
        }
    }
}
