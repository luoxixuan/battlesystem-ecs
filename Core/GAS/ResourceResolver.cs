using System;
using System.Collections.Generic;
using System.Threading;
using BattleSystemECS.Core;
using BattleSystemECS.Components;

namespace BattleSystemECS.Core.GAS
{
    public enum ResourceKind { CurrentHealth, MaxHealth, Shield, Mana, Gold }
    public enum ResourceRejectionReason { None, UnknownResource, InvalidValue, InvalidTarget, InvalidSource, InvalidOwner, UnsupportedOperation, InvalidOperation, TargetAlreadyDead, RequestQueueOverflow, UnconsumedRequests }
    public readonly struct ResourceApplyResult
    {
        public readonly bool Accepted; public readonly float Applied; public readonly bool Deferred; public readonly ResourceRejectionReason Reason;
        public ResourceApplyResult(bool accepted, float applied, ResourceRejectionReason reason, bool deferred = false) { Accepted = accepted; Applied = applied; Deferred = deferred; Reason = reason; }
    }

    public readonly struct ResourcePolicy
    {
        public readonly ResourceKind Kind;
        public readonly bool AllowsNegative;
        public readonly bool ClampToMaximum;
        public ResourcePolicy(ResourceKind kind, bool allowsNegative = false, bool clampToMaximum = true)
        { Kind = kind; AllowsNegative = allowsNegative; ClampToMaximum = clampToMaximum; }
        public float Clamp(float value, float maximum)
        { if (!AllowsNegative && value < 0f) value = 0f; return ClampToMaximum ? Math.Min(value, Math.Max(0f, maximum)) : value; }
    }

    /// <summary>唯一可变资源写入边界。普通属性聚合器不会写资源列。</summary>
    public sealed class ResourceResolver
    {
        public const int MaxPendingRequests = 4096;
        private readonly ComponentStore _store;
        private ResourceRejectionReason _lastRejectionReason;
        public ResourceRejectionReason LastRejectionReason { get { lock (_diagnosticsLock) return _lastRejectionReason; } }
        private int _rejectedCount, _requestOverflowCount, _unconsumedRequestCount, _eventPublicationFailed, _eventPublicationFailureCount;
        private int _staleHandleRejectedCount, _peakPendingRequestCount;
        private readonly int[] _rejectionsByReason = new int[Enum.GetValues(typeof(ResourceRejectionReason)).Length];
        private readonly object _diagnosticsLock = new object();
        public int RejectedCount => Volatile.Read(ref _rejectedCount);
        public int StaleHandleRejectedCount => Volatile.Read(ref _staleHandleRejectedCount);
        public int GetRejectionCount(ResourceRejectionReason reason)
        {
            int index = (int)reason;
            return (uint)index < (uint)_rejectionsByReason.Length
                ? Volatile.Read(ref _rejectionsByReason[index])
                : 0;
        }
        public GameplayEventQueue Events { get; } = new GameplayEventQueue(8192, 64);
        internal Action<long, bool> BeforeStateCommit { get; set; }
        public int EventOverflowCount => Events.OverflowCount;
        public CommandRejection LastEventRejection => Events.LastRejection;
        public bool LastEventPublicationFailed => Volatile.Read(ref _eventPublicationFailed) != 0;
        public int EventPublicationFailures => Volatile.Read(ref _eventPublicationFailureCount);
        private bool _deferred;
        private bool _isCommitting;
        // 与 DamageResolver 共用 store 级提交锁；提交区内保持状态与关键事实原子一致。
        private readonly object _eventCommitLock;
        private readonly object _pendingLock = new object();
        private readonly List<ResourceRequest> _pending = new List<ResourceRequest>(256);
        public int PendingRequestCount { get { lock (_pendingLock) return _pending.Count; } }
        public int PeakPendingRequestCount => Volatile.Read(ref _peakPendingRequestCount);
        public int RequestOverflowCount => Volatile.Read(ref _requestOverflowCount);
        public int UnconsumedRequestCount => Volatile.Read(ref _unconsumedRequestCount);
        public ResourceResolver(ComponentStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _eventCommitLock = store.GameplayCommitLock;
        }
        internal bool CanAccept(int requestCount, int criticalEventCount)
        {
            if (requestCount < 0 || criticalEventCount < 0 || !Events.CanPublish(criticalEventCount, true)) return false;
            if (!_deferred) return true;
            lock (_pendingLock) return _pending.Count <= MaxPendingRequests - requestCount;
        }

