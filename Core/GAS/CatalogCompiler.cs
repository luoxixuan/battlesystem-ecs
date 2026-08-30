using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Collections.ObjectModel;

namespace BattleSystemECS.Core.GAS
{
    public readonly struct AbilityCatalogEntry
    {
        public readonly AbilityId Id;
        public readonly string Name;
        public readonly int AreaShape;
        public readonly float Duration;
        internal AbilityCatalogEntry(AbilityId id, string name, int areaShape, float duration) { Id = id; Name = name; AreaShape = areaShape; Duration = duration; }
    }

    public sealed class GameplayCatalog
    {
        private readonly AbilityCatalogEntry[] _abilities;
        private readonly AbilityDefinition[] _abilityDefinitions;
        private readonly TargetingDefinition[] _targetings;
        private readonly ModifierDefinition[] _modifiers;
        private readonly TriggerDefinition[] _triggers;
        private readonly GameplayEffectDefinition[] _effects;
        private readonly ExecutionDefinition[] _executions;
        private readonly IReadOnlyDictionary<string, AbilityId> _aliases;
        private readonly IReadOnlyList<AbilityCatalogEntry> _abilitiesView;
        private readonly IReadOnlyList<AbilityDefinition> _abilityDefinitionsView;
        private readonly IReadOnlyList<TargetingDefinition> _targetingsView;
        private readonly IReadOnlyList<ModifierDefinition> _modifiersView;
        private readonly IReadOnlyList<TriggerDefinition> _triggersView;
        private readonly IReadOnlyList<GameplayEffectDefinition> _effectsView;
        private readonly IReadOnlyList<ExecutionDefinition> _executionsView;
        public IReadOnlyList<AbilityCatalogEntry> Abilities => _abilitiesView;
        public IReadOnlyList<AbilityDefinition> AbilityDefinitions => _abilityDefinitionsView;
        public IReadOnlyList<TargetingDefinition> Targetings => _targetingsView;
        public IReadOnlyList<ModifierDefinition> Modifiers => _modifiersView;
        public IReadOnlyList<TriggerDefinition> Triggers => _triggersView;
        public IReadOnlyList<GameplayEffectDefinition> Effects => _effectsView;
        public IReadOnlyList<ExecutionDefinition> Executions => _executionsView;
        public IReadOnlyDictionary<string, AbilityId> Aliases => _aliases;
        internal GameplayCatalog(IReadOnlyList<AbilityDefinition> abilities, IReadOnlyList<TargetingDefinition> targetings, IReadOnlyList<GameplayEffectDefinition> effects, IReadOnlyList<ExecutionDefinition> executions, IReadOnlyList<TriggerDefinition> triggers, IReadOnlyList<ModifierDefinition> modifiers, IReadOnlyDictionary<string, AbilityId> aliases)
        {
            _abilityDefinitions = Copy(abilities); _targetings = Copy(targetings); _effects = Copy(effects); _executions = Copy(executions); _triggers = Copy(triggers); _modifiers = Copy(modifiers); _aliases = new ReadOnlyDictionary<string, AbilityId>(new Dictionary<string, AbilityId>(aliases));
            _abilities = new AbilityCatalogEntry[_abilityDefinitions.Length];
            for (int i = 0; i < _abilities.Length; i++) _abilities[i] = new AbilityCatalogEntry(_abilityDefinitions[i].Id, _abilityDefinitions[i].Name, LegacyAreaShape(_abilityDefinitions[i].Targeting.Shape), DurationFor(_abilityDefinitions[i], _effects, _executions));
            _abilitiesView = Array.AsReadOnly(_abilities); _abilityDefinitionsView = Array.AsReadOnly(_abilityDefinitions); _targetingsView = Array.AsReadOnly(_targetings); _modifiersView = Array.AsReadOnly(_modifiers); _triggersView = Array.AsReadOnly(_triggers); _effectsView = Array.AsReadOnly(_effects); _executionsView = Array.AsReadOnly(_executions);
        }
        private static T[] Copy<T>(IReadOnlyList<T> values) { var copy = new T[values == null ? 0 : values.Count]; if (values != null) for (int i = 0; i < copy.Length; i++) copy[i] = values[i]; return copy; }
        private static float DurationFor(AbilityDefinition ability, GameplayEffectDefinition[] effects, ExecutionDefinition[] executions) { float duration = 0f; foreach (var effect in ability.Effects) if ((uint)effect.Value < (uint)effects.Length && effects[effect.Value].Duration > duration) duration = effects[effect.Value].Duration; foreach (var execution in ability.Executions) if ((uint)execution.Value < (uint)executions.Length && executions[execution.Value].Duration > duration) duration = executions[execution.Value].Duration; return duration; }
        private static int LegacyAreaShape(TargetingShape shape) { switch (shape) { case TargetingShape.TimeRewind: return AreaShapeType.TimeRewind; case TargetingShape.ChainHeal: return AreaShapeType.ChainHeal; case TargetingShape.MassResurrect: return AreaShapeType.MassResurrect; case TargetingShape.AoeStun: return AreaShapeType.AoeStun; case TargetingShape.AoeRoot: return AreaShapeType.AoeRoot; case TargetingShape.AoeKnockback: return AreaShapeType.AoeKnockback; default: return AreaShapeType.FromString(shape.ToString()); } }
        public bool TryGetAbility(AbilityId id, out AbilityDefinition definition) { if ((uint)id.Value < (uint)_abilityDefinitions.Length && _abilityDefinitions[id.Value].Id.Value == id.Value) { definition = _abilityDefinitions[id.Value]; return true; } definition = default(AbilityDefinition); return false; }
        public bool TryGetEffect(EffectId id, out GameplayEffectDefinition definition) { if ((uint)id.Value < (uint)_effects.Length && _effects[id.Value].Id.Value == id.Value) { definition = _effects[id.Value]; return true; } definition = default(GameplayEffectDefinition); return false; }
        public bool TryGetExecution(ExecutionId id, out ExecutionDefinition definition)
        {
            if ((uint)id.Value < (uint)_executions.Length && _executions[id.Value].Id.Value == id.Value) { definition = _executions[id.Value]; return true; }
            definition = default(ExecutionDefinition); return false;
        }
        public bool TryGetTrigger(TriggerId id, out TriggerDefinition definition)
        {
            if ((uint)id.Value < (uint)_triggers.Length && _triggers[id.Value].Id.Value == id.Value) { definition = _triggers[id.Value]; return true; }
            definition = default(TriggerDefinition); return false;
        }
        public bool TryResolveAlias(string alias, out AbilityId id) => _aliases.TryGetValue(alias, out id);
    }

