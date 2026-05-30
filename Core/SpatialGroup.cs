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
        }
    }
}
