using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 科技树系统 — 解锁分支研究节点，应用效果到玩家属性。
    /// 节点解锁消耗研究点数（每波结算产出），有前置依赖。
    /// </summary>
    public class TechTreeSystem
    {
        private readonly ComponentStore store;
        private readonly IRenderer renderer;
        private readonly int playerId;
        private TechTreeConfig config;

        // Per-player computed tech multipliers (applied to base stats on unlock)
        // Avoids recomputing on every attack
        private float _attackDamageMult = 1.0f;
        private float _attackSpeedMult = 1.0f;
        private float _maxHealthMult = 1.0f;
        private float _damageTakenMult = 1.0f;  // < 1.0 = less damage taken
        private float _goldOnKillMult = 1.0f;
        private float _allIncomeMult = 1.0f;
        private float _armorAdd = 0f;
        private float _critRateAdd = 0f;
        private float _critDamageMult = 1.0f;
        private float _armorPenetration = 0f;
        private float _goldOnWaveBonus = 0f;
        private float _lowHpRegenThreshold = 0f;  // if > 0, hp pct below which regen kicks in
        private float _lowHpRegenValue = 0f;
        private const float LOW_HP_REGEN_THRESHOLD = 0.30f;
        private bool _hasRespawn = false;
        private float _respawnHpPct = 0f;

        public TechTreeSystem(ComponentStore store, IRenderer renderer, int playerId, TechTreeConfig config)
        {
            this.store = store;
            this.renderer = renderer;
            this.playerId = playerId;
            this.config = config;
        }

        /// <summary>
        /// Reload config (for hot reload or game restart).
        /// </summary>
        public void ReloadConfig(TechTreeConfig newConfig)
        {
            this.config = newConfig;
        }

        /// <summary>
        /// Check if a node can be unlocked (prerequisites met + enough points + not already unlocked).
        /// </summary>
        public bool CanUnlock(string nodeId, out string reason)
        {
            var node = FindNode(nodeId);
            if (node == null)
            {
                reason = $"节点 '{nodeId}' 不存在";
                return false;
            }
            if (store.IsTechUnlocked(playerId, nodeId))
            {
                reason = $"节点 '{node.name}' 已解锁";
                return false;
            }
            int points = store.GetResearchPoints(playerId);
            if (points < node.cost)
            {
                reason = $"研究点数不足（需要 {node.cost}，当前 {points}）";
                return false;
            }
            foreach (var prereq in node.prerequisites)
            {
                if (!store.IsTechUnlocked(playerId, prereq))
                {
                    var prereqNode = FindNode(prereq);
                    reason = $"前置节点未解锁：'{(prereqNode != null ? prereqNode.name : prereq)}'";
                    return false;
                }
            }
            reason = null;
            return true;
        }

        /// <summary>
        /// Attempt to unlock a tech node. Returns true on success.
        /// </summary>
        public bool TryUnlock(string nodeId)
        {
            if (!CanUnlock(nodeId, out string reason))
            {
                if (reason != null) renderer.Log($"[TECH] 无法解锁 '{nodeId}': {reason}");
                return false;
            }
            var node = FindNode(nodeId);

            store.PlayerResearchPoints[playerId] -= node.cost;
            store.UnlockTech(playerId, nodeId);

            ApplyEffects(node.effects);

            renderer.Log($"[TECH] ✅ 解锁科技：{node.name}（消耗 {node.cost} 研究点数）");
            renderer.Log($"[TECH]    效果：{node.description}");
            return true;
        }

        /// <summary>
        /// Get all nodes currently available to unlock (prerequisites met, not yet unlocked).
        /// </summary>
        public List<TechNodeDef> GetAvailableNodes()
        {
            var available = new List<TechNodeDef>();
            foreach (var branch in config.branches)
            {
                foreach (var node in branch.nodes)
                {
                    if (store.IsTechUnlocked(playerId, node.id)) continue;
                    if (!CanUnlock(node.id, out _)) continue;
                    available.Add(node);
                }
            }
            return available;
        }

        /// <summary>
        /// Get the full tech tree config.
        /// </summary>
        public TechTreeConfig GetConfig() => config;

        /// <summary>
        /// Get research points per wave award.
        /// </summary>
        public int GetPointsPerWave() => config.researchPointsPerWave;

        /// <summary>
        /// Called when a wave completes — award research points.
        /// </summary>
        public void OnWaveComplete()
        {
            int award = config.researchPointsPerWave;
            store.AddResearchPoints(playerId, award);
            renderer.Log($"[TECH] 波次完成，+{award} 研究点数（当前: {store.GetResearchPoints(playerId)})");
        }

        /// <summary>
        /// Apply a tech effect to the player's computed stats.
        /// </summary>
        private void ApplyEffects(List<TechEffect> effects)
        {
            foreach (var eff in effects)
            {
                switch (eff.type)
                {
                    case "attack_damage_mult":     _attackDamageMult += eff.value; break;
                    case "attack_speed_mult":       _attackSpeedMult += eff.value; break;
                    case "max_health_mult":         _maxHealthMult += eff.value; break;
                    case "damage_taken_mult":       _damageTakenMult += eff.value; break;
                    case "gold_on_kill_mult":       _goldOnKillMult += eff.value; break;
                    case "all_income_mult":         _allIncomeMult += eff.value; break;
                    case "armor_add":               _armorAdd += eff.value; break;
                    case "crit_rate_add":           _critRateAdd += eff.value; break;
                    case "crit_damage_mult":        _critDamageMult += eff.value; break;
                    case "armor_penetration":       _armorPenetration += eff.value; break;
                    case "gold_on_wave_bonus":      _goldOnWaveBonus += eff.value; break;
                    case "low_hp_regen":
                        _lowHpRegenThreshold = LOW_HP_REGEN_THRESHOLD;
                        _lowHpRegenValue = eff.value;
                        break;
                    case "immunity_knockback":
                        // future: track immunity state
                        break;
                    case "respawn_once":
                        _hasRespawn = true;
                        _respawnHpPct = eff.value;
                        break;
                }
            }
        }

        /// <summary>
        /// Get final attack damage including tech multipliers.
        /// </summary>
        public float GetFinalAttackDamage()
        {
            return store.GetPlayerAttackDamage(playerId) * _attackDamageMult;
        }

        /// <summary>
        /// Get attack speed multiplier.
        /// </summary>
        public float GetAttackSpeedMult() => _attackSpeedMult;

        /// <summary>
        /// Get armor value.
        /// </summary>
        public float GetArmor() => _armorAdd;

        /// <summary>
        /// Get crit rate bonus.
        /// </summary>
        public float GetCritRateBonus() => _critRateAdd;

        /// <summary>
        /// Get crit damage multiplier.
        /// </summary>
        public float GetCritDamageMult() => _critDamageMult;

        /// <summary>
        /// Get gold on kill multiplier.
        /// </summary>
        public float GetGoldOnKillMult() => _goldOnKillMult;

        /// <summary>
        /// Get all income multiplier.
        /// </summary>
        public float GetAllIncomeMult() => _allIncomeMult;

        /// <summary>
        /// Get armor penetration ratio.
        /// </summary>
        public float GetArmorPenetration() => _armorPenetration;

        /// <summary>
        /// Get bonus gold on wave complete.
        /// </summary>
        public float GetGoldOnWaveBonus() => _goldOnWaveBonus;

        /// <summary>
        /// Check and apply low-HP regen. Returns amount healed.
        /// </summary>
        public float TickLowHpRegen()
        {
            if (_lowHpRegenValue <= 0f) return 0f;
            float hp = store.GetPlayerCurrentHealth(playerId);
            float maxHp = store.GetPlayerMaxHealth(playerId);
            if (hp > 0 && hp / maxHp < _lowHpRegenThreshold)
            {
                float heal = maxHp * _lowHpRegenValue;
                float newHp = Math.Min(maxHp, hp + heal);
                store.SetPlayerCurrentHealth(playerId, newHp);
                return newHp - hp;
            }
            return 0f;
        }

        /// <summary>
        /// Check if player has respawn and consume it. Returns true if respawn was used.
        /// </summary>
        public bool TryRespawn()
        {
            if (!_hasRespawn) return false;
            _hasRespawn = false;
            float maxHp = store.GetPlayerMaxHealth(playerId);
            store.SetPlayerCurrentHealth(playerId, maxHp * _respawnHpPct);
            renderer.Log($"[TECH] 不朽触发！玩家复活至 {(_respawnHpPct * 100):F0}% 生命");
            return true;
        }

        private TechNodeDef FindNode(string nodeId)
        {
            foreach (var branch in config.branches)
            {
                foreach (var node in branch.nodes)
                {
                    if (node.id == nodeId) return node;
                }
            }
            return null;
        }

        // ==================== Config Loader ====================

        public static TechTreeConfig LoadConfig(IRenderer logger)
        {
            string path = "Configs/tech_tree.json";
            if (!File.Exists(path))
            {
                logger.Log($"[TECH] 配置文件不存在: {path}，使用默认配置");
                return DefaultConfig();
            }
            try
            {
                string json = File.ReadAllText(path);
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var cfg = JsonSerializer.Deserialize<TechTreeConfig>(json, opts);
                logger.Log($"[TECH] 科技树配置加载成功: {cfg.branches.Count} 分支");
                foreach (var b in cfg.branches)
                    logger.Log($"[TECH]   分支 '{b.name}': {b.nodes.Count} 节点");
                return cfg;
            }
            catch (Exception ex)
            {
                logger.Log($"[TECH] 科技树配置加载失败: {ex.Message}，使用默认配置");
                return DefaultConfig();
            }
        }

        private static TechTreeConfig DefaultConfig()
        {
            return new TechTreeConfig
            {
                researchPointsPerWave = 1,
                branches = new List<TechBranchDef>()
            };
        }
    }
}
