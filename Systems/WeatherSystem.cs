using System;
using BattleSystemECS.Config;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Weather system — manages dynamic weather effects per player.
    /// 
    /// Weather types: Clear(0), Rain(1), Fog(2), Storm(3)
    /// 
    /// Effects per frame:
    ///   - Enemy move speed: reduced by weather intensity
    ///   - Tower range: reduced by weather intensity (visibility penalty)
    ///   - Tower damage: increased by weather intensity (focus bonus)
    /// 
    /// Integration points:
    ///   - FrameScheduler.Tick() calls Weather.Update(deltaTime) each turn
    ///   - EnemyMovementSystem reads WeatherIntensity for speed penalty
    ///   - TowerAttackSystem reads WeatherIntensity for range/damage modifiers
    /// </summary>
    public class WeatherSystem
    {
        private readonly ComponentStore _store;
        private readonly GameConfig _gameConfig;
        private readonly Random _rng = new Random();

        // Cached per-type multipliers — updated on weather change
        private float _cachedEnemySpeedMult = 1.0f;
        private float _cachedTowerRangeMult = 1.0f;
        private float _cachedTowerDamageMult = 1.0f;

        // Frame counter for weather transitions
        private int _turnCount = 0;

        public WeatherSystem(ComponentStore store, GameConfig gameConfig)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _gameConfig = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
        }

        /// <summary>
        /// Called each turn — updates weather timer and applies transitions.
        /// </summary>
        public void Update(float deltaTime)
        {
            _turnCount++;

            // Update weather state for player 0 (single-player)
            for (int playerId = 0; playerId < ComponentStore.MAX_PLAYERS; playerId++)
            {
                if (_store.PlayerCurrentHealth[playerId] <= 0) continue;

                int currentWeather = _store.CurrentWeather[playerId];
                float timer = _store.WeatherTimer[playerId];
                float intensity = _store.WeatherIntensity[playerId];

                // Decrement timer if active (timer >= 0 means timed; -1 means permanent)
                if (timer > 0f)
                {
                    timer -= deltaTime;
                    _store.SetWeatherTimer(playerId, timer);

                    if (timer <= 0f)
                    {
                        // Time's up — transition to Clear or roll new weather
                        TransitionWeather(playerId);
                    }
                }

                // Update cached multipliers when weather changes
                UpdateCachedMultipliers(playerId, currentWeather, intensity);
            }
        }

        /// <summary>
        /// Transition to new weather — rolls from available types or goes Clear.
        /// </summary>
        private void TransitionWeather(int playerId)
        {
            var weatherConfig = _gameConfig.Weather;
            if (weatherConfig == null || weatherConfig.Types.Count == 0)
            {
                // No config — stay Clear
                _store.SetCurrentWeather(playerId, WeatherConfig.Clear);
                _store.SetWeatherIntensity(playerId, 0f);
                _store.SetWeatherTimer(playerId, -1f);
                _cachedEnemySpeedMult = 1.0f;
                _cachedTowerRangeMult = 1.0f;
                _cachedTowerDamageMult = 1.0f;
                return;
            }

            // 70% chance to roll a new weather type (30% Clear)
            if (_rng.NextDouble() < 0.7 && weatherConfig.Types.Count > 0)
            {
                int roll = _rng.Next(weatherConfig.Types.Count);
                int idx = 0;
                string chosenType = "";
                WeatherTypeConfig chosenConfig = null;
                foreach (var kvp in weatherConfig.Types)
                {
                    if (idx == roll)
                    {
                        chosenType = kvp.Key;
                        chosenConfig = kvp.Value;
                        break;
                    }
                    idx++;
                }

                if (!string.IsNullOrEmpty(chosenType) && chosenConfig != null)
                {
                    int weatherType = WeatherTypeNameToId(chosenType);
                    _store.SetCurrentWeather(playerId, weatherType);

                    // Random intensity within configured range
                    float intensity = chosenConfig.MinIntensity
                        + (float)(_rng.NextDouble() * (chosenConfig.MaxIntensity - chosenConfig.MinIntensity));
                    _store.SetWeatherIntensity(playerId, intensity);
                    _store.SetWeatherTimer(playerId, chosenConfig.DefaultDuration);

                    _cachedEnemySpeedMult = chosenConfig.EnemySpeedMult;
                    _cachedTowerRangeMult = chosenConfig.TowerRangeMult;
                    _cachedTowerDamageMult = chosenConfig.TowerDamageMult;
                }
            }
            else
            {
                // Go Clear (calm)
                _store.SetCurrentWeather(playerId, WeatherConfig.Clear);
                _store.SetWeatherIntensity(playerId, 0f);
                _store.SetWeatherTimer(playerId, -1f);
                _cachedEnemySpeedMult = 1.0f;
                _cachedTowerRangeMult = 1.0f;
                _cachedTowerDamageMult = 1.0f;
            }
        }

        /// <summary>
        /// Returns the enemy speed multiplier for current weather.
        /// Called by EnemyMovementSystem hot path.
        /// </summary>
        public float GetEnemySpeedMultiplier(int playerId)
        {
            float intensity = _store.WeatherIntensity[playerId];
            float baseMult = _cachedEnemySpeedMult;
            // Interpolate between Clear (1.0) and weather type effect based on intensity
            // e.g., Storm base=0.7, intensity=0.8 → 0.7 + (1-0.7)*0.8 = 0.94 (near-full effect)
            return baseMult + (1f - baseMult) * intensity;
        }

        /// <summary>
        /// Returns the tower range multiplier for current weather.
        /// Called by TowerAttackSystem hot path.
        /// </summary>
        public float GetTowerRangeMultiplier(int playerId)
        {
            float intensity = _store.WeatherIntensity[playerId];
            float baseMult = _cachedTowerRangeMult;
            return baseMult + (1f - baseMult) * intensity;
        }

        /// <summary>
        /// Returns the tower damage multiplier for current weather.
        /// Called by TowerAttackSystem hot path.
        /// </summary>
        public float GetTowerDamageMultiplier(int playerId)
        {
            float intensity = _store.WeatherIntensity[playerId];
            float baseMult = _cachedTowerDamageMult;
            return baseMult + (1f - baseMult) * intensity;
        }

        /// <summary>
        /// Forces a specific weather type (e.g., from a special event or boss ability).
        /// </summary>
        public void ForceWeather(int playerId, int weatherType, float intensity, float duration)
        {
            _store.SetCurrentWeather(playerId, weatherType);
            _store.SetWeatherIntensity(playerId, intensity);
            _store.SetWeatherTimer(playerId, duration);
            UpdateCachedMultipliers(playerId, weatherType, intensity);
        }

        private void UpdateCachedMultipliers(int playerId, int weatherType, float intensity)
        {
            var config = _gameConfig.Weather;
            if (config == null) return;

            string typeName = WeatherIdToTypeName(weatherType);
            if (!string.IsNullOrEmpty(typeName) && config.Types.TryGetValue(typeName, out var typeConfig))
            {
                _cachedEnemySpeedMult = typeConfig.EnemySpeedMult;
                _cachedTowerRangeMult = typeConfig.TowerRangeMult;
                _cachedTowerDamageMult = typeConfig.TowerDamageMult;
            }
            else
            {
                _cachedEnemySpeedMult = 1.0f;
                _cachedTowerRangeMult = 1.0f;
                _cachedTowerDamageMult = 1.0f;
            }
        }

        private static int WeatherTypeNameToId(string name)
        {
            return name?.ToLowerInvariant() switch
            {
                "clear" => WeatherConfig.Clear,
                "rain" => WeatherConfig.Rain,
                "fog" => WeatherConfig.Fog,
                "storm" => WeatherConfig.Storm,
                _ => WeatherConfig.Clear
            };
        }

        private static string WeatherIdToTypeName(int id)
        {
            return id switch
            {
                WeatherConfig.Clear => "Clear",
                WeatherConfig.Rain => "Rain",
                WeatherConfig.Fog => "Fog",
                WeatherConfig.Storm => "Storm",
                _ => "Clear"
            };
        }
    }
}