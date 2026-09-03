using System;

namespace BattleSystemECS.Core.GAS
{
    /// <summary>活动效果运行态的唯一 owner；句柄索引直接定位槽位，所有操作均为 O(1)。</summary>
    public sealed class ActiveGameplayEffectStore
    {
        private const int PageSize = 256;
        private readonly ActiveGameplayEffect[][] _runtimePages;
        private readonly GameplayEffectDefinition[][] _definitionPages;
        private readonly LegacyEffectSnapshot[][] _legacySnapshotPages;
        private int _allocatedPageCount;

        public EffectPool Handles { get; }
        public int AllocatedPageCount => _allocatedPageCount;
        public int AllocatedSlotCapacity => Math.Min(Handles.Capacity, _allocatedPageCount * PageSize);

        public ActiveGameplayEffectStore(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            Handles = new EffectPool(capacity);
            int pageCount = (capacity + PageSize - 1) / PageSize;
            _runtimePages = new ActiveGameplayEffect[pageCount][];
            _definitionPages = new GameplayEffectDefinition[pageCount][];
            _legacySnapshotPages = new LegacyEffectSnapshot[pageCount][];
        }

        public bool TryAdd(GameplayEffectApplication application, out EffectHandle handle)
        {
            if (!Handles.TryAllocate(out handle)) return false;
            EnsurePage(handle.Index);
            var runtime = application.Runtime;
            runtime.Handle = handle;
            int page = handle.Index / PageSize;
            int offset = handle.Index % PageSize;
            _runtimePages[page][offset] = runtime;
            _definitionPages[page][offset] = application.Definition;
            _legacySnapshotPages[page][offset] = application.LegacySnapshot;
            return true;
        }

        public bool TryGet(EffectHandle handle, out ActiveGameplayEffect runtime, out GameplayEffectDefinition definition, out LegacyEffectSnapshot legacySnapshot)
        {
            if (!Handles.TryResolveReadOnly(handle, out int index, out _))
            {
                runtime = default(ActiveGameplayEffect);
                definition = default(GameplayEffectDefinition);
                legacySnapshot = default(LegacyEffectSnapshot);
                return false;
            }
            int page = index / PageSize;
            int offset = index % PageSize;
            runtime = _runtimePages[page][offset];
            definition = _definitionPages[page][offset];
            legacySnapshot = _legacySnapshotPages[page][offset];
            return true;
        }

        public bool TryUpdate(EffectHandle handle, ActiveGameplayEffect runtime)
        {
            if (!Handles.TryResolveReadOnly(handle, out int index, out _)) return false;
            int page = index / PageSize;
            int offset = index % PageSize;
            if (!runtime.Handle.Equals(handle) || !runtime.DefinitionId.Equals(_definitionPages[page][offset].Id)) return false;
            runtime.Handle = handle;
            _runtimePages[page][offset] = runtime;
            return true;
        }

        public bool Release(EffectHandle handle)
        {
            if (!Handles.Release(handle)) return false;
            int page = handle.Index / PageSize;
            int offset = handle.Index % PageSize;
            _runtimePages[page][offset] = default(ActiveGameplayEffect);
            _definitionPages[page][offset] = default(GameplayEffectDefinition);
            _legacySnapshotPages[page][offset] = default(LegacyEffectSnapshot);
            return true;
        }

        private void EnsurePage(int index)
        {
            int page = index / PageSize;
            if (_runtimePages[page] != null) return;
            _runtimePages[page] = new ActiveGameplayEffect[PageSize];
            _definitionPages[page] = new GameplayEffectDefinition[PageSize];
            _legacySnapshotPages[page] = new LegacyEffectSnapshot[PageSize];
            _allocatedPageCount++;
        }
    }
}