        internal bool CanApplyPlayerDamage(PlayerDamageRequest request)
        {
            if (!_store.TryResolve(request.Source, out _, out _) ||
                !_store.TryResolve(request.Target, out int targetId, out _) ||
                (uint)targetId >= ComponentStore.MAX_PLAYERS || !_store.PositionActive[targetId] ||
                _store.PlayerCurrentHealth[targetId] <= 0f || request.OwnerPlayerId != targetId ||
                request.RawAmount <= 0f || float.IsNaN(request.RawAmount) || float.IsInfinity(request.RawAmount))
                return false;
            return true;
        }

        internal ResourceApplyResult TryApply(PlayerDamageRequest request)
        {
            lock (_eventCommitLock)
            {
                if (!CanApplyPlayerDamage(request))
                {
                    RecordRejection(ResourceRejectionReason.InvalidTarget);
                    return new ResourceApplyResult(false, 0f, ResourceRejectionReason.InvalidTarget);
                }
                int playerId = request.Target.Index;
                float beforeHealth = _store.PlayerCurrentHealth[playerId];
                float beforeShield = _store.PlayerShield[playerId];
                float beforeManaShield = _store.PlayerManaShield[playerId];
                bool beforeManaShieldTriggered = _store.PlayerManaShieldTriggered[playerId];
                int beforeReincarnationCharges = _store.PlayerReincarnationCharges[playerId];
                bool beforeHasReincarnated = _store.PlayerHasReincarnated[playerId];
                _store.DecreasePlayerHealth(playerId, request.RawAmount);
                float applied = Math.Max(0f, beforeHealth - _store.PlayerCurrentHealth[playerId]) +
                                Math.Max(0f, beforeShield - _store.PlayerShield[playerId]);
                var damage = new GameplayEvent(GameplayEventType.DamageApplied, request.Source, request.Target,
                    request.Sequence, ownerPlayerId: request.OwnerPlayerId);
                bool lethal = _store.PlayerCurrentHealth[playerId] <= 0f;
                bool published = lethal
                    ? Events.TryPublishBatch(damage, new GameplayEvent(GameplayEventType.DeathQueued,
                        request.Source, request.Target, request.Sequence, ownerPlayerId: request.OwnerPlayerId), true)
                    : Events.TryPublishBatch(damage, true);
                if (published) return new ResourceApplyResult(true, applied, ResourceRejectionReason.None);
                _store.PlayerCurrentHealth[playerId] = beforeHealth;
                _store.PlayerShield[playerId] = beforeShield;
                _store.PlayerManaShield[playerId] = beforeManaShield;
                _store.PlayerManaShieldTriggered[playerId] = beforeManaShieldTriggered;
                _store.PlayerReincarnationCharges[playerId] = beforeReincarnationCharges;
                _store.PlayerHasReincarnated[playerId] = beforeHasReincarnated;
                RecordRejection(ResourceRejectionReason.RequestQueueOverflow);
                return new ResourceApplyResult(false, 0f, ResourceRejectionReason.RequestQueueOverflow);
            }
        }

        internal ResourceApplyResult TryApply(ShieldRequest request, int ownerPlayerId)
        {
            ResourceApplyResult RejectShield(ResourceRejectionReason reason)
            {
                RecordRejection(reason);
                return new ResourceApplyResult(false, 0f, reason);
            }

            if (ownerPlayerId < 0 || ownerPlayerId >= ComponentStore.MAX_PLAYERS)
                return RejectShield(ResourceRejectionReason.InvalidOwner);
            if (request.Amount <= 0f || request.Duration < 0f ||
                float.IsNaN(request.Amount) || float.IsInfinity(request.Amount) ||
                float.IsNaN(request.Duration) || float.IsInfinity(request.Duration) || request.Clock != ClockId.Combat)
                return RejectShield(ResourceRejectionReason.InvalidValue);
            lock (_eventCommitLock)
            {
                if (!_store.TryResolve(request.Source, out _, out _) ||
                    !_store.TryResolve(request.Target, out int targetId, out _) ||
                    (uint)targetId >= ComponentStore.MAX_PLAYERS || !_store.PositionActive[targetId])
                    return RejectShield(ResourceRejectionReason.InvalidTarget);
                float before = _store.PlayerShield[targetId];
                float beforeDuration = _store.PlayerShieldDuration[targetId];
                _store.PlayerShield[targetId] = Math.Max(0f, before + request.Amount);
                if (request.Duration > beforeDuration) _store.PlayerShieldDuration[targetId] = request.Duration;
                if (Events.TryPublishBatch(new GameplayEvent(GameplayEventType.ShieldChanged, request.Source, request.Target,
                    request.Sequence, ownerPlayerId: ownerPlayerId), true))
                    return new ResourceApplyResult(true, _store.PlayerShield[targetId] - before, ResourceRejectionReason.None);
                _store.PlayerShield[targetId] = before;
                _store.PlayerShieldDuration[targetId] = beforeDuration;
                return RejectShield(ResourceRejectionReason.RequestQueueOverflow);
            }
        }

