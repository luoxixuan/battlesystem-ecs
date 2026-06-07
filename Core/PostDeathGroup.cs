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
        // Round 110 Direction 10 — DoomClock objective helper. Lives in PostDeath
        // because the countdown runs alongside objective state and is not
        // combat-critical (zero cost when inactive). It also advances the wave
        // cleared counter and the cycle index when a wave completes.
        public Systems.DoomClockSystem? DoomClock { get; set; }

        // Round 196 Direction 3 — Soul Harvest per-frame regen tick. Lives in
        // PostDeath (alongside DoomClock) so the regen cadence matches combo /
        // objective bookkeeping. Sentinel-gated fast path: zero overhead when
        // every player has PlayerSoulRegen == 0. OnEnemyKilled credit is
        // event-driven (synchronous in ResolveEnemiesKilledThisFrame) so kills
        // are visible to the next Update's regen check.
        public Systems.SoulHarvestSystem? SoulHarvest { get; set; }

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
            // DoomClock countdown runs in PostDeath so it ticks on the same cadence
            // as the objective score / wave completion bookkeeping. The timer
            // short-circuits to 0 in Update when the run ends.
            DoomClock?.Update(deltaTime, Phase);

            // Round 196 Direction 3 — Soul Harvest per-frame regen tick. Sentinels
            // short-circuit on PlayerSoulRegen == 0, so cost is O(MAX_PLAYERS) with
            // a single float compare per slot when no player has regen configured.
            SoulHarvest?.Update(deltaTime);

            // Wave branch: pause combat if branch selection is active
            if (WaveBranch?.IsBranchActive == true)
                return;
        }
    }
}
