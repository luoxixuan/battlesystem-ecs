using BattleSystemECS.Core.GAS;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class EffectRuntimeStateTests
    {
        [Fact]
        public void GameplayEffectDefinitionExposesOnlyReadonlyFields()
        {
            Assert.All(typeof(GameplayEffectDefinition).GetFields(), field => Assert.True(field.IsInitOnly, field.Name));
        }

        [Fact]
        public void SharedDefinitionCreatesIndependentRuntimeClocks()
        {
            var definition = GameplayEffectDef.Periodic("shared", -1, 2f, 5f, 1f);
            var source = new EntityHandle(1, 1);
            var firstApplication = LegacyEffectAdapter.CreateApplication(definition, source, new EntityHandle(2, 1));
            var secondApplication = LegacyEffectAdapter.CreateApplication(definition, source, new EntityHandle(3, 1));
            var effects = new ActiveGameplayEffectStore(2);
            Assert.True(effects.TryAdd(firstApplication, out var firstHandle));
            Assert.True(effects.TryAdd(secondApplication, out var secondHandle));

            Assert.True(effects.TryGet(firstHandle, out var first, out var immutable, out _));
            first.RemainingTime = 1f;
            first.TicksRemaining = 1;
            Assert.True(effects.TryUpdate(firstHandle, first));

            Assert.True(effects.TryGet(secondHandle, out var second, out _, out _));
            Assert.Equal(5f, immutable.Duration);
            Assert.Equal(5f, second.RemainingTime);
            Assert.Equal(5, second.TicksRemaining);
        }

        [Fact]
        public void LegacyProjectionStateDoesNotMutateStaticDefinition()
        {
            var definition = GameplayEffectDef.Periodic("runtime", -1, 3f, 4f, 1f);
            var active = new AppliedEffect(definition, 7);

            active.RemainingTime -= 1f;
            active.TicksRemaining--;

            Assert.Equal(4f, definition.Duration);
            Assert.Equal(4, definition.TotalTicks);
            Assert.Equal(3f, active.RemainingTime);
            Assert.Equal(3, active.TicksRemaining);
        }

        [Fact]
        public void NewInstanceDoesNotInheritPreviousRuntimeState()
        {
            var definition = GameplayEffectDef.Periodic("recycle", -1, 1f, 2f, 1f);
            var first = new AppliedEffect(definition, 1);
            first.RemainingTime = 0f;
            first.TicksRemaining = 0;

            var second = new AppliedEffect(definition, 1);

            Assert.Equal(2f, second.RemainingTime);
            Assert.Equal(2, second.TicksRemaining);
        }

        [Fact]
        public void EffectHandleIsRejectedAfterTargetRecycle()
        {
            var store = new ComponentStore();
            int source = store.AddEnemy(0, 0, 1, 10, 10, 1, 1, 1);
            int target = store.AddEnemy(0, 0, 1, 10, 10, 1, 1, 1);
            var effect = LegacyEffectAdapter.ToRuntime(GameplayEffectDef.Periodic("handle", -1, 1f, 2f, 1f), source, target);
            Assert.True(store.TryAddEffect(target, effect));
            var stored = store.GetEffect(target, 0);
            var targetHandle = store.GetEntityHandle(target);
            Assert.True(store.TryGetEffect(targetHandle, stored.Handle, out _));

            store.DestroyEntity(target);

            Assert.False(store.TryGetEffect(targetHandle, stored.Handle, out _));
            Assert.False(store.GameplayEffectPool.TryResolve(stored.Handle, out _));
        }

        [Fact]
        public void SourceDeathRemovePolicyExpiresBeforeTick()
        {
            var store = new ComponentStore();
            int source = store.AddEnemy(0, 0, 1, 10, 10, 1, 1, 1);
            int target = store.AddEnemy(0, 0, 1, 10, 10, 1, 1, 1);
            var effect = LegacyEffectAdapter.ToRuntime(GameplayEffectDef.Periodic("source", -1, 1f, 2f, 1f), source, target);
            effect.SourceDeath = SourceDeathPolicy.Remove;
            Assert.True(store.TryAddEffect(target, effect));
            store.DestroyEntity(source);

            new BuffSystem(store, 0).Update(1f);

            Assert.Equal(0, store.GetEffectCount(target));
        }

        [Fact]
        public void SourceDeathPersistPolicyKeepsEffectUntilItsOwnExpiry()
        {
            var store = new ComponentStore();
            int source = store.AddEnemy(0, 0, 1, 10, 10, 1, 1, 1);
            int target = store.AddEnemy(0, 0, 1, 10, 10, 1, 1, 1);
            var application = LegacyEffectAdapter.CreateApplication(
                GameplayEffectDef.Periodic("persist", -1, 1f, 2f, 1f),
                store.GetEntityHandle(source), store.GetEntityHandle(target));
            Assert.True(store.TryAddGameplayEffect(target, application, out _));
            store.DestroyEntity(source);

            new BuffSystem(store, 0).Update(0.5f);

            Assert.Equal(1, store.GetEffectCount(target));
            Assert.True(store.TryGetActiveEffectAt(target, 0, out var active, out _, out _));
            Assert.Equal(1.5f, active.RemainingTime);
        }

        [Fact]
        public void FirstTickAndCatchUpPoliciesProduceDeclaredTickCounts()
        {
            var store = new ComponentStore();
            int immediate = store.AddEnemy(0, 0, 1, 10, 10, 1, 1, 1);
            int catchAll = store.AddEnemy(0, 0, 1, 10, 10, 1, 1, 1);
            int onePerFrame = store.AddEnemy(0, 0, 1, 10, 10, 1, 1, 1);
            int skipMissed = store.AddEnemy(0, 0, 1, 10, 10, 1, 1, 1);
            AddWithPolicies(store, immediate, FirstTickPolicy.Immediate, CatchUpPolicy.OnePerFrame);
            AddWithPolicies(store, catchAll, FirstTickPolicy.NextInterval, CatchUpPolicy.CatchUpAll);
            AddWithPolicies(store, onePerFrame, FirstTickPolicy.NextInterval, CatchUpPolicy.OnePerFrame);
            AddWithPolicies(store, skipMissed, FirstTickPolicy.NextInterval, CatchUpPolicy.SkipMissed);
            var system = new BuffSystem(store, 0);

            system.Update(0f);
            system.ResolveDotDamage();
            Assert.Equal(9f, store.EnemyHealth[immediate]);

            system.Update(2.5f);
            system.ResolveDotDamage();
            Assert.Equal(8f, store.EnemyHealth[catchAll]);
            Assert.Equal(9f, store.EnemyHealth[onePerFrame]);
            Assert.Equal(9f, store.EnemyHealth[skipMissed]);
        }

        [Fact]
        public void StackRefreshUpdatesOnlyTypedRuntimeState()
        {
            var store = new ComponentStore();
            int target = store.AddEnemy(0, 0, 1, 20, 20, 1, 1, 1);
            var definition = GameplayEffectDef.Periodic("stack-refresh", -1, 1f, 4f, 1f,
                StackingBehavior.MaxStacksRefresh, 3);
            var system = new BuffSystem(store, 0);
            system.ApplyDot(target, definition);
            system.Update(0.5f);
            system.ApplyDot(target, definition);

            Assert.True(store.TryGetActiveEffectAt(target, 0, out var active, out var immutable, out _));
            Assert.Equal(2, active.StackCount);
            Assert.Equal(4f, active.RemainingTime);
            Assert.Equal(4, active.TicksRemaining);
            Assert.Equal(0f, active.TickAccumulator);
            Assert.Equal(4f, immutable.Duration);
        }

        [Fact]
        public void LegacyFacadeProjectsAndUpdatesTypedRuntimeWithoutOwningDefinition()
        {
            var store = new ComponentStore();
            int target = store.AddEnemy(0, 0, 1, 10, 10, 1, 1, 1);
            var definition = GameplayEffectDef.Periodic("projection", -1, 1f, 3f, 1f);
            Assert.True(store.TryAddGameplayEffect(target, LegacyEffectAdapter.CreateApplication(definition,
                store.GetEntityHandle(target), store.GetEntityHandle(target)), out _));

            var projection = store.GetEffect(target, 0);
            projection.RemainingTime = 1.25f;
            projection.TicksRemaining = 1;
            store.SetEffect(target, 0, projection);

            Assert.True(store.TryGetActiveEffectAt(target, 0, out var active, out var immutable, out _));
            Assert.Equal(1.25f, active.RemainingTime);
            Assert.Equal(1, active.TicksRemaining);
            Assert.Equal(3f, immutable.Duration);
            Assert.Equal(1.25f, store.GetEffect(target, 0).Definition.RemainingTime);
        }

        [Fact]
        public void ParallelReadersObserveStableRuntimeWithoutSharedWrites()
        {
            var effects = new ActiveGameplayEffectStore(1);
            var application = LegacyEffectAdapter.CreateApplication(
                GameplayEffectDef.Periodic("parallel-read", -1, 2f, 3f, 1f),
                new EntityHandle(1, 1), new EntityHandle(2, 1));
            Assert.True(effects.TryAdd(application, out var handle));
            var remaining = new float[256];

            Parallel.For(0, remaining.Length, i =>
            {
                Assert.True(effects.TryGet(handle, out var active, out _, out _));
                remaining[i] = active.RemainingTime;
            });

            Assert.All(remaining, value => Assert.Equal(3f, value));
            Assert.Equal(0, effects.Handles.InvalidResolveCount);
            Assert.Equal(0, effects.Handles.StaleResolveCount);
            Assert.Equal(0, effects.Handles.InactiveResolveCount);
        }

        private static void AddWithPolicies(ComponentStore store, int targetId, FirstTickPolicy firstTick, CatchUpPolicy catchUp)
        {
            var application = LegacyEffectAdapter.CreateApplication(
                GameplayEffectDef.Periodic("policy-" + targetId, -1, 1f, 10f, 1f),
                default(EntityHandle), store.GetEntityHandle(targetId));
            var runtime = application.Runtime;
            runtime.FirstTick = firstTick;
            runtime.CatchUp = catchUp;
            application = new GameplayEffectApplication(application.Definition, application.LegacySnapshot, runtime);
            Assert.True(store.TryAddGameplayEffect(targetId, application, out _));
        }

        [Fact]
        public void RemovingFirstSlotPreservesSecondHandleAndRejectsRemovedHandle()
        {
            var store = new ComponentStore();
            int target = store.AddEnemy(0, 0, 1, 10, 10, 1, 1, 1);
            var def = GameplayEffectDef.Periodic("swap", -1, 1f, 3f, 1f);
            Assert.True(store.TryAddEffect(target, LegacyEffectAdapter.ToRuntime(def, target, target)));
            Assert.True(store.TryAddEffect(target, LegacyEffectAdapter.ToRuntime(def, target, target)));
            var first = store.GetEffect(target, 0);
            var second = store.GetEffect(target, 1);
            var targetHandle = store.GetEntityHandle(target);

            Assert.True(store.TryRemoveEffect(targetHandle, first.Handle, out _));
            Assert.False(store.TryGetEffect(targetHandle, first.Handle, out _));
            Assert.True(store.TryGetEffect(targetHandle, second.Handle, out var shifted));
            Assert.Equal(second.Handle, shifted.Handle);
        }
    }
}
