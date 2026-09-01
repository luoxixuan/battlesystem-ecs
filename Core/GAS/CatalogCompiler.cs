using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Collections.ObjectModel;
using BattleSystemECS.Config;

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
        private readonly bool _hasRuntimeExtensions;
        public IReadOnlyList<AbilityCatalogEntry> Abilities => _abilitiesView;
        public IReadOnlyList<AbilityDefinition> AbilityDefinitions => _abilityDefinitionsView;
        public IReadOnlyList<TargetingDefinition> Targetings => _targetingsView;
        public IReadOnlyList<ModifierDefinition> Modifiers => _modifiersView;
        public IReadOnlyList<TriggerDefinition> Triggers => _triggersView;
        public IReadOnlyList<GameplayEffectDefinition> Effects => _effectsView;
        public IReadOnlyList<ExecutionDefinition> Executions => _executionsView;
        public IReadOnlyDictionary<string, AbilityId> Aliases => _aliases;
        internal bool HasRuntimeExtensions => _hasRuntimeExtensions;
        internal GameplayCatalog(IReadOnlyList<AbilityDefinition> abilities, IReadOnlyList<TargetingDefinition> targetings, IReadOnlyList<GameplayEffectDefinition> effects, IReadOnlyList<ExecutionDefinition> executions, IReadOnlyList<TriggerDefinition> triggers, IReadOnlyList<ModifierDefinition> modifiers, IReadOnlyDictionary<string, AbilityId> aliases, bool hasRuntimeExtensions = false)
        {
            _hasRuntimeExtensions = hasRuntimeExtensions;
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
        public static GameplayCatalog CreateEmpty()
        {
            return new GameplayCatalog(Array.Empty<AbilityDefinition>(), Array.Empty<TargetingDefinition>(),
                Array.Empty<GameplayEffectDefinition>(), Array.Empty<ExecutionDefinition>(),
                Array.Empty<TriggerDefinition>(), Array.Empty<ModifierDefinition>(),
                new Dictionary<string, AbilityId>(StringComparer.OrdinalIgnoreCase));
        }

        public static GameplayCatalog CompileRuntimeExtensions(GameplayCatalog catalog, RuntimeCatalogSpec spec)
        {
            catalog = catalog ?? CreateEmpty();
            if (catalog.HasRuntimeExtensions) return catalog;
            if (spec.TriggerThreshold < 1) throw new CatalogValidationException("runtime catalog: trigger threshold must be positive");
            if (spec.DamageBonusPerKill < 0f || float.IsNaN(spec.DamageBonusPerKill) || float.IsInfinity(spec.DamageBonusPerKill)) throw new CatalogValidationException("runtime catalog: damage bonus is invalid");
            if (spec.MaxMultiplier < 1f || float.IsNaN(spec.MaxMultiplier) || float.IsInfinity(spec.MaxMultiplier)) throw new CatalogValidationException("runtime catalog: max multiplier is invalid");
            int effectId = catalog.Effects.Count;
            int triggerId = catalog.Triggers.Count;
            int maxStacks = spec.DamageBonusPerKill > 0f ? Math.Max(1, (int)Math.Ceiling((spec.MaxMultiplier - 1f) / spec.DamageBonusPerKill)) : 1;
            var effects = new List<GameplayEffectDefinition>(catalog.Effects);
            effects.Add(new GameplayEffectDefinition(new EffectId(effectId), EffectType.Duration,
                new[] { new ModifierDefinition(CatalogRegistries.AttackDamage, AttributeModifierOp.Multiply, 1f + spec.DamageBonusPerKill) },
                0f, 0f, ClockId.Combat, StackingBehavior.MaxStacksRefresh, maxStacks, RefreshPolicy.StacksAndDuration,
                SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent, CatalogRegistries.SkillTag, Array.Empty<ExecutionId>(),
                stackKey: CatalogRegistries.SkillTag));
            var triggers = new List<TriggerDefinition>(catalog.Triggers);
            triggers.Add(new TriggerDefinition(new TriggerId(triggerId), GameplayEventType.HitConfirmed, new EffectId(effectId),
                CatalogRegistries.SkillConsumer, scope: TriggerScope.PerSource, threshold: spec.TriggerThreshold,
                mode: TriggerMode.EveryN, preserveRemainder: true));
            var extended = new GameplayCatalog(catalog.AbilityDefinitions, catalog.Targetings, effects, catalog.Executions,
                triggers, catalog.Modifiers, catalog.Aliases, hasRuntimeExtensions: true);
            CatalogValidator.Validate(extended, "runtime catalog");
            return extended;
        }

        /// <summary>
        /// 将敌人配置引用编译进玩家能力共用的不可变目录。规范定义优先，敌人 id
        /// 作为别名加入，使行为树引用和显示名称归一到同一个 AbilityId。
        /// </summary>
        public static GameplayCatalog CompileEnemyExtensions(GameplayCatalog catalog, IReadOnlyList<EnemyAbilityDef> enemyAbilities)
        {
            catalog = catalog ?? CreateEmpty();
            if (enemyAbilities == null || enemyAbilities.Count == 0) return catalog;

            var abilities = new List<AbilityDefinition>(catalog.AbilityDefinitions);
            var targetings = new List<TargetingDefinition>(catalog.Targetings);
            var effects = new List<GameplayEffectDefinition>(catalog.Effects);
            var executions = new List<ExecutionDefinition>(catalog.Executions);
            var aliases = new Dictionary<string, AbilityId>(catalog.Aliases, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < enemyAbilities.Count; i++)
            {
                var source = enemyAbilities[i];
                if (source == null || string.IsNullOrWhiteSpace(source.Id) || string.IsNullOrWhiteSpace(source.Name))
                    throw new CatalogValidationException($"enemy abilities: missing id/name at index {i}");
                if (!EnemyAbilityTypeRegistry.TryResolve(source.AbilityType, out var type))
                    throw new CatalogValidationException($"enemy abilities: unsupported AbilityType '{source.AbilityType}' for '{source.Id}'");

                bool hasNameAlias = aliases.TryGetValue(source.Name, out var abilityId);
                bool needsTypedDefinition = type.Payload.HasValue;
                EffectPayloadKind requiredPayload = type.Payload.GetValueOrDefault();
                bool nameIsCompatible = hasNameAlias && (!needsTypedDefinition ||
                    AbilityContainsPayload(abilities, executions, abilityId, requiredPayload,
                        type.Operation));
                if (!nameIsCompatible)
                {
                    abilityId = new AbilityId(abilities.Count);
                    var executionIds = new List<ExecutionId>();
                    var effectIds = new List<EffectId>();
                    TargetingShape shape = type.Targeting;
                    EffectPayloadKind payload = type.Payload ?? EffectPayloadKind.GameplayEvent;
                    ExecutionOperation operation = type.Operation;
                    float magnitude = 0f;
                    float duration = 0f;

                    switch (type.Kind)
                    {
                        case EnemyAbilityKind.SelfHeal:
                        case EnemyAbilityKind.HealAllies:
                            magnitude = source.HealAmount;
                            break;
                        case EnemyAbilityKind.AoeDamage:
                            magnitude = source.DamageMultiplier;
                            break;
                        case EnemyAbilityKind.StunAoe:
                            magnitude = source.StunDuration;
                            duration = source.StunDuration;
                            break;
                        case EnemyAbilityKind.SlowAoe:
                            magnitude = source.SlowFactor;
                            duration = source.SlowDuration;
                            break;
                        case EnemyAbilityKind.SummonMinion:
                        case EnemyAbilityKind.StealthAttack:
                            magnitude = 1f;
                            break;
                        case EnemyAbilityKind.BuffAllies:
                            magnitude = source.DamageMultiplier;
                            duration = source.BuffDuration;
                            if (magnitude <= 0f || duration <= 0f)
                                throw new CatalogValidationException($"enemy abilities: '{source.Id}' requires positive buff magnitude and duration");
                            var buffEffect = new EffectId(effects.Count);
                            effects.Add(new GameplayEffectDefinition(buffEffect, EffectType.Duration,
                                new[] { new ModifierDefinition(CatalogRegistries.AttackDamage, AttributeModifierOp.Multiply, 1f + magnitude) },
                                duration, 0f, ClockId.Enemy, StackingBehavior.DurationRefresh, 1, RefreshPolicy.Duration,
                                SourceDeathPolicy.Persist, EffectPayloadKind.Status, CatalogRegistries.EnemyBuffTag,
                                Array.Empty<ExecutionId>(), grantedTags: new[] { CatalogRegistries.EnemyBuffTag },
                                stackKey: CatalogRegistries.EnemyBuffTag));
                            effectIds.Add(buffEffect);
                            break;
                        case EnemyAbilityKind.SilenceTower:
                            duration = source.SilenceDuration;
                            if (duration <= 0f)
                                throw new CatalogValidationException($"enemy abilities: '{source.Id}' requires positive silence duration");
                            var silenceEffect = new EffectId(effects.Count);
                            effects.Add(new GameplayEffectDefinition(silenceEffect, EffectType.Duration,
                                Array.Empty<ModifierDefinition>(), duration, 0f, ClockId.Enemy,
                                StackingBehavior.DurationRefresh, 1, RefreshPolicy.Duration, SourceDeathPolicy.Persist,
                                EffectPayloadKind.Status, CatalogRegistries.TowerSilencedTag, Array.Empty<ExecutionId>(),
                                grantedTags: new[] { CatalogRegistries.TowerSilencedTag },
                                stackKey: CatalogRegistries.TowerSilencedTag));
                            effectIds.Add(silenceEffect);
                            break;
                        case EnemyAbilityKind.DispelTower:
                            magnitude = 1f;
                            break;
                    }

                    // 无执行项的定义表示世界动作或不受支持的条目，由运行时适配器决定是否执行。
                    if (operation != ExecutionOperation.Default)
                    {
                        var executionId = new ExecutionId(executions.Count);
                        executions.Add(new ExecutionDefinition(executionId, payload, Math.Max(0f, magnitude),
                            CatalogRegistries.SkillTag, MagnitudeSource.Multiplier, DamageAmountStage.LegacyMultiplier,
                            Math.Max(0f, duration), operation));
                        executionIds.Add(executionId);
                    }

                    bool group = string.Equals(source.AbilityType, "heal_allies", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(source.AbilityType, "aoe_damage", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(source.AbilityType, "stun_aoe", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(source.AbilityType, "slow_aoe", StringComparison.OrdinalIgnoreCase) ||
                                 type.Kind == EnemyAbilityKind.BuffAllies || type.Kind == EnemyAbilityKind.SilenceTower ||
                                 type.Kind == EnemyAbilityKind.DispelTower;
                    bool self = string.Equals(source.AbilityType, "self_heal", StringComparison.OrdinalIgnoreCase) ||
                                 payload == EffectPayloadKind.WorldAction;
                    float configuredRadius = type.Kind == EnemyAbilityKind.SilenceTower && source.SilenceRadius > 0f
                        ? source.SilenceRadius : type.Kind == EnemyAbilityKind.DispelTower && source.DispelRadius > 0f
                            ? source.DispelRadius : source.AoeRadius;
                    RelationFilter relation = type.Kind == EnemyAbilityKind.BuffAllies
                        ? RelationFilter.Allies : self ? RelationFilter.Self : RelationFilter.Enemies;
                    var targeting = new TargetingDefinition(new TargetingId(abilityId.Value), shape,
                        (int)Math.Max(0f, configuredRadius), 1, 1, group ? 0 : 1,
                        radius: Math.Max(0f, configuredRadius), relation: relation,
                        maxTargetsMode: group ? MaxTargetsPolicy.Unlimited : MaxTargetsPolicy.Fixed);
                    targetings.Add(targeting);
                    string compiledName = hasNameAlias ? source.Name + " [" + source.Id + "]" : source.Name;
                    abilities.Add(new AbilityDefinition(abilityId, compiledName, targeting, ClockId.Combat,
                        Math.Max(0f, source.Cooldown), GameplayPhaseMask.Wave, effectIds.ToArray(),
                        Array.Empty<ModifierDefinition>(), CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer,
                        executions: executionIds.ToArray()));
                    if (!hasNameAlias) aliases.Add(source.Name, abilityId);
                }

                if (aliases.TryGetValue(source.Id, out var existing) && existing.Value != abilityId.Value)
                    throw new CatalogValidationException($"enemy abilities: alias '{source.Id}' is ambiguous");
                aliases[source.Id] = abilityId;
            }

            var extended = new GameplayCatalog(abilities, targetings, effects, executions,
                catalog.Triggers, catalog.Modifiers, aliases, catalog.HasRuntimeExtensions);
            CatalogValidator.Validate(extended, "enemy ability catalog extensions");
            return extended;
        }

        public static GameplayCatalog CompileGlobalSkillExtensions(GameplayCatalog catalog,
            IReadOnlyList<GlobalSkillDef> globalSkills)
        {
            catalog = catalog ?? CreateEmpty();
            if (globalSkills == null || globalSkills.Count == 0) return catalog;
            var abilities = new List<AbilityDefinition>(catalog.AbilityDefinitions);
            var targetings = new List<TargetingDefinition>(catalog.Targetings);
            var executions = new List<ExecutionDefinition>(catalog.Executions);
            var aliases = new Dictionary<string, AbilityId>(catalog.Aliases, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < globalSkills.Count; i++)
            {
                var source = globalSkills[i];
                if (source == null || string.IsNullOrWhiteSpace(source.Name))
                    throw new CatalogValidationException($"global skills: missing name at index {i}");
                if (aliases.ContainsKey(source.Name)) continue;
                var abilityId = new AbilityId(abilities.Count);
                var executionId = new ExecutionId(executions.Count);
                TargetingShape shape;
                RelationFilter relation;
                GameplayPhaseMask phases;
                ExecutionDefinition execution;
                switch ((GlobalSkillType)source.SkillType)
                {
                    case GlobalSkillType.MeteorStrike:
                        shape = TargetingShape.Circle; relation = RelationFilter.Enemies; phases = GameplayPhaseMask.Wave;
                        execution = new ExecutionDefinition(executionId, EffectPayloadKind.Damage,
                            Math.Max(0f, source.DamagePct), CatalogRegistries.SkillTag,
                            MagnitudeSource.Constant, DamageAmountStage.Raw, operation: ExecutionOperation.ApplyDamage);
                        break;
                    case GlobalSkillType.EmergencyHeal:
                        shape = TargetingShape.Heal; relation = RelationFilter.Self;
                        phases = GameplayPhaseMask.Build | GameplayPhaseMask.Wave;
                        execution = new ExecutionDefinition(executionId, EffectPayloadKind.Heal,
                            Math.Max(0f, source.HealPct), CatalogRegistries.SkillTag,
                            MagnitudeSource.Constant, operation: ExecutionOperation.ApplyHeal);
                        break;
                    default:
                        throw new CatalogValidationException($"global skills: unsupported typed SkillType {source.SkillType} at index {i}");
                }
                executions.Add(execution);
                var targeting = new TargetingDefinition(new TargetingId(abilityId.Value), shape,
                    int.MaxValue, 1, 1, relation == RelationFilter.Self ? 1 : 0,
                    radius: int.MaxValue, relation: relation,
                    maxTargetsMode: relation == RelationFilter.Self ? MaxTargetsPolicy.Fixed : MaxTargetsPolicy.Unlimited);
                targetings.Add(targeting);
                abilities.Add(new AbilityDefinition(abilityId, source.Name, targeting, ClockId.Combat,
                    Math.Max(0f, source.Cooldown), phases, Array.Empty<EffectId>(), Array.Empty<ModifierDefinition>(),
                    CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer,
                    executions: new[] { executionId },
                    costs: source.ManaCost > 0f ? new[] { new CostDefinition(CatalogRegistries.Mana, source.ManaCost) } : Array.Empty<CostDefinition>()));
                aliases.Add(source.Name, abilityId);
            }
            var extended = new GameplayCatalog(abilities, targetings, catalog.Effects, executions,
                catalog.Triggers, catalog.Modifiers, aliases, catalog.HasRuntimeExtensions);
            CatalogValidator.Validate(extended, "global skill catalog extensions");
            return extended;
        }

        /// <summary>
        /// The player skill bar is the lowest-precedence compatibility source. Every entry
        /// must resolve to a canonical or static definition and may only repeat non-default
        /// fields when they agree with that winning definition.
        /// </summary>
        public static void ValidatePlayerSkillAliases(GameplayCatalog catalog,
            IReadOnlyList<SkillConfig> playerSkills, string sourcePath)
        {
            if (catalog == null) throw new CatalogValidationException(sourcePath + ": compiled catalog is missing");
            if (playerSkills == null) throw new CatalogValidationException(sourcePath + ":$.Skills: player skill list is null");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < playerSkills.Count; i++)
            {
                var source = playerSkills[i];
                string path = sourcePath + ":$.Skills[" + i + "]";
                if (source == null || string.IsNullOrWhiteSpace(source.Name))
                    throw new CatalogValidationException(path + ".Name: required string is missing");
                if (!names.Add(source.Name))
                    throw new CatalogValidationException(path + ".Name: duplicate player alias '" + source.Name + "'");
                if (!catalog.TryResolveAlias(source.Name, out var id) || !catalog.TryGetAbility(id, out var ability))
                    throw new CatalogValidationException(path + ".Name: alias '" + source.Name + "' is not declared by canonical or static skills");

                AssertCompatible(source.Cooldown, ability.Cooldown, path + ".Cooldown");
                AssertCompatible(source.AttackRange, ability.Targeting.Range, path + ".AttackRange");
                AssertCompatible(source.AreaWidth, ability.Targeting.Width, path + ".AreaWidth");
                AssertCompatible(source.AreaHeight, ability.Targeting.Height, path + ".AreaHeight");
                AssertCompatible(source.AreaRadius, ability.Targeting.Radius, path + ".AreaRadius");
                if (!string.IsNullOrWhiteSpace(source.AreaShape))
                {
                    TargetingShape shape = ParseShapeName(source.AreaShape, path + ".AreaShape");
                    if (shape != ability.Targeting.Shape)
                        throw new CatalogValidationException(path + ".AreaShape: conflicts with higher-precedence definition");
                }
            }
        }

        private static void AssertCompatible(float lowerPriorityValue, float winningValue, string path)
        {
            if (lowerPriorityValue == 0f) return;
            if (float.IsNaN(lowerPriorityValue) || float.IsInfinity(lowerPriorityValue) ||
                Math.Abs(lowerPriorityValue - winningValue) > 0.0001f)
                throw new CatalogValidationException(path + ": conflicts with higher-precedence definition");
        }

        private static TargetingShape ParseShapeName(string value, string path)
        {
            switch (value.Trim().ToLowerInvariant())
            {
                case "single": return TargetingShape.Single;
                case "cross": return TargetingShape.Cross;
                case "box": return TargetingShape.Box;
                case "circle": return TargetingShape.Circle;
                case "chain": return TargetingShape.Chain;
                case "heal": return TargetingShape.Heal;
                case "shield": return TargetingShape.Shield;
                case "line": return TargetingShape.Line;
                case "freeze": return TargetingShape.Freeze;
                case "cone": return TargetingShape.Cone;
                case "groundtarget": return TargetingShape.GroundTarget;
                case "slow": return TargetingShape.Slow;
                case "timerwind": return TargetingShape.TimeRewind;
                case "chainheal": return TargetingShape.ChainHeal;
                case "massresurrect": return TargetingShape.MassResurrect;
                case "aoestun": return TargetingShape.AoeStun;
                case "aoeroot": return TargetingShape.AoeRoot;
                case "aoeknockback": return TargetingShape.AoeKnockback;
                default: throw new CatalogValidationException(path + ": unknown target shape '" + value + "'");
            }
        }

        private static bool AbilityContainsPayload(IReadOnlyList<AbilityDefinition> abilities,
            IReadOnlyList<ExecutionDefinition> executions, AbilityId abilityId, EffectPayloadKind payload,
            ExecutionOperation operation)
        {
            if ((uint)abilityId.Value >= (uint)abilities.Count) return false;
            var ability = abilities[abilityId.Value];
            for (int i = 0; i < ability.Executions.Count; i++)
            {
                int executionId = ability.Executions[i].Value;
                if ((uint)executionId < (uint)executions.Count && executions[executionId].Payload == payload &&
                    executions[executionId].Operation == operation) return true;
            }
            return false;
        }

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
                            var grantedTags = ParseTags(mod, "GrantedTags", canonicalSkillsPath, id);
                            var blockedEffectTags = ParseTags(mod, "BlockedTags", canonicalSkillsPath, id);
                            EnsureNoTagConflict(grantedTags, blockedEffectTags, canonicalSkillsPath, id, "effect");
                            effects.Add(new GameplayEffectDefinition(new EffectId(effectIndex), period > 0 ? EffectType.Periodic : EffectType.Duration, Array.Empty<ModifierDefinition>(), Number(mod, "Duration", duration, canonicalSkillsPath, id), period, ClockId.Combat, stacking, maxStacks, stacking == StackingBehavior.None ? RefreshPolicy.None : RefreshPolicy.Duration, SourceDeathPolicy.Persist, modType == "CrowdControl" ? EffectPayloadKind.CrowdControl : EffectPayloadKind.Damage, tag, new[] { executions[executions.Count - 1].Id }, grantedTags, blockedEffectTags));
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
                    var requiredTags = ParseTags(node, "RequiredTags", canonicalSkillsPath, id);
                    var blockedTags = ParseTags(node, "BlockedTags", canonicalSkillsPath, id);
                    EnsureNoTagConflict(requiredTags, blockedTags, canonicalSkillsPath, id, "ability");
                    typedAbilities.Add(new AbilityDefinition(new AbilityId(id), name, targeting, ClockId.Combat, Number(node, "Cooldown", 0f, canonicalSkillsPath, id), ParseAllowedPhases(node, targeting.Shape, canonicalSkillsPath, id), effectIds.ToArray(), abilityModifiers.ToArray(), CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer, ActivationPolicy.Instant, manaCost, abilityExecutions.ToArray(), manaCost > 0 ? new[] { new CostDefinition(CatalogRegistries.Mana, manaCost) } : Array.Empty<CostDefinition>(), requiredTags, blockedTags));
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
                        TargetingShape staticShape = staticRecord.Width > 1 || staticRecord.Height > 1
                            ? TargetingShape.Box : TargetingShape.Single;
                        var entry = new AbilityCatalogEntry(new AbilityId(abilities.Count), name, (int)staticShape, 0f);
                        var staticExecution = new ExecutionId(executions.Count);
                        executions.Add(new ExecutionDefinition(staticExecution, EffectPayloadKind.Damage, staticRecord.DamageMultiplier, CatalogRegistries.SkillTag, MagnitudeSource.Multiplier, DamageAmountStage.LegacyMultiplier));
                        var staticTargeting = new TargetingDefinition(new TargetingId(entry.Id.Value), staticShape, staticRecord.Range,
                                staticRecord.Width, staticRecord.Height, staticShape == TargetingShape.Single ? 1 : 0,
                                requiredTags: ParseTags(node, "TargetRequiredTags", skillPath, entry.Id.Value),
                                blockedTags: ParseTags(node, "TargetBlockedTags", skillPath, entry.Id.Value),
                                relation: RelationFilter.Enemies,
                                maxTargetsMode: staticShape == TargetingShape.Single ? MaxTargetsPolicy.Fixed : MaxTargetsPolicy.Unlimited);
                        var staticRequired = ParseTags(node, "RequiredTags", skillPath, entry.Id.Value);
                        var staticBlocked = ParseTags(node, "BlockedTags", skillPath, entry.Id.Value);
                        EnsureNoTagConflict(staticRequired, staticBlocked, skillPath, entry.Id.Value, "ability");
                        typedAbilities.Add(new AbilityDefinition(entry.Id, name, staticTargeting,
                            ClockId.Combat, staticRecord.Cooldown, ParseAllowedPhases(node, staticShape, skillPath, entry.Id.Value), Array.Empty<EffectId>(),
                            Array.Empty<ModifierDefinition>(), CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer,
                            ActivationPolicy.Instant, staticRecord.ManaCost, new[] { staticExecution },
                            staticRecord.ManaCost > 0 ? new[] { new CostDefinition(CatalogRegistries.Mana, staticRecord.ManaCost) } : Array.Empty<CostDefinition>(), staticRequired, staticBlocked));
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
            bool self = parsed == TargetingShape.Heal || parsed == TargetingShape.Shield ||
                        parsed == TargetingShape.TimeRewind ||
                        parsed == TargetingShape.MassResurrect;
            bool fixedTarget = parsed == TargetingShape.Single || self;
            bool chain = parsed == TargetingShape.Chain || parsed == TargetingShape.ChainHeal;
            var requiredTags = ParseTags(node, "TargetRequiredTags", path, id);
            var blockedTags = ParseTags(node, "TargetBlockedTags", path, id);
            EnsureNoTagConflict(requiredTags, blockedTags, path, id, "targeting");
            return new TargetingDefinition(new TargetingId(id), parsed, Int(node, "AttackRange", path, id),
                Int(node, "AreaWidth", path, id), Int(node, "AreaHeight", path, id), chain ? 4 : fixedTarget ? 1 : 0,
                Number(node, "AreaRadius", 0f, path, id), Number(node, "ConeAngleDegrees", 0f, path, id),
                requiredTags, blockedTags,
                relation: parsed == TargetingShape.ChainHeal ? RelationFilter.Allies :
                    self ? RelationFilter.Self : RelationFilter.Enemies,
                maxTargetsMode: chain || fixedTarget ? MaxTargetsPolicy.Fixed : MaxTargetsPolicy.Unlimited);
        }
        private static GameplayPhaseMask ParseAllowedPhases(JsonElement node, TargetingShape shape, string path, int id)
        {
            if (!node.TryGetProperty("AllowedPhases", out var phases))
                return IsPreparationShape(shape) ? GameplayPhaseMask.Build | GameplayPhaseMask.Wave : GameplayPhaseMask.Wave;
            if (phases.ValueKind != JsonValueKind.Array)
                throw new CatalogValidationException($"{path}: AllowedPhases must be an array for id {id}");
            GameplayPhaseMask result = GameplayPhaseMask.None;
            foreach (var phase in phases.EnumerateArray())
            {
                if (phase.ValueKind != JsonValueKind.String)
                    throw new CatalogValidationException($"{path}: AllowedPhases contains a non-string value for id {id}");
                string value = phase.GetString();
                if (string.Equals(value, "Build", StringComparison.OrdinalIgnoreCase)) result |= GameplayPhaseMask.Build;
                else if (string.Equals(value, "Wave", StringComparison.OrdinalIgnoreCase)) result |= GameplayPhaseMask.Wave;
                else if (string.Equals(value, "Intermission", StringComparison.OrdinalIgnoreCase)) result |= GameplayPhaseMask.Intermission;
                else throw new CatalogValidationException($"{path}: unknown gameplay phase '{value}' for id {id}");
            }
            if (result == GameplayPhaseMask.None) throw new CatalogValidationException($"{path}: AllowedPhases is empty for id {id}");
            return result;
        }
        private static bool IsPreparationShape(TargetingShape shape) =>
            shape == TargetingShape.Heal || shape == TargetingShape.Shield || shape == TargetingShape.ChainHeal ||
            shape == TargetingShape.TimeRewind || shape == TargetingShape.MassResurrect;
        private static TagId[] ParseTags(JsonElement node, string property, string path, int id)
        {
            if (!node.TryGetProperty(property, out var tags)) return Array.Empty<TagId>();
            if (tags.ValueKind != JsonValueKind.Array)
                throw new CatalogValidationException($"{path}: {property} must be an array for id {id}");
            var result = new List<TagId>();
            var seen = new HashSet<int>();
            foreach (var value in tags.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) ||
                    !CatalogRegistries.TryTag(value.GetString(), out var tag))
                    throw new CatalogValidationException($"{path}: unknown tag in {property} for id {id}");
                if (!seen.Add(tag.Value)) throw new CatalogValidationException($"{path}: duplicate tag '{value.GetString()}' in {property} for id {id}");
                result.Add(tag);
            }
            return result.ToArray();
        }
        private static void EnsureNoTagConflict(IReadOnlyList<TagId> required, IReadOnlyList<TagId> blocked,
            string path, int id, string scope)
        {
            for (int i = 0; i < required.Count; i++)
                for (int j = 0; j < blocked.Count; j++)
                    if (required[i].Equals(blocked[j]))
                        throw new CatalogValidationException($"{path}: {scope} required/blocked tag conflict for id {id}");
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
