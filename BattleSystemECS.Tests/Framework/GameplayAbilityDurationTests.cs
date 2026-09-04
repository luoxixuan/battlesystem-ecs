using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class GameplayAbilityDurationTests
    {
        [Fact]
        public void InstantActivateKeepsPhaseNone()
        {
            var def = new GameplayAbilityDef("instant", "desc", 2f, 0f, -1, 10f,
                AbilityActivation.Instant, 0, 0);
            var inst = new AbilityInstance(def);
            Assert.Equal(AbilityDurationKind.Instant, inst.State.DurationKind);
            inst.Activate();
            Assert.Equal(AbilityPhase.None, inst.State.Phase);
            Assert.False(inst.TryBeginTimed(1f, ClockId.Combat));
            Assert.Equal(AbilityPhase.None, inst.State.Phase);
        }

        [Fact]
        public void TimedAbilityReachesCompleted()
        {
            var state = new AbilityState(new AbilityId(1), new EntityHandle(2, 1), 0f, 1, 1, AbilityDurationKind.Timed);
            Assert.True(state.TryBeginTimed(1.5f, ClockId.Combat, 0d));
            Assert.Equal(AbilityPhase.Executing, state.Phase);
            Assert.False(state.TryTickTimed(1.0d));
            Assert.Equal(AbilityPhase.Executing, state.Phase);
            Assert.True(state.TryCompleteTimed());
            Assert.Equal(AbilityPhase.Completed, state.Phase);
            Assert.False(state.TryCancelTimed());
            Assert.False(state.TryTickTimed(3d));
        }

        [Fact]
        public void TimedAbilityReachesCancelled()
        {
            var state = new AbilityState(new AbilityId(1), new EntityHandle(2, 1), 0f, 1, 1, AbilityDurationKind.Timed);
            Assert.True(state.TryBeginTimed(2f, ClockId.Enemy, 10d));
            Assert.Equal(AbilityPhase.Executing, state.Phase);
            Assert.True(state.TryCancelTimed());
            Assert.Equal(AbilityPhase.Cancelled, state.Phase);
            Assert.False(state.TryCompleteTimed());
        }

        [Fact]
        public void TimedAbilityExpiresOnVirtualTime()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 1f, 1f, 1f, 1);
            int owner = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            Assert.True(store.TryAddAbility(owner, new GameplayAbilityDef("channel", "", 0f, 0f, -1, 0f,
                AbilityActivation.Instant, 0, 0)));
            Assert.True(store.TryBeginTimedAbility(owner, 0, 1f, ClockId.Enemy));
            Assert.Equal(AbilityPhase.Executing, store.GetAbility(owner, 0).State.Phase);
            store.GameplayEffectsRuntime.Tick(0.4f, ClockId.Enemy);
            Assert.Equal(AbilityPhase.Executing, store.GetAbility(owner, 0).State.Phase);
            store.GameplayEffectsRuntime.Tick(1f, ClockId.Combat);
            Assert.Equal(AbilityPhase.Executing, store.GetAbility(owner, 0).State.Phase);
            store.GameplayEffectsRuntime.Tick(0.6f, ClockId.Enemy);
            Assert.Equal(AbilityPhase.Expired, store.GetAbility(owner, 0).State.Phase);
        }
    }
}
