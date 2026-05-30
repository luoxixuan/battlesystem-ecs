#nullable enable
namespace BattleSystemECS.Core
{
    /// <summary>Enemy movement, pathfinding, wound, path modifiers, healer, summons, steal gold.</summary>
    public class MovementGroup : ISystemGroup
    {
        public Systems.EnemyWoundSystem? Wound { get; set; }
        public Systems.PathfindingSystem? Pathfinding { get; set; }
        public Systems.EnemyMovementSystem? EnemyMovement { get; set; }
        public Systems.PathModifierSystem? PathModifier { get; set; }
        public Systems.EnemyHealerSystem? EnemyHealer { get; set; }
        public Systems.EnemyStealGoldSystem? StealGold { get; set; }
        public Systems.PlayerSummonSystem? Summon { get; set; }

        public void Execute(ComponentStore store, float deltaTime, int turn)
        {
            Wound?.SetTurn(turn);
            Wound?.Update();
            Pathfinding?.SetTurn(turn);
            EnemyMovement?.SetTurn(turn);
            EnemyMovement?.Update();

            PathModifier?.SetTurn();
            PathModifier?.Update(deltaTime);

            EnemyHealer?.SetTurn(turn);
            EnemyHealer?.Update(deltaTime);

            StealGold?.Update();

            Summon?.SetTurn(turn);
            Summon?.Update(deltaTime);
        }
    }
}
