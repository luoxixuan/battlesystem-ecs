#nullable enable
using System;
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
    ///   • a typed activation request into the shared ability runtime, followed by a
    ///     resolver-owned damage request against the selected enemy
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
        private PhaseContext _phaseContext = PhaseContext.Unbound;
        public PhaseContextKind CurrentPhaseContext => _phaseContext.Kind;
        public AbilityActivationResult LastActivation { get; private set; }

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
            if (!_phaseContext.AllowsCombat) return;
            if (deltaTime <= 0f) return;
            var towerIds = store.ActiveTowerIds;
            for (int i = 0; i < towerIds.Count; i++)
            {
                int towerId = towerIds[i];
                if (!store.TowerActive[towerId]) continue;
                // Fast skip: no active skill → cooldown is 0, no work to do
                if (store.TowerActiveSkillId[towerId] < 0) continue;
                GameplayAbilityRuntime.TickCooldown(store.TowerActiveCooldown, towerId, deltaTime);
            }
        }

        /// <summary>
        /// Public API: player/HUD calls this when the player presses the hotkey
        /// bound to a tower's active skill. Returns true if the cast succeeded
        /// (i.e. the tower was ready and the cooldown gate was passed).
        ///
        /// The bool-returning method is retained as the public compatibility adapter;
        /// callers needing rejection diagnostics should use ActivateTower.
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
            int skillId = store.TowerActiveSkillId[towerId];
            if (skillId < 0) return Reject(towerId, AbilityActivationRejectReason.InvalidRequest);
            int targetId = FindTarget(towerId);
            var request = new AbilityActivationRequest(towerId, towerId, store.TowerActiveCooldownMax[towerId], targetId,
                new AbilityId(skillId), new EffectId(skillId), new TriggerId(skillId));
            var ready = GameplayAbilityRuntime.TryActivate(store.TowerActiveCooldown, request);
            if (!ready.Accepted) return ready;
            // A tower may be activated before enemies spawn; retain the legacy
            // cooldown-only acknowledgement while the effect path remains inert.
            if (targetId < 0) return GameplayAbilityRuntime.AbilityCommit(store.TowerActiveCooldown, request);
            var skill = _config?.TryGetSkillById(skillId);
            float multiplier = skill != null && skill.DamageMultiplier > 0f ? skill.DamageMultiplier : 1f;
            float damage = store.TowerAttackDamage[towerId] * multiplier;
            if (damage <= 0f) return Reject(towerId, AbilityActivationRejectReason.InvalidRequest);
            var damageRequest = new DamageRequest(store.GetEntityHandle(towerId), store.GetEntityHandle(targetId), damage,
                DamageType.True, ElementType.None, DamageFlags.None, DamageAmountStage.Raw,
                DamageCommitBoundary.GameplayResolve, store.AllocateGameplaySequence(targetId),
                ability: request.Ability, effect: request.Effect, ownerPlayerId: 0);
            var applied = store.DamageResolver.TryApply(damageRequest);
            if (!applied.Accepted) return Reject(towerId, AbilityActivationRejectReason.InvalidRequest);
            var committed = GameplayAbilityRuntime.AbilityCommit(store.TowerActiveCooldown, request);
            if (committed.Accepted)
                Console.WriteLine($"[TOWER_ACTIVE] tower={towerId} target={targetId} skill={ResolveSkillName(skillId)} damage={damage:F1}");
            return committed;
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

        internal void SetPhaseContext(PhaseContext context) => _phaseContext = context;

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
