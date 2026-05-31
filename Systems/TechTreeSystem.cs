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
        private readonly GameConfig gameConfig;
        private TechTreeConfig config;
        // O(1) node lookup — built once from config, rebuilt on ReloadConfig
        private Dictionary<string, TechNodeDef> _nodeLookup;

        // Per-player computed tech multipliers (applied to base stats on unlock)
        // Avoids recomputing on every attack
        private float _attackDamageMult = 1.0f;
        private float _attackSpeedMult = 1.0f;
        private float _maxHealthMult = 1.0f;
        private float _damageTakenMult = 1.0f;  // < 1.0 = less damage taken
        private float _goldOnKillMult = 1.0f;
        private float _allIncomeMult = 1.0f;
        private float _experiencePerKill = 0f;
        private float _goldOnEliteKill = 0f;
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
        private bool _hasKnockbackImmunity = false;
        // Enemy resistance multipliers (tech tree provides global bonuses, applied to all enemies)
        private float _enemyStunResistance = 0f;
        private float _enemyFreezeResistance = 0f;
        private float _enemySlowResistance = 0f;
        private float _enemyDamageResistance = 0f;
        // Armor shred per stack: flat armor reduction applied per stack (from AcidTower path upgrade)
        private float _armorShredPerStack = 0f;
        // Mana system bonuses (from tech tree)
        private float _maxManaBonus = 0f;
        private float _manaRegenBonus = 0f;
        private float _manaCostMultiplier = 1f;
        // Cooldown reduction bonus (multiplicative, e.g. 0.3 = 30% faster cooldowns)
        private float _cooldownReduction = 0f;
        // Tower slot bonus: additional tower slots from tech tree (e.g. +2 = can place 2 more towers)
        private int _towerSlotBonus = 0;

        public TechTreeSystem(ComponentStore store, IRenderer renderer, int playerId, TechTreeConfig config, GameConfig gameConfig = null)
        {
            this.store = store;
            this.renderer = renderer;
            this.playerId = playerId;
            this.config = config;
            this.gameConfig = gameConfig;
            BuildNodeLookup();
        }

        /// <summary>
        /// Build O(1) node lookup dictionary from config branches.
        /// </summary>
        private void BuildNodeLookup()
        {
            _nodeLookup = new Dictionary<string, TechNodeDef>();
            if (config?.branches == null) return;
            foreach (var branch in config.branches)
            {
                if (branch.nodes == null) continue;
                foreach (var node in branch.nodes)
                    _nodeLookup[node.id] = node;
            }
        }

        /// <summary>
        /// Reload config (for hot reload or game restart).
        /// </summary>
        public void ReloadConfig(TechTreeConfig newConfig)
        {
            this.config = newConfig;
            BuildNodeLookup();
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
                    case "experience_on_kill_add":  _experiencePerKill += eff.value; break;
                    case "gold_on_elite_kill":      _goldOnEliteKill += eff.value; break;
                    case "armor_add":               _armorAdd += eff.value; break;
                    case "crit_rate_add":           _critRateAdd += eff.value; break;
                    case "crit_damage_mult":        _critDamageMult += eff.value; break;
                    case "armor_penetration":       _armorPenetration += eff.value; break;
                    case "armor_shred":            _armorShredPerStack += eff.value; break;
                    case "gold_on_wave_bonus":      _goldOnWaveBonus += eff.value; break;
                    case "low_hp_regen":
                        _lowHpRegenThreshold = LOW_HP_REGEN_THRESHOLD;
                        _lowHpRegenValue = eff.value;
                        break;
                    case "immunity_knockback":
                        _hasKnockbackImmunity = true;
                        break;
                    case "respawn_once":
                        _hasRespawn = true;
                        _respawnHpPct = eff.value;
                        break;
                    case "stun_resist":
                        _enemyStunResistance += eff.value;
                        // Global stun resistance applied to all enemies (stored in global slot 0)
                        for (int i = 0; i < ComponentStore.MAX_ENTITIES; i++)
                            store.EnemyStunResistance[i] += eff.value;
                        break;
                    case "freeze_resist":
                        _enemyFreezeResistance += eff.value;
                        for (int i = 0; i < ComponentStore.MAX_ENTITIES; i++)
                            store.EnemyFreezeResistance[i] += eff.value;
                        break;
                    case "slow_resist":
                        _enemySlowResistance += eff.value;
                        for (int i = 0; i < ComponentStore.MAX_ENTITIES; i++)
                            store.EnemySlowResistance[i] += eff.value;
                        break;
                    case "damage_resist":
                        _enemyDamageResistance += eff.value;
                        for (int i = 0; i < ComponentStore.MAX_ENTITIES; i++)
                            store.EnemyDamageResistance[i] += eff.value;
                        break;
                    case "cooldown_reduction":
                        _cooldownReduction += eff.value;
                        store.PlayerCooldownReduction[playerId] = Math.Min(_cooldownReduction, 0.6f);
                        break;
                    case "tower_slot_bonus":
                        _towerSlotBonus += (int)eff.value;
                        store.PlayerMaxTowers[playerId] = 20 + _towerSlotBonus;
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
        /// Get attack damage multiplier from tech tree.
        /// </summary>
        public float GetAttackDamageMult() => _attackDamageMult;

        /// <summary>
        /// Get attack speed multiplier.
        /// </summary>
        public float GetAttackSpeedMult() => _attackSpeedMult;

        /// <summary>
        /// Get armor value.
        /// </summary>
        public float GetArmor() => _armorAdd;

        /// <summary>
        /// Get damage taken multiplier (tech tree reduces incoming damage).
        /// </summary>
        public float GetDamageTakenMult() => _damageTakenMult;

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
        /// Get bonus experience per kill.
        /// </summary>
        public float GetExperiencePerKill() => _experiencePerKill;

        /// <summary>
        /// Get bonus gold awarded when an elite enemy is killed.
        /// </summary>
        public float GetGoldOnEliteKill() => _goldOnEliteKill;

        /// <summary>
        /// Get armor penetration ratio.
        /// </summary>
        public float GetArmorPenetration() => _armorPenetration;

        /// <summary>
        /// Get armor shred per stack (flat reduction per armor shred stack applied to enemies).
        /// </summary>
        public float GetArmorShredPerStack() => _armorShredPerStack;

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
            if (hp > 0 && maxHp > 0 && hp / maxHp < _lowHpRegenThreshold)
            {
                float heal = maxHp * _lowHpRegenValue;
                float newHp = Math.Min(maxHp, hp + heal);
                store.SetPlayerCurrentHealth(playerId, newHp);
                return newHp - hp;
            }
            return 0f;
        }

