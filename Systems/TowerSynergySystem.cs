using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Linq;
using BattleSystemECS.Core;
using BattleSystemECS.Components;

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
                string type = store.TowerType[towerId];
                if (string.IsNullOrEmpty(type)) continue;

                if (!_towersByType.ContainsKey(type))
                    _towersByType[type] = new List<int>();
                _towersByType[type].Add(towerId);
            }
        }

        /// <summary>
        /// Update — 检查塔组合，触发协同效果
        /// 在 TowerAttackSystem.Update 之后调用（配合 GameManager 帧调度）
        /// </summary>
        public void Update()
        {
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