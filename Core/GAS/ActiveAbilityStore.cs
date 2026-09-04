using System;

namespace BattleSystemECS.Core.GAS
{
    /// <summary>稀疏 AbilityState 池；实体槽位通过 generation handle 定位，Unity 仍读 AbilityInstances 投影。</summary>
    public sealed class ActiveAbilityStore
    {
        private const int PageSize = 256;
        private readonly AbilityInstance[][] _instancePages;
        private int _allocatedPageCount;

        public AbilityPool Handles { get; }
        public int AllocatedPageCount => _allocatedPageCount;
        public int AllocatedSlotCapacity => Math.Min(Handles.Capacity, _allocatedPageCount * PageSize);

        public ActiveAbilityStore(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            Handles = new AbilityPool(capacity);
            int pageCount = (capacity + PageSize - 1) / PageSize;
            _instancePages = new AbilityInstance[pageCount][];
        }

        public bool TryAdd(AbilityInstance instance, out AbilityHandle handle)
        {
            if (!Handles.TryAllocate(out handle)) return false;
            EnsurePage(handle.Index);
            int page = handle.Index / PageSize;
            int offset = handle.Index % PageSize;
            _instancePages[page][offset] = instance;
            return true;
        }

        public bool TryGet(AbilityHandle handle, out AbilityInstance instance)
        {
            if (!Handles.TryResolveReadOnly(handle, out int index, out _))
            {
                instance = default(AbilityInstance);
                return false;
            }
            int page = index / PageSize;
            int offset = index % PageSize;
            instance = _instancePages[page][offset];
            return true;
        }

        public bool TryUpdate(AbilityHandle handle, AbilityInstance instance)
        {
            if (!Handles.TryResolveReadOnly(handle, out int index, out _)) return false;
            int page = index / PageSize;
            int offset = index % PageSize;
            _instancePages[page][offset] = instance;
            return true;
        }

        public bool Release(AbilityHandle handle)
        {
            if (!Handles.Release(handle)) return false;
            int page = handle.Index / PageSize;
            int offset = handle.Index % PageSize;
            _instancePages[page][offset] = default(AbilityInstance);
            return true;
        }

        private void EnsurePage(int index)
        {
            int page = index / PageSize;
            if (_instancePages[page] != null) return;
            _instancePages[page] = new AbilityInstance[PageSize];
            _allocatedPageCount++;
        }
    }
}
