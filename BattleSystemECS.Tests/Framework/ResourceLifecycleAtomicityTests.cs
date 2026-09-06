using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Components;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class ResourceLifecycleAtomicityTests
    {
        private static ComponentStore StoreWithFullResourceQueue()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 100f, 1f, 1f, 1);
            var filler = new GameplayEvent(GameplayEventType.AbilityRejected, default(EntityHandle), default(EntityHandle), 9001L);
            while (store.ResourceResolver.Events.TryPublish(filler, true)) { }
            return store;
        }

        [Fact]
        public void TimedShieldExpiryOverflowKeepsShieldAndDuration()
        {
            var store = StoreWithFullResourceQueue();
            store.PlayerShield[0] = 25f;
            store.PlayerShieldDuration[0] = 1f;
            int eventsBefore = store.ResourceResolver.Events.Count;

            store.ResourceResolver.TickTimedShields(2f, ClockId.Combat);

            Assert.Equal(25f, store.PlayerShield[0]);
            Assert.Equal(1f, store.PlayerShieldDuration[0]);
            Assert.Equal(eventsBefore, store.ResourceResolver.Events.Count);
            Assert.Equal(ResourceRejectionReason.RequestQueueOverflow, store.ResourceResolver.LastRejectionReason);
            Assert.Equal(1, store.ResourceResolver.GetRejectionCount(ResourceRejectionReason.RequestQueueOverflow));
            Assert.Equal(0, store.ResourceResolver.EventPublicationFailures);
        }

        [Fact]
        public void GoldLifecycleOverflowKeepsGoldAndPublishesNoFact()
        {
            var store = StoreWithFullResourceQueue();
            int eventsBefore = store.ResourceResolver.Events.Count;
            float goldBefore = store.PlayerGold[0];

            float applied = store.ResourceResolver.ApplyLifecycleGold(0, 17f, store.GetEntityHandle(0), 11L, 0);

            Assert.Equal(0f, applied);
            Assert.Equal(goldBefore, store.PlayerGold[0]);
            Assert.Equal(eventsBefore, store.ResourceResolver.Events.Count);
            Assert.Equal(ResourceRejectionReason.RequestQueueOverflow, store.ResourceResolver.LastRejectionReason);
            Assert.Equal(1, store.ResourceResolver.GetRejectionCount(ResourceRejectionReason.RequestQueueOverflow));
            Assert.Equal(0, store.ResourceResolver.EventPublicationFailures);
        }

        [Fact]
        public void ManaLifecycleOverflowKeepsManaAndPublishesNoFact()
        {
            var store = StoreWithFullResourceQueue();
            store.PlayerMaxMana[0] = 100f;
            int eventsBefore = store.ResourceResolver.Events.Count;
            float manaBefore = store.PlayerMana[0];

            float applied = store.ResourceResolver.ApplyLifecycleMana(0, 17f, store.GetEntityHandle(0), 12L, 0);

            Assert.Equal(0f, applied);
            Assert.Equal(manaBefore, store.PlayerMana[0]);
            Assert.Equal(eventsBefore, store.ResourceResolver.Events.Count);
            Assert.Equal(ResourceRejectionReason.RequestQueueOverflow, store.ResourceResolver.LastRejectionReason);
            Assert.Equal(1, store.ResourceResolver.GetRejectionCount(ResourceRejectionReason.RequestQueueOverflow));
            Assert.Equal(0, store.ResourceResolver.EventPublicationFailures);
        }

        [Fact]
        public void LifecycleSuccessPublishesExactlyOneFactWithStateChange()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 100f, 1f, 1f, 1);
            float before = store.PlayerGold[0];

            float applied = store.ResourceResolver.ApplyLifecycleGold(0, 17f, store.GetEntityHandle(0), 13L, 0);

            Assert.Equal(17f, applied);
            Assert.Equal(before + 17f, store.PlayerGold[0]);
            Assert.Equal(1, store.ResourceResolver.Events.Count);
            Assert.Equal(GameplayEventType.ResourceChanged, store.ResourceResolver.Events.Get(0).Type);
            Assert.Equal(0, store.ResourceResolver.EventPublicationFailures);
        }

        [Fact]
        public void ShieldBatchPublicationFailureRollsBackState()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 100f, 1f, 1f, 1);
                var handle = store.GetEntityHandle(0);
                float before = store.PlayerShield[0];
                store.ResourceResolver.Events.BeforeBatchPublish = () =>
                {
                    store.ResourceResolver.Events.BeforeBatchPublish = null;
                    while (store.ResourceResolver.Events.TryPublish(new GameplayEvent(GameplayEventType.AbilityRejected, handle, handle, 99L), true)) { }
                };
                var result = store.ResourceResolver.TryApply(new ShieldRequest(handle, handle, 5f, 2f, ClockId.Combat, 1L), 0);
                Assert.False(result.Accepted);
                Assert.Equal(ResourceRejectionReason.RequestQueueOverflow, result.Reason);
                Assert.Equal(before, store.PlayerShield[0]);
            }
        }

        [Fact]
        public void DamageBatchPublicationFailureRollsBackEnemyState()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 100f, 1f, 1f, 1);
                int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                var source = store.GetEntityHandle(0);
                var target = store.GetEntityHandle(enemy);
                float before = store.EnemyHealth[enemy];
                store.DamageResolver.Events.BeforeBatchPublish = () =>
                {
                    store.DamageResolver.Events.BeforeBatchPublish = null;
                    while (store.DamageResolver.Events.TryPublish(new GameplayEvent(GameplayEventType.AbilityRejected, source, target, 99L), true)) { }
                };
                var result = store.DamageResolver.TryApply(new DamageRequest(source, target, 2f, DamageType.True, 1L, ownerPlayerId: 0));
                Assert.False(result.Accepted);
                Assert.Equal(DamageRejectionReason.RequestQueueOverflow, result.Reason);
                Assert.Equal(before, store.EnemyHealth[enemy]);
            }
        }

        [Fact]
        public void GenericEnemyResourcePublicationFailureRollsBackEveryMutableResourceField()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 100f, 1f, 1f, 1);
                int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                var source = store.GetEntityHandle(0);
                var target = store.GetEntityHandle(enemy);
                store.EnemyMaxHealth[enemy] = 20f;
                store.EnemyHealth[enemy] = 8f;
                store.EnemyCurrentMana[enemy] = 3f;
                store.ResourceResolver.Events.BeforeBatchPublish = () =>
                {
                    store.ResourceResolver.Events.BeforeBatchPublish = null;
                    while (store.ResourceResolver.Events.TryPublish(new GameplayEvent(GameplayEventType.AbilityRejected, source, target, 99L), true)) { }
                };
                var result = store.ResourceResolver.TryApply(new ResourceRequest(source, target, new AttributeKey(2), 5f, 20L, 0));
                Assert.False(result.Accepted);
                Assert.Equal(ResourceRejectionReason.RequestQueueOverflow, result.Reason);
                Assert.Equal(20f, store.EnemyMaxHealth[enemy]);
                Assert.Equal(8f, store.EnemyHealth[enemy]);
                Assert.Equal(3f, store.EnemyCurrentMana[enemy]);
            }
        }

        [Fact]
        public void DamagePublicationFailureRollsBackShieldBreakReactionState()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 100f, 1f, 1f, 1);
                int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                var source = store.GetEntityHandle(0);
                var target = store.GetEntityHandle(enemy);
                store.EnemyShield[enemy] = 2f;
                store.EnemyShieldType[enemy] = ElementType.Fire;
                store.EnemyShieldBreakReaction[enemy] = ElementType.Ice;
                store.EnemyShieldBreakElementDuration[enemy] = 7f;
                store.DamageResolver.Events.BeforeBatchPublish = () =>
                {
                    store.DamageResolver.Events.BeforeBatchPublish = null;
                    while (store.DamageResolver.Events.TryPublish(new GameplayEvent(GameplayEventType.AbilityRejected, source, target, 99L), true)) { }
                };
                var result = store.DamageResolver.TryApply(new DamageRequest(source, target, 5f, DamageType.True, ElementType.Fire, DamageFlags.None,
                    DamageAmountStage.Raw, DamageCommitBoundary.GameplayResolve, 21L, ownerPlayerId: 0));
                Assert.False(result.Accepted);
                Assert.Equal(DamageRejectionReason.RequestQueueOverflow, result.Reason);
                Assert.Equal(2f, store.EnemyShield[enemy]);
                Assert.Equal(ElementType.None, store.EnemyElementStatus[enemy]);
                Assert.Equal(0f, store.EnemyElementTimer[enemy * 4 + 1]);
                Assert.Empty(store.PendingShieldBreaks);
            }
        }

        [Fact]
        public void PlayerDamagePublicationFailureRollsBackEveryDecreasePlayerHealthMutation()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 100f, 1f, 1f, 1);
                var player = store.GetEntityHandle(0);
                store.PlayerMaxHealth[0] = 100f;
                store.PlayerCurrentHealth[0] = 5f;
                store.PlayerManaShield[0] = 1f;
                store.PlayerManaShieldAbsorbRatio[0] = 1f;
                store.PlayerManaShieldTriggered[0] = false;
                store.PlayerShield[0] = 1f;
                store.PlayerReincarnationCharges[0] = 1;
                store.PlayerReincarnationHealFraction[0] = 0.5f;
                store.PlayerHasReincarnated[0] = false;
                long deathsBefore = store.DeathEnqueueCount;
                store.ResourceResolver.Events.BeforeBatchPublish = () =>
                {
                    store.ResourceResolver.Events.BeforeBatchPublish = null;
                    while (store.ResourceResolver.Events.TryPublish(
                        new GameplayEvent(GameplayEventType.AbilityRejected, player, player, 9001L), true)) { }
                };

                var result = store.ResourceResolver.TryApply(new PlayerDamageRequest(player, player, 20f, 31L, ownerPlayerId: 0));

                Assert.False(result.Accepted);
                Assert.Equal(ResourceRejectionReason.RequestQueueOverflow, result.Reason);
                Assert.Equal(5f, store.PlayerCurrentHealth[0]);
                Assert.Equal(1f, store.PlayerManaShield[0]);
                Assert.False(store.PlayerManaShieldTriggered[0]);
                Assert.Equal(1f, store.PlayerShield[0]);
                Assert.Equal(1, store.PlayerReincarnationCharges[0]);
                Assert.False(store.PlayerHasReincarnated[0]);
                Assert.Equal(deathsBefore, store.DeathEnqueueCount);
                Assert.DoesNotContain(Enumerable.Range(0, store.ResourceResolver.Events.Count)
                    .Select(i => store.ResourceResolver.Events.Get(i).Type),
                    type => type == GameplayEventType.DamageApplied || type == GameplayEventType.DeathQueued);
                Assert.Equal(1, store.ResourceResolver.GetRejectionCount(ResourceRejectionReason.RequestQueueOverflow));
            }
        }

        [Fact]
        public async Task ConcurrentDamageFailureCannotRollbackSuccessfulLethalCommit()
        {
            using (var store = new ComponentStore())
            using (var failureAtSnapshot = new ManualResetEventSlim())
            using (var releaseFailure = new ManualResetEventSlim())
            {
                store.AddPlayer(0, 100f, 1f, 1f, 1);
                int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                var source = store.GetEntityHandle(0);
                var target = store.GetEntityHandle(enemy);
                int observerCalls = 0;
                bool failureHeldCommitLock = false;
                store.DamageResolver.EventObserver = _ => Interlocked.Increment(ref observerCalls);
                store.DamageResolver.BeforeStateCommit = (sequence, commitLockHeld) =>
                {
                    if (sequence == 42L)
                    {
                        failureHeldCommitLock = commitLockHeld;
                        failureAtSnapshot.Set();
                        Assert.True(releaseFailure.Wait(TimeSpan.FromSeconds(10)));
                        while (store.DamageResolver.Events.TryPublish(
                            new GameplayEvent(GameplayEventType.AbilityRejected, source, target, 9001L), true)) { }
                    }
                    else if (commitLockHeld)
                    {
                        store.DamageResolver.Events.Clear();
                    }
                };
                var failingRequest = new DamageRequest(source, target, 10f, DamageType.True, 42L, ownerPlayerId: 0);
                var successfulRequest = new DamageRequest(source, target, 10f, DamageType.True, 41L, ownerPlayerId: 0);
                Task<DamageApplyResult> failure = Task.Factory.StartNew(() => store.DamageResolver.TryApply(failingRequest),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Assert.True(failureAtSnapshot.Wait(TimeSpan.FromSeconds(10)));
                Task<DamageApplyResult> success = Task.Factory.StartNew(() => store.DamageResolver.TryApply(successfulRequest),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                if (failureHeldCommitLock)
                {
                    releaseFailure.Set();
                    await failure.WaitAsync(TimeSpan.FromSeconds(10));
                }
                else
                {
                    await success.WaitAsync(TimeSpan.FromSeconds(10));
                    releaseFailure.Set();
                }

                var failedResult = await failure.WaitAsync(TimeSpan.FromSeconds(10));
                var successResult = await success.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(failedResult.Accepted);
                Assert.Equal(DamageRejectionReason.RequestQueueOverflow, failedResult.Reason);
                Assert.True(successResult.Accepted);
                Assert.True(successResult.DeathQueued);
                Assert.Equal(0f, store.EnemyHealth[enemy]);
                Assert.True(store.IsEnemyPendingDeath(enemy));
                Assert.Equal(1, store.DeathEnqueueCount);
                Assert.Equal(1, observerCalls);
                Assert.Single(Enumerable.Range(0, store.DamageResolver.Events.Count),
                    i => store.DamageResolver.Events.Get(i).Type == GameplayEventType.DeathQueued);
            }
        }

        [Fact]
        public async Task ConcurrentResourceFailureCannotRollbackSuccessfulLethalCommit()
        {
            using (var store = new ComponentStore())
            using (var failureAtSnapshot = new ManualResetEventSlim())
            using (var releaseFailure = new ManualResetEventSlim())
            {
                store.AddPlayer(0, 100f, 1f, 1f, 1);
                int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                var source = store.GetEntityHandle(0);
                var target = store.GetEntityHandle(enemy);
                bool failureHeldCommitLock = false;
                store.ResourceResolver.BeforeStateCommit = (sequence, commitLockHeld) =>
                {
                    if (sequence == 52L)
                    {
                        failureHeldCommitLock = commitLockHeld;
                        failureAtSnapshot.Set();
                        Assert.True(releaseFailure.Wait(TimeSpan.FromSeconds(10)));
                        while (store.ResourceResolver.Events.TryPublish(
                            new GameplayEvent(GameplayEventType.AbilityRejected, source, target, 9001L), true)) { }
                    }
                    else if (commitLockHeld)
                    {
                        store.ResourceResolver.Events.Clear();
                    }
                };
                var failingRequest = new ResourceRequest(source, target, new AttributeKey(3), -10f, 52L, 0);
                var successfulRequest = new ResourceRequest(source, target, new AttributeKey(3), -10f, 51L, 0);
                Task<ResourceApplyResult> failure = Task.Factory.StartNew(() => store.ResourceResolver.TryApply(failingRequest),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Assert.True(failureAtSnapshot.Wait(TimeSpan.FromSeconds(10)));
                Task<ResourceApplyResult> success = Task.Factory.StartNew(() => store.ResourceResolver.TryApply(successfulRequest),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                if (failureHeldCommitLock)
                {
                    releaseFailure.Set();
                    await failure.WaitAsync(TimeSpan.FromSeconds(10));
                }
                else
                {
                    await success.WaitAsync(TimeSpan.FromSeconds(10));
                    releaseFailure.Set();
                }

                var failedResult = await failure.WaitAsync(TimeSpan.FromSeconds(10));
                var successResult = await success.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(failedResult.Accepted);
                Assert.Equal(ResourceRejectionReason.RequestQueueOverflow, failedResult.Reason);
                Assert.True(successResult.Accepted);
                Assert.Equal(0f, store.EnemyHealth[enemy]);
                Assert.True(store.IsEnemyPendingDeath(enemy));
                Assert.Equal(1, store.DeathEnqueueCount);
                Assert.Single(Enumerable.Range(0, store.ResourceResolver.Events.Count),
                    i => store.ResourceResolver.Events.Get(i).Type == GameplayEventType.DeathQueued);
            }
        }

        [Fact]
        public async Task ConcurrentResourceLethalRequestsPublishDeathExactlyOnce()
        {
            using (var store = new ComponentStore())
            using (var bothValidated = new Barrier(2))
            {
                store.AddPlayer(0, 100f, 1f, 1f, 1);
                int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                var source = store.GetEntityHandle(0);
                var target = store.GetEntityHandle(enemy);
                store.ResourceResolver.BeforeStateCommit = (_, commitLockHeld) =>
                {
                    if (!commitLockHeld)
                        Assert.True(bothValidated.SignalAndWait(TimeSpan.FromSeconds(10)));
                };
                var firstRequest = new ResourceRequest(source, target, new AttributeKey(3), -10f, 61L, 0);
                var secondRequest = new ResourceRequest(source, target, new AttributeKey(3), -10f, 62L, 0);

                Task<ResourceApplyResult> first = Task.Factory.StartNew(() => store.ResourceResolver.TryApply(firstRequest),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Task<ResourceApplyResult> second = Task.Factory.StartNew(() => store.ResourceResolver.TryApply(secondRequest),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                ResourceApplyResult[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

                Assert.Single(results, result => result.Accepted);
                Assert.Single(results, result => result.Reason == ResourceRejectionReason.TargetAlreadyDead);
                Assert.Equal(0f, store.EnemyHealth[enemy]);
                Assert.True(store.IsEnemyPendingDeath(enemy));
                Assert.Equal(1, store.DeathEnqueueCount);
                Assert.Single(Enumerable.Range(0, store.ResourceResolver.Events.Count),
                    i => store.ResourceResolver.Events.Get(i).Type == GameplayEventType.DeathQueued);
            }
        }

        [Fact]
        public void DeathResolveResourceQueueOverflowKeepsDeathBatchUncommitted()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 100f, 1f, 1f, 1);
                int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                store.EnemyGoldReward[enemy] = 5;
                store.QueueEnemyDeath(enemy, 0, 77L, store.GetEntityHandle(0));
                float goldBefore = store.PlayerGold[0];
                int callbacks = 0;
                store.OnEnemyKilled += (_, __) => callbacks++;
                var filler = new GameplayEvent(GameplayEventType.AbilityRejected, default(EntityHandle), default(EntityHandle), 9001L);
                while (store.ResourceResolver.Events.TryPublish(filler, true)) { }

                store.ResolveEnemiesKilledThisFrame();

                Assert.True(store.EnemyActive[enemy]);
                Assert.Equal(0, store.TotalKills);
                Assert.Equal(0, store.DeathResolveCount);
                Assert.Equal(goldBefore, store.PlayerGold[0]);
                Assert.Equal(0, callbacks);
                Assert.Equal(store.ResourceResolver.Events.Capacity, store.ResourceResolver.Events.Count);
                Assert.True(store.IsEnemyPendingDeath(enemy));

                store.BeginFrame();
                store.ResolveEnemiesKilledThisFrame();
                Assert.False(store.EnemyActive[enemy]);
                Assert.Equal(1, store.TotalKills);
                Assert.Equal(1, store.DeathResolveCount);
                Assert.Equal(1, callbacks);
                store.ResolveEnemiesKilledThisFrame();
                Assert.Equal(1, store.TotalKills);
            }
        }

        [Fact]
        public void DeathBatchCommitsAtExactReservedCapacity()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 100f, 1f, 1f, 1);
                int first = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                int second = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                store.EnemyGoldReward[first] = store.EnemyGoldReward[second] = 1;
                var filler = new GameplayEvent(GameplayEventType.AbilityRejected, default(EntityHandle), default(EntityHandle), 9001L);
                for (int i = 0; i < store.DamageResolver.Events.Capacity - 2; i++) Assert.True(store.DamageResolver.Events.TryPublish(filler, true));
                for (int i = 0; i < store.ResourceResolver.Events.Capacity - 2; i++) Assert.True(store.ResourceResolver.Events.TryPublish(filler, true));
                store.QueueEnemyDeath(first, 0, 101L, store.GetEntityHandle(0));
                store.QueueEnemyDeath(second, 0, 102L, store.GetEntityHandle(0));

                store.ResolveEnemiesKilledThisFrame();

                Assert.Equal(2, store.TotalKills);
                Assert.Equal(store.DamageResolver.Events.Capacity, store.DamageResolver.Events.Count);
                Assert.Equal(store.ResourceResolver.Events.Capacity, store.ResourceResolver.Events.Count);
            }
        }

        [Fact]
        public void NewDeathWhileBlockedJoinsRetriedBatchWithoutLoss()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 100f, 1f, 1f, 1);
                int first = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                int second = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                store.EnemyGoldReward[first] = store.EnemyGoldReward[second] = 1;
                var filler = new GameplayEvent(GameplayEventType.AbilityRejected, default(EntityHandle), default(EntityHandle), 9001L);
                while (store.ResourceResolver.Events.TryPublish(filler, true)) { }
                store.QueueEnemyDeath(first, 0, 111L, store.GetEntityHandle(0));
                store.ResolveEnemiesKilledThisFrame();
                store.QueueEnemyDeath(second, 0, 112L, store.GetEntityHandle(0));

                store.BeginFrame();
                store.ResolveEnemiesKilledThisFrame();

                Assert.False(store.EnemyActive[first]);
                Assert.False(store.EnemyActive[second]);
                Assert.Equal(2, store.TotalKills);
                Assert.Equal(2, store.DeathResolveCount);
            }
        }

        [Fact]
        public void DeathBatchPublishesEveryKillBeforeReentrantCallbackProducer()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 100f, 1f, 1f, 1);
                int first = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                int second = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                store.QueueEnemyDeath(first, 0, 71L, store.GetEntityHandle(0));
                store.QueueEnemyDeath(second, 0, 72L, store.GetEntityHandle(0));
                int callbacks = 0;
                int visibleKillsInCallback = -1;
                store.OnEnemyKilled += (_, __) =>
                {
                    callbacks++;
                    visibleKillsInCallback = 0;
                    for (int i = 0; i < store.DamageResolver.Events.Count; i++)
                        if (store.DamageResolver.Events.Get(i).Type == GameplayEventType.KillConfirmed) visibleKillsInCallback++;
                    var filler = new GameplayEvent(GameplayEventType.AbilityRejected, default(EntityHandle), default(EntityHandle), 9001L);
                    while (store.DamageResolver.Events.TryPublish(filler, true)) { }
                };

                store.ResolveEnemiesKilledThisFrame();

                Assert.False(store.EnemyActive[first]);
                Assert.False(store.EnemyActive[second]);
                Assert.Equal(2, callbacks);
                Assert.Equal(0, visibleKillsInCallback);
                Assert.Equal(2, store.TotalKills);
                int kills = 0;
                for (int i = 0; i < store.DamageResolver.Events.Count; i++)
                    if (store.DamageResolver.Events.Get(i).Type == GameplayEventType.KillConfirmed) kills++;
                Assert.Equal(2, kills);
            }
        }

        [Fact]
        public void ThrowingDeathCallbackStillFinishesReservedCommitExactlyOnce()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 100f, 1f, 1f, 1);
                int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                store.QueueEnemyDeath(enemy, 0, 81L, store.GetEntityHandle(0));
                store.OnEnemyKilled += (_, __) => throw new InvalidOperationException("observer failure");

                var error = Assert.Throws<InvalidOperationException>(() => store.ResolveEnemiesKilledThisFrame());

                Assert.Equal("observer failure", error.Message);
                Assert.False(store.EnemyActive[enemy]);
                Assert.Equal(1, store.TotalKills);
                Assert.Equal(GameplayEventType.KillConfirmed, store.DamageResolver.Events.Get(store.DamageResolver.Events.Count - 1).Type);
                store.ResolveEnemiesKilledThisFrame();
                Assert.Equal(1, store.TotalKills);
            }
        }

        [Fact]
        public void DeathQueuedByCallbackSurvivesSameFrameCascadeAndCommitsExactlyOnce()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 100f, 1f, 1f, 1);
                int first = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                int second = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                store.EnemyGoldReward[first] = store.EnemyGoldReward[second] = 3;
                int firstCallbacks = 0;
                int secondCallbacks = 0;
                store.OnEnemyKilled += (enemyId, playerId) =>
                {
                    if (enemyId == first)
                    {
                        firstCallbacks++;
                        store.QueueEnemyDeath(second, playerId, 202L, store.GetEntityHandle(playerId));
                    }
                    else if (enemyId == second) secondCallbacks++;
                };
                store.QueueEnemyDeath(first, 0, 201L, store.GetEntityHandle(0));

                store.ResolveEnemiesKilledThisFrame();

                Assert.False(store.EnemyActive[first]);
                Assert.True(store.EnemyActive[second]);
                Assert.True(store.IsEnemyPendingDeath(second));
                Assert.Equal(1, firstCallbacks);
                Assert.Equal(0, secondCallbacks);
                Assert.Equal(1, store.TotalKills);

                store.ResolveEnemiesKilledThisFrame();

                Assert.False(store.EnemyActive[second]);
                Assert.False(store.IsEnemyPendingDeath(second));
                Assert.Equal(1, firstCallbacks);
                Assert.Equal(1, secondCallbacks);
                Assert.Equal(2, store.TotalKills);
                Assert.Equal(2, store.DeathResolveCount);
                Assert.Equal(2, Enumerable.Range(0, store.DamageResolver.Events.Count)
                    .Count(i => store.DamageResolver.Events.Get(i).Type == GameplayEventType.KillConfirmed));
                store.BeginFrame();
                store.ResolveEnemiesKilledThisFrame();
                Assert.Equal(2, store.TotalKills);
            }
        }

        [Fact]
        public void ThrowingDeathSubscribersDoNotSkipLaterSubscribersOrRepeatCommit()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 100f, 1f, 1f, 1);
                int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                int first = 0, middle = 0, last = 0;
                store.OnEnemyKilled += (_, __) => { first++; throw new InvalidOperationException("first failure"); };
                store.OnEnemyKilled += (_, __) => { middle++; throw new ApplicationException("middle failure"); };
                store.OnEnemyKilled += (_, __) => last++;
                store.QueueEnemyDeath(enemy, 0, 301L, store.GetEntityHandle(0));

                var error = Assert.Throws<InvalidOperationException>(() => store.ResolveEnemiesKilledThisFrame());

                Assert.Equal("first failure", error.Message);
                Assert.Equal(1, first);
                Assert.Equal(1, middle);
                Assert.Equal(1, last);
                Assert.False(store.EnemyActive[enemy]);
                Assert.Equal(1, store.TotalKills);
                Assert.Equal(GameplayEventType.KillConfirmed,
                    store.DamageResolver.Events.Get(store.DamageResolver.Events.Count - 1).Type);
                store.ResolveEnemiesKilledThisFrame();
                Assert.Equal(1, first);
                Assert.Equal(1, middle);
                Assert.Equal(1, last);
                Assert.Equal(1, store.TotalKills);
            }
        }

        [Fact]
        public void CallbackQueuedDeathSurvivesBeginFrameAndKeepsRewardsAndFactsExact()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 100f, 1f, 1f, 1);
                store.WaveGoldDecayRate = 0f;
                int first = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                int second = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                store.EnemyGoldReward[first] = 2;
                store.EnemyGoldReward[second] = 5;
                float goldBefore = store.PlayerGold[0];
                int callbacks = 0;
                store.OnEnemyKilled += (enemyId, playerId) =>
                {
                    callbacks++;
                    if (enemyId == first)
                        store.QueueEnemyDeath(second, playerId, 402L, store.GetEntityHandle(playerId));
                };
                store.QueueEnemyDeath(first, 0, 401L, store.GetEntityHandle(0));
                store.ResolveEnemiesKilledThisFrame();

                store.BeginFrame();
                store.ResolveEnemiesKilledThisFrame();

                Assert.False(store.EnemyActive[first]);
                Assert.False(store.EnemyActive[second]);
                Assert.Equal(2, callbacks);
                Assert.Equal(2, store.TotalKills);
                Assert.Equal(2, store.DeathResolveCount);
                Assert.Equal(goldBefore + 7f, store.PlayerGold[0]);
                Assert.Single(Enumerable.Range(0, store.DamageResolver.Events.Count),
                    i => store.DamageResolver.Events.Get(i).Type == GameplayEventType.KillConfirmed);
                Assert.Single(Enumerable.Range(0, store.ResourceResolver.Events.Count),
                    i => store.ResourceResolver.Events.Get(i).Type == GameplayEventType.ResourceChanged);
            }
        }

        [Fact]
        public void ThrowingTowerKillSubscriberDoesNotSkipLaterSubscriberOrRepeat()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 100f, 1f, 1f, 1);
                int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                int tower = store.CreateEntity();
                store.AddTower(tower, TowerType.Basic, 1f, 1, 1f, 1, 1f);
                int throwing = 0, later = 0;
                store.OnTowerKill += (_, __, ___) => { throwing++; throw new InvalidOperationException("tower observer failure"); };
                store.OnTowerKill += (_, __, ___) => later++;
                store.QueueTowerKill(enemy, 0, tower);
                store.QueueEnemyDeath(enemy, 0, 501L, store.GetEntityHandle(tower));

                var error = Assert.Throws<InvalidOperationException>(() => store.ResolveEnemiesKilledThisFrame());

                Assert.Equal("tower observer failure", error.Message);
                Assert.Equal(1, throwing);
                Assert.Equal(1, later);
                Assert.False(store.EnemyActive[enemy]);
                store.ResolveEnemiesKilledThisFrame();
                Assert.Equal(1, throwing);
                Assert.Equal(1, later);
            }
        }

        [Fact]
        public void DeathSubscriberDuplicateRemoveAndRegistrationOrderMatchEventSemantics()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 100f, 1f, 1f, 1);
                int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                var calls = new List<int>();
                Action<int, int> duplicate = (_, __) => calls.Add(2);
                Action<int, int> removed = (_, __) => calls.Add(9);
                store.OnEnemyKilled += (_, __) => calls.Add(1);
                store.OnEnemyKilled += duplicate;
                store.OnEnemyKilled += duplicate;
                store.OnEnemyKilled += removed;
                store.OnEnemyKilled -= duplicate;
                store.OnEnemyKilled -= removed;
                store.OnEnemyKilled += (_, __) => calls.Add(3);
                store.QueueEnemyDeath(enemy, 0, 601L, store.GetEntityHandle(0));

                store.ResolveEnemiesKilledThisFrame();

                Assert.Equal(new[] { 1, 2, 3 }, calls);
                Assert.Equal(3, store.EnemyKilledSubscriberCount);
            }
        }

        [Fact]
        public async Task RegistrationDuringDispatchUsesSnapshotAndAppearsOnNextDeath()
        {
            using (var store = new ComponentStore())
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                store.AddPlayer(0, 100f, 1f, 1f, 1);
                int first = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                int second = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                int original = 0, late = 0;
                store.OnEnemyKilled += (_, __) =>
                {
                    original++;
                    if (original == 1) { entered.Set(); release.Wait(TimeSpan.FromSeconds(2)); }
                };
                store.QueueEnemyDeath(first, 0, 701L, store.GetEntityHandle(0));
                Task dispatch = Task.Factory.StartNew(() => store.ResolveEnemiesKilledThisFrame(),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

                store.OnEnemyKilled += (_, __) => late++;
                release.Set();
                await dispatch.WaitAsync(TimeSpan.FromSeconds(10));

                Assert.Equal(1, original);
                Assert.Equal(0, late);
                store.QueueEnemyDeath(second, 0, 702L, store.GetEntityHandle(0));
                store.ResolveEnemiesKilledThisFrame();
                Assert.Equal(2, original);
                Assert.Equal(1, late);
            }
        }

        [Fact]
        public void DeathReservationHookProducerCannotCreatePartialCommit()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 100f, 1f, 1f, 1);
                int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
                store.EnemyGoldReward[enemy] = 5;
                store.QueueEnemyDeath(enemy, 0, 91L, store.GetEntityHandle(0));
                store.ResourceResolver.Events.BeforeBatchPublish = () =>
                {
                    store.ResourceResolver.Events.BeforeBatchPublish = null;
                    var filler = new GameplayEvent(GameplayEventType.AbilityRejected, default(EntityHandle), default(EntityHandle), 9001L);
                    while (store.ResourceResolver.Events.TryPublish(filler, true)) { }
                };

                store.ResolveEnemiesKilledThisFrame();

                Assert.True(store.EnemyActive[enemy]);
                Assert.True(store.IsEnemyPendingDeath(enemy));
                Assert.Equal(0, store.TotalKills);
                Assert.DoesNotContain(Enumerable.Range(0, store.DamageResolver.Events.Count)
                    .Select(i => store.DamageResolver.Events.Get(i).Type), type => type == GameplayEventType.KillConfirmed);
                store.BeginFrame();
                store.ResolveEnemiesKilledThisFrame();
                Assert.False(store.EnemyActive[enemy]);
                Assert.Equal(1, store.TotalKills);
            }
        }

        [Fact]
        public void ApplyPlayerDamageAuthority_SequencesAllocatedAtCommitAndStrictlyIncrease()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 100f, 1f, 1f, 1);
                store.PlayerMaxHealth[0] = 100f;
                store.PlayerCurrentHealth[0] = 100f;
                int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 5f, 1, 1);

                Assert.True(store.ApplyPlayerDamageAuthority(enemy, 0, 3f, out _));
                Assert.True(store.ApplyPlayerDamageAuthority(enemy, 0, 4f, out _));

                Assert.Equal(2, store.ResourceResolver.Events.Count);
                long first = store.ResourceResolver.Events.Get(0).Sequence;
                long second = store.ResourceResolver.Events.Get(1).Sequence;
                Assert.True(first > 0L);
                Assert.True(second > first);
            }
        }

        [Fact]
        public void ApplyPlayerDamageAuthority_AppliedIncludesShieldConsumption()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 100f, 1f, 1f, 1);
                store.PlayerMaxHealth[0] = 100f;
                store.PlayerCurrentHealth[0] = 80f;
                store.PlayerShield[0] = 10f;
                int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 5f, 1, 1);

                Assert.True(store.ApplyPlayerDamageAuthority(enemy, 0, 15f, out float applied));

                Assert.Equal(0f, store.PlayerShield[0], 3);
                Assert.Equal(75f, store.PlayerCurrentHealth[0], 3);
                Assert.Equal(15f, applied, 3);
            }
        }

        [Fact]
        public void ApplyPlayerDamageAuthority_CapacityExhaustedLeavesSixFieldsAndNoFacts()
        {
            using (var store = StoreWithFullResourceQueue())
            {
                store.PlayerMaxHealth[0] = 100f;
                store.PlayerCurrentHealth[0] = 40f;
                store.PlayerShield[0] = 5f;
                store.PlayerManaShield[0] = 3f;
                store.PlayerManaShieldTriggered[0] = false;
                store.PlayerReincarnationCharges[0] = 1;
                store.PlayerHasReincarnated[0] = false;
                int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 5f, 1, 1);
                int eventsBefore = store.ResourceResolver.Events.Count;
                long deathsBefore = store.DeathEnqueueCount;

                Assert.False(store.ApplyPlayerDamageAuthority(enemy, 0, 12f, out float applied));

                Assert.Equal(0f, applied);
                Assert.Equal(40f, store.PlayerCurrentHealth[0]);
                Assert.Equal(5f, store.PlayerShield[0]);
                Assert.Equal(3f, store.PlayerManaShield[0]);
                Assert.False(store.PlayerManaShieldTriggered[0]);
                Assert.Equal(1, store.PlayerReincarnationCharges[0]);
                Assert.False(store.PlayerHasReincarnated[0]);
                Assert.Equal(eventsBefore, store.ResourceResolver.Events.Count);
                Assert.Equal(deathsBefore, store.DeathEnqueueCount);
            }
        }

        [Fact]
        public void ApplyPlayerDamageAuthority_LethalPublishesTwoFactsAndRejectsFollowUp()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 100f, 1f, 1f, 1);
                store.PlayerMaxHealth[0] = 100f;
                store.PlayerCurrentHealth[0] = 8f;
                store.PlayerShield[0] = 0f;
                store.PlayerReincarnationCharges[0] = 0;
                int enemy = store.AddEnemy(0, 0, 1f, 10f, 10f, 5f, 1, 1);

                Assert.True(store.ApplyPlayerDamageAuthority(enemy, 0, 20f, out float applied));
                Assert.True(applied > 0f);
                Assert.Equal(2, store.ResourceResolver.Events.Count);
                Assert.Equal(GameplayEventType.DamageApplied, store.ResourceResolver.Events.Get(0).Type);
                Assert.Equal(GameplayEventType.DeathQueued, store.ResourceResolver.Events.Get(1).Type);
                Assert.True(store.PlayerCurrentHealth[0] <= 0f);

                Assert.False(store.ApplyPlayerDamageAuthority(enemy, 0, 1f, out float secondApplied));
                Assert.Equal(0f, secondApplied);
                Assert.Equal(2, store.ResourceResolver.Events.Count);
            }
        }

        [Fact]
        public void MassDeathOverEventCapacity_DrainsAcrossFramesWithoutDroppingKills()
        {
            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 100f, 1f, 1f, 1);
                int n = store.DamageResolver.Events.Capacity + 1;
                var ids = new int[n];
                for (int i = 0; i < n; i++)
                {
                    ids[i] = store.AddEnemy(0, 0, 1f, 1f, 1f, 1f, 0, 1);
                    store.QueueEnemyDeath(ids[i], 0);
                }

                store.ResolveEnemiesKilledThisFrame();

                Assert.Equal(store.DamageResolver.Events.Capacity, store.TotalKills);
                Assert.Equal(1, store.GetActiveEnemyCount());
                Assert.Equal(0, store.DamageResolver.EventPublicationFailures);

                store.BeginFrame();
                store.ResolveEnemiesKilledThisFrame();

                Assert.Equal(n, store.TotalKills);
                Assert.Equal(0, store.GetActiveEnemyCount());
                Assert.Equal(0, store.DamageResolver.EventPublicationFailures);
            }
        }
    }
}
