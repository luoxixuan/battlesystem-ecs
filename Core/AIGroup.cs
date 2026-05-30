#nullable enable
namespace BattleSystemECS.Core
{
    /// <summary>Enemy AI, abilities, burrow, necromancer, life link, affixes.</summary>
    public class AIGroup : ISystemGroup
    {
        public Systems.EnemyAISystem? EnemyAI { get; set; }
        public Systems.EnemyAbilitySystem? EnemyAbility { get; set; }
        public Systems.EnemyBurrowSystem? Burrow { get; set; }
        public Systems.NecromancerSystem? Necromancer { get; set; }
        public Systems.EnemyLifeLinkSystem? LifeLink { get; set; }
        public Systems.EnemyAffixSystem? EnemyAffix { get; set; }

        public void Execute(ComponentStore store, float deltaTime, int turn)
        {
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
        }
    }
}
