using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Systems;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Integration
{
    public sealed class StrictCatalogEntryFlowTests : BattleTestBase
    {
        private static string HeroSkillsPath => Path.Combine(Directory.GetCurrentDirectory(), "Data", "Configs", "hero_skills.json");

        [Fact]
        public void StrictReferenceMatrixRejectsEveryUnclosedProductionEntry()
        {
            AssertStrictReject(config => config.Skills[0].Name = "missing-skill", "Skills[0]");
            AssertStrictReject(config => config.GlobalSkills.Add(new GlobalSkillDef { Name = "missing-global" }), "GlobalSkills[");
            AssertStrictReject(config => config.TowerTypes.Add(new TowerConfig
            {
                Name = "invalid tower",
                SpecialAbility = new TowerSpecialAbility { AbilityType = "missing_tower" }
            }), "SpecialAbility");
            AssertStrictReject(config =>
            {
                config.AutoSkill.Enabled = true;
                config.Skills.Clear();
            }, "AutoSkill");
            AssertStrictReject(config => config.EnemyAbilities.Add(new EnemyAbilityDef
            {
                Id = "missing-enemy",
                Name = "missing enemy",
                AbilityType = "aoe_damage"
            }), "missing-enemy");

            var heroConfig = GameConfigLoader.LoadStrictCatalog(Renderer);
            string path = Path.Combine(Path.GetTempPath(), "strict-hero-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, "{\"Skills\":[{\"SlotIndex\":0,\"SkillName\":\"missing hero\"}]}");
            try
            {
                var error = Assert.Throws<CatalogValidationException>(() =>
                    GameConfigLoader.ValidateStrictReferences(heroConfig, heroConfig.CompiledCatalog!, path));
                Assert.Contains("missing hero", error.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void EnemyGroupHealTargetsAllAlliesAndCapacityFailureIsAtomic()
        {
            var config = GameConfigLoader.LoadStrictCatalog(Renderer);
            int player = Player();
            int healer = Enemy(e => { e.X = 0f; e.Y = 0f; e.Health = 100f; e.MaxHealth = 100f; });
            int first = Enemy(e => { e.X = 1f; e.Y = 0f; e.Health = 20f; e.MaxHealth = 100f; });
            int second = Enemy(e => { e.X = 0f; e.Y = 1f; e.Health = 30f; e.MaxHealth = 200f; });
            var system = new EnemyAbilitySystem(Store, Renderer, player, config);
            system.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            var healDef = FindEnemyAbility(config, ExecutionOperation.ApplyHeal,
                (source, _) => source.AoeRadius > 0f).Source;

            system.EnqueueAbility(healer, healDef.Id);
            system.ExecuteAbilities();

            Assert.Equal(20f + 100f * healDef.HealAmount, Store.EnemyHealth[first], 3);
            Assert.Equal(30f + 200f * healDef.HealAmount, Store.EnemyHealth[second], 3);
            float firstAfter = Store.EnemyHealth[first];
            float secondAfter = Store.EnemyHealth[second];
            system.EnqueueAbility(healer, healDef.Id);
            system.ExecuteAbilities();
            Assert.Equal(firstAfter, Store.EnemyHealth[first]);
            Assert.Equal(secondAfter, Store.EnemyHealth[second]);

            using var blockedStore = new ComponentStore();
            blockedStore.AddPlayer(0, 10f, 1f, 1f, 1);
            int blockedHealer = blockedStore.AddEnemy(0, 0f, 0f, 100f, 100f, 1f, 1, 1);
            int blockedFirst = blockedStore.AddEnemy(0, 1f, 0f, 20f, 100f, 1f, 1, 1);
            int blockedSecond = blockedStore.AddEnemy(0, 0f, 1f, 30f, 100f, 1f, 1, 1);
            blockedStore.EnemyHealth[blockedFirst] = 20f;
            blockedStore.EnemyMaxHealth[blockedFirst] = 100f;
            blockedStore.EnemyHealth[blockedSecond] = 30f;
            blockedStore.EnemyMaxHealth[blockedSecond] = 200f;
            blockedStore.ResourceResolver.EnableDeferred(true);
            var playerHandle = blockedStore.GetEntityHandle(0);
            for (int i = 0; i < ResourceResolver.MaxPendingRequests - 1; i++)
                Assert.True(blockedStore.ResourceResolver.TryApply(new ResourceRequest(playerHandle, playerHandle,
                    new AttributeKey(7), 1f, i + 1, ownerPlayerId: 0)).Accepted);
            var blockedSystem = new EnemyAbilitySystem(blockedStore, Renderer, 0, config);
            blockedSystem.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            blockedSystem.EnqueueAbility(blockedHealer, healDef.Id);
            blockedSystem.ExecuteAbilities();
            Assert.Equal(20f, blockedStore.EnemyHealth[blockedFirst]);
            Assert.Equal(30f, blockedStore.EnemyHealth[blockedSecond]);
            blockedStore.BeginFrame();
            blockedSystem.EnqueueAbility(blockedHealer, healDef.Id);
            blockedSystem.ExecuteAbilities();
            blockedStore.ResourceResolver.CommitBoundary(DamageCommitBoundary.GameplayResolve);
            Assert.Equal(20f + 100f * healDef.HealAmount, blockedStore.EnemyHealth[blockedFirst], 3);
            Assert.Equal(30f + 200f * healDef.HealAmount, blockedStore.EnemyHealth[blockedSecond], 3);
        }

        [Fact]
        public void EnemyWorldActionsActivateCatalogBeforeExactlyOneAdapterCommit()
        {
            var config = GameConfigLoader.LoadStrictCatalog(Renderer);
            int player = Player();
            int summoner = Enemy(e =>
            {
                e.X = 2f; e.Y = 3f; e.Health = 100f; e.MaxHealth = 100f;
                e.Damage = 20f; e.MoveSpeed = 1f; e.GoldReward = 6;
            });
            int ambusher = Enemy(e => { e.X = 0f; e.Y = 0f; e.Health = 100f; e.MaxHealth = 100f; });
            var system = new EnemyAbilitySystem(Store, Renderer, player, config);
            system.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            var summon = FindEnemyAbility(config, ExecutionOperation.SummonEnemy).Source;
            var stealth = FindEnemyAbility(config, ExecutionOperation.PrepareStealth).Source;
            int activeBefore = Store.ActiveEnemyIds.Count;

            system.EnqueueAbility(summoner, summon.Id);
            system.ExecuteAbilities();
            Assert.Equal(activeBefore + 1, Store.ActiveEnemyIds.Count);
            int minion = Store.ActiveEnemyIds[Store.ActiveEnemyIds.Count - 1];
            Assert.Equal(Store.PositionX[summoner], Store.PositionX[minion]);
            Assert.Equal(Store.PositionY[summoner], Store.PositionY[minion]);
            Assert.Equal(Store.EnemyMaxHealth[summoner] * summon.MinionHealthMult,
                Store.EnemyMaxHealth[minion], 3);
            Assert.Equal(Store.EnemyDamage[summoner] * summon.MinionDamageMult,
                Store.EnemyDamage[minion], 3);
            system.EnqueueAbility(summoner, summon.Id);
            system.ExecuteAbilities();
            Assert.Equal(activeBefore + 1, Store.ActiveEnemyIds.Count);

            Assert.Equal(1f, Store.EnemyStealthMultiplier[ambusher]);
            system.EnqueueAbility(ambusher, stealth.Id);
            system.ExecuteAbilities();
            Assert.Equal(stealth.DamageMultiplier, Store.EnemyStealthMultiplier[ambusher]);
            system.EnqueueAbility(ambusher, stealth.Id);
            system.ExecuteAbilities();
            Assert.Equal(stealth.DamageMultiplier, Store.EnemyStealthMultiplier[ambusher]);
        }

        [Fact]
        public void DirectSummonDefinitionRequiresPositiveMultipliersDuringCompilation()
        {
            var invalid = new EnemyAbilityDef
            {
                Id = "invalid-summon", Name = "Invalid Summon", AbilityType = "summon_minion",
                MinionHealthMult = 0f, MinionDamageMult = 0.5f
            };

            var error = Assert.Throws<CatalogValidationException>(() =>
                CatalogCompiler.CompileEnemyExtensions(CatalogCompiler.CreateEmpty(), new[] { invalid }));
            Assert.Contains("positive summon", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void CompiledSummonDefinitionRequiresPositiveMultipliersDuringCatalogValidation()
        {
            var source = new EnemyAbilityDef
            {
                Id = "compiled-summon", Name = "Compiled Summon", AbilityType = "summon_minion",
                MinionHealthMult = 0.5f, MinionDamageMult = 0.25f
            };
            var catalog = CatalogCompiler.CompileEnemyExtensions(CatalogCompiler.CreateEmpty(), new[] { source });
            var ability = Assert.Single(catalog.AbilityDefinitions);
            var executionId = Assert.Single(ability.Executions);
            var original = catalog.Executions[executionId.Value];
            var invalidExecution = new ExecutionDefinition(original.Id, original.Payload, original.Magnitude,
                original.Tag, original.MagnitudeSource, original.Stage, 0f, original.Operation,
                original.Probability, original.Parameter);
            var invalid = new GameplayCatalog(catalog.AbilityDefinitions, catalog.Targetings, catalog.Effects,
                new[] { invalidExecution }, catalog.Triggers, catalog.Modifiers, catalog.Aliases);

            var error = Assert.Throws<CatalogValidationException>(() => CatalogValidator.Validate(invalid, "compiled-summon"));
            Assert.Contains("positive health and damage", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void InvalidSummonContractRejectsBeforeEntityAndCooldownCommit()
        {
            var source = new EnemyAbilityDef
            {
                Id = "atomic-summon", Name = "Atomic Summon", AbilityType = "summon_minion", Cooldown = 10f,
                MinionHealthMult = 0.5f, MinionDamageMult = 0.25f
            };
            var catalog = CatalogCompiler.CompileEnemyExtensions(CatalogCompiler.CreateEmpty(), new[] { source });
            var config = new GameConfig
            {
                StrictCatalogReferences = true,
                EnemyAbilities = new List<EnemyAbilityDef> { source },
                CompiledCatalog = catalog
            };
            int player = Player();
            int summoner = Enemy(e => { e.Health = 100f; e.MaxHealth = 100f; e.Damage = 20f; });
            var system = new EnemyAbilitySystem(Store, Renderer, player, config);
            system.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            int activeBefore = Store.ActiveEnemyIds.Count;
            int nextBefore = Store.NextEntityId;

            source.MinionHealthMult = 0f;
            system.EnqueueAbility(summoner, source.Id);
            system.ExecuteAbilities();
            Assert.Equal(activeBefore, Store.ActiveEnemyIds.Count);
            Assert.Equal(nextBefore, Store.NextEntityId);

            source.MinionHealthMult = 0.5f;
            system.EnqueueAbility(summoner, source.Id);
            system.ExecuteAbilities();
            Assert.Equal(activeBefore + 1, Store.ActiveEnemyIds.Count);
            Assert.Equal(nextBefore + 1, Store.NextEntityId);
        }

        [Fact]
        public void MismatchedWorldActionDefinitionRejectsWithoutWorldOrCooldownSideEffects()
        {
            var source = new EnemyAbilityDef
            {
                Id = "test-world-action", Name = "Test World Action", AbilityType = "summon_minion",
                Cooldown = 12f, MinionHealthMult = 0.3f, MinionDamageMult = 0.3f
            };
            var targeting = new TargetingDefinition(new TargetingId(0), TargetingShape.Single,
                0, 1, 1, 1, relation: RelationFilter.Self, maxTargetsMode: MaxTargetsPolicy.Fixed);
            var wrong = new ExecutionDefinition(new ExecutionId(0), EffectPayloadKind.WorldAction, 1f,
                CatalogRegistries.SkillTag, operation: ExecutionOperation.PrepareStealth);
            var ability = new AbilityDefinition(new AbilityId(0), source.Name, targeting, ClockId.Combat, source.Cooldown,
                GameplayPhaseMask.Wave, Array.Empty<EffectId>(), Array.Empty<ModifierDefinition>(),
                CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer, executions: new[] { wrong.Id });
            var config = new GameConfig
            {
                StrictCatalogReferences = true,
                EnemyAbilities = new List<EnemyAbilityDef> { source },
                CompiledCatalog = new GameplayCatalog(new[] { ability }, new[] { targeting },
                    Array.Empty<GameplayEffectDefinition>(), new[] { wrong }, Array.Empty<TriggerDefinition>(),
                    Array.Empty<ModifierDefinition>(), new Dictionary<string, AbilityId>
                    {
                        [source.Id] = ability.Id,
                        [source.Name] = ability.Id
                    })
            };
            int player = Player();
            int summoner = Enemy(e => { e.Health = 100f; e.MaxHealth = 100f; e.Damage = 10f; });
            var wrongCatalog = config.CompiledCatalog;
            config.CompiledCatalog = CatalogCompiler.CreateEmpty();
            var missingSystem = new EnemyAbilitySystem(Store, Renderer, player, config);
            missingSystem.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            int activeBefore = Store.ActiveEnemyIds.Count;
            missingSystem.EnqueueAbility(summoner, source.Id);
            missingSystem.ExecuteAbilities();
            Assert.Equal(activeBefore, Store.ActiveEnemyIds.Count);

            config.CompiledCatalog = wrongCatalog;
            var system = new EnemyAbilitySystem(Store, Renderer, player, config);
            system.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));

            system.EnqueueAbility(summoner, source.Id);
            system.ExecuteAbilities();
            Assert.Equal(activeBefore, Store.ActiveEnemyIds.Count);

            config.CompiledCatalog = CatalogCompiler.CompileEnemyExtensions(CatalogCompiler.CreateEmpty(),
                config.EnemyAbilities);
            var corrected = new EnemyAbilitySystem(Store, Renderer, player, config);
            corrected.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            corrected.EnqueueAbility(summoner, source.Id);
            corrected.ExecuteAbilities();
            Assert.Equal(activeBefore + 1, Store.ActiveEnemyIds.Count);
        }

        [Fact]
        public void StrictBootstrapRejectsWorldActionWithMismatchedOperation()
        {
            var config = GameConfigLoader.LoadStrictCatalog(Renderer);
            var catalog = config.CompiledCatalog!;
            var (_, summon) = FindEnemyAbility(config, ExecutionOperation.SummonEnemy);
            ExecutionId executionId = Assert.Single(summon.Executions);
            var executions = new ExecutionDefinition[catalog.Executions.Count];
            for (int i = 0; i < executions.Length; i++) executions[i] = catalog.Executions[i];
            var original = executions[executionId.Value];
            executions[executionId.Value] = new ExecutionDefinition(original.Id, original.Payload,
                original.Magnitude, original.Tag, original.MagnitudeSource, original.Stage,
                original.Duration, ExecutionOperation.PrepareStealth);
            var wrong = new GameplayCatalog(catalog.AbilityDefinitions, catalog.Targetings, catalog.Effects,
                executions, catalog.Triggers, catalog.Modifiers, catalog.Aliases, catalog.HasRuntimeExtensions);

            var validation = Assert.Throws<CatalogValidationException>(() =>
                GameConfigLoader.ValidateStrictReferences(config, wrong, HeroSkillsPath));
            Assert.Contains("SummonEnemy", validation.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void HeroEntriesReachResourceDamageDeathAndPresentationThroughProductionTick()
        {
            var config = GameConfigLoader.LoadStrictCatalog(Renderer);
            int player = Player(p => { p.Health = 100f; p.X = 0f; p.Y = 0f; p.AttackDamage = 1f; });
            Store.PlayerCurrentHealth[player] = 40f;
            Store.PlayerMana[player] = 100f;
            int enemy = Enemy(e => { e.X = 0f; e.Y = 1f; e.Health = 100f; e.MaxHealth = 100f; e.Damage = 0f; e.MoveSpeed = 0f; e.GoldReward = 7; });
            float expectedReward = Store.EnemyGoldReward[enemy];
            var events = new RecordingBattleEventBus();
            var registry = new SystemRegistry();
            registry.CreateAll(Store, config, Renderer, player, new StateMachine(), events);
            registry.WireDependencies(Store, player);
            var scheduler = new FrameScheduler(Store, config, events);
            registry.AssignToGroups(scheduler);
            scheduler.Phase = GameState.WavePhase;
            Store.HeroIsDeployed[0] = true;
            var heal = FindHeroBinding(config, ability => ability.Targeting.Relation == RelationFilter.Self &&
                HasPayload(config, ability, EffectPayloadKind.Heal));
            var damage = FindHeroBinding(config, ability => ability.Targeting.Relation == RelationFilter.Enemies &&
                ability.Effects.Count == 0 && HasPayload(config, ability, EffectPayloadKind.Damage));

            Assert.True(registry.HeroSkill!.RequestHeroSkill(0, heal.Slot));
            scheduler.Tick(0.016f, 0);
            Assert.True(Store.PlayerCurrentHealth[player] > 40f);
            Store.EnemyInvulnFramesLeft[enemy] = 0;
            Store.EnemyBlinkIFramesLeft[enemy] = 0f;
            Store.EnemyHealth[enemy] = 10f;
            Store.PlayerMana[player] = 100f;
            Assert.Equal(damage.Ability.Id.Value, registry.HeroSkill.GetHeroSkillId(0, damage.Slot));
            Assert.True(registry.HeroSkill.IsHeroSkillReady(0, damage.Slot));
            Assert.True(registry.HeroSkill.RequestHeroSkill(0, damage.Slot));
            scheduler.Tick(0.016f, 1);
            Assert.True(registry.HeroSkill.LastActivation.Accepted,
                $"activation={registry.HeroSkill.LastActivation.Reason}; effects={damage.Ability.Effects.Count}; executions={damage.Ability.Executions.Count}; damage={Store.DamageResolver.LastRejection}; pending={Store.DamageResolver.PendingRequestCount}; mana={Store.PlayerMana[player]}");

            Assert.False(Store.EnemyActive[enemy]);
            Assert.Equal(1, Store.TotalKills);
            Assert.True(Store.PlayerGold[player] >= expectedReward);
            Assert.True(events.KillEvents.Count >= 2);
            Assert.Equal(new[] { "killed", "destroyed" }, events.KillEvents.Take(2));
            Assert.NotEqual(heal.Ability.Id, damage.Ability.Id);
        }

        [Fact]
        public void EnemyAbilityTypeRegistryMatchesCompiledExecutionContracts()
        {
            var config = GameConfigLoader.LoadStrictCatalog(Renderer);
            var catalog = config.CompiledCatalog!;

            foreach (var source in config.EnemyAbilities)
            {
                Assert.True(EnemyAbilityTypeRegistry.TryResolve(source, out var type, out var payload, out var operation),
                    $"Unregistered enemy ability type '{source.AbilityType}'");
                Assert.Equal(source.AbilityType, type.Name, ignoreCase: true);
                Assert.True(catalog.TryResolveAlias(source.Id, out var abilityId));
                Assert.True(catalog.TryGetAbility(abilityId, out var ability));

                if (type.DispatchMode == EnemyAbilityDispatchMode.TypedCatalog)
                {
                    Assert.Contains(ability.Executions, executionId =>
                        catalog.TryGetExecution(executionId, out var execution) &&
                        execution.Payload == payload && execution.Operation == operation);
                }
            }
        }

        [Fact]
        public void StrictTypedTelegraphQueuesExactDefinitionAndDamagesOnceThroughProductionGraph()
        {
            var config = GameConfigLoader.LoadStrictCatalog(Renderer);
            var source = Assert.Single(TypedGameConfigParser.ParseEnemyAbilities(
                "[{\"Id\":\"typed_telegraph_fixture\",\"Name\":\"Typed Telegraph Fixture\"," +
                "\"AbilityType\":\"aoe_damage\",\"Cooldown\":5,\"AoeRadius\":4," +
                "\"DamageMultiplier\":2,\"TelegraphDuration\":1,\"TelegraphColor\":2}]",
                "typed-telegraph.json"));
            config.EnemyAbilities.Add(source);
            config.CompiledCatalog = CatalogCompiler.CompileEnemyExtensions(config.CompiledCatalog!, new[] { source });
            config.ManaShield.Enabled = false;
            GameConfigLoader.ValidateStrictReferences(config, config.CompiledCatalog, HeroSkillsPath);
            Assert.True(config.CompiledCatalog.TryResolveAlias(source.Id, out var abilityId));
            var ability = config.CompiledCatalog.AbilityDefinitions[abilityId.Value];
            var execution = Assert.Single(ability.Executions.Select(id => config.CompiledCatalog.Executions[id.Value]),
                value => value.Operation == ExecutionOperation.QueueTelegraph);
            int player = Player(p => { p.X = 0f; p.Y = 0f; p.Health = 100f; });
            int enemy = Enemy(e => { e.X = 0f; e.Y = 4f; e.Damage = 10f; e.MoveSpeed = 0f; });
            var events = new RecordingBattleEventBus();
            var (registry, scheduler) = CreateProduction(config, player, events);
            scheduler.AI.EnemyAI = null;
            scheduler.Combat.ManaShield = null;
            Store.PlayerShield[player] = 0f;
            Store.PlayerManaShield[player] = 0f;
            Store.PlayerManaShieldAbsorbRatio[player] = 0f;
            Store.PlayerMana[player] = 0f;
            Store.PlayerArmor[player] = 0f;
            Store.PlayerMinHealthFloor[player] = 0f;
            int published = 0;
            float appliedDamage = 0f;
            float remainingAfterDamage = 0f;
            registry.EventBus!.PlayerDamaged.Subscribe(evt =>
            {
                published++;
                appliedDamage += evt.Damage;
                remainingAfterDamage = evt.RemainingHealth;
            });
            float before = Store.PlayerCurrentHealth[player];

            registry.EnemyAbility!.EnqueueAbility(enemy, source.Id);
            scheduler.Tick(0.5f, 0);

            Assert.Equal(EffectPayloadKind.Telegraph, execution.Payload);
            Assert.Equal(source.TelegraphDuration, execution.Duration);
            Assert.Equal(source.TelegraphColor, execution.Parameter);
            Assert.Equal(before, Store.PlayerCurrentHealth[player]);
            Assert.Equal(1, registry.Telegraph!.ActiveZoneCount);
            var ids = new List<int>(); var xs = new List<float>(); var ys = new List<float>();
            var radii = new List<float>(); var remaining = new List<float>(); var durations = new List<float>();
            var shapes = new List<int>(); var colors = new List<int>();
            registry.Telegraph.GetActiveZones(ids, xs, ys, radii, remaining, durations, shapes, colors);
            Assert.Equal(source.TelegraphDuration, Assert.Single(durations));
            Assert.Equal(source.TelegraphDuration, Assert.Single(remaining));
            Assert.Equal(source.TelegraphColor, Assert.Single(colors));

            // ability.commit 在 Spatial.telegraph.update 之后，创建当帧不倒计时。
            scheduler.Tick(1f, 1);

            Assert.Equal(0, registry.Telegraph.ActiveZoneCount);
            Assert.Equal(1, published);
            Assert.Equal(Store.EnemyDamage[enemy] * source.DamageMultiplier, appliedDamage, 3);
            Assert.Equal(before - appliedDamage, remainingAfterDamage, 3);
        }

        [Fact]
        public void StrictFreezeDefinitionAppliesConfiguredProbabilityAndDurationThroughProductionGraph()
        {
            var config = GameConfigLoader.LoadStrictCatalog(Renderer);
            var freezeSource = config.SkillDefs.First(skill =>
                skill.FreezeDuration > 0f && skill.FreezeChance > 0f &&
                string.Equals(skill.AreaShape, "freeze", StringComparison.OrdinalIgnoreCase));
            Assert.True(config.CompiledCatalog!.TryResolveAlias(freezeSource.Name, out var abilityId));
            var ability = config.CompiledCatalog.AbilityDefinitions[abilityId.Value];
            var freezeExecution = Assert.Single(ability.Executions.Select(id => config.CompiledCatalog.Executions[id.Value]),
                value => value.Operation == ExecutionOperation.ApplyFreeze);
            Assert.Equal(freezeSource.FreezeDuration, freezeExecution.Magnitude);
            Assert.Equal(freezeSource.FreezeChance, freezeExecution.Probability);
            config.Skills.Clear();
            config.Skills.Add(freezeSource);
            int player = Player(p => { p.X = 0f; p.Y = 0f; p.AttackDamage = 1f; });
            const int targetCount = 64;
            var targets = new List<int>(targetCount);
            for (int i = 0; i < targetCount; i++)
                targets.Add(Enemy(e => { e.X = 0f; e.Y = 1f; e.Health = 1000f; e.MaxHealth = 1000f; e.MoveSpeed = 0f; }));
            var (registry, scheduler) = CreateProduction(config, player, new RecordingBattleEventBus());

            Assert.True(registry.Skill!.RequestCatalogAbility(abilityId));
            scheduler.Tick(0.016f, 0);

            Assert.True(registry.Skill.LastCatalogActivation.Accepted,
                registry.Skill.LastCatalogActivation.Reason.ToString());
            int frozen = targets.Count(Store.IsEnemyFrozen);
            Assert.InRange(frozen, 1, targetCount - 1);
            Assert.All(targets.Where(Store.IsEnemyFrozen), target =>
                Assert.Equal((float)Math.Ceiling(freezeSource.FreezeDuration), Store.EnemyStunDurationLeft[target]));
        }

        [Fact]
        public void StrictEnemyControlFieldsCompileAndCommitTheirConfiguredValues()
        {
            var config = GameConfigLoader.LoadStrictCatalog(Renderer);
            int player = Player(p => { p.X = 0f; p.Y = 0f; });
            int caster = Enemy(e => { e.X = 0f; e.Y = 1f; e.Health = 100f; e.MaxHealth = 100f; });
            int slowCaster = Enemy(e => { e.X = 1f; e.Y = 0f; e.Health = 100f; e.MaxHealth = 100f; });
            var stun = config.EnemyAbilities.First(ability => ability.StunDuration > 0);
            var slow = config.EnemyAbilities.First(ability => ability.SlowFactor > 0f && ability.SlowDuration > 0);
            var system = new EnemyAbilitySystem(Store, Renderer, player, config);
            system.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            Store.PlayerStunDuration[player] = 0;
            Store.PlayerSlowFactor[player] = 0f;
            Store.PlayerSlowDuration[player] = 0;

            system.EnqueueAbility(caster, stun.Id);
            system.ExecuteAbilities();
            system.EnqueueAbility(slowCaster, slow.Id);
            system.ExecuteAbilities();

            Assert.Equal(stun.StunDuration, Store.PlayerStunDuration[player]);
            Assert.Equal(slow.SlowFactor, Store.PlayerSlowFactor[player]);
            Assert.Equal(slow.SlowDuration, Store.PlayerSlowDuration[player]);
            Assert.True(config.CompiledCatalog!.TryResolveAlias(stun.Id, out var stunId));
            Assert.Contains(config.CompiledCatalog.AbilityDefinitions[stunId.Value].Executions, executionId =>
                config.CompiledCatalog.TryGetExecution(executionId, out var execution) &&
                execution.Operation == ExecutionOperation.ApplyCrowdControl &&
                execution.Magnitude == stun.StunDuration);
        }

        [Fact]
        public void ManualSkillRequestReachesDamageDeathRewardAndPresentationThroughProductionTick()
        {
            var config = GameConfigLoader.LoadStrictCatalog(Renderer);
            int player = Player(p => { p.X = 0f; p.Y = 0f; p.AttackDamage = 100f; p.AttackRange = 0f; });
            var events = new RecordingBattleEventBus();
            var (registry, scheduler) = CreateProduction(config, player, events);
            scheduler.Combat.PlayerTowerAttack = null;
            scheduler.Combat.TowerAttack = null;
            AbilityDefinition damage = default;
            bool found = false;
            for (int slot = 0; slot < Store.AbilityCount[player] && !found; slot++)
            {
                string name = Store.GetAbility(player, slot).Definition.Name;
                found = config.CompiledCatalog!.TryResolveAlias(name, out var id) &&
                    config.CompiledCatalog.TryGetAbility(id, out damage) &&
                    damage.Targeting.Relation == RelationFilter.Enemies && HasPayload(config, damage, EffectPayloadKind.Damage);
            }
            Assert.True(found);
            int enemy = Enemy(e => { e.X = 0f; e.Y = 1f; e.Health = 1f; e.MaxHealth = 1f; e.MoveSpeed = 0f; e.Damage = 0f; e.GoldReward = 11; });
            float expectedReward = Store.EnemyGoldReward[enemy];

            Assert.True(registry.Skill!.RequestCatalogAbility(damage.Id));
            scheduler.Tick(0.016f, 0);

            Assert.True(registry.Skill.LastCatalogActivation.Accepted, registry.Skill.LastCatalogActivation.Reason.ToString());
            Assert.False(Store.EnemyActive[enemy]);
            Assert.True(Store.PlayerGold[player] >= expectedReward);
            Assert.True(events.KillEvents.Count >= 2);
            Assert.Equal(new[] { "killed", "destroyed" }, events.KillEvents.Take(2));
        }

        [Fact]
        public void GlobalSkillRequestReachesDamageDeathRewardAndPresentationThroughProductionTick()
        {
            var config = GameConfigLoader.LoadStrictCatalog(Renderer);
            int player = Player(p => { p.X = 0f; p.Y = 10f; p.AttackDamage = 0f; p.AttackRange = 0f; });
            Store.PlayerMana[player] = Store.PlayerMaxMana[player] = 10000f;
            int globalIndex = config.GlobalSkills.FindIndex(def =>
                config.CompiledCatalog!.TryResolveAlias(def.Name, out var id) &&
                config.CompiledCatalog.TryGetAbility(id, out var ability) && HasPayload(config, ability, EffectPayloadKind.Damage));
            Assert.True(globalIndex >= 0);
            int enemy = Enemy(e => { e.X = 0f; e.Y = 1f; e.Health = 1f; e.MaxHealth = 1f; e.MoveSpeed = 0f; e.Damage = 0f; e.GoldReward = 13; });
            float expectedReward = Store.EnemyGoldReward[enemy];
            var events = new RecordingBattleEventBus();
            var (registry, scheduler) = CreateProduction(config, player, events);
            scheduler.Combat.PlayerTowerAttack = null;
            for (int i = 0; i < globalIndex; i++) Store.PlayerGlobalSkillCooldown[i] = 100f;
            Store.PlayerGlobalSkillPressed[player] = true;

            scheduler.Tick(0.016f, 0);

            Assert.False(Store.EnemyActive[enemy]);
            Assert.True(Store.PlayerGold[player] >= expectedReward);
            Assert.True(Store.PlayerGlobalSkillCooldown[globalIndex] > 0f);
            Assert.True(events.KillEvents.Count >= 2);
            Assert.Equal(new[] { "killed", "destroyed" }, events.KillEvents.Take(2));
        }

        [Fact]
        public void TowerActiveRequestReachesDamageDeathRewardAndPresentationThroughProductionTick()
        {
            var config = GameConfigLoader.LoadStrictCatalog(Renderer);
            int player = Player(p => { p.X = 0f; p.Y = 0f; p.AttackDamage = 0f; p.AttackRange = 0f; });
            var damage = FindCatalogAbility(config, a => config.TryGetSkillById(a.Id.Value) != null &&
                a.Targeting.Relation == RelationFilter.Enemies && HasPayload(config, a, EffectPayloadKind.Damage));
            int tower = RawTower(0, 0, damage: 100f, range: 20);
            Store.SetTowerActiveSkill(tower, damage.Id.Value, damage.Cooldown);
            int enemy = Enemy(e => { e.X = 0f; e.Y = 1f; e.Health = 1f; e.MaxHealth = 1f; e.MoveSpeed = 0f; e.Damage = 0f; e.GoldReward = 17; });
            float expectedReward = Store.EnemyGoldReward[enemy];
            var events = new RecordingBattleEventBus();
            var (registry, scheduler) = CreateProduction(config, player, events);
            scheduler.Combat.PlayerTowerAttack = null;
            scheduler.Combat.TowerAttack = null;

            Assert.True(registry.TowerActiveSkill!.RequestTowerActive(tower));
            scheduler.Tick(0.016f, 0);

            Assert.True(registry.TowerActiveSkill.LastActivation.Accepted, registry.TowerActiveSkill.LastActivation.Reason.ToString());
            Assert.False(Store.EnemyActive[enemy]);
            Assert.True(Store.PlayerGold[player] >= expectedReward);
            Assert.Equal(new[] { "killed", "destroyed" }, events.KillEvents);
        }

        [Fact]
        public void AutoSkillBuildRequestReachesCatalogHealAndResourceFactsThroughProductionTick()
        {
            var config = GameConfigLoader.LoadStrictCatalog(Renderer);
            config.AutoSkill.Enabled = true;
            config.AutoSkill.MaxSkillsPerPhase = 1;
            int player = Player(p => { p.X = 0f; p.Y = 0f; p.AttackDamage = 0f; });
            Store.PlayerMaxHealth[player] = 100f;
            Store.PlayerCurrentHealth[player] = 20f;
            var events = new RecordingBattleEventBus();
            var (registry, scheduler) = CreateProduction(config, player, events);
            int buildSlot = -1;
            for (int slot = 0; slot < Store.AbilityCount[player]; slot++)
            {
                var instance = Store.GetAbility(player, slot);
                if (SkillSystem.IsBuildAllowedAbility(instance.Definition.AreaShape) && buildSlot < 0) buildSlot = slot;
                else { instance.CurrentCooldown = 100f; Store.SetAbility(player, slot, instance); }
            }
            Assert.True(buildSlot >= 0);
            scheduler.Phase = GameState.BuildPhase;

            scheduler.Tick(0.016f, 0);

            Assert.True(registry.AutoSkill!.SuccessfulCastCount > 0);
            Assert.True(Store.PlayerCurrentHealth[player] > 20f);
            Assert.Contains(Enumerable.Range(0, Store.ResourceResolver.Events.Count),
                i => Store.ResourceResolver.Events.Get(i).Type == GameplayEventType.HealApplied);
        }

        [Fact]
        public void EnemyDamageUsesTypedPlayerResolverForShieldHealthEventsAndDeathFacts()
        {
            var config = GameConfigLoader.LoadStrictCatalog(Renderer);
            var source = FindEnemyAbility(config, ExecutionOperation.ApplyDamage).Source;
            int player = Player(p => { p.X = 0f; p.Y = 0f; p.AttackDamage = 0f; p.AttackRange = 0f; });
            Store.PlayerMaxHealth[player] = Store.PlayerCurrentHealth[player] = 20f;
            Store.PlayerShield[player] = 5f;
            int first = Enemy(e => { e.X = 0f; e.Y = 10f; e.Health = 100f; e.MaxHealth = 100f; e.Damage = 10f; e.MoveSpeed = 0f; });
            var events = new RecordingBattleEventBus();
            var (registry, scheduler) = CreateProduction(config, player, events);
            scheduler.AI.EnemyAI = null;
            var damaged = new List<PlayerDamagedEvent>();
            registry.EventBus!.PlayerDamaged.Subscribe(damaged.Add);
            Assert.True(config.CompiledCatalog!.TryResolveAlias(source.Id, out var typedId));
            Assert.True(config.CompiledCatalog.TryGetAbility(typedId, out var typed));
            int expectedHits = typed.Executions.Count(id => config.CompiledCatalog.TryGetExecution(id, out var execution) &&
                execution.Payload == EffectPayloadKind.Damage);
            registry.EnemyAbility!.EnqueueAbility(first, source.Id);

            scheduler.Tick(0.016f, 0);

            Assert.Equal(expectedHits, damaged.Count);
            Assert.All(damaged, fact => Assert.Equal(first, fact.AttackerId));
            Assert.True(Store.PlayerShield[player] < 5f || Store.PlayerCurrentHealth[player] < 20f);
            Assert.Equal(first, damaged[0].AttackerId);
            int lethal = Enemy(e => { e.X = 0f; e.Y = 10f; e.Health = 100f; e.MaxHealth = 100f; e.Damage = 10000f; e.MoveSpeed = 0f; });
            registry.EnemyAbility.EnqueueAbility(lethal, source.Id);
            scheduler.Tick(0.016f, 1);

            Assert.False(Store.IsPlayerAlive(player));
            Assert.Contains(Enumerable.Range(0, Store.ResourceResolver.Events.Count),
                i => Store.ResourceResolver.Events.Get(i).Type == GameplayEventType.DeathQueued);
            Assert.Equal(lethal, damaged[damaged.Count - 1].AttackerId);
        }

        [Fact]
        public void WaveRequestsDoNotLeakAcrossBuildBoundaryIntoNextWave()
        {
            var config = GameConfigLoader.LoadStrictCatalog(Renderer);
            int player = Player(p => { p.X = 0f; p.Y = 10f; p.AttackDamage = 100f; p.AttackRange = 0f; });
            Store.PlayerMana[player] = Store.PlayerMaxMana[player] = 10000f;
            int enemy = Enemy(e => { e.X = 0f; e.Y = 10f; e.Health = 100f; e.MaxHealth = 100f; e.Damage = 10f; e.MoveSpeed = 0f; });
            int tower = RawTower(0, 10, damage: 100f, range: 20);
            var events = new RecordingBattleEventBus();
            var (registry, scheduler) = CreateProduction(config, player, events);
            scheduler.Combat.PlayerTowerAttack = null;
            scheduler.Combat.TowerAttack = null;
            scheduler.AI.EnemyAI = null;
            var damage = FindCatalogAbility(config, ability => config.TryGetSkillById(ability.Id.Value) != null &&
                ability.Targeting.Relation == RelationFilter.Enemies && HasPayload(config, ability, EffectPayloadKind.Damage));
            Store.SetTowerActiveSkill(tower, damage.Id.Value, damage.Cooldown);
            Store.HeroIsDeployed[0] = true;
            var enemyAbility = FindEnemyAbility(config, ExecutionOperation.ApplyDamage).Source;
            int heroSlot = FindHeroBinding(config, _ => true).Slot;

            Assert.True(registry.Skill!.RequestCatalogAbility(damage.Id));
            Assert.True(registry.TowerActiveSkill!.RequestTowerActive(tower));
            Assert.True(registry.HeroSkill!.RequestHeroSkill(0, heroSlot));
            registry.EnemyAbility!.EnqueueAbility(enemy, enemyAbility.Id);
            Store.PlayerGlobalSkillPressed[player] = true;
            scheduler.Phase = GameState.BuildPhase;
            scheduler.Tick(0.016f, 0);
            scheduler.Phase = GameState.WavePhase;
            float enemyHealth = Store.EnemyHealth[enemy];
            float playerHealth = Store.PlayerCurrentHealth[player];

            scheduler.Tick(0.016f, 1);

            Assert.Equal(enemyHealth, Store.EnemyHealth[enemy]);
            Assert.Equal(playerHealth, Store.PlayerCurrentHealth[player]);
            Assert.Equal(0f, Store.TowerActiveCooldown[tower]);
            Assert.False(registry.Skill.LastCatalogActivation.Accepted);
            Assert.False(registry.TowerActiveSkill.LastActivation.Accepted);
            Assert.False(registry.HeroSkill.LastActivation.Accepted);
            Assert.False(Store.PlayerGlobalSkillPressed[player]);
        }

        private (SystemRegistry Registry, FrameScheduler Scheduler) CreateProduction(GameConfig config, int player,
            IBattleEventBus events)
        {
            var registry = new SystemRegistry();
            registry.CreateAll(Store, config, Renderer, player, new StateMachine(), events);
            registry.WireDependencies(Store, player);
            var scheduler = new FrameScheduler(Store, config, events);
            registry.AssignToGroups(scheduler);
            scheduler.Phase = GameState.WavePhase;
            return (registry, scheduler);
        }

        private static AbilityDefinition FindCatalogAbility(GameConfig config, Func<AbilityDefinition, bool> predicate) =>
            config.CompiledCatalog!.AbilityDefinitions.First(predicate);

        private static (EnemyAbilityDef Source, AbilityDefinition Ability) FindEnemyAbility(GameConfig config,
            ExecutionOperation operation, Func<EnemyAbilityDef, AbilityDefinition, bool>? predicate = null)
        {
            foreach (var source in config.EnemyAbilities)
            {
                if (!config.CompiledCatalog!.TryResolveAlias(source.Id, out var id) ||
                    !config.CompiledCatalog.TryGetAbility(id, out var ability) ||
                    predicate != null && !predicate(source, ability)) continue;
                if (ability.Executions.Any(executionId =>
                        config.CompiledCatalog.TryGetExecution(executionId, out var execution) &&
                        execution.Operation == operation)) return (source, ability);
            }
            throw new Xunit.Sdk.XunitException($"Missing enemy ability operation {operation}");
        }

        private static (int Slot, AbilityDefinition Ability) FindHeroBinding(GameConfig config,
            Func<AbilityDefinition, bool> predicate)
        {
            var bindings = HeroSkillSystem.HeroSkillsConfigLoader.Parse(File.ReadAllText(HeroSkillsPath), HeroSkillsPath);
            foreach (var binding in bindings.Skills ?? Enumerable.Empty<HeroSkillSystem.HeroSkillSlotEntry>())
            {
                if (binding.SkillName != null && config.CompiledCatalog!.TryResolveAlias(binding.SkillName, out var id) &&
                    config.CompiledCatalog.TryGetAbility(id, out var ability) && predicate(ability))
                    return (binding.SlotIndex, ability);
            }
            throw new Xunit.Sdk.XunitException("Missing hero binding for requested catalog behavior");
        }

        private static bool HasPayload(GameConfig config, AbilityDefinition ability, EffectPayloadKind payload) =>
            ability.Executions.Any(id => config.CompiledCatalog!.TryGetExecution(id, out var execution) && execution.Payload == payload);

        private void AssertStrictReject(Action<GameConfig> mutate, string expected)
        {
            var config = GameConfigLoader.LoadStrictCatalog(Renderer);
            mutate(config);
            var error = Assert.Throws<CatalogValidationException>(() =>
                GameConfigLoader.ValidateStrictReferences(config, config.CompiledCatalog!, HeroSkillsPath));
            Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class RecordingBattleEventBus : IBattleEventBus
        {
            public List<string> KillEvents { get; } = new List<string>();
            public void OnEntityCreated(int entityId, float x, float y, string entityType) { }
            public void OnTowerCreated(int entityId, float x, float y, TowerType towerType) { }
            public void OnEntityDestroyed(int entityId) => KillEvents.Add("destroyed");
            public void OnPositionChanged(int entityId, float x, float y) { }
            public void OnPositionsChanged(List<(int entityId, float x, float y)> changes) { }
            public void OnDamageDealt(int targetId, float amount, string damageType, bool isCritical) { }
            public void OnEntityKilled(int entityId, int killerId) => KillEvents.Add("killed");
            public void OnProjectileFired(float fromX, float fromY, float toX, float toY, float speed) { }
            public void OnWaveStarted(int waveNumber) { }
            public void OnGameOver(bool victory) { }
        }
    }
}
