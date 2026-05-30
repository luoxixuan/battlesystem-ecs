#nullable enable
using System;
using BattleSystemECS.Config;
using BattleSystemECS.Components;

namespace BattleSystemECS.Core
{
    /// <summary>
    /// Unified frame scheduler — all frame paths (GameManager / Benchmark / Tests) go through here.
    /// 
    /// System groups encapsulate related systems and their execution order.
    /// Adding a new system: add it to the appropriate group, not FrameScheduler.
    /// </summary>
    public class FrameScheduler
    {
        private readonly ComponentStore store;

        public GameState Phase { get; set; } = GameState.WavePhase;

        // ── System groups — one per logical phase ──
        public BuildGroup          Build          { get; } = new();
        public PreGameGroup        PreGame        { get; } = new();
        public SpawningGroup       Spawning       { get; } = new();
        public AIGroup             AI             { get; } = new();
        public MovementGroup       Movement       { get; } = new();
        public TerrainGroup        Terrain        { get; } = new();
        public CombatSetupGroup    CombatSetup    { get; } = new();
        public SpatialGroup        Spatial        { get; } = new();
        public CombatGroup         Combat         { get; } = new();
        public SkillBuffGroup      SkillBuff      { get; } = new();
        public PostDeathGroup      PostDeath      { get; } = new();

        public event Action<int, int>? OnEnemyKilled;

        public FrameScheduler(ComponentStore store, GameConfig gameConfig)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            _ = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
        }

        /// <summary>
        /// Execute one full frame of systems, gated by current Phase.
        /// BuildPhase: economy/UI-only systems. WavePhase/Intermission: full combat pipeline.
        /// </summary>
        public void Tick(float deltaTime, int turn)
        {
            store.BeginFrame();
            store.SetTurnCCFlags();

            UpdateTimeScale(ref deltaTime);

            if (Phase == GameState.BuildPhase)
            {
                Build.Execute(store, deltaTime);
                return;
            }

            RunWavePhase(deltaTime, turn);
        }

        /// <summary>
        /// Full game turn with post-tick game logic, matching GameManager.Run() behavior.
        /// </summary>
        public void TickGameTurn(float deltaTime, int turn)
        {
            Tick(deltaTime, turn);
        }

        // ─── Private helpers ───────────────────────────────────────────────

        private void UpdateTimeScale(ref float deltaTime)
        {
            if (store.GlobalTimeScaleDuration[0] > 0f)
            {
                store.GlobalTimeScaleDuration[0] -= 1f;
                if (store.GlobalTimeScaleDuration[0] <= 0f)
                {
                    store.GlobalTimeScaleDuration[0] = 0f;
                    store.GlobalTimeScale[0] = 1f;
                }
            }
            deltaTime *= store.GlobalTimeScale[0];
        }

        /// <summary>
        /// WavePhase pipeline — 13 logical phases executed in strict order.
        /// Each phase is encapsulated in a SystemGroup — add/remove systems inside the group, not here.
        /// </summary>
        private void RunWavePhase(float deltaTime, int turn)
        {
            // Phase 0: Sync PostDeath phase
            PostDeath.Phase = Phase;

            // Phase 1: Pre-game (weather, day/night, difficulty, events)
            PreGame.Execute(store, deltaTime, turn);

            // Phase 2: Spawning (waves, nests)
            Spawning.Execute(store, deltaTime, turn);

            // Phase 3: AI (behavior trees, abilities, burrow, necromancer, life link, affixes)
            AI.Execute(store, deltaTime, turn);

            // Phase 4: Movement (wound, pathfinding, modifiers, healer, summons)
            Movement.Execute(store, deltaTime, turn);

            // Phase 5: Terrain + Mutators + Morph
            Terrain.Execute(store, deltaTime, turn);

            // Phase 6: Pre-combat setup (SetTurn on all combat systems)
            CombatSetup.Execute(store, deltaTime, turn);

            // Phase 7: Spatial Grid rebuild + patrol/chrono/fog/telegraph
            Spatial.Execute(store, deltaTime, turn);

            // Phase 8: Main combat (attacks, synergy, auras, projectiles)
            Combat.Execute(store, deltaTime, turn);

            // Phase 9: Skill resolution + Buff DoT + Bleed
            SkillBuff.Execute(store, deltaTime, turn);

            // Phase 10: Death resolve
            store.ResolveEnemiesKilledThisFrame();

            // Phase 11: Post-death (fission, life link, objective, resources, corpses, combo)
            PostDeath.Execute(store, deltaTime, turn);
        }
    }
}
