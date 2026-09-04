// netstandard2.1 polyfill for Random.Shared (.NET 6+ API).
// 不是确定性资产：种子是 TickCount xor ManagedThreadId。模拟热路径必须走 Frame DeterminismContext。
// 仅压测 / 纯表现等非模拟路径可以继续用 Rng.Shared。
using System;
using System.Threading;

namespace BattleSystemECS
{
    public static class Rng
    {
        [ThreadStatic]
        private static Random _shared;

        /// <summary>
        /// 墙钟线程局部 Random。禁止进入 GAS / 战斗公式 / 决定实体数量与位置的模拟路径。
        /// </summary>
        public static Random Shared
        {
            get
            {
                if (_shared == null)
                {
                    _shared = new Random(Environment.TickCount ^ Thread.CurrentThread.ManagedThreadId);
                }
                return _shared;
            }
        }
    }
}
