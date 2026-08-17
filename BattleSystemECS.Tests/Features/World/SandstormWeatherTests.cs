using BattleSystemECS.Tests.Infrastructure;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Features.World
{
    /// <summary>
    /// Tests for the Sandstorm weather type (Round 185 Direction 1) — persistent
    /// percentage-based DoT on all enemies plus tower range reduction. Verifies:
    /// 1. WeatherConfig.Sandstorm = 4 enum value
    /// 2. WeatherTypeConfig.EnemyDotPct field defaults to 0 (inert for non-Sandstorm)
    /// 3. ForceWeather(..., Sandstorm, ...) sets up dotPct on the cache
    /// 4. Update() applies DoT = maxHp * dotPct * dt to active enemies
    /// 5. Update() with dotPct=0 (default) does NOT touch enemy health (sentinel fast path)
    /// 6. Recoil: Range multiplier drops to 0.8 (Sandstorm spec)
    /// 7. GetEnemyDotPct() returns 0 for non-Sandstorm weather
    /// </summary>
    public class SandstormWeatherTests : BattleTestBase
    {
        private void ConfigureSandstorm(float dotPct, float rangeMult = 0.8f)
        {
            var wc = new WeatherConfig();
            var s = new WeatherTypeConfig
            {
                Name = "Sandstorm",
                EnemySpeedMult = 0.95f,
                TowerRangeMult = rangeMult,
                TowerDamageMult = 1.0f,
                DefaultDuration = 12f,
                MinIntensity = 0.5f,
                MaxIntensity = 1.0f,
                EnemyDotPct = dotPct,
            };
            wc.Types["Sandstorm"] = s;
            Config.Weather = wc;
        }

        [Fact]
        public void WeatherConfig_Sandstorm_Enum_IsFour()
        {
            // Round 185 Direction 1: Sandstorm = 4, sitting after Storm = 3
            Assert.Equal(4, WeatherConfig.Sandstorm);
        }

        [Fact]
        public void WeatherTypeConfig_EnemyDotPct_DefaultIsInert()
        {
            // Default-constructed WeatherTypeConfig must have dotPct = 0 (no DoT)
            var t = new WeatherTypeConfig();
            Assert.Equal(0f, t.EnemyDotPct);
        }

        [Fact]
        public void Sandstorm_RangeMult_IsReduced()
        {
            // Sandstorm's tower range must be reduced per spec (0.8x).
            // The multiplier formula is baseMult + (1-baseMult)*intensity, so at intensity=0
            // (full effect) we get the base 0.8. At intensity=1 (no effect) we get 1.0.
            ConfigureSandstorm(dotPct: 0.005f, rangeMult: 0.8f);
            var sys = new WeatherSystem(Store, Config);

            // Force sandstorm at zero intensity → full base effect (0.8x range)
            sys.ForceWeather(0, WeatherConfig.Sandstorm, intensity: 0f, duration: 12f);
            Assert.Equal(0.8f, sys.GetTowerRangeMultiplier(0), 0.001f);

            // At intensity 1.0 (no effect), mult is 1.0 (matches Clear)
            sys.ForceWeather(0, WeatherConfig.Sandstorm, intensity: 1.0f, duration: 12f);
            Assert.Equal(1.0f, sys.GetTowerRangeMultiplier(0), 0.001f);
        }

        [Fact]
        public void Sandstorm_Update_AppliesDotToActiveEnemies()
        {
            // End-to-end: 2 active enemies, Sandstorm @ 0.5%/s, 1s tick
            // Enemy A: 100 HP → should take 0.5 dmg
            // Enemy B: 200 HP → should take 1.0 dmg
            ConfigureSandstorm(dotPct: 0.005f);
            // WeatherSystem.Update() skips a player if PlayerCurrentHealth[playerId] <= 0
            // (pre-existing "dead player skips weather transitions" pattern). Set HP first.
            Store.PlayerCurrentHealth[0] = 100f;
            var sys = new WeatherSystem(Store, Config);
            sys.ForceWeather(0, WeatherConfig.Sandstorm, intensity: 1.0f, duration: 12f);

            int eA = Enemy(e => { e.MoveSpeed = 1f; e.Damage = 0f; e.GoldReward = 1; });
            // Note: AddEnemy already calls AddActiveEnemyId internally. Manual call
            // would create a duplicate entry in _activeEnemyIds, causing Sandstorm DoT
            // to apply twice. Don't double-register.
            Store.EnemyActive[eA] = true;

            int eB = Enemy(e => { e.X = 1f; e.MoveSpeed = 1f; e.Health = 200f; e.MaxHealth = 200f; e.Damage = 0f; e.GoldReward = 1; });
            Store.EnemyActive[eB] = true;

            // Tick 1 second of game time
            sys.Update(1.0f);

            // Assert: 100 * 0.005 * 1.0 = 0.5; 200 * 0.005 * 1.0 = 1.0
            Assert.Equal(99.5f, Store.EnemyHealth[eA], 0.001f);
            Assert.Equal(199.0f, Store.EnemyHealth[eB], 0.001f);
        }

        [Fact]
        public void ClearWeather_Update_DoesNotApplyDot()
        {
            // Sentinel fast path: with default (no Sandstorm config), Update() must NOT
            // touch any enemy HP. Cached dotPct = 0 short-circuits the loop.
            // No Sandstorm config — default Weather with empty Types
            Store.PlayerCurrentHealth[0] = 100f;
            var sys = new WeatherSystem(Store, Config);

            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Damage = 0f; e.GoldReward = 1; });
            Store.EnemyActive[eid] = true;
            // AddEnemy already registers in _activeEnemyIds; don't double-register.

            float before = Store.EnemyHealth[eid];
            sys.Update(1.0f);
            Assert.Equal(before, Store.EnemyHealth[eid]);
        }

        [Fact]
        public void GetEnemyDotPct_ZeroForNonSandstormWeather()
        {
            // GetEnemyDotPct should be 0 for Clear (sentinel early-return)
            var sys = new WeatherSystem(Store, Config);
            // Force Clear weather (no config → no Sandstorm → cached dotPct = 0)
            sys.ForceWeather(0, WeatherConfig.Clear, intensity: 1.0f, duration: -1f);
            Assert.Equal(0f, sys.GetEnemyDotPct(0));
        }

        [Fact]
        public void GetEnemyDotPct_ScalesWithIntensity()
        {
            // dotPct = 0.01 (1%/s), intensity = 0.5 → returns 0.005
            ConfigureSandstorm(dotPct: 0.01f);
            var sys = new WeatherSystem(Store, Config);
            sys.ForceWeather(0, WeatherConfig.Sandstorm, intensity: 0.5f, duration: 12f);
            Assert.Equal(0.005f, sys.GetEnemyDotPct(0), 0.001f);
        }
    }
}