    /// <summary>Strict bootstrap for canonical skills.json. Legacy game_config skills remain an explicit caller choice.</summary>
    public static class CatalogCompiler
    {
        public static GameplayCatalog Compile(string canonicalSkillsPath, IEnumerable<string> staticSkillPaths = null)
        {
            if (string.IsNullOrEmpty(canonicalSkillsPath) || !File.Exists(canonicalSkillsPath))
                throw new CatalogValidationException($"{canonicalSkillsPath}: canonical skills catalog not found");
            var abilities = new List<AbilityCatalogEntry>();
            var typedAbilities = new List<AbilityDefinition>();
            var targetings = new List<TargetingDefinition>();
            var modifiers = new List<ModifierDefinition>();
            var triggers = new List<TriggerDefinition>();
            var effects = new List<GameplayEffectDefinition>();
            var executions = new List<ExecutionDefinition>();
            var aliases = new Dictionary<string, AbilityId>(StringComparer.OrdinalIgnoreCase);
            var staticNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var doc = JsonDocument.Parse(File.ReadAllText(canonicalSkillsPath)))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    throw new CatalogValidationException($"{canonicalSkillsPath}: expected array");
                int id = 0;
                foreach (var node in doc.RootElement.EnumerateArray())
                {
                    string name = RequiredString(node, "Name", canonicalSkillsPath, id);
                    float duration = Number(node, "DotDuration", 0f, canonicalSkillsPath, id);
                    float period = Number(node, "DotTickInterval", 0f, canonicalSkillsPath, id);
                    if (duration < 0 || period < 0 || (duration > 0 && period <= 0))
                        throw new CatalogValidationException($"{canonicalSkillsPath}: invalid duration/period for id {id}");
                    TargetingDefinition targeting = ParseTargeting(node, canonicalSkillsPath, id);
                    targetings.Add(targeting);
                    var effectIds = new List<EffectId>();
                    var abilityModifiers = new List<ModifierDefinition>();
                    var abilityExecutions = new List<ExecutionId>();
                    float damageMultiplier = Number(node, "DamageMultiplier", 0f, canonicalSkillsPath, id);
                    if (damageMultiplier > 0f)
                    {
                        var multiplierId = new ExecutionId(executions.Count);
                        executions.Add(new ExecutionDefinition(multiplierId, EffectPayloadKind.Damage, damageMultiplier, CatalogRegistries.SkillTag, MagnitudeSource.Multiplier, DamageAmountStage.LegacyMultiplier, 0f, ExecutionOperation.ApplyDamage));
                        abilityExecutions.Add(multiplierId);
                    }
                    if (node.TryGetProperty("Modifiers", out var modArray))
                    {
                        if (modArray.ValueKind != JsonValueKind.Array) throw new CatalogValidationException($"{canonicalSkillsPath}: Modifiers must be an array for id {id}");
                        foreach (var mod in modArray.EnumerateArray())
                        {
                            string modName = RequiredString(mod, "Name", canonicalSkillsPath, id);
                            string modType = RequiredString(mod, "Type", canonicalSkillsPath, id);
                            float magnitude = Number(mod, "Value", 0f, canonicalSkillsPath, id);
                            TagId tag = ParseTag(RequiredString(mod, "EffectTag", canonicalSkillsPath, id), canonicalSkillsPath, id);
                            if (string.Equals(modType, "Damage", StringComparison.OrdinalIgnoreCase))
                            {
                                // Damage is an execution payload, not an attribute modifier.
                                var executionId = new ExecutionId(executions.Count);
                                executions.Add(new ExecutionDefinition(executionId, EffectPayloadKind.Damage, magnitude, tag, MagnitudeSource.Constant, DamageAmountStage.Raw, 0f, ExecutionOperation.ApplyDamage));
                                abilityExecutions.Add(executionId);
                                continue;
                            }
                            if (!string.Equals(modType, "Debuff", StringComparison.OrdinalIgnoreCase) && !string.Equals(modType, "CrowdControl", StringComparison.OrdinalIgnoreCase))
                                throw new CatalogValidationException($"{canonicalSkillsPath}: unknown modifier type '{modType}' for ability {id} ({name})");
                            int effectIndex = effects.Count;
                            StackingBehavior stacking = ParseStacking(RequiredString(mod, "StackingType", canonicalSkillsPath, id), canonicalSkillsPath, id);
                            int maxStacks = Int(mod, "StackLimitCount", canonicalSkillsPath, id); if (maxStacks < 1) maxStacks = 1;
                            var effectExecutionId = new ExecutionId(executions.Count);
                            executions.Add(new ExecutionDefinition(effectExecutionId, modType == "CrowdControl" ? EffectPayloadKind.CrowdControl : EffectPayloadKind.Damage, magnitude, tag, MagnitudeSource.Constant, DamageAmountStage.Raw, Number(mod, "Duration", duration, canonicalSkillsPath, id), modType == "CrowdControl" ? ExecutionOperation.ApplyCrowdControl : ExecutionOperation.ApplyDamage));
                            effects.Add(new GameplayEffectDefinition(new EffectId(effectIndex), period > 0 ? EffectType.Periodic : EffectType.Duration, Array.Empty<ModifierDefinition>(), Number(mod, "Duration", duration, canonicalSkillsPath, id), period, ClockId.Combat, stacking, maxStacks, stacking == StackingBehavior.None ? RefreshPolicy.None : RefreshPolicy.Duration, SourceDeathPolicy.Persist, modType == "CrowdControl" ? EffectPayloadKind.CrowdControl : EffectPayloadKind.Damage, tag, new[] { executions[executions.Count - 1].Id }));
                            effectIds.Add(new EffectId(effectIndex));
                            AddAlias(aliases, modName, new AbilityId(id), canonicalSkillsPath);
                        }
                    }
                    if (targeting.Shape == TargetingShape.Slow)
                    {
                        var slowId = new ExecutionId(executions.Count);
                        executions.Add(new ExecutionDefinition(slowId, EffectPayloadKind.Slow, Number(node, "SlowAmount", 0f, canonicalSkillsPath, id), CatalogRegistries.SkillTag, MagnitudeSource.Constant, DamageAmountStage.Raw, Number(node, "SlowDuration", 0f, canonicalSkillsPath, id), ExecutionOperation.ApplySlow));
                        abilityExecutions.Add(slowId);
                    }
                    else if (targeting.Shape == TargetingShape.Heal || targeting.Shape == TargetingShape.Shield || targeting.Shape == TargetingShape.ChainHeal || targeting.Shape == TargetingShape.MassResurrect || targeting.Shape == TargetingShape.AoeStun || targeting.Shape == TargetingShape.AoeRoot || targeting.Shape == TargetingShape.AoeKnockback || targeting.Shape == TargetingShape.TimeRewind)
                    {
                        float specialMagnitude = Number(node, "HealPercent", 0f, canonicalSkillsPath, id);
                        float specialDuration = 0f;
                        EffectPayloadKind payload = EffectPayloadKind.Heal;
                        ExecutionOperation operation = ExecutionOperation.ApplyHeal;
                        if (targeting.Shape == TargetingShape.Shield) { specialMagnitude = Number(node, "ShieldAmount", 0f, canonicalSkillsPath, id); specialDuration = Number(node, "ShieldDuration", 0f, canonicalSkillsPath, id); payload = EffectPayloadKind.Shield; operation = ExecutionOperation.ApplyShield; }
                        else if (targeting.Shape == TargetingShape.AoeStun) { specialMagnitude = Number(node, "AoeStunDuration", 0f, canonicalSkillsPath, id); payload = EffectPayloadKind.CrowdControl; operation = ExecutionOperation.ApplyCrowdControl; }
                        else if (targeting.Shape == TargetingShape.AoeRoot) { specialMagnitude = Number(node, "AoeRootDuration", 0f, canonicalSkillsPath, id); payload = EffectPayloadKind.CrowdControl; operation = ExecutionOperation.ApplyCrowdControl; }
                        else if (targeting.Shape == TargetingShape.AoeKnockback) { specialMagnitude = Number(node, "AoeKnockbackForce", 0f, canonicalSkillsPath, id); payload = EffectPayloadKind.CrowdControl; operation = ExecutionOperation.ApplyCrowdControl; }
                        else if (targeting.Shape == TargetingShape.MassResurrect) { payload = EffectPayloadKind.Resurrect; operation = ExecutionOperation.Resurrect; }
                        else if (targeting.Shape == TargetingShape.TimeRewind) { payload = EffectPayloadKind.Resource; operation = ExecutionOperation.RestoreSnapshot; }
                        if (targeting.Shape != TargetingShape.ChainHeal || specialMagnitude > 0f)
                        {
                            var specialId = new ExecutionId(executions.Count);
                            executions.Add(new ExecutionDefinition(specialId, payload, specialMagnitude, CatalogRegistries.SkillTag, MagnitudeSource.Constant, DamageAmountStage.Raw, specialDuration, operation));
                            abilityExecutions.Add(specialId);
                        }
                        if (targeting.Shape == TargetingShape.ChainHeal)
                        {
                            float shield = Number(node, "ShieldAmount", 0f, canonicalSkillsPath, id);
                            if (shield > 0f)
                            {
                                var shieldId = new ExecutionId(executions.Count);
                                executions.Add(new ExecutionDefinition(shieldId, EffectPayloadKind.Shield, shield, CatalogRegistries.SkillTag, MagnitudeSource.Constant, DamageAmountStage.Raw, Number(node, "ShieldDuration", 0f, canonicalSkillsPath, id), ExecutionOperation.ApplyShield));
                                abilityExecutions.Add(shieldId);
                            }
                        }
                    }
                    var entry = new AbilityCatalogEntry(new AbilityId(id), name, (int)targeting.Shape, duration);
                    int manaCost = Int(node, "ManaCost", canonicalSkillsPath, id);
                    typedAbilities.Add(new AbilityDefinition(new AbilityId(id), name, targeting, ClockId.Combat, Number(node, "Cooldown", 0f, canonicalSkillsPath, id), GameplayPhaseMask.Wave, effectIds.ToArray(), abilityModifiers.ToArray(), CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer, ActivationPolicy.Instant, manaCost, abilityExecutions.ToArray(), manaCost > 0 ? new[] { new CostDefinition(CatalogRegistries.Mana, manaCost) } : Array.Empty<CostDefinition>()));
                    abilities.Add(entry);
                    AddAlias(aliases, name, entry.Id, canonicalSkillsPath);
                    id++;
                }
            }
            if (staticSkillPaths != null)
            {
                var ordered = new List<string>(staticSkillPaths);
                ordered.Sort(StringComparer.Ordinal);
                foreach (string skillPath in ordered)
                {
                    if (!File.Exists(skillPath))
                        throw new CatalogValidationException($"{skillPath}: static skill file not found");
                    using (var doc = JsonDocument.Parse(File.ReadAllText(skillPath)))
                    {
                        JsonElement node = doc.RootElement;
                        if (node.ValueKind != JsonValueKind.Object)
                            throw new CatalogValidationException($"{skillPath}: expected object");
                        var staticRecord = StaticSkillSchemaAdapter.Read(node, skillPath, abilities.Count);
                        string name = staticRecord.Name;
                        if (staticNames.Contains(name)) throw new CatalogValidationException($"{skillPath}: static skill alias conflict '{name}'");
                        if (aliases.ContainsKey(name)) continue; // curated entries have precedence
                        staticNames.Add(name);
                        var entry = new AbilityCatalogEntry(new AbilityId(abilities.Count), name, AreaShapeType.Single, 0f);
                        var staticExecution = new ExecutionId(executions.Count);
                        executions.Add(new ExecutionDefinition(staticExecution, EffectPayloadKind.Damage, staticRecord.DamageMultiplier, CatalogRegistries.SkillTag, MagnitudeSource.Multiplier, DamageAmountStage.LegacyMultiplier));
                        typedAbilities.Add(new AbilityDefinition(entry.Id, name, new TargetingDefinition(new TargetingId(entry.Id.Value), TargetingShape.Single, staticRecord.Range, staticRecord.Width, staticRecord.Height, 1), ClockId.Combat, staticRecord.Cooldown, GameplayPhaseMask.Wave, Array.Empty<EffectId>(), Array.Empty<ModifierDefinition>(), CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer, ActivationPolicy.Instant, staticRecord.ManaCost, new[] { staticExecution }, staticRecord.ManaCost > 0 ? new[] { new CostDefinition(CatalogRegistries.Mana, staticRecord.ManaCost) } : Array.Empty<CostDefinition>()));
                        targetings.Add(typedAbilities[typedAbilities.Count - 1].Targeting);
                        abilities.Add(entry);
                        AddAlias(aliases, name, entry.Id, skillPath);
                    }
                }
            }
            var catalog = new GameplayCatalog(typedAbilities, targetings, effects, executions, triggers, modifiers, aliases);
            CatalogValidator.Validate(catalog, canonicalSkillsPath);
            return catalog;
        }

        private static string RequiredString(JsonElement node, string property, string path, int id)
        {
            if (!node.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw new CatalogValidationException($"{path}: missing {property} for id {id}");
            return value.GetString();
        }
        private static float Number(JsonElement node, string property, float fallback, string path, int id)
        {
            if (!node.TryGetProperty(property, out var value)) return fallback;
            if (!value.TryGetSingle(out var number) || float.IsNaN(number) || float.IsInfinity(number))
                throw new CatalogValidationException($"{path}: invalid {property} for id {id}");
            return number;
        }
        private static void AddAlias(Dictionary<string, AbilityId> aliases, string alias, AbilityId id, string path)
        {
            if (aliases.ContainsKey(alias)) throw new CatalogValidationException($"{path}: alias conflict '{alias}'");
            aliases.Add(alias, id);
        }

        private static TargetingDefinition ParseTargeting(JsonElement node, string path, int id)
        {
            if (!node.TryGetProperty("AreaShape", out var shapeNode) || shapeNode.ValueKind != JsonValueKind.String)
                throw new CatalogValidationException($"{path}: missing AreaShape for id {id}");
            string value = shapeNode.GetString().ToLowerInvariant();
            TargetingShape parsed;
            switch (value)
            {
                case "single": parsed = TargetingShape.Single; break; case "cross": parsed = TargetingShape.Cross; break; case "box": parsed = TargetingShape.Box; break; case "circle": parsed = TargetingShape.Circle; break; case "chain": parsed = TargetingShape.Chain; break; case "heal": parsed = TargetingShape.Heal; break; case "shield": parsed = TargetingShape.Shield; break; case "line": parsed = TargetingShape.Line; break; case "freeze": parsed = TargetingShape.Freeze; break; case "cone": parsed = TargetingShape.Cone; break; case "groundtarget": parsed = TargetingShape.GroundTarget; break; case "slow": parsed = TargetingShape.Slow; break; case "timerwind": parsed = TargetingShape.TimeRewind; break; case "chainheal": parsed = TargetingShape.ChainHeal; break; case "massresurrect": parsed = TargetingShape.MassResurrect; break; case "aoestun": parsed = TargetingShape.AoeStun; break; case "aoeroot": parsed = TargetingShape.AoeRoot; break; case "aoeknockback": parsed = TargetingShape.AoeKnockback; break; default: throw new CatalogValidationException($"{path}: unknown target shape '{value}' for id {id}");
            }
            return new TargetingDefinition(new TargetingId(id), parsed, Int(node, "AttackRange", path, id), Int(node, "AreaWidth", path, id), Int(node, "AreaHeight", path, id), 1, Number(node, "AreaRadius", 0f, path, id), Number(node, "ConeAngleDegrees", 0f, path, id));
        }
        private static int Int(JsonElement node, string property, string path, int id) { if (!node.TryGetProperty(property, out var value)) return 0; if (!value.TryGetInt32(out var number) || number < 0) throw new CatalogValidationException($"{path}: invalid {property} for id {id}"); return number; }
        private static TagId ParseTag(string value, string path, int id) { if (!CatalogRegistries.TryTag(value, out var tag)) throw new CatalogValidationException($"{path}: unknown effect tag '{value}' for id {id}"); return tag; }
        private static StackingBehavior ParseStacking(string value, string path, int id) { switch (value.ToLowerInvariant()) { case "none": return StackingBehavior.None; case "duration": case "durationrefresh": return StackingBehavior.DurationRefresh; case "maxstacks": return StackingBehavior.MaxStacks; case "maxstacksrefresh": return StackingBehavior.MaxStacksRefresh; default: throw new CatalogValidationException($"{path}: unknown stacking type '{value}' for id {id}"); } }
    }

    /// <summary>Explicit compatibility importer for the legacy game configuration skill table.</summary>
    public static class LegacySkillImporter
    {
        public static IReadOnlyDictionary<string, AbilityId> ImportAliases(IEnumerable<string> names, string sourcePath)
        {
            if (names == null) throw new CatalogValidationException($"{sourcePath}: legacy skill list is null");
            var result = new Dictionary<string, AbilityId>(StringComparer.OrdinalIgnoreCase);
            int id = 0;
            foreach (string name in names)
            {
                if (string.IsNullOrWhiteSpace(name))
                    throw new CatalogValidationException($"{sourcePath}: legacy skill id {id} has empty name");
                if (result.ContainsKey(name))
                    throw new CatalogValidationException($"{sourcePath}: legacy alias conflict '{name}'");
                result.Add(name, new AbilityId(id++));
            }
            return result;
        }
    }
}
