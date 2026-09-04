using System;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Buffers;

namespace BattleSystemECS.Core.GAS
{
    public sealed class GameplayEventQueue
    {
        internal interface IBufferPool
        {
            GameplayEvent[] Rent(int minimumLength);
            void Return(GameplayEvent[] buffer, bool clearArray);
        }

        private sealed class SharedBufferPool : IBufferPool
        {
            internal static readonly SharedBufferPool Instance = new SharedBufferPool();
            public GameplayEvent[] Rent(int minimumLength) => ArrayPool<GameplayEvent>.Shared.Rent(minimumLength);
            public void Return(GameplayEvent[] buffer, bool clearArray) => ArrayPool<GameplayEvent>.Shared.Return(buffer, clearArray);
        }

        private const ulong DigestOffset = 14695981039346656037UL;
        private const ulong DigestPrime = 1099511628211UL;
        private readonly CommandBuffer<GameplayEvent> _buffer;
        private readonly IBufferPool _bufferPool;
        private readonly object _sync = new object();
        private static readonly object TieLock = new object();
        private ulong _sequenceDigest = DigestOffset;
        private long _publishedCount;
        private int _reservedCriticalSlots;
        internal bool DigestEnabled { get; set; }
        public GameplayEventQueue(int capacity, int reserved = 0) : this(capacity, reserved, SharedBufferPool.Instance) { }
        internal GameplayEventQueue(int capacity, int reserved, IBufferPool bufferPool)
        {
            _buffer = new CommandBuffer<GameplayEvent>(capacity, reserved);
            _bufferPool = bufferPool ?? throw new ArgumentNullException(nameof(bufferPool));
        }
        public int Count { get { lock (_sync) return _buffer.Count; } }
        public int PeakCount { get { lock (_sync) return _buffer.PeakCount; } }
        public int Capacity => _buffer.Capacity;
        public int Reserved => _buffer.Reserved;
        public int OverflowCount { get { lock (_sync) return _buffer.OverflowCount; } }
        public CommandRejection LastRejection { get { lock (_sync) return _buffer.LastRejection; } }
        /// <summary>累计成功发布事实的确定性摘要；Clear 不会抹掉历史摘要。</summary>
        public ulong SequenceDigest { get { lock (_sync) return _sequenceDigest; } }
        public long PublishedCount { get { lock (_sync) return _publishedCount; } }
        internal Action BeforeBatchPublish { get; set; }
        internal bool TryPublishBatch(GameplayEvent[] values, bool critical = false)
        {
            if (values == null) throw new System.ArgumentNullException(nameof(values));
            if (values.Length == 0) return true;
            BeforeBatchPublish?.Invoke();
            lock (_sync)
            {
                if (!_buffer.CanAdd(values.Length + _reservedCriticalSlots, critical)) { _buffer.RecordCapacityRejection(critical); return false; }
                for (int i = 0; i < values.Length; i++)
                    if (!_buffer.TryAdd(values[i], critical)) return false;
                _publishedCount += values.Length;
                if (DigestEnabled) for (int i = 0; i < values.Length; i++) AddToDigest(values[i]);
                return true;
            }
        }

        internal bool TryPublishBatch(GameplayEvent first, bool critical)
        {
            BeforeBatchPublish?.Invoke();
            lock (_sync)
            {
                if (!_buffer.CanAdd(1 + _reservedCriticalSlots, critical)) { _buffer.RecordCapacityRejection(critical); return false; }
                AppendOneLocked(first, critical);
                return true;
            }
        }

        internal bool TryPublishBatch(GameplayEvent first, GameplayEvent second, bool critical)
        {
            BeforeBatchPublish?.Invoke();
            lock (_sync)
            {
                if (!_buffer.CanAdd(2 + _reservedCriticalSlots, critical)) { _buffer.RecordCapacityRejection(critical); return false; }
                AppendOneLocked(first, critical);
                AppendOneLocked(second, critical);
                return true;
            }
        }

        internal bool TryPublishBatch(GameplayEvent first, GameplayEvent second, GameplayEvent third, bool critical)
        {
            BeforeBatchPublish?.Invoke();
            lock (_sync)
            {
                if (!_buffer.CanAdd(3 + _reservedCriticalSlots, critical)) { _buffer.RecordCapacityRejection(critical); return false; }
                AppendOneLocked(first, critical);
                AppendOneLocked(second, critical);
                AppendOneLocked(third, critical);
                return true;
            }
        }

