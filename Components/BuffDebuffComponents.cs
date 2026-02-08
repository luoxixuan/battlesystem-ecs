using System;

namespace BattleSystemECS.Components
{
    /// <summary>
    /// Buff 组件
    /// </summary>
    public class BuffComponent
    {
        public string BuffName { get; set; }
        public float Duration { get; set; }
        public float RemainingDuration { get; set; }
        public float AttackBonus { get; set; }
        public float DefenseBonus { get; set; }
        public float HealAmount { get; set; }

        public BuffComponent(string buffName, float duration, float attackBonus = 0f, float defenseBonus = 0f, float healAmount = 0f)
        {
            BuffName = buffName;
            Duration = duration;
            RemainingDuration = duration;
            AttackBonus = attackBonus;
            DefenseBonus = defenseBonus;
            HealAmount = healAmount;
        }

        public bool IsActive()
        {
            return RemainingDuration > 0f;
        }

        public void UpdateDuration(float deltaTime)
        {
            if (RemainingDuration > 0f)
            {
                RemainingDuration = Math.Max(0f, RemainingDuration - deltaTime);
            }
        }

        public void ApplyEffect(HealthComponent health, AttackPowerComponent attack, DefensePowerComponent defense)
        {
            // 治疗效果
            if (HealAmount > 0f && health != null)
            {
                health.Current = Math.Min(health.Max, health.Current + HealAmount);
            }

            // 攻击力加成
            if (AttackBonus > 0f && attack != null)
            {
                attack.Value += AttackBonus;
            }

            // 防御力加成
            if (DefenseBonus > 0f && defense != null)
            {
                defense.Value += DefenseBonus;
            }
        }

        public void RemoveEffect(HealthComponent health, AttackPowerComponent attack, DefensePowerComponent defense)
        {
            // 移除攻击力加成
            if (AttackBonus > 0f && attack != null)
            {
                attack.Value = Math.Max(0f, attack.Value - AttackBonus);
            }

            // 移除防御力加成
            if (DefenseBonus > 0f && defense != null)
            {
                defense.Value = Math.Max(0f, defense.Value - DefenseBonus);
            }
        }
    }

    /// <summary>
    /// Debuff 组件
    /// </summary>
    public class DebuffComponent
    {
        public string DebuffName { get; set; }
        public float Duration { get; set; }
        public float RemainingDuration { get; set; }
        public float AttackPenalty { get; set; }
        public float DefensePenalty { get; set; }
        public float DamageOverTime { get; set; }

        public DebuffComponent(string debuffName, float duration, float attackPenalty = 0f, float defensePenalty = 0f, float damageOverTime = 0f)
        {
            DebuffName = debuffName;
            Duration = duration;
            RemainingDuration = duration;
            AttackPenalty = attackPenalty;
            DefensePenalty = defensePenalty;
            DamageOverTime = damageOverTime;
        }

        public bool IsActive()
        {
            return RemainingDuration > 0f;
        }

        public void UpdateDuration(float deltaTime)
        {
            if (RemainingDuration > 0f)
            {
                RemainingDuration = Math.Max(0f, RemainingDuration - deltaTime);
            }
        }

        public void ApplyEffect(HealthComponent health, AttackPowerComponent attack, DefensePowerComponent defense)
        {
            // 攻击力惩罚
            if (AttackPenalty > 0f && attack != null)
            {
                attack.Value = Math.Max(0f, attack.Value - AttackPenalty);
            }

            // 防御力惩罚
            if (DefensePenalty > 0f && defense != null)
            {
                defense.Value = Math.Max(0f, defense.Value - DefensePenalty);
            }
        }

        public void ApplyDamageOverTime(HealthComponent health, float deltaTime)
        {
            if (DamageOverTime > 0f && health != null)
            {
                // 每秒造成伤害
                float damage = DamageOverTime * deltaTime;
                health.Current = Math.Max(0f, health.Current - damage);

                if (health.Current <= 0f)
                {
                    // 死亡时立即移除 Debuff
                    RemainingDuration = 0f;
                }
            }
        }

        public void RemoveEffect(HealthComponent health, AttackPowerComponent attack, DefensePowerComponent defense)
        {
            // 恢复攻击力惩罚
            if (AttackPenalty > 0f && attack != null)
            {
                attack.Value += AttackPenalty;
            }

            // 恢复防御力惩罚
            if (DefensePenalty > 0f && defense != null)
            {
                defense.Value += DefensePenalty;
            }
        }
    }
}
