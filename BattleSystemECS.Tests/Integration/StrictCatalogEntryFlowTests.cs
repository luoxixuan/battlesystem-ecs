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
            var healDef = Assert.Single(config.EnemyAbilities, ability => ability.Id == "healer_aoe_heal");

            system.EnqueueAbility(healer, "healer_aoe_heal");
            system.ExecuteAbilities();

            Assert.Equal(20f + 100f * healDef.HealAmount, Store.EnemyHealth[first], 3);
            Assert.Equal(30f + 200f * healDef.HealAmount, Store.EnemyHealth[second], 3);
            float firstAfter = Store.EnemyHealth[first];
            float secondAfter = Store.EnemyHealth[second];
            system.EnqueueAbility(healer, "healer_aoe_heal");
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
            blockedSystem.EnqueueAbility(blockedHealer, "healer_aoe_heal");
            blockedSystem.ExecuteAbilities();
            Assert.Equal(20f, blockedStore.EnemyHealth[blockedFirst]);
            Assert.Equal(30f, blockedStore.EnemyHealth[blockedSecond]);
            blockedStore.BeginFrame();
            blockedSystem.EnqueueAbility(blockedHealer, "healer_aoe_heal");
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
            int activeBefore = Store.ActiveEnemyIds.Count;

            system.EnqueueAbility(summoner, "summon_minion");
            system.ExecuteAbilities();
            Assert.Equal(activeBefore + 1, Store.ActiveEnemyIds.Count);
            int minion = Store.ActiveEnemyIds[Store.ActiveEnemyIds.Count - 1];
            Assert.Equal(Store.PositionX[summoner], Store.PositionX[minion]);
            Assert.Equal(Store.PositionY[summoner], Store.PositionY[minion]);
            system.EnqueueAbility(summoner, "summon_minion");
            system.ExecuteAbilities();
            Assert.Equal(activeBefore + 1, Store.ActiveEnemyIds.Count);

            Assert.Equal(1f, Store.EnemyStealthMultiplier[ambusher]);
            system.EnqueueAbility(ambusher, "stealth_strike_1");
            system.ExecuteAbilities();
            var stealth = Assert.Single(config.EnemyAbilities, ability => ability.Id == "stealth_strike_1");
            Assert.Equal(stealth.DamageMultiplier, Store.EnemyStealthMultiplier[ambusher]);
            system.EnqueueAbility(ambusher, "stealth_strike_1");
            system.ExecuteAbilities();
            Assert.Equal(stealth.DamageMultiplier, Store.EnemyStealthMultiplier[ambusher]);
        }

        [Fact]
        public void MismatchedWorldActionDefinitionRejectsWithoutWorldOrCooldownSideEffects()
        {
            var source = new EnemyAbilityDef
            {
                Id = "summon_minion", Name = "Summon Minion", AbilityType = "summon_minion",
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
            Assert.True(catalog.TryResolveAlias("summon_minion", out var summonId));
            Assert.True(catalog.TryGetAbility(summonId, out var summon));
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

            Assert.True(registry.HeroSkill!.RequestHeroSkill(0, 2));
            scheduler.Tick(0.016f, 0);
            Assert.True(Store.PlayerCurrentHealth[player] > 40f);
            Assert.True(config.CompiledCatalog!.TryResolveAlias("Cold Nova", out var coldNova));
            Assert.True(config.CompiledCatalog.TryGetAbility(coldNova, out var coldDefinition));
            EffectId coldEffect = Assert.Single(coldDefinition.Effects);
            Assert.True(registry.HeroSkill.RequestHeroSkill(0, 3));
            scheduler.Tick(0.016f, 1);
            Assert.Contains(Enumerable.Range(0, Store.GetEffectCount(enemy)), slot =>
                Store.TryGetActiveEffectAt(enemy, slot, out _, out var definition, out _) && definition.Id == coldEffect);
            Store.EnemyInvulnFramesLeft[enemy] = 0;
            Store.EnemyBlinkIFramesLeft[enemy] = 0f;
            Assert.True(config.CompiledCatalog!.TryResolveAlias("Cross Slash", out var crossSlash));
            Assert.Equal(crossSlash.Value, registry.HeroSkill.GetHeroSkillId(0, 0));
            Assert.True(registry.HeroSkill.IsHeroSkillReady(0, 0));
            Assert.True(config.CompiledCatalog.TryGetAbility(crossSlash, out var crossDefinition));
            Assert.True(registry.HeroSkill.RequestHeroSkill(0, 0));
            scheduler.Tick(0.016f, 2);
            Assert.True(registry.HeroSkill.LastActivation.Accepted,
                $"activation={registry.HeroSkill.LastActivation.Reason}; effects={crossDefinition.Effects.Count}; executions={crossDefinition.Executions.Count}");

            Assert.False(Store.EnemyActive[enemy]);
            Assert.Equal(1, Store.TotalKills);
            Assert.True(Store.PlayerGold[player] >= expectedReward);
            Assert.True(events.KillEvents.Count >= 2);
            Assert.Equal(new[] { "killed", "destroyed" }, events.KillEvents.Take(2));
            Assert.True(config.CompiledCatalog!.TryResolveAlias("Guardian Heal", out var heal));
            Assert.True(config.CompiledCatalog.TryResolveAlias("Cross Slash", out var damage));
            Assert.NotEqual(heal, damage);
        }

        [Fact]
        public void EnemyAbilityTypeRegistryMatchesCompiledExecutionContracts()
        {
            var config = GameConfigLoader.LoadStrictCatalog(Renderer);
            var catalog = config.CompiledCatalog!;

            foreach (var source in config.EnemyAbilities)
            {
                Assert.True(EnemyAbilityTypeRegistry.TryResolve(source.AbilityType, out var type),
                    $"Unregistered enemy ability type '{source.AbilityType}'");
                Assert.Equal(source.AbilityType, type.Name, ignoreCase: true);
                Assert.True(catalog.TryResolveAlias(source.Id, out var abilityId));
                Assert.True(catalog.TryGetAbility(abilityId, out var ability));

                if (type.DispatchMode == EnemyAbilityDispatchMode.TypedCatalog)
                {
                    Assert.True(type.Payload.HasValue);
                    Assert.Contains(ability.Executions, executionId =>
                        catalog.TryGetExecution(executionId, out var execution) &&
                        execution.Payload == type.Payload.Value && execution.Operation == type.Operation);
                }
            }
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
            var source = config.EnemyAbilities.First(def => string.Equals(def.AbilityType, "aoe_damage", StringComparison.OrdinalIgnoreCase));
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
            var enemyAbility = config.EnemyAbilities.First(def => string.Equals(def.AbilityType, "aoe_damage", StringComparison.OrdinalIgnoreCase));

            Assert.True(registry.Skill!.RequestCatalogAbility(damage.Id));
            Assert.True(registry.TowerActiveSkill!.RequestTowerActive(tower));
            Assert.True(registry.HeroSkill!.RequestHeroSkill(0, 0));
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
