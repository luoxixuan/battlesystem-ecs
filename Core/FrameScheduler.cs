#nullable enable
using System;
using System.Collections.Generic;
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
        private readonly IBattleEventBus _eventBus;

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

        // Round 182 Direction 6 — PathfindingSystem reference (optional). Set via property;
        // required by TickBlinkerCycle to validate path waypoint count before advancing
        // the node index. Injected lazily so construction order doesn't matter.
        private Systems.PathfindingSystem? _pathfinding;

        public FrameScheduler(ComponentStore store, GameConfig gameConfig, IBattleEventBus? eventBus = null)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            _ = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
            _eventBus = eventBus ?? NullEventBus.Instance;
            store.OnEnemyKilled += (enemyId, killerId) =>
            {
                _eventBus.OnEntityKilled(enemyId, killerId);
                _eventBus.OnEntityDestroyed(enemyId);
            };
        }

        /// <summary>
        /// Round 182 Direction 6 — Inject the PathfindingSystem so the Blink-Dash cycle
        /// ticker can look up waypoint counts before advancing node indices. Optional:
        /// TickBlinkerCycle falls back to a no-advance behavior when pathfinding is null
        /// (the timer still ticks and i-frames still decrement, but no teleport happens).
        /// </summary>
        public void SetPathfindingSystem(Systems.PathfindingSystem pathfinding)
        {
            _pathfinding = pathfinding;
        }

        /// <summary>
        /// Execute one full frame of systems, gated by current Phase.
        /// BuildPhase: economy/UI-only systems. WavePhase/Intermission: full combat pipeline.
        /// </summary>
        public void Tick(float deltaTime, int turn)
        {
            store.BeginFrame();
            // Attribute modifiers become visible at the scheduler's aggregate boundary.
            store.AttributeAggregator.AggregateDirty();
            store.SetTurnCCFlags();

            // ── I-frames countdown (Round 118) ───────────────────────────────────
            // Decrement EnemyInvulnFramesLeft for every active enemy. Runs once per tick
            // (both BuildPhase and WavePhase) at the top of Tick so the countdown is
            // frame-rate independent and uniform regardless of phase. Hits floor at 0
            // (no negative). O(MAX_ENEMIES) per tick but cheap (1 int cmp/sub per slot).
            DecrementInvulnFramesLeft();

            // ── Phaser cycle ticker (Round 181 Direction 9) ──────────────────────
            // Advances each phaser's phase→vulnerable→phase state machine. Runs once
            // per tick (both BuildPhase and WavePhase) right after the I-frames
            // countdown so the phase windows stay frame-rate independent. Phasers
            // remain ticking even during BuildPhase so the visual phase state and
            // damage immunity stay continuous across phases.
            TickPhaserCycle(deltaTime);

            // ── Blinker cycle ticker (Round 182 Direction 6) ─────────────────────
            // Advances each blinker's "between blinks" timer; when the timer reaches
            // EnemyBlinkInterval, snap the enemy forward along its current path by
            // EnemyBlinkDistance tiles and grant 0.2s of i-frames. Decrement
            // EnemyBlinkIFramesLeft each frame so towers can re-target after the brief
            // i-frame window expires. Sentinel-gated on EnemyIsBlinker (non-blinkers
            // pay zero overhead).
            TickBlinkerCycle(deltaTime);

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

            // ── Emit position events after movement ──
            EmitPositionEvents();

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

        /// <summary>
        /// Round 118 — Post-Hit Invulnerability (I-frames) countdown.
        /// Walks ActiveEnemyIds (not the full MAX_ENTITIES array) and decrements
        /// EnemyInvulnFramesLeft for each. The write is safe here because we run serially
        /// at the top of Tick(), before any Parallel.For in combat systems. When the counter
        /// hits 0 it stays at 0 (no negative clamp needed since the check in TowerAttackSystem
        /// is "> 0" not "!= 0"). Inactive enemies (counter already 0) are a no-op branch.
        /// </summary>
        private void DecrementInvulnFramesLeft()
        {
            var activeEnemies = store.ActiveEnemyIds;
            for (int i = 0; i < activeEnemies.Count; i++)
            {
                int eid = activeEnemies[i];
                if (store.EnemyInvulnFramesLeft[eid] > 0)
                {
                    store.EnemyInvulnFramesLeft[eid]--;
                }
            }
        }

        /// <summary>
        /// Round 181 Direction 9 — Phase-Through enemy cycle ticker. Advances each
        /// phaser's per-frame state machine:
        ///   - If currently in phase (EnemyPhaserPhaseActive=true): decrement
        ///     EnemyPhaserDurationLeft by deltaTime; when it hits ≤ 0, clear the phase
        ///     flag and reset the cycle timer to start counting toward the next phase.
        ///   - If currently vulnerable (EnemyPhaserPhaseActive=false): increment
        ///     EnemyPhaserCycleTimer by deltaTime; when it reaches EnemyPhaserInterval,
        ///     enter the phase state with EnemyPhaserDurationLeft = EnemyPhaserPhaseDuration.
        /// Sentinel-gated: only EnemyIsPhaser==true enemies pay the cycle work; the
        /// hot path fast-returns on the first bool read. O(activeEnemies) per tick,
        /// cheap (1 bool + 1-2 float ops per slot).
        /// </summary>
        private void TickPhaserCycle(float deltaTime)
        {
            if (deltaTime <= 0f) return;
            var activeEnemies = store.ActiveEnemyIds;
            for (int i = 0; i < activeEnemies.Count; i++)
            {
                int eid = activeEnemies[i];
                if (!store.EnemyIsPhaser[eid]) continue;
                if (store.EnemyPhaserPhaseActive[eid])
                {
                    float dur = store.EnemyPhaserDurationLeft[eid] - deltaTime;
                    if (dur <= 0f)
                    {
                        // Phase window expired → re-enter vulnerable gap
                        store.EnemyPhaserPhaseActive[eid] = false;
                        store.EnemyPhaserDurationLeft[eid] = 0f;
                        store.EnemyPhaserCycleTimer[eid] = 0f;
                    }
                    else
                    {
                        store.EnemyPhaserDurationLeft[eid] = dur;
                    }
                }
                else
                {
                    // In vulnerable gap — count up toward next phase trigger
                    float t = store.EnemyPhaserCycleTimer[eid] + deltaTime;
                    float interval = store.EnemyPhaserInterval[eid];
                    if (interval > 0f && t >= interval)
                    {
                        // Trigger next phase
                        store.EnemyPhaserPhaseActive[eid] = true;
                        store.EnemyPhaserDurationLeft[eid] = store.EnemyPhaserPhaseDuration[eid];
                        store.EnemyPhaserCycleTimer[eid] = 0f;
                    }
                    else
                    {
                        store.EnemyPhaserCycleTimer[eid] = t;
                    }
                }
            }
        }

        /// <summary>
        /// Round 182 Direction 6 — Blink-Dash cycle ticker. Advances each blinker's
        /// per-frame state machine:
        ///   - Decrement EnemyBlinkIFramesLeft (active i-frame window decays each frame)
        ///   - Increment EnemyBlinkTimer; when it reaches EnemyBlinkInterval:
        ///       * Advance EnemyPathNodeIndex by ceil(EnemyBlinkDistance) tiles, clamped
        ///         to [0, path.Waypoints.Count - 1] (PathfindingSystem guards the count).
        ///         This effectively teleports the enemy forward along its current path;
        ///         the next movement tick will start moving toward the new waypoint.
        ///       * Reset EnemyBlinkTimer to 0 (next blink fires after another interval).
        ///       * Set EnemyBlinkIFramesLeft = 0.2f (post-blink invulnerability).
        ///   - When pathfinding is null (not yet injected), skip the node-index advance
        ///     (timer still ticks but no teleport happens — graceful degradation).
        /// Sentinel-gated on EnemyIsBlinker; non-blinkers pay zero overhead. O(activeEnemies)
        /// per tick, cheap (1 bool + a few float/int ops per slot).
        /// </summary>
        private void TickBlinkerCycle(float deltaTime)
        {
            if (deltaTime <= 0f) return;
            var activeEnemies = store.ActiveEnemyIds;
            // Constant for the post-blink i-frame window: 0.2s = 12 frames at 60Hz. Short
            // enough to keep the enemy vulnerable (player can damage it normally for the
            // vast majority of its lifespan) but long enough to give a visual "blink" feel.
            const float BLINK_IFRAME_DURATION = 0.2f;
            for (int i = 0; i < activeEnemies.Count; i++)
            {
                int eid = activeEnemies[i];
                if (!store.EnemyIsBlinker[eid]) continue;

                // Step 1: Decrement i-frames first (so the i-frame window shrinks
                // symmetrically with the cooldown, not the other way around).
                float ifr = store.EnemyBlinkIFramesLeft[eid];
                if (ifr > 0f)
                {
                    float newIfr = ifr - deltaTime;
                    store.EnemyBlinkIFramesLeft[eid] = newIfr > 0f ? newIfr : 0f;
                }

                // Step 2: Tick the between-blinks timer; trigger blink when ready.
                float timer = store.EnemyBlinkTimer[eid] + deltaTime;
                float interval = store.EnemyBlinkInterval[eid];
                if (interval > 0f && timer >= interval)
                {
                    // Trigger blink: advance path node index by BlinkDistance tiles
                    // (rounded up so even a 0.5-tile blink actually moves the enemy
                    // one node forward). Clamp to last waypoint so we don't overshoot
                    // the end of the path (which would leak the enemy through).
                    if (_pathfinding != null)
                    {
                        int pathId = store.EnemyPathId[eid];
                        int totalNodes = _pathfinding.GetPathWaypointCount(pathId);
                        if (totalNodes > 0)
                        {
                            int curNode = store.EnemyPathNodeIndex[eid];
                            // Only advance if the enemy is still on a valid path
                            // (curNode < 0 = at goal / leaked / never-pathed; skip the warp
                            // so a finished enemy can't be teleported to a stale node).
                            if (curNode >= 0)
                            {
                                int distance = (int)MathF.Ceiling(store.EnemyBlinkDistance[eid]);
                                if (distance < 1) distance = 1;
                                int newNode = curNode + distance;
                                if (newNode >= totalNodes) newNode = totalNodes - 1;
                                store.EnemyPathNodeIndex[eid] = newNode;
                            }
                        }
                    }
                    // Reset cycle: timer to 0, grant 0.2s i-frames
                    store.EnemyBlinkTimer[eid] = 0f;
                    store.EnemyBlinkIFramesLeft[eid] = BLINK_IFRAME_DURATION;
                }
                else
                {
                    store.EnemyBlinkTimer[eid] = timer;
                }
            }
        }
        // Reused batch buffer for position events — Clear()'d each frame instead of
        // allocating a new List (AGENTS.md §5.2 禁止每帧分配 List/字典). The backing array
        // grows to the peak active-enemy count once and is reused thereafter. Consumers must
        // process OnPositionsChanged synchronously and must not retain the reference beyond
        // the call (NullEventBus / ConsoleEventBus both comply).
        private readonly List<(int, float, float)> _positionChanges = new List<(int, float, float)>();

        /// <summary>
        /// Emit OnPositionChanged for every active enemy after the movement phase.
        /// Uses batch API to reduce cross-boundary call overhead.
        /// </summary>
        private void EmitPositionEvents()
        {
            var activeEnemies = store.ActiveEnemyIds;
            _positionChanges.Clear();
            for (int i = 0; i < activeEnemies.Count; i++)
            {
                int eid = activeEnemies[i];
                _positionChanges.Add((eid, store.PositionX[eid], store.PositionY[eid]));
            }
            if (_positionChanges.Count > 0)
                _eventBus.OnPositionsChanged(_positionChanges);
        }
    }
}
