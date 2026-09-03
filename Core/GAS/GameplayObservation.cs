using System;
using BattleSystemECS.Components;

namespace BattleSystemECS.Core.GAS
{
    internal sealed class GameplayObservationSnapshot
    {
        public int SchemaVersion { get; }
        public int Frame { get; }
        public int ActiveEnemies { get; }
        public long DeathsEnqueued { get; }
        public long DeathsResolved { get; }
        public int AbilityPoolRejections { get; }
        public int EffectSlotRejections { get; }

        public int EffectPoolCapacity { get; }
        public int EffectPoolActive { get; }
        public int EffectPoolPeakActive { get; }
        public int EffectHandleAllocatedPages { get; }
        public int EffectHandleAllocatedSlots { get; }
        public int EffectRuntimeAllocatedPages { get; }
        public int EffectRuntimeAllocatedSlots { get; }
        public int EffectPoolAllocationFailures { get; }
        public int EffectPoolInvalidResolves { get; }
        public int EffectPoolStaleResolves { get; }
        public int EffectPoolInactiveResolves { get; }

        public int ActiveRuntimeEffects { get; }
        public int PeakActiveRuntimeEffects { get; }
        public int EffectRuntimeRejections { get; }
        public int EffectRuntimeStateUpdateFailures { get; }
        public int EffectRuntimeEventPeak { get; }
        public int EffectRuntimeEventOverflows { get; }
        public int EffectRuntimePublicationFailures { get; }
        public int EffectRuntimeAbortPublicationFailures { get; }
        public int DamageEventPublicationFailures { get; }

        public long DamageAccepted { get; }
        public long DamageRejected { get; }
        public long DamageLegacyApplied { get; }
        public long DamageStaleHandleRejections { get; }
        public int DamagePending { get; }
        public int DamagePendingPeak { get; }
        public int DamageRequestOverflows { get; }
        public int DamageUnconsumedRequests { get; }
        public int DamageEventPeak { get; }
        public int DamageEventOverflows { get; }
        public long[] DamageRejectionsByReason { get; }

        public int ResourceRejected { get; }
        public int ResourceStaleHandleRejections { get; }
        public int ResourcePending { get; }
        public int ResourcePendingPeak { get; }
        public int ResourceRequestOverflows { get; }
        public int ResourceUnconsumedRequests { get; }
        public int ResourceEventPeak { get; }
        public int ResourceEventOverflows { get; }
        public int ResourceEventPublicationFailures { get; }
        public int[] ResourceRejectionsByReason { get; }

        public int TriggerCounters { get; }
        public int TriggerCounterPeak { get; }
        public int TriggerDefinitions { get; }
        public int TriggerDefinitionPeak { get; }
        public int TriggerSeenPeak { get; }
        public int TriggerEventPeak { get; }
        public int TriggerEventOverflows { get; }
        public int TriggerRejections { get; }
        public int TriggerLoopAborts { get; }
        public int TriggerPublicationFailures { get; }
        public int TriggerAbortPublicationFailures { get; }
        public ulong StateDigest { get; }
        public ulong GameplayEventSequenceDigest { get; }
        public long GameplayEventPublishedCount { get; }

        internal GameplayObservationSnapshot(ComponentStore store)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            SchemaVersion = 1;
            Frame = store.CurrentFrame;
            ActiveEnemies = store.GetActiveEnemyCount();
            DeathsEnqueued = store.DeathEnqueueCount;
            DeathsResolved = store.DeathResolveCount;
            AbilityPoolRejections = store.AbilityPoolRejections;
            EffectSlotRejections = store.EffectPoolRejections;

            EffectPool pool = store.GameplayEffectPool;
            EffectPoolCapacity = pool.Capacity;
            EffectPoolActive = pool.ActiveCount;
            EffectPoolPeakActive = pool.PeakActiveCount;
            EffectHandleAllocatedPages = pool.AllocatedPageCount;
            EffectHandleAllocatedSlots = pool.AllocatedSlotCapacity;
            EffectRuntimeAllocatedPages = store.GameplayEffects.AllocatedPageCount;
            EffectRuntimeAllocatedSlots = store.GameplayEffects.AllocatedSlotCapacity;
            EffectPoolAllocationFailures = pool.AllocationFailures;
            EffectPoolInvalidResolves = pool.InvalidResolveCount;
            EffectPoolStaleResolves = pool.StaleResolveCount;
            EffectPoolInactiveResolves = pool.InactiveResolveCount;

