using System;

namespace BattleSystemECS.Components
{
    /// <summary>
    /// 技能组件
    /// </summary>
    public class SkillComponent
    {
        public string SkillName { get; set; }
        public float Damage { get; set; }
        public float Cooldown { get; set; }
        public float CurrentCooldown { get; set; }
        public int ManaCost { get; set; }
        public int CurrentMana { get; set; }
        public int MaxMana { get; set; }

        public SkillComponent(string skillName, float damage, float cooldown, int manaCost, int maxMana)
        {
            SkillName = skillName;
            Damage = damage;
            Cooldown = cooldown;
            CurrentCooldown = 0f;
            ManaCost = manaCost;
            CurrentMana = maxMana;
            MaxMana = maxMana;
        }

        public bool CanUseSkill()
        {
            return CurrentCooldown <= 0f && CurrentMana >= ManaCost;
        }

        public void UseSkill()
        {
            CurrentMana -= ManaCost;
            CurrentCooldown = Cooldown;
        }

        public void UpdateCooldown(float deltaTime)
        {
            if (CurrentCooldown > 0f)
            {
                CurrentCooldown = Math.Max(0f, CurrentCooldown - deltaTime);
            }
        }

        public void RegenerateMana(float deltaTime, float regenRate)
        {
            CurrentMana = (int)Math.Min(MaxMana, CurrentMana + regenRate * deltaTime);
        }
    }
}
