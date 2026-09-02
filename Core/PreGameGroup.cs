#nullable enable
namespace BattleSystemECS.Core
{
    /// <summary>Pre-game setup: weather, day/night, difficulty, construction, random events.</summary>
    internal sealed class PreGameGroup : ISystemGroup
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
        // 使用独立 FrameBindingFacts 的显式适配器；不调用旧的字符串绑定 API。
        internal void RegisterBoundFrameAdapters(FrameScheduler scheduler)
        {
            if (Weather != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("pregame.weather.update"), c => Weather.Update(c.Delta));
            if (DayNight != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("pregame.day-night.update"), c => DayNight.Update(c.Delta));
            if (AdaptiveDifficulty != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("pregame.adaptive-difficulty.update"), c => AdaptiveDifficulty.Update(c.Delta));
            if (Construction != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("pregame.construction.update"), c => Construction.Update(c.Delta));
            if (Desperation != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("pregame.desperation.update"), c => Desperation.Update());
            if (TimeRewind != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("pregame.time-rewind.update"), c => TimeRewind.Update(c.Delta));
            if (WaveSpawning != null)
            {
                scheduler.RegisterFrameBinding(FrameBindingFacts.Get("pregame.wave.read-current-wave"), c => scheduler.GraphCurrentWave = WaveSpawning.GetCurrentWave());
                scheduler.RegisterFrameBinding(FrameBindingFacts.Get("pregame.wave.read-current-level"), c => scheduler.GraphCurrentLevel = WaveSpawning.GetCurrentLevel());
            }
            if (RandomEvent != null)
            {
                scheduler.RegisterFrameBinding(FrameBindingFacts.Get("pregame.random-event.update"), c => RandomEvent.Update(c.Delta, scheduler.GraphCurrentWave, scheduler.GraphCurrentLevel));
                scheduler.RegisterFrameBinding(FrameBindingFacts.Get("pregame.random-event.callback-dispatch"), c => RandomEvent.DispatchPendingCallbacks());
            }
        }

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
            RandomEvent?.DispatchPendingCallbacks();
        }
    }
}
