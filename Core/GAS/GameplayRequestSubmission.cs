using BattleSystemECS.Core;

namespace BattleSystemECS.Core.GAS
{
    public sealed class GameplayRequestSubmissionSession
    {
        public const int InvalidSource = 1;
        public const int InvalidTarget = 2;
        private long _lastRejectedSequence = long.MinValue;

        public bool TrySubmit(DamageRequest request, ComponentStore store, GameplayEventQueue events, out GameplayEvent committed)
        {
            committed = default(GameplayEvent);
            if (store == null || events == null) return false;
            if (!store.TryResolve(request.Source, out _, out var sourceFailure)) return Reject(request, events, InvalidSource + (int)sourceFailure * 10, out committed);
            if (!store.TryResolve(request.Target, out _, out var targetFailure)) return Reject(request, events, InvalidTarget + (int)targetFailure * 10, out committed);
            var candidate = new GameplayEvent(GameplayEventType.DamageApplied, request.Source, request.Target, request.Sequence);
            if (!events.TryPublish(candidate, true)) { committed = default(GameplayEvent); return false; }
            committed = candidate; return true;
        }

        private bool Reject(DamageRequest request, GameplayEventQueue events, int reason, out GameplayEvent committed)
        {
            committed = new GameplayEvent(GameplayEventType.DamageBlocked, request.Source, request.Target, request.Sequence, reason);
            if (request.Sequence == _lastRejectedSequence) return false;
            _lastRejectedSequence = request.Sequence;
            events.TryPublish(committed, true);
            return false;
        }
    }
}
