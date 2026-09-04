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
                AdjustContribution(entityId, granted[i], 1);
        }

        public void RemoveGranted(int entityId, IReadOnlyList<TagId> granted)
        {
            if (granted == null) return;
            for (int i = 0; i < granted.Count; i++)
                AdjustContribution(entityId, granted[i], -1);
        }

        public bool Has(int entityId, TagId tag)
        {
            return GetCount(entityId, tag) > 0;
        }

        public int GetCount(int entityId, TagId tag)
        {
            return _counts.TryGetValue((entityId, tag.Value), out int n) ? n : 0;
        }

        /// <summary>
        /// 授予叶标签时把已编译祖先一并计入。HasTag(祖先) 走同一整数键，不在运行时扫平列表。
        /// </summary>
        private void AdjustContribution(int entityId, TagId leaf, int delta)
        {
            AddDelta(entityId, leaf, delta);
            var ancestors = GameplayTagVocabulary.AncestorsOf(leaf);
            for (int i = 0; i < ancestors.Count; i++)
                AddDelta(entityId, ancestors[i], delta);
        }

        private void AddDelta(int entityId, TagId tag, int delta)
        {
            var key = (entityId, tag.Value);
            _counts.TryGetValue(key, out int n);
            int next = n + delta;
            if (next <= 0) _counts.Remove(key);
            else _counts[key] = next;
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
            return store != null && store.TagState.Has(entityId, tag);
        }

        public static int GetCount(ComponentStore store, int entityId, TagId tag)
        {
            return store == null ? 0 : store.TagState.GetCount(entityId, tag);
        }
    }
}
