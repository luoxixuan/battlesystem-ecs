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
        public Systems.EnemyStrafeSystem? EnemyStrafe { get; set; }
        public Systems.ReflectTowerSystem? ReflectTower { get; set; }

        public void Execute(ComponentStore store, float deltaTime, int turn)
        {
            // Zone control (CC zones: Slow/Stun/Freeze/Root) — runs before AI so CC is applied this turn
            ZoneControl?.Update(deltaTime);

            // Enemy strafe/dodge: decrement timers and cooldowns before AI evaluates
            EnemyStrafe?.SetTurn();
            EnemyStrafe?.Update();

            EnemyAI?.SetTurn(turn, deltaTime);
            EnemyAI?.Update();

            EnemyAbility?.SetTurn(turn);
            EnemyAbility?.UpdateCooldowns(deltaTime);
            EnemyAbility?.ExecuteAbilities();
            // Tick cast timers right after ExecuteAbilities so any newly-started casts in this
            // frame (via EnqueueAbility) are visible to Movement and TowerAttack in the same
            // frame. Casts that resolve this frame will be enqueued above and executed next
            // turn (we don't re-enter ExecuteAbilities to keep the frame's resolve order stable).
            EnemyAbility?.TickCastTimers();
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
