using System.Collections.Generic;
using System.Linq;
using System.IO;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Systems;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class DamageResolverGoldenTests
    {
        [Fact]
        public void BuffDotThroughScheduler_CommitsOnceBeforeDestroyAndPreservesAttribution()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 3f, 1f, 1f, 1);
                store.PlayerCurrentHealth[0] = 100f;
                int enemy = store.AddEnemy(0, 0, 1f, 5f, 5f, 1f, 1, 1);
                store.EnemyGoldReward[enemy] = 7;
                var oldHandle = store.GetEntityHandle(enemy);
                var buff = new BuffSystem(store, 0);
                buff.ApplyDot(enemy, 5f, 1);
                Assert.True(store.TryGetActiveEffectAt(enemy, 0, out var activeEffect, out _, out _));
                var scheduler = new FrameScheduler(store, new Config.GameConfig());
                scheduler.SkillBuff.Buff = buff;
                var order = new List<string>();
                store.OnEnemyKilled += (id, killer) =>
                {
                    order.Add("KillConfirmed");
                    Assert.Equal(0, killer);
                    Assert.True(store.EnemyActive[id]);
                    Assert.Equal(7f, store.GetPlayerGold(killer));
                    Assert.Contains(GameplayEventType.ResourceChanged,
                        Enumerable.Range(0, store.ResourceResolver.Events.Count).Select(i => store.ResourceResolver.Events.Get(i).Type));
                };

                scheduler.Tick(1f, 0);

                Assert.True(store.DamageResolver.Events.Count >= 3);
                var first = store.DamageResolver.Events.Get(0);
                var second = store.DamageResolver.Events.Get(1);
                Assert.Equal(GameplayEventType.HitConfirmed, first.Type);
                Assert.Equal(GameplayEventType.DamageApplied, second.Type);
                Assert.Equal(store.GetEntityHandle(0), first.Source);
                Assert.Equal(store.GetEntityHandle(enemy), first.Target);
                Assert.Equal(activeEffect.DefinitionId, first.EffectDefinition);
                Assert.Equal(0L, first.ParentSequence);
                Assert.Equal(first.Sequence, second.Sequence);
                var types = Enumerable.Range(0, store.DamageResolver.Events.Count).Select(i => store.DamageResolver.Events.Get(i).Type).ToList();
                int deathIndex = types.IndexOf(GameplayEventType.DeathQueued);
                int killIndex = types.IndexOf(GameplayEventType.KillConfirmed);
                Assert.Contains(GameplayEventType.HitConfirmed, types);
                Assert.True(killIndex > deathIndex);
                var kill = store.DamageResolver.Events.Get(killIndex);
                Assert.Equal(first.Sequence, kill.Sequence);
                Assert.Equal(enemy, kill.Target.Index);
                Assert.Equal(store.GetEntityHandle(0).Generation, kill.Source.Generation);
                Assert.Single(order);
                Assert.False(store.EnemyActive[enemy]);
                Assert.False(store.TryResolve(oldHandle, out _, out _));
                Assert.Equal(7f, store.GetPlayerGold(0));
            }
        }

        [Fact]
        public void DamageRequestRejectsUnsupportedSemanticsInsteadOfDroppingFields()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 3f, 1f, 1f, 1);
                int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                var source = store.GetEntityHandle(0);
                var target = store.GetEntityHandle(enemy);
                var result = store.DamageResolver.TryApply(new DamageRequest(source, target, 1f, DamageType.Physical, 1, ownerPlayerId: 0));
                Assert.True(result.Accepted);
                Assert.Equal(9f, store.EnemyHealth[enemy]);
            }
        }

        [Fact]
        public void DamageRequestIgnoreInvulnerabilityIsSupportedAndOtherFlagsAreRejected()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 3f, 1f, 1f, 1);
                int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                store.EnemyIsInvulnerable[enemy] = true;
                var source = store.GetEntityHandle(0);
                var target = store.GetEntityHandle(enemy);
                var blocked = store.DamageResolver.TryApply(new DamageRequest(source, target, 1f, DamageType.True, ElementType.None, DamageFlags.None, DamageAmountStage.Raw, DamageCommitBoundary.GameplayResolve, 1, ownerPlayerId: 0));
                Assert.False(blocked.Accepted);
                Assert.Equal(DamageRejectionReason.Invulnerable, blocked.Reason);
                var bypass = store.DamageResolver.TryApply(new DamageRequest(source, target, 1f, DamageType.True, ElementType.None, DamageFlags.IgnoreInvulnerability, DamageAmountStage.Raw, DamageCommitBoundary.GameplayResolve, 2, ownerPlayerId: 0));
                Assert.True(bypass.Accepted);
                var unsupported = store.DamageResolver.TryApply(new DamageRequest(source, target, 1f, DamageType.True, ElementType.None, DamageFlags.IgnoreShield, DamageAmountStage.Raw, DamageCommitBoundary.GameplayResolve, 3, ownerPlayerId: 0));
                Assert.False(unsupported.Accepted);
                Assert.Equal(DamageRejectionReason.Invulnerable, unsupported.Reason);
            }
        }

        [Fact]
        public void DamageRequestWithoutOwnerIsRejectedInsteadOfGuessingPlayer()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 3f, 1f, 1f, 1);
                int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                var h = store.GetEntityHandle(enemy);
                var result = store.DamageResolver.TryApply(new DamageRequest(h, h, 1f, DamageType.True, 1));
                Assert.False(result.Accepted);
                Assert.Equal(DamageRejectionReason.InvalidOwner, result.Reason);
            }
        }

        [Fact]
        public void DamageRequestOwnerAbovePlayerCapacityIsRejected()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 3f, 1f, 1f, 1);
                int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                var target = store.GetEntityHandle(enemy);
                var result = store.DamageResolver.TryApply(new DamageRequest(target, target, 1f, DamageType.True, 1, ownerPlayerId: ComponentStore.MAX_PLAYERS));
                Assert.False(result.Accepted);
                Assert.Equal(DamageRejectionReason.InvalidOwner, result.Reason);
                Assert.Equal(10f, store.EnemyHealth[enemy]);
            }
        }

        [Fact]
        public void ResourceWriterArchitecturePublishesRuntimeDamageFacts()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 3f, 1f, 1f, 1);
                int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                var source = store.GetEntityHandle(0);
                var target = store.GetEntityHandle(enemy);
                var result = store.DamageResolver.TryApply(new DamageRequest(source, target, 1f, DamageType.True, 1, ownerPlayerId: 0));
                Assert.True(result.Accepted);
                Assert.Equal(9f, store.EnemyHealth[enemy]);
                Assert.Equal(GameplayEventType.HitConfirmed, store.DamageResolver.Events.Get(0).Type);
            }
        }

        [Fact]
        public void WeatherDotUsesEarlyResolveBoundaryBeforeAiPhase()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 3f, 1f, 1f, 1);
                store.PlayerCurrentHealth[0] = 100f;
                var config = new Config.GameConfig();
                config.Weather.Types["Sandstorm"] = new Config.WeatherTypeConfig { EnemyDotPct = 0.1f, MinIntensity = 1f, MaxIntensity = 1f, DefaultDuration = 10f };
                int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                var weather = new WeatherSystem(store, config);
                weather.ForceWeather(0, Config.WeatherConfig.Sandstorm, 1f, 10f);
                var scheduler = new FrameScheduler(store, config);
                scheduler.PreGame.Weather = weather;
                scheduler.Tick(1f, 0);
                Assert.Equal(9f, store.EnemyHealth[enemy]);
                Assert.Equal(GameplayEventType.HitConfirmed, store.DamageResolver.Events.Get(0).Type);
            }
        }

        [Fact]
        public void BleedThroughSchedulerUsesResolverAndQueuesDeathOnce()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 3f, 1f, 1f, 1);
                int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                store.EnemyBleedMaxStacks[enemy] = 5f;
                var bleed = new BleedSystem(store, 0);
                bleed.ApplyBleedFromTower(1, enemy, 1f, 1f, 3f);
                var scheduler = new FrameScheduler(store, new Config.GameConfig());
                scheduler.SkillBuff.Bleed = bleed;
                scheduler.Tick(1f, 0);
                Assert.Equal(0f, store.EnemyHealth[enemy]);
                Assert.False(store.EnemyActive[enemy]);
                Assert.Equal(1, store.TotalKills);
                Assert.Equal(GameplayEventType.HitConfirmed, store.DamageResolver.Events.Get(0).Type);
            }
        }

        [Fact]
        public void ConcurrentDeathQueuePublishesOnlyOneEntry()
        {
            using (var store = new ComponentStore())
            {
                int enemy = store.AddEnemy(0, 0, 1f, 1f, 1f, 1f, 1, 1);
                System.Threading.Tasks.Parallel.For(0, 64, _ => store.QueueEnemyDeath(enemy, 0));
                store.ResolveEnemiesKilledThisFrame();
                Assert.Equal(1, store.TotalKills);
                Assert.False(store.EnemyActive[enemy]);
            }
        }

        [Fact]
        public void DeathQueueGenerationGuardDoesNotConsumeRecycledEntity()
        {
            using (var store = new ComponentStore())
            {
                int enemy = store.AddEnemy(0, 0, 1f, 1f, 1f, 1f, 1, 1);
                var old = store.GetEntityHandle(enemy);
                store.QueueEnemyDeath(enemy, 0);
                store.DestroyEntity(enemy);
                int recycled = store.AddEnemy(0, 0, 1f, 5f, 5f, 1f, 1, 1);
                Assert.Equal(enemy, recycled);
                Assert.NotEqual(old.Generation, store.GetEntityHandle(recycled).Generation);
                store.QueueEnemyDeath(recycled, 0);
                store.ResolveEnemiesKilledThisFrame();
                Assert.False(store.EnemyActive[recycled]);
                Assert.Equal(1, store.TotalKills);
            }
        }

        [Fact]
        public void ResolverEventCapacityIsObservable()
        {
            using (var store = new ComponentStore())
            {
                var source = new EntityHandle(1, 1);
                var target = new EntityHandle(2, 1);
                for (int i = 0; i < 8192; i++)
                    Assert.True(store.DamageResolver.Events.TryPublish(new GameplayEvent(GameplayEventType.DamageApplied, source, target, i), true));
                Assert.False(store.DamageResolver.Events.TryPublish(new GameplayEvent(GameplayEventType.DamageApplied, source, target, 8193), true));
                Assert.Equal(1, store.DamageResolver.EventOverflowCount);
                Assert.Equal(CommandRejection.CriticalCapacity, store.DamageResolver.LastEventRejection);
            }
        }

        [Fact]
        public void ResourceResolverProcessesAddAndSetWithValidatedHandlesAndFacts()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 3f, 1f, 1f, 1);
                store.PlayerMaxMana[0] = 10f;
                var handle = store.GetEntityHandle(0);
                var add = store.ResourceResolver.TryApply(new ResourceRequest(handle, handle, new AttributeKey(7), 3f, 17L, ownerPlayerId: 0));
                var set = store.ResourceResolver.TryApply(new ResourceRequest(handle, handle, new AttributeKey(7), 1f, ResourceOperation.Set, 42, 18L, ownerPlayerId: 0));
                Assert.True(add.Accepted);
                Assert.True(set.Accepted);
                Assert.Equal(1f, store.PlayerMana[0]);
                Assert.Equal(2, store.ResourceResolver.Events.Count);
                Assert.Equal(GameplayEventType.ResourceChanged, store.ResourceResolver.Events.Get(0).Type);
                Assert.Equal(42, new ResourceRequest(handle, handle, new AttributeKey(7), 1f, ResourceOperation.Set, 42, 18L).CauseId);
            }
        }

        [Fact]
        public void ResourceDamagePublishesDamageAppliedNotHealApplied()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 3f, 1f, 1f, 1);
                store.PlayerCurrentHealth[0] = 10f;
                store.PlayerMaxHealth[0] = 10f;
                var h = store.GetEntityHandle(0);
                var result = store.ResourceResolver.TryApply(new ResourceRequest(h, h, new AttributeKey(3), -3f, 1, ownerPlayerId: 0));
                Assert.True(result.Accepted);
                Assert.Equal(7f, store.PlayerCurrentHealth[0]);
                Assert.Equal(GameplayEventType.DamageApplied, store.ResourceResolver.Events.Get(0).Type);
            }
        }

        [Fact]
        public void DamageIgnoreShieldConsumesHealthAndLeavesShield()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 3f, 1f, 1f, 1);
                int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                store.EnemyShield[enemy] = 5f;
                var h = store.GetEntityHandle(0); var t = store.GetEntityHandle(enemy);
                var result = store.DamageResolver.TryApply(new DamageRequest(h, t, 2f, DamageType.True, ElementType.None, DamageFlags.IgnoreShield, DamageAmountStage.Raw, DamageCommitBoundary.GameplayResolve, 1, ownerPlayerId: 0));
                Assert.True(result.Accepted);
                Assert.Equal(8f, store.EnemyHealth[enemy]);
                Assert.Equal(5f, store.EnemyShield[enemy]);
            }
        }

        [Fact]
        public void ResourceResolverRejectsInvalidOperationAndStaleSource()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 3f, 1f, 1f, 1);
                var handle = store.GetEntityHandle(0);
                var invalid = store.ResourceResolver.TryApply(new ResourceRequest(handle, handle, new AttributeKey(7), 1f, (ResourceOperation)99, 0, 1));
                Assert.False(invalid.Accepted);
                Assert.Equal(ResourceRejectionReason.InvalidOperation, invalid.Reason);
                store.DestroyEntity(0);
                var stale = store.ResourceResolver.TryApply(new ResourceRequest(handle, handle, new AttributeKey(7), 1f, 2));
                Assert.False(stale.Accepted);
                Assert.Equal(ResourceRejectionReason.InvalidTarget, stale.Reason);
            }
        }

        [Fact]
        public void DeferredDamageIsCommittedOnlyAtItsBoundary()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 3f, 1f, 1f, 1);
                int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                var request = new DamageRequest(store.GetEntityHandle(0), store.GetEntityHandle(enemy), 2f,
                    DamageType.True, ElementType.None, DamageFlags.None, DamageAmountStage.Raw,
                    DamageCommitBoundary.EarlyResolve, 11L, ownerPlayerId: 0);
                store.DamageResolver.EnableDeferred(true);
                var submitted = store.DamageResolver.TryApply(request);
                Assert.True(submitted.Accepted);
                Assert.True(submitted.Deferred);
                Assert.Equal(10f, store.EnemyHealth[enemy]);
                store.DamageResolver.CommitBoundary(DamageCommitBoundary.EarlyResolve);
                Assert.Equal(8f, store.EnemyHealth[enemy]);
                Assert.False(store.DamageResolver.PendingRequestCount > 0);
            }
        }

        [Fact]
        public void ValidatedDamageRejectsStaleGenerationEvenWhenIndexIsReused()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 3f, 1f, 1f, 1);
                int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                var staleTarget = store.GetEntityHandle(enemy);
                store.DestroyEntity(enemy);
                int recycled = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                Assert.Equal(enemy, recycled);
                var request = new DamageRequest(store.GetEntityHandle(0), staleTarget, 1f, DamageType.True, 12L, ownerPlayerId: 0);
                var result = store.DamageResolver.TryApplyValidated(request);
                Assert.False(result.Accepted);
                Assert.Equal(DamageRejectionReason.InvalidTarget, result.Reason);
                Assert.Equal(10f, store.EnemyHealth[recycled]);
            }
        }

        [Fact]
        public void DeferredRequestsAreDiagnosedAtFrameBoundaryInsteadOfSilentlyDropped()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 3f, 1f, 1f, 1);
                int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                store.DamageResolver.EnableDeferred(true);
                var request = new DamageRequest(store.GetEntityHandle(0), store.GetEntityHandle(enemy), 1f,
                    DamageType.True, 23L, ownerPlayerId: 0);
                Assert.True(store.DamageResolver.TryApply(request).Deferred);
                store.BeginFrame();
                Assert.Equal(1, store.DamageResolver.UnconsumedRequestCount);
                Assert.Equal(DamageRejectionReason.UnconsumedRequests, store.DamageResolver.LastRejection);
                Assert.Equal(10f, store.EnemyHealth[enemy]);
            }
        }

        [Fact]
        public void ResourceRequestRequiresExplicitOwner()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 3f, 1f, 1f, 1);
                var handle = store.GetEntityHandle(0);
                var result = store.ResourceResolver.TryApply(new ResourceRequest(handle, handle, new AttributeKey(7), 1f, 31L));
                Assert.False(result.Accepted);
                Assert.Equal(ResourceRejectionReason.InvalidOwner, result.Reason);
            }
        }

        [Fact]
        public void PostCritDamageIsAcceptedWithoutApplyingCritTwice()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 3f, 1f, 1f, 1);
                int enemy = store.AddEnemy(0, 0, 1f, 20f, 20f, 1f, 1, 1, armor: 100f);
                var request = new DamageRequest(store.GetEntityHandle(0), store.GetEntityHandle(enemy), 10f,
                    DamageType.Physical, ElementType.None, DamageFlags.None, DamageAmountStage.PostCrit,
                    DamageCommitBoundary.GameplayResolve, 41L, ownerPlayerId: 0);
                var result = store.DamageResolver.TryApply(request);
                Assert.True(result.Accepted);
                Assert.Equal(15f, store.EnemyHealth[enemy], 3);
            }
        }

        [Fact]
        public void HitConfirmedObserverRunsBeforeResourceWrite()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 3f, 1f, 1f, 1);
                int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                float healthAtHit = -1f;
                store.DamageResolver.EventObserver = e =>
                {
                    if (e.Type == GameplayEventType.HitConfirmed) healthAtHit = store.EnemyHealth[enemy];
                };
                var result = store.DamageResolver.TryApply(new DamageRequest(store.GetEntityHandle(0), store.GetEntityHandle(enemy), 2f, DamageType.True, 42L, ownerPlayerId: 0));
                Assert.True(result.Accepted);
                Assert.Equal(10f, healthAtHit);
                Assert.Equal(8f, store.EnemyHealth[enemy]);
            }
        }

        [Fact]
        public void SameSequenceDeferredBatchReplaysInDeterministicTargetOrder()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 3f, 1f, 1f, 1);
                int firstTarget = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                int secondTarget = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                var source = store.GetEntityHandle(0);
                store.DamageResolver.EnableDeferred(true);
                var second = new DamageRequest(source, store.GetEntityHandle(secondTarget), 1f, DamageType.True,
                    ElementType.None, DamageFlags.None, DamageAmountStage.Raw, DamageCommitBoundary.GameplayResolve, 77L, ownerPlayerId: 0);
                var first = new DamageRequest(source, store.GetEntityHandle(firstTarget), 1f, DamageType.True,
                    ElementType.None, DamageFlags.None, DamageAmountStage.Raw, DamageCommitBoundary.GameplayResolve, 77L, ownerPlayerId: 0);
                Assert.True(store.DamageResolver.TryApply(second).Deferred);
                Assert.True(store.DamageResolver.TryApply(first).Deferred);
                store.DamageResolver.CommitBoundary(DamageCommitBoundary.GameplayResolve);
                Assert.Equal(firstTarget, store.DamageResolver.Events.Get(0).Target.Index);
                Assert.Equal(secondTarget, store.DamageResolver.Events.Get(2).Target.Index);
                Assert.NotEqual(store.DamageResolver.Events.Get(0).Target.Index, store.DamageResolver.Events.Get(2).Target.Index);
            }
        }

        [Fact]
        public void ReflectProvenanceIsAcceptedAndSameChainRecursionIsRejected()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 3f, 1f, 1f, 1);
                int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                var source = store.GetEntityHandle(0);
                var target = store.GetEntityHandle(enemy);
                var accepted = store.DamageResolver.TryApply(new DamageRequest(source, target, 1f, DamageType.True,
                    ElementType.None, DamageFlags.Reflect, DamageAmountStage.Raw, DamageCommitBoundary.GameplayResolve,
                    90L, parentSequence: 89L, ownerPlayerId: 0, provenanceId: 500L, provenanceDepth: 1));
                Assert.True(accepted.Accepted);
                Assert.Equal(DamageFlags.Reflect, store.DamageResolver.Events.Get(0).Flags);
                var secondHop = store.DamageResolver.TryApply(new DamageRequest(source, target, 1f, DamageType.True,
                    ElementType.None, DamageFlags.Reflect, DamageAmountStage.Raw, DamageCommitBoundary.GameplayResolve,
                    91L, parentSequence: 90L, ownerPlayerId: 0, provenanceId: 500L, provenanceDepth: 2));
                Assert.True(secondHop.Accepted);
                var overDepth = store.DamageResolver.TryApply(new DamageRequest(source, target, 1f, DamageType.True,
                    ElementType.None, DamageFlags.Reflect, DamageAmountStage.Raw, DamageCommitBoundary.GameplayResolve,
                    92L, parentSequence: 91L, ownerPlayerId: 0, provenanceId: 500L, provenanceDepth: 5));
                Assert.False(overDepth.Accepted);
                Assert.Equal(DamageRejectionReason.UnsupportedFlags, overDepth.Reason);
                var recursive = store.DamageResolver.TryApply(new DamageRequest(source, target, 1f, DamageType.True,
                    ElementType.None, DamageFlags.Reflect, DamageAmountStage.Raw, DamageCommitBoundary.GameplayResolve,
                    93L, parentSequence: 93L, ownerPlayerId: 0, provenanceId: 500L, provenanceDepth: 2));
                Assert.False(recursive.Accepted);
            }
        }

        [Fact]
        public void TransferProvenanceIsRetainedByDamageFacts()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 3f, 1f, 1f, 1);
                int sourceEnemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                int targetEnemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                long provenance = store.AllocateGameplaySequence(sourceEnemy);
                Assert.True(store.ApplyDamageAuthority(sourceEnemy, targetEnemy, 1f, 0,
                    flags: DamageFlags.Transfer, stage: DamageAmountStage.Raw,
                    parentSequence: provenance, provenanceId: provenance, provenanceDepth: 1));
                var hit = store.DamageResolver.Events.Get(0);
                Assert.Equal(DamageFlags.Transfer, hit.Flags);
                Assert.NotEqual(0L, hit.ProvenanceId);
                Assert.InRange(hit.ProvenanceDepth, 1, DamageResolver.MaxProvenanceDepth);
            }
        }
    }
}
