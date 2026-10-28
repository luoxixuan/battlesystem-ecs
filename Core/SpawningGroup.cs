#nullable enable
namespace BattleSystemECS.Core
{
    /// <summary>Wave spawning and nest/spawner systems.</summary>
    internal sealed class SpawningGroup
    {
        public Systems.WaveSpawningSystem? WaveSpawning { get; set; }
        public Systems.NestSystem? Nest { get; set; }
        /// <summary>为 true 时跳过波次生成 Update（回调派发与巢穴不受影响）；由 FrameScheduler.SetSpawningPaused 写入。</summary>
        internal bool WavePaused { get; set; }
        // 使用独立 FrameBindingFacts 的显式适配器；不调用旧的字符串绑定 API。
        internal void RegisterBoundFrameAdapters(FrameScheduler scheduler)
        {
            if (WaveSpawning != null)
            {
                scheduler.RegisterFrameBinding(FrameBindingFacts.Get("spawning.wave.update"), c =>
                {
                    if (scheduler.RunsGameplayScenario && !WavePaused) WaveSpawning.Update();
                });
                scheduler.RegisterFrameBinding(FrameBindingFacts.Get("spawning.wave.callback-dispatch"), c => WaveSpawning.DispatchPendingCallbacks());
            }
            if (Nest != null)
            {
                scheduler.RegisterFrameBinding(FrameBindingFacts.Get("spawning.nest.prepare"), c => Nest.SetTurn(c.Turn));
                scheduler.RegisterFrameBinding(FrameBindingFacts.Get("spawning.nest.update"), c => Nest.Update(c.Delta));
            }
        }

    }
}
