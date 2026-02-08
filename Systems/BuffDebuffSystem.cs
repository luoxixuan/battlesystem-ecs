using System;
using BattleSystemECS.Components;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Buff/Debuff 系统 - ECS 架构
    /// </summary>
    public class BuffDebuffSystem
    {
        private EntityManager entityManager;
        private IRenderer renderer;

        public BuffDebuffSystem(EntityManager entityManager, IRenderer renderer)
        {
            this.entityManager = entityManager;
            this.renderer = renderer;
        }

        /// <summary>
        /// 更新 Buff/Debuff 持续时间
        /// </summary>
        public void UpdateDurations(float deltaTime)
        {
            // 更新 Buff
            var entitiesWithBuff = entityManager.GetEntitiesWithComponent<BuffComponent>();

            foreach (var entityId in entitiesWithBuff)
            {
                if (!entityManager.IsEntityAlive(entityId))
                    continue;

                var buff = entityManager.GetComponent<BuffComponent>(entityId);
                if (buff != null && buff.IsActive())
                {
                    buff.UpdateDuration(deltaTime);
                    entityManager.SetComponent(entityId, buff);

                    // Buff 持续时间结束时，移除效果
                    if (!buff.IsActive())
                    {
                        RemoveBuffEffect(entityId, buff);
                    }
                }
            }

            // 更新 Debuff
            var entitiesWithDebuff = entityManager.GetEntitiesWithComponent<DebuffComponent>();

            foreach (var entityId in entitiesWithDebuff)
            {
                if (!entityManager.IsEntityAlive(entityId))
                    continue;

                var debuff = entityManager.GetComponent<DebuffComponent>(entityId);
                if (debuff != null && debuff.IsActive())
                {
                    debuff.UpdateDuration(deltaTime);
                    entityManager.SetComponent(entityId, debuff);

                    // 应用持续伤害
                    if (debuff.DamageOverTime > 0f)
                    {
                        ApplyDamageOverTime(entityId, deltaTime);
                    }

                    // Debuff 持续时间结束时，移除效果
                    if (!debuff.IsActive())
                    {
                        RemoveDebuffEffect(entityId, debuff);
                    }
                }
            }
        }

        /// <summary>
        /// 应用 Buff 效果
        /// </summary>
        public void ApplyBuffEffects()
        {
            var entitiesWithBuff = entityManager.GetEntitiesWithComponent<BuffComponent>();

            foreach (var entityId in entitiesWithBuff)
            {
                if (!entityManager.IsEntityAlive(entityId))
                    continue;

                var buff = entityManager.GetComponent<BuffComponent>(entityId);
                if (buff != null && buff.IsActive())
                {
                    ApplyBuffEffect(entityId, buff);
                }
            }
        }

        /// <summary>
        /// 应用 Debuff 效果
        /// </summary>
        public void ApplyDebuffEffects()
        {
            var entitiesWithDebuff = entityManager.GetEntitiesWithComponent<DebuffComponent>();

            foreach (var entityId in entitiesWithDebuff)
            {
                if (!entityManager.IsEntityAlive(entityId))
                    continue;

                var debuff = entityManager.GetComponent<DebuffComponent>(entityId);
                if (debuff != null && debuff.IsActive())
                {
                    ApplyDebuffEffect(entityId, debuff);
                }
            }
        }

        private void ApplyBuffEffect(int entityId, BuffComponent buff)
        {
            var health = entityManager.GetComponent<HealthComponent>(entityId);
            var attack = entityManager.GetComponent<AttackPowerComponent>(entityId);
            var defense = entityManager.GetComponent<DefensePowerComponent>(entityId);

            buff.ApplyEffect(health, attack, defense);

            if (health != null)
                entityManager.SetComponent(entityId, health);

            if (attack != null)
                entityManager.SetComponent(entityId, attack);

            if (defense != null)
                entityManager.SetComponent(entityId, defense);

            renderer.Log($"[BUFF] {buff.BuffName} 效果已应用到实体 {entityId}");
        }

        private void ApplyDebuffEffect(int entityId, DebuffComponent debuff)
        {
            var health = entityManager.GetComponent<HealthComponent>(entityId);
            var attack = entityManager.GetComponent<AttackPowerComponent>(entityId);
            var defense = entityManager.GetComponent<DefensePowerComponent>(entityId);

            debuff.ApplyEffect(health, attack, defense);

            if (health != null)
                entityManager.SetComponent(entityId, health);

            if (attack != null)
                entityManager.SetComponent(entityId, attack);

            if (defense != null)
                entityManager.SetComponent(entityId, defense);

            renderer.Log($"[DEBUFF] {debuff.DebuffName} 效果已应用到实体 {entityId}");
        }

        private void RemoveBuffEffect(int entityId, BuffComponent buff)
        {
            var health = entityManager.GetComponent<HealthComponent>(entityId);
            var attack = entityManager.GetComponent<AttackPowerComponent>(entityId);
            var defense = entityManager.GetComponent<DefensePowerComponent>(entityId);

            buff.RemoveEffect(health, attack, defense);

            if (health != null)
                entityManager.SetComponent(entityId, health);

            if (attack != null)
                entityManager.SetComponent(entityId, attack);

            if (defense != null)
                entityManager.SetComponent(entityId, defense);

            renderer.Log($"[BUFF] {buff.BuffName} 效果已移除");
        }

        private void RemoveDebuffEffect(int entityId, DebuffComponent debuff)
        {
            var health = entityManager.GetComponent<HealthComponent>(entityId);
            var attack = entityManager.GetComponent<AttackPowerComponent>(entityId);
            var defense = entityManager.GetComponent<DefensePowerComponent>(entityId);

            debuff.RemoveEffect(health, attack, defense);

            if (health != null)
                entityManager.SetComponent(entityId, health);

            if (attack != null)
                entityManager.SetComponent(entityId, attack);

            if (defense != null)
                entityManager.SetComponent(entityId, defense);

            renderer.Log($"[DEBUFF] {debuff.DebuffName} 效果已移除");
        }

        private void ApplyDamageOverTime(int entityId, float deltaTime)
        {
            var debuff = entityManager.GetComponent<DebuffComponent>(entityId);
            if (debuff == null)
                return;

            debuff.ApplyDamageOverTime(entityManager.GetComponent<HealthComponent>(entityId), deltaTime);

            renderer.Log($"[DEBUFF] {debuff.DebuffName} 持续伤害：每秒 {debuff.DamageOverTime:F1} 点");
        }
    }
}
