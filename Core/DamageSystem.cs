using System;
using BattleSystemECS.Components;
using BattleSystemECS.Core;

namespace BattleSystemECS.Core
{
    /// <summary>
    /// 伤害系统 - ECS 架构，逻辑核心
    /// </summary>
    public class DamageSystem
    {
        private EntityManager entityManager;
        private Random random;

        public DamageSystem(EntityManager entityManager)
        {
            this.entityManager = entityManager;
            this.random = new Random();
        }

        /// <summary>
        /// 计算伤害
        /// </summary>
        public float CalculateDamage(int attackerId, int defenderId)
        {
            var attackPower = entityManager.GetComponent<AttackPowerComponent>(attackerId);
            var defensePower = entityManager.GetComponent<DefensePowerComponent>(defenderId);

            if (attackPower == null || defensePower == null)
                return 0f;

            // 伤害公式：攻击力 - 防御力 * 0.5，最少 1 点伤害
            float damage = Math.Max(1f, attackPower.Value - defensePower.Value * 0.5f);

            // 暴击几率 20%
            bool isCritical = CheckCritical();
            if (isCritical)
            {
                damage *= 1.5f; // 暴击 1.5 倍伤害
            }

            return damage;
        }

        /// <summary>
        /// 检查是否暴击
        /// </summary>
        public bool CheckCritical()
        {
            // 20% 暴击几率
            return random.NextDouble() < 0.2;
        }
    }
}
