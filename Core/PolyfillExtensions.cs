// Polyfills for netstandard2.1 compatibility
using System;
using System.Collections.Generic;

namespace BattleSystemECS
{
    internal static class PolyfillExtensions
    {
        /// <summary>
        /// Fallback for netstandard2.1 where CollectionsMarshal.AsSpan is not available.
        /// Allocates a copy (unlike the .NET 5+ zero-alloc version).
        /// </summary>
        public static ReadOnlySpan<T> AsSpan<T>(this List<T> list)
        {
            return list.ToArray().AsSpan();
        }
    }
}
