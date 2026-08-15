#nullable enable
using System;

namespace BattleSystemECS.Core
{
    /// <summary>
    /// Type-safe, allocation-free event channel.
    ///
    /// Replaces the previous string-keyed <c>IEventBus</c> (a
    /// <c>Dictionary&lt;string, List&lt;Action&lt;object&gt;&gt;&gt;</c> guarded by a lock
    /// that snapshotted its handler list with <c>ToArray()</c> on every publish). Each
    /// game event is now a compile-time-typed channel, so:
    ///   - event names are checked at compile time (no magic-string typos that silently
    ///     disable every subscriber);
    ///   - payloads are strongly typed (no <c>object</c> payload, no runtime casts);
    ///   - <c>Publish</c> is a single multicast-delegate invocation — no lock, no
    ///     dictionary lookup, no per-publish allocation.
    ///
    /// Threading contract (matches the codebase's actual two-phase usage):
    ///   - <c>Subscribe</c>/<c>Unsubscribe</c> run at construction/teardown time
    ///     (single-threaded);
    ///   - <c>Publish</c> runs from the serial / main-thread phase only. Do NOT call
    ///     it from inside a <c>Parallel.For</c> — parallel systems collect events and
    ///     publish them serially (the two-phase pattern).
    /// </summary>
    public sealed class EventChannel<T>
    {
        private Action<T>? _handlers;

        /// <summary>Register a handler. Called at init; not thread-safe.</summary>
        public void Subscribe(Action<T> handler)
        {
            _handlers += handler;
        }

        /// <summary>Remove a handler. Called at teardown; not thread-safe.</summary>
        public void Unsubscribe(Action<T> handler)
        {
            _handlers -= handler;
        }

        /// <summary>
        /// Publish a payload. Zero-allocation: one multicast-delegate invoke when
        /// there are subscribers, otherwise a single null check.
        /// </summary>
        public void Publish(T payload)
        {
            _handlers?.Invoke(payload);
        }

        /// <summary>Number of subscribers (diagnostics / tests only).</summary>
        public int SubscriberCount => _handlers?.GetInvocationList().Length ?? 0;

        /// <summary>Drop all subscribers (used by <see cref="EventBus.Reset"/>).</summary>
        public void Clear()
        {
            _handlers = null;
        }
    }

    /// <summary>
    /// The single inter-system event bus: one typed channel per game event.
    /// Systems receive this instance via constructor injection (see SystemRegistry).
    ///
    /// Kept distinct from <see cref="IBattleEventBus"/>, which is the logic→render
    /// boundary consumed by the Unity view layer.
    /// </summary>
    public sealed class EventBus
    {
        public readonly EventChannel<PlayerDamagedEvent> PlayerDamaged = new EventChannel<PlayerDamagedEvent>();
        public readonly EventChannel<EnemyHitEvent> EnemyHit = new EventChannel<EnemyHitEvent>();
        public readonly EventChannel<EnemyHitEvent> EnemyCrit = new EventChannel<EnemyHitEvent>();
        public readonly EventChannel<EnemyChargingEvent> EnemyCharging = new EventChannel<EnemyChargingEvent>();
        public readonly EventChannel<EnemyChargeReleasedEvent> EnemyChargeReleased = new EventChannel<EnemyChargeReleasedEvent>();
        public readonly EventChannel<BossPhaseChangedEvent> BossPhaseChanged = new EventChannel<BossPhaseChangedEvent>();
        public readonly EventChannel<SideQuestCompletedEvent> SideQuestCompleted = new EventChannel<SideQuestCompletedEvent>();

        /// <summary>Number of event channels on this bus.</summary>
        public int EventTypeCount => 7;

        /// <summary>Clear all subscribers (start of a new game / level).</summary>
        public void Reset()
        {
            PlayerDamaged.Clear();
            EnemyHit.Clear();
            EnemyCrit.Clear();
            EnemyCharging.Clear();
            EnemyChargeReleased.Clear();
            BossPhaseChanged.Clear();
            SideQuestCompleted.Clear();
        }
    }
}