        internal void TickTimedShields(float deltaTime, ClockId clock)
        {
            if (clock != ClockId.Combat || deltaTime <= 0f) return;
            for (int playerId = 0; playerId < ComponentStore.MAX_PLAYERS; playerId++)
            {
                float remaining = _store.PlayerShieldDuration[playerId];
                if (remaining <= 0f) continue;
                remaining -= deltaTime;
                if (remaining > 0f) { _store.PlayerShieldDuration[playerId] = remaining; continue; }
                var handle = _store.GetEntityHandle(playerId);
                lock (_eventCommitLock)
                {
                    float beforeShield = _store.PlayerShield[playerId];
                    float beforeDuration = _store.PlayerShieldDuration[playerId];
                    if (beforeShield > 0f && handle.IsValid && !Events.CanPublish(1, true))
                    {
                        RecordRejection(ResourceRejectionReason.RequestQueueOverflow);
                        continue;
                    }
                    _store.PlayerShieldDuration[playerId] = 0f;
                    if (beforeShield <= 0f) continue;
                    _store.PlayerShield[playerId] = 0f;
                    if (handle.IsValid && !PublishResourceFact(new GameplayEvent(GameplayEventType.ShieldChanged, handle, handle,
                        _store.AllocateGameplaySequence(playerId), ownerPlayerId: playerId)))
                    {
                        _store.PlayerShield[playerId] = beforeShield;
                        _store.PlayerShieldDuration[playerId] = beforeDuration;
                    }
                }
            }
        }
        /// <summary>提交独立的治疗请求；治疗不是通用资源写入。</summary>
        public ResourceApplyResult TryApply(HealRequest request)
        {
            if (request.RawAmount <= 0f || float.IsNaN(request.RawAmount) || float.IsInfinity(request.RawAmount))
            { RecordRejection(ResourceRejectionReason.InvalidValue); return new ResourceApplyResult(false, 0f, ResourceRejectionReason.InvalidValue); }
            return TryApply(request.AllowMissingSource
                ? ResourceRequest.ForPersistentEffect(request.Source, request.Target, new AttributeKey(3), request.RawAmount, request.Sequence, request.OwnerPlayerId)
                : new ResourceRequest(request.Source, request.Target, new AttributeKey(3), request.RawAmount, request.Sequence, request.OwnerPlayerId));
        }
        private void SetRejection(ResourceRejectionReason reason) { lock (_diagnosticsLock) _lastRejectionReason = reason; }
        private void RecordRejection(ResourceRejectionReason reason, int count = 1,
            HandleResolveFailure handleFailure = HandleResolveFailure.None)
        {
            if (count <= 0 || reason == ResourceRejectionReason.None) return;
            Interlocked.Add(ref _rejectedCount, count);
            Interlocked.Add(ref _rejectionsByReason[(int)reason], count);
            if (handleFailure == HandleResolveFailure.StaleGeneration)
                Interlocked.Add(ref _staleHandleRejectedCount, count);
            SetRejection(reason);
        }
        internal void BeginFrame() { SetRejection(ResourceRejectionReason.None); Volatile.Write(ref _eventPublicationFailed, 0); lock (_pendingLock) { if (_pending.Count != 0) { Interlocked.Add(ref _unconsumedRequestCount, _pending.Count); RecordRejection(ResourceRejectionReason.UnconsumedRequests, _pending.Count); _pending.Clear(); } } }
        internal void EnableDeferred(bool value) { _deferred = value; }
        internal void RejectPendingEnemyDamage()
        {
            lock (_pendingLock)
            {
                int write = 0;
                for (int i = 0; i < _pending.Count; i++)
                {
                    var request = _pending[i];
                    int target;
                    bool enemyDamage = request.Resource.Value == 3 && request.Delta < 0f && _store.TryResolve(request.Target, out target, out _) && _store.EnemyActive[target];
                    if (enemyDamage) { Interlocked.Increment(ref _unconsumedRequestCount); RecordRejection(ResourceRejectionReason.UnsupportedOperation); }
                    else _pending[write++] = request;
                }
                if (write < _pending.Count) _pending.RemoveRange(write, _pending.Count - write);
            }
        }
        internal void RejectAllPending()
        {
            lock (_pendingLock)
            {
                int count = _pending.Count;
                if (count == 0) return;
                Interlocked.Add(ref _unconsumedRequestCount, count);
                RecordRejection(ResourceRejectionReason.UnsupportedOperation, count);
                _pending.Clear();
            }
        }
        internal void CommitBoundary(DamageCommitBoundary boundary)
        {
            if (_isCommitting) return;
            lock (_pendingLock)
            {
                if (_pending.Count == 0) return;
                int write = 0;
                var batch = new List<ResourceRequest>();
                for (int i = 0; i < _pending.Count; i++)
                {
                    if (_pending[i].CommitBoundary == boundary) batch.Add(_pending[i]);
                    else _pending[write++] = _pending[i];
                }
                if (write < _pending.Count) _pending.RemoveRange(write, _pending.Count - write);
                batch.Sort((left, right) => { int c = left.Sequence.CompareTo(right.Sequence); return c != 0 ? c : left.Target.Index.CompareTo(right.Target.Index); });
                _isCommitting = true;
                try { for (int i = 0; i < batch.Count; i++) TryApply(batch[i], allowDeferred: false); }
                finally { _isCommitting = false; }
            }
        }
        public ResourceApplyResult TryApply(ResourceRequest request) => TryApply(request, allowDeferred: true);
        private ResourceApplyResult TryApply(ResourceRequest request, bool allowDeferred)
        {
            ResourceApplyResult Reject(ResourceApplyResult result,
                HandleResolveFailure handleFailure = HandleResolveFailure.None)
            {
                RecordRejection(result.Reason, 1, handleFailure);
                return result;
            }
            if (request.Operation != ResourceOperation.Add && request.Operation != ResourceOperation.Set)
                return Reject(new ResourceApplyResult(false, 0f, ResourceRejectionReason.InvalidOperation));
            ResourceKind kind;
            switch (request.Resource.Value)
            {
                case 2: kind = ResourceKind.MaxHealth; break;
                case 3: kind = ResourceKind.CurrentHealth; break;
                case 4: kind = ResourceKind.Gold; break;
                case 7: kind = ResourceKind.Mana; break;
                case 9: kind = ResourceKind.Shield; break;
                default: return Reject(new ResourceApplyResult(false, 0f, ResourceRejectionReason.UnknownResource));
            }
            if (float.IsNaN(request.Delta) || float.IsInfinity(request.Delta)) return Reject(new ResourceApplyResult(false, 0f, ResourceRejectionReason.InvalidValue));
            if (request.Operation == ResourceOperation.Set && kind == ResourceKind.CurrentHealth && request.Delta < 0f)
                return Reject(new ResourceApplyResult(false, 0f, ResourceRejectionReason.UnsupportedOperation));
            if (_deferred && allowDeferred)
            {
                lock (_eventCommitLock)
                {
                    ResourceApplyResult validation = ValidateLiveResourceRequest(request, kind, out _, out _, out _);
                    if (!validation.Accepted) return validation;
                }
                lock (_pendingLock)
                {
                    if (_pending.Count >= MaxPendingRequests) { Interlocked.Increment(ref _requestOverflowCount); return Reject(new ResourceApplyResult(false, 0f, ResourceRejectionReason.RequestQueueOverflow)); }
                    _pending.Add(request);
                    if (_pending.Count > _peakPendingRequestCount)
                        Volatile.Write(ref _peakPendingRequestCount, _pending.Count);
                }
                return new ResourceApplyResult(true, 0f, ResourceRejectionReason.None, deferred: true);
            }
            int targetId;
            bool isPlayer;
            bool isEnemy;
            int requiredEvents;
            lock (_eventCommitLock)
            {
            ResourceApplyResult validation = ValidateLiveResourceRequest(request, kind, out targetId, out isPlayer, out isEnemy);
            if (!validation.Accepted) return validation;
            requiredEvents = isEnemy && kind == ResourceKind.CurrentHealth && request.Delta < 0f ? 2 : 1;
            BeforeStateCommit?.Invoke(request.Sequence, Monitor.IsEntered(_eventCommitLock));
            if (!Events.CanPublish(requiredEvents, true))
                return Reject(new ResourceApplyResult(false, 0f, ResourceRejectionReason.RequestQueueOverflow));
            float oldGold = isPlayer ? _store.PlayerGold[targetId] : 0f;
            float oldMana = isPlayer ? _store.PlayerMana[targetId] : 0f;
            float oldShield = isPlayer ? _store.PlayerShield[targetId] : 0f;
            float oldPlayerHealth = isPlayer ? _store.PlayerCurrentHealth[targetId] : 0f;
            float oldEnemyHealth = isEnemy ? _store.EnemyHealth[targetId] : 0f;
            float oldPlayerMaxHealth = isPlayer ? _store.PlayerMaxHealth[targetId] : 0f;
            float oldPlayerMaxMana = isPlayer ? _store.PlayerMaxMana[targetId] : 0f;
            float oldEnemyShield = isEnemy ? _store.EnemyShield[targetId] : 0f;
            float oldEnemyMaxHealth = isEnemy ? _store.EnemyMaxHealth[targetId] : 0f;
            float oldEnemyMana = isEnemy ? _store.EnemyCurrentMana[targetId] : 0f;
            float applied;
            if (isEnemy)
            {
                applied = ApplyEnemyResource(targetId, kind, request.Delta, request.Operation);
            }
            else
            {
                switch (kind)
                {
                    case ResourceKind.CurrentHealth: applied = request.Operation == ResourceOperation.Set ? SetCurrentHealth(targetId, request.Delta) : ApplyCurrentHealthDelta(targetId, request.Delta); break;
                    case ResourceKind.MaxHealth: applied = request.Operation == ResourceOperation.Set ? SetMaxHealth(targetId, request.Delta) : SetMaxHealth(targetId, _store.PlayerMaxHealth[targetId] + request.Delta); break;
                    case ResourceKind.Gold: applied = request.Operation == ResourceOperation.Set ? SetGold(targetId, request.Delta) : ApplyGold(targetId, request.Delta); break;
                    case ResourceKind.Mana: applied = request.Operation == ResourceOperation.Set ? SetMana(targetId, request.Delta) : ApplyMana(targetId, request.Delta); break;
                    default: applied = request.Operation == ResourceOperation.Set ? SetShield(targetId, request.Delta) : ApplyShield(targetId, request.Delta); break;
                }
            }
            var type = kind == ResourceKind.CurrentHealth && request.Delta < 0f ? GameplayEventType.DamageApplied : kind == ResourceKind.CurrentHealth ? GameplayEventType.HealApplied : kind == ResourceKind.Shield ? GameplayEventType.ShieldChanged : GameplayEventType.ResourceChanged;
            var resourceFact = new GameplayEvent(type, request.Source, request.Target, default(EffectHandle), default(EffectId), request.Sequence, provenanceId: request.ProvenanceId, ownerPlayerId: request.OwnerPlayerId);
            bool lethal = isEnemy && kind == ResourceKind.CurrentHealth && _store.EnemyHealth[targetId] <= 0f;
            bool published = lethal
                ? Events.TryPublishBatch(resourceFact, new GameplayEvent(GameplayEventType.DeathQueued,
                    request.Source, request.Target, request.Sequence, ownerPlayerId: request.OwnerPlayerId), true)
                : Events.TryPublishBatch(resourceFact, true);
            if (!published)
            {
                if (isPlayer) { _store.PlayerGold[targetId] = oldGold; _store.PlayerMana[targetId] = oldMana; _store.PlayerShield[targetId] = oldShield; _store.PlayerCurrentHealth[targetId] = oldPlayerHealth; _store.PlayerMaxHealth[targetId] = oldPlayerMaxHealth; _store.PlayerMaxMana[targetId] = oldPlayerMaxMana; }
                if (isEnemy) { _store.EnemyHealth[targetId] = oldEnemyHealth; _store.EnemyShield[targetId] = oldEnemyShield; _store.EnemyMaxHealth[targetId] = oldEnemyMaxHealth; _store.EnemyCurrentMana[targetId] = oldEnemyMana; }
                return Reject(new ResourceApplyResult(false, 0f, ResourceRejectionReason.RequestQueueOverflow));
            }
            if (lethal)
            {
                _store.QueueEnemyDeath(targetId, request.OwnerPlayerId, request.Sequence, request.Source);
            }
            return new ResourceApplyResult(true, applied, ResourceRejectionReason.None);
            }
        }

