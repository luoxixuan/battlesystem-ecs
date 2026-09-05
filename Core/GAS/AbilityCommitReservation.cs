using System;

namespace BattleSystemECS.Core.GAS
{
    /// <summary>
    /// 当帧 AbilityRequests 的资源/容量预留。不进存档。
    /// Commit 每条先 <see cref="Release"/> 再复查；队列清空时 <c>ClearAbilityQueue</c> 再 <see cref="Clear"/>。
    /// </summary>
    internal sealed class AbilityCommitReservation
    {
        public const int MaxQueue = 256;
        public const int MaxCostEntries = 1024;
        public const int MaxRuntimeEntries = 1024;
        public const int MaxCostScratch = 16;

        /// <summary>每 store 一份 Activate 暂存，禁止 static 跨 store 并行测试互踩。</summary>
        internal readonly int[] CostKeyScratch = new int[MaxCostScratch];
        internal readonly float[] CostAmountScratch = new float[MaxCostScratch];
        internal readonly int[] RuntimeTargetScratch = new int[MaxRuntimeEntries];

        internal struct CapacityNeed
        {
            public int EffectRequests;
            public int EffectEvents;
            public int DamageRequests;
            public int DamageEvents;
            public int ResourceRequests;
            public int ResourceEvents;
            public int Modifiers;
            public int RuntimeSlots;
        }

        private int _effectRequests, _effectEvents, _damageRequests, _damageEvents;
        private int _resourceRequests, _resourceEvents, _modifiers, _totalRuntime;
        private readonly int[] _runtimeEntities = new int[MaxRuntimeEntries];
        private readonly int[] _runtimeSlots = new int[MaxRuntimeEntries];
        private int _runtimeCount;
        private readonly int[] _costEntities = new int[MaxCostEntries];
        private readonly int[] _costKeys = new int[MaxCostEntries];
        private readonly float[] _costAmounts = new float[MaxCostEntries];
        private int _costCount;
        private readonly int[] _itemCostStart = new int[MaxQueue];
        private readonly int[] _itemCostCount = new int[MaxQueue];
        private readonly int[] _itemRuntimeStart = new int[MaxQueue];
        private readonly int[] _itemRuntimeCount = new int[MaxQueue];
        private readonly CapacityNeed[] _itemNeed = new CapacityNeed[MaxQueue];
        private readonly byte[] _itemActive = new byte[MaxQueue];

        public bool IsEmpty =>
            _costCount == 0 && _runtimeCount == 0 && _effectRequests == 0 && _effectEvents == 0 &&
            _damageRequests == 0 && _damageEvents == 0 && _resourceRequests == 0 &&
            _resourceEvents == 0 && _modifiers == 0 && _totalRuntime == 0;

        public int EffectRequests => _effectRequests;
        public int EffectEvents => _effectEvents;
        public int DamageRequests => _damageRequests;
        public int DamageEvents => _damageEvents;
        public int ResourceRequests => _resourceRequests;
        public int ResourceEvents => _resourceEvents;
        public int Modifiers => _modifiers;
        public int TotalRuntime => _totalRuntime;
        /// <summary>Release 把负计数夹到 0 的次数；生产应为 0，非零说明预留记账漂移。</summary>
        public int ReleaseUnderflowCount { get; private set; }

        public void Clear()
        {
            _effectRequests = _effectEvents = _damageRequests = _damageEvents = 0;
            _resourceRequests = _resourceEvents = _modifiers = _totalRuntime = 0;
            _runtimeCount = 0;
            _costCount = 0;
            Array.Clear(_itemActive, 0, _itemActive.Length);
        }

        public float PeekCost(int entityId, AttributeKey resource)
        {
            int key = resource.Value;
            float total = 0f;
            for (int i = 0; i < _costCount; i++)
                if (_costEntities[i] == entityId && _costKeys[i] == key) total += _costAmounts[i];
            return total;
        }

