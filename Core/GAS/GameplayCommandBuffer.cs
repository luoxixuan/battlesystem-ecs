using System;
using System.Collections.Generic;

namespace BattleSystemECS.Core.GAS
{
    public enum CommandRejection { None, Capacity, ReservedExhausted, CriticalCapacity }

    public sealed class CommandBuffer<T> where T : struct
    {
        private readonly T[] _items;
        private int _count;
        public int Capacity => _items.Length;
        public int Count => _count;
        public int PeakCount { get; private set; }
        public int OverflowCount { get; private set; }
        public CommandRejection LastRejection { get; private set; }
        public int Reserved { get; }
        public CommandBuffer(int capacity, int reserved = 0) { if (capacity < 1 || reserved < 0 || reserved > capacity) throw new ArgumentOutOfRangeException(); _items = new T[capacity]; Reserved = reserved; }
        public bool TryAdd(T value, bool critical = false) {
            int limit = critical ? Capacity : Capacity - Reserved;
            if (_count >= limit) {
                OverflowCount++;
                LastRejection = critical ? CommandRejection.CriticalCapacity : (limit == Capacity ? CommandRejection.Capacity : CommandRejection.ReservedExhausted);
                return false;
            }
            _items[_count++] = value;
            if (_count > PeakCount) PeakCount = _count;
            LastRejection = CommandRejection.None;
            return true;
        }
        public bool CanAdd(int count, bool critical = false)
        {
            if (count < 0) return false;
            int limit = critical ? Capacity : Capacity - Reserved;
            return _count <= limit - count;
        }
        internal void RecordCapacityRejection(bool critical)
        {
            OverflowCount++;
            int limit = critical ? Capacity : Capacity - Reserved;
            LastRejection = critical ? CommandRejection.CriticalCapacity :
                (limit == Capacity ? CommandRejection.Capacity : CommandRejection.ReservedExhausted);
        }
        public T Get(int index) => index >= 0 && index < _count ? _items[index] : throw new ArgumentOutOfRangeException(nameof(index));
        public void Sort(Comparison<T> comparison) { if (comparison == null) throw new ArgumentNullException(nameof(comparison)); Array.Sort(_items, 0, _count, Comparer<T>.Create(comparison)); }
        public bool TryMerge(CommandBuffer<T> source, Comparison<T> comparison, bool critical = false) {
            if (source == null) throw new ArgumentNullException(nameof(source));
            int limit = critical ? Capacity : Capacity - Reserved;
            if (_count > limit - source._count) {
                OverflowCount++;
                LastRejection = critical ? CommandRejection.CriticalCapacity : (limit == Capacity ? CommandRejection.Capacity : CommandRejection.ReservedExhausted);
                return false;
            }
            // 先按 comparison 钉源序（sequence / entity），再追加；digest 不依赖线程完成序。
            if (source._count > 1) source.Sort(comparison);
            for (int i = 0; i < source._count; i++) if (!TryAdd(source._items[i], critical)) return false;
            Sort(comparison); return true;
        }
        public void Clear() { Array.Clear(_items, 0, _count); _count = 0; LastRejection = CommandRejection.None; }
        public void RemovePrefix(int count)
        {
            if (count <= 0) return;
            if (count >= _count) { Clear(); return; }
            int remaining = _count - count;
            Array.Copy(_items, count, _items, 0, remaining);
            Array.Clear(_items, remaining, count);
            _count = remaining;
        }
        public void RemoveAt(int index)
        {
            if (index < 0 || index >= _count) return;
            int remaining = _count - index - 1;
            if (remaining > 0) Array.Copy(_items, index + 1, _items, index, remaining);
            _items[_count - 1] = default(T);
            _count--;
        }
        public void TruncateTo(int count)
        {
            if (count < 0) count = 0;
            if (count >= _count) return;
            Array.Clear(_items, count, _count - count);
            _count = count;
        }
        public void ResetOverflowCount() { OverflowCount = 0; }
        public void ResetDiagnostics() { PeakCount = _count; OverflowCount = 0; }
    }

    public sealed class CommandSink<T> where T : struct
    {
        private readonly CommandBuffer<T> _buffer;
        public bool Aborted { get; private set; }
        public int Count => _buffer.Count;
        public int PeakCount => _buffer.PeakCount;
        public int OverflowCount => _buffer.OverflowCount;
        public CommandRejection LastRejection => _buffer.LastRejection;
        public T Get(int index) => _buffer.Get(index);
        public CommandSink(int capacity, int reserved = 0) { _buffer = new CommandBuffer<T>(capacity, reserved); }
        public bool Submit(T command, bool critical = false) { if (Aborted) return false; if (_buffer.TryAdd(command, critical)) return true; if (critical) Aborted = true; return false; }
        public void Sort(Comparison<T> comparison) => _buffer.Sort(comparison);
        public bool TryMerge(CommandSink<T> source, Comparison<T> comparison, bool critical = false) {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (Aborted) return false;
            bool ok = _buffer.TryMerge(source._buffer, comparison, critical);
            if (!ok && critical) Aborted = true;
            return ok;
        }
        public void Clear() { _buffer.Clear(); Aborted = false; }
        public void ResetDiagnostics() { _buffer.ResetDiagnostics(); Aborted = false; }
    }
}
