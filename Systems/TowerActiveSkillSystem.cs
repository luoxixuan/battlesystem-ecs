#nullable enable
using System;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Round 138 — Per-Tower Active Skill System.
    ///
    /// Some tower types have a manual-cast active ability (e.g. press Q to trigger a
    /// powerful AOE attached to that specific tower). This system handles:
    ///   • per-frame cooldown tick (so the gate resolves in seconds, not frames)
    ///   • the public TriggerTowerActive(towerId) API the player/HUD calls on input
    ///   • a soft-coupling dispatch into the shared SkillDef table — any player skill
    ///     can be repurposed as a tower active (designer just references its id)
    ///
    /// Design notes:
    ///   • Inert by default: ActiveSkillId == -1 → no field writes, no per-tick work
    ///     beyond a single `if` check, so the cost is O(activeTowers) per frame.
    ///   • Effect dispatch is intentionally minimal — we look up the SkillDef and
    ///     broadcast a log + put the tower on cooldown. The full cast pipeline
    ///     (damage application, AOE query) is delegated to a follow-up injection
    ///     into SkillSystem.CastByTower when the wiring is finalized. Round 138
    ///     establishes the cooldown + public API surface only.
    ///   • No HUD binding: that's the renderer's job. This system just answers
    ///     "is this tower's active ready?" and "fire it".
    /// </summary>
    public class TowerActiveSkillSystem
    {
        private ComponentStore store;
        private GameConfig? _config;

        public TowerActiveSkillSystem(ComponentStore store, GameConfig? config = null)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            _config = config;
        }

        /// <summary>
        /// Optional late binding of the config — the same GameConfig used by
        /// SkillSystem. If null, TriggerTowerActive still works (cooldown gate)
        /// but skill-id → definition lookup falls back to logging only.
        /// </summary>
        public void SetConfig(GameConfig config) => _config = config;

        /// <summary>
        /// Per-frame cooldown tick. Walks ActiveTowerIds, decrements TowerActiveCooldown
        /// for any tower with an active skill configured. Skips inactive/disabled
        /// towers so we don't waste cycles on the swap-and-pop trailing slot.
        /// </summary>
        public void Update(float deltaTime)
        {
            if (deltaTime <= 0f) return;
            var towerIds = store.ActiveTowerIds;
            for (int i = 0; i < towerIds.Count; i++)
            {
                int towerId = towerIds[i];
                if (!store.TowerActive[towerId]) continue;
                // Fast skip: no active skill → cooldown is 0, no work to do
                if (store.TowerActiveSkillId[towerId] < 0) continue;
                store.TickTowerActiveCooldown(towerId, deltaTime);
            }
        }

        /// <summary>
        /// Public API: player/HUD calls this when the player presses the hotkey
        /// bound to a tower's active skill. Returns true if the cast succeeded
        /// (i.e. the tower was ready and the cooldown gate was passed).
        ///
        /// Note: the actual effect dispatch (AOE damage, debuff, heal) is left as
        /// a follow-up hook — Round 138 establishes the gate + cooldown contract
        /// and emits a log line so the design intent is observable. The skill-id
        /// resolution into a real cast path is the next round's job (the relevant
        /// SkillSystem.CastXxx methods are player-targeted, so they need a
        /// "cast by tower" variant — that's a bigger refactor than fits in one round).
        /// </summary>
        public bool TriggerTowerActive(int towerId)
        {
            if (!ComponentStore.IsValidEntity(towerId)) return false;
            if (!store.TowerActive[towerId]) return false;
            int skillId = store.TowerActiveSkillId[towerId];
            if (skillId < 0) return false;
            if (store.TowerActiveCooldown[towerId] > 0f) return false;
            // Gate passed — flip the cooldown to its max and emit the log.
            store.SetTowerActiveOnCooldown(towerId);
            string towerName = store.TowerType[towerId].ToString();
            string skillName = ResolveSkillName(skillId);
            Console.WriteLine($"[TOWER_ACTIVE] tower={towerId} ({towerName}) cast skillId={skillId} ({skillName}) cd={store.TowerActiveCooldownMax[towerId]:F1}s");
            return true;
        }

        /// <summary>
        /// 技能 id → 显示名。TowerConfig.ActiveSkillId 语义（见其注释）是索引共享
        /// SkillDefs 表；越界时回退玩家技能栏（Skills）。两表皆未命中返回 "?"。
        /// </summary>
        private string ResolveSkillName(int skillId)
        {
            var cfg = _config;
            if (cfg == null) return "?";
            var defs = cfg.SkillDefs;
            if (defs != null && skillId >= 0 && skillId < defs.Count) return defs[skillId]?.Name ?? "?";
            var skills = cfg.Skills;
            if (skills != null && skillId >= 0 && skillId < skills.Count) return skills[skillId]?.Name ?? "?";
            return "?";
        }

        /// <summary>
        /// Read-only helper for HUD/renderer: returns true if the tower can fire
        /// its active skill right now. Mirrors the gate in TriggerTowerActive.
        /// </summary>
        public bool IsTowerActiveReady(int towerId) => store.IsTowerActiveReady(towerId);

        /// <summary>
        /// Read-only helper for HUD/renderer: returns the configured active skill id
        /// or -1 if the tower has none. Lets the renderer show "(no active)" or
        /// "Press Q: [skill name]" depending on what designers wired up.
        /// </summary>
        public int GetTowerActiveSkillId(int towerId) => store.GetTowerActiveSkillId(towerId);

        /// <summary>
        /// Read-only helper for HUD/renderer: returns the remaining cooldown in
        /// seconds. 0 means ready.
        /// </summary>
        public float GetTowerActiveCooldown(int towerId) => store.GetTowerActiveCooldown(towerId);
    }
}
