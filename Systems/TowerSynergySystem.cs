using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Linq;
using BattleSystemECS.Core;
using BattleSystemECS.Components;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Tower Synergy System — 塔协同增益系统
    /// 
    /// 启动时从 Data/Towers/tower_synergy.json 加载协同配置，
    /// SetTurn 时按 TowerType 分组缓存 ActiveTowerIds，
    /// Update 时检查塔组合，触发协同效果（Buff/伤害加成）。
    /// 
    /// 新增 System 独立运行，不污染现有热路径（Parallel.For 遍历 ActiveTowerIds）。
    /// </summary>
    public class TowerSynergySystem
    {
        private ComponentStore store;
        private IRenderer logger;

        // 协同配置（从 JSON 加载）
        private List<SynergyConfig> _synergies = new List<SynergyConfig>();

        // SetTurn 时缓存的活跃塔列表（按 TowerType 分组）
        private Dictionary<string, List<int>> _towersByType = new Dictionary<string, List<int>>();

        // SynergyConfig 模型（JSON 反序列化用）
        private class SynergyConfig
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string[] RequiredTypes { get; set; }
            public string Description { get; set; }
            public SynergyEffect Effect { get; set; }
            public int Threshold { get; set; }
        }

        private class SynergyEffect
        {
            public int BonusChainCount { get; set; }
            public float DotDamageBonus { get; set; }
        }

        public TowerSynergySystem(ComponentStore store, IRenderer logger)
        {
            this.store = store;
            this.logger = logger;
        }

        /// <summary>
        /// 启动时从 JSON 加载协同配置
        /// </summary>
        public void LoadSynergyConfig()
        {
            string configPath = Path.Combine("Data", "Towers", "tower_synergy.json");
            if (!File.Exists(configPath))
            {
                logger.Log("[TowerSynergy] Config not found, synergy disabled.");
                return;
            }

            try
            {
                string json = File.ReadAllText(configPath);
                var wrapper = JsonSerializer.Deserialize<SynergyWrapper>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (wrapper?.Synergies != null)
                {
                    _synergies = wrapper.Synergies;
                    logger.Log($"[TowerSynergy] Loaded {_synergies.Count} synergy definitions.");
                }
            }
            catch (Exception ex)
            {
                logger.Log($"[TowerSynergy] Failed to load config: {ex.Message}");
            }
        }

        private class SynergyWrapper
        {
            public List<SynergyConfig> Synergies { get; set; }
        }

        /// <summary>
        /// SetTurn 时缓存 ActiveTowerIds，按 TowerType 分组
        /// </summary>
        public void SetTurn()
        {
            _towersByType.Clear();

            foreach (var towerId in store.ActiveTowerIds)
            {
                TowerType type = store.TowerType[towerId];
                if (type == TowerType.Basic) continue;

                string typeStr = type.ToString();
                if (!_towersByType.ContainsKey(typeStr))
                    _towersByType[typeStr] = new List<int>();
                _towersByType[typeStr].Add(towerId);
            }
        }

        /// <summary>
        /// Update — 检查塔组合，触发协同效果
        /// 在 TowerAttackSystem.Update 之后调用（配合 GameManager 帧调度）
        ///
        /// Round 91 Synergy Tiering 阶段 1：按同类塔数量设 TowerSynergyTier (0/1/2/3)
        /// 阶段 2：应用 tier mult（与既有二元协同 multiplier 串行叠加）
        /// 阶段 3：执行既有 JSON 协同
        /// </summary>
        public void Update()
        {
            // 阶段 1+2: Synergy Tiering — 零开销早退
            ResolveSynergyTiers();

            // 阶段 3: 既有 JSON 协同
            if (_synergies.Count == 0) return;

            foreach (var synergy in _synergies)
            {
                // 检查 requiredTypes 组合是否满足 threshold
                if (!TryGetSynergyTowers(synergy.RequiredTypes, synergy.Threshold, out var towers))
                    continue;

                // 应用协同效果
                ApplySynergyEffect(synergy, towers);
            }
        }

        /// <summary>
        /// Round 91: 按同类塔聚集度设 TowerSynergyTier (0/1/2/3)
        /// 最低阈值时整段跳过（零开销）；tier 决定 damage mult 叠加
        ///
        /// 重要：每帧先清零所有活跃塔的 TowerSynergyMultiplier 字段（避免 tier bonus 跨帧累加），
        /// 然后按 tier 重新写入。阶段 3 的 ApplySynergyEffect 用 Max() 在此基础上再叠加 binary synergy bonus。
        /// </summary>
        private void ResolveSynergyTiers()
        {
            // 零开销：当前活跃塔总数低于 tier1 阈值直接跳过
            int totalActive = store.ActiveTowerIds.Count;
            if (totalActive < SynergyTierConfig.SynergyTier1Count)
            {
                // 同时清零所有活跃塔的 tier + multiplier（避免 tier bonus 跨帧累加）
                ResetAllTiers();
                return;
            }

            // 先清零所有活跃塔的 tier + tier-derived multiplier
            // （注：阶段 3 的 binary synergy 也会用 Max() 写入 mult，所以这里先清零是安全的）
            ResetAllTiers();

            // 按类型计算 tier
            foreach (var kv in _towersByType)
            {
                var group = kv.Value;
                int count = group.Count;
                int tier = 0;
                if (count >= SynergyTierConfig.SynergyTier3Count) tier = 3;
                else if (count >= SynergyTierConfig.SynergyTier2Count) tier = 2;
                else if (count >= SynergyTierConfig.SynergyTier1Count) tier = 1;

                if (tier == 0) continue;

                // 取 tier 对应的 damage mult
                float tierBonus = tier == 3
                    ? SynergyTierConfig.SynergyTier3Bonus
                    : tier == 2
                        ? SynergyTierConfig.SynergyTier2Bonus
                        : SynergyTierConfig.SynergyTier1Bonus;

                foreach (var towerId in group)
                {
                    if (store.TowerIsDispelled[towerId]) continue;
                    // 写入 tier 字段（用于 UI / debug / 后续扩展）
                    store.TowerSynergyTier[towerId] = tier;
                    // 绝对写入 mult（每帧覆盖，避免累加 bug）
                    // mult = 1.0 + tierBonus；阶段 3 的 binary synergy 会用 Max() 再次写入
                    store.SetTowerSynergyMultiplier(towerId, 1.0f + tierBonus);
                }
            }
        }

        /// <summary>
        /// 清零所有活跃塔的 tier 字段和 tier-derived multiplier。
        /// 阶段 3 的 ApplySynergyEffect 用 Max() 写入 binary synergy 的 mult 不会被覆盖。
        /// </summary>
        private void ResetAllTiers()
        {
            foreach (var towerId in store.ActiveTowerIds)
            {
                store.TowerSynergyTier[towerId] = 0;
                store.TowerSynergyMultiplier[towerId] = 0f;
            }
        }

        private bool TryGetSynergyTowers(string[] requiredTypes, int threshold, out List<int> towers)
        {
            towers = new List<int>();

            // 对于 threshold=2 的协同，检查每对 requiredTypes
            if (requiredTypes.Length >= 2)
            {
                var type1 = requiredTypes[0];
                var type2 = requiredTypes[1];

                if (!_towersByType.TryGetValue(type1, out var group1) ||
                    !_towersByType.TryGetValue(type2, out var group2))
                    return false;

                // 简单计数：如果两个 group 的总塔数 >= threshold，则触发
                int total = group1.Count + group2.Count;
                if (total >= threshold)
                {
                    towers.AddRange(group1);
                    towers.AddRange(group2);
                    return true;
                }
            }

            return false;
        }

        private void ApplySynergyEffect(SynergyConfig synergy, List<int> towers)
        {
            // bonusChainCount: 额外链式闪电弹射次数
            if (synergy.Effect?.BonusChainCount > 0)
            {
                foreach (var towerId in towers)
                {
                    // Skip towers that are dispelled (cannot receive new synergy buffs)
                    if (store.TowerIsDispelled[towerId]) continue;

                    // 对 Tesla 塔增加额外链式弹射（通过 multiplier 缩放链式伤害）
                    // bonusChainCount 存储在 TowerSynergyMultiplier 中，供 TowerAttackSystem 读取
                    float existingMult = store.GetTowerSynergyMultiplier(towerId);
                    float bonusMult = 1.0f + (synergy.Effect.BonusChainCount * 0.1f); // 每额外1次链式弹射 +10% 伤害
                    store.SetTowerSynergyMultiplier(towerId, Math.Max(existingMult, bonusMult));
                }
                logger.Log($"[TowerSynergy] {synergy.Name} activated ({towers.Count} towers), bonus chain count +{synergy.Effect.BonusChainCount}");
            }

            // dotDamageBonus: 火焰 DoT 伤害加成（针对 Frost+Firewall 组合）
            if (synergy.Effect?.DotDamageBonus > 0)
            {
                foreach (var towerId in towers)
                {
                    // Skip towers that are dispelled (cannot receive new synergy buffs)
                    if (store.TowerIsDispelled[towerId]) continue;

                    float existingMult = store.GetTowerSynergyMultiplier(towerId);
                    float bonusMult = 1.0f + synergy.Effect.DotDamageBonus; // DoT 伤害 +X%
                    store.SetTowerSynergyMultiplier(towerId, Math.Max(existingMult, bonusMult));
                }
                logger.Log($"[TowerSynergy] {synergy.Name} activated ({towers.Count} towers), DoT damage +{synergy.Effect.DotDamageBonus:P0}");
            }
        }
    }
}