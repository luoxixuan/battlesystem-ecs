#nullable enable
namespace BattleSystemECS.Core
{
    /// <summary>Pre-game setup: weather, day/night, difficulty, construction, random events.</summary>
    public class PreGameGroup : ISystemGroup
    {
        public Systems.WeatherSystem? Weather { get; set; }
        public Systems.DayNightSystem? DayNight { get; set; }
        public Systems.AdaptiveDifficultySystem? AdaptiveDifficulty { get; set; }
        public Systems.TowerConstructionSystem? Construction { get; set; }
        public Systems.RandomEventSystem? RandomEvent { get; set; }
        public Systems.DesperationSystem? Desperation { get; set; }
        public Systems.WaveSpawningSystem? WaveSpawning { get; set; }
        // Round 109 Direction 5 — Time Rewind snapshot sampler. Runs in PreGameGroup so
        // samples are taken even during BuildPhase (between waves) — a Time Rewind cast
        // during combat can restore to a snapshot taken 3s before combat started.
        public Systems.TimeRewindSnapshotSystem? TimeRewind { get; set; }

        public void Execute(ComponentStore store, float deltaTime, int turn)
        {
            Weather?.Update(deltaTime);
            DayNight?.Update(deltaTime);
            AdaptiveDifficulty?.Update(deltaTime);
            Construction?.Update(deltaTime);
            Desperation?.Update();
            TimeRewind?.Update(deltaTime);

            int waveNum = WaveSpawning?.GetCurrentWave() ?? 1;
            int levelNum = WaveSpawning?.GetCurrentLevel() ?? 1;
            RandomEvent?.Update(deltaTime, waveNum, levelNum);
        }
    }
}