        internal static GameplayEventReservation TryReserveAtomic(GameplayEventQueue firstQueue, int firstCount,
            GameplayEventQueue secondQueue, int secondCount)
        {
            if (firstQueue == null || secondQueue == null) throw new ArgumentNullException();
            if (ReferenceEquals(firstQueue, secondQueue)) throw new ArgumentException("queues must differ");
            if (firstCount < 0 || secondCount < 0) throw new ArgumentOutOfRangeException();
            firstQueue.BeforeBatchPublish?.Invoke();
            secondQueue.BeforeBatchPublish?.Invoke();
            GameplayEvent[] firstBuffer = null;
            GameplayEvent[] secondBuffer = null;
            try
            {
                firstBuffer = firstQueue._bufferPool.Rent(Math.Max(1, firstCount));
                secondBuffer = secondQueue._bufferPool.Rent(Math.Max(1, secondCount));
            }
            catch
            {
                if (firstBuffer != null) firstQueue._bufferPool.Return(firstBuffer, true);
                if (secondBuffer != null) secondQueue._bufferPool.Return(secondBuffer, true);
                throw;
            }
            bool reserved;
            GetLockOrder(firstQueue, secondQueue, out object firstLock, out object secondLock, out bool tie);
            if (tie)
            {
                lock (TieLock) lock (firstLock) lock (secondLock) reserved = TryReserveLocked(firstQueue, firstCount, secondQueue, secondCount);
            }
            else lock (firstLock) lock (secondLock) reserved = TryReserveLocked(firstQueue, firstCount, secondQueue, secondCount);
            if (reserved) return new GameplayEventReservation(firstQueue, firstCount, firstBuffer,
                secondQueue, secondCount, secondBuffer);
            firstQueue._bufferPool.Return(firstBuffer, true);
            secondQueue._bufferPool.Return(secondBuffer, true);
            return null;
        }

        private static bool TryReserveLocked(GameplayEventQueue firstQueue, int firstCount,
            GameplayEventQueue secondQueue, int secondCount)
        {
            if (!firstQueue._buffer.CanAdd(firstCount + firstQueue._reservedCriticalSlots, true) ||
                !secondQueue._buffer.CanAdd(secondCount + secondQueue._reservedCriticalSlots, true)) return false;
            firstQueue._reservedCriticalSlots += firstCount;
            secondQueue._reservedCriticalSlots += secondCount;
            return true;
        }

        internal static void GetLockOrder(GameplayEventQueue firstQueue, GameplayEventQueue secondQueue,
            out object firstLock, out object secondLock, out bool tie) =>
            GetLockOrder(firstQueue, RuntimeHelpers.GetHashCode(firstQueue), secondQueue,
                RuntimeHelpers.GetHashCode(secondQueue), out firstLock, out secondLock, out tie);

        internal static void GetLockOrder(GameplayEventQueue firstQueue, int firstHash,
            GameplayEventQueue secondQueue, int secondHash, out object firstLock, out object secondLock, out bool tie)
        {
            tie = firstHash == secondHash;
            bool firstBeforeSecond = tie || firstHash < secondHash;
            firstLock = firstBeforeSecond ? firstQueue._sync : secondQueue._sync;
            secondLock = firstBeforeSecond ? secondQueue._sync : firstQueue._sync;
        }

        internal sealed class GameplayEventReservation : IDisposable
        {
            private readonly GameplayEventQueue _firstQueue, _secondQueue;
            private readonly GameplayEvent[] _first, _second;
            private readonly int _firstLength, _secondLength;
            private int _firstCount, _secondCount;
            private int _state;
            internal GameplayEventReservation(GameplayEventQueue firstQueue, int firstCount, GameplayEvent[] first,
                GameplayEventQueue secondQueue, int secondCount, GameplayEvent[] second)
            { _firstQueue = firstQueue; _secondQueue = secondQueue; _firstLength = firstCount; _secondLength = secondCount; _first = first; _second = second; }
            internal void StageFirst(GameplayEvent value) { if (Volatile.Read(ref _state) != 0 || _firstCount >= _firstLength) throw new InvalidOperationException("first reservation exceeded"); _first[_firstCount++] = value; }
            internal void StageSecond(GameplayEvent value) { if (Volatile.Read(ref _state) != 0 || _secondCount >= _secondLength) throw new InvalidOperationException("second reservation exceeded"); _second[_secondCount++] = value; }
            internal void Commit()
            {
                if (Volatile.Read(ref _state) == 2) return;
                if (_firstCount != _firstLength || _secondCount != _secondLength) throw new InvalidOperationException("reservation was not fully staged");
                if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;
                try { LockBoth(true); }
                finally { ReturnBuffers(); Volatile.Write(ref _state, 2); }
            }
            public void Dispose()
            {
                if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;
                try { LockBoth(false); }
                finally { ReturnBuffers(); Volatile.Write(ref _state, 2); }
            }
            private void Release() { _firstQueue._reservedCriticalSlots -= _firstLength; _secondQueue._reservedCriticalSlots -= _secondLength; }
            private void ReturnBuffers() { _firstQueue._bufferPool.Return(_first, true); _secondQueue._bufferPool.Return(_second, true); }
            private void LockBoth(bool commit)
            {
                GetLockOrder(_firstQueue, _secondQueue, out object firstLock, out object secondLock, out bool tie);
                if (tie)
                {
                    lock (TieLock) lock (firstLock) lock (secondLock) CompleteLocked(commit);
                    return;
                }
                lock (firstLock) lock (secondLock) CompleteLocked(commit);
            }
            private void CompleteLocked(bool commit)
            {
                try
                {
                    if (commit)
                    {
                        _firstQueue.AppendLocked(_first, _firstLength, true);
                        _secondQueue.AppendLocked(_second, _secondLength, true);
                    }
                }
                finally { Release(); }
            }
        }

