using System;
using BattleSystemECS.Components;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 技能系统 - ECS 架构
    /// </summary>
    public class SkillSystem
    {
        private EntityManager entityManager;
        private IRenderer renderer;

        public SkillSystem(EntityManager entityManager, IRenderer renderer)
        {
            this.entityManager = entityManager;
            this.renderer = renderer;
        }

        /// <summary>
        /// 更新技能冷却
        /// </summary>
        public void UpdateCooldowns(float deltaTime)
        {
            var entitiesWithSkill = entityManager.GetEntitiesWithComponent<SkillComponent>();

            foreach (var entityId in entitiesWithSkill)
            {
                if (!entityManager.IsEntityAlive(entityId))
                    continue;

                var skill = entityManager.GetComponent<SkillComponent>(entityId);
                if (skill != null)
                {
                    skill.UpdateCooldown(deltaTime);
                    entityManager.SetComponent(entityId, skill);
                }
            }
        }

        /// <summary>
        /// 恢复魔力
        /// </summary>
        public void RegenerateMana(float deltaTime, float regenRate)
        {
            var entitiesWithSkill = entityManager.GetEntitiesWithComponent<SkillComponent>();

            foreach (var entityId in entitiesWithSkill)
            {
                if (!entityManager.IsEntityAlive(entityId))
                    continue;

                var skill = entityManager.GetComponent<SkillComponent>(entityId);
                if (skill != null)
                {
                    skill.RegenerateMana(deltaTime, regenRate);
                    entityManager.SetComponent(entityId, skill);
                }
            }
        }

        /// <summary>
        /// 使用技能
        /// </summary>
        public bool UseSkill(int entityId, string skillName)
        {
            var skill = entityManager.GetComponent<SkillComponent>(entityId);
            if (skill == null)
            {
                renderer.Log($"[ERROR] 实体 {entityId} 没有技能组件");
                return false;
            }

            if (skill.SkillName != skillName)
            {
                renderer.Log($"[ERROR] 实体 {entityId} 没有名为 {skillName} 的技能");
                return false;
            }

            if (!skill.CanUseSkill())
            {
                if (skill.CurrentCooldown > 0f)
                {
                    renderer.Log($"[ERROR] 技能 {skillName} 还在冷却中，剩余 {skill.CurrentCooldown:F1} 秒");
                }
                else if (skill.CurrentMana < skill.ManaCost)
                {
                    renderer.Log($"[ERROR] 魔力不足，需要 {skill.ManaCost}，当前 {skill.CurrentMana}");
                }

                return false;
            }

            // 使用技能
            skill.UseSkill();
            entityManager.SetComponent(entityId, skill);

            renderer.Log($"[SKILL] {skillName} 释放成功！造成 {skill.Damage:F1} 点伤害，消耗 {skill.ManaCost} 魔力");

            return true;
        }

        /// <summary>
        /// 获取技能状态
        /// </summary>
        public string GetSkillStatus(int entityId, string skillName)
        {
            var skill = entityManager.GetComponent<SkillComponent>(entityId);
            if (skill == null)
                return "无技能";

            string status = $"技能: {skill.SkillName}\n";
            status += $"伤害: {skill.Damage:F1}\n";
            status += $"冷却: {skill.CurrentCooldown:F1}/{skill.Cooldown:F1}秒\n";
            status += $"魔力: {skill.CurrentMana}/{skill.MaxMana}\n";
            status += $"魔力消耗: {skill.ManaCost}\n";
            status += $"可以使用: {skill.CanUseSkill()}";

            return status;
        }
    }
}