            GameplayEffectRuntime effects = store.GameplayEffectsRuntime;
            ActiveRuntimeEffects = effects.ActiveRuntimeCount;
            PeakActiveRuntimeEffects = effects.PeakActiveRuntimeCount;
            EffectRuntimeRejections = effects.Rejections;
            EffectRuntimeStateUpdateFailures = effects.StateUpdateFailures;
            EffectRuntimeEventPeak = effects.Events.PeakCount;
            EffectRuntimeEventOverflows = effects.Events.OverflowCount;
            EffectRuntimePublicationFailures = effects.PublicationFailures;
            EffectRuntimeAbortPublicationFailures = effects.AbortPublicationFailures;

            DamageResolver damage = store.DamageResolver;
            DamageAccepted = damage.AcceptedCount;
            DamageRejected = damage.RejectedCount;
            DamageLegacyApplied = damage.LegacyApplyCount;
            DamageStaleHandleRejections = damage.StaleHandleRejectedCount;
            DamagePending = damage.PendingRequestCount;
            DamagePendingPeak = damage.PeakPendingRequestCount;
            DamageRequestOverflows = damage.RequestOverflowCount;
            DamageUnconsumedRequests = damage.UnconsumedRequestCount;
            DamageEventPeak = damage.Events.PeakCount;
            DamageEventOverflows = damage.Events.OverflowCount;
            DamageEventPublicationFailures = damage.EventPublicationFailures;
            DamageRejectionsByReason = new long[Enum.GetValues(typeof(DamageRejectionReason)).Length];
            for (int i = 0; i < DamageRejectionsByReason.Length; i++)
                DamageRejectionsByReason[i] = damage.GetRejectionCount((DamageRejectionReason)i);

            ResourceResolver resources = store.ResourceResolver;
            ResourceRejected = resources.RejectedCount;
            ResourceStaleHandleRejections = resources.StaleHandleRejectedCount;
            ResourcePending = resources.PendingRequestCount;
            ResourcePendingPeak = resources.PeakPendingRequestCount;
            ResourceRequestOverflows = resources.RequestOverflowCount;
            ResourceUnconsumedRequests = resources.UnconsumedRequestCount;
            ResourceEventPeak = resources.Events.PeakCount;
            ResourceEventOverflows = resources.Events.OverflowCount;
            ResourceEventPublicationFailures = resources.EventPublicationFailures;
            ResourceRejectionsByReason = new int[Enum.GetValues(typeof(ResourceRejectionReason)).Length];
            for (int i = 0; i < ResourceRejectionsByReason.Length; i++)
                ResourceRejectionsByReason[i] = resources.GetRejectionCount((ResourceRejectionReason)i);

            GameplayTriggerRuntime triggers = store.GameplayTriggersRuntime;
            TriggerCounters = triggers.CounterCount;
            TriggerCounterPeak = triggers.PeakCounterCount;
            TriggerDefinitions = triggers.TriggerDefinitionCount;
            TriggerDefinitionPeak = triggers.PeakTriggerDefinitionCount;
            TriggerSeenPeak = triggers.PeakSeenCount;
            TriggerEventPeak = triggers.NextEvents.PeakCount;
            TriggerEventOverflows = triggers.NextEvents.OverflowCount;
            TriggerRejections = triggers.Rejections;
            TriggerLoopAborts = triggers.LoopAborts;
            TriggerPublicationFailures = triggers.PublicationFailures;
            TriggerAbortPublicationFailures = triggers.AbortPublicationFailures;
            StateDigest = ComputeStateDigest(store);
            GameplayEventSequenceDigest = ComputeEventDigest(damage, resources, effects, triggers);
            GameplayEventPublishedCount = damage.Events.PublishedCount + resources.Events.PublishedCount +
                effects.Events.PublishedCount + triggers.NextEvents.PublishedCount + triggers.AbortEvents.PublishedCount;
        }

