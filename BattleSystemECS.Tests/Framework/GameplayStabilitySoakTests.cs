using System;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class GameplayStabilitySoakTests
    {
        [Fact]
        public void PeriodicDeathRecycleSoakRejectsExpectedStaleHandlesAndRecovers()
        {
            using var store = new ComponentStore();
            GameplayObservation.EnableDigests(store);
            store.AddPlayer(0, 10f, 1f, 0f, 1);
            int target = store.AddEnemy(0f, 0f, 0f, 1f, 1f, 0f, 0, 1);
            var definition = new GameplayEffectDefinition(
                new EffectId(7100),
                EffectType.Periodic,
                Array.Empty<ModifierDefinition>(),
                duration: 1f,
                period: 1f,
                clock: ClockId.Combat,
                stacking: StackingBehavior.None,
                maxStacks: 1,
                refresh: RefreshPolicy.None,
                sourceDeath: SourceDeathPolicy.Persist,
                EffectPayloadKind.Damage,
                default(TagId),
                Array.Empty<ExecutionId>(),
                periodicMagnitude: 1f);
            var scheduler = new FrameScheduler(store, new GameConfig());
            scheduler.SkillBuff.Buff = null;
            scheduler.SealGraphComposition();
            scheduler.Phase = GameState.WavePhase;

            const int cycles = 128;
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                EntityHandle staleTarget = store.GetEntityHandle(target);
                Assert.True(store.GameplayEffectsRuntime.TryApply(definition.Id, definition,
                    store.GetEntityHandle(0), staleTarget, out _, ownerPlayerId: 0));

                scheduler.Tick(1f, cycle);

                Assert.False(store.EnemyActive[target]);
                Assert.Equal(0, store.GameplayEffectsRuntime.ActiveRuntimeCount);
                int replacement = store.AddEnemy(0f, 0f, 0f, 1f, 1f, 0f, 0, 1);
                Assert.Equal(target, replacement);
                var staleRequest = new DamageRequest(store.GetEntityHandle(0), staleTarget, 1f,
                    DamageType.True, 10000L + cycle, ownerPlayerId: 0);
                DamageApplyResult staleResult = store.DamageResolver.TryApply(staleRequest);
                Assert.False(staleResult.Accepted);
                Assert.Equal(DamageRejectionReason.InvalidTarget, staleResult.Reason);
                target = replacement;
            }

            GameplayObservationSnapshot observation = GameplayObservation.Capture(store);
            Assert.Equal(cycles, observation.DeathsEnqueued);
            Assert.Equal(cycles, observation.DeathsResolved);
            Assert.Equal(cycles, observation.DamageStaleHandleRejections);
            Assert.Equal(cycles,
                observation.DamageRejectionsByReason[(int)DamageRejectionReason.InvalidTarget]);
            Assert.Equal(1, observation.EffectPoolPeakActive);
            Assert.Equal(1, observation.PeakActiveRuntimeEffects);
            Assert.Equal(1, observation.EffectHandleAllocatedPages);
            Assert.Equal(1, observation.EffectRuntimeAllocatedPages);
            Assert.Equal(0, observation.EffectPoolAllocationFailures);
            Assert.Equal(0, observation.EffectRuntimeRejections);
            Assert.Equal(0, observation.EffectRuntimeStateUpdateFailures);
            Assert.Equal(0, observation.DamageRequestOverflows);
            Assert.Equal(0, observation.DamageUnconsumedRequests);
            Assert.Equal(0, observation.DamageEventPublicationFailures);
            Assert.Equal(0, observation.ResourceEventPublicationFailures);
            Assert.Equal(0, observation.TriggerLoopAborts);
            Assert.Equal(0, observation.DamageLegacyApplied);
            EvidenceWriter.WriteJsonIfRequested("BATTLESYSTEM_LIFECYCLE_SOAK_REPORT", new
            {
                schemaVersion = 1,
                scenario = "periodic-death-entity-recycle",
                cycles,
                expectedStaleRejections = cycles,
                stateDigest = observation.StateDigest,
                gameplayEventSequenceDigest = observation.GameplayEventSequenceDigest,
                gameplayEventPublishedCount = observation.GameplayEventPublishedCount,
                observation
            });
        }
    }
}
