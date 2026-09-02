#nullable enable
namespace BattleSystemECS.Core
{
    /// <summary>Spatial grid rebuild + post-rebuild systems: patrol, chrono, fog, point defense, telegraph.</summary>
    internal sealed class SpatialGroup : ISystemGroup
    {
        public Systems.PatrolTowerSystem? PatrolTower { get; set; }
        public Systems.ChronoTowerSystem? ChronoTower { get; set; }
        public Systems.FogOfWarSystem? Fog { get; set; }
        public Systems.PointDefenseSystem? PointDefense { get; set; }
        public Systems.TelegraphSystem? Telegraph { get; set; }
        // Round 106 Direction 2 — Mine / Trap tower system. Must run after
        // RebuildSpatialGrid() so that mines can see enemy positions. Per-turn
        // SetTurn clears the per-frame trigger latch.
        public Systems.MineSystem? Mine { get; set; }

        internal void RegisterFrameBindings(FrameScheduler scheduler)
        {
            if (PatrolTower != null) { scheduler.RegisterFrameBinding(FrameBindingFacts.Get("spatial.patrol.prepare"), c => PatrolTower?.SetTurn(c.Turn)); scheduler.RegisterFrameBinding(FrameBindingFacts.Get("spatial.patrol.update"), c => PatrolTower?.Update(c.Delta)); }
            if (ChronoTower != null) { scheduler.RegisterFrameBinding(FrameBindingFacts.Get("spatial.chrono.prepare"), c => ChronoTower?.SetTurn()); scheduler.RegisterFrameBinding(FrameBindingFacts.Get("spatial.chrono.update"), c => ChronoTower?.Update()); }
            if (Fog != null) { scheduler.RegisterFrameBinding(FrameBindingFacts.Get("spatial.fog.prepare"), c => Fog?.SetTurn()); scheduler.RegisterFrameBinding(FrameBindingFacts.Get("spatial.fog.update"), c => Fog?.Update()); }
            if (PointDefense != null) { scheduler.RegisterFrameBinding(FrameBindingFacts.Get("spatial.point-defense.prepare"), c => PointDefense?.SetTurn(c.Turn)); scheduler.RegisterFrameBinding(FrameBindingFacts.Get("spatial.point-defense.update"), c => PointDefense?.Update(c.Delta)); }
            if (Telegraph != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("spatial.telegraph.update"), c => Telegraph?.Update(c.Delta));
            if (Mine != null) { scheduler.RegisterFrameBinding(FrameBindingFacts.Get("spatial.mine.prepare"), c => Mine?.SetTurn(c.Turn)); scheduler.RegisterFrameBinding(FrameBindingFacts.Get("spatial.mine.update"), c => Mine?.Update(c.Delta)); }
        }

        public void Execute(ComponentStore store, float deltaTime, int turn)
        {
            store.RebuildSpatialGrid();

            PatrolTower?.SetTurn(turn);
            PatrolTower?.Update(deltaTime);

            ChronoTower?.SetTurn();
            ChronoTower?.Update();

            Fog?.SetTurn();
            Fog?.Update();

            PointDefense?.SetTurn(turn);
            PointDefense?.Update(deltaTime);

            Telegraph?.Update(deltaTime);

            Mine?.SetTurn(turn);
            Mine?.Update(deltaTime);
        }
    }
}
