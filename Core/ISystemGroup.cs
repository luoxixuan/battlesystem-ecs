namespace BattleSystemECS.Core
{
    /// <summary>
    /// A group of systems executed together in a single phase of the frame.
    /// Each group encapsulates its own systems, initialization, and execution order.
    /// FrameScheduler only orchestrates groups — systems are added/removed within each group.
    /// </summary>
    public interface ISystemGroup
    {
        /// <summary>
        /// Execute all systems in this group for one frame tick.
        /// Called once per frame during WavePhase.
        /// </summary>
        void Execute(ComponentStore store, float deltaTime, int turn);
    }

    /// <summary>
    /// Variant for BuildPhase groups — no turn parameter needed.
    /// </summary>
    public interface IBuildPhaseGroup
    {
        void Execute(ComponentStore store, float deltaTime);
    }
}