        private ResourceApplyResult ValidateLiveResourceRequest(ResourceRequest request, ResourceKind kind,
            out int targetId, out bool isPlayer, out bool isEnemy)
        {
            targetId = -1;
            isPlayer = false;
            isEnemy = false;
            if (!_store.TryResolve(request.Target, out targetId, out HandleResolveFailure failure))
            {
                if (ComponentStore.IsValidEntity(request.Target.Index) &&
                    _store.GetEntityHandle(request.Target.Index).Equals(request.Target) &&
                    _store.EnemyActive[request.Target.Index] &&
                    (_store.EnemyHealth[request.Target.Index] <= 0f || _store.IsEnemyPendingDeath(request.Target.Index)))
                    return RejectLive(ResourceRejectionReason.TargetAlreadyDead);
                return RejectLive(ResourceRejectionReason.InvalidTarget, failure);
            }
            if (!_store.TryResolve(request.Source, out _, out failure) && !request.AllowMissingSource)
                return RejectLive(ResourceRejectionReason.InvalidSource, failure);
            if (request.OwnerPlayerId < 0 || request.OwnerPlayerId >= ComponentStore.MAX_PLAYERS)
                return RejectLive(ResourceRejectionReason.InvalidOwner);
            isEnemy = ComponentStore.IsValidEntity(targetId) && _store.EnemyActive[targetId];
            isPlayer = !isEnemy && (uint)targetId < ComponentStore.MAX_PLAYERS && _store.PositionActive[targetId];
            if (!isPlayer && !isEnemy) return RejectLive(ResourceRejectionReason.InvalidTarget);
            if (isEnemy && (_store.EnemyHealth[targetId] <= 0f || _store.IsEnemyPendingDeath(targetId)))
                return RejectLive(ResourceRejectionReason.TargetAlreadyDead);
            if (isEnemy && kind == ResourceKind.Gold) return RejectLive(ResourceRejectionReason.UnsupportedOperation);
            return new ResourceApplyResult(true, 0f, ResourceRejectionReason.None);
        }

