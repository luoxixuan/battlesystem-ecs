#nullable enable
namespace BattleSystemECS.Core
{
    /// <summary>Spatial grid rebuild + post-rebuild systems: patrol, chrono, fog, point defense, telegraph.</summary>
    internal sealed class SpatialGroup
    {
        public Systems.ChronoTowerSystem? ChronoTower { get; set; }
        public Systems.TelegraphSystem? Telegraph { get; set; }
        // Round 106 Direction 2 — Mine / Trap tower system. Must run after
        // RebuildSpatialGrid() so that mines can see enemy positions. Per-turn
        // SetTurn clears the per-frame trigger latch.
        public Systems.MineSystem? Mine { get; set; }

        internal void RegisterFrameBindings(FrameScheduler scheduler)
        {
            if (ChronoTower != null) { scheduler.RegisterFrameBinding(FrameBindingFacts.Get("spatial.chrono.prepare"), c => ChronoTower?.SetTurn()); scheduler.RegisterFrameBinding(FrameBindingFacts.Get("spatial.chrono.update"), c => ChronoTower?.Update()); }
            if (Telegraph != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("spatial.telegraph.update"), c => Telegraph?.Update(c.Delta));
            if (Mine != null) { scheduler.RegisterFrameBinding(FrameBindingFacts.Get("spatial.mine.prepare"), c => Mine?.SetTurn(c.Turn)); scheduler.RegisterFrameBinding(FrameBindingFacts.Get("spatial.mine.update"), c => Mine?.Update(c.Delta)); }
        }

    }
}
