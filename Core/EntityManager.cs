using System;
using BattleSystemECS.Core;

namespace BattleSystemECS
{
    /// <summary>
    /// SOA EntityManager — 仅负责实体创建与命名。
    /// 所有组件数据通过 ComponentStore 的并行数组直接访问，无 class 组件包装。
    /// </summary>
    public class EntityManager
    {
        private ComponentStore store;

        public EntityManager(ComponentStore store)
        {
            this.store = store;
        }

        public Entity CreateEntity()
        {
            int entityId = store.CreateEntity();
            if (entityId < 0)
                throw new InvalidOperationException($"EntityManager.CreateEntity: pool exhausted (MAX_ENTITIES={ComponentStore.MAX_ENTITIES}).");
            store.SetEntityName(entityId, $"Entity_{entityId}");
            return new Entity(entityId);
        }

        public void SetName(Entity entity, string name)
        {
            if (entity == null) return;
            store.SetEntityName(entity.Id, name);
        }
    }
}
