namespace BattleSystemECS.Core
{
    public enum PhaseContextKind { Unbound, Init, Build, Wave, Intermission, BranchSelection, LevelComplete, GameOver, Victory, Other }

    public readonly struct PhaseContext
    {
        public readonly PhaseContextKind Kind;
        public bool AllowsCombat => Kind == PhaseContextKind.Wave;
        public bool AllowsPreparationResources => Kind == PhaseContextKind.Build;
        public PhaseContext(PhaseContextKind kind) { Kind = kind; }
        public static PhaseContext FromGameState(GameState state) => new PhaseContext(state switch
        {
            GameState.Init => PhaseContextKind.Init,
            GameState.BuildPhase => PhaseContextKind.Build,
            GameState.WavePhase => PhaseContextKind.Wave,
            GameState.Intermission => PhaseContextKind.Intermission,
            GameState.BranchSelection => PhaseContextKind.BranchSelection,
            GameState.LevelComplete => PhaseContextKind.LevelComplete,
            GameState.GameOver => PhaseContextKind.GameOver,
            GameState.Victory => PhaseContextKind.Victory,
            _ => PhaseContextKind.Other
        });
        public static PhaseContext Unbound => new PhaseContext(PhaseContextKind.Unbound);
    }
}
