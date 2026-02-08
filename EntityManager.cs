using System;
using System.Collections.Generic;

namespace BattleSystemECS
{
    /// <summary>
    /// 实体管理器 - ECS 架构
    /// </summary>
    public class EntityManager
    {
        private Dictionary<int, Dictionary<Type, object>> entities;
        private Dictionary<Type, HashSet<int>> componentIndex;
        private int nextEntityId = 1;
        private HashSet<int> aliveEntities;

        public EntityManager()
        {
            entities = new Dictionary<int, Dictionary<Type, object>>();
            componentIndex = new Dictionary<Type, HashSet<int>>();
            aliveEntities = new HashSet<int>();
        }

        /// <summary>
        /// 创建实体
        /// </summary>
        public int CreateEntity()
        {
            int entityId = nextEntityId++;
            entities[entityId] = new Dictionary<Type, object>();
            aliveEntities.Add(entityId);
            return entityId;
        }

        /// <summary>
        /// 添加组件
        /// </summary>
        public void AddComponent<T>(int entityId, T component) where T : class
        {
            if (!entities.ContainsKey(entityId))
                return;

            entities[entityId][typeof(T)] = component;

            if (!componentIndex.ContainsKey(typeof(T)))
                componentIndex[typeof(T)] = new HashSet<int>();

            componentIndex[typeof(T)].Add(entityId);
        }

        /// <summary>
        /// 获取组件
        /// </summary>
        public T GetComponent<T>(int entityId) where T : class
        {
            if (!entities.ContainsKey(entityId))
                return null;

            if (!entities[entityId].ContainsKey(typeof(T)))
                return null;

            return entities[entityId][typeof(T)] as T;
        }

        /// <summary>
        /// 检查实体是否有组件
        /// </summary>
        public bool HasComponent<T>(int entityId) where T : class
        {
            if (!entities.ContainsKey(entityId))
                return false;

            return entities[entityId].ContainsKey(typeof(T));
        }

        /// <summary>
        /// 设置组件
        /// </summary>
        public void SetComponent<T>(int entityId, T component) where T : class
        {
            if (!entities.ContainsKey(entityId))
                return;

            entities[entityId][typeof(T)] = component;
        }

        /// <summary>
        /// 获取拥有特定组件的所有实体
        /// </summary>
        public HashSet<int> GetEntitiesWithComponent<T>() where T : class
        {
            if (!componentIndex.ContainsKey(typeof(T)))
                return new HashSet<int>();

            return new HashSet<int>(componentIndex[typeof(T)]);
        }

        /// <summary>
        /// 检查实体是否存活
        /// </summary>
        public bool IsEntityAlive(int entityId)
        {
            return aliveEntities.Contains(entityId);
        }

        /// <summary>
        /// 设置实体死亡
        /// </summary>
        public void SetEntityDead(int entityId)
        {
            aliveEntities.Remove(entityId);
        }

        /// <summary>
        /// 获取所有存活的实体
        /// </summary>
        public HashSet<int> GetAliveEntities()
        {
            return new HashSet<int>(aliveEntities);
        }

        /// <summary>
        /// 销毁实体
        /// </summary>
        public void DestroyEntity(int entityId)
        {
            if (entities.ContainsKey(entityId))
            {
                // 从组件索引中移除
                foreach (var componentType in entities[entityId].Keys)
                {
                    if (componentIndex.ContainsKey(componentType))
                    {
                        componentIndex[componentType].Remove(entityId);
                    }
                }

                entities.Remove(entityId);
            }

            aliveEntities.Remove(entityId);
        }

        /// <summary>
        /// 销毁所有实体
        /// </summary>
        public void DestroyAllEntities()
        {
            entities.Clear();
            componentIndex.Clear();
            aliveEntities.Clear();
            nextEntityId = 1;
        }
    }
}
