#nullable enable
namespace BattleSystemECS.Core
{
    /// <summary>Terrain effects, wave mutators, enemy morphing.</summary>
    internal sealed class TerrainGroup : ISystemGroup
    {
        public Systems.TerrainSystem? Terrain { get; set; }
        public Systems.WaveMutatorSystem? WaveMutator { get; set; }
        public Systems.EnemyMorphSystem? EnemyMorph { get; set; }

        internal void RegisterFrameBindings(FrameScheduler scheduler)
        {
            if (Terrain != null) { scheduler.RegisterFrameBinding(FrameBindingFacts.Get("terrain.prepare"), c => Terrain?.SetTurn()); scheduler.RegisterFrameBinding(FrameBindingFacts.Get("terrain.update"), c => Terrain?.Update(c.Delta)); }
            if (WaveMutator != null) { scheduler.RegisterFrameBinding(FrameBindingFacts.Get("terrain.wave-mutator.prepare"), c => WaveMutator?.SetTurn(c.Turn)); scheduler.RegisterFrameBinding(FrameBindingFacts.Get("terrain.wave-mutator.update"), c => WaveMutator?.Update(c.Delta)); }
            if (EnemyMorph != null) scheduler.RegisterFrameBinding(FrameBindingFacts.Get("terrain.enemy-morph.update"), c => EnemyMorph?.Update(c.Delta));
        }

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
