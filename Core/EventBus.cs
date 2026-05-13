using System;
using System.Collections.Generic;

namespace BattleSystemECS.Core
{
    /// <summary>
    /// Event Bus — 解耦系统间通信
    /// 系统发布事件，其他系统订阅感兴趣的事件类型
    ///
    /// Thread-safety:
    ///   All public methods use an internal lock. Subscribe/Unsubscribe/Clear are
    ///   thread-safe. Publish is safe for concurrent calls from main thread or
    ///   serial-only contexts.
    ///
    /// Usage:
    ///   bus.Subscribe("enemy_killed", data => { ... });
    ///   bus.Publish("enemy_killed", new { enemyId = 5, gold = 10 });
    ///   bus.Reset(); // call at start of each game turn / level
    /// </summary>
    public class EventBus
    {
        private static readonly EventBus _instance = new EventBus();
        public static EventBus Instance => _instance;

        private readonly Dictionary<string, List<Action<object>>> _handlers =
            new Dictionary<string, List<Action<object>>>();

        private readonly object _lock = new object();

        /// <summary>
        /// Subscribe to an event type. Handler receives an object payload.
        /// Thread-safe.
        /// </summary>
        public void Subscribe(string eventType, Action<object> handler)
        {
            lock (_lock)
            {
                if (!_handlers.ContainsKey(eventType))
                    _handlers[eventType] = new List<Action<object>>();
                _handlers[eventType].Add(handler);
            }
        }

        /// <summary>
        /// Publish an event with optional payload data.
        /// All registered handlers for this event type are called.
        ///
        /// Thread-safe: iterates over a snapshot copy to prevent concurrent modification
        /// during handler execution.
        ///
        /// NOTE: Do NOT call Publish from within a Parallel.For — events in parallel
        /// execution contexts should be queued and dispatched serially on the main thread.
        /// This EventBus is designed for main-thread / serial event dispatch only.
        ///
        /// NOTE: Handlers are invoked inside the lock — keep them fast. For expensive
        /// operations, queue results and process after lock release.
        /// </summary>
        public void Publish(string eventType, object data = null)
        {
            lock (_lock)
            {
                if (_handlers.Count == 0) return;
                if (!_handlers.TryGetValue(eventType, out var list)) return;
                var snapshot = list.ToArray();
                foreach (var handler in snapshot)
                {
                    try { handler(data); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[EventBus] Handler error for '{eventType}': {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Unsubscribe a handler from an event type. Thread-safe.
        /// </summary>
        public void Unsubscribe(string eventType, Action<object> handler)
        {
            lock (_lock)
            {
                if (_handlers.TryGetValue(eventType, out var list))
                    list.Remove(handler);
            }
        }

        /// <summary>
        /// Clear all handlers (useful for reset between levels or tests).
        /// Thread-safe.
        /// </summary>
        public void Clear()
        {
            lock (_lock) { _handlers.Clear(); }
        }

        /// <summary>
        /// Alias for Clear — resets the EventBus to a clean state.
        /// Call this at the start of each game turn or level to prevent stale subscriptions.
        /// </summary>
        public void Reset() => Clear();

        /// <summary>
        /// Get subscriber count for a specific event type.
        /// </summary>
        public int SubscriberCount(string eventType)
        {
            lock (_lock)
                return _handlers.TryGetValue(eventType, out var list) ? list.Count : 0;
        }

        /// <summary>
        /// Total number of event types registered.
        /// </summary>
        public int EventTypeCount
        {
            get { lock (_lock) return _handlers.Count; }
        }
    }
}