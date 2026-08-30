using System;
using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Auto Skill System — 在 BuildPhase 自动施放玩家技能。
    /// 
    /// 职责：
    /// - BuildPhase 期间检查所有就绪（非冷却中）技能
    /// - 按选优策略（冷却最短 / 伤害最高 / AoE 最广）选出一个技能施放
    /// - 调用 SkillSystem.CastSkill() 执行施放
    /// 
    /// 设计原则：
    /// - BuildPhase 不运行战斗引擎，此系统对性能基准无影响
    /// - 无并行，不写共享状态，完全串行
    /// - 依赖 SkillSystem 内部的 SkillDamageQueue，两阶段模式不受影响
    /// </summary>
    public class AutoSkillSystem
    {
        private readonly ComponentStore store;
        private readonly IRenderer renderer;
        private readonly int playerId;
        private readonly SkillSystem skillSystem;
        private readonly AutoSkillConfig config;
        private bool _buildPhaseRejectReported;

        // 选优策略枚举
        private static readonly Random _rng = new Random();

        public AutoSkillSystem(
            ComponentStore store,
            IRenderer renderer,
            int playerId,
            SkillSystem skillSystem,
            AutoSkillConfig config)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            this.playerId = playerId;
            this.skillSystem = skillSystem ?? throw new ArgumentNullException(nameof(skillSystem));
            this.config = config ?? new AutoSkillConfig();
        }

        /// <summary>
        /// BuildPhase 每帧调用：检查就绪技能，选优施放。
        /// 施放上限由 AutoSkillConfig.MaxSkillsPerPhase 控制，防止一帧内刷空所有技能。
        /// </summary>
        public void Update(bool allowCombat = true)
        {
            if (allowCombat) _buildPhaseRejectReported = false;
            if (!config.Enabled)
                return;

            int castCount = 0;
            int maxCasts = config.MaxSkillsPerPhase;

            // 收集所有就绪技能
            var candidates = CollectReadySkills();
            if (candidates.Count == 0)
                return;

            // 按策略排序
            candidates = SortByStrategy(candidates);

            // 施放优先级最高的技能（可配置上限）
            int toCast = Math.Min(candidates.Count, maxCasts);
            if (!allowCombat)
            {
                if (!_buildPhaseRejectReported)
                {
                    renderer.Log("[ABILITY_REJECTED] PhaseNotAllowed source=AutoSkill");
                    _buildPhaseRejectReported = true;
                }
                return;
            }
            for (int i = 0; i < toCast; i++)
            {
                var skill = candidates[i];
                skillSystem.CastSkill(skill.Name);
                renderer.Log($"[AUTOSKILL] Auto-cast '{skill.Name}' (strategy: {config.SelectionStrategy})");
                castCount++;
            }

            if (castCount > 0)
            {
                renderer.Log($"[AUTOSKILL] AutoSkillSystem cast {castCount} skill(s) this phase.");
            }
        }

        /// <summary>
        /// 收集当前所有冷却完毕的技能。
        /// </summary>
        private List<CandidateSkill> CollectReadySkills()
        {
            var candidates = new List<CandidateSkill>();
            int count = store.AbilityCount[playerId];

            for (int slot = 0; slot < count; slot++)
            {
                var inst = store.GetAbility(playerId, slot);
                // 跳过冷却中的或无法激活的
                if (!inst.CanActivate())
                    continue;

                var def = inst.Definition;
                candidates.Add(new CandidateSkill
                {
                    Name = def.Name,
                    Cooldown = def.Cooldown,
                    DamageMultiplier = def.FixedBaseDamage,
                    AreaRadius = def.AreaRadius,
                    AreaShape = def.AreaShape,
                    Slot = slot
                });
            }

            return candidates;
        }

        /// <summary>
        /// 根据配置策略对候选技能排序。
        /// </summary>
        private List<CandidateSkill> SortByStrategy(List<CandidateSkill> candidates)
        {
            switch (config.SelectionStrategy)
            {
                case AutoSkillStrategy.CooldownShortest:
                    // 冷却最短的优先（最频繁可再次使用）
                    candidates.Sort((a, b) => a.Cooldown.CompareTo(b.Cooldown));
                    break;

                case AutoSkillStrategy.DamageHighest:
                    // 伤害最高的优先
                    candidates.Sort((a, b) => b.DamageMultiplier.CompareTo(a.DamageMultiplier));
                    break;

                case AutoSkillStrategy.AoeLargest:
                    // AoE 半径最大的优先
                    candidates.Sort((a, b) => b.AreaRadius.CompareTo(a.AreaRadius));
                    break;

                case AutoSkillStrategy.Random:
                    // 随机打乱（模拟随机选优）
                    Shuffle(candidates);
                    break;

                case AutoSkillStrategy.CoolestFirst:
                default:
                    // 综合评分：优先 AoE 大 + 冷却短
                    candidates.Sort((a, b) =>
                    {
                        double scoreA = (a.AreaRadius * 10.0) / Math.Max(a.Cooldown, 0.1);
                        double scoreB = (b.AreaRadius * 10.0) / Math.Max(b.Cooldown, 0.1);
                        return scoreB.CompareTo(scoreA);
                    });
                    break;
            }

            return candidates;
        }

        private static void Shuffle(List<CandidateSkill> list)
        {
            int n = list.Count;
            while (n > 1)
            {
                int k = _rng.Next(n--);
                var tmp = list[n];
                list[n] = list[k];
                list[k] = tmp;
            }
        }

        private struct CandidateSkill
        {
            public string Name;
            public float Cooldown;
            public float DamageMultiplier;
            public int AreaRadius;
            public int AreaShape;
            public int Slot;
        }
    }
}
