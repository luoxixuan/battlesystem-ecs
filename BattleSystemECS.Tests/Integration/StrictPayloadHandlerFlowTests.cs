using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Systems;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Integration
{
    public sealed class StrictPayloadHandlerFlowTests : BattleTestBase
    {
        private static string DefaultHeroSkillsPath =>
            Path.Combine(Directory.GetCurrentDirectory(), "Data", "Configs", "hero_skills.json");

        [Fact]
        public void StrictHeroMassResurrectBindingUsesSharedProductionHandlerAndPublishesActivation()
        {
            var config = GameConfigLoader.LoadStrictCatalog(Renderer);
            string path = WriteHeroBinding("Mass Resurrect");
            try
            {
                GameConfigLoader.ValidateStrictReferences(config, config.CompiledCatalog!, path);
                int player = Player(p => { p.X = 4f; p.Y = 6f; });
                string monsterType = config.MonsterTypes[0].Type;
                Store.NecromancerQueueCorpse(-1, 5f, 6f, monsterType, 1f, 0f);
                var registry = CreateProduction(config, player);
                registry.Necromancer!.SetTurn(0, 0f);
                var hero = new HeroSkillSystem(Store, player, path, config);
                hero.SetPayloadHandler(registry.AbilityPayloads!);
                hero.Initialize();
                hero.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
                Store.HeroIsDeployed[0] = true;
                int activeBefore = Store.ActiveEnemyIds.Count;

                Assert.True(hero.TriggerHeroSkill(0, 0));

                Assert.True(hero.LastActivation.Accepted, hero.LastActivation.Reason.ToString());
                Assert.Equal(activeBefore + 1, Store.ActiveEnemyIds.Count);
                int minion = Store.ActiveEnemyIds[Store.ActiveEnemyIds.Count - 1];
                Assert.True(Store.EnemyIsReanimated[minion]);
                Assert.Equal(player, Store.EnemyOwnerId[minion]);
                var activation = FindLastEvent(GameplayEventType.AbilityActivated);
                Assert.Equal(player, activation.Source.Index);
                Assert.Equal(player, activation.Target.Index);
                Assert.Equal(player, activation.OwnerPlayerId);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void StrictTowerTimeRewindBindingRestoresOwnerResourcesWithTowerSourceFacts()
        {
            var config = GameConfigLoader.LoadStrictCatalog(Renderer);
            int rewindSkill = config.GetSkillIdByName("Time Rewind");
            Assert.True(rewindSkill >= 0);
            config.TowerTypes.Add(new TowerConfig { Name = "Rewind Test Tower", ActiveSkillId = rewindSkill });
            GameConfigLoader.ValidateStrictReferences(config, config.CompiledCatalog!, DefaultHeroSkillsPath);
            int player = Player(p => { p.X = 0f; p.Y = 0f; });
            var registry = CreateProduction(config, player);
            Store.PlayerMaxHealth[player] = 100f;
            Store.PlayerCurrentHealth[player] = 80f;
            Store.PlayerMana[player] = 70f;
            Store.PlayerShield[player] = 60f;
            registry.TimeRewind!.AppendSnapshot(player);
            Store.PlayerCurrentHealth[player] = 10f;
            Store.PlayerMana[player] = 20f;
            Store.PlayerShield[player] = 30f;
            int tower = RawTower(2, 3, damage: 1f, range: 5);
            Store.SetTowerActiveSkill(tower, rewindSkill, 5f);
            registry.TowerActiveSkill!.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));

            Assert.True(registry.TowerActiveSkill.TriggerTowerActive(tower));

            Assert.Equal(80f, Store.PlayerCurrentHealth[player]);
            Assert.Equal(70f, Store.PlayerMana[player]);
            Assert.Equal(60f, Store.PlayerShield[player]);
            var resourceFacts = Enumerable.Range(0, Store.ResourceResolver.Events.Count)
                .Select(i => Store.ResourceResolver.Events.Get(i))
                .Where(gameplayEvent => gameplayEvent.Source.Index == tower &&
                    gameplayEvent.Target.Index == player && gameplayEvent.OwnerPlayerId == player)
                .ToArray();
            Assert.Equal(3, resourceFacts.Length);
            Assert.Contains(resourceFacts, gameplayEvent => gameplayEvent.Type == GameplayEventType.HealApplied);
            Assert.Contains(resourceFacts, gameplayEvent => gameplayEvent.Type == GameplayEventType.ResourceChanged);
            Assert.Contains(resourceFacts, gameplayEvent => gameplayEvent.Type == GameplayEventType.ShieldChanged);
            var activation = FindLastEvent(GameplayEventType.AbilityActivated);
            Assert.Equal(tower, activation.Source.Index);
            Assert.Equal(player, activation.Target.Index);
            Assert.Equal(player, activation.OwnerPlayerId);
        }

        [Fact]
        public void StrictCatalogStartupRejectsExecutionWithoutProductionHandler()
        {
            var config = GameConfigLoader.LoadStrictCatalog(Renderer);
            var catalog = config.CompiledCatalog!;
            Assert.True(catalog.TryResolveAlias("Time Rewind", out var abilityId));
            Assert.True(catalog.TryGetAbility(abilityId, out var ability));
            ExecutionId executionId = Assert.Single(ability.Executions);
            var executions = catalog.Executions.ToArray();
            var original = executions[executionId.Value];
            executions[executionId.Value] = new ExecutionDefinition(original.Id, original.Payload,
                original.Magnitude, original.Tag, original.MagnitudeSource, original.Stage,
                original.Duration, ExecutionOperation.Default);
            var unsupported = new GameplayCatalog(catalog.AbilityDefinitions, catalog.Targetings, catalog.Effects,
                executions, catalog.Triggers, catalog.Modifiers, catalog.Aliases, catalog.HasRuntimeExtensions);

            var error = Assert.Throws<CatalogValidationException>(() =>
                CatalogValidator.Validate(unsupported, "strict startup"));

            Assert.Contains("no production handler", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Resource/Default", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void MassResurrectCapacityFailureDoesNotClaimCorpseCommitCooldownOrPublishActivation()
        {
            var config = GameConfigLoader.LoadStrictCatalog(Renderer);
            string path = WriteHeroBinding("Mass Resurrect");
            try
            {
                GameConfigLoader.ValidateStrictReferences(config, config.CompiledCatalog!, path);
                int player = Player(p => { p.X = 1f; p.Y = 1f; });
                Store.NecromancerQueueCorpse(-1, 2f, 1f, config.MonsterTypes[0].Type, 1f, 0f);
                var registry = CreateProduction(config, player);
                registry.Necromancer!.SetTurn(0, 0f);
                var hero = new HeroSkillSystem(Store, player, path, config);
                hero.SetPayloadHandler(registry.AbilityPayloads!);
                hero.Initialize();
                hero.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
                Store.HeroIsDeployed[0] = true;
                typeof(ComponentStore).GetField("nextEntityId", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(Store, ComponentStore.MAX_ENTITIES);
                Assert.Equal(0, Store.AvailableEntityCapacity);

                Assert.False(hero.TriggerHeroSkill(0, 0));

                Assert.Equal(AbilityActivationRejectReason.InvalidRequest, hero.LastActivation.Reason);
                Assert.True(Store.CorpseActive[0]);
                Assert.False(Store.CorpseReanimated[0]);
                Assert.Equal(-1, Store.CorpseOwnerId[0]);
                Assert.True(hero.IsHeroSkillReady(0, 0));
                Assert.DoesNotContain(Enumerable.Range(0, Store.DamageResolver.Events.Count),
                    i => Store.DamageResolver.Events.Get(i).Type == GameplayEventType.AbilityActivated);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void TimeRewindCapacityFailureDoesNotPartiallyRestoreOrCommitCooldown()
        {
            var config = GameConfigLoader.LoadStrictCatalog(Renderer);
            int rewindSkill = config.GetSkillIdByName("Time Rewind");
            int player = Player();
            var registry = CreateProduction(config, player);
            Store.PlayerCurrentHealth[player] = 80f;
            Store.PlayerMana[player] = 70f;
            Store.PlayerShield[player] = 60f;
            registry.TimeRewind!.AppendSnapshot(player);
            Store.PlayerCurrentHealth[player] = 10f;
            Store.PlayerMana[player] = 20f;
            Store.PlayerShield[player] = 30f;
            int tower = RawTower(0, 0, damage: 1f, range: 5);
            Store.SetTowerActiveSkill(tower, rewindSkill, 5f);
            registry.TowerActiveSkill!.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            Store.ResourceResolver.EnableDeferred(true);
            var handle = Store.GetEntityHandle(player);
            for (int i = 0; i < ResourceResolver.MaxPendingRequests - 2; i++)
                Assert.True(Store.ResourceResolver.TryApply(new ResourceRequest(handle, handle,
                    new AttributeKey(4), 1f, i + 1, ownerPlayerId: player)).Accepted);

            Assert.False(registry.TowerActiveSkill.TriggerTowerActive(tower));

            Assert.Equal(AbilityActivationRejectReason.InvalidRequest, registry.TowerActiveSkill.LastActivation.Reason);
            Assert.Equal(10f, Store.PlayerCurrentHealth[player]);
            Assert.Equal(20f, Store.PlayerMana[player]);
            Assert.Equal(30f, Store.PlayerShield[player]);
            Assert.Equal(0f, Store.TowerActiveCooldown[tower]);
            Assert.DoesNotContain(Enumerable.Range(0, Store.DamageResolver.Events.Count),
                i => Store.DamageResolver.Events.Get(i).Type == GameplayEventType.AbilityActivated);
        }

        private SystemRegistry CreateProduction(GameConfig config, int player)
        {
            var registry = new SystemRegistry();
            registry.CreateAll(Store, config, Renderer, player, new StateMachine());
            registry.WireDependencies(Store, player);
            Assert.NotNull(registry.AbilityPayloads);
            return registry;
        }

        private GameplayEvent FindLastEvent(GameplayEventType type)
        {
            for (int i = Store.DamageResolver.Events.Count - 1; i >= 0; i--)
            {
                var gameplayEvent = Store.DamageResolver.Events.Get(i);
                if (gameplayEvent.Type == type) return gameplayEvent;
            }
            throw new Xunit.Sdk.XunitException($"Missing gameplay event {type}");
        }

        private static string WriteHeroBinding(string skillName)
        {
            string path = Path.Combine(Path.GetTempPath(), "hero-payload-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, "{\"Skills\":[{\"SlotIndex\":0,\"SkillName\":\"" + skillName + "\"}]}");
            return path;
        }
    }
}
