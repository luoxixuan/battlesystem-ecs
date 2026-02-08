using System;
using System.Collections.Generic;
using BattleSystemECS.Core;

namespace BattleSystemECS
{
    public class EntityManager
    {
        private Dictionary<int, string> entityNames;
        private Dictionary<int, Dictionary<string, object>> entityComponents;
        private int nextEntityId = 1;

        public EntityManager()
        {
            this.entityNames = new Dictionary<int, string>();
            this.entityComponents = new Dictionary<int, Dictionary<string, object>>();
        }

        public Entity CreateEntity()
        {
            int entityId = nextEntityId++;
            entityNames[entityId] = $"Entity_{entityId}";
            entityComponents[entityId] = new Dictionary<string, object>();
            return new Entity(entityId);
        }

        public void SetName(Entity entity, string name)
        {
            if (entity != null && entityNames.ContainsKey(entity.Id))
            {
                entityNames[entity.Id] = name;
            }
        }

        public string GetName(Entity entity)
        {
            if (entity != null && entityNames.ContainsKey(entity.Id))
            {
                return entityNames[entity.Id];
            }
            return entity != null ? $"Entity_{entity.Id}" : "Unknown";
        }

        public void AddComponent<T>(Entity entity, T component)
        {
            if (entity == null) return;

            int entityId = entity.Id;
            string componentName = typeof(T).Name;

            if (!entityComponents.ContainsKey(entityId))
            {
                entityComponents[entityId] = new Dictionary<string, object>();
            }

            entityComponents[entityId][componentName] = component;
        }

        public T GetComponent<T>(Entity entity)
        {
            if (entity == null) return default(T);

            int entityId = entity.Id;
            string componentName = typeof(T).Name;

            if (entityComponents.ContainsKey(entityId) && entityComponents[entityId].ContainsKey(componentName))
            {
                try
                {
                    return (T)entityComponents[entityId][componentName];
                }
                catch
                {
                    return default(T);
                }
            }

            return default(T);
        }

        public bool HasComponent<T>(Entity entity)
        {
            if (entity == null) return false;

            int entityId = entity.Id;
            string componentName = typeof(T).Name;

            return entityComponents.ContainsKey(entityId) && entityComponents[entityId].ContainsKey(componentName);
        }

        public void SetComponent<T>(Entity entity, T component)
        {
            if (entity == null) return;

            int entityId = entity.Id;
            string componentName = typeof(T).Name;

            if (!entityComponents.ContainsKey(entityId))
            {
                entityComponents[entityId] = new Dictionary<string, object>();
            }

            entityComponents[entityId][componentName] = component;
        }

        public List<Entity> GetAllEntities()
        {
            List<Entity> entities = new List<Entity>();
            foreach (var kvp in entityNames)
            {
                entities.Add(new Entity(kvp.Key));
            }
            return entities;
        }
    }
}
