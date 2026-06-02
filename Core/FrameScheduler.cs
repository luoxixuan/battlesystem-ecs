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
        /// 
        /// Bullet-time dt split (direction 10): when PlayerBulletTimeTurnsLeft[0] > 0, "enemyDt" (used by
        /// PreGame/Spawning/AI/Movement/Terrain/CombatSetup/Spatial) is scaled down by PlayerBulletTimeScale[0],
        /// while "combatDt" (used by Combat/SkillBuff/PostDeath) stays at full speed. This makes enemies + their
        /// movement + projectiles crawl while the player's tower/attack systems continue at normal rate — the
        /// classic "tactical pause" effect. Inactive (turns <= 0) → both dts equal the input dt (zero overhead).
        /// </summary>
        private void RunWavePhase(float deltaTime, int turn)
        {
            // Phase 0: Sync PostDeath phase
            PostDeath.Phase = Phase;

            // Phase 0.5: Bullet-time dt split (only active when turns > 0; otherwise enemyDt == combatDt)
            SplitDeltaForBulletTime(deltaTime, out float enemyDt, out float combatDt);

            // Phase 1: Pre-game (weather, day/night, difficulty, events) — ENEMY side
            PreGame.Execute(store, enemyDt, turn);

            // Phase 2: Spawning (waves, nests) — ENEMY side
            Spawning.Execute(store, enemyDt, turn);

            // Phase 3: AI (behavior trees, abilities, burrow, necromancer, life link, affixes) — ENEMY side
            AI.Execute(store, enemyDt, turn);

            // Phase 4: Movement (wound, pathfinding, modifiers, healer, summons) — ENEMY side
            Movement.Execute(store, enemyDt, turn);

            // Phase 5: Terrain + Mutators + Morph — ENEMY side
            Terrain.Execute(store, enemyDt, turn);

            // Phase 6: Pre-combat setup (SetTurn on all combat systems) — ENEMY side
            CombatSetup.Execute(store, enemyDt, turn);

            // Phase 7: Spatial Grid rebuild + patrol/chrono/fog/telegraph — ENEMY side
            Spatial.Execute(store, enemyDt, turn);

            // Phase 8: Main combat (attacks, synergy, auras, projectiles) — COMBAT side (full speed)
            Combat.Execute(store, combatDt, turn);

            // Phase 9: Skill resolution + Buff DoT + Bleed — COMBAT side (full speed)
            SkillBuff.Execute(store, combatDt, turn);

            // Phase 10: Death resolve (uses queued damage, dt-free)
            store.ResolveEnemiesKilledThisFrame();

            // Phase 11: Post-death (fission, life link, objective, resources, corpses, combo) — COMBAT side
            PostDeath.Execute(store, combatDt, turn);
        }

        /// <summary>
        /// Bullet-time dt split (direction 10). When PlayerBulletTimeTurnsLeft[0] > 0, the enemy-side
        /// dt is scaled by PlayerBulletTimeScale[0] (e.g. 0.3 = 30% speed). Decrement the counter at the
        /// start of each tick so a 3-turn bullet-time covers exactly 3 ticks.
        /// When inactive (turns <= 0), both outputs equal the input dt — zero overhead path.
        /// </summary>
        private void SplitDeltaForBulletTime(float inputDt, out float enemyDt, out float combatDt)
        {
            if (store.PlayerBulletTimeTurnsLeft[0] > 0f)
            {
                // Read the scale FIRST — we need it for this tick's enemyDt even on the final tick.
                float scale = store.PlayerBulletTimeScale[0];
                // Decrement counter; reset state to defaults on final tick (1→0) so the *next* tick is a no-op.
                store.PlayerBulletTimeTurnsLeft[0] -= 1f;
                if (store.PlayerBulletTimeTurnsLeft[0] <= 0f)
                {
                    store.PlayerBulletTimeTurnsLeft[0] = 0f;
                    store.PlayerBulletTimeScale[0] = 1f;  // reset to no-op default (post-tick)
                }
                enemyDt = inputDt * scale;
                combatDt = inputDt;
            }
            else
            {
                enemyDt = inputDt;
                combatDt = inputDt;
            }
        }
    }
}