        public int RuntimeFor(int entityId)
        {
            int total = 0;
            for (int i = 0; i < _runtimeCount; i++)
                if (_runtimeEntities[i] == entityId) total += _runtimeSlots[i];
            return total;
        }

        public bool TryReserve(int queueIndex, CapacityNeed need, int sourceEntityId,
            int[] runtimeTargets, int runtimeTargetCount, int[] costKeys, float[] costAmounts, int costCount)
        {
            if ((uint)queueIndex >= MaxQueue) return false;
            if (costCount < 0 || runtimeTargetCount < 0) return false;
            if (_costCount > MaxCostEntries - costCount) return false;
            if (_runtimeCount > MaxRuntimeEntries - runtimeTargetCount) return false;
            _itemNeed[queueIndex] = need;
            _itemCostStart[queueIndex] = _costCount;
            _itemCostCount[queueIndex] = costCount;
            for (int i = 0; i < costCount; i++)
            {
                _costEntities[_costCount] = sourceEntityId;
                _costKeys[_costCount] = costKeys[i];
                _costAmounts[_costCount] = costAmounts[i];
                _costCount++;
            }
            _itemRuntimeStart[queueIndex] = _runtimeCount;
            _itemRuntimeCount[queueIndex] = runtimeTargetCount;
            for (int i = 0; i < runtimeTargetCount; i++)
            {
                _runtimeEntities[_runtimeCount] = runtimeTargets[i];
                _runtimeSlots[_runtimeCount] = need.RuntimeSlots;
                _runtimeCount++;
            }
            _effectRequests += need.EffectRequests;
            _effectEvents += need.EffectEvents;
            _damageRequests += need.DamageRequests;
            _damageEvents += need.DamageEvents;
            _resourceRequests += need.ResourceRequests;
            _resourceEvents += need.ResourceEvents;
            _modifiers += need.Modifiers;
            _totalRuntime += need.RuntimeSlots * runtimeTargetCount;
            _itemActive[queueIndex] = 1;
            return true;
        }

        public void Release(int queueIndex)
        {
            if ((uint)queueIndex >= MaxQueue || _itemActive[queueIndex] == 0) return;
            var need = _itemNeed[queueIndex];
            _effectRequests -= need.EffectRequests;
            _effectEvents -= need.EffectEvents;
            _damageRequests -= need.DamageRequests;
            _damageEvents -= need.DamageEvents;
            _resourceRequests -= need.ResourceRequests;
            _resourceEvents -= need.ResourceEvents;
            _modifiers -= need.Modifiers;
            int runtimeTargets = _itemRuntimeCount[queueIndex];
            _totalRuntime -= need.RuntimeSlots * runtimeTargets;
            bool underflow = false;
            if (_effectRequests < 0) { _effectRequests = 0; underflow = true; }
            if (_effectEvents < 0) { _effectEvents = 0; underflow = true; }
            if (_damageRequests < 0) { _damageRequests = 0; underflow = true; }
            if (_damageEvents < 0) { _damageEvents = 0; underflow = true; }
            if (_resourceRequests < 0) { _resourceRequests = 0; underflow = true; }
            if (_resourceEvents < 0) { _resourceEvents = 0; underflow = true; }
            if (_modifiers < 0) { _modifiers = 0; underflow = true; }
            if (_totalRuntime < 0) { _totalRuntime = 0; underflow = true; }
            if (underflow) ReleaseUnderflowCount++;
            int costStart = _itemCostStart[queueIndex];
            int costCount = _itemCostCount[queueIndex];
            for (int i = 0; i < costCount; i++) _costAmounts[costStart + i] = 0f;
            int runtimeStart = _itemRuntimeStart[queueIndex];
            for (int i = 0; i < runtimeTargets; i++) _runtimeSlots[runtimeStart + i] = 0;
            _itemActive[queueIndex] = 0;
        }
    }
}
