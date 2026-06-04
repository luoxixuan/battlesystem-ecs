using System;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// DoomClock System — Round 110 Direction 10.
    /// A time-limited endless mode: a global countdown (e.g. 3 min) ticks down each
    /// frame while infinite waves spawn. The player wins when the timer hits 0 with
    /// the player still alive. Loses if player dies (lives=0) before the timer does.
    ///
    /// Final score formula (computed at win):
    ///   finalScore = wavesCleared * DoomClockWaveScore
    ///               + (int)(remainingTime * DoomClockTimeBonusPerSec)
    ///               + (int)(healthFraction * 100) * DoomClockHealthBonusPerPercent
    ///
    /// Wave cycling:
    ///   - Initial waves are taken from LevelConfig.DoomClockInitialWaves.
    ///   - When the pool is exhausted, DoomClockSystem cycles back to wave 0 and
    ///     increments DoomClockCycleCount. WaveSpawningSystem uses the cycle count
    ///     to scale enemy HP / damage by (DoomClockWaveScaling ^ cycle).
    ///
    /// Hot-path cost: O(1) per tick. Only the timer decrement runs unconditionally
    /// during WavePhase; the cycle / score logic only fires on wave completion.
    /// Enemies with no active DoomClock objective incur zero overhead.
    /// </summary>
    public class DoomClockSystem
    {
        private readonly ComponentStore _store;
        private readonly int _playerId;

        public DoomClockSystem(ComponentStore store, int playerId = 0)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _playerId = playerId;
        }

        /// <summary>
        /// Initialize DoomClock state for a new level. Called by ObjectiveSystem
        /// when ObjectiveType == DoomClock. Idempotent — safe to call once per level.
        /// </summary>
        public void InitializeFromLevel(Config.LevelConfig level)
        {
            _store.DoomClockTimer[_playerId] = level.DoomClockDuration;
            _store.DoomClockDuration[_playerId] = level.DoomClockDuration;
            _store.DoomClockWavesCleared[_playerId] = 0;
            _store.DoomClockCycleCount[_playerId] = 0;
            _store.DoomClockFinalScore[_playerId] = 0;
            _store.DoomClockActive[_playerId] = true;
        }

        /// <summary>
        /// Per-tick countdown. Only active during WavePhase (matches Timed mode
        /// behavior in ObjectiveSystem). Decrements the global timer; stops at 0.
        ///
        /// When the timer hits 0 mid-wave, the run still continues until the
        /// current wave is cleared (or player dies) — matches "Survive the clock
        /// AND the current wave" semantics. The final score is only computed
        /// when the objective check fires.
        /// </summary>
        public void Update(float deltaTime, GameState phase)
        {
            // Zero-overhead fast path: inactive / non-WavePhase tick.
            if (!_store.DoomClockActive[_playerId]) return;
            if (phase != GameState.WavePhase) return;

            float t = _store.DoomClockTimer[_playerId];
            if (t <= 0f) return;  // already expired, wait for objective check

            t -= deltaTime;
            if (t < 0f) t = 0f;
            _store.DoomClockTimer[_playerId] = t;
        }

        /// <summary>
        /// Called when a wave is fully cleared. Increments the cleared-wave counter
        /// and advances the cycle count when wrapping back to wave 0.
        ///
        /// Returns the new cycle count (for the spawner to apply scaling).
        /// Returns -1 if DoomClock is not active (caller should fall back to default).
        /// </summary>
        public int OnWaveCleared()
        {
            if (!_store.DoomClockActive[_playerId]) return -1;

            _store.DoomClockWavesCleared[_playerId]++;
            // Cycle count is updated lazily by GetCurrentCycle() when the spawner
            // asks for the wave index. Here we just expose the cleared count.
            return _store.DoomClockCycleCount[_playerId];
        }

        /// <summary>
        /// Compute the effective wave template index (0-based) and the current cycle
        /// count, given the size of the DoomClockInitialWaves pool. The cycle is
        /// bumped automatically when the index wraps back to 0.
        ///
        /// Returns (waveTemplateIndex, cycleCount). If pool is empty, returns
        /// (currentWaveIndex % fallbackPoolSize, 0) where fallbackPoolSize is
        /// derived from level.Waves (so legacy levels still work).
        /// </summary>
        public (int Index, int Cycle) GetCurrentWaveSlot(int currentWaveIndex, int poolSize)
        {
            if (poolSize <= 0) return (0, 0);

            int cycle = currentWaveIndex / poolSize;
            int idx = currentWaveIndex % poolSize;

            // Sync the cycle counter (so OnWaveCleared can return the right value).
            _store.DoomClockCycleCount[_playerId] = cycle;

            return (idx, cycle);
        }

        /// <summary>
        /// Compute the enemy's stat multiplier for the current cycle.
        /// multiplier = DoomClockWaveScaling ^ cycleCount.
        /// At cycle 0 multiplier = 1.0 (no scaling).
        /// At cycle 1 (default 1.1) multiplier = 1.1.
        /// At cycle 5 (default 1.1) multiplier ≈ 1.61.
        /// </summary>
        public float GetCycleScalingMultiplier(float waveScaling)
        {
            int cycle = _store.DoomClockCycleCount[_playerId];
            if (cycle <= 0) return 1f;
            if (waveScaling <= 1f) return 1f;
            return MathF.Pow(waveScaling, cycle);
        }

        /// <summary>
        /// Compute the final score at the end of a successful run.
        /// finalScore = wavesCleared * waveScore
        ///             + (int)(remainingTime * timeBonusPerSec)
        ///             + (int)(healthFraction * 100) * healthBonusPerPercent
        ///
        /// Storing the result in DoomClockFinalScore[playerId] so the UI / log
        /// can read it after the run ends. Caller must have already set
        /// DoomClockActive = false before invoking.
        /// </summary>
        public int ComputeFinalScore(Config.LevelConfig level, float playerHealthFraction)
        {
            if (playerHealthFraction < 0f) playerHealthFraction = 0f;
            if (playerHealthFraction > 1f) playerHealthFraction = 1f;

            int waveBonus = _store.DoomClockWavesCleared[_playerId] * level.DoomClockWaveScore;
            int timeBonus = (int)(_store.DoomClockTimer[_playerId] * level.DoomClockTimeBonusPerSec);
            int healthBonus = (int)(playerHealthFraction * 100f) * level.DoomClockHealthBonusPerPercent;
            int finalScore = waveBonus + timeBonus + healthBonus;

            _store.DoomClockFinalScore[_playerId] = finalScore;
            return finalScore;
        }

        /// <summary>
        /// End the run (called by ObjectiveSystem.CheckObjective on win or lose).
        /// On win, computes and stores the final score. On lose, leaves the score
        /// at 0 (lobby leaderboard convention).
        /// </summary>
        public void EndRun(bool won, Config.LevelConfig level, float playerHealthFraction)
        {
            if (!_store.DoomClockActive[_playerId]) return;
            _store.DoomClockActive[_playerId] = false;

            if (won)
            {
                ComputeFinalScore(level, playerHealthFraction);
            }
            else
            {
                _store.DoomClockFinalScore[_playerId] = 0;
            }
        }

        /// <summary>
        /// Formatted status string for HUD / log output.
        /// </summary>
        public string GetStatus()
        {
            if (!_store.DoomClockActive[_playerId])
            {
                int final = _store.DoomClockFinalScore[_playerId];
                return final > 0
                    ? $"[DOOM CLOCK] FINAL SCORE: {final}"
                    : "[DOOM CLOCK] (ended)";
            }

            float t = _store.DoomClockTimer[_playerId];
            int waves = _store.DoomClockWavesCleared[_playerId];
            int cycle = _store.DoomClockCycleCount[_playerId];
            return $"[DOOM CLOCK] Time: {t:F1}s | Waves: {waves} | Cycle: {cycle}";
        }
    }
}
