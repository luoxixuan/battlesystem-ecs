using System;

namespace BattleSystemECS.Core
{
    /// <summary>
    /// Event bus interface — decouples inter-system communication.
    /// Systems subscribe to event types and publish events for others to consume.
    ///
    /// Thread-safety: implementations must be thread-safe for Subscribe/Unsubscribe/Clear.
    /// Publish is safe for main-thread or serial-only contexts.
    /// </summary>
    public interface IEventBus
    {
        void Subscribe(string eventType, Action<object> handler);
        void Publish(string eventType, object data = null);
        void Unsubscribe(string eventType, Action<object> handler);
        void Clear();
        void Reset();
        int SubscriberCount(string eventType);
        int EventTypeCount { get; }
    }
}
