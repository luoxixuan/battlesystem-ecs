#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Core.GAS;

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
        private readonly List<int> _catalogTargets = new List<int>(16);
        private readonly List<float> _catalogMagnitudeScales = new List<float>(16);
        private PhaseContext _phaseContext = PhaseContext.Unbound;
        private IAbilityPayloadHandler? _payloadHandler;
        public AbilityActivationResult LastActivation { get; private set; }
        private int _pendingHeroId = -1;
        private int _pendingSlot = -1;
        public PhaseContextKind CurrentPhaseContext => _phaseContext.Kind;

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
                var def = HeroSkillsConfigLoader.Parse(json, _heroSkillsPath);
                if (def?.Skills == null) return;

                foreach (var entry in def.Skills)
                {
                    int resolvedId = ResolveSkillIdByName(entry.SkillName!);
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
            if (!_phaseContext.AllowsCombat) return;
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
                    GameplayAbilityRuntime.TickCooldown(_heroSkillCooldowns, flatIdx, deltaTime);
                }
            }
            if (_pendingHeroId >= 0)
            {
                int heroId = _pendingHeroId;
                int slot = _pendingSlot;
                _pendingHeroId = -1;
                _pendingSlot = -1;
                TriggerHeroSkill(heroId, slot);
            }
        }

        /// <summary>Queues one input activation for the production frame node.</summary>
        public bool RequestHeroSkill(int heroId, int slot)
        {
            if (_pendingHeroId >= 0 || heroId < 0 || heroId >= ComponentStore.MAX_HEROES ||
                slot < 0 || slot >= MAX_HERO_SKILLS) return false;
            _pendingHeroId = heroId;
            _pendingSlot = slot;
            return true;
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
            if (!_phaseContext.AllowsCombat) return false;
            if (heroId < 0 || heroId >= ComponentStore.MAX_HEROES) return false;
            if (slot < 0 || slot >= MAX_HERO_SKILLS) return false;
            if (!store.HeroIsDeployed[heroId]) return false;

            int flatIdx = heroId * MAX_HERO_SKILLS + slot;
            int skillId = _heroSkillIds[flatIdx];
            if (skillId < 0) return false;
            var catalog = _config?.CompiledCatalog;
            if (catalog == null)
            {
                if (!GameplayAbilityRuntime.TryActivate(_heroSkillCooldowns,
                        new AbilityActivationRequest(store.PlayerEntityId, flatIdx, _heroSkillCooldownMax[flatIdx],
                            ownerPlayerId: store.PlayerEntityId)).Accepted) return false;
                _heroSkillCooldowns[flatIdx] = _heroSkillCooldownMax[flatIdx];
                return true;
            }
            if (!TryResolveCatalogAbility(catalog, ResolveSkillNameById(skillId), out var abilityId) ||
                !catalog.TryGetAbility(abilityId, out var ability)) return false;

            bool selfTarget = ability.Targeting.Relation == RelationFilter.Self;
            var activation = new AbilityActivationRequest(store.PlayerEntityId, flatIdx,
                _heroSkillCooldownMax[flatIdx], store.PlayerEntityId, abilityId,
                ownerPlayerId: store.PlayerEntityId);
            AbilityActivationResult result;
            if (selfTarget)
                result = GameplayAbilityRuntime.Activate(store, catalog, _heroSkillCooldowns, activation, _payloadHandler);
            else
            {
                bool collected = ability.Targeting.Relation == RelationFilter.Allies
                    ? TargetingRuntime.TryCollectAllyTargets(store, store.PlayerEntityId, ability.Targeting,
                        _catalogTargets, _catalogMagnitudeScales)
                    : TargetingRuntime.TryCollectEnemyTargets(store, store.PlayerEntityId, ability.Targeting,
                        _catalogTargets, _catalogMagnitudeScales);
                if (!collected || _catalogTargets.Count == 0) return false;
                result = GameplayAbilityRuntime.ActivateTargets(store, catalog, _heroSkillCooldowns, activation,
                    _catalogTargets, _catalogMagnitudeScales, _payloadHandler);
            }
            LastActivation = result;
            if (!result.Accepted) return false;
            Console.WriteLine($"[HERO_SKILL] hero={heroId} slot={slot} ability={abilityId.Value} effects={result.AppliedEffects}");
            return true;
        }

        internal void SetPhaseContext(PhaseContext context)
        {
            _phaseContext = context;
            store.GameplayPhaseContext = context;
            if (!context.AllowsCombat) { _pendingHeroId = -1; _pendingSlot = -1; }
        }

        internal void SetPayloadHandler(IAbilityPayloadHandler payloadHandler) =>
            _payloadHandler = payloadHandler ?? throw new ArgumentNullException(nameof(payloadHandler));

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

        private static bool TryResolveCatalogAbility(GameplayCatalog catalog, string skillName, out AbilityId ability)
        {
            ability = default(AbilityId);
            if (string.IsNullOrWhiteSpace(skillName)) return false;
            return catalog.Aliases != null && catalog.Aliases.TryGetValue(skillName, out ability) && catalog.TryGetAbility(ability, out _);
        }

        private float ResolveCooldownForSkill(int skillId)
        {
            // Cooldown is in seconds per the SkillConfig contract. Default to 5s if not set
            // so the gate is observable even if a designer forgot to set it.
            float cd = ResolveSkillConfigById(skillId)?.Cooldown ?? 0f;
            return cd > 0f ? cd : 5f;
        }

        // ── Internal config DTO + parser ──

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
        /// Strict parser for the hero_skills.json shape. Public so tests can drive
        /// it without a fixture file.
        /// </summary>
        public static class HeroSkillsConfigLoader
        {
            public static HeroSkillsConfigDef Parse(string json, string sourcePath = "hero_skills.json")
            {
                if (string.IsNullOrWhiteSpace(json)) return new HeroSkillsConfigDef();
                using var document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw Invalid(sourcePath, "$", "expected an object");

                var def = new HeroSkillsConfigDef { Skills = new List<HeroSkillSlotEntry>() };
                if (TryGetProperty(root, "Description", out var description) &&
                    description.ValueKind == JsonValueKind.String)
                    def.Description = description.GetString();
                if (!TryGetProperty(root, "Skills", out var skills)) return def;
                if (skills.ValueKind != JsonValueKind.Array)
                    throw Invalid(sourcePath, "$.Skills", "expected an array");

                var declaredSlots = new HashSet<int>();
                int index = 0;
                foreach (JsonElement node in skills.EnumerateArray())
                {
                    string entryPath = "$.Skills[" + index + "]";
                    if (node.ValueKind != JsonValueKind.Object)
                        throw Invalid(sourcePath, entryPath, "expected an object");
                    if (!TryGetProperty(node, "SlotIndex", out var slotNode))
                        throw Invalid(sourcePath, entryPath + ".SlotIndex", "property is required");
                    if (slotNode.ValueKind != JsonValueKind.Number || !slotNode.TryGetInt32(out int slot))
                        throw Invalid(sourcePath, entryPath + ".SlotIndex", "expected an integer");
                    if (slot < 0 || slot >= MAX_HERO_SKILLS)
                        throw Invalid(sourcePath, entryPath + ".SlotIndex",
                            "must be in range 0.." + (MAX_HERO_SKILLS - 1));
                    if (!declaredSlots.Add(slot))
                        throw Invalid(sourcePath, entryPath + ".SlotIndex", "duplicate slot " + slot);
                    if (!TryGetProperty(node, "SkillName", out var nameNode) ||
                        nameNode.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nameNode.GetString()))
                        throw Invalid(sourcePath, entryPath + ".SkillName", "non-empty string is required");

                    def.Skills.Add(new HeroSkillSlotEntry { SlotIndex = slot, SkillName = nameNode.GetString() });
                    index++;
                }
                return def;
            }

            private static bool TryGetProperty(JsonElement node, string name, out JsonElement value)
            {
                foreach (JsonProperty property in node.EnumerateObject())
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                value = default(JsonElement);
                return false;
            }

            private static CatalogValidationException Invalid(string sourcePath, string jsonPath, string reason) =>
                new CatalogValidationException($"{sourcePath}: {jsonPath}: {reason}");
        }
    }
}
