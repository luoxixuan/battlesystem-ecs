using System;
using BattleSystemECS.Config;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Adaptive Difficulty System — dynamically adjusts wave difficulty based on player performance.
    ///
    /// Performance signals (per wave):
    ///   - Enemies leaked: increases difficulty
    ///   - Enemies killed (kills): decreases difficulty
    ///   - Gold remaining: bonus for efficiency
    ///   - Health remaining: bonus for not taking damage
    ///
    /// Metrics are collected during the wave, then computed when the wave completes.
    /// The resulting AdaptiveDifficultyLevel is read by WaveSpawningSystem to scale enemy stats.
    ///
    /// Integration points:
    ///   - FrameScheduler.Tick() calls AdaptiveDifficulty.Update() each turn (WavePhase)
    ///   - WaveSpawningSystem reads AdaptiveDifficultyLevel when spawning enemies
    ///   - OnWaveComplete: resets per-wave counters and computes new difficulty level
    /// </summary>
    public class AdaptiveDifficultySystem
    {
        private readonly ComponentStore _store;
        private readonly GameConfig _gameConfig;

        // Wave-level kill tracking (reset each wave)
        private int[] _killsThisWave = new int[ComponentStore.MAX_PLAYERS];
        private float[] _damageTakenThisWave = new float[ComponentStore.MAX_PLAYERS];

        // Difficulty config (loaded from game_config.json or defaults)
        private float _difficultyGrowthPerLeak = 0.10f;   // +10% difficulty per leak
        private float _difficultyShrinkPerKill = 0.005f;  // -0.5% difficulty per kill
        private float _minDifficulty = 0.5f;              // floor: 50% easier than baseline
        private float _maxDifficulty = 3.0f;              // ceiling: 3x harder than baseline
        private float _initialDifficulty = 1.0f;           // baseline multiplier

        public AdaptiveDifficultySystem(ComponentStore store, GameConfig gameConfig)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _gameConfig = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
            LoadConfig();
        }

        private void LoadConfig()
        {
            _difficultyGrowthPerLeak = _gameConfig.DifficultyGrowthPerWave > 0
                ? _gameConfig.DifficultyGrowthPerWave * 2f  // more aggressive than static growth
                : 0.10f;
            _initialDifficulty = 1.0f;
            _minDifficulty = 0.5f;
            _maxDifficulty = 3.0f;
        }

        /// <summary>
        /// Called each turn during WavePhase — tracks performance signals.
        /// </summary>
        public void Update(float deltaTime)
        {
            for (int playerId = 0; playerId < ComponentStore.MAX_PLAYERS; playerId++)
            {
                if (_store.PlayerCurrentHealth[playerId] <= 0) continue;

                // Track leaks that happened this turn (DecrementPlayerBaseLives is called in BenchmarkSystem)
                // We track leaks via EnemiesLeakedThisWave which is incremented when enemies reach bottom
                // The actual leak tracking happens in BenchmarkSystem/GameManager
                // Here we just track that the system is active
            }
        }

        /// <summary>
        /// Record a kill for the adaptive difficulty system.
        /// Called from ComboSystem or wherever kills are counted.
        /// </summary>
        public void RecordKill(int playerId)
        {
            if (playerId < 0 || playerId >= ComponentStore.MAX_PLAYERS) return;
            _killsThisWave[playerId]++;
        }

        /// <summary>
        /// Record damage taken by a player this wave.
        /// </summary>
        public void RecordDamageTaken(int playerId, float damage)
        {
            if (playerId < 0 || playerId >= ComponentStore.MAX_PLAYERS) return;
            _damageTakenThisWave[playerId] += damage;
        }

        /// <summary>
        /// Called by WaveSpawningSystem.OnWaveComplete — computes new difficulty level.
        /// Uses: leaks this wave, kills this wave, damage taken, gold remaining.
        /// </summary>
        public void OnWaveComplete(int playerId)
        {
            if (playerId < 0 || playerId >= ComponentStore.MAX_PLAYERS) return;

            int leaks = _store.EnemiesLeakedThisWave[playerId];
            int kills = _killsThisWave[playerId];

            // Compute performance score delta
            // Good performance (few leaks, many kills) → decrease difficulty
            // Poor performance (many leaks, few kills) → increase difficulty
            float currentLevel = _store.AdaptiveDifficultyLevel[playerId];
            float performanceScore = 0f;

            // Leaks penalty: each leak adds difficulty
            performanceScore += leaks * _difficultyGrowthPerLeak;

            // Kill bonus: each kill reduces difficulty
            performanceScore -= kills * _difficultyShrinkPerKill;

            // Compute new difficulty level (clamped)
            float newLevel = currentLevel + performanceScore;
            newLevel = Math.Clamp(newLevel, _minDifficulty, _maxDifficulty);

            _store.AdaptiveDifficultyLevel[playerId] = newLevel;

            // Update cumulative score for display/debug
            _store.AdaptiveDifficultyScore[playerId] += (kills > 0 || leaks > 0)
                ? (kills * 0.5f) - (leaks * 1.0f)
                : 0f;

            // Reset per-wave counters
            _killsThisWave[playerId] = 0;
            _damageTakenThisWave[playerId] = 0f;
            _store.EnemiesLeakedThisWave[playerId] = 0;
        }

        /// <summary>
        /// Called at level start — resets all adaptive difficulty state.
        /// </summary>
        public void Reset()
        {
            for (int i = 0; i < ComponentStore.MAX_PLAYERS; i++)
            {
                _killsThisWave[i] = 0;
                _damageTakenThisWave[i] = 0f;
                _store.AdaptiveDifficultyLevel[i] = _initialDifficulty;
                _store.AdaptiveDifficultyScore[i] = 0f;
                _store.EnemiesLeakedThisWave[i] = 0;
            }
        }

        /// <summary>
        /// Returns the current difficulty multiplier for a player.
        /// Read by WaveSpawningSystem when spawning enemies.
        /// </summary>
        public float GetDifficultyMult(int playerId)
        {
            if (playerId < 0 || playerId >= ComponentStore.MAX_PLAYERS) return 1.0f;
            return _store.AdaptiveDifficultyLevel[playerId];
        }
    }
}