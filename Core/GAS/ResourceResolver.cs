using System;
using BattleSystemECS.Core;

namespace BattleSystemECS.Core.GAS
{
    public enum ResourceKind { CurrentHealth, MaxHealth, Shield, Mana, Gold }
    public enum ResourceRejectionReason { None, UnknownResource, InvalidValue, InvalidTarget, UnsupportedOperation }
    public readonly struct ResourceApplyResult
    {
        public readonly bool Accepted; public readonly float Applied; public readonly ResourceRejectionReason Reason;
        public ResourceApplyResult(bool accepted, float applied, ResourceRejectionReason reason) { Accepted = accepted; Applied = applied; Reason = reason; }
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
        private readonly ComponentStore _store;
        public ResourceResolver(ComponentStore store) { _store = store ?? throw new ArgumentNullException(nameof(store)); }
        public ResourceApplyResult TryApply(ResourceRequest request)
        {
            if (!Valid(request.Target.Index)) return new ResourceApplyResult(false, 0f, ResourceRejectionReason.InvalidTarget);
            if (float.IsNaN(request.Delta) || float.IsInfinity(request.Delta)) return new ResourceApplyResult(false, 0f, ResourceRejectionReason.InvalidValue);
            switch (request.Resource.Value)
            {
                case 3: return new ResourceApplyResult(true, Heal(request.Target.Index, request.Delta), ResourceRejectionReason.None);
                case 4: return new ResourceApplyResult(true, ApplyGold(request.Target.Index, request.Delta), ResourceRejectionReason.None);
                case 7: return new ResourceApplyResult(true, ApplyMana(request.Target.Index, request.Delta), ResourceRejectionReason.None);
                case 9: return new ResourceApplyResult(true, ApplyShield(request.Target.Index, request.Delta), ResourceRejectionReason.None);
                default: return new ResourceApplyResult(false, 0f, ResourceRejectionReason.UnknownResource);
            }
        }
        public float Apply(ResourceRequest request)
        {
            var result = TryApply(request); if (!result.Accepted) return 0f; return result.Applied;
        }
        public float Heal(int playerId, float amount) { if (!Valid(playerId) || amount <= 0f || !Finite(amount)) return 0f; var old = _store.PlayerCurrentHealth[playerId]; var next = Clamp(old + amount, _store.PlayerMaxHealth[playerId]); _store.PlayerCurrentHealth[playerId] = next; return next - old; }
        public float ApplyShield(int playerId, float delta) { if (!Valid(playerId) || !Finite(delta)) return 0f; var old = _store.PlayerShield[playerId]; var next = Math.Max(0f, old + delta); _store.PlayerShield[playerId] = next; return next - old; }
        public float ApplyMana(int playerId, float delta) { if (!Valid(playerId) || !Finite(delta)) return 0f; var old = _store.PlayerMana[playerId]; var next = Clamp(old + delta, _store.PlayerMaxMana[playerId]); _store.PlayerMana[playerId] = next; return next - old; }
        public float ApplyGold(int playerId, float delta) { if (!Valid(playerId) || !Finite(delta)) return 0f; var old = _store.PlayerGold[playerId]; var next = Math.Max(0f, old + delta); _store.PlayerGold[playerId] = next; return next - old; }
        public float SetMaxHealth(int playerId, float value) { if (!Valid(playerId) || !Finite(value)) return 0f; var old = _store.PlayerMaxHealth[playerId]; var next = Math.Max(0f, value); _store.PlayerMaxHealth[playerId] = next; if (_store.PlayerCurrentHealth[playerId] > next) _store.PlayerCurrentHealth[playerId] = next; return next - old; }
        private static float Clamp(float value, float max) => Math.Min(Math.Max(0f, value), Math.Max(0f, max));
        private static bool Valid(int playerId) => (uint)playerId < ComponentStore.MAX_PLAYERS;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
