using System;
using Xunit;
using BattleSystemECS.Tests.Infrastructure;
using BattleSystemECS.Core;

namespace BattleSystemECS.Tests.Framework
{
    /// <summary>
    /// State machine transition and callback invariants.
    /// </summary>
    public class StateMachineTests : BattleTestBase
    {
        // ══════════════════════════════════════════════════════════════
        //  Valid transitions
        // ══════════════════════════════════════════════════════════════

        // ── 合法 / 非法迁移矩阵：11 个原本几乎相同的 [Fact] 合并为单个 [Theory]，
        //    每个 InlineData case 保留各自的合法/非法断言。 ──
        [Theory(DisplayName = "IsValidTransition 合法/非法迁移矩阵")]
        [InlineData(GameState.Init, GameState.BuildPhase, true)]
        [InlineData(GameState.BuildPhase, GameState.WavePhase, true)]
        [InlineData(GameState.WavePhase, GameState.Intermission, true)]
        [InlineData(GameState.WavePhase, GameState.LevelComplete, true)]
        [InlineData(GameState.Intermission, GameState.WavePhase, true)]
        [InlineData(GameState.Intermission, GameState.BranchSelection, true)]
        [InlineData(GameState.BranchSelection, GameState.WavePhase, true)]
        [InlineData(GameState.LevelComplete, GameState.BuildPhase, true)]
        [InlineData(GameState.BuildPhase, GameState.Intermission, false)]
        [InlineData(GameState.Init, GameState.WavePhase, false)]
        [InlineData(GameState.WavePhase, GameState.BuildPhase, false)]
        public void IsValidTransition_Matrix(GameState from, GameState to, bool expected)
        {
            Assert.Equal(expected, StateMachine.IsValidTransition(from, to));
        }

        // ── Any-state 迁移改为遍历全部 GameState，不再抽样 2-3 个状态。 ──
        [Fact(DisplayName = "任意状态均可迁移到 GameOver")]
        public void AnyState_To_GameOver_Valid()
        {
            foreach (GameState state in Enum.GetValues<GameState>())
            {
                Assert.True(
                    StateMachine.IsValidTransition(state, GameState.GameOver),
                    $"{state} -> {GameState.GameOver} 应为合法迁移");
            }
        }

        [Fact(DisplayName = "任意状态均可迁移到 Victory")]
        public void AnyState_To_Victory_Valid()
        {
            foreach (GameState state in Enum.GetValues<GameState>())
            {
                Assert.True(
                    StateMachine.IsValidTransition(state, GameState.Victory),
                    $"{state} -> {GameState.Victory} 应为合法迁移");
            }
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
