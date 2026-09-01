#nullable enable
using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Components;
using BattleSystemECS.Core.GAS;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Round 138 — Per-Tower Active Skill System.
    ///
    /// Some tower types have a manual-cast active ability (e.g. press Q to trigger a
    /// powerful AOE attached to that specific tower). This system handles:
    ///   • per-frame cooldown tick (so the gate resolves in seconds, not frames)
    ///   • the public TriggerTowerActive(towerId) API the player/HUD calls on input
    ///   • 先向共享能力运行时提交类型化激活请求，再由解析器向选中敌人提交伤害请求
    ///
    /// Design notes:
    ///   • Inert by default: ActiveSkillId == -1 → no field writes, no per-tick work
    ///     beyond a single `if` check, so the cost is O(activeTowers) per frame.
    ///   • Effect dispatch is intentionally minimal — we look up the SkillDef and
    ///     broadcast a log + put the tower on cooldown. The full cast pipeline
    ///     (damage application, AOE query) is delegated to a follow-up injection
    ///     into SkillSystem.CastByTower when the wiring is finalized. Round 138
    ///     establishes the cooldown + public API surface only.
    ///   • No HUD binding: that's the renderer's job. This system just answers
    ///     "is this tower's active ready?" and "fire it".
    /// </summary>
    public class TowerActiveSkillSystem
    {
        private ComponentStore store;
        private GameConfig? _config;
        private readonly CompatibilityCatalogEntry?[] _compatibilityCatalogs = new CompatibilityCatalogEntry?[ComponentStore.MAX_ENTITIES];
        private PhaseContext _phaseContext = PhaseContext.Unbound;
        private readonly List<int> _catalogTargets = new List<int>(32);
        private readonly List<float> _catalogMagnitudeScales = new List<float>(32);
        private int _pendingTowerId = -1;
        private IAbilityPayloadHandler? _payloadHandler;
        public PhaseContextKind CurrentPhaseContext => _phaseContext.Kind;
        public AbilityActivationResult LastActivation { get; private set; }

        public bool RequestTowerActive(int towerId)
        {
            if (_pendingTowerId >= 0 || !ComponentStore.IsValidEntity(towerId) || !store.TowerActive[towerId]) return false;
            _pendingTowerId = towerId;
            return true;
        }

        public TowerActiveSkillSystem(ComponentStore store, GameConfig? config = null)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            _config = config;
        }

        /// <summary>
        /// Optional late binding of the config — the same GameConfig used by
        /// SkillSystem. If null, TriggerTowerActive still works (cooldown gate)
        /// but skill-id → definition lookup falls back to logging only.
        /// </summary>
        public void SetConfig(GameConfig config) => _config = config;

        /// <summary>
        /// Per-frame cooldown tick. Walks ActiveTowerIds, decrements TowerActiveCooldown
        /// for any tower with an active skill configured. Skips inactive/disabled
        /// towers so we don't waste cycles on the swap-and-pop trailing slot.
        /// </summary>
        public void Update(float deltaTime)
        {
            if (!_phaseContext.AllowsCombat) { _pendingTowerId = -1; return; }
            var towerIds = store.ActiveTowerIds;
            if (deltaTime > 0f) for (int i = 0; i < towerIds.Count; i++)
            {
                int towerId = towerIds[i];
                if (!store.TowerActive[towerId]) continue;
                // Fast skip: no active skill → cooldown is 0, no work to do
                if (store.TowerActiveSkillId[towerId] < 0) continue;
                GameplayAbilityRuntime.TickCooldown(store.TowerActiveCooldown, towerId, deltaTime);
            }
            if (_pendingTowerId >= 0)
            {
                int towerId = _pendingTowerId;
                _pendingTowerId = -1;
                LastActivation = ActivateTower(towerId);
            }
        }

        /// <summary>
        /// Public API: player/HUD calls this when the player presses the hotkey
        /// bound to a tower's active skill. Returns true if the cast succeeded
        /// (i.e. the tower was ready and the cooldown gate was passed).
        ///
        /// 返回 bool 的方法保留为公开兼容适配器；需要拒绝诊断的调用方应使用 ActivateTower。
        /// </summary>
        public bool TriggerTowerActive(int towerId)
        {
            LastActivation = ActivateTower(towerId);
            return LastActivation.Accepted;
        }

        public AbilityActivationResult ActivateTower(int towerId)
        {
            if (!_phaseContext.AllowsCombat) return Reject(towerId, AbilityActivationRejectReason.PhaseNotAllowed);
            if (!ComponentStore.IsValidEntity(towerId) || !store.TowerActive[towerId]) return Reject(towerId, AbilityActivationRejectReason.InvalidRequest);
            if (store.TowerIsSilenced[towerId] ||
                GameplayTagRuntime.HasTag(store, towerId, CatalogRegistries.TowerSilencedTag))
                return Reject(towerId, AbilityActivationRejectReason.TagRequirementsNotMet);
            int skillId = store.TowerActiveSkillId[towerId];
            if (skillId < 0) return Reject(towerId, AbilityActivationRejectReason.InvalidRequest);
            var catalog = _config?.CompiledCatalog;
            var configuredSkill = _config?.TryGetSkillById(skillId);
            AbilityId abilityId = default(AbilityId);
            AbilityDefinition catalogAbility = default(AbilityDefinition);
            bool resolved = catalog != null && configuredSkill != null &&
                catalog.TryResolveAlias(configuredSkill.Name, out abilityId) && catalog.TryGetAbility(abilityId, out catalogAbility);
            bool useTypedTargeting = resolved;
            if (!resolved)
            {
                if (_config?.StrictCatalogReferences == true)
                    return Reject(towerId, AbilityActivationRejectReason.UnsupportedDefinition);
                ResolveCompatibilityCatalog(towerId, skillId, configuredSkill, out catalog, out abilityId, out catalogAbility);
            }
            if (catalog == null || !catalog.TryGetAbility(abilityId, out catalogAbility))
                return Reject(towerId, AbilityActivationRejectReason.UnsupportedDefinition);
            int targetId = useTypedTargeting && UsesOwnerTarget(catalog, catalogAbility)
                ? store.PlayerEntityId : useTypedTargeting ? towerId : FindTarget(towerId);
            if (targetId < 0) return Reject(towerId, AbilityActivationRejectReason.NoTarget);
            bool selfTarget = catalogAbility.Targeting.Relation == RelationFilter.Self;
            if (useTypedTargeting && !selfTarget)
            {
                if (!TargetingRuntime.TryCollectEnemyTargets(store, towerId, catalogAbility.Targeting,
                        _catalogTargets, _catalogMagnitudeScales))
                    return Reject(towerId, AbilityActivationRejectReason.UnsupportedDefinition);
                if (_catalogTargets.Count == 0)
                    return Reject(towerId, AbilityActivationRejectReason.NoTarget);
                targetId = _catalogTargets[0];
            }
            var request = new AbilityActivationRequest(towerId, towerId, store.TowerActiveCooldownMax[towerId], targetId,
                abilityId, catalogAbility.Effects.Count > 0 ? catalogAbility.Effects[0] : (EffectId?)null,
                catalogAbility.TriggerRefs.Count > 0 ? catalogAbility.TriggerRefs[0] : (TriggerId?)null,
                ownerPlayerId: store.PlayerEntityId);
            var result = !useTypedTargeting || selfTarget
                ? GameplayAbilityRuntime.Activate(store, catalog, store.TowerActiveCooldown, request, _payloadHandler)
                : GameplayAbilityRuntime.ActivateTargets(store, catalog, store.TowerActiveCooldown, request,
                    _catalogTargets, _catalogMagnitudeScales, _payloadHandler);
            if (result.Accepted)
                Console.WriteLine($"[TOWER_ACTIVE] tower={towerId} target={targetId} skill={ResolveSkillName(skillId)}");
            // 兼容审计：冷却归 GameplayAbilityRuntime.AbilityCommit 所有，不属于此适配器。
            return result;
        }

        private void ResolveCompatibilityCatalog(int towerId, int skillId, SkillConfig? skill,
            out GameplayCatalog catalog, out AbilityId abilityId, out AbilityDefinition ability)
        {
            float attackDamage = store.TowerAttackDamage[towerId];
            float cooldown = store.TowerActiveCooldownMax[towerId];
            float multiplier = skill != null && skill.DamageMultiplier > 0f ? skill.DamageMultiplier : 1f;
            var cached = _compatibilityCatalogs[towerId];
            if (cached != null && cached.SkillId == skillId && cached.Skill == skill &&
                cached.AttackDamage.Equals(attackDamage) && cached.Cooldown.Equals(cooldown) && cached.Multiplier.Equals(multiplier))
            {
                catalog = cached.Catalog;
                abilityId = cached.AbilityId;
                ability = cached.Ability;
                return;
            }

            string name = skill?.Name ?? $"tower-active-{skillId}";
            abilityId = new AbilityId(0);
            var execution = new ExecutionDefinition(new ExecutionId(0), EffectPayloadKind.Damage, attackDamage * multiplier,
                CatalogRegistries.SkillTag, MagnitudeSource.Constant, DamageAmountStage.Raw, operation: ExecutionOperation.ApplyDamage);
            var targeting = new TargetingDefinition(new TargetingId(0), TargetingShape.Single, 1, 1, 1, 1);
            ability = new AbilityDefinition(abilityId, name, targeting, ClockId.Combat, cooldown, GameplayPhaseMask.Wave,
                Array.Empty<EffectId>(), Array.Empty<ModifierDefinition>(), CatalogRegistries.SkillExecutor,
                CatalogRegistries.SkillConsumer, executions: new[] { execution.Id });
            catalog = new GameplayCatalog(new[] { ability }, new[] { targeting }, Array.Empty<GameplayEffectDefinition>(),
                new[] { execution }, Array.Empty<TriggerDefinition>(), Array.Empty<ModifierDefinition>(),
                new Dictionary<string, AbilityId>(StringComparer.OrdinalIgnoreCase) { [name] = abilityId });
            _compatibilityCatalogs[towerId] = new CompatibilityCatalogEntry(skillId, skill, attackDamage, cooldown,
                multiplier, catalog, abilityId, ability);
        }

        private sealed class CompatibilityCatalogEntry
        {
            public readonly int SkillId;
            public readonly SkillConfig? Skill;
            public readonly float AttackDamage, Cooldown, Multiplier;
            public readonly GameplayCatalog Catalog;
            public readonly AbilityId AbilityId;
            public readonly AbilityDefinition Ability;
            public CompatibilityCatalogEntry(int skillId, SkillConfig? skill, float attackDamage, float cooldown,
                float multiplier, GameplayCatalog catalog, AbilityId abilityId, AbilityDefinition ability)
            {
                SkillId = skillId; Skill = skill; AttackDamage = attackDamage; Cooldown = cooldown; Multiplier = multiplier;
                Catalog = catalog; AbilityId = abilityId; Ability = ability;
            }
        }

        private AbilityActivationResult Reject(int towerId, AbilityActivationRejectReason reason) =>
            new AbilityActivationResult(false, towerId, towerId, reason);

        private int FindTarget(int towerId)
        {
            int best = -1; float bestDistance = float.MaxValue;
            var enemies = store.ActiveEnemyIds;
            for (int i = 0; i < enemies.Count; i++)
            {
                int enemy = enemies[i];
                if (!store.EnemyActive[enemy] || store.EnemyHealth[enemy] <= 0f) continue;
                float dx = store.PositionX[enemy] - store.PositionX[towerId];
                float dy = store.PositionY[enemy] - store.PositionY[towerId];
                float distance = dx * dx + dy * dy;
                if (distance < bestDistance) { bestDistance = distance; best = enemy; }
            }
            return best;
        }

        private static bool UsesOwnerTarget(GameplayCatalog catalog, AbilityDefinition ability)
        {
            for (int i = 0; i < ability.Executions.Count; i++)
            {
                if (!catalog.TryGetExecution(ability.Executions[i], out var execution)) continue;
                if (execution.Payload == EffectPayloadKind.Resurrect && execution.Operation == ExecutionOperation.Resurrect ||
                    execution.Payload == EffectPayloadKind.Resource && execution.Operation == ExecutionOperation.RestoreSnapshot)
                    return true;
            }
            return false;
        }

        internal void SetPhaseContext(PhaseContext context)
        {
            _phaseContext = context;
            store.GameplayPhaseContext = context;
            if (!context.AllowsCombat) _pendingTowerId = -1;
        }

        internal void SetPayloadHandler(IAbilityPayloadHandler payloadHandler) =>
            _payloadHandler = payloadHandler ?? throw new ArgumentNullException(nameof(payloadHandler));

        /// <summary>
        /// 技能 id → 显示名。id 语义遵循 GameConfig 的归一化索引空间
        /// （GetSkillDisplayName：SkillDefs 优先、Skills 偏移回退），与
        /// HeroSkillSystem / TowerConfig.ActiveSkillId 的解析处处同义。
        /// </summary>
        private string ResolveSkillName(int skillId) => _config?.GetSkillDisplayName(skillId) ?? "?";

        /// <summary>
        /// Read-only helper for HUD/renderer: returns true if the tower can fire
        /// its active skill right now. Mirrors the gate in TriggerTowerActive.
        /// </summary>
        public bool IsTowerActiveReady(int towerId) => store.IsTowerActiveReady(towerId);

        /// <summary>
        /// Read-only helper for HUD/renderer: returns the configured active skill id
        /// or -1 if the tower has none. Lets the renderer show "(no active)" or
        /// "Press Q: [skill name]" depending on what designers wired up.
        /// </summary>
        public int GetTowerActiveSkillId(int towerId) => store.GetTowerActiveSkillId(towerId);

        /// <summary>
        /// Read-only helper for HUD/renderer: returns the remaining cooldown in
        /// seconds. 0 means ready.
        /// </summary>
        public float GetTowerActiveCooldown(int towerId) => store.GetTowerActiveCooldown(towerId);
    }
}
