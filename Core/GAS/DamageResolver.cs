using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using BattleSystemECS.Components;

namespace BattleSystemECS.Core.GAS
{
    public enum DamageRejectionReason { None, InvalidSource, InvalidTarget, NonFiniteAmount, NonPositiveAmount, TargetAlreadyDead, Invulnerable, InvalidOwner, UnsupportedDamageType, UnsupportedAmountStage, UnsupportedFlags, UnsupportedCommitBoundary, RequestQueueOverflow, UnconsumedRequests }
    public readonly struct DamageApplyResult
    {
        public readonly bool Accepted; public readonly float Applied; public readonly float ShieldAbsorbed; public readonly bool DeathQueued; public readonly bool Deferred; public readonly DamageRejectionReason Reason;
        public DamageApplyResult(bool accepted, float applied, float shieldAbsorbed, bool deathQueued, DamageRejectionReason reason, bool deferred = false) { Accepted = accepted; Applied = applied; ShieldAbsorbed = shieldAbsorbed; DeathQueued = deathQueued; Deferred = deferred; Reason = reason; }
    }

    /// <summary>统一伤害规则入口；旧的 int 入口只应通过此 adapter 到达这里。</summary>
    public sealed class DamageResolver
    {
        public const int MaxProvenanceDepth = 4;
        public const int MaxPendingRequests = 4096;
        private readonly ComponentStore _store;
        public GameplayEventQueue Events { get; } = new GameplayEventQueue(8192, 64);
        public int EventOverflowCount => Events.OverflowCount;
        public CommandRejection LastEventRejection => Events.LastRejection;
        private int _eventPublicationFailed;
        public bool LastEventPublicationFailed { get { return Volatile.Read(ref _eventPublicationFailed) != 0; } }
        private int _lastCommittedBoundary = (int)DamageCommitBoundary.GameplayResolve;
        private int _lastLegacyRejection;
        private long _requestsValidated, _requestsFastPath, _factsPublished, _acceptedCount;
        public DamageCommitBoundary LastCommittedBoundary => (DamageCommitBoundary)Volatile.Read(ref _lastCommittedBoundary);
        public DamageRejectionReason LastLegacyRejection => (DamageRejectionReason)Volatile.Read(ref _lastLegacyRejection);
        public long RequestsValidated => Interlocked.Read(ref _requestsValidated);
        public long RequestsFastPath => Interlocked.Read(ref _requestsFastPath);
        public long FactsPublished => Interlocked.Read(ref _factsPublished);
        public long AcceptedCount => Interlocked.Read(ref _acceptedCount);
        private long _rejectedCount, _unconsumedRequestCount;
        private int _requestOverflowCount;
        private readonly object _diagnosticsLock = new object();
        public long RejectedCount => Interlocked.Read(ref _rejectedCount);
        private DamageRejectionReason _lastRejection;
        public DamageRejectionReason LastRejection { get { lock (_diagnosticsLock) return _lastRejection; } }
        public bool DiagnosticsEnabled { get; set; }
        // 测试/诊断观察点：事实进入队列后立即调用。
        public Action<GameplayEvent> EventObserver { get; set; }
        public int PendingRequestCount { get { lock (_pendingLock) return _pending.Count; } }
        public int RequestOverflowCount => Volatile.Read(ref _requestOverflowCount);
        public int UnconsumedRequestCount => (int)Interlocked.Read(ref _unconsumedRequestCount);
        private int _earlyBoundaryClosed;
        private bool _deferred;
        private bool _isCommitting;
        private readonly object _pendingLock = new object();
        private readonly List<DamageRequest> _pending = new List<DamageRequest>(256);
        internal void ResetDiagnostics() { Interlocked.Exchange(ref _requestsValidated, 0); Interlocked.Exchange(ref _requestsFastPath, 0); Interlocked.Exchange(ref _factsPublished, 0); }
        internal void MarkEventPublicationFailure(bool failed) { if (failed) Volatile.Write(ref _eventPublicationFailed, 1); }
        public DamageResolver(ComponentStore store) { _store = store ?? throw new ArgumentNullException(nameof(store)); }
        internal void BeginFrame() { Volatile.Write(ref _eventPublicationFailed, 0); Volatile.Write(ref _lastCommittedBoundary, (int)DamageCommitBoundary.EarlyResolve); Volatile.Write(ref _earlyBoundaryClosed, 0); lock (_pendingLock) { if (_pending.Count != 0) { Interlocked.Add(ref _unconsumedRequestCount, _pending.Count); Interlocked.Add(ref _rejectedCount, _pending.Count); SetRejection(DamageRejectionReason.UnconsumedRequests); _pending.Clear(); } } }
        private void SetRejection(DamageRejectionReason reason) { lock (_diagnosticsLock) _lastRejection = reason; }
        internal void EnableDeferred(bool value) { _deferred = value; }
        internal void MarkBoundary(DamageCommitBoundary boundary) { Volatile.Write(ref _lastCommittedBoundary, (int)boundary); }
        internal void RejectPending(DamageCommitBoundary boundary)
        {
            lock (_pendingLock)
            {
                int write = 0;
                for (int i = 0; i < _pending.Count; i++)
                {
                    if (_pending[i].CommitBoundary == boundary) { Interlocked.Increment(ref _unconsumedRequestCount); Interlocked.Increment(ref _rejectedCount); SetRejection(DamageRejectionReason.UnsupportedCommitBoundary); }
                    else _pending[write++] = _pending[i];
                }
                if (write < _pending.Count) _pending.RemoveRange(write, _pending.Count - write);
            }
        }
        // 帧调度器在阶段末显式提交边界，供诊断与后续排队消费者观察。
        internal void CommitBoundary(DamageCommitBoundary boundary)
        {
            if (_isCommitting) return;
            Volatile.Write(ref _lastCommittedBoundary, (int)boundary);
            lock (_pendingLock)
            {
                if (_pending.Count == 0) { if (boundary == DamageCommitBoundary.EarlyResolve) Volatile.Write(ref _earlyBoundaryClosed, 1); return; }
            // 先摘出当前批次再提交；提交期间产生的新请求继续延迟到下一边界，避免递归执行。
            var batch = new List<DamageRequest>();
            int write = 0;
            for (int i = 0; i < _pending.Count; i++)
            {
                if (_pending[i].CommitBoundary == boundary) batch.Add(_pending[i]);
                else _pending[write++] = _pending[i];
            }
            if (write < _pending.Count) _pending.RemoveRange(write, _pending.Count - write);
            batch.Sort((left, right) =>
            {
                int c = left.Sequence.CompareTo(right.Sequence);
                if (c != 0) return c;
                c = left.Target.Index.CompareTo(right.Target.Index);
                if (c != 0) return c;
                return left.Source.Index.CompareTo(right.Source.Index);
            });
            _isCommitting = true;
            try { for (int i = 0; i < batch.Count; i++) TryApplyInternal(batch[i], false, bypassDeferred: true); }
            finally { _isCommitting = false; }
            if (boundary == DamageCommitBoundary.EarlyResolve) Volatile.Write(ref _earlyBoundaryClosed, 1);
            }
        }
        public DamageApplyResult TryApply(DamageRequest request) => TryApplyInternal(request, false, bypassDeferred: false);
        internal DamageApplyResult TryApplyValidated(DamageRequest request) => TryApplyInternal(request, true, bypassDeferred: false);
        private DamageApplyResult TryApplyInternal(DamageRequest request, bool validated, bool bypassDeferred)
        {
            if (DiagnosticsEnabled) { if (validated) Interlocked.Increment(ref _requestsFastPath); else Interlocked.Increment(ref _requestsValidated); }
            int target;
            if (!validated && !_store.TryResolve(request.Source, out _, out _)) { Interlocked.Increment(ref _rejectedCount); SetRejection(DamageRejectionReason.InvalidSource); return new DamageApplyResult(false, 0f, 0f, false, DamageRejectionReason.InvalidSource); }
            int source;
            // validated 适配器同样解析完整句柄；调用方不能只提供索引绕过代数和 active 校验。
            if (!_store.TryResolve(request.Source, out source, out _)) { Interlocked.Increment(ref _rejectedCount); SetRejection(DamageRejectionReason.InvalidSource); return new DamageApplyResult(false, 0f, 0f, false, DamageRejectionReason.InvalidSource); }
            if (!_store.TryResolve(request.Target, out target, out _)) { Interlocked.Increment(ref _rejectedCount); SetRejection(DamageRejectionReason.InvalidTarget); return new DamageApplyResult(false, 0f, 0f, false, DamageRejectionReason.InvalidTarget); }
            if (float.IsNaN(request.RawAmount) || float.IsInfinity(request.RawAmount)) return new DamageApplyResult(false, 0f, 0f, false, DamageRejectionReason.NonFiniteAmount);
            if (request.RawAmount <= 0f) return new DamageApplyResult(false, 0f, 0f, false, DamageRejectionReason.NonPositiveAmount);
            if (!IsSupportedDamageType(request.DamageType)) return new DamageApplyResult(false, 0f, 0f, false, DamageRejectionReason.UnsupportedDamageType);
            if (request.AmountStage != DamageAmountStage.Raw && request.AmountStage != DamageAmountStage.PostCrit && request.AmountStage != DamageAmountStage.PostMitigation) return new DamageApplyResult(false, 0f, 0f, false, DamageRejectionReason.UnsupportedAmountStage);
            if ((request.Flags & ~(DamageFlags.IgnoreInvulnerability | DamageFlags.IgnoreShield | DamageFlags.IgnoreArmor | DamageFlags.IgnoreResistance | DamageFlags.Execute | DamageFlags.Reflect | DamageFlags.Transfer)) != DamageFlags.None) return new DamageApplyResult(false, 0f, 0f, false, DamageRejectionReason.UnsupportedFlags);
            if ((request.Flags & (DamageFlags.Reflect | DamageFlags.Transfer)) != DamageFlags.None && request.ParentSequence == request.Sequence)
                return new DamageApplyResult(false, 0f, 0f, false, DamageRejectionReason.UnsupportedFlags);
            if ((request.Flags & (DamageFlags.Reflect | DamageFlags.Transfer)) != DamageFlags.None && request.ProvenanceDepth > MaxProvenanceDepth)
                return new DamageApplyResult(false, 0f, 0f, false, DamageRejectionReason.UnsupportedFlags);
            if (request.CommitBoundary != DamageCommitBoundary.GameplayResolve && request.CommitBoundary != DamageCommitBoundary.EarlyResolve) return new DamageApplyResult(false, 0f, 0f, false, DamageRejectionReason.UnsupportedCommitBoundary);
            if (request.CommitBoundary == DamageCommitBoundary.EarlyResolve && Volatile.Read(ref _earlyBoundaryClosed) != 0) return new DamageApplyResult(false, 0f, 0f, false, DamageRejectionReason.UnsupportedCommitBoundary);
            Volatile.Write(ref _lastCommittedBoundary, (int)request.CommitBoundary);
            if (request.OwnerPlayerId < 0 || request.OwnerPlayerId >= ComponentStore.MAX_PLAYERS) return new DamageApplyResult(false, 0f, 0f, false, DamageRejectionReason.InvalidOwner);
            if (!_store.EnemyActive[target] || _store.EnemyHealth[target] <= 0f || _store.IsEnemyPendingDeath(target)) return new DamageApplyResult(false, 0f, 0f, false, DamageRejectionReason.TargetAlreadyDead);
            if (_store.EnemyIsInvulnerable[target] && (request.Flags & DamageFlags.IgnoreInvulnerability) == 0) return new DamageApplyResult(false, 0f, 0f, false, DamageRejectionReason.Invulnerable);
            if (request.DamageType != DamageType.True && (_store.EnemyDamageImmunityMask[target] & (int)request.DamageType) != 0)
                return new DamageApplyResult(false, 0f, 0f, false, DamageRejectionReason.Invulnerable);
            if (_deferred && !bypassDeferred)
            {
                lock (_pendingLock)
                {
                    if (_pending.Count >= MaxPendingRequests) { Interlocked.Increment(ref _requestOverflowCount); Interlocked.Increment(ref _rejectedCount); SetRejection(DamageRejectionReason.RequestQueueOverflow); return new DamageApplyResult(false, 0f, 0f, false, DamageRejectionReason.RequestQueueOverflow); }
                    _pending.Add(request);
                }
                return new DamageApplyResult(true, 0f, 0f, false, DamageRejectionReason.None, deferred: true);
            }
            var hitFact = new GameplayEvent(GameplayEventType.HitConfirmed, request.Source, request.Target, default(EffectHandle), request.Effect, request.Flags, request.Sequence, request.ParentSequence, provenanceId: request.ProvenanceId, provenanceDepth: request.ProvenanceDepth);
            bool hitPublished = Events.TryPublish(hitFact, true);
            EventObserver?.Invoke(hitFact);
            if (!hitPublished) Volatile.Write(ref _eventPublicationFailed, 1);
            if (DiagnosticsEnabled && hitPublished) Interlocked.Increment(ref _factsPublished);

            float beforeHealth = _store.EnemyHealth[target];
            float beforeShield = _store.EnemyShield[target];
            float damage = request.RawAmount;
            if (request.AmountStage != DamageAmountStage.PostMitigation && request.DamageType != DamageType.True)
            {
                if (request.DamageType == DamageType.Physical && (request.Flags & DamageFlags.IgnoreArmor) == 0 && _store.EnemyArmor[target] > 0f)
                    damage *= 100f / (100f + _store.EnemyArmor[target]);
                if ((request.Flags & DamageFlags.IgnoreResistance) == 0)
                {
                    if (request.DamageType == DamageType.Magic) damage *= 1f - Clamp01(_store.EnemyMagicResist[target]);
                    else if (request.DamageType == DamageType.Fire) damage *= 1f - Clamp01(_store.EnemyFireResist[target]);
                    else if (request.DamageType == DamageType.Ice) damage *= 1f - Clamp01(_store.EnemyIceResist[target]);
                    else if (request.DamageType == DamageType.Lightning) damage *= 1f - Clamp01(_store.EnemyLightningResist[target]);
                    else if (request.DamageType == DamageType.Holy) damage *= 1f - Clamp01(_store.EnemyHolyResist[target]);
                    damage *= 1f - Clamp01(_store.EnemyDamageResistance[target]);
                }
            }
            _store.ResourceResolver.ApplyEnemyDamageResources(target, damage, request.ElementType, (request.Flags & DamageFlags.IgnoreShield) != 0, (request.Flags & DamageFlags.Execute) != 0);
            float applied = Math.Max(0f, beforeHealth - _store.EnemyHealth[target]);
            float absorbed = Math.Max(0f, beforeShield - _store.EnemyShield[target]);
            bool death = _store.EnemyHealth[target] <= 0f;
            if (death) _store.ResourceResolver.ClampEnemyHealthAtZero(target);
            if (death) _store.QueueEnemyDeath(target, request.OwnerPlayerId, request.Sequence, request.Source);
            if (!Events.TryPublish(new GameplayEvent(GameplayEventType.DamageApplied, request.Source, request.Target, default(EffectHandle), request.Effect, request.Flags, request.Sequence, request.ParentSequence, provenanceId: request.ProvenanceId, provenanceDepth: request.ProvenanceDepth), true)) Volatile.Write(ref _eventPublicationFailed, 1);
            if (DiagnosticsEnabled && !LastEventPublicationFailed) Interlocked.Increment(ref _factsPublished);
            if (death && !Events.TryPublish(new GameplayEvent(GameplayEventType.DeathQueued, request.Source, request.Target, default(EffectHandle), request.Effect, request.Flags, request.Sequence, request.ParentSequence, provenanceId: request.ProvenanceId, provenanceDepth: request.ProvenanceDepth), true)) Volatile.Write(ref _eventPublicationFailed, 1);
            if (DiagnosticsEnabled && death && !LastEventPublicationFailed) Interlocked.Increment(ref _factsPublished);
            Interlocked.Increment(ref _acceptedCount);
            return new DamageApplyResult(true, applied, absorbed, death, DamageRejectionReason.None);
        }

