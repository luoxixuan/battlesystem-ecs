#nullable enable
namespace BattleSystemECS.Core
{
    /// <summary>Post-death resolution: fission, life link penalties, objective, resources, income, corpses, combo.</summary>
    public class PostDeathGroup : ISystemGroup
    {
        public Systems.EnemyFissionSystem? EnemyFission { get; set; }
        public Systems.EnemyLifeLinkSystem? LifeLink { get; set; }
        public Systems.ObjectiveSystem? Objective { get; set; }
        public Systems.ResourceNodeSystem? ResourceNode { get; set; }
        public Systems.TowerIncomeSystem? TowerIncome { get; set; }
        public Systems.CorpseEffectSystem? CorpseEffect { get; set; }
        public Systems.WaveBranchSystem? WaveBranch { get; set; }
        public Systems.ComboSystem? Combo { get; set; }

        /// <summary>Current game phase, set by FrameScheduler before Execute.</summary>
        public GameState Phase { get; set; } = GameState.WavePhase;

        public void Execute(ComponentStore store, float deltaTime, int turn)
        {
            EnemyFission?.Update();
            LifeLink?.ResolveBreakPenalties();
            Objective?.Update(deltaTime, Phase);
            ResourceNode?.Update(deltaTime, Phase);
            TowerIncome?.Update(deltaTime);
            CorpseEffect?.Update(deltaTime);
            Combo?.Update(deltaTime);

            // Wave branch: pause combat if branch selection is active
            if (WaveBranch?.IsBranchActive == true)
                return;
        }
    }
}
