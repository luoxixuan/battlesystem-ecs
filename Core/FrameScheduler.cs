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
        private FrameGraph? _frameGraph;
        private readonly FrameExecutionContext _executionContext;
        private int _frameNumber;
        private readonly Core.GAS.ClockId _effectClock;
        private readonly FrameScenarioKind _scenarioKind;
        private FrameGraphCompositionKind _compositionKind = FrameGraphCompositionKind.Direct;
        private readonly Dictionary<string, Action<NodeExecutionContext>> _frameBindings = new Dictionary<string, Action<NodeExecutionContext>>(StringComparer.Ordinal);
        private readonly Dictionary<string, FrameNodeRuntimeDeclaration> _runtimeFrameDeclarations = new Dictionary<string, FrameNodeRuntimeDeclaration>(StringComparer.Ordinal);
        private readonly HashSet<string> _completedRegistrationBindings = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<Action<PhaseContext>> _abilityPhaseConsumers = new List<Action<PhaseContext>>();
        private readonly List<Action> _abilityRejectConsumers = new List<Action>();
        private Func<int, int>? _pathWaypointCountQuery;

        public FrameGraphCompositionKind CompositionKind => _compositionKind;
        public bool IsCompositionSealed => _frameGraph != null;
        internal bool RemoveFrameBinding(string id) => _frameBindings.Remove(id) || Build.RemoveBinding(id);
        internal void RegisterFrameBinding(string id, Action<NodeExecutionContext> execute)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Frame binding id is required.", nameof(id));
            if (!FrameBindingFacts.TryGet(id, out var contract))
                throw new FrameGraphValidationException("Unknown frame binding id: " + id);
            RegisterFrameNodeContract(id, contract.RegistrationId, contract.Phase, contract.ExecutionPolicy, contract.RequiredTokens);
            _frameBindings[id] = execute ?? throw new ArgumentNullException(nameof(execute));
        }
        internal void RegisterFrameBinding(FrameBindingRegistration registration, Action<NodeExecutionContext> execute)
        {
            if (string.IsNullOrEmpty(registration.NodeId)) return;
            if (registration.RegistrationId.StartsWith("disabled.", StringComparison.Ordinal) &&
                !(_compositionKind == FrameGraphCompositionKind.Direct && registration.NodeId.StartsWith("movement.wound.", StringComparison.Ordinal))) return;
            RegisterFrameNodeContract(registration.NodeId, registration.RegistrationId, registration.Phase,
                registration.ExecutionPolicy, registration.RequiredTokens);
            _frameBindings[registration.NodeId] = execute ?? throw new ArgumentNullException(nameof(execute));
        }
        internal void RegisterBuildFrameBinding(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Frame binding id is required.", nameof(id));
            if (!FrameBindingFacts.TryGet(id, out var contract))
                throw new FrameGraphValidationException("Unknown frame binding id: " + id);
            RegisterFrameNodeContract(id, contract.RegistrationId, contract.Phase, contract.ExecutionPolicy, contract.RequiredTokens);
        }
        internal void RegisterBuildFrameBinding(FrameBindingRegistration registration)
        {
            if (string.IsNullOrEmpty(registration.NodeId)) return;
            if (registration.RegistrationId.StartsWith("disabled.", StringComparison.Ordinal)) return;
            RegisterFrameNodeContract(registration.NodeId, registration.RegistrationId, registration.Phase,
                registration.ExecutionPolicy, registration.RequiredTokens);
        }
        internal bool TryGetFrameBinding(string id, out Action<NodeExecutionContext>? execute) => _frameBindings.TryGetValue(id, out execute);
        internal IReadOnlyDictionary<string, FrameNodeRuntimeDeclaration> RuntimeFrameDeclarations => _runtimeFrameDeclarations;
        internal bool TryGetFrameNodeContract(string nodeId, out FrameNodeRuntimeDeclaration? declaration)
            => _runtimeFrameDeclarations.TryGetValue(nodeId, out declaration);
        /// <summary>记录绑定路径实际声明的帧节点合同；声明独立于 manifest，重复或漂移立即失败。</summary>
        internal void RegisterFrameNodeContract(string nodeId, string registrationId, FramePhaseMask phase,
            FrameExecutionSemantics executionPolicy, IReadOnlyList<string> requiredTokens)
        {
            if (_frameGraph != null) throw new InvalidOperationException("Frame node contract cannot change after frame graph seal.");
            if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(registrationId))
                throw new ArgumentException("Frame node and registration identifiers are required.");
            var declaration = new FrameNodeRuntimeDeclaration(nodeId, registrationId, phase, executionPolicy, requiredTokens);
            if (_runtimeFrameDeclarations.TryGetValue(nodeId, out var existing))
            {
                if (!existing.Equals(declaration))
                    throw new FrameGraphValidationException("Runtime frame node declaration drift: " + nodeId);
                throw new FrameGraphValidationException("Duplicate runtime frame node declaration: " + nodeId);
            }
            _runtimeFrameDeclarations.Add(nodeId, declaration);
        }
        /// <summary>登记实际绑定事实；已有声明必须与独立事实逐字段一致。</summary>
        internal void RegisterOrValidateFrameBindingFact(FrameBindingRegistration fact)
        {
            if (_runtimeFrameDeclarations.TryGetValue(fact.NodeId, out var existing))
            {
                if (!string.Equals(existing.RegistrationId, fact.RegistrationId, StringComparison.Ordinal) ||
                    existing.Phase != fact.Phase || existing.ExecutionPolicy != fact.ExecutionPolicy ||
                    existing.RequiredTokens.Length != fact.RequiredTokens.Length)
                    throw new FrameGraphValidationException("Runtime frame node declaration drift: " + fact.NodeId);
                for (int i = 0; i < fact.RequiredTokens.Length; i++)
                    if (!string.Equals(existing.RequiredTokens[i], fact.RequiredTokens[i], StringComparison.Ordinal))
                        throw new FrameGraphValidationException("Runtime frame node declaration drift: " + fact.NodeId);
                return;
            }
            RegisterFrameNodeContract(fact.NodeId, fact.RegistrationId, fact.Phase,
                fact.ExecutionPolicy, fact.RequiredTokens);
        }
        internal void CompleteRegistrationBinding(SystemRegistrationEntry entry)
        {
            if (_frameGraph != null) throw new InvalidOperationException("Registration binding cannot complete after frame graph seal.");
            if (entry.IsDisabled) throw new InvalidOperationException("Disabled registration cannot complete binding: " + entry.Id);
            if (!_completedRegistrationBindings.Add(entry.Id))
                throw new InvalidOperationException("Registration binder executed more than once: " + entry.Id);
        }
        internal bool IsRegistrationBindingComplete(string registrationId) => _completedRegistrationBindings.Contains(registrationId);

        internal void AddCompletedRegistrationDependencies(FrameGraphBuilder builder)
        {
            foreach (var entry in SystemRegistrationManifest.Entries)
            {
                if (!entry.IsDisabled && _completedRegistrationBindings.Contains(entry.Id))
                    for (int i = 0; i < entry.ProvidedTokens.Length; i++)
                        builder.AddAvailableDependency(entry.ProvidedTokens[i]);
            }
        }
        public IReadOnlyList<FrameNodeAdapter> FrameGraphPlan => RequireFrameGraph().Nodes;
        public IReadOnlyList<FrameCompositionDiagnostic> FrameGraphDiagnostics => RequireFrameGraph().Diagnostics;
        public IReadOnlyList<string> FrameGraphAvailableDependencies => RequireFrameGraph().AvailableDependencies;
        public string FrameGraphTopologyHash => RequireFrameGraph().TopologyHash;
        public string FrameGraphReviewRoot => RequireFrameGraph().ReviewRoot;
        public TimeContext LastTimeContext { get; private set; }
        public Core.GAS.ClockId EffectClock => _effectClock;
        public FrameScenarioKind ScenarioKind => _scenarioKind;
        internal bool HasPathfindingDependency => _pathWaypointCountQuery != null;
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
                store.GameplayPhaseContext = context;
                for (int i = 0; i < _abilityPhaseConsumers.Count; i++)
                    _abilityPhaseConsumers[i](context);
            }
        }
        // ── System groups — one per logical phase ──
        internal BuildGroup       Build       { get; } = new();
        internal PreGameGroup     PreGame     { get; } = new();
        internal SpawningGroup    Spawning    { get; } = new();
        internal AIGroup          AI          { get; } = new();
        internal MovementGroup    Movement    { get; } = new();
        internal TerrainGroup     Terrain     { get; } = new();
        internal CombatSetupGroup CombatSetup { get; } = new();
        internal SpatialGroup     Spatial     { get; } = new();
        internal CombatGroup      Combat      { get; } = new();
        internal SkillBuffGroup   SkillBuff   { get; } = new();
        internal PostDeathGroup   PostDeath   { get; } = new();

        /// <summary>
        /// 波次生成是否被暂停。暂停期间不再推进 WaveSpawningSystem.Update（不生成、不自动进入下一波），
        /// 已排队的波次回调仍会派发；巢穴等其他生成系统不受影响。
        /// </summary>
        public bool IsSpawningPaused => Spawning.WavePaused;

        /// <summary>
        /// 暂停/恢复波次生成（供 Unity 等外部驱动自行控制波次节奏）。可在封印前后随时调用；
        /// 在 OnWaveComplete 回调内调用是安全的。与 <see cref="ProductionSystemInstaller"/> 的图组装无关。
        /// </summary>
        public void SetSpawningPaused(bool paused) => Spawning.WavePaused = paused;

        public FrameScheduler(ComponentStore store, GameConfig gameConfig, IBattleEventBus? eventBus = null,
            Core.GAS.ClockId effectClock = Core.GAS.ClockId.Combat,
            FrameScenarioKind scenarioKind = FrameScenarioKind.Gameplay)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            _capacityWatch = new CapacityDropWatch(store);
            store.GameplayPhaseContext = PhaseContext.FromGameState(_phase);
            _ = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
            if (!Enum.IsDefined(typeof(Core.GAS.ClockId), effectClock))
                throw new ArgumentOutOfRangeException(nameof(effectClock), effectClock, "Unknown effect clock.");
            if (!Enum.IsDefined(typeof(FrameScenarioKind), scenarioKind))
                throw new ArgumentOutOfRangeException(nameof(scenarioKind), scenarioKind, "Unknown frame scenario kind.");
            _eventBus = eventBus ?? NullEventBus.Instance;
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
        /// 注册可选的路径节点数量查询。缺失时 Blinker 保持 no-op 路径策略。
        /// </summary>
        internal void RegisterPathWaypointCountQuery(Func<int, int> query)
        {
            if (_frameGraph != null) throw new InvalidOperationException("Pathfinding dependency cannot change after frame graph seal.");
            _pathWaypointCountQuery = query ?? throw new ArgumentNullException(nameof(query));
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
            store.DeferAbilityAndEffectCommit = true;
            _frameGraph = FrameSystemGraph.Build(this, _compositionKind);
        }

        internal bool RunsGameplayScenario => _scenarioKind == FrameScenarioKind.Gameplay;

        public void ConfigureGameplayRuntime(IReadOnlyList<Core.GAS.TriggerDefinition> triggers)
        {
            _gameplayTriggers = triggers ?? Array.Empty<Core.GAS.TriggerDefinition>();
        }

        internal void RegisterAbilityPhaseConsumer(Action<PhaseContext> phaseChanged, Action? rejectPending = null)
        {
            if (_frameGraph != null) throw new InvalidOperationException("Ability phase consumers cannot change after frame graph seal.");
            if (phaseChanged == null) throw new ArgumentNullException(nameof(phaseChanged));
            _abilityPhaseConsumers.Add(phaseChanged);
            if (rejectPending != null) _abilityRejectConsumers.Add(rejectPending);
            phaseChanged(PhaseContext.FromGameState(_phase));
        }

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
            graph.Execute(_executionContext);
            LastTimeContext = _executionContext.Time;
            // 每帧推进基线（几次计数读取），保证订阅前后的增量都按帧归属
            if (_capacityWatch.TryCollect(_executionContext.Frame, out var report)) CapacityDropped?.Invoke(report);
        }

        /// <summary>
        /// 帧末容量丢弃通知：本帧新增 Resolver 请求溢出拒绝、事件溢出或触发器循环中止时发出。
        /// 生产由 GameManager 订阅并限频记日志，使压测之外的丢弃也可见。
        /// </summary>
        public event Action<CapacityDropReport>? CapacityDropped;
        private readonly CapacityDropWatch _capacityWatch;

        private void RejectNonWaveAbilityWork()
        {
            for (int i = 0; i < _abilityRejectConsumers.Count; i++)
                _abilityRejectConsumers[i]();
        }

        private void RejectPhaseTransitionWork()
        {
            for (int i = 0; i < _abilityRejectConsumers.Count; i++)
                _abilityRejectConsumers[i]();
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

        /// <summary>压测专用：开启生产 FrameGraph 逐节点计时（仅 BenchmarkSystem 调用）。</summary>
        internal FrameGraph EnableFrameGraphNodeTiming()
        {
            FrameGraph graph = RequireFrameGraph();
            graph.EnableNodeTiming();
            return graph;
        }

        private FrameGraph RequireFrameGraph()
        {
            return _frameGraph ?? throw new InvalidOperationException(
                "Frame graph composition must be built, validated, and sealed before the first Tick.");
        }

        internal void GraphBeginFrame(NodeExecutionContext context)
        {
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
        internal void GraphTickEffectBuild(NodeExecutionContext context) => GraphTickEffects(context, Core.GAS.ClockId.Build);
        internal void GraphTickEffectCombat(NodeExecutionContext context) => GraphTickSupplementalEffect(context, Core.GAS.ClockId.Combat);
        internal void GraphTickEffectEnemy(NodeExecutionContext context) => GraphTickSupplementalEffect(context, Core.GAS.ClockId.Enemy);
        internal void GraphTickEffectReal(NodeExecutionContext context) => GraphTickSupplementalEffect(context, Core.GAS.ClockId.RealTime);
        internal void GraphTickEffectGlobal(NodeExecutionContext context) => GraphTickSupplementalEffect(context, Core.GAS.ClockId.Global);

        internal void GraphConsumeRally(NodeExecutionContext context) => SkillBuff.ConsumeRally();

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

        internal void GraphRejectNonWaveAbilities(NodeExecutionContext context)
        {
            Core.GAS.GameplayAbilityRuntime.RejectQueuedAbilities(store);
            store.GameplayEffectsRuntime.RejectQueuedEffects();
            RejectNonWaveAbilityWork();
        }

        internal void GraphCommitQueuedAbilities(NodeExecutionContext context) =>
            Core.GAS.GameplayAbilityRuntime.CommitQueuedAbilities(store);

        internal void GraphCommitQueuedEffects(NodeExecutionContext context) =>
            store.GameplayEffectsRuntime.CommitQueuedEffects();
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
                    if (_pathWaypointCountQuery != null)
                    {
                        int pathId = store.EnemyPathId[eid];
                        int totalNodes = _pathWaypointCountQuery(pathId);
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
