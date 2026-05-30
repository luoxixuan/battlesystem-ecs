using Xunit;
using BattleSystemECS.Core;

namespace BattleSystemECS.Tests
{
    /// <summary>
    /// State machine transition and callback invariants.
    /// </summary>
    public class StateMachineTests
    {
        // ══════════════════════════════════════════════════════════════
        //  Valid transitions
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void Init_To_BuildPhase_Valid()
        {
            Assert.True(StateMachine.IsValidTransition(GameState.Init, GameState.BuildPhase));
        }

        [Fact]
        public void BuildPhase_To_WavePhase_Valid()
        {
            Assert.True(StateMachine.IsValidTransition(GameState.BuildPhase, GameState.WavePhase));
        }

        [Fact]
        public void WavePhase_To_Intermission_Valid()
        {
            Assert.True(StateMachine.IsValidTransition(GameState.WavePhase, GameState.Intermission));
        }

        [Fact]
        public void WavePhase_To_LevelComplete_Valid()
        {
            Assert.True(StateMachine.IsValidTransition(GameState.WavePhase, GameState.LevelComplete));
        }

        [Fact]
        public void Intermission_To_WavePhase_Valid()
        {
            Assert.True(StateMachine.IsValidTransition(GameState.Intermission, GameState.WavePhase));
        }

        [Fact]
        public void Intermission_To_BranchSelection_Valid()
        {
            Assert.True(StateMachine.IsValidTransition(GameState.Intermission, GameState.BranchSelection));
        }

        [Fact]
        public void BranchSelection_To_WavePhase_Valid()
        {
            Assert.True(StateMachine.IsValidTransition(GameState.BranchSelection, GameState.WavePhase));
        }

        [Fact]
        public void LevelComplete_To_BuildPhase_Valid()
        {
            Assert.True(StateMachine.IsValidTransition(GameState.LevelComplete, GameState.BuildPhase));
        }

        [Fact]
        public void AnyState_To_GameOver_Valid()
        {
            Assert.True(StateMachine.IsValidTransition(GameState.BuildPhase, GameState.GameOver));
            Assert.True(StateMachine.IsValidTransition(GameState.WavePhase, GameState.GameOver));
            Assert.True(StateMachine.IsValidTransition(GameState.Init, GameState.GameOver));
        }

        [Fact]
        public void AnyState_To_Victory_Valid()
        {
            Assert.True(StateMachine.IsValidTransition(GameState.WavePhase, GameState.Victory));
            Assert.True(StateMachine.IsValidTransition(GameState.BuildPhase, GameState.Victory));
        }

        // ══════════════════════════════════════════════════════════════
        //  Invalid transitions
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void BuildPhase_To_Intermission_Invalid()
        {
            Assert.False(StateMachine.IsValidTransition(GameState.BuildPhase, GameState.Intermission));
        }

        [Fact]
        public void Init_To_WavePhase_Invalid()
        {
            Assert.False(StateMachine.IsValidTransition(GameState.Init, GameState.WavePhase));
        }

        [Fact]
        public void WavePhase_To_BuildPhase_Invalid()
        {
            Assert.False(StateMachine.IsValidTransition(GameState.WavePhase, GameState.BuildPhase));
        }

        // ══════════════════════════════════════════════════════════════
        //  Transition execution
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void TransitionTo_Valid_ReturnsTrue()
        {
            var sm = new StateMachine();
            bool ok = sm.TransitionTo(GameState.BuildPhase);
            Assert.True(ok);
            Assert.Equal(GameState.BuildPhase, sm.CurrentState);
        }

        [Fact]
        public void TransitionTo_Invalid_ReturnsFalse()
        {
            var sm = new StateMachine();
            bool ok = sm.TransitionTo(GameState.WavePhase); // Init → WavePhase invalid
            Assert.False(ok);
            Assert.Equal(GameState.Init, sm.CurrentState);
        }

        [Fact]
        public void TransitionTo_Chain()
        {
            var sm = new StateMachine();
            Assert.True(sm.TransitionTo(GameState.BuildPhase));
            Assert.True(sm.TransitionTo(GameState.WavePhase));
            Assert.True(sm.TransitionTo(GameState.Intermission));
            Assert.True(sm.TransitionTo(GameState.WavePhase));
            Assert.True(sm.TransitionTo(GameState.LevelComplete));
            Assert.True(sm.TransitionTo(GameState.BuildPhase));
        }

        // ══════════════════════════════════════════════════════════════
        //  OnEnter / OnExit callbacks
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void OnEnter_FiresOnValidTransition()
        {
            var sm = new StateMachine();
            int counter = 0;
            sm.OnEnter(GameState.BuildPhase, () => counter++);

            sm.TransitionTo(GameState.BuildPhase);
            Assert.Equal(1, counter);
        }

        [Fact]
        public void OnEnter_DoesNotFireOnInvalidTransition()
        {
            var sm = new StateMachine();
            int counter = 0;
            sm.OnEnter(GameState.WavePhase, () => counter++);

            sm.TransitionTo(GameState.WavePhase); // invalid from Init
            Assert.Equal(0, counter);
        }

        [Fact]
        public void OnExit_FiresWhenLeavingState()
        {
            var sm = new StateMachine();
            sm.TransitionTo(GameState.BuildPhase); // Init → BuildPhase

            int exited = 0;
            sm.OnExit(GameState.BuildPhase, () => exited++);

            sm.TransitionTo(GameState.WavePhase);
            Assert.Equal(1, exited);
        }

        [Fact]
        public void OnEnter_MultipleCallbacks_AllFire()
        {
            var sm = new StateMachine();
            int a = 0, b = 0;
            sm.OnEnter(GameState.BuildPhase, () => a++);
            sm.OnEnter(GameState.BuildPhase, () => b++);

            sm.TransitionTo(GameState.BuildPhase);
            Assert.Equal(1, a);
            Assert.Equal(1, b);
        }

        [Fact]
        public void OnEnter_CallbackException_DoesNotCrash()
        {
            var sm = new StateMachine();
            sm.OnEnter(GameState.BuildPhase, () => throw new System.Exception("test crash"));

            bool ok = sm.TransitionTo(GameState.BuildPhase);
            Assert.True(ok); // transition still succeeds despite callback error
            Assert.Equal(GameState.BuildPhase, sm.CurrentState);
        }
    }
}
