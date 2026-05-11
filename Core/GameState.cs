using System;
using System.Collections.Generic;

namespace BattleSystemECS.Core
{
    /// <summary>
    /// Game state machine.
    /// Replaces ad-hoc boolean flags with well-defined states and transitions.
    /// </summary>
    public enum GameState
    {
        Init,           // Game initializing (loading config, creating entities)
        BuildPhase,     // Player places/upgrades towers
        WavePhase,      // Active combat — waves spawning and fighting
        Intermission,   // Between waves (brief pause / info display)
        LevelComplete,  // All waves in current level cleared
        GameOver,       // Player defeated
        Victory         // All levels cleared
    }

    /// <summary>
    /// State machine controller — manages transitions and notifies listeners.
    /// </summary>
    public class StateMachine
    {
        public GameState CurrentState { get; private set; } = GameState.Init;

        // State enter/exit callbacks
        private readonly Dictionary<GameState, List<Action>> onEnter = new Dictionary<GameState, List<Action>>();
        private readonly Dictionary<GameState, List<Action>> onExit = new Dictionary<GameState, List<Action>>();

        /// <summary>
        /// Attempt to transition to a new state.
        /// Returns true if the transition is valid and was performed.
        /// </summary>
        public bool TransitionTo(GameState newState)
        {
            if (!IsValidTransition(CurrentState, newState))
            {
                Console.Error.WriteLine(
                    $"[StateMachine] Invalid transition: {CurrentState} -> {newState}");
                return false;
            }

            // Exit old state
            FireCallbacks(onExit, CurrentState);

            var oldState = CurrentState;
            CurrentState = newState;

            // Enter new state
            FireCallbacks(onEnter, newState);

            return true;
        }

        /// <summary>
        /// Register a callback for when a state is entered.
        /// </summary>
        public void OnEnter(GameState state, Action callback)
        {
            if (!onEnter.ContainsKey(state))
                onEnter[state] = new List<Action>();
            onEnter[state].Add(callback);
        }

        /// <summary>
        /// Register a callback for when a state is exited.
        /// </summary>
        public void OnExit(GameState state, Action callback)
        {
            if (!onExit.ContainsKey(state))
                onExit[state] = new List<Action>();
            onExit[state].Add(callback);
        }

        /// <summary>
        /// Check if a given transition is valid.
        /// </summary>
        public static bool IsValidTransition(GameState from, GameState to)
        {
            // Any state can go to GameOver or Victory
            if (to == GameState.GameOver || to == GameState.Victory)
                return true;

            switch (from)
            {
                case GameState.Init:
                    return to == GameState.BuildPhase;

                case GameState.BuildPhase:
                    return to == GameState.WavePhase;

                case GameState.WavePhase:
                    return to == GameState.Intermission
                        || to == GameState.LevelComplete;

                case GameState.Intermission:
                    return to == GameState.WavePhase;

                case GameState.LevelComplete:
                    return to == GameState.BuildPhase;

                default:
                    return false;
            }
        }

        private static void FireCallbacks(Dictionary<GameState, List<Action>> dict, GameState state)
        {
            if (dict.TryGetValue(state, out var list))
            {
                foreach (var cb in list)
                {
                    try { cb(); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(
                            $"[StateMachine] Callback error in {state}: {ex.Message}");
                    }
                }
            }
        }
    }
}
