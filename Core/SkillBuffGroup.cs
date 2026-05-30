#nullable enable
namespace BattleSystemECS.Core
{
    /// <summary>Skill resolution, buff DoT, bleed damage.</summary>
    public class SkillBuffGroup : ISystemGroup
    {
        public Systems.BuffSystem? Buff { get; set; }
        public Systems.SkillSystem? Skill { get; set; }
        public Systems.BleedSystem? Bleed { get; set; }

        public void Execute(ComponentStore store, float deltaTime, int turn)
        {
            Buff?.Update(deltaTime);
            Skill?.ResolveSkillDamage();
            Buff?.ResolveDotDamage();
            Bleed?.Update(deltaTime);
            Bleed?.ResolveBleedDamage();
            Skill?.Update(deltaTime);
        }
    }
}
