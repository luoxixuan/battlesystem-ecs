using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Content.Contracts;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Skill system refactored to use the GAS (Gameplay Ability System) architecture.
    /// Skills are stored as AbilityInstances in ComponentStore, one slot per ability.
    /// Casting is driven by the GameplayAbilityDef data (area shape, radius, etc.)
    /// instead of hard-coded string branching.
    /// </summary>
    public class SkillSystem : global::BattleSystemECS.Content.Contracts.IAbilityActivationPort
    {
        private ComponentStore store;
        private IRenderer renderer;
        private int playerId;
        private float deltaTime = 1f;
        private GameConfig gameConfig;
        private global::BattleSystemECS.Content.Contracts.ICombatTuningView techTreeSystem;
        private global::BattleSystemECS.Content.Contracts.IEffectCommandPort dotSystem;
        private global::BattleSystemECS.Content.Contracts.IResourcePort manaSystem; // 可选，法力系统未初始化时为空。
        private global::BattleSystemECS.Content.Contracts.ISummonCommandPort summonSystem; // 可选，召唤系统未初始化时为空。
        private global::BattleSystemECS.Content.Contracts.IHealingZoneCommandPort healingZoneSystem; // 可选，治疗区域系统未初始化时为空。
        private global::BattleSystemECS.Content.Contracts.ISnapshotRestorePort timeRewindSystem; // 可选，快照系统未初始化时为空。
        private global::BattleSystemECS.Content.Contracts.IResurrectionPort necromancerSystem; // 可选，复活系统未初始化时为空（第 133 轮 MassResurrect）。
        // Cached turn counter from SetTurn (used for MassResurrect's corpse-age gating)
        private int _currentTurn;
        private List<int> _activeEnemyList;
        // Cached wave-based difficulty multiplier (updated via SetWaveNumber)
        private float _waveDifficultyMult = 1f;
        // Cached armor stats (updated on SetTurn — used in damage calculation)
        private float _armorPenetration = 0f;
        private float _damageTakenMult = 1f;
        private bool _buildPhaseRejectReported;
        private IAbilityPayloadHandler _payloadHandler;

        // 缓存敌人控制抗性统计，每次 SetTurn 从 ICombatTuningView 获取。
        private float _enemyFreezeResistance = 0f;  // from techTreeSystem.GetFreezeResistance()
        private float _enemySlowResistance = 0f;    // from techTreeSystem.GetSlowResistance()
        // Ping-pong double-buffer: eliminates per-frame new ConcurrentBag<>() allocation
        // Tuple: (enemyId, rawDamage) — raw damage only; armor reduction handled by PlayerTowerAttackSystem and TowerAttackSystem
        // 类型化 GAS 请求暂存在此，等待帧图伤害提交；这里是兼容投影，不是第二个伤害所有者。
        private List<Core.GAS.DamageRequest>[] _skillDamageQueue = new List<Core.GAS.DamageRequest>[2];
        private int _skillDamageQueueIdx = 0;
        private int _pendingSkillDamageCount;
        private int _rejectedSkillDamageCount;
        private int _consumedSkillDamageCount;
        private int _rejectedAbilityCount;
        private SkillDamageRejectReason _lastRejectReason;
        private PhaseContext _phaseContext = PhaseContext.Unbound;
        private int _pendingCatalogAbilityId = -1;

        /// <summary>技能伤害请求诊断计数；仅在串行请求/提交边界更新。</summary>
        public int PendingSkillDamageCount => _pendingSkillDamageCount;
        public int RejectedSkillDamageCount => _rejectedSkillDamageCount;
        public int ConsumedSkillDamageCount => _consumedSkillDamageCount;
        public int RejectedAbilityCount => _rejectedAbilityCount;
        public SkillDamageRejectReason LastRejectReason => _lastRejectReason;
        public AbilityActivationResult LastCatalogActivation { get; private set; }
        public PhaseContextKind CurrentPhaseContext => _phaseContext.Kind;

        public bool RequestCatalogAbility(AbilityId abilityId)
        {
            if (_pendingCatalogAbilityId >= 0 || gameConfig?.CompiledCatalog == null ||
                !gameConfig.CompiledCatalog.TryGetAbility(abilityId, out _)) return false;
            _pendingCatalogAbilityId = abilityId.Value;
            return true;
        }

        /// <summary>
        /// 目录优先的激活边界。旧技能槽仅作兼容投影；提交冷却前，选目标与执行引用
        /// 必须在编译目录中闭合。
        /// </summary>
        public AbilityActivationResult TryActivateCatalogAbility(AbilityId abilityId)
        {
            var catalog = gameConfig?.CompiledCatalog;
            if (catalog == null || !catalog.TryGetAbility(abilityId, out var definition))
                return CatalogReject(abilityId, AbilityActivationRejectReason.UnsupportedDefinition);
            if ((uint)definition.Targeting.Id.Value >= (uint)catalog.Targetings.Count ||
                !catalog.TryGetAbility(abilityId, out _))
                return CatalogReject(abilityId, AbilityActivationRejectReason.UnsupportedDefinition);
            for (int i = 0; i < definition.Executions.Count; i++)
                if (!catalog.TryGetExecution(definition.Executions[i], out _))
                    return CatalogReject(abilityId, AbilityActivationRejectReason.UnsupportedDefinition);

            int slot = GameplayAbilityRuntime.FindStoredSlot(store, playerId, abilityId, definition.Name);
            if (slot < 0) return CatalogReject(abilityId, AbilityActivationRejectReason.InvalidRequest);
            if (!IsAbilityAllowed((int)definition.Targeting.Shape))
                return CatalogReject(abilityId, AbilityActivationRejectReason.PhaseNotAllowed, slot);
            bool selfTarget = definition.Targeting.Relation == RelationFilter.Self;
            var source = store.GetEntityHandle(playerId);
            if (selfTarget)
            {
                var abilityRequest = new AbilityRequest(source, abilityId, source,
                    store.AllocateGameplaySequence(playerId));
                return GameplayAbilityRuntime.Activate(store, catalog, abilityRequest, _payloadHandler);
            }
            var request = new AbilityActivationRequest(playerId, slot, definition.Cooldown, playerId, abilityId,
                definition.Effects.Count > 0 ? definition.Effects[0] : (EffectId?)null,
                definition.TriggerRefs.Count > 0 ? definition.TriggerRefs[0] : (TriggerId?)null,
                ownerPlayerId: playerId);
            bool collected = definition.Targeting.Relation == RelationFilter.Allies
                ? TargetingRuntime.TryCollectAllyTargets(store, playerId, definition.Targeting,
                    _catalogTargets, _catalogMagnitudeScales)
                : TargetingRuntime.TryCollectEnemyTargets(store, playerId, definition.Targeting,
                    _catalogTargets, _catalogMagnitudeScales);
            if (!collected)
                return CatalogReject(abilityId, AbilityActivationRejectReason.UnsupportedDefinition, slot);
            if (_catalogTargets.Count == 0)
                return CatalogReject(abilityId, AbilityActivationRejectReason.NoTarget, slot);
            return GameplayAbilityRuntime.ActivateTargets(store, catalog, playerId, slot, request,
                _catalogTargets, _catalogMagnitudeScales, _payloadHandler);
        }

        private static AbilityActivationResult CatalogReject(AbilityId abilityId, AbilityActivationRejectReason reason, int slot = -1) =>
            new AbilityActivationResult(false, abilityId.Value, slot, reason);

        internal void SetPhaseContext(PhaseContext context)
        {
            _phaseContext = context;
            store.GameplayPhaseContext = context;
            if (!context.AllowsCombat) _pendingCatalogAbilityId = -1;
        }

        // 统一的 AoE 命中收集（替代原先每个 Cast 方法各自 Parallel.ForEach + lock 的模式）：
        //   - 敌人数 < ParallelMinEnemies：纯串行直写 _mergedHits（跳过 TPL 启停开销）；
        //   - 否则按 ParallelBatchSize/批分区并行，每批独占一个批缓冲、无锁收集，
        //     Parallel.For 全屏障后按批序合并进 _mergedHits（= 敌人索引序，确定性顺序）。
        // 各 Cast 方法只提供 filter 谓词（只读 store + 写自己的缓冲），
        // 命中后的伤害入队/效果应用统一在串行段完成（_skillDamageQueue 仅串行访问，无需锁）。
        private const int ParallelBatchSize = 256;
        private const int ParallelMinEnemies = 500;
        private List<int>[] _hitBatchBuffers = new List<int>[8];
        private readonly List<int> _mergedHits = new List<int>(64);
        private readonly List<int> _catalogTargets = new List<int>(64);
        private readonly List<float> _catalogMagnitudeScales = new List<float>(64);
        private readonly Dictionary<int, Func<AbilityExecutionContext, int>> _shapeHandlers = new Dictionary<int, Func<AbilityExecutionContext, int>>();

        private readonly struct AbilityExecutionContext
        {
            public readonly GameplayAbilityDef Definition;
            public readonly float Damage;
            public readonly float X;
            public readonly float Y;
            public AbilityExecutionContext(GameplayAbilityDef definition, float damage, float x, float y)
            { Definition = definition; Damage = damage; X = x; Y = y; }
        }

        // Chain Lightning constants
        private const int CHAIN_LIGHTNING_MAX_TARGETS = 4;  // primary + 3 chain targets
        private const float CHAIN_LIGHTNING_DAMAGE_DECAY = 0.70f;  // each hop deals 70% of previous
        // Pre-allocated bool[] reused across CastChainLightning calls (avoids per-call allocation)
        private bool[] _chainHitBuffer = new bool[0];
        private int _chainHitBufferSize = 0;

        // Chain Heal constants (Round 131 — mirror of Chain Lightning, but applies heal to friendlies)
        // Hits injured allies (most-HP-deficit first), then chains up to 3 additional allies, each at 50% of previous heal.
        // Each healed ally also receives a small shield bonus (ShieldBonusPerHop) for survivability.
        private const int CHAIN_HEAL_MAX_TARGETS = 4;        // primary + 3 chain targets (matches Chain Lightning symmetric)
        private const float CHAIN_HEAL_DECAY = 0.50f;        // each hop heals 50% of previous (slower decay than damage, since healing is precious)
        private const float CHAIN_HEAL_DEFAULT_RANGE = 200f; // default hop range in pixels (matches chain lightning default)
        // Pre-allocated bool[] reused across CastChainHeal calls (avoids per-call allocation; tracks friendlies already healed)
        private bool[] _chainHealHitBuffer = new bool[0];
        private int _chainHealHitBufferSize = 0;

        public SkillSystem(ComponentStore store, IRenderer renderer, int playerId, GameConfig gameConfig, global::BattleSystemECS.Content.Contracts.ICombatTuningView techTreeSystem = null)
        {
            this.store = store;
            this.renderer = renderer;
            this.playerId = playerId;
            this.gameConfig = gameConfig;
            this.techTreeSystem = techTreeSystem;
            this.dotSystem = null; // wired up via InjectDotSystem after construction
            _skillDamageQueue[0] = new List<Core.GAS.DamageRequest>(256);
            _skillDamageQueue[1] = new List<Core.GAS.DamageRequest>(256);
            RegisterShapeHandlers();
        }

        private void RegisterShapeHandlers()
        {
            _shapeHandlers[AreaShapeType.Single] = c => CastSingleTarget(c.Damage, c.X, c.Y, c.Definition.AreaRadius, c.Definition.Name);
            _shapeHandlers[AreaShapeType.Cross] = c => CastCrossArea(c.Damage, c.X, c.Y, c.Definition.AreaRadius, c.Definition.Name);
            _shapeHandlers[AreaShapeType.Box] = c => CastBoxArea(c.Damage, c.X, c.Y, c.Definition.AreaRadius, c.Definition.Name);
            _shapeHandlers[AreaShapeType.Circle] = c => CastCircleArea(c.Damage, c.X, c.Y, c.Definition.AreaRadius, c.Definition.Name, c.Definition);
            _shapeHandlers[AreaShapeType.Chain] = c => CastChainLightning(c.Damage, c.X, c.Y, c.Definition.AreaRadius, c.Definition.Name);
            _shapeHandlers[AreaShapeType.Heal] = c => { CastHeal(c.Definition); return 0; };
            _shapeHandlers[AreaShapeType.Shield] = c => { CastShield(c.Definition); return 0; };
            _shapeHandlers[AreaShapeType.Line] = c => CastLineArea(c.Damage, c.X, c.Y, c.Definition.AreaRadius, c.Definition.Name);
            _shapeHandlers[AreaShapeType.Freeze] = c => CastFreezeArea(c.Damage, c.X, c.Y, c.Definition.AreaRadius, c.Definition.Name, c.Definition);
            _shapeHandlers[AreaShapeType.Cone] = c => CastConeArea(c.Damage, c.X, c.Y, c.Definition.AreaRadius, c.Definition.Name, c.Definition.ConeAngleDegrees);
            _shapeHandlers[AreaShapeType.GroundTarget] = c => CastGroundTarget(c.Damage, c.Definition.AreaRadius, c.Definition.Name);
            _shapeHandlers[AreaShapeType.Slow] = c => CastSlowArea(c.Damage, c.X, c.Y, c.Definition.AreaRadius, c.Definition.Name, c.Definition);
            _shapeHandlers[AreaShapeType.TimeWarp] = c => { CastTimeWarp(c.Definition); return 0; };
            _shapeHandlers[AreaShapeType.Summon] = c => { CastSummon(c.Definition); return 0; };
            _shapeHandlers[AreaShapeType.HealingZone] = c => CastHealingZone(c.Definition);
            _shapeHandlers[AreaShapeType.Polymorph] = c => CastPolymorphArea(c.Damage, c.X, c.Y, c.Definition.AreaRadius, c.Definition.Name, c.Definition);
            _shapeHandlers[AreaShapeType.TimeRewind] = c => { CastTimeRewind(c.Definition); return 0; };
            _shapeHandlers[AreaShapeType.ChainHeal] = c => CastChainHealAbility(c.Definition, c.X, c.Y);
            _shapeHandlers[AreaShapeType.MassResurrect] = c => CastMassResurrect(c.Definition, c.X, c.Y);
            _shapeHandlers[AreaShapeType.AoeStun] = c => CastAoeStun(c.X, c.Y, c.Definition.AreaRadius, c.Definition.AoeStunDuration, c.Definition.Name);
            _shapeHandlers[AreaShapeType.AoeRoot] = c => CastAoeRoot(c.X, c.Y, c.Definition.AreaRadius, c.Definition.AoeRootDuration, c.Definition.Name);
            _shapeHandlers[AreaShapeType.AoeKnockback] = c => CastAoeKnockback(c.X, c.Y, c.Definition.AreaRadius, c.Definition.AoeKnockbackForce, c.Definition.Name);
        }
        public void InjectDotSystem(global::BattleSystemECS.Content.Contracts.IEffectCommandPort dotSystem)
        {
            this.dotSystem = dotSystem;
        }

        /// <summary>
        /// 注入法力资源端口；由 ProductionSystemInstaller 按 schema v3 的 Skill typed recipe 在 Wiring 阶段调用。
        /// </summary>
        public void InjectManaSystem(global::BattleSystemECS.Content.Contracts.IResourcePort manaSystem)
        {
            this.manaSystem = manaSystem;
        }

        /// <summary>
        /// 注入召唤命令端口；由 ProductionSystemInstaller 的 Skill typed recipe 在 Wiring 阶段调用。
        /// </summary>
        public void InjectSummonSystem(global::BattleSystemECS.Content.Contracts.ISummonCommandPort summonSystem)
        {
            this.summonSystem = summonSystem;
        }

        public void InjectHealingZoneSystem(global::BattleSystemECS.Content.Contracts.IHealingZoneCommandPort healingZoneSystem)
        {
            this.healingZoneSystem = healingZoneSystem;
        }

        public void InjectTimeRewindSystem(global::BattleSystemECS.Content.Contracts.ISnapshotRestorePort timeRewindSystem)
        {
            this.timeRewindSystem = timeRewindSystem;
        }

        /// <summary>
        /// 注入复活端口；由 ProductionSystemInstaller 的 Skill typed recipe 在 Wiring 阶段调用。若未注入，
        /// casting AreaShapeType.MassResurrect logs a warning and returns 0.
        /// </summary>
        public void InjectNecromancerSystem(global::BattleSystemECS.Content.Contracts.IResurrectionPort necromancerSystem)
        {
            this.necromancerSystem = necromancerSystem;
        }

        internal void SetPayloadHandler(IAbilityPayloadHandler payloadHandler) =>
            _payloadHandler = payloadHandler ?? throw new ArgumentNullException(nameof(payloadHandler));

        /// <summary>
        /// Cache active enemy list at turn start — uses frame-cached list (zero allocation).
        /// </summary>
        public void SetTurn(int turn)
        {
            this._currentTurn = turn;
            this._activeEnemyList = store.GetCachedActiveEnemyIds();
            // Cache armor stats from tech tree
            _armorPenetration = techTreeSystem != null ? techTreeSystem.GetArmorPenetration() : 0f;
            _damageTakenMult = techTreeSystem != null ? techTreeSystem.GetDamageTakenMult() : 1f;
            // Cache enemy CC resistance stats (freeze/slow duration reduction)
            _enemyFreezeResistance = techTreeSystem != null ? techTreeSystem.GetFreezeResistance() : 0f;
            _enemySlowResistance = techTreeSystem != null ? techTreeSystem.GetSlowResistance() : 0f;
        }

        /// <summary>
        /// Update the cached wave difficulty multiplier when wave number changes.
        /// Call this when a new wave starts.
        /// </summary>
        public void SetWaveNumber(int waveNumber)
        {
            _waveDifficultyMult = techTreeSystem != null ? techTreeSystem.GetWaveDifficultyMultiplier(waveNumber) : 1f;
        }

        /// <summary>
        /// Initialize player abilities using GAS — adds one AbilityInstance per skill slot.
        /// Replaces the old single-slot overwrite bug (InitializePlayerSkills called
        /// SetSkillName three times on the same playerId, leaving only Sniper Shot equipped).
        /// Bug#9 fix: Clear existing abilities before re-initializing to prevent accumulation.
        /// </summary>
        public void InitializePlayerSkills()
        {
            store.ResetPlayerAbilities(playerId);

            var skills = gameConfig?.Skills;
            if (skills == null || skills.Count == 0)
            {
                // Fallback: register no abilities (empty skill bar)
                renderer.Log("[SKILL] No skills in game config — skill bar empty");
                return;
            }

            foreach (var sc in skills)
            {
                var def = new GameplayAbilityDef(
                    sc.Name,
                    sc.Description,
                    sc.Cooldown, sc.ManaCost,   // cooldown, mana cost
                    -1, sc.DamageMultiplier > 0 ? sc.DamageMultiplier : 1f,  // fixed base damage multiplier
                    sc.AutoCast ? AbilityActivation.Passive : AbilityActivation.Instant,
                    AreaShapeType.FromString(sc.AreaShape),
                    sc.AreaRadius,
                    sc.DotDuration,
                    sc.DotTickInterval,
                    sc.DotDamagePerTick,
                    sc.HealPercent,
                    sc.ShieldAmount,
                    sc.ShieldDuration,
                    StackingBehavior.None, 1,  // dotStacking, dotMaxStacks
                    sc.FreezeDuration,
                    sc.FreezeChance,  // Cold Nova freeze fields
                    sc.ConeAngleDegrees,  // cone angle in degrees (only meaningful for AreaShape="cone")
                    sc.SlowAmount,         // Slow Nova speed reduction (0 = no slow)
                    sc.SlowDuration        // Slow Nova duration in seconds (0 = no slow)
                );
                def.SummonDefId = sc.SummonDefId;  // carry summon def id through to runtime def
                // Polymorph fields are not part of the positional constructor — set them after.
                // 0 = no polymorph applied; safe to leave at default for non-polymorph skills.
                def.PolymorphDuration = sc.PolymorphDuration;
                def.PolymorphDamageTakenMultiplier = sc.PolymorphDamageTakenMultiplier;
                // Round 136 Direction 2 — AOE CC group control fields. 0 = no effect (safe default).
                def.AoeStunDuration = sc.AoeStunDuration;
                def.AoeRootDuration = sc.AoeRootDuration;
                def.AoeKnockbackForce = sc.AoeKnockbackForce;
                store.AddAbility(playerId, def);
                renderer.Log($"[SKILL] {sc.Name} registered (shape: {sc.AreaShape}, radius: {sc.AreaRadius}, DoT: {sc.DotDuration}s/{sc.DotTickInterval}s×{sc.DotDamagePerTick})");
            }

            // Apply "Attack+10%" and "Crit Rate+5%" buffs via GameplayEffect
            var attackBoost = new GameplayEffectDef("Attack+10%", EffectType.Instant,
                AttributeSetDefinitions.ATTACK_DAMAGE, AttributeModifierOp.Multiply, 1.1f);
            store.TryAddGameplayEffect(playerId, LegacyEffectAdapter.CreateApplication(attackBoost,
                store.GetEntityHandle(playerId), store.GetEntityHandle(playerId)), out _);
            // Sync to bit flags for O(1) hot-path queries
            store.AddBuff(playerId, BuffType.AttackBoost);
            renderer.Log("[SKILL] Applied Effect: Attack+10% (instant, ×1.1)");

            var critBoost = new GameplayEffectDef("Crit Rate+5%", EffectType.Instant,
                AttributeSetDefinitions.CRIT_RATE, AttributeModifierOp.Add, 0.05f);
            store.TryAddGameplayEffect(playerId, LegacyEffectAdapter.CreateApplication(critBoost,
                store.GetEntityHandle(playerId), store.GetEntityHandle(playerId)), out _);
            // Sync to bit flags for O(1) hot-path queries
            store.AddBuff(playerId, BuffType.CritRateBoost);
            renderer.Log("[SKILL] Applied Effect: Crit Rate+5% (instant, +0.05)");

            // Defense+10%: apply armor reduction to incoming damage
            float armorValue = 0.10f;  // 10% damage reduction
            store.PlayerArmor[playerId] = armorValue;
            store.AddBuff(playerId, BuffType.DefenseBoost);
            renderer.Log($"[SKILL] Applied Effect: Defense+10% (armor={armorValue})");
        }

        /// <summary>
        /// Update cooldown timers for all abilities.
        /// Auto-cast any Passive ability that is off cooldown.
        /// </summary>
        public void Update(float deltaTime, bool allowCombat = true)
        {
            this.deltaTime = deltaTime;
            if (allowCombat) _buildPhaseRejectReported = false;

            if (_pendingCatalogAbilityId >= 0)
            {
                int requestedId = _pendingCatalogAbilityId;
                _pendingCatalogAbilityId = -1;
                if (_phaseContext.AllowsCombat)
                    LastCatalogActivation = TryActivateCatalogAbility(new AbilityId(requestedId));
                else
                {
                    _rejectedAbilityCount++;
                    _lastRejectReason = SkillDamageRejectReason.PhaseNotAllowed;
                }
            }

            if (!_phaseContext.AllowsCombat && !_phaseContext.AllowsPreparationResources)
            {
                int unboundCount = store.AbilityCount[playerId];
                for (int slot = 0; slot < unboundCount; slot++)
                {
                    var pending = store.GetAbility(playerId, slot);
                    if (pending.Definition.Activation != AbilityActivation.Passive ||
                        !GameplayAbilityRuntime.TryActivate(store, playerId, slot, out _)) continue;
                    _rejectedAbilityCount++;
                    _lastRejectReason = SkillDamageRejectReason.PhaseNotAllowed;
                    if (!_buildPhaseRejectReported)
                    {
                        renderer.Log($"[ABILITY_REJECTED] PhaseNotAllowed skill={pending.Definition.Name}");
                        _buildPhaseRejectReported = true;
                    }
                }
                return;
            }

            // Cooldown decay multipliers are per-player invariants — computed once per
            // frame, not per slot. Applied as deltaTime × cdrFactor × adrFactor:
            //   • CDR (PlayerCooldownReduction): 0 = none, 0.3 = 30% faster; capped at
            //     0.6 to avoid near-zero cooldowns.
            //   • Adrenaline cooldown multiplier (Round 207 Direction 2) layered on top:
            //     tier 1 low-HP → LowTierCooldownMult (0.80 = -20% cd), tier 2 critical →
            //     CriticalTierCooldownMult (0.50 = -50% cd). A mult < 1 reduces deltaTime
            //     (slower decay) — the opposite of what we want — so we INVERT: 1/mult.
            //     Sentinels: disabled config keeps mult at 1; degenerate mult (<= 0, or the
            //     1/0 = Infinity case) falls back / clamps so decay never goes negative.
            float cdrFactor = 1f + Math.Min(store.PlayerCooldownReduction[playerId], 0.6f);
            float adrMult = store.PlayerAdrenalineCooldownMult[playerId];
            if (adrMult <= 0f) adrMult = 1f;
            float adrFactor = 1f + Math.Min((1f / adrMult) - 1f, 4f);

            int count = store.AbilityCount[playerId];
            for (int slot = 0; slot < count; slot++)
            {
                var inst = store.GetAbility(playerId, slot);
                if (inst.CurrentCooldown > 0f)
                {
                    GameplayAbilityRuntime.TickCooldown(store, playerId, slot, deltaTime * cdrFactor * adrFactor);
                    inst = store.GetAbility(playerId, slot);
                }

                // Auto-cast Passive abilities that are ready
                if (inst.Definition.Activation == AbilityActivation.Passive &&
                    GameplayAbilityRuntime.TryActivate(store, playerId, slot, out _))
                {
                    if (!IsAbilityAllowed(inst.Definition.AreaShape))
                    {
                        _rejectedAbilityCount++;
                        _lastRejectReason = SkillDamageRejectReason.PhaseNotAllowed;
                        if (!_buildPhaseRejectReported)
                        {
                            renderer.Log($"[ABILITY_REJECTED] PhaseNotAllowed skill={inst.Definition.Name}");
                            _buildPhaseRejectReported = true;
                        }
                        continue;
                    }
                    if (!TryActivateCatalogProjection(inst.Definition, out _))
                        ExecuteAbility(inst.Definition, slot);
                }
            }
        }

        /// <summary>
        /// Cast a named ability.  Dispatches to the ability's area-shape handler
        /// so no string-based branching is needed per skill type.
        /// </summary>
        public bool CastSkill(string skillName)
        {
            var catalog = gameConfig?.CompiledCatalog;
            if (catalog != null && catalog.TryResolveAlias(skillName, out var catalogId))
            {
                var activation = TryActivateCatalogAbility(catalogId);
                if (!activation.Accepted)
                    renderer.Log($"[ABILITY_REJECTED] {activation.Reason} skill={skillName}");
                return activation.Accepted;
            }
            if (gameConfig?.StrictCatalogReferences == true)
            {
                _rejectedAbilityCount++;
                renderer.Log($"[ABILITY_REJECTED] UnsupportedDefinition skill={skillName}");
                return false;
            }
            int count = store.AbilityCount[playerId];
            for (int slot = 0; slot < count; slot++)
            {
                var inst = store.GetAbility(playerId, slot);
                if (inst.Definition.Name == skillName)
                {
                    if (!IsAbilityAllowed(inst.Definition.AreaShape))
                    {
                        _rejectedAbilityCount++;
                        _lastRejectReason = SkillDamageRejectReason.PhaseNotAllowed;
                        renderer.Log($"[ABILITY_REJECTED] PhaseNotAllowed skill={skillName}");
                        return false;
                    }
                    if (!GameplayAbilityRuntime.TryActivate(store, playerId, slot, out _))
                    {
                        renderer.Log($"[SKILL] '{skillName}' on cooldown: {inst.CurrentCooldown:F1}s remaining (epsilon-consistent via CanActivate())");
                        return false;
                    }
                    float cost = inst.Definition.Cost;
                    if (cost > 0f && manaSystem != null && !manaSystem.TryConsumeMana(cost))
                    {
                        renderer.Log($"[SKILL] Not enough mana for '{skillName}': need {cost:F0}, have {manaSystem.GetCurrentMana():F0}");
                        return false;
                    }
                    ExecuteAbility(inst.Definition, slot);
                    return true;
                }
            }
            renderer.Log($"[SKILL] Unknown ability: '{skillName}'");
            return false;
        }

        private bool TryActivateCatalogProjection(GameplayAbilityDef definition, out bool accepted)
        {
            accepted = false;
            var catalog = gameConfig?.CompiledCatalog;
            if (catalog != null && catalog.TryResolveAlias(definition.Name, out var abilityId))
            {
                var activation = TryActivateCatalogAbility(abilityId);
                accepted = activation.Accepted;
                if (!accepted)
                    renderer.Log($"[ABILITY_REJECTED] {activation.Reason} skill={definition.Name}");
                return true;
            }
            if (gameConfig?.StrictCatalogReferences != true) return false;
            _rejectedAbilityCount++;
            renderer.Log($"[ABILITY_REJECTED] UnsupportedDefinition skill={definition.Name}");
            return true;
        }

        public static bool IsBuildAllowedAbility(int shape)
        {
            return AbilityPhaseRules.IsBuildAllowed(shape);
        }

        private bool IsAbilityAllowed(int shape) =>
            _phaseContext.AllowsCombat ||
            (_phaseContext.AllowsPreparationResources && IsBuildAllowedAbility(shape));

        /// <summary>
        /// Execute an ability by its definition data — area shape drives the damage pattern.
        /// </summary>
        private bool ExecuteAbility(GameplayAbilityDef def, int slot, AbilityDefinition? catalogDefinition = null)
        {
            float baseDamage = techTreeSystem != null ? techTreeSystem.GetFinalAttackDamage() : store.GetPlayerAttackDamage(playerId);
            float finalDamage = (def.DamageMultiplierAttr < 0) ? baseDamage * def.FixedBaseDamage : baseDamage;
            finalDamage *= _waveDifficultyMult;
            float playerX = store.PositionX[playerId];
            float playerY = store.PositionY[playerId];
            int shape = catalogDefinition.HasValue ? (int)catalogDefinition.Value.Targeting.Shape : def.AreaShape;
            if (!_shapeHandlers.TryGetValue(shape, out var handler))
            {
                _rejectedAbilityCount++;
                _lastRejectReason = SkillDamageRejectReason.UnsupportedCommitBoundary;
                renderer.Log($"[ABILITY_REJECTED] UnsupportedShape shape={shape} skill={def.Name}");
                return false;
            }
            int enemiesHit = handler(new AbilityExecutionContext(def, finalDamage, playerX, playerY));
            if (!GameplayAbilityRuntime.AbilityCommit(store, playerId, slot)) return false;
            renderer.Log($"[SKILL] {def.Name} cast! Hit {enemiesHit} enemies, cooldown: {def.Cooldown}s");
            return true;
        }

        // 保留给旧回放夹具；新激活路径从上方基于目录的方法进入。
        private void ExecuteAbilityLegacySwitch(GameplayAbilityDef def, int slot)
        {
            float baseDamage = techTreeSystem != null ? techTreeSystem.GetFinalAttackDamage() : store.GetPlayerAttackDamage(playerId);
            // Use FixedBaseDamage multiplier when DamageMultiplierAttr == -1
            float finalDamage = (def.DamageMultiplierAttr < 0)
                ? baseDamage * def.FixedBaseDamage
                : baseDamage; // attribute-based not wired up yet
            // Apply wave-based difficulty scaling
            finalDamage *= _waveDifficultyMult;
            // Note: _damageTakenMult and _armorPenetration are cached in SetTurn for completeness.
            // _damageTakenMult defaults to 1.0 and has minimal gameplay impact in benchmarks.
            // _armorPenetration is not applied here to avoid per-enemy serial overhead in SkillSystem.
            // PlayerTowerAttackSystem and TowerAttackSystem already apply armor reduction to
            // player and tower attacks respectively; skill damage relies on those two systems.

            float playerX = store.PositionX[playerId];
            float playerY = store.PositionY[playerId];

            int enemiesHit = 0;

            switch (def.AreaShape)
            {
                case AreaShapeType.Single:
                    enemiesHit = CastSingleTarget(finalDamage, playerX, playerY, def.AreaRadius, def.Name);
                    break;
                case AreaShapeType.Cross: // (+) shape
                    enemiesHit = CastCrossArea(finalDamage, playerX, playerY, def.AreaRadius, def.Name);
                    break;
                case AreaShapeType.Box: // (N×N)
                    enemiesHit = CastBoxArea(finalDamage, playerX, playerY, def.AreaRadius, def.Name);
                    break;
                case AreaShapeType.Circle: // radius-based AOE, for DoT abilities
                    enemiesHit = CastCircleArea(finalDamage, playerX, playerY, def.AreaRadius, def.Name, def);
                    break;
                case AreaShapeType.Chain: // Chain Lightning — O(N) nearest-neighbor chaining
                    enemiesHit = CastChainLightning(finalDamage, playerX, playerY, def.AreaRadius, def.Name);
                    break;
                case AreaShapeType.Heal: // restore player HP
                    CastHeal(def);
                    enemiesHit = 0;
                    break;
                case AreaShapeType.Shield: // apply shield to player
                    CastShield(def);
                    enemiesHit = 0;
                    break;
                case AreaShapeType.Line: // horizontal laser beam along player's Y axis
                    enemiesHit = CastLineArea(finalDamage, playerX, playerY, def.AreaRadius, def.Name);
                    break;
                case AreaShapeType.Freeze: // circle AoE + chance to freeze enemies
                    enemiesHit = CastFreezeArea(finalDamage, playerX, playerY, def.AreaRadius, def.Name, def);
                    break;
                case AreaShapeType.Cone: // directional fan-shaped AoE
                    enemiesHit = CastConeArea(finalDamage, playerX, playerY, def.AreaRadius, def.Name, def.ConeAngleDegrees);
                    break;
                case AreaShapeType.GroundTarget: // player selects a point, AoE around that point
                    enemiesHit = CastGroundTarget(finalDamage, def.AreaRadius, def.Name);
                    break;
                case AreaShapeType.Slow: // circle AoE that slows enemies in radius (move speed reduction, non-freeze)
                    enemiesHit = CastSlowArea(finalDamage, playerX, playerY, def.AreaRadius, def.Name, def);
                    break;
                case AreaShapeType.TimeWarp: // slow/fast game time
                    CastTimeWarp(def);
                    enemiesHit = 0;
                    break;
                case AreaShapeType.Summon: // spawn a player-summoned combat unit
                    CastSummon(def);
                    enemiesHit = 0;
                    break;
                case AreaShapeType.HealingZone: // place a ground healing zone that heals allies in radius
                    enemiesHit = CastHealingZone(def);
                    break;
                case AreaShapeType.Polymorph: // circle AoE that turns enemies into a harmless form (sheep/chicken)
                    enemiesHit = CastPolymorphArea(finalDamage, playerX, playerY, def.AreaRadius, def.Name, def);
                    break;
                case AreaShapeType.TimeRewind: // restore player HP / Mana / Shield from a recent snapshot
                    CastTimeRewind(def);
                    enemiesHit = 0;
                    break;
                case AreaShapeType.ChainHeal: // O(N) nearest-neighbor heal chaining on injured allies
                    enemiesHit = CastChainHealAbility(def, playerX, playerY);
                    break;
                case AreaShapeType.MassResurrect: // AOE revival of un-reanimated corpses within AreaRadius tiles (Round 133)
                    enemiesHit = CastMassResurrect(def, playerX, playerY);
                    break;
                case AreaShapeType.AoeStun: // circle AoE that stuns all enemies in radius (Round 136 Direction 2)
                    enemiesHit = CastAoeStun(playerX, playerY, def.AreaRadius, def.AoeStunDuration, def.Name);
                    break;
                case AreaShapeType.AoeRoot: // circle AoE that roots all enemies in radius (Round 136 Direction 2)
                    enemiesHit = CastAoeRoot(playerX, playerY, def.AreaRadius, def.AoeRootDuration, def.Name);
                    break;
                case AreaShapeType.AoeKnockback: // circle AoE that pushes all enemies radially from player (Round 136 Direction 2)
                    enemiesHit = CastAoeKnockback(playerX, playerY, def.AreaRadius, def.AoeKnockbackForce, def.Name);
                    break;
                default:
                    renderer.Log($"[SKILL] Unknown area shape {def.AreaShape} for ability '{def.Name}'");
                    return;
            }

            // Start cooldown
            GameplayAbilityRuntime.AbilityCommit(store, playerId, slot);

            renderer.Log($"[SKILL] {def.Name} cast! Hit {enemiesHit} enemies, cooldown: {def.Cooldown}s");
        }

        /// <summary>
        /// ChainHeal (AreaShape=17) 的参数推导：base heal = HealPercent × 玩家最大 HP
        /// （与单目标 CastHeal 用 HealPercent 的口径一致）；ShieldAmount / ShieldDuration
        /// 作为每次命中附带的护盾加成。
        /// </summary>
        private int CastChainHealAbility(GameplayAbilityDef def, float playerX, float playerY)
        {
            float casterMaxHp = store.PlayerMaxHealth[playerId] > 0f ? store.PlayerMaxHealth[playerId] : 200f;
            float chainBaseHeal = def.HealPercent > 0f ? casterMaxHp * def.HealPercent : 0f;
            int chainRange = def.AreaRadius > 0 ? def.AreaRadius : (int)CHAIN_HEAL_DEFAULT_RANGE;
            return CastChainHeal(chainBaseHeal, playerX, playerY, chainRange, def.Name, def.ShieldAmount, def.ShieldDuration);
        }

        /// <summary>
        /// MassResurrect (AreaShape=18)：委托 global::BattleSystemECS.Content.Contracts.IResurrectionPort.MassResurrect，其中
        /// hpFraction = HealPercent（0.0-1.0，按尸体最大 HP 复活比例，30% 回退）。
        /// AreaRadius 为 AOE 半径（tiles，3 回退——尸体按世界坐标存放，但 MassResurrect
        /// 是战术 AOE，小半径 3-5 tiles 是合理量级）。
        /// </summary>
        private int CastMassResurrect(GameplayAbilityDef def, float playerX, float playerY)
        {
            if (necromancerSystem == null)
            {
                renderer.Log($"[SKILL] '{def.Name}' failed: global::BattleSystemECS.Content.Contracts.IResurrectionPort not injected into SkillSystem");
                return 0;
            }
            float massRadius = def.AreaRadius > 0 ? def.AreaRadius : 3f; // default 3 tiles
            float hpFraction = def.HealPercent > 0f ? def.HealPercent : 0.3f; // 30% HP fallback
            // Ensure the necromancer system has an up-to-date sim time for corpse age gating.
            // IResurrectionPort.SetTurn 签名为 (int turn, float simTime)；AIGroup
            // wires it before SkillSystem runs (Necromancer sits in AI group, Skill in SkillBuff
            // group — so the simTime is already current). We re-set it here as a defensive
            // measure in case the order ever changes, using the same turn-based time proxy
            // convention the AIGroup uses (pass `turn` for both args — turn as time).
            necromancerSystem.SetTurn(_currentTurn, _currentTurn);
            return necromancerSystem.MassResurrect(playerId, playerX, playerY, massRadius, hpFraction);
        }

        private int CastSingleTarget(float finalDamage, float playerX, float playerY, int range, string name)
        {
            // _activeEnemyList is guaranteed non-null after SetTurn(); no fallback needed
            if (_activeEnemyList == null) return 0;

            // Phase 1: collect candidates in range (lock-free, threshold-gated)
            CollectCircleHits(playerX, playerY, range * range);

            // Serial phase: find global closest (recompute distSq on the filtered set)
            int closestEnemyId = -1;
            float closestDistSq = float.MaxValue;
            foreach (int enemyId in _mergedHits)
            {
                float dx = store.PositionX[enemyId] - playerX;
                float dy = store.PositionY[enemyId] - playerY;
                float distSq = dx * dx + dy * dy;
                if (distSq < closestDistSq)
                {
                    closestDistSq = distSq;
                    closestEnemyId = enemyId;
                }
            }

            if (closestEnemyId != -1)
            {
                float enemyX = store.PositionX[closestEnemyId];
                float enemyY = store.PositionY[closestEnemyId];

                QueueSkillDamage(closestEnemyId, finalDamage);

                renderer.Log($"[SKILL] {name} queued damage for enemy {closestEnemyId} at ({enemyX:F0},{enemyY:F0}), dmg: {finalDamage:F1}");
                return 1;
            }
            return 0;
        }

        /// <summary>
        /// 统一的命中收集驱动：低于阈值纯串行；否则按批分区并行（每批独占缓冲、无锁），
        /// Parallel.For 全屏障后按批序合并进 results（敌人索引序，确定性）。
        /// filter 只允许读 store 并把命中 id 写入自己的 hits 缓冲，不得做任何其他共享写。
        /// </summary>
        private void CollectHits(List<int> activeEnemyIds, Action<int, List<int>> filter, List<int> results)
        {
            results.Clear();
            int count = activeEnemyIds.Count;
            if (count < ParallelMinEnemies)
            {
                for (int i = 0; i < count; i++)
                {
                    filter(activeEnemyIds[i], results);
                }
                return;
            }
            int numBatches = (count + ParallelBatchSize - 1) / ParallelBatchSize;
            if (_hitBatchBuffers.Length < numBatches)
            {
                var grown = new List<int>[numBatches];
                Array.Copy(_hitBatchBuffers, grown, _hitBatchBuffers.Length);
                _hitBatchBuffers = grown;
            }
            // 确保本帧用到的每个槽位都已实例化（初始数组与扩容后的旧槽位可能是 null）
            for (int b = 0; b < numBatches; b++)
            {
                if (_hitBatchBuffers[b] == null)
                    _hitBatchBuffers[b] = new List<int>(ParallelBatchSize);
            }
            Parallel.For(0, numBatches, ParallelOptionsCache.HotPath, batchIdx =>
            {
                var batchBuffer = _hitBatchBuffers[batchIdx];
                batchBuffer.Clear();
                int start = batchIdx * ParallelBatchSize;
                int end = Math.Min(start + ParallelBatchSize, count);
                for (int i = start; i < end; i++)
                {
                    filter(activeEnemyIds[i], batchBuffer);
                }
            });
            for (int b = 0; b < numBatches; b++)
            {
                var batchBuffer = _hitBatchBuffers[b];
                for (int k = 0; k < batchBuffer.Count; k++)
                {
                    results.Add(batchBuffer[k]);
                }
            }
        }

        /// <summary>
        /// 圆形 AoE 命中收集 —— 所有圆形类 Cast 方法（Single/Circle/Freeze/Slow/Polymorph/
        /// GroundTarget/AoeStun/AoeRoot/AoeKnockback）共用的过滤谓词：存活、非玩家、
        /// 位于 (cx,cy) 半径平方 radiusSq 之内。命中写入 _mergedHits（敌人索引序）。
        /// 调用前需已确认 _activeEnemyList 非空。
        /// </summary>
        private void CollectCircleHits(float cx, float cy, int radiusSq)
        {
            CollectHits(_activeEnemyList, (enemyId, hits) =>
            {
                if (enemyId == playerId) return;
                if (store.GetEnemyHealth(enemyId) <= 0f) return;
                float dx = store.PositionX[enemyId] - cx;
                float dy = store.PositionY[enemyId] - cy;
                if (dx * dx + dy * dy <= radiusSq) hits.Add(enemyId);
            }, _mergedHits);
        }

        /// <summary>
        /// 串行段：对 _mergedHits 中每个敌人入队等额伤害（verbose 时逐敌打命中日志）。
        /// 返回命中数。
        /// </summary>
        private int QueueDamageForMergedHits(float finalDamage, string name, bool verbose)
        {
            int hitCount = 0;
            foreach (int enemyId in _mergedHits)
            {
                QueueSkillDamage(enemyId, finalDamage);
                if (verbose)
                {
                    float enemyX = store.PositionX[enemyId];
                    float enemyY = store.PositionY[enemyId];
                    renderer.Log($"[SKILL] {name} queued damage for enemy {enemyId} at ({enemyX:F0},{enemyY:F0}), dmg: {finalDamage:F1}");
                }
                hitCount++;
            }
            return hitCount;
        }

        private int CastCrossArea(float finalDamage, float playerX, float playerY, int radius, string name)
        {
            // _activeEnemyList is guaranteed non-null after SetTurn(); no fallback needed
            if (_activeEnemyList == null) return 0;
            var activeEnemyIds = _activeEnemyList;

            // Phase 1: collect all enemies in cross area (lock-free, threshold-gated)
            CollectHits(activeEnemyIds, (enemyId, hits) =>
            {
                if (enemyId == playerId) return;
                float enemyHealth = store.GetEnemyHealth(enemyId);
                if (enemyHealth <= 0f) return;

                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];

                // Check cross shape: all points with |dx| <= radius on horizontal arm
                // or |dy| <= radius on vertical arm
                bool inHorizontalArm = Math.Abs(enemyY - playerY) < 0.5f && Math.Abs(enemyX - playerX) <= radius;
                bool inVerticalArm = Math.Abs(enemyX - playerX) < 0.5f && Math.Abs(enemyY - playerY) <= radius;

                if (inHorizontalArm || inVerticalArm)
                {
                    hits.Add(enemyId);
                }
            }, _mergedHits);

            // Serial phase: apply damage
            return QueueDamageForMergedHits(finalDamage, name, verbose: true);
        }

        private int CastBoxArea(float finalDamage, float playerX, float playerY, int range, string name)
        {
            // _activeEnemyList is guaranteed non-null after SetTurn(); no fallback needed
            if (_activeEnemyList == null) return 0;
            var activeEnemyIds = _activeEnemyList;

            float xMin = playerX - (float)range;
            float xMax = playerX + (float)range;
            float yMin = playerY - (float)range;
            float yMax = playerY + (float)range;

            // Phase 1: collect all enemies in box area (lock-free, threshold-gated)
            CollectHits(activeEnemyIds, (enemyId, hits) =>
            {
                if (enemyId == playerId) return;
                float enemyHealth = store.GetEnemyHealth(enemyId);
                if (enemyHealth <= 0f) return;

                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];

                if (enemyX >= xMin && enemyX <= xMax &&
                    enemyY >= yMin && enemyY <= yMax)
                {
                    hits.Add(enemyId);
                }
            }, _mergedHits);

            // Serial phase: apply damage
            return QueueDamageForMergedHits(finalDamage, name, verbose: true);
        }


        private int CastCircleArea(float finalDamage, float playerX, float playerY, int radius, string name, GameplayAbilityDef def)
        {
            // _activeEnemyList is guaranteed non-null after SetTurn(); no fallback needed
            if (_activeEnemyList == null) return 0;

            CollectCircleHits(playerX, playerY, radius * radius);

            // Serial phase: apply DoT effect to each enemy
            int hitCount = 0;
            foreach (int enemyId in _mergedHits)
            {
                if (dotSystem != null && def.HasDot)
                {
                    if (!TryApplyCatalogPeriodicDot(enemyId, def))
                    {
                        var dotDef = def.DotStackingBehavior != StackingBehavior.None
                            ? GameplayEffectDef.Periodic(
                                $"DoT:{def.Name}",
                                -1,
                                def.DotDamagePerTick,
                                def.DotDuration,
                                def.DotTickInterval,
                                def.DotStackingBehavior,
                                def.DotMaxStacks)
                            : GameplayEffectDef.Periodic(
                                $"DoT:{def.Name}",
                                -1,
                                def.DotDamagePerTick,
                                def.DotDuration,
                                def.DotTickInterval);
                        dotSystem.ApplyDot(enemyId, dotDef);
                    }
                }
                else
                {
                    // Fallback: immediate damage if no dotSystem wired
                    QueueSkillDamage(enemyId, finalDamage);
                }
                hitCount++;
            }
            return hitCount;
        }

        private bool TryApplyCatalogPeriodicDot(int enemyId, GameplayAbilityDef def)
        {
            var catalog = gameConfig?.CompiledCatalog;
            if (catalog == null || !catalog.TryResolveAlias(def.Name, out var abilityId) ||
                !catalog.TryGetAbility(abilityId, out var ability))
                return false;
            var source = store.GetEntityHandle(playerId);
            var target = store.GetEntityHandle(enemyId);
            if (!source.IsValid || !target.IsValid) return false;
            bool applied = false;
            for (int i = 0; i < ability.Effects.Count; i++)
            {
                if (!catalog.TryGetEffect(ability.Effects[i], out var effect) ||
                    effect.Type != EffectType.Periodic || effect.Modifiers.Count != 0)
                    continue;
                store.GameplayEffectsRuntime.TryApply(effect.Id, effect, source, target, out _,
                    snapshot: def.DotDamagePerTick, ownerPlayerId: playerId);
                applied = true;
            }
            return applied;
        }

        /// <summary>
        /// Chain Lightning: O(N) nearest-neighbor chaining.
        /// Hits primary target, then up to 3 additional targets, each at 70% of previous damage.
        /// Serial implementation — no parallel needed (chain order is inherently sequential).
        /// </summary>
        private int CastChainLightning(float baseDamage, float playerX, float playerY, int range, string name)
        {
            if (_activeEnemyList == null) return 0;
            var activeEnemyIds = _activeEnemyList;
            int count = activeEnemyIds.Count;

            int rangeSq = range * range;
            // Ensure pooled buffer is large enough
            if (_chainHitBufferSize < count)
            {
                _chainHitBuffer = new bool[count];
                _chainHitBufferSize = count;
            }
            else
            {
                Array.Clear(_chainHitBuffer, 0, _chainHitBufferSize);
                _chainHitBufferSize = count;
            }

            float currentDamage = baseDamage;
            int totalHit = 0;
            float originX = playerX;
            float originY = playerY;

            for (int hop = 0; hop < CHAIN_LIGHTNING_MAX_TARGETS; hop++)
            {
                int bestIdx = -1;
                float bestDistSq = float.MaxValue;

                for (int i = 0; i < count; i++)
                {
                    int enemyId = activeEnemyIds[i];
                    if (enemyId == playerId) continue;
                    if (_chainHitBuffer[i]) continue;
                    float health = store.EnemyHealth[enemyId];
                    if (health <= 0f) continue;

                    float ex = store.PositionX[enemyId];
                    float ey = store.PositionY[enemyId];
                    float dx = ex - originX;
                    float dy = ey - originY;
                    float distSq = dx * dx + dy * dy;

                    if (distSq <= rangeSq && distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestIdx = i;
                    }
                }

                if (bestIdx == -1) break;

                _chainHitBuffer[bestIdx] = true;
                int bestId = activeEnemyIds[bestIdx];
                QueueSkillDamage(bestId, currentDamage);
                totalHit++;

                float bestX = store.PositionX[bestId];
                float bestY = store.PositionY[bestId];
                renderer.Log($"[SKILL] {name} chain #{hop + 1} → enemy {bestId} at ({bestX:F0},{bestY:F0}), dmg: {currentDamage:F1}");

                originX = bestX;
                originY = bestY;
                currentDamage *= CHAIN_LIGHTNING_DAMAGE_DECAY;
            }

            return totalHit;
        }

        /// <summary>
        /// Chain Heal: O(N) nearest-neighbor heal chaining on injured allies (Round 131).
        /// Targets players in the same arena. Primary target = most-injured friendly (max HP deficit),
        /// ties broken by distance. Caster is excluded from the friendly pool so the heal always
        /// jumps to OTHER injured allies first (caster can self-heal via the default Guardian Heal
        /// spell — chain heal is explicitly a "heal teammates" skill).
        /// Each hop deals 50% of the previous heal (CHAIN_HEAL_DECAY), up to 3 chain targets.
        /// Dead or full-HP friendlies are skipped (no overheal). Serial implementation — no parallel needed.
        /// On heal hit, also applies a small shield bonus (shieldPerHit > 0) for survivability,
        /// with the shield persisting for shieldDuration seconds.
        /// </summary>
        public int CastChainHealPublic(float baseHeal, float playerX, float playerY, int range, string name, float shieldPerHit, float shieldDuration) =>
            IsAbilityAllowed(AreaShapeType.ChainHeal)
                ? CastChainHeal(baseHeal, playerX, playerY, range, name, shieldPerHit, shieldDuration)
                : RejectAbility(name);

        private int CastChainHeal(float baseHeal, float playerX, float playerY, int range, string name, float shieldPerHit, float shieldDuration)
        {
            if (baseHeal <= 0f) return 0;
            // Build friendly pool (players 0..MAX_PLAYERS) — pool is tiny (≤10) so no need to cache
            int friendlyCount = ComponentStore.MAX_PLAYERS;
            // Ensure pooled buffer is large enough for the friendly pool (cheap, MAX_PLAYERS=10)
            if (_chainHealHitBufferSize < friendlyCount)
            {
                _chainHealHitBuffer = new bool[friendlyCount];
                _chainHealHitBufferSize = friendlyCount;
            }
            else
            {
                Array.Clear(_chainHealHitBuffer, 0, _chainHealHitBufferSize);
                _chainHealHitBufferSize = friendlyCount;
            }

            float rangeSq = (float)range * range;
            float currentHeal = baseHeal;
            int totalHit = 0;
            float originX = playerX;
            float originY = playerY;

            for (int hop = 0; hop < CHAIN_HEAL_MAX_TARGETS; hop++)
            {
                int bestIdx = -1;
                float bestDistSq = float.MaxValue;
                // Pick most-injured friendly within range (max HP deficit = max(MaxHP - CurrentHP))
                float bestDeficit = 0f; // 0 = skip; only consider friendlies with positive deficit AND in range

                for (int i = 0; i < friendlyCount; i++)
                {
                    if (i == playerId) continue;     // exclude caster — chain heal targets OTHER allies
                    if (_chainHealHitBuffer[i]) continue;
                    float maxHp = store.PlayerMaxHealth[i];
                    if (maxHp <= 0f) continue;       // invalid / uninitialized player slot
                    float curHp = store.PlayerCurrentHealth[i];
                    if (curHp <= 0f) continue;       // dead
                    float deficit = maxHp - curHp;
                    if (deficit <= 0.001f) continue; // already full HP

                    float fx = store.PositionX[i];
                    float fy = store.PositionY[i];
                    float dx = fx - originX;
                    float dy = fy - originY;
                    float distSq = dx * dx + dy * dy;

                    if (distSq > rangeSq) continue;

                    // Pick max-deficit, ties broken by nearest
                    bool isBetter = deficit > bestDeficit ||
                                    (deficit == bestDeficit && distSq < bestDistSq);
                    if (isBetter)
                    {
                        bestDeficit = deficit;
                        bestDistSq = distSq;
                        bestIdx = i;
                    }
                }

                if (bestIdx == -1) break;

                _chainHealHitBuffer[bestIdx] = true;
                int friendlyId = bestIdx;

                // Apply heal: clamp to MaxHealth (no overheal)
                float friendlyMaxHp = store.PlayerMaxHealth[friendlyId];
                float newHp = store.PlayerCurrentHealth[friendlyId] + currentHeal;
                if (newHp > friendlyMaxHp) newHp = friendlyMaxHp;
                store.ApplyPlayerResourceAuthority(friendlyId, friendlyId, new Core.GAS.AttributeKey(3), currentHeal);

                // Apply shield bonus if requested (small bonus, 0 = no shield)
                if (shieldPerHit > 0f)
                {
                    store.ApplyPlayerShield(friendlyId, shieldPerHit, shieldDuration);
                }

                totalHit++;
                float friendlyX = store.PositionX[friendlyId];
                float friendlyY = store.PositionY[friendlyId];
                renderer.Log($"[SKILL] {name} chain #{hop + 1} → player {friendlyId} at ({friendlyX:F0},{friendlyY:F0}), heal: {currentHeal:F1}, shield: {shieldPerHit:F0}");

                // Chain origin moves to the healed friendly (next hop searches around the heal target)
                originX = friendlyX;
                originY = friendlyY;
                currentHeal *= CHAIN_HEAL_DECAY;
            }

            return totalHit;
        }

        /// <summary>
        /// Line/Ray AreaShape: hits all enemies sharing the player's Y coordinate
        /// within range (horizontal laser beam). Extensible to vertical via parameter.
        /// </summary>
        private int CastLineArea(float finalDamage, float playerX, float playerY, int range, string name)
        {
            if (_activeEnemyList == null) return 0;
            var activeEnemyIds = _activeEnemyList;

            // Phase 1: collect all enemies on same Y row within range (lock-free, threshold-gated)
            CollectHits(activeEnemyIds, (enemyId, hits) =>
            {
                if (enemyId == playerId) return;
                float enemyHealth = store.GetEnemyHealth(enemyId);
                if (enemyHealth <= 0f) return;

                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];

                // Horizontal line: same Y (dy ≈ 0), within range on X axis
                bool onSameRow = Math.Abs(enemyY - playerY) < 0.5f;
                bool withinRange = Math.Abs(enemyX - playerX) <= range;

                if (onSameRow && withinRange)
                {
                    hits.Add(enemyId);
                }
            }, _mergedHits);

            // Serial phase: apply damage
            return QueueDamageForMergedHits(finalDamage, name, verbose: true);
        }

        /// <summary>
        /// 死亡入队 —— QueueEnemyDeath 交给帧末 ResolveEnemiesKilledThisFrame() 统一结算。
        /// </summary>
        private void HandleKill(int enemyId)
        {
            store.QueueEnemyDeath(enemyId, playerId);
            renderer.Log($"[SKILL] Killed enemy {enemyId}");
        }

        /// <summary>
        /// Freeze AreaShape: circle AoE that damages and can freeze enemies.
        /// Reuses the shared circle range query, then applies freeze via ApplyEnemyFreeze
        /// with probability-based roll (FreezeChance). Follows two-phase pattern.
        /// </summary>
        private int CastFreezeArea(float finalDamage, float playerX, float playerY,
            int radius, string name, GameplayAbilityDef def)
        {
            if (_activeEnemyList == null) return 0;

            CollectCircleHits(playerX, playerY, radius * radius);

            // Serial phase: apply damage + freeze roll（命中序 = 敌人索引序，RNG 逐敌顺序与串行实现一致）
            int hitCount = 0;
            foreach (int enemyId in _mergedHits)
            {
                QueueSkillDamage(enemyId, finalDamage);

                if (def.FreezeDuration > 0f && def.FreezeChance > 0f)
                {
                    float roll = (float)Rng.Shared.NextDouble();
                    if (roll < def.FreezeChance)
                    {
                        int freezeTurns = Math.Max(1, (int)Math.Ceiling(def.FreezeDuration * (1f - _enemyFreezeResistance)));
                        store.ApplyEnemyFreeze(enemyId, freezeTurns);
                        renderer.Log($"[SKILL] {name} froze enemy {enemyId} for {freezeTurns} turns");
                    }
                }
                hitCount++;
            }
            return hitCount;
        }

        /// <summary>
        /// Slow AreaShape: circle AoE that slows enemies in radius (non-freeze, move speed reduction).
        /// Reuses circular range query + applies slow via ApplySlow with factor + duration.
        /// Follows two-phase pattern (parallel collect → serial apply).
        /// </summary>
        private int CastSlowArea(float finalDamage, float playerX, float playerY,
            int radius, string name, GameplayAbilityDef def)
        {
            if (_activeEnemyList == null) return 0;

            CollectCircleHits(playerX, playerY, radius * radius);

            // Serial phase: apply damage and slow effect
            int hitCount = 0;
            foreach (int enemyId in _mergedHits)
            {
                QueueSkillDamage(enemyId, finalDamage);

                if (def.SlowAmount > 0f && def.SlowDuration > 0f)
                {
                    // Apply slow factor (e.g., 0.5 = 50% speed) + duration in turns
                    int slowTurns = Math.Max(1, (int)Math.Ceiling(def.SlowDuration * (1f - _enemySlowResistance)));
                    store.ApplySlow(enemyId, def.SlowAmount, slowTurns);
                    renderer.Log($"[SKILL] {name} slowed enemy {enemyId} by {def.SlowAmount:F2}x for {slowTurns} turns");
                }
                hitCount++;
            }
            return hitCount;
        }

        /// <summary>
        /// Polymorph AreaShape: circle AoE that turns enemies into a harmless form (sheep/chicken).
        /// Reuses the same parallel-collect / serial-apply pattern as CastSlowArea. After the
        /// parallel hit-list is finalized, applies damage and ApplyPolymorph serially. While
        /// polymorphed, enemies cannot attack (BT short-circuited in EnemyAISystem) and can take
        /// extra damage per def.PolymorphDamageTakenMultiplier (e.g. 1.5 = +50% damage taken).
        /// </summary>
        private int CastPolymorphArea(float finalDamage, float playerX, float playerY,
            int radius, string name, GameplayAbilityDef def)
        {
            if (_activeEnemyList == null) return 0;

            CollectCircleHits(playerX, playerY, radius * radius);

            // Serial phase: apply damage and polymorph effect
            int hitCount = 0;
            foreach (int enemyId in _mergedHits)
            {
                QueueSkillDamage(enemyId, finalDamage);

                if (def.PolymorphDuration > 0f)
                {
                    int polyTurns = Math.Max(1, (int)Math.Ceiling(def.PolymorphDuration));
                    store.ApplyPolymorph(enemyId, polyTurns, def.PolymorphDamageTakenMultiplier);
                    renderer.Log($"[SKILL] {name} polymorphed enemy {enemyId} for {polyTurns} turns (×{def.PolymorphDamageTakenMultiplier:F2} dmg taken)");
                }
                hitCount++;
            }
            return hitCount;
        }

        private void CastHeal(GameplayAbilityDef def)
        {
            if (dotSystem == null)
            {
                renderer.Log($"[SKILL] {def.Name}: dotSystem not wired, cannot heal");
                return;
            }
            dotSystem.HealPlayer(def.HealPercent);
            renderer.Log($"[SKILL] {def.Name} cast — HealPercent={def.HealPercent:F2} ({def.HealPercent * 100:F0}% max HP)");
        }

        private void CastShield(GameplayAbilityDef def)
        {
            store.ApplyPlayerShield(playerId, def.ShieldAmount, def.ShieldDuration);
            renderer.Log($"[SKILL] {def.Name} cast — Shield={def.ShieldAmount:F0}, Duration={def.ShieldDuration:F0}s");
        }

        /// <summary>
        /// TimeWarp AreaShape: applies GlobalTimeScale (slow/fast time) + GlobalTimeScaleDuration.
        /// The time scale is stored in ShieldAmount (e.g., 0.3 = 30% speed = bullet time).
        /// The duration is stored in ShieldDuration (seconds remaining).
        /// </summary>
        private void CastTimeWarp(GameplayAbilityDef def)
        {
            float timeScale = def.ShieldAmount; // 0.3 = bullet time, 2.0 = fast forward
            float duration = def.ShieldDuration; // seconds

            store.GlobalTimeScale[playerId] = timeScale;
            store.GlobalTimeScaleDuration[playerId] = duration;

            string mode = timeScale < 1f ? "BULLET TIME" : "FAST FORWARD";
            renderer.Log($"[SKILL] {def.Name} cast — {mode} {timeScale:F1}x speed for {duration:F0}s");
        }

        /// <summary>
        /// Summon AreaShape: spawns a player-summoned combat unit at the player's position.
        /// The SummonDef ID is carried in def.SummonDefId.
        /// </summary>
        private void CastSummon(GameplayAbilityDef def)
        {
            if (summonSystem == null)
            {
                renderer.Log($"[SUMMON] global::BattleSystemECS.Content.Contracts.ISummonCommandPort not available — cannot cast '{def.Name}'");
                return;
            }

            string summonDefId = def.SummonDefId;
            if (string.IsNullOrEmpty(summonDefId))
            {
                renderer.Log($"[SUMMON] Summon ability '{def.Name}' has no SummonDefId configured");
                return;
            }

            // Find the summon definition in gameConfig
            var summonDef = gameConfig.Summons.Find(s => s.Id == summonDefId);
            if (summonDef == null)
            {
                renderer.Log($"[SUMMON] SummonDef '{summonDefId}' not found in game config");
                return;
            }

            int unitId = summonSystem.SummonUnit(playerId, summonDef);
            if (unitId >= 0)
            {
                renderer.Log($"[SUMMON] {def.Name} cast — spawned unit (ID: {unitId})");
            }
        }

        /// <summary>
        /// HealingZone AreaShape: places a ground healing zone at the player's position.
        /// The zone heals allies (player + summoned units) within its radius over time.
        /// Duration and heal rate come from def.Cooldown (duration) and def.HealPercent (hps).
        /// 使用 IHealingZoneCommandPort.AddHealingZone()；内部对应 CorpseEffect 类型 4。
        /// </summary>
        private int CastHealingZone(GameplayAbilityDef def)
        {
            if (healingZoneSystem == null)
            {
                renderer.Log($"[HEALZONE] global::BattleSystemECS.Content.Contracts.IHealingZoneCommandPort not available — cannot cast '{def.Name}'");
                return 0;
            }

            float posX = store.PositionX[playerId];
            float posY = store.PositionY[playerId];
            int radius = def.AreaRadius;
            float duration = def.Cooldown > 0f ? def.Cooldown : 10f; // duration = cooldown field
            float healPerSec = def.HealPercent; // HealPercent field stores HPS for zone abilities

            int zoneId = healingZoneSystem.AddHealingZone(posX, posY, radius, duration, healPerSec);

            if (zoneId >= 0)
            {
                renderer.Log($"[HEALZONE] {def.Name} cast — zone at ({posX:F1},{posY:F1}), radius={radius}, duration={duration}s, hps={healPerSec}");
                return 1; // zones placed, not enemies hit
            }
            else
            {
                renderer.Log($"[HEALZONE] {def.Name} failed — healing zone pool full");
                return 0;
            }
        }

        /// <summary>
        /// GroundTarget AreaShape: player selects a point on the map (via stored target position),
        /// then AoE damages all enemies within radius of that point.
        /// For benchmark purposes, defaults to player's own position as target.
        /// Follows two-phase pattern (parallel collect → serial apply).
        /// </summary>
        private int CastGroundTarget(float finalDamage, int radius, string name)
        {
            if (_activeEnemyList == null) return 0;

            // For benchmark compatibility, use player's current position as target.
            // In real gameplay, this would read a stored mouse-click target coordinate.
            float targetX = store.PositionX[playerId];
            float targetY = store.PositionY[playerId];

            CollectCircleHits(targetX, targetY, radius * radius);

            // Serial phase: apply damage and count
            return QueueDamageForMergedHits(finalDamage, name, verbose: false);
        }

        /// <summary>
        /// Cone AreaShape: directional fan-shaped AoE (e.g. Dragon Breath, flame thrower).
        /// Player faces "up" (negative Y direction). Fan angle is controlled by coneAngleDegrees.
        /// AreaRadius controls max range; ConeAngleDegrees in skill config controls cone angle in degrees.
        /// Reuses circular range query + cosine-based angle filtering.
        /// </summary>
        private int CastConeArea(float finalDamage, float playerX, float playerY, int range, string name, float coneAngleDegrees = 60.0f)
        {
            if (_activeEnemyList == null) return 0;
            var activeEnemyIds = _activeEnemyList;

            int radiusSq = range * range;
            // coneAngleDegrees: total fan angle; half-angle used for cosine threshold
            double halfConeAngle = coneAngleDegrees * (Math.PI / 180.0) / 2.0;
            double cosThreshold = Math.Cos(halfConeAngle);

            // Direction: player faces "up" (negative Y in world space)
            const double dirX = 0.0;
            const double dirY = -1.0;

            CollectHits(activeEnemyIds, (enemyId, hits) =>
            {
                if (enemyId == playerId) return;
                float enemyHealth = store.GetEnemyHealth(enemyId);
                if (enemyHealth <= 0f) return;

                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];

                double dx = (double)enemyX - (double)playerX;
                double dy = (double)enemyY - (double)playerY;
                double distSq = dx * dx + dy * dy;

                if (distSq > radiusSq) return;

                // Normalize direction to enemy
                double len = Math.Sqrt(distSq);
                if (len < 0.0001) return; // too close to player center, skip

                double toEnemyNormX = dx / len;
                double toEnemyNormY = dy / len;

                // Dot product with cone direction: cos(angle) = dot(dir, toEnemy)
                double dot = toEnemyNormX * dirX + toEnemyNormY * dirY;

                if (dot >= cosThreshold)
                {
                    hits.Add(enemyId);
                }
            }, _mergedHits);

            // Serial phase: apply damage
            return QueueDamageForMergedHits(finalDamage, name, verbose: true);
        }

        /// <summary>
        /// Serial-phase damage resolution. Called from GameManager.Run() after all attack systems
        /// have finished their Update(), before ResolveEnemiesKilledThisFrame().
        /// Follows the two-phase pattern: parallel collect → serial apply.
        /// </summary>
        public void ResolveSkillDamage()
        {
            if (!_phaseContext.AllowsCombat)
            {
                RejectPendingSkillDamage(SkillDamageRejectReason.UnsupportedCommitBoundary);
                return;
            }
            // Phase 2 (serial): ping-pong swap — read from current bag, clear alternate for next frame
            int readIdx = _skillDamageQueueIdx;
            int writeIdx = 1 - _skillDamageQueueIdx;
            _skillDamageQueueIdx = writeIdx;
            _skillDamageQueue[writeIdx].Clear();
            int consumed = _skillDamageQueue[readIdx].Count;
            _pendingSkillDamageCount -= consumed;
            _consumedSkillDamageCount += consumed;
            foreach (var request in _skillDamageQueue[readIdx])
            {
                if (!store.TryResolve(request.Target, out int enemyId, out _)) continue;
                if (enemyId < 0 || enemyId >= ComponentStore.MAX_ENTITIES) continue;
                if (store.EnemyHealth[enemyId] <= 0f) continue; // already dead this frame
                // Invulnerability check: skip damage if enemy is invulnerable
                if (store.EnemyIsInvulnerable[enemyId]) continue;

                var result = store.DamageResolver.TryApply(request);
                if (result.Accepted && !result.Deferred && store.EnemyHealth[enemyId] <= 0f)
                    HandleKill(enemyId);
            }
            _skillDamageQueue[readIdx].Clear();
        }

        private void QueueSkillDamage(int enemyId, float damage)
        {
            if (enemyId < 0 || enemyId >= ComponentStore.MAX_ENTITIES || damage <= 0f) return;
            var source = store.GetEntityHandle(store.PlayerEntityId);
            var target = store.GetEntityHandle(enemyId);
            var request = new Core.GAS.DamageRequest(source, target, damage, Components.DamageType.True,
                Components.ElementType.None, Core.GAS.DamageFlags.None, Core.GAS.DamageAmountStage.Raw,
                Core.GAS.DamageCommitBoundary.GameplayResolve, store.AllocateGameplaySequence(enemyId),
                ownerPlayerId: playerId);
            _skillDamageQueue[_skillDamageQueueIdx].Add(request);
            _pendingSkillDamageCount++;
        }

        /// <summary>
        /// BuildPhase 的明确边界：技能战斗请求拒绝并消费，避免泄漏到下一 Wave。
        /// </summary>
        public int RejectPendingSkillDamage() => RejectPendingSkillDamage(SkillDamageRejectReason.PhaseNotAllowed);

        public int RejectPendingSkillDamage(SkillDamageRejectReason reason)
        {
            int rejected = _skillDamageQueue[0].Count + _skillDamageQueue[1].Count;
            if (rejected > 0)
                _lastRejectReason = reason;
            _skillDamageQueue[0].Clear();
            _skillDamageQueue[1].Clear();
            _pendingSkillDamageCount = 0;
            _rejectedSkillDamageCount += rejected;
            return rejected;
        }

        /// <summary>
        /// Auto-cast the first available ability (for benchmark compatibility).
        /// </summary>
        public void AutoCastBestSkill()
        {
            int count = store.AbilityCount[playerId];
            for (int slot = 0; slot < count; slot++)
            {
                var inst = store.GetAbility(playerId, slot);
                if (GameplayAbilityRuntime.TryActivate(store, playerId, slot, out _))
                {
                    if (!IsAbilityAllowed(inst.Definition.AreaShape))
                    {
                        _rejectedAbilityCount++;
                        _lastRejectReason = SkillDamageRejectReason.PhaseNotAllowed;
                        renderer.Log($"[ABILITY_REJECTED] PhaseNotAllowed skill={inst.Definition.Name}");
                        return;
                    }
                    if (!TryActivateCatalogProjection(inst.Definition, out _))
                        ExecuteAbility(inst.Definition, slot);
                    return;
                }
            }
        }

        /// <summary>
        /// TimeRewind AreaShape (16): restore player HP / Mana / Shield from a recent snapshot
        /// 由 ISnapshotRestorePort 捕获；回溯时长编码在
        /// <c>def.HealPercent</c> (treated as seconds, e.g. 3.0 = 3 seconds back).
        /// Falls back to the system default (3.0s) when HealPercent is unset.
        /// </summary>
        private void CastTimeRewind(GameplayAbilityDef def)
        {
            if (timeRewindSystem == null)
            {
                renderer.Log($"[TIMEREWIND] global::BattleSystemECS.Content.Contracts.ISnapshotRestorePort not available — cannot cast '{def.Name}'");
                return;
            }

            float secondsBack = def.HealPercent > 0f ? def.HealPercent : ComponentStore.DEFAULT_REWIND_SECONDS;
            float actual = timeRewindSystem.RestoreFromSnapshot(playerId, secondsBack);
            if (actual < 0f)
            {
                renderer.Log($"[TIMEREWIND] {def.Name} cast — no snapshot data yet (wait ~{ComponentStore.SNAPSHOT_INTERVAL:F2}s after game start)");
                return;
            }

            renderer.Log($"[TIMEREWIND] {def.Name} cast — rolled state back {actual:F2}s " +
                         $"(HP={store.PlayerCurrentHealth[playerId]:F1}/{store.PlayerMaxHealth[playerId]:F1}, " +
                         $"Mana={store.PlayerMana[playerId]:F1}, Shield={store.PlayerShield[playerId]:F1})");
        }

        // ===================================================================
        // Round 136 Direction 2 — AOE CC group control (群体禁锢/击晕)
        //   * AoeStun (AreaShape=19):  circle AoE that stuns every enemy in radius
        //   * AoeRoot (AreaShape=20):  circle AoE that roots every enemy in radius
        //   * AoeKnockback (AreaShape=21): circle AoE that pushes every enemy radially from player
        //
        // All three follow the same pattern: parallel collect IDs in radius → serial apply effect.
        // No damage is dealt (these are pure CC skills). Per-enemy CC resistance + CC immunity
        // mask + EnemyIsUnstoppable are all honored via the underlying store.Apply* methods.
        // ===================================================================

        /// <summary>
        /// AoeStun (AreaShape=19): circle AoE stun. Returns number of enemies hit.
        /// Skips enemies with HP ≤ 0 (dead), and lets the per-enemy helper apply CC immunity,
        /// resistance, and refresh-or-set semantics. No damage is applied.
        /// </summary>
        public int CastAoeStun(float centerX, float centerY, int radius, float duration, string name)
        {
            if (!_phaseContext.AllowsCombat) return RejectAbility(name);
            if (_activeEnemyList == null) return 0;
            if (radius <= 0 || duration <= 0f) return 0;

            int stunTurns = Math.Max(1, (int)Math.Ceiling(duration));

            CollectCircleHits(centerX, centerY, radius * radius);

            // Serial phase: apply stun（stunTurns 为循环不变量，已提出）
            int hitCount = 0;
            foreach (int enemyId in _mergedHits)
            {
                store.ApplyEnemyStun(enemyId, stunTurns);
                hitCount++;
            }
            if (hitCount > 0)
                renderer.Log($"[SKILL] {name} AOE-stunned {hitCount} enemies in radius {radius} for {stunTurns} turns");
            return hitCount;
        }

        /// <summary>
        /// AoeRoot (AreaShape=20): circle AoE root. Returns number of enemies hit.
        /// Rooted enemies cannot MOVE but can still cast abilities / perform basic melee
        /// (movement zeroed in EnemyMovementSystem when EnemyRootDurationLeft > 0).
        /// No damage is applied.
        /// </summary>
        public int CastAoeRoot(float centerX, float centerY, int radius, float duration, string name)
        {
            if (!_phaseContext.AllowsCombat) return RejectAbility(name);
            if (_activeEnemyList == null) return 0;
            if (radius <= 0 || duration <= 0f) return 0;

            int rootTurns = Math.Max(1, (int)Math.Ceiling(duration));

            CollectCircleHits(centerX, centerY, radius * radius);

            // Serial phase: apply root（rootTurns 为循环不变量，已提出）
            int hitCount = 0;
            foreach (int enemyId in _mergedHits)
            {
                store.ApplyEnemyRoot(enemyId, rootTurns);
                hitCount++;
            }
            if (hitCount > 0)
                renderer.Log($"[SKILL] {name} AOE-rooted {hitCount} enemies in radius {radius} for {rootTurns} turns");
            return hitCount;
        }

        /// <summary>
        /// AoeKnockback (AreaShape=21): circle AoE knockback. Returns number of enemies hit.
        /// Pushes each enemy radially AWAY from the player by <paramref name="force"/> units.
        /// Knockback is consumed by TowerAttackSystem.ResolveKnockback each frame. No damage applied.
        /// Enemies at the exact player position get a unit-random direction to avoid div-by-zero.
        /// </summary>
        public int CastAoeKnockback(float centerX, float centerY, int radius, float force, string name)
        {
            if (!_phaseContext.AllowsCombat) return RejectAbility(name);
            if (_activeEnemyList == null) return 0;
            if (radius <= 0 || force <= 0f) return 0;

            CollectCircleHits(centerX, centerY, radius * radius);

            // Serial phase: apply knockback impulse（radial 方向由消费方 ResolveKnockback 按
            // 施加时刻的 (Position - center) 解析，这里只写力大小 —— 与原串行实现一致）
            int hitCount = 0;
            foreach (int enemyId in _mergedHits)
            {
                store.ApplyEnemyKnockback(enemyId, force);
                hitCount++;
            }
            if (hitCount > 0)
                renderer.Log($"[SKILL] {name} AOE-knockbacked {hitCount} enemies in radius {radius} with force {force:F1}");
            return hitCount;
        }

        private int RejectAbility(string name)
        {
            _rejectedAbilityCount++;
            _lastRejectReason = SkillDamageRejectReason.PhaseNotAllowed;
            renderer.Log($"[ABILITY_REJECTED] PhaseNotAllowed skill={name}");
            return 0;
        }
    }
}
