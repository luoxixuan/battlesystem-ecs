#nullable enable
namespace BattleSystemECS.Core
{
    /// <summary>Enemy AI, abilities, burrow, necromancer, life link, affixes, mana burn, fear, zone control.</summary>
    public class AIGroup : ISystemGroup
    {
        public Systems.EnemyAISystem? EnemyAI { get; set; }
        public Systems.EnemyAbilitySystem? EnemyAbility { get; set; }
        public Systems.EnemyBurrowSystem? Burrow { get; set; }
        public Systems.NecromancerSystem? Necromancer { get; set; }
        public Systems.EnemyLifeLinkSystem? LifeLink { get; set; }
        public Systems.EnemyAffixSystem? EnemyAffix { get; set; }
        public Systems.ManaBurnSystem? ManaBurn { get; set; }
        public Systems.EnemyLifestealSystem? Lifesteal { get; set; }
        public Systems.PhaseSystem? Phase { get; set; }
        public Systems.FearSystem? Fear { get; set; }
        public Systems.ZoneControlSystem? ZoneControl { get; set; }

        public void Execute(ComponentStore store, float deltaTime, int turn)
        {
            // Zone control (CC zones: Slow/Stun/Freeze/Root) — runs before AI so CC is applied this turn
            ZoneControl?.Update(deltaTime);

            EnemyAI?.SetTurn(turn, deltaTime);
            EnemyAI?.Update();

            EnemyAbility?.SetTurn(turn);
            EnemyAbility?.UpdateCooldowns(deltaTime);
            EnemyAbility?.ExecuteAbilities();
            EnemyAbility?.Update();

            Burrow?.SetTurn(turn);
            Burrow?.Update();
            Burrow?.ApplyBurrowEffects();

            Necromancer?.SetTurn(turn, turn);
            Necromancer?.Update(deltaTime);

            LifeLink?.SetTurn(turn);
            LifeLink?.Update();
            LifeLink?.DecrementCooldowns(deltaTime);

            EnemyAffix?.Update(deltaTime);

            ManaBurn?.SetTurn(turn);
            ManaBurn?.Update();

            Lifesteal?.SetTurn(turn);
            Lifesteal?.Update();

            Phase?.SetTurn(turn);
            Phase?.Update();

            Fear?.SetTurn(turn);
            Fear?.Update(deltaTime);
        }
    }
}
