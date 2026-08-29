#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Round 144 — Hero Active Skill Set System.
    ///
    /// HeroSystem ships a placeholder hero (move + auto-attack) and the original
    /// design comment flagged "Skills: can have active abilities (future extension)".
    /// This system implements the minimum viable active-skill set:
    ///   • per-hero, per-slot cooldown (4 slots × MAX_HEROES = 20 floats)
    ///   • per-slot skill id (4 × MAX_HEROES = 20 ints) — id maps to GameConfig.SkillDefs
    ///   • a single public TriggerHeroSkill(heroId, slot) gate — returns true on cast
    ///   • a per-frame Update() that decrements cooldowns on deployed heroes
    ///   • loads skill slots from Data/Configs/hero_skills.json on Initialize()
    ///   • soft-coupling: the effect dispatch (damage / AOE / heal) is a no-op log
    ///     line, mirroring the Round 138 TowerActiveSkillSystem approach. The gate
    ///     + cooldown contract is the value; effect integration is a follow-up.
    ///
    /// Design notes:
    ///   • Inert by default: slotId == -1 → no per-tick work beyond a single `if`.
    ///   • Storage: 2D arrays flattened to [heroId * MAX_HERO_SKILLS + slot]
    ///     (20 slots total) for cache locality.
    ///   • No HUD binding: renderer polls IsHeroSkillReady() / GetHeroSkillCooldown().
    ///   • Safe even if hero_skills.json is missing: falls back to all slots = -1
    ///     (no skills configured) and the system is a per-frame no-op.
    /// </summary>
    public class HeroSkillSystem
    {
        /// <summary>Number of skill slots per hero (designer-tunable, kept small for UI).</summary>
        public const int MAX_HERO_SKILLS = 4;

        private readonly ComponentStore store;
        private readonly int playerId;
        private GameConfig? _config;
        private readonly string _heroSkillsPath;

        // Flattened [heroId * MAX_HERO_SKILLS + slot]; -1 = empty.
        private int[] _heroSkillIds = new int[ComponentStore.MAX_HEROES * MAX_HERO_SKILLS];
        // Cooldowns in seconds, flat-indexed the same way.
        private float[] _heroSkillCooldowns = new float[ComponentStore.MAX_HEROES * MAX_HERO_SKILLS];
        // Cooldown max mirror (so HUD can show "ready in Xs" without re-parsing).
        private float[] _heroSkillCooldownMax = new float[ComponentStore.MAX_HEROES * MAX_HERO_SKILLS];
        // Track which slots have ever been configured — so we don't accidentally
        // overwrite a real skill id with -1 on a re-init.
        private bool[] _slotInitialized = new bool[ComponentStore.MAX_HEROES * MAX_HERO_SKILLS];

        // Cached "any slot is configured" sentinel for O(1) fast-path in Update().
        private bool _anySkillConfigured;

        public HeroSkillSystem(ComponentStore store, int playerId, string? heroSkillsPath = null, GameConfig? config = null)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.playerId = playerId;
            _config = config;
            _heroSkillsPath = heroSkillsPath ?? Path.Combine("Data", "Configs", "hero_skills.json");
            // Init all slots to -1 (no skill) so the system is a no-op until configured.
            for (int i = 0; i < _heroSkillIds.Length; i++) _heroSkillIds[i] = -1;
        }

        /// <summary>
        /// Optional late binding of GameConfig — lets the system resolve a skillId
        /// to a human-readable name in logs. Not required for the gate to work.
        /// </summary>
        public void SetConfig(GameConfig config) => _config = config;

        /// <summary>
        /// Load skill slot assignments from Data/Configs/hero_skills.json.
        /// JSON format (missing file → no-op, all slots stay -1):
        ///   {
        ///     "Description": "...",
        ///     "Skills": [
        ///       { "SlotIndex": 0, "SkillName": "Cross Slash" },
        ///       { "SlotIndex": 1, "SkillName": "Heal" }
        ///     ]
        ///   }
        /// SkillName is looked up against GameConfig.SkillDefs (case-insensitive
        /// Name match); if not found the slot stays -1 and a log line is emitted.
        /// Safe to call multiple times — re-init is idempotent.
        /// </summary>
        public void Initialize()
        {
            _anySkillConfigured = false;
            for (int i = 0; i < _heroSkillIds.Length; i++) _heroSkillIds[i] = -1;
            for (int i = 0; i < _slotInitialized.Length; i++) _slotInitialized[i] = false;

            if (!File.Exists(_heroSkillsPath))
            {
                // Missing file is a soft fallback — system stays inert.
                return;
            }

            try
            {
                string json = File.ReadAllText(_heroSkillsPath);
                var def = HeroSkillsConfigLoader.Parse(json);
                if (def?.Skills == null) return;

                foreach (var entry in def.Skills)
                {
                    if (entry.SlotIndex < 0 || entry.SlotIndex >= MAX_HERO_SKILLS) continue;
                    if (string.IsNullOrWhiteSpace(entry.SkillName)) continue;

                    int resolvedId = ResolveSkillIdByName(entry.SkillName);
                    if (resolvedId < 0) continue;

                    // Apply to all hero slots (every hero shares the same skill set —
                    // the per-slot cooldown is what differentiates them in play).
                    for (int heroId = 0; heroId < ComponentStore.MAX_HEROES; heroId++)
                    {
                        int flatIdx = heroId * MAX_HERO_SKILLS + entry.SlotIndex;
                        _heroSkillIds[flatIdx] = resolvedId;
                        _slotInitialized[flatIdx] = true;
                        _heroSkillCooldownMax[flatIdx] = ResolveCooldownForSkill(resolvedId);
                    }
                    _anySkillConfigured = true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HERO_SKILL] failed to load {_heroSkillsPath}: {ex.Message}");
            }
        }

        /// <summary>
        /// Per-frame cooldown tick. Only runs if at least one skill slot is configured
        /// (sentinel check avoids O(MAX_HEROES) loop when the system is inert).
        /// Walks deployed heroes only — undeployed heroes don't tick.
        /// </summary>
        public void Update(float deltaTime)
        {
            if (deltaTime <= 0f) return;
            if (!_anySkillConfigured) return;

            for (int heroId = 0; heroId < ComponentStore.MAX_HEROES; heroId++)
            {
                if (!store.HeroIsDeployed[heroId]) continue;
                int baseIdx = heroId * MAX_HERO_SKILLS;
                for (int slot = 0; slot < MAX_HERO_SKILLS; slot++)
                {
                    int flatIdx = baseIdx + slot;
                    if (_heroSkillIds[flatIdx] < 0) continue;
                    float cd = _heroSkillCooldowns[flatIdx];
                    if (cd <= 0f) continue;
                    cd -= deltaTime;
                    if (cd < 0f) cd = 0f;
                    _heroSkillCooldowns[flatIdx] = cd;
                }
            }
        }

        /// <summary>
        /// Public API: player/HUD calls this when the player presses a hotkey
        /// bound to a hero's skill slot. Returns true if the cast succeeded
        /// (hero deployed, slot configured, cooldown gate passed).
        /// Effect dispatch is intentionally a log line + cooldown flip — same
        /// pattern as TowerActiveSkillSystem.TriggerTowerActive (Round 138).
        /// </summary>
        public bool TriggerHeroSkill(int heroId, int slot)
        {
            if (heroId < 0 || heroId >= ComponentStore.MAX_HEROES) return false;
            if (slot < 0 || slot >= MAX_HERO_SKILLS) return false;
            if (!store.HeroIsDeployed[heroId]) return false;

            int flatIdx = heroId * MAX_HERO_SKILLS + slot;
            int skillId = _heroSkillIds[flatIdx];
            if (skillId < 0) return false;
            if (_heroSkillCooldowns[flatIdx] > 0f) return false;

            // Gate passed — flip the cooldown to its max and emit the log.
            _heroSkillCooldowns[flatIdx] = _heroSkillCooldownMax[flatIdx];
            string skillName = ResolveSkillNameById(skillId);
            Console.WriteLine($"[HERO_SKILL] hero={heroId} slot={slot} cast skillId={skillId} ({skillName}) cd={_heroSkillCooldownMax[flatIdx]:F1}s");
            return true;
        }

        /// <summary>Read-only helper for HUD/renderer.</summary>
        public bool IsHeroSkillReady(int heroId, int slot)
        {
            if (heroId < 0 || heroId >= ComponentStore.MAX_HEROES) return false;
            if (slot < 0 || slot >= MAX_HERO_SKILLS) return false;
            int flatIdx = heroId * MAX_HERO_SKILLS + slot;
            return _heroSkillIds[flatIdx] >= 0 && _heroSkillCooldowns[flatIdx] <= 0f;
        }

        /// <summary>Returns the configured skill id for a slot, or -1 if none.</summary>
        public int GetHeroSkillId(int heroId, int slot)
        {
            if (heroId < 0 || heroId >= ComponentStore.MAX_HEROES) return -1;
            if (slot < 0 || slot >= MAX_HERO_SKILLS) return -1;
            int flatIdx = heroId * MAX_HERO_SKILLS + slot;
            return _heroSkillIds[flatIdx];
        }

        /// <summary>Returns the current cooldown in seconds (0 = ready).</summary>
        public float GetHeroSkillCooldown(int heroId, int slot)
        {
            if (heroId < 0 || heroId >= ComponentStore.MAX_HEROES) return 0f;
            if (slot < 0 || slot >= MAX_HERO_SKILLS) return 0f;
            int flatIdx = heroId * MAX_HERO_SKILLS + slot;
            return _heroSkillCooldowns[flatIdx];
        }

        /// <summary>Returns the max cooldown for a slot (0 if no skill).</summary>
        public float GetHeroSkillCooldownMax(int heroId, int slot)
        {
            if (heroId < 0 || heroId >= ComponentStore.MAX_HEROES) return 0f;
            if (slot < 0 || slot >= MAX_HERO_SKILLS) return 0f;
            int flatIdx = heroId * MAX_HERO_SKILLS + slot;
            return _heroSkillCooldownMax[flatIdx];
        }

        /// <summary>True if at least one skill slot is configured (used by Update fast-path).</summary>
        public bool HasAnyConfiguredSkill() => _anySkillConfigured;

        // ── Private helpers ───────────────────────────────────────────────
        // 技能 id 归一化索引空间的唯一约定在 GameConfig（GetSkillIdByName /
        // TryGetSkillById）：[0, SkillDefs.Count) 索引共享定义表（Data/Configs/
        // skills.json + Data/Skills/*.json），其后索引玩家技能栏回退。此处只做委托，
        // 勿在此重新实现遍历 —— 此前只在 _config.Skills（占位技能）里找名字，
        // hero_skills.json 引用的精选技能名永远解析失败 → 槽位恒 -1 → 系统休眠。

        private int ResolveSkillIdByName(string name) => _config?.GetSkillIdByName(name) ?? -1;

        private SkillConfig? ResolveSkillConfigById(int skillId) => _config?.TryGetSkillById(skillId);

        private string ResolveSkillNameById(int skillId) => ResolveSkillConfigById(skillId)?.Name ?? "?";

        private float ResolveCooldownForSkill(int skillId)
        {
            // Cooldown is in seconds per the SkillConfig contract. Default to 5s if not set
            // so the gate is observable even if a designer forgot to set it.
            float cd = ResolveSkillConfigById(skillId)?.Cooldown ?? 0f;
            return cd > 0f ? cd : 5f;
        }

        // ── Internal config DTO + parser (no Newtonsoft / System.Text.Json dep needed) ──

        public class HeroSkillsConfigDef
        {
            public string? Description { get; set; }
            public List<HeroSkillSlotEntry>? Skills { get; set; }
        }

        public class HeroSkillSlotEntry
        {
            public int SlotIndex { get; set; }
            public string? SkillName { get; set; }
        }

        /// <summary>
        /// Minimal JSON parser for the hero_skills.json shape. Avoids pulling in a
        /// JSON dependency and matches the project's "minimal deps" convention.
        /// Public so tests can drive it without a fixture file.
        /// </summary>
        public static class HeroSkillsConfigLoader
        {
            public static HeroSkillsConfigDef? Parse(string json)
            {
                // Treat empty / whitespace as a valid empty def (not an error).
                if (string.IsNullOrWhiteSpace(json)) return new HeroSkillsConfigDef();
                var def = new HeroSkillsConfigDef();
                def.Skills = new List<HeroSkillSlotEntry>();

                // Find the "Skills": [ ... ] block and pull each { ... } object.
                int arrStart = json.IndexOf("\"Skills\"", StringComparison.OrdinalIgnoreCase);
                if (arrStart < 0) return def;
                int bracket = json.IndexOf('[', arrStart);
                if (bracket < 0) return def;
                int depth = 0;
                int arrEnd = -1;
                for (int i = bracket; i < json.Length; i++)
                {
                    if (json[i] == '[') depth++;
                    else if (json[i] == ']')
                    {
                        depth--;
                        if (depth == 0) { arrEnd = i; break; }
                    }
                }
                if (arrEnd < 0) return def;

                string body = json.Substring(bracket + 1, arrEnd - bracket - 1);
                int pos = 0;
                while (pos < body.Length)
                {
                    int objStart = body.IndexOf('{', pos);
                    if (objStart < 0) break;
                    int objDepth = 0;
                    int objEnd = -1;
                    for (int i = objStart; i < body.Length; i++)
                    {
                        if (body[i] == '{') objDepth++;
                        else if (body[i] == '}')
                        {
                            objDepth--;
                            if (objDepth == 0) { objEnd = i; break; }
                        }
                    }
                    if (objEnd < 0) break;
                    string obj = body.Substring(objStart, objEnd - objStart + 1);
                    var entry = ParseEntry(obj);
                    if (entry != null) def.Skills.Add(entry);
                    pos = objEnd + 1;
                }
                return def;
            }

            private static HeroSkillSlotEntry? ParseEntry(string obj)
            {
                int slot = ExtractInt(obj, "SlotIndex");
                string? name = ExtractString(obj, "SkillName");
                if (string.IsNullOrEmpty(name)) return null;
                return new HeroSkillSlotEntry { SlotIndex = slot, SkillName = name };
            }

            private static int ExtractInt(string obj, string key)
            {
                int k = obj.IndexOf("\"" + key + "\"", StringComparison.OrdinalIgnoreCase);
                if (k < 0) return 0;
                int colon = obj.IndexOf(':', k);
                if (colon < 0) return 0;
                int i = colon + 1;
                while (i < obj.Length && (obj[i] == ' ' || obj[i] == '\t')) i++;
                int start = i;
                bool neg = false;
                if (i < obj.Length && obj[i] == '-') { neg = true; i++; }
                while (i < obj.Length && (obj[i] >= '0' && obj[i] <= '9')) i++;
                if (i == start) return 0;
                if (!int.TryParse(obj.Substring(start, i - start), out int v)) return 0;
                return neg ? -v : v;
            }

            private static string? ExtractString(string obj, string key)
            {
                int k = obj.IndexOf("\"" + key + "\"", StringComparison.OrdinalIgnoreCase);
                if (k < 0) return null;
                int colon = obj.IndexOf(':', k);
                if (colon < 0) return null;
                int q1 = obj.IndexOf('"', colon + 1);
                if (q1 < 0) return null;
                int q2 = obj.IndexOf('"', q1 + 1);
                if (q2 < 0) return null;
                return obj.Substring(q1 + 1, q2 - q1 - 1);
            }
        }
    }
}
