using System;
using System.Collections.Generic;

namespace BattleSystemECS.Core
{
    /// <summary>
    /// Event Bus — 解耦系统间通信
    /// 系统发布事件，其他系统订阅感兴趣的事件类型
    /// 
    /// Usage:
    ///   bus.Subscribe("enemy_killed", data => { ... });
    ///   bus.Publish("enemy_killed", new { enemyId = 5, gold = 10 });
    /// </summary>
    public class EventBus
    {
        private static readonly EventBus _instance = new EventBus();
        public static EventBus Instance => _instance;
        private readonly Dictionary<string, List<Action<object>>> handlers
            = new Dictionary<string, List<Action<object>>>();

        /// <summary>
        /// Subscribe to an event type. Handler receives an object payload.
        /// </summary>
        public void Subscribe(string eventType, Action<object> handler)
        {
            if (!handlers.ContainsKey(eventType))
            {
                handlers[eventType] = new List<Action<object>>();
            }
            handlers[eventType].Add(handler);
        }

        /// <summary>
        /// Publish an event with optional payload data.
        /// All registered handlers for this event type are called.
        /// </summary>
        public void Publish(string eventType, object data = null)
        {
            // Fast path: no handlers registered for this event type at all
            if (handlers.Count == 0)
                return;

            if (handlers.TryGetValue(eventType, out var list))
            {
                // Iterate a copy in case handler unsubscribes during execution
                var snapshot = list.ToArray();
                foreach (var handler in snapshot)
                {
                    try
                    {
                        handler(data);
                    }
                    catch (Exception ex)
                    {
                        // Log but don't crash — one bad handler shouldn't break everything
                        Console.Error.WriteLine($"[EventBus] Handler error for '{eventType}': {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Unsubscribe a handler from an event type.
        /// </summary>
        public void Unsubscribe(string eventType, Action<object> handler)
        {
            if (handlers.TryGetValue(eventType, out var list))
            {
                list.Remove(handler);
            }
        }

        /// <summary>
        /// Clear all handlers (useful for reset between levels).
        /// </summary>
        public void Clear()
        {
            handlers.Clear();
        }

        /// <summary>
        /// Get subscriber count for a specific event type.
        /// </summary>
        public int SubscriberCount(string eventType)
        {
            return handlers.TryGetValue(eventType, out var list) ? list.Count : 0;
        }

        /// <summary>
        /// Total number of event types registered.
        /// </summary>
        public int EventTypeCount => handlers.Count;
    }
}
