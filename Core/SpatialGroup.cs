#nullable enable
namespace BattleSystemECS.Core
{
    /// <summary>Spatial grid rebuild + post-rebuild systems: patrol, chrono, fog, point defense, telegraph.</summary>
    public class SpatialGroup : ISystemGroup
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
