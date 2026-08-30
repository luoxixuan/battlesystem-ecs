using System;

namespace BattleSystemECS.Core.GAS
{
    public enum EffectPoolFailure { None, Capacity, InvalidIndex, StaleGeneration, Inactive }
    public sealed class EffectPool
    {
        private readonly int[] _generations;
        private readonly bool[] _active;
        private readonly int[] _nextFree;
        private int _freeHead;
        public int Capacity => _active.Length;
        public int AllocationFailures { get; private set; }
        public int InvalidResolveCount { get; private set; }
        public int StaleResolveCount { get; private set; }
        public int InactiveResolveCount { get; private set; }
        public HandleResolveFailure LastFailure { get; private set; }
        public EffectPoolFailure LastPoolFailure { get; private set; }
        public EffectPool(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _generations = new int[capacity];
            _active = new bool[capacity];
            _nextFree = new int[capacity];
            for (int i = 0; i < capacity - 1; i++) _nextFree[i] = i + 1;
            _nextFree[capacity - 1] = -1;
            _freeHead = 0;
        }
        public bool TryAllocate(out EffectHandle handle)
        {
            if (_freeHead < 0)
            {
                AllocationFailures++;
                handle = default(EffectHandle);
                LastFailure = HandleResolveFailure.Capacity;
                LastPoolFailure = EffectPoolFailure.Capacity;
                return false;
            }
            int index = _freeHead;
            _freeHead = _nextFree[index];
            _generations[index] = _generations[index] == int.MaxValue ? 1 : _generations[index] + 1;
            _active[index] = true;
            handle = new EffectHandle(index, _generations[index]);
            LastFailure = HandleResolveFailure.None;
            LastPoolFailure = EffectPoolFailure.None;
            return true;
        }
        public bool Release(EffectHandle handle) { return Release(handle, out _); }
        public bool Release(EffectHandle handle, out HandleResolveFailure failure)
        {
            if (!TryResolve(handle, out _, out failure)) return false;
            _active[handle.Index] = false;
            _nextFree[handle.Index] = _freeHead;
            _freeHead = handle.Index;
            LastFailure = HandleResolveFailure.None;
            LastPoolFailure = EffectPoolFailure.None;
            return true;
        }
        public bool TryResolve(EffectHandle handle, out int index) { return TryResolve(handle, out index, out _); }
        public bool TryResolve(EffectHandle handle, out int index, out HandleResolveFailure failure) {
            if (TryResolveReadOnly(handle, out index, out failure)) { LastFailure = HandleResolveFailure.None; LastPoolFailure = EffectPoolFailure.None; return true; }
            if (failure == HandleResolveFailure.InvalidIndex) { InvalidResolveCount++; LastPoolFailure = EffectPoolFailure.InvalidIndex; }
            else if (failure == HandleResolveFailure.StaleGeneration) { StaleResolveCount++; LastPoolFailure = EffectPoolFailure.StaleGeneration; }
            else { InactiveResolveCount++; LastPoolFailure = EffectPoolFailure.Inactive; }
            LastFailure = failure;
            return false;
        }

        internal bool TryResolveReadOnly(EffectHandle handle, out int index, out HandleResolveFailure failure)
        {
            index = handle.Index;
            if (index < 0 || index >= _active.Length || !handle.IsValid) { failure = HandleResolveFailure.InvalidIndex; return false; }
            if (_generations[index] != handle.Generation) { failure = HandleResolveFailure.StaleGeneration; return false; }
            if (!_active[index]) { failure = HandleResolveFailure.Inactive; return false; }
            failure = HandleResolveFailure.None;
            return true;
        }
    }
}
