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

            // Phase 12: Threat Score EMA update (Round 99 Direction 5)
            // O(MAX_PLAYERS) per-tick: decay the running average using an exponential moving
            // average with half-life ThreatScoreConfig.DPSWindowSec, then add this frame's
            // PlayerDPSAccumulator (which is reset to 0 below). Hot-path cost: ~10 float ops.
            DecayAndAccumulateThreatScore(combatDt);
        }

        /// <summary>
        /// Per-tick decay of <c>PlayerRecentDPS</c> using an exponential moving average.
        /// Single-player game uses index 0; loop covers all MAX_PLAYERS for future multi-player.
        /// decayFactor is computed from <c>DPSWindowSec</c> and the actual tick deltaTime so
        /// the half-life is in **seconds** (not frames), keeping the metric frame-rate independent.
        ///
        /// IMPORTANT: PlayerDPSAccumulator stores raw damage per-frame, not DPS. We divide by
        /// deltaTime to convert it to actual damage-per-second before blending into the EMA.
        /// Without this, the rate of accumulation would scale with FPS and the threat metric
        /// would be tied to the framerate instead of the player's actual damage output.
        /// </summary>
        private void DecayAndAccumulateThreatScore(float deltaTime)
        {
            // Decay factor per tick: alpha = 1 - exp(-ln(2) * dt / halfLife)
            // At dt = 1/60 and halfLife = 5s: alpha ≈ 0.00231, so 99% decay in ~33s.
            // This is the standard EMA half-life formulation — independent of frame rate.
            float halfLife = ThreatScoreConfig.DPSWindowSec;
            float alpha = 1f - MathF.Exp(-0.6931472f * deltaTime / halfLife);
            // Guard against dt=0 (BuildPhase or paused tick) — skip blending, keep last value.
            float invDt = deltaTime > 0f ? 1f / deltaTime : 0f;
            int playerCount = store.PlayerRecentDPS.Length; // uses MAX_PLAYERS, not a hardcoded literal
            for (int p = 0; p < playerCount; p++)
            {
                float decayed = store.PlayerRecentDPS[p] * (1f - alpha);
                // Convert per-frame accumulator to per-second DPS for frame-rate independence.
                float added = store.PlayerDPSAccumulator[p] * invDt * alpha;
                store.PlayerRecentDPS[p] = decayed + added;
                store.PlayerDPSAccumulator[p] = 0f;
            }
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
