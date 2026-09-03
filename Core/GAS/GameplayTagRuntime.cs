using System.Collections.Generic;
using BattleSystemECS.Components;

namespace BattleSystemECS.Core.GAS
{
    /// <summary>效果授予标签的贡献计数；HasTag 走计数而不是每次扫槽。</summary>
    public sealed class TagContributionState
    {
        private readonly Dictionary<(int entity, int tag), int> _counts = new Dictionary<(int, int), int>(256);

        public void AddGranted(int entityId, IReadOnlyList<TagId> granted)
        {
            if (granted == null) return;
            for (int i = 0; i < granted.Count; i++)
            {
                var key = (entityId, granted[i].Value);
                _counts.TryGetValue(key, out int n);
                _counts[key] = n + 1;
            }
        }

        public void RemoveGranted(int entityId, IReadOnlyList<TagId> granted)
        {
            if (granted == null) return;
            for (int i = 0; i < granted.Count; i++)
            {
                var key = (entityId, granted[i].Value);
                if (!_counts.TryGetValue(key, out int n)) continue;
                if (n <= 1) _counts.Remove(key);
                else _counts[key] = n - 1;
            }
        }

        public bool Has(int entityId, TagId tag)
        {
            return _counts.TryGetValue((entityId, tag.Value), out int n) && n > 0;
        }

        public void ClearEntity(int entityId)
        {
            if (_counts.Count == 0) return;
            var stale = new List<(int, int)>(8);
            foreach (var pair in _counts)
                if (pair.Key.entity == entityId) stale.Add(pair.Key);
            for (int i = 0; i < stale.Count; i++) _counts.Remove(stale[i]);
        }
    }

    /// <summary>查询生效中的玩法效果授予的标签。</summary>
    public static class GameplayTagRuntime
    {
        public static bool Matches(ComponentStore store, int entityId,
            IReadOnlyList<TagId> requiredTags, IReadOnlyList<TagId> blockedTags)
        {
            if (store == null || !store.GetEntityHandle(entityId).IsValid) return false;
            for (int i = 0; i < requiredTags.Count; i++)
                if (!HasTag(store, entityId, requiredTags[i])) return false;
            for (int i = 0; i < blockedTags.Count; i++)
                if (HasTag(store, entityId, blockedTags[i])) return false;
            return true;
        }

        public static bool HasTag(ComponentStore store, int entityId, TagId tag)
        {
            if (store.TagState.Has(entityId, tag)) return true;
            int count = store.GetEffectCount(entityId);
            for (int slot = 0; slot < count; slot++)
            {
                if (!store.TryGetActiveEffectAt(entityId, slot, out _, out var definition, out _)) continue;
                for (int i = 0; i < definition.GrantedTags.Count; i++)
                    if (definition.GrantedTags[i].Equals(tag)) return true;
            }
            return false;
        }
    }
}
