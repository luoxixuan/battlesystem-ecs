using System;
using System.Threading.Tasks;

namespace BattleSystemECS.Core
{
    /// <summary>
    /// 热路径共享的 ParallelOptions 缓存 —— 消除每帧 Parallel.For 调用点的
    /// new ParallelOptions 分配（每次约 40 字节 × 每帧数十个系统调用点 = 可观的 Gen0 压力）。
    /// ParallelOptions 的字段只在调度启动时读取，Parallel.For 并发复用同一实例是线程安全的；
    /// 任何调用方不得修改该实例的字段。
    /// </summary>
    public static class ParallelOptionsCache
    {
        /// <summary>与 TPL 默认一致：并行度 = 逻辑处理器数。</summary>
        public static readonly ParallelOptions HotPath = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount
        };

        /// <summary>并行度 = 4（轻量并行段专用，如 ManaBurnSystem 的批收集）。</summary>
        public static readonly ParallelOptions Capped4 = new ParallelOptions
        {
            MaxDegreeOfParallelism = 4
        };
    }
}
