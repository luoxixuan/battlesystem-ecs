using System;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 技能释放系统 - SOA (Struct of Arrays) 优化
    /// 实现三种技能：
    /// 1. 十字范围伤害技能（Cross Slash）
    /// 2. 3x3 范围伤害技能（Mega Explosion）
    /// 3. 攻击距离 9 的单体技能（Sniper Shot）
    /// </summary>
    public class SkillSystem
    {
        private Core.ComponentStore store;
        private IRenderer renderer;
        private int playerId;
        private float deltaTime = 1f;

        // 技能列表
        private string skillCrossSlash = "Cross Slash";      // 十字范围伤害，倍率 400%，攻击距离 3
        private string skillMegaExplosion = "Mega Explosion"; // 3x3 范围伤害，倍率 400%，攻击距离 5
        private string skillSniperShot = "Sniper Shot";       // 单体伤害，倍率 400%，攻击距离 9

        public SkillSystem(Core.ComponentStore store, IRenderer renderer, int playerId)
        {
            this.store = store;
            this.renderer = renderer;
            this.playerId = playerId;
        }

        /// <summary>
        /// 初始化玩家技能
        /// </summary>
        public void InitializePlayerSkills()
        {
            // 设置十字范围伤害技能
            store.SetSkillName(playerId, skillCrossSlash);
            store.SetSkillDamageMultiplier(playerId, 4f);  // 400%
            store.SetSkillAreaWidth(playerId, 3);      // 十字形：中心 + 左右
            store.SetSkillAreaHeight(playerId, 3);     // 十字形：中心 + 上下
            store.SetSkillAttackRange(playerId, 3);    // 攻击距离 3
            store.SetSkillCooldown(playerId, 5f);     // 冷却时间 5 秒
            store.SetSkillCurrentCooldown(playerId, 0f); // 当前冷却 0 秒

            renderer.Log("[SKILL] Cross Slash skill equipped!");
            renderer.Log("[SKILL]   - Damage Multiplier: 400%");
            renderer.Log("[SKILL]   - Area: 3x3 (Cross shape)");
            renderer.Log("[SKILL]   - Attack Range: 3 grids");
            renderer.Log("[SKILL]   - Cooldown: 5 seconds");

            // 设置 3x3 范围伤害技能
            store.SetSkillName(playerId, skillMegaExplosion);
            store.SetSkillDamageMultiplier(playerId, 4f);  // 400%
            store.SetSkillAreaWidth(playerId, 3);      // 3x3 范围
            store.SetSkillAreaHeight(playerId, 3);
            store.SetSkillAttackRange(playerId, 5);    // 攻击距离 5
            store.SetSkillCooldown(playerId, 10f);    // 冷却时间 10 秒
            store.SetSkillCurrentCooldown(playerId, 0f); // 当前冷却 0 秒

            renderer.Log("[SKILL] Mega Explosion skill equipped!");
            renderer.Log("[SKILL]   - Damage Multiplier: 400%");
            renderer.Log("[SKILL]   - Area: 3x3 (Box shape)");
            renderer.Log("[SKILL]   - Attack Range: 5 grids");
            renderer.Log("[SKILL]   - Cooldown: 10 seconds");

            // 设置单体高伤害技能
            store.SetSkillName(playerId, skillSniperShot);
            store.SetSkillDamageMultiplier(playerId, 4f);  // 400%
            store.SetSkillAreaWidth(playerId, 1);      // 单体
            store.SetSkillAreaHeight(playerId, 1);
            store.SetSkillAttackRange(playerId, 9);    // 攻击距离 9
            store.SetSkillCooldown(playerId, 8f);     // 冷却时间 8 秒
            store.SetSkillCurrentCooldown(playerId, 0f); // 当前冷却 0 秒

            renderer.Log("[SKILL] Sniper Shot skill equipped!");
            renderer.Log("[SKILL]   - Damage Multiplier: 400%");
            renderer.Log("[SKILL]   - Area: 1x1 (Single target)");
            renderer.Log("[SKILL]   - Attack Range: 9 grids");
            renderer.Log("[SKILL]   - Cooldown: 8 seconds");
        }

        /// <summary>
        /// 更新技能冷却
        /// </summary>
        public void Update(float deltaTime)
        {
            this.deltaTime = deltaTime;

            // 更新技能冷却
            float currentCooldown = store.GetSkillCurrentCooldown(playerId);
            if (currentCooldown > 0f)
            {
                float newCooldown = System.Math.Max(0f, currentCooldown - deltaTime);
                store.SetSkillCurrentCooldown(playerId, newCooldown);
            }
        }

        /// <summary>
        /// 释放技能
        /// </summary>
        public void CastSkill(string skillName)
        {
            float currentCooldown = store.GetSkillCurrentCooldown(playerId);
            if (currentCooldown > 0f)
            {
                renderer.Log($"[SKILL] Skill '{skillName}' is on cooldown! {currentCooldown:F1}s remaining");
                return;
            }

            // 获取玩家属性
            float baseDamage = store.GetPlayerAttackDamage(playerId);
            float attackRange = store.GetPlayerAttackRange(playerId);
            float playerX = store.PositionX[playerId];
            float playerY = store.PositionY[playerId];

            // 根据技能名称释放不同技能
            if (skillName == skillCrossSlash)
            {
                CastCrossSlash(baseDamage, playerX, playerY);
            }
            else if (skillName == skillMegaExplosion)
            {
                CastMegaExplosion(baseDamage, playerX, playerY);
            }
            else if (skillName == skillSniperShot)
            {
                CastSniperShot(baseDamage, playerX, playerY);
            }
            else
            {
                renderer.Log($"[SKILL] Unknown skill: '{skillName}'");
            }
        }

        /// <summary>
        /// 释放十字范围伤害技能
        /// </summary>
        private void CastCrossSlash(float baseDamage, float playerX, float playerY)
        {
            float damageMultiplier = 4f;  // 400%
            float finalDamage = baseDamage * damageMultiplier;
            int range = 3;

            // 十字形：中心 + 左右 + 上下
            int[] xOffset = { 0, -1, 1, 0, 0 };
            int[] yOffset = { 0, 0, 0, -1, 1 };

            int enemiesHit = 0;

            // 获取所有活跃敌人
            var activeEnemyIds = store.GetAllActiveEnemyIds();

            foreach (int enemyId in activeEnemyIds)
            {
                if (enemyId == playerId) continue;

                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];
                float enemyHealth = store.GetEnemyHealth(enemyId);

                if (enemyHealth <= 0f)
                    continue;

                // 检查敌人是否在十字范围内
                for (int i = 0; i < xOffset.Length; i++)
                {
                    float targetX = playerX + xOffset[i];
                    float targetY = playerY + yOffset[i];

                    if (System.Math.Abs(enemyX - targetX) < 0.5f && System.Math.Abs(enemyY - targetY) < 0.5f)
                    {
                        // 计算攻击距离
                        float distance = System.Math.Abs(targetX - playerX);
                        if (distance <= range)
                        {
                            // 造成伤害
                            enemyHealth = System.Math.Max(0f, enemyHealth - finalDamage);
                            store.SetEnemyHealth(enemyId, enemyHealth);
                            enemiesHit++;

                            renderer.Log($"[SKILL] Cross Slash hit enemy {enemyId} at ({enemyX:F0}, {enemyY:F0}), damage: {finalDamage:F1}");

                            if (enemyHealth <= 0f)
                            {
                                // 击杀敌人，奖励金币
                                int goldReward = store.GetEnemyGoldReward(enemyId);
                                float currentGold = store.GetPlayerGold(playerId);
                                store.SetPlayerGold(playerId, currentGold + goldReward);

                                renderer.Log($"[SKILL] Cross Slash killed enemy {enemyId}, gained {goldReward} gold");
                            }
                            break;
                        }
                    }
                }
            }

            // 设置冷却
            float cooldown = store.GetSkillCooldown(playerId);
            store.SetSkillCurrentCooldown(playerId, cooldown);

            renderer.Log($"[SKILL] Cross Slash cast! Hit {enemiesHit} enemies, cooldown: {cooldown}s");
        }

        /// <summary>
        /// 释放 3x3 范围伤害技能
        /// </summary>
        private void CastMegaExplosion(float baseDamage, float playerX, float playerY)
        {
            float damageMultiplier = 4f;  // 400%
            float finalDamage = baseDamage * damageMultiplier;
            int range = 5;

            // 3x3 方形：以玩家为中心，3x3 范围
            int enemiesHit = 0;

            // 获取所有活跃敌人
            var activeEnemyIds = store.GetAllActiveEnemyIds();

            foreach (int enemyId in activeEnemyIds)
            {
                if (enemyId == playerId) continue;

                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];
                float enemyHealth = store.GetEnemyHealth(enemyId);

                if (enemyHealth <= 0f)
                    continue;

                // 检查敌人是否在 3x3 范围内
                // 3x3 范围：从 (playerX -1, playerY -1) 到 (playerX + 1, playerY + 1)
                if (enemyX >= playerX - 1f && enemyX <= playerX + 1f &&
                    enemyY >= playerY - 1f && enemyY <= playerY + 1f)
                {
                    // 计算攻击距离
                    float distance = System.Math.Abs(enemyX - playerX);
                    if (distance <= range)
                    {
                        // 造成伤害
                        enemyHealth = System.Math.Max(0f, enemyHealth - finalDamage);
                        store.SetEnemyHealth(enemyId, enemyHealth);
                        enemiesHit++;

                        renderer.Log($"[SKILL] Mega Explosion hit enemy {enemyId} at ({enemyX:F0}, {enemyY:F0}), damage: {finalDamage:F1}");

                        if (enemyHealth <= 0f)
                        {
                            // 击杀敌人，奖励金币
                            int goldReward = store.GetEnemyGoldReward(enemyId);
                            float currentGold = store.GetPlayerGold(playerId);
                            store.SetPlayerGold(playerId, currentGold + goldReward);

                            renderer.Log($"[SKILL] Mega Explosion killed enemy {enemyId}, gained {goldReward} gold");
                        }
                    }
                }
            }

            // 设置冷却
            float cooldown = store.GetSkillCooldown(playerId);
            store.SetSkillCurrentCooldown(playerId, cooldown);

            renderer.Log($"[SKILL] Mega Explosion cast! Hit {enemiesHit} enemies, cooldown: {cooldown}s");
        }

        /// <summary>
        /// 释放攻击距离 9 的单体技能
        /// </summary>
        private void CastSniperShot(float baseDamage, float playerX, float playerY)
        {
            float damageMultiplier = 4f;  // 400%
            float finalDamage = baseDamage * damageMultiplier;
            int range = 9;

            // 单体技能：只攻击距离最近的单个敌人
            float closestDistance = float.MaxValue;
            int closestEnemyId = -1;

            // 获取所有活跃敌人
            var activeEnemyIds = store.GetAllActiveEnemyIds();

            foreach (int enemyId in activeEnemyIds)
            {
                if (enemyId == playerId) continue;

                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];
                float enemyHealth = store.GetEnemyHealth(enemyId);

                if (enemyHealth <= 0f)
                    continue;

                // 计算攻击距离（Y 轴向距离优先）
                float distance = System.Math.Abs(enemyX - playerX) * 2f + (playerY - enemyY);
                if (distance < closestDistance && distance <= range)
                {
                    closestDistance = distance;
                    closestEnemyId = enemyId;
                }
            }

            // 攻击最近的敌人
            if (closestEnemyId != -1)
            {
                float enemyX = store.PositionX[closestEnemyId];
                float enemyY = store.PositionY[closestEnemyId];
                float enemyHealth = store.GetEnemyHealth(closestEnemyId);

                // 造成伤害
                enemyHealth = System.Math.Max(0f, enemyHealth - finalDamage);
                store.SetEnemyHealth(closestEnemyId, enemyHealth);

                renderer.Log($"[SKILL] Sniper Shot hit enemy {closestEnemyId} at ({enemyX:F0}, {enemyY:F0}), damage: {finalDamage:F1}, range: {range}");

                if (enemyHealth <= 0f)
                {
                    // 击杀敌人，奖励金币
                    int goldReward = store.GetEnemyGoldReward(closestEnemyId);
                    float currentGold = store.GetPlayerGold(playerId);
                    store.SetPlayerGold(playerId, currentGold + goldReward);

                    renderer.Log($"[SKILL] Sniper Shot killed enemy {closestEnemyId}, gained {goldReward} gold");
                }
            }

            // 设置冷却
            float cooldown = store.GetSkillCooldown(playerId);
            store.SetSkillCurrentCooldown(playerId, cooldown);

            if (closestEnemyId != -1)
            {
                renderer.Log($"[SKILL] Sniper Shot cast! Hit 1 enemy, cooldown: {cooldown}s");
            }
            else
            {
                renderer.Log($"[SKILL] Sniper Shot cast! No enemies in range, cooldown: {cooldown}s");
            }
        }

        /// <summary>
        /// 自动释放技能（根据冷却时间）
        /// </summary>
        public void AutoCastSkill()
        {
            float currentCooldown = store.GetSkillCurrentCooldown(playerId);
            if (currentCooldown <= 0f)
            {
                // 自动释放冷却时间最短的技能
                CastSkill(skillCrossSlash);
            }
        }
    }
}