        private static bool IsSupportedDamageType(DamageType type)
        {
            int value = (int)type;
            return value == (int)DamageType.Physical || value == (int)DamageType.Magic || value == (int)DamageType.Fire || value == (int)DamageType.Ice || value == (int)DamageType.Lightning || value == (int)DamageType.True || value == (int)DamageType.Holy;
        }
        private static float Clamp01(float value) => value <= 0f ? 0f : value >= 1f ? 1f : value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ApplyLegacy(int targetId, float amount, ElementType element, int ownerPlayerId)
        {
            Volatile.Write(ref _lastLegacyRejection, (int)DamageRejectionReason.None);
            if (ownerPlayerId < 0 || ownerPlayerId >= ComponentStore.MAX_PLAYERS) { Volatile.Write(ref _lastLegacyRejection, (int)DamageRejectionReason.InvalidOwner); return; }
            if (!ComponentStore.IsValidEntity(targetId) || !_store.EnemyActive[targetId] || _store.EnemyHealth[targetId] <= 0f || amount <= 0f) { Volatile.Write(ref _lastLegacyRejection, (int)DamageRejectionReason.TargetAlreadyDead); return; }
            var target = _store.GetEntityHandle(targetId);
            var source = _store.GetEntityHandle(_store.PlayerEntityId);
            if (!source.IsValid) { Volatile.Write(ref _lastLegacyRejection, (int)DamageRejectionReason.InvalidSource); return; }
            TryApply(new DamageRequest(source, target, amount, DamageType.True, element, DamageFlags.None,
                DamageAmountStage.Raw, DamageCommitBoundary.GameplayResolve,
                _store.AllocateGameplaySequence(targetId), ownerPlayerId: ownerPlayerId));
        }
    }
}