/// <summary>
        /// Returns true if the player has immunity to knockback (enemy dodge lateral movement).
        /// </summary>
        public bool GetKnockbackImmunity() => _hasKnockbackImmunity;

        /// <summary>
        /// Get global stun resistance multiplier (tech tree bonus).
        /// </summary>
        public float GetStunResistance() => _enemyStunResistance;

        /// <summary>
        /// Get global freeze resistance multiplier (tech tree bonus).
        /// </summary>
        public float GetFreezeResistance() => _enemyFreezeResistance;

        /// <summary>
        /// Get global slow resistance multiplier (tech tree bonus).
        /// </summary>
        public float GetSlowResistance() => _enemySlowResistance;

        /// <summary>
        /// Get global damage resistance multiplier (tech tree bonus).
        /// </summary>
        public float GetDamageResistance() => _enemyDamageResistance;

        /// <summary>
        /// Get max mana bonus from tech tree (flat additive bonus to max mana).
        /// </summary>
        public float GetMaxManaBonus() => _maxManaBonus;

        /// <summary>
        /// Get mana regen bonus from tech tree (additive bonus to regen/sec).
        /// </summary>
        public float GetManaRegenBonus() => _manaRegenBonus;

        /// <summary>
        /// Get mana cost multiplier from tech tree (multiplicative cost modifier).
        /// Values less than 1.0 reduce mana costs (discount), greater than 1.0 increase them.
        /// </summary>
        public float GetManaCostMultiplier() => _manaCostMultiplier;

        /// <summary>
        /// Get cooldown reduction bonus from tech tree (multiplicative, e.g. 0.3 = 30% faster cooldowns).
        /// </summary>
        public float GetCooldownReduction() => _cooldownReduction;

        /// <summary>
        /// Get tower slot bonus from tech tree (additional tower slots beyond the base 20).
        /// </summary>
        public int GetTowerSlotBonus() => _towerSlotBonus;

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

        /// <summary>
        /// Get wave-based damage scaling multiplier for a given wave number.
        /// Formula: 1.0 + (waveNumber - 1) * PlayerDamageScalingPerWave
        /// </summary>
        public float GetWaveDifficultyMultiplier(int waveNumber)
        {
            if (waveNumber <= 0) return 1.0f;
            float growthPerWave = gameConfig?.PlayerDamageScalingPerWave ?? 0.05f;
            return 1.0f + (waveNumber - 1) * growthPerWave;
        }

        private TechNodeDef FindNode(string nodeId)
        {
            return _nodeLookup.TryGetValue(nodeId, out var node) ? node : null;
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
