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
            // Round 103 — Buff Share: subscribe to entity-invalidated events so the per-frame
            // base-attack-speed cache is purged when a tower is destroyed, removed, or
            // recycled (Claude bug scan fix #2: stale cache on ID reuse).
            ComponentStore.OnTowerEntityInvalidated += InvalidateBuffShareCache;
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

        // ─────────────────────────────────────────────────────────────────────
        // Buff Share (Round 103 Direction 8)
        //
        // Towers with TowerBuffShareRadius > 0 AND a non-zero TowerBuffShareMask
        // share a snapshot of their own attack speed with nearby friendly towers.
        // The shared bonus is APPLIED to TowerAttackSpeed in-place (multiplicative),
        // and the original base value is cached in _baseAttackSpeedByTowerId so the
        // next frame's ResolveBuffShares() can restore it before reapplying — this
        // prevents frame-over-frame compound growth (a classic multiplicative-bug
        // pattern caught by the bug scanner).
        //
        // CRITICAL (Claude bug scan #1, Round 103): The cache MUST be keyed by
        // tower entity ID, NOT by position in ActiveTowerIds. ActiveTowerIds is a
        // swap-and-pop list whose order mutates when towers are added/destroyed
        // between frames — position-keyed cache would silently apply one tower's
        // base stat to a completely different tower.
        //
        // Cost: O(N²) tower×tower pair scan (N ≤ 200 active towers typically),
        //       but gated by a quick "any sharing tower" check at the top for
        //       the zero-overhead fast path when no tower has a share radius set.
        // ─────────────────────────────────────────────────────────────────────
        private const int CACHE_SLOT_COUNT = 16; // small, plenty for typical max-tower counts
        private int[] _cachedTowerId = new int[CACHE_SLOT_COUNT];
        private float[] _baseAttackSpeed = new float[CACHE_SLOT_COUNT];
        private int _cacheUsed = 0; // number of valid entries (entries with non-zero _cachedTowerId)

        private int FindCacheSlot(int towerId)
        {
            for (int i = 0; i < _cacheUsed; i++)
                if (_cachedTowerId[i] == towerId) return i;
            return -1;
        }

        /// <summary>
        /// Round 103 — Buff Share: drop any cached base-attack-speed entry for the given
        /// towerId. Called from ComponentStore.OnTowerEntityInvalidated when a tower is
        /// destroyed, removed, or a new tower occupies a recycled entityId.
        /// Claude bug scan fix #2: stale cache on ID reuse.
        /// </summary>
        public void InvalidateBuffShareCache(int towerId)
        {
            int slot = FindCacheSlot(towerId);
            if (slot < 0) return;
            int last = _cacheUsed - 1;
            if (slot != last)
            {
                _cachedTowerId[slot] = _cachedTowerId[last];
                _baseAttackSpeed[slot] = _baseAttackSpeed[last];
            }
            _cacheUsed = last;
        }

        public void ResolveBuffShares()
        {
            var activeTowerIds = store.ActiveTowerIds;
            int count = activeTowerIds.Count;
            if (count == 0)
            {
                _cacheUsed = 0;
                return;
            }

            // Fast-path: skip the entire pass if no sharing tower exists this frame
            bool anyShare = false;
            for (int i = 0; i < count; i++)
            {
                int tid = activeTowerIds[i];
                if (store.TowerBuffShareRadius[tid] > 0f && store.TowerBuffShareMask[tid] != 0)
                {
                    anyShare = true;
                    break;
                }
            }
            if (!anyShare)
            {
                // Restore base speed for any tower that might have a stale shared value from
                // a prior frame (e.g. the share tower was just sold), so speed returns to base.
                // Also opportunistically drop cache entries whose tower is no longer active.
                for (int i = _cacheUsed - 1; i >= 0; i--)
                {
                    int tid = _cachedTowerId[i];
                    int slot = FindCacheSlot(tid);
                    if (slot < 0) continue;
                    if (store.TowerActive[tid])
                    {
                        store.TowerAttackSpeed[tid] = _baseAttackSpeed[slot];
                    }
                    else
                    {
                        // Tower was removed/destroyed — drop its cache entry (swap with last)
                        int last = _cacheUsed - 1;
                        if (slot != last)
                        {
                            _cachedTowerId[slot] = _cachedTowerId[last];
                            _baseAttackSpeed[slot] = _baseAttackSpeed[last];
                        }
                        _cacheUsed = last;
                    }
                }
                return;
            }

            // Step 1: restore each cached tower's attack speed to its base value.
            // Cache is keyed by towerId, so order changes in ActiveTowerIds don't matter.
            for (int i = _cacheUsed - 1; i >= 0; i--)
            {
                int tid = _cachedTowerId[i];
                if (store.TowerActive[tid])
                {
                    store.TowerAttackSpeed[tid] = _baseAttackSpeed[i];
                }
                else
                {
                    // Tower was removed/destroyed — drop its cache entry
                    int last = _cacheUsed - 1;
                    if (i != last)
                    {
                        _cachedTowerId[i] = _cachedTowerId[last];
                        _baseAttackSpeed[i] = _baseAttackSpeed[last];
                    }
                    _cacheUsed = last;
                }
            }

            // Step 2: for each sharing tower, scan all towers within radius² and apply
            // the configured share bits multiplicatively to the target's speed.
            float efficiency = BuffShareConfig.DefaultShareEfficiencyPct;
            for (int si = 0; si < count; si++)
            {
                int shareId = activeTowerIds[si];
                float shareRadius = store.TowerBuffShareRadius[shareId];
                if (shareRadius <= 0f) continue;
                int shareMask = store.TowerBuffShareMask[shareId];
                if (shareMask == 0) continue;
                if (!store.TowerActive[shareId]) continue;
                if (store.TowerIsDispelled[shareId]) continue; // dispel clears the share buff

                float sx = store.PositionX[shareId];
                float sy = store.PositionY[shareId];
                float radiusSq = shareRadius * shareRadius;

                for (int ti = 0; ti < count; ti++)
                {
                    if (ti == si) continue; // skip self
                    int targetId = activeTowerIds[ti];
                    if (!store.TowerActive[targetId]) continue;
                    if (store.TowerIsDispelled[targetId]) continue;

                    float tx = store.PositionX[targetId];
                    float ty = store.PositionY[targetId];
                    float dx = tx - sx;
                    float dy = ty - sy;
                    if (dx * dx + dy * dy > radiusSq) continue;

                    // Seed base speed on first share received (keyed by towerId)
                    int slot = FindCacheSlot(targetId);
                    if (slot < 0)
                    {
                        if (_cacheUsed >= _cachedTowerId.Length)
                        {
                            // Grow cache (rare; CACHE_SLOT_COUNT=16 is plenty for typical play)
                            int newSize = _cachedTowerId.Length * 2;
                            int[] newIds = new int[newSize];
                            float[] newSpeeds = new float[newSize];
                            Array.Copy(_cachedTowerId, newIds, _cacheUsed);
                            Array.Copy(_baseAttackSpeed, newSpeeds, _cacheUsed);
                            _cachedTowerId = newIds;
                            _baseAttackSpeed = newSpeeds;
                        }
                        slot = _cacheUsed++;
                        _cachedTowerId[slot] = targetId;
                        _baseAttackSpeed[slot] = store.TowerAttackSpeed[targetId];
                    }

                    if ((shareMask & BuffShareConfig.ShareAttackSpeed) != 0)
                    {
                        store.TowerAttackSpeed[targetId] *= (1f + efficiency);
                    }
                }
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