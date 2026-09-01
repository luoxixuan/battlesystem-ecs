namespace BattleSystemECS.Core.GAS
{
    public sealed class GameplayEventQueue
    {
        private readonly CommandBuffer<GameplayEvent> _buffer;
        public GameplayEventQueue(int capacity, int reserved = 0) { _buffer = new CommandBuffer<GameplayEvent>(capacity, reserved); }
        public int Count => _buffer.Count;
        public int Capacity => _buffer.Capacity;
        public int Reserved => _buffer.Reserved;
        public int OverflowCount => _buffer.OverflowCount;
        public CommandRejection LastRejection => _buffer.LastRejection;
        public bool TryPublish(GameplayEvent value, bool critical = false) => _buffer.TryAdd(value, critical);
        public bool CanPublish(int count, bool critical = false) => _buffer.CanAdd(count, critical);
        public GameplayEvent Get(int index) => _buffer.Get(index);
        public void Sort(System.Comparison<GameplayEvent> comparison) => _buffer.Sort(comparison);
        public bool TryMerge(GameplayEventQueue source, System.Comparison<GameplayEvent> comparison, bool critical = false) { if (source == null) throw new System.ArgumentNullException(nameof(source)); return _buffer.TryMerge(source._buffer, comparison, critical); }
        public void Clear() => _buffer.Clear();
        public void RemovePrefix(int count) => _buffer.RemovePrefix(count);
        public void RemoveAt(int index) => _buffer.RemoveAt(index);
        public void ResetDiagnostics() => _buffer.ResetOverflowCount();
    }
}
