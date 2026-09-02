using System;
using BattleSystemECS.Config;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Day/Night cycle system — global environmental phase that alternates
    /// between Day (tower buffs) and Night (enemy buffs).
    /// 
    /// Day effects:   Tower range +20%, enemy speed +10%
    /// Night effects: Tower range -30%, enemy damage +15%
    /// 
    /// Integration points:
    ///   - FrameScheduler.Tick() calls DayNight.Update(deltaTime) each turn
    ///   - EnemyMovementSystem reads DayNight phase for speed modifier
    ///   - TowerAttackSystem reads DayNight phase for range modifier
    ///   - EnemyAbilitySystem reads DayNight phase for damage modifier (enemy projectiles)
    /// </summary>
    public class DayNightSystem : global::BattleSystemECS.Content.Contracts.IEnemySpeedModifierView, global::BattleSystemECS.Content.Contracts.ITowerRangeModifierView
    {
        private readonly ComponentStore _store;
        private readonly GameConfig _gameConfig;

        // Cached multipliers — recomputed on phase transition
        private float _cachedEnemySpeedMult = 1.0f;
        private float _cachedTowerRangeMult = 1.0f;
        private float _cachedEnemyDamageMult = 1.0f;

        public DayNightSystem(ComponentStore store, GameConfig gameConfig)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _gameConfig = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
        }

        /// <summary>
        /// Called each turn — updates day/night timer and applies phase transitions.
        /// </summary>
        public void Update(float deltaTime)
        {
            var config = _gameConfig.DayNight;
            if (config == null) return;

            // -1 duration means day/night cycles are disabled
            if (config.DayDuration < 0f && config.NightDuration < 0f) return;

            for (int playerId = 0; playerId < ComponentStore.MAX_PLAYERS; playerId++)
            {
                if (_store.PlayerCurrentHealth[playerId] <= 0) continue;

                int phase = _store.GetDayNightPhase(playerId);
                float timer = _store.GetDayNightTimer(playerId);

                // Decrement timer
                if (timer > 0f)
                {
                    timer -= deltaTime;
                    _store.SetDayNightTimer(playerId, timer);

                    if (timer <= 0f)
                    {
                        // Phase transition
                        TransitionPhase(playerId, phase == DayNightConfig.Day ? DayNightConfig.Night : DayNightConfig.Day);
                    }
                }
            }
        }

        /// <summary>
        /// Transition to a new day/night phase.
        /// </summary>
        private void TransitionPhase(int playerId, int newPhase)
        {
            var config = _gameConfig.DayNight;
            if (newPhase == DayNightConfig.Day)
            {
                _store.SetDayNightPhase(playerId, DayNightConfig.Day);
                _store.SetDayNightTimer(playerId, config.DayDuration);
                _store.IncrementDayNightCycleCount(playerId);
                UpdateCachedMultipliers(playerId, DayNightConfig.Day);
            }
            else
            {
                _store.SetDayNightPhase(playerId, DayNightConfig.Night);
                _store.SetDayNightTimer(playerId, config.NightDuration);
                UpdateCachedMultipliers(playerId, DayNightConfig.Night);
            }
        }

        /// <summary>
        /// Initialize day/night state at game start. Call once during bootstrap.
        /// </summary>
        public void Initialize(int playerId)
        {
            var config = _gameConfig.DayNight;
            if (config == null) return;

            // Start at day
            _store.SetDayNightPhase(playerId, DayNightConfig.Day);
            _store.SetDayNightTimer(playerId, config.DayDuration);
            _store.IncrementDayNightCycleCount(playerId); // cycle 0 complete (we're at start of day 1)
            UpdateCachedMultipliers(playerId, DayNightConfig.Day);
        }

        /// <summary>
        /// Returns the enemy move speed multiplier for the current phase.
        /// Called by EnemyMovementSystem hot path.
        /// </summary>
        public float GetEnemySpeedMultiplier(int playerId)
        {
            return _cachedEnemySpeedMult;
        }

        /// <summary>
        /// Returns the tower attack range multiplier for the current phase.
        /// Called by TowerAttackSystem hot path.
        /// </summary>
        public float GetTowerRangeMultiplier(int playerId)
        {
            return _cachedTowerRangeMult;
        }

        /// <summary>
        /// Returns the enemy damage multiplier for the current phase.
        /// Called by EnemyAbilitySystem / EnemyProjectileSystem for damage calculation.
        /// </summary>
        public float GetEnemyDamageMultiplier(int playerId)
        {
            return _cachedEnemyDamageMult;
        }

        /// <summary>
        /// Returns the current phase (0=Day, 1=Night).
        /// </summary>
        public int GetPhase(int playerId)
        {
            return _store.GetDayNightPhase(playerId);
        }

        /// <summary>
        /// Returns true if currently in night phase.
        /// </summary>
        public bool IsNight(int playerId)
        {
            return _store.GetDayNightPhase(playerId) == DayNightConfig.Night;
        }

        private void UpdateCachedMultipliers(int playerId, int phase)
        {
            var config = _gameConfig.DayNight;
            if (config == null)
            {
                _cachedEnemySpeedMult = 1.0f;
                _cachedTowerRangeMult = 1.0f;
                _cachedEnemyDamageMult = 1.0f;
                return;
            }

            if (phase == DayNightConfig.Day)
            {
                _cachedEnemySpeedMult = 1.0f + config.DayEnemySpeedBonus;
                _cachedTowerRangeMult = 1.0f + config.DayTowerRangeBonus;
                _cachedEnemyDamageMult = 1.0f; // no enemy damage bonus during day
            }
            else // Night
            {
                _cachedEnemySpeedMult = 1.0f + config.NightEnemySpeedBonus;
                _cachedTowerRangeMult = 1.0f + config.NightTowerRangePenalty;
                _cachedEnemyDamageMult = 1.0f + config.NightEnemyDamageBonus;
            }
        }

        /// <summary>
        /// Forces a specific day/night phase (e.g., from a special event or boss ability).
        /// Does NOT reset the cycle count or timer — use SetPhaseAndTimer for full control.
        /// </summary>
        public void ForcePhase(int playerId, int phase, float duration)
        {
            _store.SetDayNightPhase(playerId, phase);
            _store.SetDayNightTimer(playerId, duration);
            UpdateCachedMultipliers(playerId, phase);
        }
    }
}