        private static ulong ComputeStateDigest(ComponentStore store)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;
            Mix(ref hash, store.CurrentFrame);
            Mix(ref hash, store.GetActiveEnemyCount());
            for (int id = 0; id < ComponentStore.MAX_ENTITIES; id++)
            {
                if (!store.EnemyActive[id]) continue;
                Mix(ref hash, id);
                Mix(ref hash, store.GetEntityHandle(id).Generation);
                Mix(ref hash, BitConverter.SingleToInt32Bits(store.EnemyHealth[id]));
                Mix(ref hash, BitConverter.SingleToInt32Bits(store.EnemyShield[id]));
                Mix(ref hash, BitConverter.SingleToInt32Bits(store.PositionX[id]));
                Mix(ref hash, BitConverter.SingleToInt32Bits(store.PositionY[id]));
            }
            for (int id = 0; id < ComponentStore.MAX_PLAYERS; id++)
            {
                if (!store.PositionActive[id]) continue;
                Mix(ref hash, id);
                Mix(ref hash, BitConverter.SingleToInt32Bits(store.PlayerCurrentHealth[id]));
                Mix(ref hash, BitConverter.SingleToInt32Bits(store.PlayerShield[id]));
                Mix(ref hash, BitConverter.SingleToInt32Bits(store.PlayerMana[id]));
                Mix(ref hash, BitConverter.SingleToInt32Bits(store.PlayerGold[id]));
            }
            for (int id = 0; id < ComponentStore.MAX_ENTITIES; id++)
            {
                if (!store.TowerActive[id]) continue;
                Mix(ref hash, id);
                Mix(ref hash, store.GetEntityHandle(id).Generation);
                Mix(ref hash, BitConverter.SingleToInt32Bits(store.TowerAttackDamage[id]));
                Mix(ref hash, BitConverter.SingleToInt32Bits(store.TowerAttackSpeed[id]));
                Mix(ref hash, store.TowerRange[id]);
                Mix(ref hash, BitConverter.SingleToInt32Bits(store.PositionX[id]));
                Mix(ref hash, BitConverter.SingleToInt32Bits(store.PositionY[id]));
            }
            return hash;

            static void Mix(ref ulong hash, int value)
            {
                unchecked { hash = (hash ^ (uint)value) * prime; }
            }
        }

        private static ulong ComputeEventDigest(DamageResolver damage, ResourceResolver resources,
            GameplayEffectRuntime effects, GameplayTriggerRuntime triggers)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;
            Mix(ref hash, damage.Events.SequenceDigest); Mix(ref hash, (ulong)damage.Events.PublishedCount);
            Mix(ref hash, resources.Events.SequenceDigest); Mix(ref hash, (ulong)resources.Events.PublishedCount);
            Mix(ref hash, effects.Events.SequenceDigest); Mix(ref hash, (ulong)effects.Events.PublishedCount);
            Mix(ref hash, triggers.NextEvents.SequenceDigest); Mix(ref hash, (ulong)triggers.NextEvents.PublishedCount);
            Mix(ref hash, triggers.AbortEvents.SequenceDigest); Mix(ref hash, (ulong)triggers.AbortEvents.PublishedCount);
            return hash;

            static void Mix(ref ulong hash, ulong value)
            {
                unchecked { hash = (hash ^ value) * prime; }
            }
        }
    }

    internal static class GameplayObservation
    {
        /// <summary>由 soak/profile harness 显式开启；生产 Tick 默认不承担 digest 计算成本。</summary>
        public static void EnableDigests(ComponentStore store)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            store.DamageResolver.Events.DigestEnabled = true;
            store.ResourceResolver.Events.DigestEnabled = true;
            store.GameplayEffectsRuntime.Events.DigestEnabled = true;
            store.GameplayTriggersRuntime.NextEvents.DigestEnabled = true;
            store.GameplayTriggersRuntime.AbortEvents.DigestEnabled = true;
        }

        public static GameplayObservationSnapshot Capture(ComponentStore store) =>
            new GameplayObservationSnapshot(store);
    }
}
