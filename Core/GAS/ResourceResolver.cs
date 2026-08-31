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
        private int _rejectedCount, _requestOverflowCount, _unconsumedRequestCount, _eventPublicationFailed;
        private readonly object _diagnosticsLock = new object();
        public int RejectedCount => Volatile.Read(ref _rejectedCount);
        public GameplayEventQueue Events { get; } = new GameplayEventQueue(8192, 64);
        public int EventOverflowCount => Events.OverflowCount;
        public CommandRejection LastEventRejection => Events.LastRejection;
        public bool LastEventPublicationFailed => Volatile.Read(ref _eventPublicationFailed) != 0;
        private bool _deferred;
        private bool _isCommitting;
        private readonly object _pendingLock = new object();
        private readonly List<ResourceRequest> _pending = new List<ResourceRequest>(256);
        public int PendingRequestCount { get { lock (_pendingLock) return _pending.Count; } }
        public int RequestOverflowCount => Volatile.Read(ref _requestOverflowCount);
        public int UnconsumedRequestCount => Volatile.Read(ref _unconsumedRequestCount);
        public ResourceResolver(ComponentStore store) { _store = store ?? throw new ArgumentNullException(nameof(store)); }
        /// <summary>提交独立的治疗请求；治疗不是通用资源写入。</summary>
        public ResourceApplyResult TryApply(HealRequest request)
        {
            if (request.RawAmount <= 0f || float.IsNaN(request.RawAmount) || float.IsInfinity(request.RawAmount))
            { SetRejection(ResourceRejectionReason.InvalidValue); Interlocked.Increment(ref _rejectedCount); return new ResourceApplyResult(false, 0f, ResourceRejectionReason.InvalidValue); }
            return TryApply(request.AllowMissingSource
                ? ResourceRequest.ForPersistentEffect(request.Source, request.Target, new AttributeKey(3), request.RawAmount, request.Sequence, request.OwnerPlayerId)
                : new ResourceRequest(request.Source, request.Target, new AttributeKey(3), request.RawAmount, request.Sequence, request.OwnerPlayerId));
        }
        private void SetRejection(ResourceRejectionReason reason) { lock (_diagnosticsLock) _lastRejectionReason = reason; }
        internal void BeginFrame() { SetRejection(ResourceRejectionReason.None); Volatile.Write(ref _eventPublicationFailed, 0); lock (_pendingLock) { if (_pending.Count != 0) { Interlocked.Add(ref _unconsumedRequestCount, _pending.Count); Interlocked.Add(ref _rejectedCount, _pending.Count); SetRejection(ResourceRejectionReason.UnconsumedRequests); _pending.Clear(); } } }
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
                    if (enemyDamage) { Interlocked.Increment(ref _unconsumedRequestCount); Interlocked.Increment(ref _rejectedCount); SetRejection(ResourceRejectionReason.UnsupportedOperation); }
                    else _pending[write++] = request;
                }
                if (write < _pending.Count) _pending.RemoveRange(write, _pending.Count - write);
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
            ResourceApplyResult Reject(ResourceApplyResult result) { SetRejection(result.Reason); Interlocked.Increment(ref _rejectedCount); return result; }
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
            int targetId;
            HandleResolveFailure failure;
            if (!_store.TryResolve(request.Target, out targetId, out failure))
                return Reject(new ResourceApplyResult(false, 0f, ResourceRejectionReason.InvalidTarget));
            if (!_store.TryResolve(request.Source, out _, out failure) && !request.AllowMissingSource)
                return Reject(new ResourceApplyResult(false, 0f, ResourceRejectionReason.InvalidSource));
            if (request.OwnerPlayerId < 0 || request.OwnerPlayerId >= ComponentStore.MAX_PLAYERS)
                return Reject(new ResourceApplyResult(false, 0f, ResourceRejectionReason.InvalidOwner));
            bool isPlayer = (uint)targetId < ComponentStore.MAX_PLAYERS && _store.PositionActive[targetId];
            bool isEnemy = ComponentStore.IsValidEntity(targetId) && _store.EnemyActive[targetId];
            if (!isPlayer && !isEnemy)
                return Reject(new ResourceApplyResult(false, 0f, ResourceRejectionReason.InvalidTarget));
            if (isEnemy && (_store.EnemyHealth[targetId] <= 0f || _store.IsEnemyPendingDeath(targetId)))
                return Reject(new ResourceApplyResult(false, 0f, ResourceRejectionReason.TargetAlreadyDead));
            if (float.IsNaN(request.Delta) || float.IsInfinity(request.Delta)) return Reject(new ResourceApplyResult(false, 0f, ResourceRejectionReason.InvalidValue));
            ResourcePolicy policy = Policy(kind);
            if (request.Operation == ResourceOperation.Set && kind == ResourceKind.CurrentHealth && request.Delta < 0f)
                return Reject(new ResourceApplyResult(false, 0f, ResourceRejectionReason.UnsupportedOperation));
            if (isEnemy && kind == ResourceKind.Gold)
                return Reject(new ResourceApplyResult(false, 0f, ResourceRejectionReason.UnsupportedOperation));
            if (_deferred && allowDeferred)
            {
                lock (_pendingLock)
                {
                    if (_pending.Count >= MaxPendingRequests) { Interlocked.Increment(ref _requestOverflowCount); return Reject(new ResourceApplyResult(false, 0f, ResourceRejectionReason.RequestQueueOverflow)); }
                    _pending.Add(request);
                }
                return new ResourceApplyResult(true, 0f, ResourceRejectionReason.None, deferred: true);
            }
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
            if (!Events.TryPublish(new GameplayEvent(type, request.Source, request.Target, default(EffectHandle), default(EffectId), request.Sequence, provenanceId: request.ProvenanceId, ownerPlayerId: request.OwnerPlayerId), true)) Volatile.Write(ref _eventPublicationFailed, 1);
            if (isEnemy && kind == ResourceKind.CurrentHealth && _store.EnemyHealth[targetId] <= 0f)
            {
                _store.QueueEnemyDeath(targetId, request.OwnerPlayerId, request.Sequence, request.Source);
                if (!Events.TryPublish(new GameplayEvent(GameplayEventType.DeathQueued, request.Source, request.Target, request.Sequence, ownerPlayerId: request.OwnerPlayerId), true)) Volatile.Write(ref _eventPublicationFailed, 1);
            }
            return new ResourceApplyResult(true, applied, ResourceRejectionReason.None);
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
            float applied = ApplyGold(playerId, delta);
            PublishResourceFact(new GameplayEvent(GameplayEventType.ResourceChanged, source, _store.GetEntityHandle(playerId), sequence, ownerPlayerId: ownerPlayerId));
            return applied;
        }
        internal float ApplyLifecycleMana(int playerId, float delta, EntityHandle source, long sequence, int ownerPlayerId)
        {
            if (!Valid(playerId) || ownerPlayerId < 0 || ownerPlayerId >= ComponentStore.MAX_PLAYERS || !Finite(delta)) return 0f;
            float applied = ApplyMana(playerId, delta);
            PublishResourceFact(new GameplayEvent(GameplayEventType.ResourceChanged, source, _store.GetEntityHandle(playerId), sequence, ownerPlayerId: ownerPlayerId));
            return applied;
        }
        private void PublishResourceFact(GameplayEvent fact)
        {
            if (!Events.TryPublish(fact, true)) Volatile.Write(ref _eventPublicationFailed, 1);
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