        private ResourceApplyResult RejectLive(ResourceRejectionReason reason,
            HandleResolveFailure failure = HandleResolveFailure.None)
        {
            RecordRejection(reason, 1, failure);
            return new ResourceApplyResult(false, 0f, reason);
        }
        public float Apply(ResourceRequest request)
        {
            var result = TryApply(request); if (!result.Accepted) return 0f; return result.Applied;
        }
        // DeathResolve 奖励写入是串行生命周期效果，统一使用 ResourceResolver 写入，
        // 并在 KillConfirmed 之前发布 ResourceChanged。
        internal float ApplyLifecycleGold(int playerId, float delta, EntityHandle source, long sequence, int ownerPlayerId)
        {
            if (!Valid(playerId) || ownerPlayerId < 0 || ownerPlayerId >= ComponentStore.MAX_PLAYERS || !Finite(delta)) return 0f;
            lock (_eventCommitLock)
            {
                if (!Events.CanPublish(1, true)) { RecordRejection(ResourceRejectionReason.RequestQueueOverflow); return 0f; }
                float before = _store.PlayerGold[playerId];
                float applied = ApplyGold(playerId, delta);
                if (!PublishResourceFact(new GameplayEvent(GameplayEventType.ResourceChanged, source, _store.GetEntityHandle(playerId), sequence, ownerPlayerId: ownerPlayerId)))
                { _store.PlayerGold[playerId] = before; return 0f; }
                return applied;
            }
        }
        internal float StageLifecycleGold(int playerId, float delta, EntityHandle source, long sequence, int ownerPlayerId,
            GameplayEventQueue.GameplayEventReservation reservation)
        {
            lock (_eventCommitLock)
            {
                if (!Valid(playerId) || ownerPlayerId < 0 || ownerPlayerId >= ComponentStore.MAX_PLAYERS || !Finite(delta)) return 0f;
                float applied = ApplyGold(playerId, delta);
                reservation.StageSecond(new GameplayEvent(GameplayEventType.ResourceChanged, source,
                    _store.GetEntityHandle(playerId), sequence, ownerPlayerId: ownerPlayerId));
                return applied;
            }
        }
        internal float ApplyLifecycleMana(int playerId, float delta, EntityHandle source, long sequence, int ownerPlayerId)
        {
            if (!Valid(playerId) || ownerPlayerId < 0 || ownerPlayerId >= ComponentStore.MAX_PLAYERS || !Finite(delta)) return 0f;
            lock (_eventCommitLock)
            {
                if (!Events.CanPublish(1, true)) { RecordRejection(ResourceRejectionReason.RequestQueueOverflow); return 0f; }
                float before = _store.PlayerMana[playerId];
                float applied = ApplyMana(playerId, delta);
                if (!PublishResourceFact(new GameplayEvent(GameplayEventType.ResourceChanged, source, _store.GetEntityHandle(playerId), sequence, ownerPlayerId: ownerPlayerId)))
                { _store.PlayerMana[playerId] = before; return 0f; }
                return applied;
            }
        }
        internal float StageLifecycleMana(int playerId, float delta, EntityHandle source, long sequence, int ownerPlayerId,
            GameplayEventQueue.GameplayEventReservation reservation)
        {
            lock (_eventCommitLock)
            {
                if (!Valid(playerId) || ownerPlayerId < 0 || ownerPlayerId >= ComponentStore.MAX_PLAYERS || !Finite(delta)) return 0f;
                float applied = ApplyMana(playerId, delta);
                reservation.StageSecond(new GameplayEvent(GameplayEventType.ResourceChanged, source,
                    _store.GetEntityHandle(playerId), sequence, ownerPlayerId: ownerPlayerId));
                return applied;
            }
        }
        private bool PublishResourceFact(GameplayEvent fact)
        {
            if (Events.TryPublish(fact, true)) return true;
            Interlocked.Increment(ref _eventPublicationFailureCount);
            Volatile.Write(ref _eventPublicationFailed, 1);
            return false;
        }
        // 仅保留给旧存档/初始化适配；运行时资源变化必须提交 ResourceRequest。
        public float Heal(int playerId, float amount) { if (!Valid(playerId) || amount <= 0f || !Finite(amount)) return 0f; var old = _store.PlayerCurrentHealth[playerId]; var next = Clamp(old + amount, _store.PlayerMaxHealth[playerId]); _store.PlayerCurrentHealth[playerId] = next; return next - old; }
        public float ApplyShield(int playerId, float delta) { if (!Valid(playerId) || !Finite(delta)) return 0f; var old = _store.PlayerShield[playerId]; var next = Math.Max(0f, old + delta); _store.PlayerShield[playerId] = next; return next - old; }
        public float ApplyMana(int playerId, float delta) { if (!Valid(playerId) || !Finite(delta)) return 0f; var old = _store.PlayerMana[playerId]; var next = Clamp(old + delta, _store.PlayerMaxMana[playerId]); _store.PlayerMana[playerId] = next; return next - old; }
        public float ApplyGold(int playerId, float delta) { if (!Valid(playerId) || !Finite(delta)) return 0f; var old = _store.PlayerGold[playerId]; var next = Math.Max(0f, old + delta); _store.PlayerGold[playerId] = next; return next - old; }
        public float SetMaxHealth(int playerId, float value) { if (!Valid(playerId) || !Finite(value)) return 0f; var old = _store.PlayerMaxHealth[playerId]; var next = Math.Max(0f, value); _store.PlayerMaxHealth[playerId] = next; if (_store.PlayerCurrentHealth[playerId] > next) _store.PlayerCurrentHealth[playerId] = next; return next - old; }
        private float SetCurrentHealth(int id, float value) { if (!Valid(id) || !Finite(value)) return 0f; var old = _store.PlayerCurrentHealth[id]; var next = Clamp(value, _store.PlayerMaxHealth[id]); _store.PlayerCurrentHealth[id] = next; return next - old; }
        private float ApplyCurrentHealthDelta(int id, float delta) { if (!Valid(id) || !Finite(delta)) return 0f; var old = _store.PlayerCurrentHealth[id]; var next = Clamp(old + delta, _store.PlayerMaxHealth[id]); _store.PlayerCurrentHealth[id] = next; return next - old; }
        private float SetMana(int id, float value) { if (!Valid(id) || !Finite(value)) return 0f; var old = _store.PlayerMana[id]; var next = Clamp(value, _store.PlayerMaxMana[id]); _store.PlayerMana[id] = next; return next - old; }
        private float SetGold(int id, float value) { if (!Valid(id) || !Finite(value)) return 0f; var old = _store.PlayerGold[id]; var next = Math.Max(0f, value); _store.PlayerGold[id] = next; return next - old; }
        private float SetShield(int id, float value) { if (!Valid(id) || !Finite(value)) return 0f; var old = _store.PlayerShield[id]; var next = Math.Max(0f, value); _store.PlayerShield[id] = next; return next - old; }
        // 兼容旧战斗数值：死亡队列在帧末解析，允许 HP 暂时低于 0；ResourceRequest 路径仍执行资源夹紧。
        internal void ApplyEnemyHealthDamage(int id, float amount) { if (ComponentStore.IsValidEntity(id) && _store.EnemyActive[id] && Finite(amount)) _store.EnemyHealth[id] -= Math.Max(0f, amount); }
        internal void ClampEnemyHealthAtZero(int id) { if (ComponentStore.IsValidEntity(id) && _store.EnemyActive[id] && _store.EnemyHealth[id] < 0f) _store.EnemyHealth[id] = 0f; }
        internal void SetEnemyShield(int id, float value) { if (ComponentStore.IsValidEntity(id) && _store.EnemyActive[id] && Finite(value)) _store.EnemyShield[id] = Math.Max(0f, value); }
        internal void ApplyEnemyDamageResources(int id, float damage, ElementType element, bool ignoreShield, bool execute = false)
        {
            // 兼容入口也必须与 DamageResolver 的快照/回滚串行；Monitor 允许事务内重入。
            lock (_eventCommitLock)
                ApplyEnemyDamageResourcesLocked(id, damage, element, ignoreShield, execute);
        }
        private void ApplyEnemyDamageResourcesLocked(int id, float damage, ElementType element, bool ignoreShield, bool execute)
        {
            if (!ComponentStore.IsValidEntity(id) || !_store.EnemyActive[id] || !Finite(damage) || damage <= 0f) return;
            float floor = _store.EnemyMinHealthFloor[id];
            if (!execute && floor > 0f && _store.EnemyMaxHealth[id] > 0f)
                damage = Math.Min(damage, Math.Max(0f, _store.EnemyHealth[id] - _store.EnemyMaxHealth[id] * floor));
            if (damage <= 0f) return;
            float shield = ignoreShield ? 0f : _store.EnemyShield[id];
            if (shield > 0f && element != ElementType.None && _store.EnemyShieldType[id] != ElementType.None)
                damage *= element == _store.EnemyShieldType[id] ? (_store.EnemyShieldWeakMult[id] > 0f ? _store.EnemyShieldWeakMult[id] : 2f) : (_store.EnemyShieldResistMult[id] > 0f ? _store.EnemyShieldResistMult[id] : 0.5f);
            if (ignoreShield) { ApplyEnemyHealthDamage(id, damage); return; }
            if (shield >= damage) { SetEnemyShield(id, shield - damage); return; }
            SetEnemyShield(id, 0f);
            ApplyEnemyHealthDamage(id, damage - shield);
            if (_store.EnemyShieldType[id] != ElementType.None)
            {
                var breakElement = _store.EnemyShieldBreakReaction[id];
                if (breakElement != ElementType.None)
                {
                    _store.EnemyElementStatus[id] |= breakElement;
                    int ordinal = breakElement == ElementType.Fire ? 0 : breakElement == ElementType.Ice ? 1 : breakElement == ElementType.Lightning ? 2 : breakElement == ElementType.Poison ? 3 : -1;
                    if (ordinal >= 0)
                    {
                        int offset = id * 4 + ordinal;
                        float duration = _store.EnemyShieldBreakElementDuration[id] > 0f ? _store.EnemyShieldBreakElementDuration[id] : 2f;
                        if (_store.EnemyElementTimer[offset] < duration) _store.EnemyElementTimer[offset] = duration;
                    }
                    _store.PendingShieldBreaks.Add(id);
                }
            }
        }
        private float ApplyEnemyResource(int id, ResourceKind kind, float value, ResourceOperation operation)
        {
            if (!Finite(value)) return 0f;
            switch (kind)
            {
                case ResourceKind.CurrentHealth:
                    float oldHp = _store.EnemyHealth[id];
                    float nextHp = operation == ResourceOperation.Set ? Math.Min(Math.Max(0f, value), Math.Max(0f, _store.EnemyMaxHealth[id])) : Math.Min(Math.Max(0f, oldHp + value), Math.Max(0f, _store.EnemyMaxHealth[id]));
                    _store.EnemyHealth[id] = nextHp; return nextHp - oldHp;
                case ResourceKind.MaxHealth:
                    float oldMax = _store.EnemyMaxHealth[id];
                    float nextMax = operation == ResourceOperation.Set ? Math.Max(0f, value) : Math.Max(0f, oldMax + value);
                    _store.EnemyMaxHealth[id] = nextMax;
                    if (_store.EnemyHealth[id] > nextMax) _store.EnemyHealth[id] = nextMax;
                    return nextMax - oldMax;
                case ResourceKind.Mana:
                    float oldMana = _store.EnemyCurrentMana[id];
                    float nextMana = operation == ResourceOperation.Set ? Math.Max(0f, value) : Math.Max(0f, oldMana + value);
                    _store.EnemyCurrentMana[id] = nextMana; return nextMana - oldMana;
                case ResourceKind.Shield:
                    float oldShield = _store.EnemyShield[id];
                    float nextShield = operation == ResourceOperation.Set ? Math.Max(0f, value) : Math.Max(0f, oldShield + value);
                    _store.EnemyShield[id] = nextShield; return nextShield - oldShield;
                default: return 0f;
            }
        }
        private static float Clamp(float value, float max) => Math.Min(Math.Max(0f, value), Math.Max(0f, max));
        private static bool Valid(int playerId) => (uint)playerId < ComponentStore.MAX_PLAYERS;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static ResourcePolicy Policy(ResourceKind kind) => kind == ResourceKind.CurrentHealth
            ? new ResourcePolicy(kind, allowsNegative: false, clampToMaximum: true)
            : new ResourcePolicy(kind, allowsNegative: true, clampToMaximum: kind == ResourceKind.Mana);
    }
}
