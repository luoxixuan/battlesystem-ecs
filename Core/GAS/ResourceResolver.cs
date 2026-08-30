using System;
using BattleSystemECS.Core;

namespace BattleSystemECS.Core.GAS
{
    public enum ResourceKind { CurrentHealth, MaxHealth, Shield, Mana, Gold }

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
        public float Apply(ResourceRequest request)
        {
            int playerId = request.Target.Index;
            switch (request.Resource.Value)
            {
                case 3: return Heal(playerId, request.Delta);
                case 4: return ApplyGold(playerId, request.Delta);
                case 7: return ApplyMana(playerId, request.Delta);
                default: return ApplyShield(playerId, request.Delta);
            }
        }
        public float Heal(int playerId, float amount) { if (!Valid(playerId) || amount <= 0f) return 0f; var old = _store.PlayerCurrentHealth[playerId]; var next = Clamp(old + amount, _store.PlayerMaxHealth[playerId]); _store.PlayerCurrentHealth[playerId] = next; return next - old; }
        public float ApplyShield(int playerId, float delta) { if (!Valid(playerId) || float.IsNaN(delta)) return 0f; var old = _store.PlayerShield[playerId]; var next = Math.Max(0f, old + delta); _store.PlayerShield[playerId] = next; return next - old; }
        public float ApplyMana(int playerId, float delta) { if (!Valid(playerId) || float.IsNaN(delta)) return 0f; var old = _store.PlayerMana[playerId]; var next = Clamp(old + delta, _store.PlayerMaxMana[playerId]); _store.PlayerMana[playerId] = next; return next - old; }
        public float ApplyGold(int playerId, float delta) { if (!Valid(playerId) || float.IsNaN(delta)) return 0f; var old = _store.PlayerGold[playerId]; var next = Math.Max(0f, old + delta); _store.PlayerGold[playerId] = next; return next - old; }
        public float SetMaxHealth(int playerId, float value) { if (!Valid(playerId)) return 0f; var old = _store.PlayerMaxHealth[playerId]; var next = Math.Max(0f, value); _store.PlayerMaxHealth[playerId] = next; if (_store.PlayerCurrentHealth[playerId] > next) _store.PlayerCurrentHealth[playerId] = next; return next - old; }
        private static float Clamp(float value, float max) => Math.Min(Math.Max(0f, value), Math.Max(0f, max));
        private static bool Valid(int playerId) => (uint)playerId < ComponentStore.MAX_PLAYERS;
    }
}
