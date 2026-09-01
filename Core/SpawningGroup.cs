#nullable enable
namespace BattleSystemECS.Core
{
    /// <summary>Wave spawning and nest/spawner systems.</summary>
    public class SpawningGroup : ISystemGroup
    {
        public Systems.WaveSpawningSystem? WaveSpawning { get; set; }
        public Systems.NestSystem? Nest { get; set; }

        public void Execute(ComponentStore store, float deltaTime, int turn)
        {
            WaveSpawning?.Update();
            WaveSpawning?.DispatchPendingCallbacks();
            Nest?.SetTurn(turn);
            Nest?.Update(deltaTime);
        }
    }
}
