#nullable enable
namespace BattleSystemECS.Core
{
    /// <summary>Terrain effects, wave mutators, enemy morphing.</summary>
    public class TerrainGroup : ISystemGroup
    {
        public Systems.TerrainSystem? Terrain { get; set; }
        public Systems.WaveMutatorSystem? WaveMutator { get; set; }
        public Systems.EnemyMorphSystem? EnemyMorph { get; set; }

        public void Execute(ComponentStore store, float deltaTime, int turn)
        {
            Terrain?.SetTurn();
            Terrain?.Update(deltaTime);
            WaveMutator?.SetTurn(turn);
            WaveMutator?.Update(deltaTime);
            EnemyMorph?.Update(deltaTime);
        }
    }
}
