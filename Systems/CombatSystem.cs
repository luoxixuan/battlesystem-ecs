using System;
using BattleSystemECS.Components;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 战斗系统 - ECS 架构，逻辑核心
    /// </summary>
    public class CombatSystem
    {
        private EntityManager entityManager;
        private DamageSystem damageSystem;
        private SkillSystem skillSystem;
        private BuffDebuffSystem buffDebuffSystem;
        private IRenderer renderer;
        private int battleTurn = 0;
        private bool battleInProgress = false;
        private int playerEntityId;
        private int enemyEntityId;

        public CombatSystem(EntityManager entityManager, IRenderer renderer)
        {
            this.entityManager = entityManager;
            this.damageSystem = new DamageSystem(entityManager);
            this.skillSystem = new SkillSystem(entityManager, renderer);
            this.buffDebuffSystem = new BuffDebuffSystem(entityManager, renderer);
            this.renderer = renderer;
        }

        /// <summary>
        /// 开始战斗
        /// </summary>
        public void StartBattle(int playerId, int enemyId)
        {
            this.playerEntityId = playerId;
            this.enemyEntityId = enemyId;
            this.battleTurn = 0;
            this.battleInProgress = true;

            renderer.LogBattleStart("玩家 VS 敌人");
            LogEntityInfo(playerId, "玩家");
            LogEntityInfo(enemyId, "敌人");
        }

        private void LogEntityInfo(int entityId, string displayName)
        {
            var health = entityManager.GetComponent<HealthComponent>(entityId);
            var attack = entityManager.GetComponent<AttackPowerComponent>(entityId);
            var defense = entityManager.GetComponent<DefensePowerComponent>(entityId);

            if (health != null && attack != null && defense != null)
            {
                renderer.Log($"{displayName} - 攻击: {attack.Value}, 防御: {defense.Value}, 生命: {health.Current}/{health.Max}");
            }
        }

        /// <summary>
        /// 处理战斗
        /// </summary>
        public void ProcessBattle()
        {
            if (!battleInProgress)
                return;

            // 更新 Buff/Debuff 持续时间和效果
            float deltaTime = 1.0f; // 假设每回合 1 秒
            buffDebuffSystem.UpdateDurations(deltaTime);
            buffDebuffSystem.ApplyBuffEffects();
            buffDebuffSystem.ApplyDebuffEffects();

            battleTurn++;
            renderer.LogTurn(battleTurn);

            // 检查双方是否存活
            bool playerAlive = entityManager.IsEntityAlive(playerEntityId);
            bool enemyAlive = entityManager.IsEntityAlive(enemyEntityId);

            if (!playerAlive || !enemyAlive)
            {
                EndBattle(playerAlive ? GetPlayerName() : GetEnemyName());
                return;
            }

            // 玩家尝试使用技能
            if (playerAlive && TryUseSkill(playerEntityId, enemyEntityId))
            {
                if (!entityManager.IsEntityAlive(enemyEntityId))
                {
                    renderer.LogDeath(GetEnemyName());
                    renderer.LogWin(GetPlayerName());
                    battleInProgress = false;
                    return;
                }
            }

            // 玩家普通攻击
            if (playerAlive)
            {
                float damage = damageSystem.CalculateDamage(playerEntityId, enemyEntityId);
                bool isCritical = damageSystem.CheckCritical();
                if (isCritical)
                    damage *= 1.5f;

                ApplyDamage(playerEntityId, enemyEntityId, damage, isCritical);
            }

            // 检查敌人是否死亡
            if (!entityManager.IsEntityAlive(enemyEntityId))
            {
                renderer.LogDeath(GetEnemyName());
                renderer.LogWin(GetPlayerName());
                battleInProgress = false;
                return;
            }

            // 敌人尝试使用技能
            if (enemyAlive && TryUseSkill(enemyEntityId, playerEntityId))
            {
                if (!entityManager.IsEntityAlive(playerEntityId))
                {
                    renderer.LogDeath(GetPlayerName());
                    renderer.LogWin(GetEnemyName());
                    battleInProgress = false;
                    return;
                }
            }

            // 敌人普通攻击
            if (enemyAlive)
            {
                float damage = damageSystem.CalculateDamage(enemyEntityId, playerEntityId);
                bool isCritical = damageSystem.CheckCritical();
                if (isCritical)
                    damage *= 1.5f;

                ApplyDamage(enemyEntityId, playerEntityId, damage, isCritical);
            }

            // 检查玩家是否死亡
            if (!entityManager.IsEntityAlive(playerEntityId))
            {
                renderer.LogDeath(GetPlayerName());
                renderer.LogWin(GetEnemyName());
                battleInProgress = false;
            }
        }

        private bool TryUseSkill(int attackerId, int defenderId)
        {
            // 尝试使用技能（示例：火球术）
            string skillName = "火球术";
            if (skillSystem.UseSkill(attackerId, skillName))
            {
                var skill = entityManager.GetComponent<SkillComponent>(attackerId);
                if (skill != null)
                {
                    ApplySkillDamage(attackerId, defenderId, skill.Damage);
                    return true;
                }
            }
            return false;
        }

        private void ApplySkillDamage(int attackerId, int defenderId, float skillDamage)
        {
            var health = entityManager.GetComponent<HealthComponent>(defenderId);
            var defense = entityManager.GetComponent<DefensePowerComponent>(defenderId);

            float damage = skillDamage;
            if (defense != null)
            {
                damage = Math.Max(1f, skillDamage - defense.Value * 0.3f); // 技能伤害受防御影响较小
            }

            health.Current = Math.Max(0f, health.Current - damage);
            entityManager.SetComponent(defenderId, health);

            // 记录技能伤害
            string attackerName = GetEntityName(attackerId);
            string defenderName = GetEntityName(defenderId);
            renderer.Log($"[SKILL DAMAGE] {attackerName} 使用火球术，对 {defenderName} 造成 {damage:F1} 点技能伤害");
            renderer.Log($"{defenderName} 剩余生命: {health.Current:F1}/{health.Max:F1}");

            // 检查是否死亡
            if (health.Current <= 0f)
            {
                entityManager.SetEntityDead(defenderId);
                renderer.LogDeath(defenderName);
            }
        }

        private void ApplyDamage(int attackerId, int defenderId, float damage, bool isCritical)
        {
            var health = entityManager.GetComponent<HealthComponent>(defenderId);
            if (health == null)
                return;

            health.Current = Math.Max(0f, health.Current - damage);
            entityManager.SetComponent(defenderId, health);

            // 记录伤害 - 通过渲染器
            string attackerName = GetEntityName(attackerId);
            string defenderName = GetEntityName(defenderId);
            renderer.LogDamage(attackerName, defenderName, damage, isCritical);
            renderer.Log($"{defenderName} 剩余生命: {health.Current:F1}/{health.Max:F1}");

            // 检查是否死亡
            if (health.Current <= 0f)
            {
                entityManager.SetEntityDead(defenderId);
                renderer.LogDeath(defenderName);
            }
        }

        private string GetPlayerName()
        {
            var name = entityManager.GetComponent<NameComponent>(playerEntityId);
            return name?.Value ?? "玩家";
        }

        private string GetEnemyName()
        {
            var name = entityManager.GetComponent<NameComponent>(enemyEntityId);
            return name?.Value ?? "敌人";
        }

        private string GetEntityName(int entityId)
        {
            var name = entityManager.GetComponent<NameComponent>(entityId);
            return name?.Value ?? "未知";
        }

        private void EndBattle(string winner)
        {
            renderer.LogWin(winner);
            battleInProgress = false;
        }

        public bool IsBattleInProgress => battleInProgress;

        public EntityManager GetEntityManager => entityManager;
    }
}
