#nullable enable
using System;
using System.Collections.Generic;
namespace BattleSystemECS.Core
{
    /// <summary>BuildPhase systems: economy, upgrades, auto-skills.</summary>
    internal sealed class BuildGroup : IBuildPhaseGroup
    {
        public object? Gold { get; set; }
        public object? TowerIncome { get; set; }
        public object? Upgrade { get; set; }
        public object? Skill { get; set; }
        public object? AutoSkill { get; set; }
        public object? TowerRelocate { get; set; }
        public object? Interest { get; set; }
        public object? Mana { get; set; }
        // Round175 Direction1 — Mana Shield: also runs in BuildPhase so the
        // shield can fill up while the player is preparing between waves.
        // Per-player system (one instance per slot).
        public object? ManaShield { get; set; }
        // Round178 Direction6 — Pre-fight Buff: BuildPhase末「3-选-1」出战 buff.
        // The system rolls weighted-random options into per-player slots on
        // the WaveRunning→WavePending transition and caches the chosen
        // buff's tower-side multipliers on OnWaveStart.
        public object? PreFightBuff { get; set; }
        public object? Objective { get; set; }
        public object? ResourceNode { get; set; }
        public object? GlobalSkill { get; set; }
        public object? Desperation { get; set; }
        public object? ShopReroll { get; set; }

        private readonly List<Binding> _bindings = new List<Binding>();
        internal void ClearBindings() => _bindings.Clear();
        internal bool RemoveBinding(string id)
        {
            for (int i = 0; i < _bindings.Count; i++)
                if (string.Equals(_bindings[i].Id, id, StringComparison.Ordinal)) { _bindings.RemoveAt(i); return true; }
            return false;
        }
        internal void Register(string id, Func<object?> slot, Action<ComponentStore, float> execute)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Build binding id is required.", nameof(id));
            _bindings.Add(new Binding(id, slot ?? throw new ArgumentNullException(nameof(slot)), execute ?? throw new ArgumentNullException(nameof(execute))));
        }
        internal bool TryGetBinding(string id, out Action<ComponentStore, float>? execute)
        {
            for (int i = 0; i < _bindings.Count; i++) if (string.Equals(_bindings[i].Id, id, StringComparison.Ordinal))
            { if (_bindings[i].Slot() == null) { execute = null; return false; } execute = _bindings[i].Execute; return true; }
            execute = null; return false;
        }
        internal bool HasSlot(string id) => id switch
        {
            "build.gold.update" => Gold != null, "build.tower-income.update" => TowerIncome != null,
            "build.upgrade.update" => Upgrade != null, "build.skill.update" or "build.skill.reject-pending" => Skill != null,
            "build.auto-skill.update" => AutoSkill != null, "build.tower-relocate.update" => TowerRelocate != null,
            "build.interest.update" => Interest != null, "build.mana.update" => Mana != null,
            "build.mana-shield.update" => ManaShield != null, "build.pre-fight-buff.update" => PreFightBuff != null,
            "build.resource-node.update" => ResourceNode != null, "build.objective.update" => Objective != null,
            "build.global-skill.update" => GlobalSkill != null, "build.desperation.update" => Desperation != null,
            "build.shop-reroll.update" => ShopReroll != null, _ => false
        };

        public void Execute(ComponentStore store, float deltaTime)
        {
            for (int i = 0; i < _bindings.Count; i++) if (_bindings[i].Slot() != null) _bindings[i].Execute(store, deltaTime);
        }

        private sealed class Binding
        {
            public readonly string Id; public readonly Func<object?> Slot; public readonly Action<ComponentStore, float> Execute;
            public Binding(string id, Func<object?> slot, Action<ComponentStore, float> execute) { Id = id; Slot = slot; Execute = execute; }
        }
    }
}
