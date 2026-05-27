using System;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Objective System — manages alternate win/lose conditions per level.
    /// 
    /// Supports:
    ///   - KillAll (default): win when all waves cleared
    ///   - Escort: protect an NPC that moves toward the exit; lose if NPC dies
    ///   - Survival: survive N waves (no kill requirement)
    ///   - Timed: eliminate all enemies within a time limit
    ///   - Endless: survive as many waves as possible; score = waves cleared
    /// 
    /// Phase gates:
    ///   BuildPhase: update escort NPC movement, timer ticking
    ///   WavePhase:  update objective progress, check win/lose conditions
    /// </summary>
    public class ObjectiveSystem
    {
        private readonly ComponentStore _store;
        private readonly int _playerId;

        public ObjectiveSystem(ComponentStore store, int playerId = 0)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _playerId = playerId;
        }

        /// <summary>
        /// Initialize objective state from level config. Called once per level load.
        /// </summary>
        public void InitializeFromLevel(Config.LevelConfig level, int mapHeight)
        {
            _store.CurrentObjectiveType[_playerId] = level.ObjectiveType;

            if (level.ObjectiveType == (int)ObjectiveType.Escort)
            {
                // Spawn escort NPC at start position (x=0, halfway up the map)
                _store.EscortNpcX[_playerId] = 0f;
                _store.EscortNpcY[_playerId] = mapHeight / 2f;
                _store.EscortNpcMaxHealth[_playerId] = level.EscortNpcMaxHealth;
                _store.EscortNpcHealth[_playerId] = level.EscortNpcMaxHealth;
                _store.EscortNpcSpeed[_playerId] = level.EscortNpcSpeed;
                _store.EscortNpcActive[_playerId] = true;
            }
            else
            {
                _store.EscortNpcActive[_playerId] = false;
            }

            if (level.ObjectiveType == (int)ObjectiveType.Timed)
            {
                _store.ObjectiveTimer[_playerId] = level.ObjectiveTimeLimit;
                _store.ObjectiveTimeLimit[_playerId] = level.ObjectiveTimeLimit;
            }
            else
            {
                _store.ObjectiveTimer[_playerId] = 0f;
            }

            if (level.ObjectiveType == (int)ObjectiveType.Survival)
            {
                _store.ObjectiveWavesRemaining[_playerId] = level.SurvivalWaveCount;
            }
            else
            {
                _store.ObjectiveWavesRemaining[_playerId] = 0;
            }

            // Reset score tracking
            _store.ObjectiveWaveScore[_playerId] = 0;
            _store.ObjectiveHealthScore[_playerId] = 0f;
        }

        /// <summary>
        /// Per-frame tick — update escort movement and objective timers.
        /// Called every frame regardless of phase (movement is real-time).
        /// </summary>
        public void Update(float deltaTime, GameState phase)
        {
            var objType = (ObjectiveType)_store.CurrentObjectiveType[_playerId];

            if (objType == ObjectiveType.Escort)
            {
                UpdateEscort(deltaTime);
            }

            if (objType == ObjectiveType.Timed && phase == GameState.WavePhase)
            {
                UpdateTimed(deltaTime);
            }
        }

        /// <summary>
        /// WavePhase-specific update — check conditions and advance progress.
        /// </summary>
        public void UpdateWavePhase(float deltaTime, int currentWave, int totalWaves)
        {
            var objType = (ObjectiveType)_store.CurrentObjectiveType[_playerId];

            if (objType == ObjectiveType.Survival)
            {
                // Survival: don't care about kills, just survive the required wave count.
                // Wave complete notification comes from WaveSpawningSystem.
            }

            if (objType == ObjectiveType.Endless)
            {
                // Track current wave for scoring
                _store.ObjectiveWaveScore[_playerId] = currentWave;
            }
        }

        /// <summary>
        /// Called when a wave is completed (all enemies killed or timeout).
        /// Returns true if the game should continue, false if objective is satisfied.
        /// </summary>
        public bool OnWaveCompleted(int wavesRemaining)
        {
            var objType = (ObjectiveType)_store.CurrentObjectiveType[_playerId];

            if (objType == ObjectiveType.Survival)
            {
                _store.ObjectiveWavesRemaining[_playerId] = wavesRemaining;
                if (wavesRemaining <= 0)
                {
                    // Player survived all required waves — objective complete!
                    return false;
                }
            }

            return true; // continue
        }

        /// <summary>
        /// Called when an escort NPC takes damage.
        /// </summary>
        public void DamageEscortNpc(float damage)
        {
            if (!_store.EscortNpcActive[_playerId]) return;

            _store.EscortNpcHealth[_playerId] -= damage;
            if (_store.EscortNpcHealth[_playerId] <= 0f)
            {
                _store.EscortNpcHealth[_playerId] = 0f;
                _store.EscortNpcActive[_playerId] = false;
            }
        }

        /// <summary>
        /// Check if the current objective has been won or lost.
        /// Returns: 0 = ongoing, 1 = won, -1 = lost
        /// </summary>
        public int CheckObjective(int activeEnemyCount, int currentWave, int totalWaves)
        {
            var objType = (ObjectiveType)_store.CurrentObjectiveType[_playerId];

            switch (objType)
            {
                case ObjectiveType.KillAll:
                    // Win when all waves done and no enemies remain
                    if (currentWave > totalWaves && activeEnemyCount == 0)
                        return 1;
                    break;

                case ObjectiveType.Escort:
                    // Lose if NPC dies
                    if (!_store.EscortNpcActive[_playerId])
                        return -1;
                    // Win if NPC reaches far side of map (x >= map width)
                    // We don't have map width here — check health is > 0 and wave complete
                    if (_store.EscortNpcActive[_playerId] && currentWave > totalWaves && activeEnemyCount == 0)
                        return 1;
                    break;

                case ObjectiveType.Survival:
                    // Win when all survival waves cleared (no kill requirement)
                    if (currentWave > totalWaves && activeEnemyCount == 0)
                        return 1;
                    break;

                case ObjectiveType.Timed:
                    // Win when timer expires with enemies remaining
                    // Lose if all enemies killed before timer expires
                    if (activeEnemyCount == 0)
                        return 1; // cleared early = win
                    if (_store.ObjectiveTimer[_playerId] <= 0f && activeEnemyCount > 0)
                        return -1; // time ran out with enemies still alive
                    break;

                case ObjectiveType.Endless:
                    // No win condition — runs until player loses (lives = 0 or escort dies)
                    // Score is tracked in ObjectiveWaveScore
                    break;
            }

            return 0; // ongoing
        }

        /// <summary>
        /// Get current objective progress as a formatted string for UI.
        /// </summary>
        public string GetObjectiveStatus()
        {
            var objType = (ObjectiveType)_store.CurrentObjectiveType[_playerId];

            switch (objType)
            {
                case ObjectiveType.Escort:
                    var hp = _store.EscortNpcHealth[_playerId];
                    var maxHp = _store.EscortNpcMaxHealth[_playerId];
                    var active = _store.EscortNpcActive[_playerId] ? "ALIVE" : "DEAD";
                    return $"[ESCORT] NPC HP: {hp:F0}/{maxHp:F0} ({active})";

                case ObjectiveType.Survival:
                    var waves = _store.ObjectiveWavesRemaining[_playerId];
                    return $"[SURVIVAL] Waves remaining: {waves}";

                case ObjectiveType.Timed:
                    var timer = _store.ObjectiveTimer[_playerId];
                    return $"[TIMED] Time remaining: {timer:F1}s";

                case ObjectiveType.Endless:
                    var score = _store.ObjectiveWaveScore[_playerId];
                    return $"[ENDLESS] Waves survived: {score}";

                default:
                    return "[KILLALL] Defeat all enemies";
            }
        }

        // ── Private helpers ─────────────────────────────────────────────────────

        private void UpdateEscort(float deltaTime)
        {
            if (!_store.EscortNpcActive[_playerId]) return;

            // Move NPC rightward at constant speed
            // Map width is not stored in store — we approximate with a large constant
            // The escort is treated as having reached the goal when the wave is cleared
            _store.EscortNpcX[_playerId] += _store.EscortNpcSpeed[_playerId] * deltaTime;
        }

        private void UpdateTimed(float deltaTime)
        {
            if (_store.ObjectiveTimer[_playerId] > 0f)
            {
                _store.ObjectiveTimer[_playerId] -= deltaTime;
                if (_store.ObjectiveTimer[_playerId] < 0f)
                    _store.ObjectiveTimer[_playerId] = 0f;
            }
        }
    }
}