        private void AppendLocked(GameplayEvent[] values, int count, bool critical)
        {
            for (int i = 0; i < count; i++)
            {
                if (!_buffer.TryAdd(values[i], critical)) throw new InvalidOperationException("queue reservation violated");
                _publishedCount++;
                if (DigestEnabled) AddToDigest(values[i]);
            }
        }
        private void AppendOneLocked(GameplayEvent value, bool critical)
        {
            if (!_buffer.TryAdd(value, critical)) throw new InvalidOperationException("queue capacity check violated");
            _publishedCount++;
            if (DigestEnabled) AddToDigest(value);
        }
        public bool TryPublish(GameplayEvent value, bool critical = false)
        {
            lock (_sync)
            {
                if (!_buffer.CanAdd(1 + _reservedCriticalSlots, critical)) { _buffer.RecordCapacityRejection(critical); return false; }
                if (!_buffer.TryAdd(value, critical)) return false;
                _publishedCount++;
                if (DigestEnabled) AddToDigest(value);
                return true;
            }
        }
        public bool CanPublish(int count, bool critical = false) { lock (_sync) return _buffer.CanAdd(count + _reservedCriticalSlots, critical); }
        public GameplayEvent Get(int index) { lock (_sync) return _buffer.Get(index); }
        public void Sort(System.Comparison<GameplayEvent> comparison) { lock (_sync) _buffer.Sort(comparison); }
        public bool TryMerge(GameplayEventQueue source, System.Comparison<GameplayEvent> comparison, bool critical = false)
        {
            if (source == null) throw new System.ArgumentNullException(nameof(source));
            if (ReferenceEquals(this, source)) throw new ArgumentException("source must differ from destination", nameof(source));
            GetLockOrder(this, source, out object first, out object second, out bool tie);
            if (tie)
            {
                lock (TieLock) lock (first) lock (second)
                {
                    return MergeLocked(source, comparison, critical);
                }
            }
            lock (first) lock (second)
            {
                return MergeLocked(source, comparison, critical);
            }
        }
        private bool MergeLocked(GameplayEventQueue source, System.Comparison<GameplayEvent> comparison, bool critical)
        {
            int sourceCount = source._buffer.Count;
            if (!_buffer.CanAdd(sourceCount + _reservedCriticalSlots, critical)) { _buffer.RecordCapacityRejection(critical); return false; }
            if (!_buffer.TryMerge(source._buffer, comparison, critical)) return false;
            _publishedCount += sourceCount;
            if (DigestEnabled) for (int i = 0; i < sourceCount; i++) AddToDigest(source._buffer.Get(i));
            return true;
        }
        public void Clear() { lock (_sync) _buffer.Clear(); }
        public void RemovePrefix(int count) { lock (_sync) _buffer.RemovePrefix(count); }
        public void RemoveAt(int index) { lock (_sync) _buffer.RemoveAt(index); }
        public void ResetDiagnostics() { lock (_sync) _buffer.ResetDiagnostics(); }

        private void AddToDigest(GameplayEvent value)
        {
            // 只有 Mutation 进 sequence digest；Initial/Replay 不得被当成又一次变化。
            if (value.Cause != GameplayEventCause.Mutation) return;
            unchecked
            {
                _sequenceDigest = Mix(_sequenceDigest, (ulong)value.Type);
                _sequenceDigest = Mix(_sequenceDigest, (ulong)value.Sequence);
                _sequenceDigest = Mix(_sequenceDigest, (ulong)value.ParentSequence);
                _sequenceDigest = Mix(_sequenceDigest, (ulong)value.Source.Index);
                _sequenceDigest = Mix(_sequenceDigest, (ulong)value.Source.Generation);
                _sequenceDigest = Mix(_sequenceDigest, (ulong)value.Target.Index);
                _sequenceDigest = Mix(_sequenceDigest, (ulong)value.Target.Generation);
                _sequenceDigest = Mix(_sequenceDigest, (ulong)value.Effect.Index);
                _sequenceDigest = Mix(_sequenceDigest, (ulong)value.Effect.Generation);
                _sequenceDigest = Mix(_sequenceDigest, (ulong)value.EffectDefinition.Value);
                _sequenceDigest = Mix(_sequenceDigest, (ulong)value.ProvenanceId);
                _sequenceDigest = Mix(_sequenceDigest, (ulong)value.ProvenanceDepth);
                _sequenceDigest = Mix(_sequenceDigest, (ulong)value.Reason);
                _sequenceDigest = Mix(_sequenceDigest, (ulong)value.OwnerPlayerId);
                _sequenceDigest = Mix(_sequenceDigest, (ulong)value.Flags);
                _sequenceDigest = Mix(_sequenceDigest, (ulong)value.Tag.Value);
                _sequenceDigest = Mix(_sequenceDigest, (ulong)value.ProducerIndex);
            }
        }

        private static ulong Mix(ulong hash, ulong value)
        {
            unchecked { return (hash ^ value) * DigestPrime; }
        }
    }
}
