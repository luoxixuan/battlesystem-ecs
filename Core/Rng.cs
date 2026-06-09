// netstandard2.1 polyfill for Random.Shared (.NET 6+ API)
// All code uses Rng.Shared instead of Random.Shared for cross-platform compatibility.
using System;
using System.Threading;

namespace BattleSystemECS
{
    public static class Rng
    {
        [ThreadStatic]
        private static Random _shared;

        /// <summary>Thread-safe shared Random instance — equivalent to .NET 6's Random.Shared.</summary>
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
