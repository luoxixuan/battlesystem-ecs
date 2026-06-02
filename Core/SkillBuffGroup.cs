#nullable enable
namespace BattleSystemECS.Core
{
    /// <summary>Skill resolution, buff DoT, bleed damage.</summary>
    public class SkillBuffGroup : ISystemGroup
    {
        public Systems.BuffSystem? Buff { get; set; }
        public Systems.SkillSystem? Skill { get; set; }
        public Systems.BleedSystem? Bleed { get; set; }
        public Systems.HealingZoneSystem? HealingZone { get; set; }
        // Wisp aura pets — runs after HealingZone so wisp heal/slow/curse are layered
        // on top of any healing-zone heals in the same frame.
        public Systems.WispSystem? Wisp { get; set; }

        public void Execute(ComponentStore store, float deltaTime, int turn)
        {
            Buff?.Update(deltaTime);
            Skill?.ResolveSkillDamage();
            Buff?.ResolveDotDamage();
            Bleed?.Update(deltaTime);
            Bleed?.ResolveBleedDamage();
            HealingZone?.Update(deltaTime);
            Skill?.Update(deltaTime);
            Wisp?.Update(deltaTime);
        }
    }
}
