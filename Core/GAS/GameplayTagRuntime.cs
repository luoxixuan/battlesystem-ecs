using System.Collections.Generic;
using BattleSystemECS.Components;

namespace BattleSystemECS.Core.GAS
{
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
