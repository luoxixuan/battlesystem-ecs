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
    /// Player Global Skill / Ultimate system.
    /// 
    /// Design:
    /// - Each player has a set of global skills (ultimates) that can be triggered via hotkey.
    /// - Global skills have high mana cost and long cooldowns (cross-wave).
    /// - Effect types: screen-wide damage, time stop, emergency heal, gold burst.
    /// - TechTreeSystem can unlock additional global skills (via GetUnlockedGlobalSkills()).
    /// - GlobalSkillSystem is called in BuildPhase and WavePhase to process cooldowns.
    /// - Hotkey input is read from ComponentStore.PlayerGlobalSkillPressed[pid].
    /// 
    /// Two-phase model:
    ///   SetTurn:    cache unlocked skills per player
    ///   Update:     decrement cooldowns, consume mana, execute ready skills
    /// </summary>
    public class GlobalSkillSystem
    {
        private ComponentStore store;
        private GameConfig gameConfig;
        private IRenderer renderer;
        private TechTreeSystem? techTreeSystem;
        private readonly bool hasTechTreeSystem;
        private readonly int playerId;
        private int _turn = 0;
        // 未绑定阶段时默认拒绝，调用方必须显式同步阶段上下文。
        private PhaseContext _phaseContext = PhaseContext.Unbound;
        private int _rejectedCandidateCount;
        private int _rejectedInputCount;
        private int _successfulActivationCount;
        public int RejectedCandidateCount => _rejectedCandidateCount;
        public int RejectedInputCount => _rejectedInputCount;
        public int RejectedActivationCount => _rejectedCandidateCount + _rejectedInputCount;
        public int SuccessfulActivationCount => _successfulActivationCount;
        public SkillDamageRejectReason LastRejectReason { get; private set; }
        public PhaseContextKind CurrentPhaseContext => _phaseContext.Kind;

        // Global skill definitions (from gameConfig.GlobalSkills)
        private const int MAX_GLOBAL_SKILLS = 8;

        public GlobalSkillSystem(ComponentStore store, GameConfig gameConfig, IRenderer renderer, int playerId, TechTreeSystem? techTreeSystem = null)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.gameConfig = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
            this.renderer = renderer;
            this.playerId = playerId;
            this.techTreeSystem = techTreeSystem;
            this.hasTechTreeSystem = techTreeSystem != null;
        }

        public void SetTurn(int turn)
        {
            _turn = turn;

            // Unlock all global skills by default (tech tree integration can be added later)
            // For now, all skills in gameConfig.GlobalSkills are available
            // Safety: cap at MAX_GLOBAL_SKILLS to avoid array bounds overflow
            int skillCount = Math.Min(gameConfig.GlobalSkills.Count, MAX_GLOBAL_SKILLS);
            for (int i = 0; i < MAX_GLOBAL_SKILLS; i++)
            {
                store.PlayerGlobalSkillUnlocked[playerId * MAX_GLOBAL_SKILLS + i] = (i < skillCount);
            }
        }

        internal void SetPhaseContext(PhaseContext context) => _phaseContext = context;

        /// <summary>
        /// Update global skill cooldowns. Called every frame during BuildPhase and WavePhase.
        /// </summary>
        public void Update(float deltaTime, bool isBuildPhase)
        {
            _ = isBuildPhase; // 兼容旧调用签名，实际阶段以 PhaseContext 为唯一事实源。
            if (!_phaseContext.AllowsCombat && !_phaseContext.AllowsPreparationResources)
            {
                RejectPendingActivation();
                return;
            }
            // Process cooldowns
            for (int i = 0; i < MAX_GLOBAL_SKILLS; i++)
            {
                int idx = playerId * MAX_GLOBAL_SKILLS + i;
                float cd = store.PlayerGlobalSkillCooldown[idx];
                if (cd > 0f)
                {
                    // Apply cooldown reduction: 0 = no reduction, 0.3 = 30% faster
                    // effectiveRate = 1 + cdr, capped at 60% (1.6x speed)
                    float cdr = store.PlayerCooldownReduction[playerId];
                    float cdrClamped = Math.Min(cdr, 0.6f);
                    store.PlayerGlobalSkillCooldown[idx] = Math.Max(0f, cd - deltaTime * (1f + cdrClamped));
                }
            }

            // Check for hotkey press — consume the signal immediately
            if (!store.PlayerGlobalSkillPressed[playerId]) return;
            store.PlayerGlobalSkillPressed[playerId] = false; // reset after reading

            // 选择当前阶段允许的第一个就绪技能。
            int skillIdx = -1;
            int rejectedCombat = 0;
            for (int i = 0; i < MAX_GLOBAL_SKILLS; i++)
            {
                int idx = playerId * MAX_GLOBAL_SKILLS + i;
                if (!store.PlayerGlobalSkillUnlocked[idx]) continue;
                if (store.PlayerGlobalSkillCooldown[idx] > 0f) continue;
                var candidate = GetSkillDef(i);
                if (_phaseContext.AllowsPreparationResources && candidate != null && !IsBuildAllowed(candidate.SkillType))
                {
                    rejectedCombat++;
                    continue;
                }
                skillIdx = i;
                break;
            }

            if (rejectedCombat > 0)
            {
                _rejectedCandidateCount += rejectedCombat;
                LastRejectReason = SkillDamageRejectReason.PhaseNotAllowed;
                renderer.Log("[ABILITY_REJECTED] PhaseNotAllowed globalSkill=combat-candidate");
            }
            if (skillIdx < 0)
            {
                return;
            }
            TryActivateGlobalSkill(skillIdx);
        }

        private static bool IsBuildAllowed(int skillType)
        {
            return skillType == (int)GlobalSkillType.EmergencyHeal ||
                   skillType == (int)GlobalSkillType.GoldBurst;
        }

        /// <summary>
        /// Try to activate a global skill by index. Returns true if activation succeeded.
        /// </summary>
        public bool TryActivateGlobalSkill(int skillIdx)
        {
            if (skillIdx < 0 || skillIdx >= MAX_GLOBAL_SKILLS) return false;

            int idx = playerId * MAX_GLOBAL_SKILLS + skillIdx;
            if (!store.PlayerGlobalSkillUnlocked[idx]) return false;
            if (store.PlayerGlobalSkillCooldown[idx] > 0f) return false;

            // Get skill definition
            var def = GetSkillDef(skillIdx);
            if (def == null) return false;
            if (!_phaseContext.AllowsCombat &&
                !(_phaseContext.AllowsPreparationResources && IsBuildAllowed(def.SkillType)))
            {
                _rejectedCandidateCount++;
                LastRejectReason = SkillDamageRejectReason.PhaseNotAllowed;
                renderer.Log($"[ABILITY_REJECTED] PhaseNotAllowed globalSkill={def.Name}");
                return false;
            }

            // Check mana cost (apply cost multiplier from tech tree)
            float costMult = store.PlayerManaCost[playerId];
            float manaCost = def.ManaCost * costMult;
            if (store.PlayerMana[playerId] < manaCost)
            {
                renderer.Log($"[GlobalSkill] Not enough mana for {def.Name} ({manaCost} required)");
                return false;
            }

            // Consume mana
            store.ApplyPlayerResourceAuthority(playerId, playerId, new Core.GAS.AttributeKey(7), -manaCost);

            // Execute effect based on skill type
            ExecuteSkillEffect(def);

            // Start cooldown
            store.PlayerGlobalSkillCooldown[idx] = def.Cooldown;

            renderer.Log($"[GlobalSkill] Activated: {def.Name}");
            _successfulActivationCount++;
            return true;
        }

        public int RejectPendingActivation()
        {
            if (!store.PlayerGlobalSkillPressed[playerId]) return 0;
            store.PlayerGlobalSkillPressed[playerId] = false;
            _rejectedInputCount++;
            LastRejectReason = SkillDamageRejectReason.PhaseNotAllowed;
            renderer.Log("[ABILITY_REJECTED] PhaseNotAllowed source=GlobalSkillPending");
            return 1;
        }

        private GlobalSkillDef? GetSkillDef(int skillIdx)
        {
            if (skillIdx < 0 || skillIdx >= gameConfig.GlobalSkills.Count) return null;
            if (skillIdx >= MAX_GLOBAL_SKILLS) return null;
            return gameConfig.GlobalSkills[skillIdx];
        }

        private void ExecuteSkillEffect(GlobalSkillDef def)
        {
            switch ((GlobalSkillType)def.SkillType)
            {
                case GlobalSkillType.MeteorStrike:
                    ExecuteMeteorStrike(def);
                    break;
                case GlobalSkillType.TimeStop:
                    ExecuteTimeStop(def);
                    break;
                case GlobalSkillType.EmergencyHeal:
                    ExecuteEmergencyHeal(def);
                    break;
                case GlobalSkillType.GoldBurst:
                    ExecuteGoldBurst(def);
                    break;
            }
        }

        private void ExecuteMeteorStrike(GlobalSkillDef def)
        {
            // Deal damage to all active enemies (full-screen)
            float damage = def.DamagePct * 0.01f * store.PlayerCurrentHealth[playerId]; // pct of player HP as damage
            // Cap at reasonable max
            damage = Math.Min(damage, def.MaxDamage);

            var activeEnemies = store.ActiveEnemyIds;
            int killed = 0;

            for (int i = 0; i < activeEnemies.Count; i++)
            {
                int eid = activeEnemies[i];
                if (!store.EnemyActive[eid]) continue;

                // Apply armor reduction (physical damage)
                float rawDmg = damage;
                float armor = store.EnemyArmor[eid];
                float armorRed = 1f - (armor / (armor + 50f)); // standard armor formula
                float finalDmg = rawDmg * Math.Max(0.1f, armorRed);

                // source handle 表示实际玩家实体；owner 仍表示技能归属玩家槽位。
                var source = store.GetEntityHandle(store.PlayerEntityId);
                var target = store.GetEntityHandle(eid);
                var result = store.DamageResolver.TryApply(new Core.GAS.DamageRequest(source, target, finalDmg,
                    DamageType.True, ElementType.None, DamageFlags.None, DamageAmountStage.Raw,
                    DamageCommitBoundary.GameplayResolve,
                    store.AllocateGameplaySequence(eid), ownerPlayerId: playerId));
                if (result.DeathQueued) killed++;
            }

            renderer.Log($"[GlobalSkill] Meteor Strike: {damage:F0} damage to all enemies, {killed} kills");
        }

        private void ExecuteTimeStop(GlobalSkillDef def)
        {
            // Apply global time scale = 0 (full freeze)
            store.GlobalTimeScale[playerId] = 0f;
            store.GlobalTimeScaleDuration[playerId] = def.Duration;

            renderer.Log($"[GlobalSkill] Time Stop: all enemies frozen for {def.Duration:F0}s");
        }

        private void ExecuteEmergencyHeal(GlobalSkillDef def)
        {
            // Heal all towers by pct of their max HP
            float healPct = def.HealPct; // 0-1
            var activeTowers = store.ActiveTowerIds;
            int healed = 0;

            for (int i = 0; i < activeTowers.Count; i++)
            {
                int tid = activeTowers[i];
                // Use TowerConstructionMaxHP as proxy for tower max health (or fall back to a reasonable default)
                float maxHp = store.TowerConstructionMaxHP[tid];
                if (maxHp <= 0f) maxHp = 1000f; // default tower HP if not set
                float currentHp = store.TowerConstructionHP[tid];
                if (currentHp <= 0f) currentHp = maxHp; // if construction HP not set, assume full
                float healAmount = maxHp * healPct;
                store.TowerConstructionHP[tid] = Math.Min(maxHp, currentHp + healAmount);
                healed++;
            }

            // Also heal player
            float playerHeal = store.PlayerCurrentHealth[playerId] * def.HealPct;
            store.ApplyPlayerResourceAuthority(playerId, playerId, new Core.GAS.AttributeKey(3), playerHeal);

            renderer.Log($"[GlobalSkill] Emergency Heal: restored {healPct * 100:F0}% HP to {healed} towers");
        }

        private void ExecuteGoldBurst(GlobalSkillDef def)
        {
            // Instant gold gain (flat, no temporary multiplier that accumulates)
            float goldGain = def.GoldAmount;
            store.ApplyPlayerResourceAuthority(playerId, playerId, new Core.GAS.AttributeKey(4), goldGain);

            renderer.Log($"[GlobalSkill] Gold Burst: +{goldGain:F0} gold");
        }
    }
}
