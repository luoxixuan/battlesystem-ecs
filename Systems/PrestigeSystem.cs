using System;
using System.Collections.Generic;
using System.IO;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Player Prestige / Meta Progression system.
    ///
    /// Manages cross-run unlocks for the roguelike meta layer (Hades / Slay-the-Spire / Risk-of-Rain pattern).
    /// Each completed run earns "Stardust" currency which can be spent to permanently unlock
    /// nodes (e.g. "+2% base tower damage", "+50 starting gold", "+1 starting life").
    /// Bonuses stack across all unlocked nodes and apply on the next run.
    ///
    /// Persistence model:
    ///   - Node definitions: Data/Configs/meta_progression.json (read-only, version-controlled)
    ///   - Player unlocks:   Data/Saves/meta_progression.json (writable, persists across runs)
    ///
    /// Integration points:
    ///   - GameManager: calls Load() on startup, Apply() to populate GameConfig.Meta*Mult
    ///   - PlayerTowerAttackSystem: reads GameConfig.MetaDamageMult / MetaAttackSpeedMult / MetaCritRateBonus
    ///   - GoldSystem: reads GameConfig.MetaGoldEarnedMult
    ///   - InitializePlayer(): reads GameConfig.MetaStartingGoldBonus / MetaStartingLivesBonus
    ///
    /// Usage:
    ///   var prestige = new PrestigeSystem(logger, gameConfig);
    ///   prestige.Load();                  // load saved unlocks + node defs
    ///   prestige.ApplyToConfig();         // resolve multipliers into GameConfig.Meta*Mult
    ///   // At end of run:
    ///   prestige.GrantStardust(earnedAmount);
    ///   prestige.UnlockNode("damage_1");  // returns true if stardust + prereq satisfied
    ///   prestige.Save();                  // persist
    /// </summary>
    public class PrestigeSystem
    {
        private readonly IRenderer logger;
        private readonly GameConfig gameConfig;
        private readonly string _savePath;

        // Cached node definitions (loaded from config or hardcoded fallback)
        private readonly List<MetaProgressionNode> _nodeDefs = new List<MetaProgressionNode>();

        // Persistent state (loaded from save file)
        private int _stardust = 0;
        private readonly Dictionary<string, int> _unlockedRanks = new Dictionary<string, int>();

        public PrestigeSystem(IRenderer renderer, GameConfig config, string saveDir = "Data/Saves")
        {
            this.logger = renderer;
            this.gameConfig = config;
            this._savePath = Path.Combine(saveDir, "meta_progression.json");

            string dir = Path.GetDirectoryName(_savePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        /// <summary>
        /// Current stardust balance (read-only).
        /// </summary>
        public int Stardust => _stardust;

        /// <summary>
        /// Returns the unlock rank (count) for a node, or 0 if not unlocked.
        /// </summary>
        public int GetNodeRank(string nodeId)
        {
            return _unlockedRanks.TryGetValue(nodeId, out var r) ? r : 0;
        }

        /// <summary>
        /// Loads meta-progression state. Safe to call before SaveSystem is initialized.
        /// </summary>
        public void Load()
        {
            // 1. Load node definitions (prefer config file; fall back to defaults)
            LoadNodeDefinitions();

            // 2. Load save data (unlocked ranks + stardust)
            LoadSaveData();
        }

        /// <summary>
        /// Apply current unlocks to GameConfig.Meta*Mult fields.
        /// Call this once at boot after Load() and before any system reads those fields.
        /// </summary>
        public void ApplyToConfig()
        {
            if (gameConfig == null) return;

            // Reset to defaults first
            gameConfig.MetaDamageMult = 1.0f;
            gameConfig.MetaGoldEarnedMult = 1.0f;
            gameConfig.MetaStartingGoldBonus = 0f;
            gameConfig.MetaStartingLivesBonus = 0;
            gameConfig.MetaCritRateBonus = 0f;
            gameConfig.MetaAttackSpeedMult = 1.0f;
            gameConfig.MetaFreeTechLevels = 0;

            // Apply each node, multiplied by its current rank
            foreach (var node in _nodeDefs)
            {
                int rank = GetNodeRank(node.Id);
                if (rank <= 0) continue;

                gameConfig.MetaDamageMult *= (float)Math.Pow(node.DamageMult, rank);
                gameConfig.MetaGoldEarnedMult *= (float)Math.Pow(node.GoldEarnedMult, rank);
                gameConfig.MetaAttackSpeedMult *= (float)Math.Pow(node.AttackSpeedMult, rank);
                gameConfig.MetaStartingGoldBonus += node.StartingGoldBonus * rank;
                gameConfig.MetaStartingLivesBonus += node.StartingLivesBonus * rank;
                gameConfig.MetaCritRateBonus += node.CritRateBonus * rank;
                gameConfig.MetaFreeTechLevels += node.FreeTechLevels * rank;
            }

            // Also seed GameConfig.PrestigeNodes for any UI/save code that wants to enumerate
            gameConfig.PrestigeNodes = new List<MetaProgressionNode>(_nodeDefs);

            logger?.Log($"[PRESTIGE] Applied {_unlockedRanks.Count} unlocked node(s); Stardust={_stardust}");
            logger?.Log($"[PRESTIGE]   MetaDamageMult={gameConfig.MetaDamageMult:F3}, MetaAttackSpeedMult={gameConfig.MetaAttackSpeedMult:F3}, MetaCritRateBonus={gameConfig.MetaCritRateBonus:F3}");
            logger?.Log($"[PRESTIGE]   MetaGoldEarnedMult={gameConfig.MetaGoldEarnedMult:F3}, MetaStartingGoldBonus={gameConfig.MetaStartingGoldBonus:F0}, MetaStartingLivesBonus={gameConfig.MetaStartingLivesBonus}");
        }

        /// <summary>
        /// Grant stardust (called at run-end).
        /// </summary>
        public void GrantStardust(int amount)
        {
            if (amount <= 0) return;
            _stardust += amount;
            logger?.Log($"[PRESTIGE] +{amount} Stardust (total: {_stardust})");
        }

        /// <summary>
        /// Try to unlock (rank up) a node. Validates cost, prerequisite, and max rank.
        /// Returns true if the node was successfully upgraded.
        /// </summary>
        public bool UnlockNode(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId)) return false;
            var def = _nodeDefs.Find(n => n.Id == nodeId);
            if (def == null)
            {
                logger?.Log($"[PRESTIGE] Unknown node: {nodeId}");
                return false;
            }

            int currentRank = GetNodeRank(nodeId);
            int maxRank = def.MaxRank > 0 ? def.MaxRank : int.MaxValue;
            if (currentRank >= maxRank)
            {
                logger?.Log($"[PRESTIGE] Node '{nodeId}' already at max rank ({maxRank})");
                return false;
            }

            // Prerequisite check
            if (!string.IsNullOrEmpty(def.PrerequisiteId) && GetNodeRank(def.PrerequisiteId) <= 0)
            {
                logger?.Log($"[PRESTIGE] Node '{nodeId}' requires '{def.PrerequisiteId}' unlocked first");
                return false;
            }

            // Cost check
            if (_stardust < def.Cost)
            {
                logger?.Log($"[PRESTIGE] Insufficient stardust for '{nodeId}': need {def.Cost}, have {_stardust}");
                return false;
            }

            _stardust -= def.Cost;
            _unlockedRanks[nodeId] = currentRank + 1;
            logger?.Log($"[PRESTIGE] Unlocked '{def.Name}' (rank {currentRank + 1}/{maxRank}) — {def.Cost} Stardust, remaining: {_stardust}");
            return true;
        }

        /// <summary>
        /// Save current stardust + unlocked ranks to disk.
        /// </summary>
        public void Save()
        {
            try
            {
                var data = new SaveModel
                {
                    Stardust = _stardust,
                    UnlockedNodes = new List<UnlockedNodeEntry>()
                };
                foreach (var kvp in _unlockedRanks)
                {
                    data.UnlockedNodes.Add(new UnlockedNodeEntry { Id = kvp.Key, Rank = kvp.Value });
                }
                string json = System.Text.Json.JsonSerializer.Serialize(data, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_savePath, json);
                logger?.Log($"[PRESTIGE] Saved to {_savePath}");
            }
            catch (Exception ex)
            {
                logger?.Log($"[PRESTIGE] Save failed: {ex.Message}");
            }
        }

        // ── Private helpers ──────────────────────────────────────────────

        private void LoadNodeDefinitions()
        {
            _nodeDefs.Clear();
            const string path = "Data/Configs/meta_progression.json";
            try
            {
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("nodes", out var arr))
                    {
                        foreach (var elem in arr.EnumerateArray())
                        {
                            var def = new MetaProgressionNode
                            {
                                Id = elem.TryGetProperty("id", out var id) ? id.GetString() : "",
                                Name = elem.TryGetProperty("name", out var nm) ? nm.GetString() : "",
                                Description = elem.TryGetProperty("description", out var d) ? d.GetString() : "",
                                Cost = elem.TryGetProperty("cost", out var c) ? c.GetInt32() : 10,
                                MaxRank = elem.TryGetProperty("maxRank", out var mr) ? mr.GetInt32() : 1,
                                PrerequisiteId = elem.TryGetProperty("prerequisite", out var p) ? p.GetString() ?? "" : "",
                                DamageMult = elem.TryGetProperty("damageMult", out var dm) ? dm.GetSingle() : 1.0f,
                                GoldEarnedMult = elem.TryGetProperty("goldEarnedMult", out var gem) ? gem.GetSingle() : 1.0f,
                                AttackSpeedMult = elem.TryGetProperty("attackSpeedMult", out var asm) ? asm.GetSingle() : 1.0f,
                                StartingGoldBonus = elem.TryGetProperty("startingGoldBonus", out var sgb) ? sgb.GetSingle() : 0f,
                                StartingLivesBonus = elem.TryGetProperty("startingLivesBonus", out var slb) ? slb.GetInt32() : 0,
                                CritRateBonus = elem.TryGetProperty("critRateBonus", out var crb) ? crb.GetSingle() : 0f,
                                FreeTechLevels = elem.TryGetProperty("freeTechLevels", out var ftl) ? ftl.GetInt32() : 0,
                            };
                            if (!string.IsNullOrEmpty(def.Id))
                                _nodeDefs.Add(def);
                        }
                        logger?.Log($"[PRESTIGE] Loaded {_nodeDefs.Count} node definitions from {path}");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Log($"[PRESTIGE] Failed to load node defs: {ex.Message}");
            }

            // Fallback: a small built-in tree so prestige is non-empty out of the box
            _nodeDefs.Add(new MetaProgressionNode
            {
                Id = "damage_1", Name = "Damage I", Description = "+5% base player damage", Cost = 10, MaxRank = 5,
                DamageMult = 1.05f
            });
            _nodeDefs.Add(new MetaProgressionNode
            {
                Id = "gold_1", Name = "Gold I", Description = "+10% gold earned", Cost = 10, MaxRank = 5,
                GoldEarnedMult = 1.10f
            });
            _nodeDefs.Add(new MetaProgressionNode
            {
                Id = "starting_gold_1", Name = "Treasure Hoard", Description = "+25 starting gold", Cost = 15, MaxRank = 4,
                StartingGoldBonus = 25f
            });
            _nodeDefs.Add(new MetaProgressionNode
            {
                Id = "lives_1", Name = "Extra Life", Description = "+1 starting life", Cost = 20, MaxRank = 3,
                PrerequisiteId = "starting_gold_1", StartingLivesBonus = 1
            });
            _nodeDefs.Add(new MetaProgressionNode
            {
                Id = "speed_1", Name = "Quick Hands", Description = "+3% attack speed", Cost = 15, MaxRank = 3,
                AttackSpeedMult = 1.03f
            });
            _nodeDefs.Add(new MetaProgressionNode
            {
                Id = "crit_1", Name = "Sharp Eye", Description = "+2% crit rate", Cost = 25, MaxRank = 3,
                PrerequisiteId = "damage_1", CritRateBonus = 0.02f
            });
            _nodeDefs.Add(new MetaProgressionNode
            {
                Id = "tech_1", Name = "Tech Savant", Description = "1 free tech-tree level", Cost = 30, MaxRank = 2,
                PrerequisiteId = "speed_1", FreeTechLevels = 1
            });
            logger?.Log($"[PRESTIGE] Loaded {_nodeDefs.Count} built-in default nodes");
        }

        private void LoadSaveData()
        {
            _unlockedRanks.Clear();
            _stardust = 0;
            if (!File.Exists(_savePath)) return;
            try
            {
                string json = File.ReadAllText(_savePath);
                var data = System.Text.Json.JsonSerializer.Deserialize<SaveModel>(json);
                if (data == null) return;
                _stardust = data.Stardust;
                if (data.UnlockedNodes != null)
                {
                    foreach (var n in data.UnlockedNodes)
                    {
                        if (!string.IsNullOrEmpty(n.Id) && n.Rank > 0)
                            _unlockedRanks[n.Id] = n.Rank;
                    }
                }
                logger?.Log($"[PRESTIGE] Loaded save: {_stardust} Stardust, {_unlockedRanks.Count} unlocked node(s)");
            }
            catch (Exception ex)
            {
                logger?.Log($"[PRESTIGE] Failed to load save: {ex.Message}");
            }
        }

        // ── Serialization models ─────────────────────────────────────────
        private class SaveModel
        {
            public int Stardust { get; set; }
            public List<UnlockedNodeEntry> UnlockedNodes { get; set; } = new List<UnlockedNodeEntry>();
        }
        private class UnlockedNodeEntry
        {
            public string Id { get; set; } = "";
            public int Rank { get; set; } = 1;
        }
    }
}
