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
            AssertStrictReject(config => config.GlobalSkills.Add(new GlobalSkillDef { Name = "missing-global" }), "GlobalSkills[0]");
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
        public void HeroEntriesReachResourceDamageDeathAndPresentationThroughProductionTick()
        {
            var config = GameConfigLoader.LoadStrictCatalog(Renderer);
            int player = Player(p => { p.Health = 100f; p.X = 0f; p.Y = 0f; p.AttackDamage = 1f; });
            Store.PlayerCurrentHealth[player] = 40f;
            int enemy = Enemy(e => { e.X = 0f; e.Y = 1f; e.Health = 10f; e.MaxHealth = 10f; e.Damage = 0f; e.MoveSpeed = 0f; e.GoldReward = 7; });
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
            Assert.Equal(new[] { "killed", "destroyed" }, events.KillEvents);
            Assert.True(config.CompiledCatalog!.TryResolveAlias("Guardian Heal", out var heal));
            Assert.True(config.CompiledCatalog.TryResolveAlias("Cross Slash", out var damage));
            Assert.NotEqual(heal, damage);
        }

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
