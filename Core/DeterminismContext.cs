using System;
using System.Threading;

namespace BattleSystemECS.Core
{
    /// <summary>
    /// 帧级确定性随机上下文。模拟热路径只从此领号，且仅 CommitSerial 相取数。
    /// <see cref="Rng.Shared"/>（墙钟 TickCount xor 线程 id）与无种子 <c>new Random()</c>
    /// 都不是确定性资产，不得进入 GAS / 战斗公式 / 决定实体数量与位置的路径。
    /// </summary>
    public sealed class DeterminismContext
    {
        public const int DefaultSeed = 1;
        private Random _rng;
        private int _seed;
        private int _commitSerialDepth;
        private bool _strictFrame;
        private int _ownerThreadId;

        public DeterminismContext(int seed = DefaultSeed)
        {
            Reset(seed);
        }

        public int Seed => _seed;

        /// <summary>比赛/测试播种。禁止在半帧中途改种，以免墙钟流与确定性流混用。</summary>
        public void Reset(int seed)
        {
            _seed = seed;
            _rng = new Random(seed);
        }

        /// <summary>
        /// 生产 Tick 入口：严格模式直到 <see cref="EndStrictFrame"/>。
        /// 未进入 Tick 的单测仍可领号（非严格）。
        /// </summary>
        internal void BeginStrictFrame()
        {
            _strictFrame = true;
            _commitSerialDepth = 0;
            _ownerThreadId = 0;
        }

        internal void EndStrictFrame()
        {
            _strictFrame = false;
            _commitSerialDepth = 0;
            _ownerThreadId = 0;
        }

        /// <summary>
        /// 本仓 SerialUpdate / SerialPrepare / InternalParallelCollectSerialCommit 的串行段
        /// 对应终态 CommitSerial（按确定顺序改共享状态）。并行工作线程不得领号。
        /// </summary>
        internal void EnterCommitSerial()
        {
            if (_commitSerialDepth++ == 0)
                _ownerThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        internal void ExitCommitSerial()
        {
            if (_commitSerialDepth > 0) _commitSerialDepth--;
            if (_commitSerialDepth == 0) _ownerThreadId = 0;
        }

        internal bool CanDraw
        {
            get
            {
                if (!_strictFrame) return true;
                return _commitSerialDepth > 0 &&
                    Thread.CurrentThread.ManagedThreadId == _ownerThreadId;
            }
        }

        public int Next()
        {
            EnsureCommitSerial();
            return _rng.Next();
        }

        public int Next(int maxValue)
        {
            EnsureCommitSerial();
            return _rng.Next(maxValue);
        }

        public int Next(int minValue, int maxValue)
        {
            EnsureCommitSerial();
            return _rng.Next(minValue, maxValue);
        }

        public double NextDouble()
        {
            EnsureCommitSerial();
            return _rng.NextDouble();
        }

        private void EnsureCommitSerial()
        {
            if (CanDraw) return;
            throw new InvalidOperationException(
                "DeterminismContext 只允许在 CommitSerial 相取数，且不得在并行工作线程领号。");
        }
    }

    /// <summary>
    /// 本仓 FrameExecutionSemantics → 终态 CommitSerial 领号许可。
    /// ParallelDisjointWrite / PresentationCommit 禁止取数。
    /// </summary>
    internal static class FrameDeterminism
    {
        public static bool AllowsCommitSerialDraw(FrameExecutionSemantics semantics)
        {
            switch (semantics)
            {
                case FrameExecutionSemantics.SerialCommit:
                case FrameExecutionSemantics.SerialUpdate:
                case FrameExecutionSemantics.SerialPrepare:
                case FrameExecutionSemantics.InternalParallelCollectSerialCommit:
                    return true;
                default:
                    return false;
            }
        }
    }
}
