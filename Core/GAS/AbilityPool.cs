using System;

namespace BattleSystemECS.Core.GAS
{
    public enum AbilityPoolFailure { None, Capacity, InvalidIndex, StaleGeneration, Inactive }

    public sealed class AbilityPool
    {
        private const int PageSize = 256;
        private readonly int _capacity;
        private readonly int[][] _generationPages;
        private readonly bool[][] _activePages;
        private readonly int[][] _nextFreePages;
        private int _freeHead = -1;
        private int _nextUnallocated;
        private int _activeCount;
        private int _allocatedPageCount;
        public int Capacity => _capacity;
        public int FreeCount => Capacity - _activeCount;
        public int ActiveCount => _activeCount;
        public int PeakActiveCount { get; private set; }
        public int AllocatedPageCount => _allocatedPageCount;
        public int AllocatedSlotCapacity => Math.Min(Capacity, _allocatedPageCount * PageSize);
        public int AllocationFailures { get; private set; }
        public int InvalidResolveCount { get; private set; }
        public int StaleResolveCount { get; private set; }
        public int InactiveResolveCount { get; private set; }
        public HandleResolveFailure LastFailure { get; private set; }
        public AbilityPoolFailure LastPoolFailure { get; private set; }

        public AbilityPool(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
            int pageCount = (capacity + PageSize - 1) / PageSize;
            _generationPages = new int[pageCount][];
            _activePages = new bool[pageCount][];
            _nextFreePages = new int[pageCount][];
        }

        public bool TryAllocate(out AbilityHandle handle)
        {
            int index;
            if (_freeHead >= 0)
            {
                index = _freeHead;
                int page = index / PageSize;
                int offset = index % PageSize;
                _freeHead = _nextFreePages[page][offset];
            }
            else if (_nextUnallocated < Capacity)
            {
                index = _nextUnallocated++;
                EnsurePage(index);
            }
            else
            {
                AllocationFailures++;
                handle = default(AbilityHandle);
                LastFailure = HandleResolveFailure.Capacity;
                LastPoolFailure = AbilityPoolFailure.Capacity;
                return false;
            }
            int generationPage = index / PageSize;
            int generationOffset = index % PageSize;
            int generation = _generationPages[generationPage][generationOffset];
            generation = generation == int.MaxValue ? 1 : generation + 1;
            _generationPages[generationPage][generationOffset] = generation;
            _activePages[generationPage][generationOffset] = true;
            _activeCount++;
            if (ActiveCount > PeakActiveCount) PeakActiveCount = ActiveCount;
            handle = new AbilityHandle(index, generation);
            LastFailure = HandleResolveFailure.None;
            LastPoolFailure = AbilityPoolFailure.None;
            return true;
        }

        public void ResetDiagnostics()
        {
            PeakActiveCount = ActiveCount;
            AllocationFailures = 0;
            InvalidResolveCount = 0;
            StaleResolveCount = 0;
            InactiveResolveCount = 0;
            LastFailure = HandleResolveFailure.None;
            LastPoolFailure = AbilityPoolFailure.None;
        }

        public bool Release(AbilityHandle handle) => Release(handle, out _);

        public bool Release(AbilityHandle handle, out HandleResolveFailure failure)
        {
            if (!TryResolve(handle, out _, out failure)) return false;
            int page = handle.Index / PageSize;
            int offset = handle.Index % PageSize;
            _activePages[page][offset] = false;
            _activeCount--;
            _nextFreePages[page][offset] = _freeHead;
            _freeHead = handle.Index;
            LastFailure = HandleResolveFailure.None;
            LastPoolFailure = AbilityPoolFailure.None;
            return true;
        }

        public bool TryResolve(AbilityHandle handle, out int index) => TryResolve(handle, out index, out _);

        public bool TryResolve(AbilityHandle handle, out int index, out HandleResolveFailure failure)
        {
            if (TryResolveReadOnly(handle, out index, out failure))
            {
                LastFailure = HandleResolveFailure.None;
                LastPoolFailure = AbilityPoolFailure.None;
                return true;
            }
            if (failure == HandleResolveFailure.InvalidIndex) { InvalidResolveCount++; LastPoolFailure = AbilityPoolFailure.InvalidIndex; }
            else if (failure == HandleResolveFailure.StaleGeneration) { StaleResolveCount++; LastPoolFailure = AbilityPoolFailure.StaleGeneration; }
            else { InactiveResolveCount++; LastPoolFailure = AbilityPoolFailure.Inactive; }
            LastFailure = failure;
            return false;
        }

        internal bool TryResolveReadOnly(AbilityHandle handle, out int index, out HandleResolveFailure failure)
        {
            index = handle.Index;
            if (index < 0 || index >= Capacity || !handle.IsValid) { failure = HandleResolveFailure.InvalidIndex; return false; }
            if (index >= _nextUnallocated) { failure = HandleResolveFailure.StaleGeneration; return false; }
            int page = index / PageSize;
            int offset = index % PageSize;
            if (_generationPages[page][offset] != handle.Generation) { failure = HandleResolveFailure.StaleGeneration; return false; }
            if (!_activePages[page][offset]) { failure = HandleResolveFailure.Inactive; return false; }
            failure = HandleResolveFailure.None;
            return true;
        }

        private void EnsurePage(int index)
        {
            int page = index / PageSize;
            if (_generationPages[page] != null) return;
            _generationPages[page] = new int[PageSize];
            _activePages[page] = new bool[PageSize];
            _nextFreePages[page] = new int[PageSize];
            for (int i = 0; i < PageSize; i++) _nextFreePages[page][i] = -1;
            _allocatedPageCount++;
        }
    }
}
