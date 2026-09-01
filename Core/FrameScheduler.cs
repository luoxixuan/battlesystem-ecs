#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
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
        public ComponentStore Store => store;
        private readonly IBattleEventBus _eventBus;
        private IReadOnlyList<Core.GAS.TriggerDefinition> _gameplayTriggers = Array.Empty<Core.GAS.TriggerDefinition>();
        private float _externalDeltaTime;
        private FrameGraph? _frameGraph;
        private readonly FrameExecutionContext _executionContext;
        private int _frameNumber;
        private readonly FrameSchedulerExecutionMode _executionMode;
        private readonly Core.GAS.ClockId _effectClock;
        private readonly FrameScenarioKind _scenarioKind;
        private FrameGraphCompositionKind _compositionKind = FrameGraphCompositionKind.Direct;

        public FrameGraphCompositionKind CompositionKind => _compositionKind;
        public FrameSchedulerExecutionMode ExecutionMode => _executionMode;
        public bool IsCompositionSealed => _frameGraph != null;
        public IReadOnlyList<FrameNodeAdapter> FrameGraphPlan => RequireFrameGraph().Nodes;
        public IReadOnlyList<FrameCompositionDiagnostic> FrameGraphDiagnostics => RequireFrameGraph().Diagnostics;
        public IReadOnlyList<string> FrameGraphAvailableDependencies => RequireFrameGraph().AvailableDependencies;
        public string FrameGraphTopologyHash => RequireFrameGraph().TopologyHash;
        public string FrameGraphReviewRoot => RequireFrameGraph().ReviewRoot;
        public TimeContext LastTimeContext { get; private set; }
        public Core.GAS.ClockId EffectClock => _effectClock;
        public FrameScenarioKind ScenarioKind => _scenarioKind;
        internal bool HasPathfindingDependency => _pathfinding != null;
        internal int GraphCurrentWave { get; set; } = 1;
        internal int GraphCurrentLevel { get; set; } = 1;
        internal int LastPublishedPersistentFrame { get; private set; } = -1;

        /// <summary>
        /// 生产边界：帧外检测到的事实必须经调度器进入当前帧死亡闭环。
        /// </summary>
        internal void QueueCurrentFrameEnemyDeath(int enemyId, int playerId)
            => store.QueueEnemyDeath(enemyId, playerId);

        internal void ResolveCurrentFrameDeaths()
            => store.ResolveEnemiesKilledThisFrame();

        private Systems.SkillSystem? _skillSystem;
        private Systems.GlobalSkillSystem? _globalSkillSystem;
        private Systems.HeroSkillSystem? _heroSkillSystem;
        private Systems.TowerActiveSkillSystem? _towerActiveSkillSystem;
        private GameState _phase = GameState.WavePhase;
        public GameState Phase
        {
            get => _phase;
            set
            {
                if (_phase == GameState.WavePhase && value != GameState.WavePhase)
                    RejectPhaseTransitionWork();
                _phase = value;
                var context = PhaseContext.FromGameState(value);
                _skillSystem?.SetPhaseContext(context);
                _globalSkillSystem?.SetPhaseContext(context);
                _heroSkillSystem?.SetPhaseContext(context);
                _towerActiveSkillSystem?.SetPhaseContext(context);
            }
        }
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

        public FrameScheduler(ComponentStore store, GameConfig gameConfig, IBattleEventBus? eventBus = null,
            FrameSchedulerExecutionMode executionMode = FrameSchedulerExecutionMode.Graph,
            Core.GAS.ClockId effectClock = Core.GAS.ClockId.Combat,
            FrameScenarioKind scenarioKind = FrameScenarioKind.Gameplay)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            _ = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
            if (!Enum.IsDefined(typeof(FrameSchedulerExecutionMode), executionMode))
                throw new ArgumentOutOfRangeException(nameof(executionMode), executionMode, "Unknown scheduler execution mode.");
            if (!Enum.IsDefined(typeof(Core.GAS.ClockId), effectClock))
                throw new ArgumentOutOfRangeException(nameof(effectClock), effectClock, "Unknown effect clock.");
            if (!Enum.IsDefined(typeof(FrameScenarioKind), scenarioKind))
                throw new ArgumentOutOfRangeException(nameof(scenarioKind), scenarioKind, "Unknown frame scenario kind.");
            _eventBus = eventBus ?? NullEventBus.Instance;
            _executionMode = executionMode;
            _effectClock = effectClock;
            _scenarioKind = scenarioKind;
            _executionContext = new FrameExecutionContext(store, 0f, 0, 0, PhaseContext.FromGameState(_phase));
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
            if (_frameGraph != null) throw new InvalidOperationException("Pathfinding dependency cannot change after frame graph seal.");
            _pathfinding = pathfinding;
        }

        internal void ConfigureGraphComposition(FrameGraphCompositionKind compositionKind)
        {
            if (_frameGraph != null) throw new InvalidOperationException("Composition kind cannot change after frame graph seal.");
            if (!Enum.IsDefined(typeof(FrameGraphCompositionKind), compositionKind))
                throw new ArgumentOutOfRangeException(nameof(compositionKind), compositionKind, "Unknown graph composition kind.");
            _compositionKind = compositionKind;
        }

        internal void SealGraphComposition()
        {
            if (_frameGraph != null) throw new InvalidOperationException("Frame graph composition is already sealed.");
            _frameGraph = FrameSystemGraph.Build(this, _compositionKind);
        }

        internal void GraphUpdateWaveSpawning(Systems.WaveSpawningSystem system)
        {
            if (_scenarioKind == FrameScenarioKind.Gameplay)
                system.Update();
        }

        public void ConfigureGameplayRuntime(IReadOnlyList<Core.GAS.TriggerDefinition> triggers)
        {
            _gameplayTriggers = triggers ?? Array.Empty<Core.GAS.TriggerDefinition>();
        }

        public void SetSkillSystem(Systems.SkillSystem? skillSystem)
        {
            _skillSystem = skillSystem;
            _skillSystem?.SetPhaseContext(PhaseContext.FromGameState(_phase));
        }

        public void SetGlobalSkillSystem(Systems.GlobalSkillSystem? globalSkillSystem)
        {
            _globalSkillSystem = globalSkillSystem;
            _globalSkillSystem?.SetPhaseContext(PhaseContext.FromGameState(_phase));
        }

        public void SetHeroSkillSystem(Systems.HeroSkillSystem? system) { _heroSkillSystem = system; _heroSkillSystem?.SetPhaseContext(PhaseContext.FromGameState(_phase)); }
        public void SetTowerActiveSkillSystem(Systems.TowerActiveSkillSystem? system) { _towerActiveSkillSystem = system; _towerActiveSkillSystem?.SetPhaseContext(PhaseContext.FromGameState(_phase)); }

        public void BindStateMachine(StateMachine stateMachine)
        {
            if (stateMachine == null) throw new ArgumentNullException(nameof(stateMachine));
            Phase = stateMachine.CurrentState;
            foreach (GameState state in Enum.GetValues(typeof(GameState)))
            {
                GameState captured = state;
                stateMachine.OnEnter(captured, () => Phase = captured);
            }
        }

        /// <summary>
        /// Execute one full frame of systems, gated by current Phase.
        /// BuildPhase 只执行准备系统，WavePhase 执行完整战斗管线。
        /// 其他阶段拒绝未提交能力请求并直接返回。
        /// </summary>
        public void Tick(float deltaTime, int turn)
        {
            FrameGraph graph = RequireFrameGraph();
            _executionContext.Reset(deltaTime, turn, _frameNumber++, PhaseContext.FromGameState(Phase));
            if (_executionMode == FrameSchedulerExecutionMode.Graph)
                graph.Execute(_executionContext);
            else
                TickLegacy(deltaTime, turn);
            LastTimeContext = _executionContext.Time;
        }

        private void TickLegacy(float deltaTime, int turn)
        {
            _externalDeltaTime = deltaTime;
            store.ApplyComputedAttributeModeAtFrameBoundary();
            store.BeginFrame();
            store.GameplayEffectsRuntime.ResetFrame();
            store.GameplayTriggersRuntime.ResetFrame();
            store.DamageResolver.EnableDeferred(true);
            store.ResourceResolver.EnableDeferred(true);
            // Attribute modifiers become visible at the scheduler's aggregate boundary.
            store.SyncComputedAttributeBases();
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

            TimeContext time = FreezeTimeContextCore(deltaTime);

            if (Phase == GameState.BuildPhase)
            {
                Build.Execute(store, time.BuildDelta);
                // BuildPhase has no combat/death commit. Reject any combat requests
                // emitted by compatibility systems instead of carrying them into Wave.
                store.DamageResolver.RejectPending(Core.GAS.DamageCommitBoundary.GameplayResolve);
                store.ResourceResolver.RejectPendingEnemyDamage();
                // Build 阶段继续推进 RealTime/Global/Build effect；Combat/Enemy 伤害在本帧明确拒绝。
                store.GameplayEffectsRuntime.Tick(_externalDeltaTime, Core.GAS.ClockId.RealTime);
                store.GameplayEffectsRuntime.Tick(time.GlobalDelta, Core.GAS.ClockId.Global);
                store.GameplayEffectsRuntime.Tick(time.BuildDelta, Core.GAS.ClockId.Build);
                store.DamageResolver.RejectPendingEnemyDamage();
                store.DamageResolver.CommitBoundary(Core.GAS.DamageCommitBoundary.GameplayResolve);
                store.ResourceResolver.CommitBoundary(Core.GAS.DamageCommitBoundary.GameplayResolve);
                if (_gameplayTriggers.Count > 0)
                {
                    store.GameplayTriggersRuntime.ConsumeOnly(store.DamageResolver.Events, _gameplayTriggers, false, Core.GAS.GameplayEventType.HitConfirmed, Core.GAS.GameplayEventType.DamageApplied, Core.GAS.GameplayEventType.EffectApplied);
                    store.GameplayTriggersRuntime.ConsumeOnly(store.ResourceResolver.Events, _gameplayTriggers, false, Core.GAS.GameplayEventType.HealApplied, Core.GAS.GameplayEventType.ShieldChanged, Core.GAS.GameplayEventType.ResourceChanged);
                    store.GameplayTriggersRuntime.Consume(store.GameplayEffectsRuntime.Events, _gameplayTriggers, true);
                    store.GameplayTriggersRuntime.ConsumeNextRounds(_gameplayTriggers);
                }
                store.DamageResolver.EnableDeferred(false);
                store.ResourceResolver.EnableDeferred(false);
                RejectNonWaveAbilityWork();
                return;
            }

            if (Phase != GameState.WavePhase)
            {
                store.DamageResolver.RejectPending(Core.GAS.DamageCommitBoundary.GameplayResolve);
                store.ResourceResolver.RejectPendingEnemyDamage();
                store.DamageResolver.EnableDeferred(false);
                store.ResourceResolver.EnableDeferred(false);
                RejectNonWaveAbilityWork();
                return;
            }

            RunWavePhaseLegacy(time, turn);
        }

        private void RejectNonWaveAbilityWork()
        {
            _skillSystem?.RejectPendingSkillDamage(Systems.SkillDamageRejectReason.PhaseNotAllowed);
            _globalSkillSystem?.RejectPendingActivation();
        }

        private void RejectPhaseTransitionWork()
        {
            _skillSystem?.RejectPendingSkillDamage(Systems.SkillDamageRejectReason.PhaseNotAllowed);
            _globalSkillSystem?.RejectPendingActivation();
            store.DamageResolver.RejectAllPending();
            store.ResourceResolver.RejectAllPending();
            store.DamageResolver.EnableDeferred(false);
            store.ResourceResolver.EnableDeferred(false);
        }

        /// <summary>
        /// Full game turn with post-tick game logic, matching GameManager.Run() behavior.
        /// </summary>
        public void TickGameTurn(float deltaTime, int turn)
        {
            Tick(deltaTime, turn);
        }

        private FrameGraph RequireFrameGraph()
        {
            return _frameGraph ?? throw new InvalidOperationException(
                "Frame graph composition must be built, validated, and sealed before the first Tick.");
        }

        internal void GraphBeginFrame(NodeExecutionContext context)
        {
            _externalDeltaTime = context.Delta;
            store.ApplyComputedAttributeModeAtFrameBoundary();
            store.BeginFrame();
            store.GameplayEffectsRuntime.ResetFrame();
            store.GameplayTriggersRuntime.ResetFrame();
            store.DamageResolver.EnableDeferred(true);
            store.ResourceResolver.EnableDeferred(true);
            store.SetTurnCCFlags();
        }

        internal void GraphPublishPersistentFrameState(NodeExecutionContext context)
        {
            Thread.MemoryBarrier();
            LastPublishedPersistentFrame=context.Frame;
        }

        internal void GraphDecrementInvulnerability(NodeExecutionContext context) => DecrementInvulnFramesLeft();
        internal void GraphTickPhaser(NodeExecutionContext context) => TickPhaserCycle(context.Delta);
        internal void GraphTickBlinker(NodeExecutionContext context) => TickBlinkerCycle(context.Delta);

        internal void GraphFreezeTime(NodeExecutionContext context)
        {
            TimeContext time = CreateTimeContext(context.Time.RawDelta, context.Turn, context.Frame, context.Phase);
            context.FreezeTime(time);
        }

        private TimeContext FreezeTimeContextCore(float rawDelta)
        {
            TimeContext time = CreateTimeContext(rawDelta, _executionContext.Turn, _executionContext.Frame, _executionContext.Time.Phase);
            _executionContext.FreezeTime(time);
            return time;
        }

        private TimeContext CreateTimeContext(float rawDelta, int turn, int frame, PhaseContext phase)
        {
            float globalDelta = rawDelta;
            UpdateTimeScale(ref globalDelta);
            float enemyDelta = globalDelta;
            float combatDelta = globalDelta;
            if (phase.Kind == PhaseContextKind.Wave)
                SplitDeltaForBulletTime(globalDelta, out enemyDelta, out combatDelta);
            Core.GAS.ClockId effectClock = phase.Kind == PhaseContextKind.Build ? Core.GAS.ClockId.Build : _effectClock;
            float effectDelta = effectClock switch
            {
                Core.GAS.ClockId.Build => globalDelta,
                Core.GAS.ClockId.Enemy => enemyDelta,
                Core.GAS.ClockId.Combat => combatDelta,
                Core.GAS.ClockId.RealTime => rawDelta,
                Core.GAS.ClockId.Global => globalDelta,
                _ => throw new InvalidOperationException($"Unsupported gameplay clock '{effectClock}'.")
            };
            return new TimeContext(rawDelta, rawDelta, enemyDelta, combatDelta, effectDelta,
                globalDelta, globalDelta, turn, frame, phase, effectClock);
        }

        internal void GraphAggregateAttributes(NodeExecutionContext context)
        {
            store.SyncComputedAttributeBases();
            store.AttributeAggregator.AggregateDirty();
        }

        internal void GraphTickEffects(NodeExecutionContext context, Core.GAS.ClockId clock) =>
            store.GameplayEffectsRuntime.Tick(context.Delta, clock);

        internal void GraphTickConfiguredEffect(NodeExecutionContext context) =>
            store.GameplayEffectsRuntime.Tick(context.Delta, context.EffectClock);

        internal void GraphTickSupplementalEffect(NodeExecutionContext context, Core.GAS.ClockId clock)
        {
            if (clock != context.EffectClock)
                store.GameplayEffectsRuntime.Tick(context.Delta, clock);
        }

        internal void GraphCommitBuildDamage(NodeExecutionContext context)
        {
            store.DamageResolver.RejectPending(Core.GAS.DamageCommitBoundary.GameplayResolve);
            store.ResourceResolver.RejectPendingEnemyDamage();
            store.DamageResolver.RejectPendingEnemyDamage();
            store.DamageResolver.CommitBoundary(Core.GAS.DamageCommitBoundary.GameplayResolve);
        }

        internal void GraphCommitBuildResources(NodeExecutionContext context) =>
            store.ResourceResolver.CommitBoundary(Core.GAS.DamageCommitBoundary.GameplayResolve);

        internal void GraphRejectNonWaveDamage(NodeExecutionContext context)
        {
            store.DamageResolver.RejectPending(Core.GAS.DamageCommitBoundary.GameplayResolve);
            store.ResourceResolver.RejectPendingEnemyDamage();
        }

        internal void GraphRejectNonWaveAbilities(NodeExecutionContext context) => RejectNonWaveAbilityWork();
        internal void GraphCloseDeferredResolvers(NodeExecutionContext context) { store.DamageResolver.EnableDeferred(false); store.ResourceResolver.EnableDeferred(false); }
        internal void GraphCommitEarlyDamage(NodeExecutionContext context) => store.DamageResolver.CommitBoundary(Core.GAS.DamageCommitBoundary.EarlyResolve);
        internal void GraphCommitEarlyResources(NodeExecutionContext context) => store.ResourceResolver.CommitBoundary(Core.GAS.DamageCommitBoundary.EarlyResolve);
        internal void GraphCommitGameplayDamage(NodeExecutionContext context) => store.DamageResolver.CommitBoundary(Core.GAS.DamageCommitBoundary.GameplayResolve);
        internal void GraphCommitGameplayResources(NodeExecutionContext context) => store.ResourceResolver.CommitBoundary(Core.GAS.DamageCommitBoundary.GameplayResolve);
        internal void GraphPrepareDeaths(NodeExecutionContext context) => store.PrepareEnemiesKilledThisFrame();
        internal void GraphDispatchDeathCallbacks(NodeExecutionContext context) => store.DispatchPreparedEnemyDeaths();
        internal void GraphEmitPositions(NodeExecutionContext context) => EmitPositionEvents();
        internal void GraphRebuildSpatialIndex(NodeExecutionContext context) => store.RebuildSpatialGrid();
        internal void GraphAggregateThreat(NodeExecutionContext context) => DecayAndAccumulateThreatScore(context.Delta);

        internal void GraphCommitGameplayEvents(NodeExecutionContext context)
        {
            ConsumeGameplayBoundary(includeDamage: true, includeEffect: true);
        }

        internal void GraphCommitPostDeathGameplayEvents(NodeExecutionContext context)
        {
            if (_gameplayTriggers.Count > 0)
            {
                store.GameplayTriggersRuntime.ConsumeOnly(store.ResourceResolver.Events, _gameplayTriggers, false,
                    Core.GAS.GameplayEventType.HealApplied, Core.GAS.GameplayEventType.ShieldChanged, Core.GAS.GameplayEventType.ResourceChanged);
                store.GameplayTriggersRuntime.Consume(store.GameplayEffectsRuntime.Events, _gameplayTriggers, true);
                store.GameplayTriggersRuntime.ConsumeOnly(store.DamageResolver.Events, _gameplayTriggers, false,
                    Core.GAS.GameplayEventType.HitConfirmed, Core.GAS.GameplayEventType.DamageApplied, Core.GAS.GameplayEventType.EffectApplied);
                store.GameplayTriggersRuntime.ConsumeOnly(store.DamageResolver.Events, _gameplayTriggers, false,
                    Core.GAS.GameplayEventType.KillConfirmed, Core.GAS.GameplayEventType.ResourceChanged, Core.GAS.GameplayEventType.DeathQueued);
                store.GameplayTriggersRuntime.ConsumeNextRounds(_gameplayTriggers);
            }
        }

        private void ConsumeGameplayBoundary(bool includeDamage, bool includeEffect)
        {
            if (_gameplayTriggers.Count == 0) return;
            store.GameplayTriggersRuntime.ConsumeOnly(store.ResourceResolver.Events, _gameplayTriggers, false,
                Core.GAS.GameplayEventType.HealApplied, Core.GAS.GameplayEventType.ShieldChanged, Core.GAS.GameplayEventType.ResourceChanged);
            if (includeDamage)
                store.GameplayTriggersRuntime.ConsumeOnly(store.DamageResolver.Events, _gameplayTriggers, false,
                    Core.GAS.GameplayEventType.HitConfirmed, Core.GAS.GameplayEventType.DamageApplied, Core.GAS.GameplayEventType.EffectApplied);
            if (includeEffect)
                store.GameplayTriggersRuntime.Consume(store.GameplayEffectsRuntime.Events, _gameplayTriggers, true);
            store.GameplayTriggersRuntime.ConsumeNextRounds(_gameplayTriggers);
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
        private void RunWavePhaseLegacy(TimeContext time, int turn)
        {
            float deltaTime = time.GlobalDelta;
            float enemyDt = time.EnemyDelta;
            float combatDt = time.CombatDelta;

            // Phase 1: Pre-game (weather, day/night, difficulty, events) — ENEMY side
            PreGame.Execute(store, enemyDt, turn);
            // Weather and other pre-game producers commit at the explicit early boundary.
            store.DamageResolver.CommitBoundary(Core.GAS.DamageCommitBoundary.EarlyResolve);
            store.ResourceResolver.CommitBoundary(Core.GAS.DamageCommitBoundary.EarlyResolve);

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
            SkillBuff.ExecuteLegacy(store, time, turn);

            // 战斗与技能阶段产生的资源/伤害请求在此提交边界统一可见。
            store.DamageResolver.CommitBoundary(Core.GAS.DamageCommitBoundary.GameplayResolve);
            store.ResourceResolver.CommitBoundary(Core.GAS.DamageCommitBoundary.GameplayResolve);
            if (_gameplayTriggers.Count > 0)
                store.GameplayTriggersRuntime.ConsumeOnly(store.ResourceResolver.Events, _gameplayTriggers, false, Core.GAS.GameplayEventType.HealApplied, Core.GAS.GameplayEventType.ShieldChanged, Core.GAS.GameplayEventType.ResourceChanged);
            if (_gameplayTriggers.Count > 0)
                store.GameplayTriggersRuntime.ConsumeOnly(store.DamageResolver.Events, _gameplayTriggers, false, Core.GAS.GameplayEventType.HitConfirmed, Core.GAS.GameplayEventType.DamageApplied, Core.GAS.GameplayEventType.EffectApplied);
            if (_gameplayTriggers.Count > 0)
                store.GameplayTriggersRuntime.Consume(store.GameplayEffectsRuntime.Events, _gameplayTriggers, true);
            if (_gameplayTriggers.Count > 0)
                store.GameplayTriggersRuntime.ConsumeNextRounds(_gameplayTriggers);
            store.AttributeAggregator.AggregateDirty();

            // Phase 10: Death resolve (uses queued damage, dt-free)
            store.ResolveEnemiesKilledThisFrame();

            // Phase 11: Post-death (fission, life link, objective, resources, corpses, combo) — COMBAT side
            PostDeath.ExecuteLegacy(store, time, turn);
            // 死亡后奖励、治疗与尸体效果沿用同一 Gameplay 提交边界。
            store.DamageResolver.CommitBoundary(Core.GAS.DamageCommitBoundary.GameplayResolve);
            store.ResourceResolver.CommitBoundary(Core.GAS.DamageCommitBoundary.GameplayResolve);
            // PostDeath 可能产生致死 deferred damage；在同一帧闭合第二次死亡解析。
            store.ResolveEnemiesKilledThisFrame();
            if (_gameplayTriggers.Count > 0)
                store.GameplayTriggersRuntime.ConsumeOnly(store.ResourceResolver.Events, _gameplayTriggers, false, Core.GAS.GameplayEventType.HealApplied, Core.GAS.GameplayEventType.ShieldChanged, Core.GAS.GameplayEventType.ResourceChanged);
            if (_gameplayTriggers.Count > 0)
                store.GameplayTriggersRuntime.Consume(store.GameplayEffectsRuntime.Events, _gameplayTriggers, true);
            // PostDeath 产生的生命链接/惩罚伤害也在本边界交给 Trigger，避免下一帧清空事实。
            if (_gameplayTriggers.Count > 0)
                store.GameplayTriggersRuntime.ConsumeOnly(store.DamageResolver.Events, _gameplayTriggers, false, Core.GAS.GameplayEventType.HitConfirmed, Core.GAS.GameplayEventType.DamageApplied, Core.GAS.GameplayEventType.EffectApplied);
            // 死亡/击杀事实只在生命周期和 PostDeath 提交完成后消费。
            if (_gameplayTriggers.Count > 0)
                store.GameplayTriggersRuntime.ConsumeOnly(store.DamageResolver.Events, _gameplayTriggers, false, Core.GAS.GameplayEventType.KillConfirmed, Core.GAS.GameplayEventType.ResourceChanged, Core.GAS.GameplayEventType.DeathQueued);
            if (_gameplayTriggers.Count > 0)
                store.GameplayTriggersRuntime.ConsumeNextRounds(_gameplayTriggers);
            store.DamageResolver.EnableDeferred(false);
            store.ResourceResolver.EnableDeferred(false);

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
