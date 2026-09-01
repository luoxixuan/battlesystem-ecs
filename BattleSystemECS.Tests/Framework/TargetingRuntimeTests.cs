using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class TargetingRuntimeTests
    {
        [Theory]
        [InlineData(TargetingShape.Cross, 2f, 0f, 2f, 1f)]
        [InlineData(TargetingShape.Circle, 2f, 0f, 4f, 0f)]
        [InlineData(TargetingShape.Box, 1f, 0.5f, 2f, 0f)]
        [InlineData(TargetingShape.Line, 2f, 0f, 2f, 1f)]
        [InlineData(TargetingShape.Cone, 0f, -2f, 0f, 2f)]
        [InlineData(TargetingShape.GroundTarget, 2f, 0f, 4f, 0f)]
        public void CompiledShapeSelectsInsideAndRejectsOutside(TargetingShape shape,
            float insideX, float insideY, float outsideX, float outsideY)
        {
            var store = Store();
            int inside = Enemy(store, insideX, insideY, 100f);
            int outside = Enemy(store, outsideX, outsideY, 100f);
            var definition = new TargetingDefinition(new TargetingId(0), shape, 3, 3, 3, 0,
                radius: 3f, angle: 60f, relation: RelationFilter.Enemies,
                maxTargetsMode: MaxTargetsPolicy.Unlimited);
            var targets = new List<int>();
            var scales = new List<float>();

            Assert.True(TargetingRuntime.TryCollectEnemyTargets(store, 0, definition, targets, scales));
            Assert.Equal(new[] { inside }, targets);
            Assert.DoesNotContain(outside, targets);
            Assert.Equal(new[] { 1f }, scales);
        }

        [Fact]
        public void FixedLimitAndBufferReuseRemainDeterministicAndDeduplicated()
        {
            var store = Store();
            int first = Enemy(store, 1f, 0f, 100f);
            Enemy(store, 2f, 0f, 100f);
            var definition = new TargetingDefinition(new TargetingId(0), TargetingShape.Circle,
                5, 5, 5, 1, radius: 5f, relation: RelationFilter.Enemies,
                maxTargetsMode: MaxTargetsPolicy.Fixed);
            var targets = new List<int> { 99, 99 };
            var scales = new List<float> { 7f };

            Assert.True(TargetingRuntime.TryCollectEnemyTargets(store, 0, definition, targets, scales));
            Assert.Equal(new[] { first }, targets);
            Assert.Single(scales);
            Assert.True(TargetingRuntime.TryCollectEnemyTargets(store, 0, definition, targets, scales));
            Assert.Equal(new[] { first }, targets);
        }

        [Fact]
        public void UnsupportedTargetTagFilterRejectsWithoutReturningPartialTargets()
        {
            var store = Store();
            Enemy(store, 1f, 0f, 100f);
            var definition = new TargetingDefinition(new TargetingId(0), TargetingShape.Circle,
                3, 3, 3, 0, radius: 3f, requiredTags: new[] { new TagId(0) },
                relation: RelationFilter.Enemies, maxTargetsMode: MaxTargetsPolicy.Unlimited);
            var targets = new List<int> { 77 };
            var scales = new List<float> { 1f };

            Assert.False(TargetingRuntime.TryCollectEnemyTargets(store, 0, definition, targets, scales));
            Assert.Empty(targets);
            Assert.Empty(scales);
        }

        [Fact]
        public void ChainUsesNearestUniqueTargetsDecayAndKillsThroughTypedRuntime()
        {
            var store = Store();
            int first = Enemy(store, 1f, 0f, 60f);
            int second = Enemy(store, 2f, 0f, 60f);
            int third = Enemy(store, 3f, 0f, 40f);
            int outside = Enemy(store, 20f, 0f, 100f);
            var targeting = new TargetingDefinition(new TargetingId(0), TargetingShape.Chain,
                2, 1, 1, 3, radius: 2f, relation: RelationFilter.Enemies,
                maxTargetsMode: MaxTargetsPolicy.Fixed);
            var targets = new List<int>();
            var scales = new List<float>();
            Assert.True(TargetingRuntime.TryCollectEnemyTargets(store, 0, targeting, targets, scales));
            Assert.Equal(new[] { first, second, third }, targets);
            Assert.Equal(1f, scales[0], 3);
            Assert.Equal(0.7f, scales[1], 3);
            Assert.Equal(0.49f, scales[2], 3);

            var execution = new ExecutionDefinition(new ExecutionId(0), EffectPayloadKind.Damage, 100f,
                CatalogRegistries.SkillTag, operation: ExecutionOperation.ApplyDamage);
            var ability = new AbilityDefinition(new AbilityId(0), "chain", targeting, ClockId.Combat, 2f,
                GameplayPhaseMask.Wave, Array.Empty<EffectId>(), Array.Empty<ModifierDefinition>(),
                CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer,
                executions: new[] { execution.Id });
            var catalog = new GameplayCatalog(new[] { ability }, new[] { targeting },
                Array.Empty<GameplayEffectDefinition>(), new[] { execution }, Array.Empty<TriggerDefinition>(),
                Array.Empty<ModifierDefinition>(), new Dictionary<string, AbilityId> { ["chain"] = ability.Id });
            var result = GameplayAbilityRuntime.ActivateTargets(store, catalog, new float[1],
                new AbilityActivationRequest(0, 0, 2f, 0, ability.Id, ownerPlayerId: 0), targets, scales);
            Assert.True(result.Accepted, result.Reason.ToString());
            Assert.Equal(0f, store.EnemyHealth[first]);
            Assert.Equal(0f, store.EnemyHealth[second]);
            Assert.Equal(0f, store.EnemyHealth[third]);
            Assert.Equal(100f, store.EnemyHealth[outside]);
            store.ResolveEnemiesKilledThisFrame();
            Assert.DoesNotContain(first, store.ActiveEnemyIds);
            Assert.DoesNotContain(second, store.ActiveEnemyIds);
            Assert.DoesNotContain(third, store.ActiveEnemyIds);
        }

        [Fact]
        public void OversizedDamageBatchRejectsBeforeAnyDeferredRequestOrCooldown()
        {
            var store = Store();
            var targeting = new TargetingDefinition(new TargetingId(0), TargetingShape.Circle,
                10000, 1, 1, 0, radius: 10000f, relation: RelationFilter.Enemies,
                maxTargetsMode: MaxTargetsPolicy.Unlimited);
            var targets = new List<int>(DamageResolver.MaxPendingRequests + 1);
            var scales = new List<float>(DamageResolver.MaxPendingRequests + 1);
            for (int i = 0; i < DamageResolver.MaxPendingRequests + 1; i++)
            {
                targets.Add(Enemy(store, i + 1, 0f, 10f));
                scales.Add(1f);
            }
            var execution = new ExecutionDefinition(new ExecutionId(0), EffectPayloadKind.Damage, 1f,
                CatalogRegistries.SkillTag, operation: ExecutionOperation.ApplyDamage);
            var catalog = Catalog(targeting, execution);
            var timers = new float[1];
            store.DamageResolver.EnableDeferred(true);

            var result = GameplayAbilityRuntime.ActivateTargets(store, catalog, timers,
                new AbilityActivationRequest(0, 0, 1f, 0, new AbilityId(0), ownerPlayerId: 0),
                targets, scales);

            Assert.False(result.Accepted);
            Assert.Equal(0, store.DamageResolver.PendingRequestCount);
            Assert.Equal(0f, timers[0]);
            Assert.All(targets, target => Assert.Equal(10f, store.EnemyHealth[target]));
        }

        [Fact]
        public void MixedDamageHealBatchRejectsBeforeEitherResolverReceivesPartialWork()
        {
            var store = Store();
            var targets = new List<int>();
            var scales = new List<float>();
            for (int i = 0; i < 10; i++) { targets.Add(Enemy(store, i + 1, 0f, 10f)); scales.Add(1f); }
            store.DamageResolver.EnableDeferred(true);
            store.ResourceResolver.EnableDeferred(true);
            var player = store.GetEntityHandle(0);
            int prefilled = ResourceResolver.MaxPendingRequests - 3;
            for (int i = 0; i < prefilled; i++)
                Assert.True(store.ResourceResolver.TryApply(new ResourceRequest(player, player,
                    new AttributeKey(7), 1f, i + 1, ownerPlayerId: 0)).Accepted);
            var targeting = new TargetingDefinition(new TargetingId(0), TargetingShape.Circle,
                20, 1, 1, 0, radius: 20f, relation: RelationFilter.Enemies,
                maxTargetsMode: MaxTargetsPolicy.Unlimited);
            var executions = new[]
            {
                new ExecutionDefinition(new ExecutionId(0), EffectPayloadKind.Damage, 1f,
                    CatalogRegistries.SkillTag, operation: ExecutionOperation.ApplyDamage),
                new ExecutionDefinition(new ExecutionId(1), EffectPayloadKind.Heal, 1f,
                    CatalogRegistries.SkillTag, operation: ExecutionOperation.ApplyHeal)
            };
            var catalog = Catalog(targeting, executions);
            var timers = new float[1];

            var result = GameplayAbilityRuntime.ActivateTargets(store, catalog, timers,
                new AbilityActivationRequest(0, 0, 1f, 0, new AbilityId(0), ownerPlayerId: 0),
                targets, scales);

            Assert.False(result.Accepted);
            Assert.Equal(0, store.DamageResolver.PendingRequestCount);
            Assert.Equal(prefilled, store.ResourceResolver.PendingRequestCount);
            Assert.Equal(0f, timers[0]);
            Assert.All(targets, target => Assert.Equal(10f, store.EnemyHealth[target]));
        }

        [Fact]
        public void ChainHealSelectsFourInjuredAlliesAndDecaysWithoutPartialCommitOnCapacityFailure()
        {
            var store = Store();
            int[] allies = new int[4];
            for (int i = 0; i < allies.Length; i++)
            {
                int player = i + 1;
                store.AddPlayer(player, 10f, 1f, 1f, 1);
                store.PlayerMaxHealth[player] = 100f;
                store.PlayerCurrentHealth[player] = 10f + i * 10f;
                store.PositionX[player] = i + 1;
                store.PositionY[player] = 0f;
                allies[i] = player;
            }
            var targeting = new TargetingDefinition(new TargetingId(0), TargetingShape.ChainHeal,
                10, 1, 1, 4, radius: 10f, relation: RelationFilter.Allies,
                maxTargetsMode: MaxTargetsPolicy.Fixed);
            var targets = new List<int>();
            var scales = new List<float>();
            Assert.True(TargetingRuntime.TryCollectAllyTargets(store, 0, targeting, targets, scales));
            Assert.Equal(allies, targets);
            Assert.Equal(new[] { 1f, 0.5f, 0.25f, 0.125f }, scales);
            var heal = new ExecutionDefinition(new ExecutionId(0), EffectPayloadKind.Heal, 40f,
                CatalogRegistries.SkillTag, operation: ExecutionOperation.ApplyHeal);
            var catalog = Catalog(targeting, heal);
            var timers = new float[1];
            var accepted = GameplayAbilityRuntime.ActivateTargets(store, catalog, timers,
                new AbilityActivationRequest(0, 0, 1f, 0, new AbilityId(0), ownerPlayerId: 0), targets, scales);
            Assert.True(accepted.Accepted, accepted.Reason.ToString());
            Assert.Equal(50f, store.PlayerCurrentHealth[allies[0]]);
            Assert.Equal(40f, store.PlayerCurrentHealth[allies[1]]);
            Assert.Equal(40f, store.PlayerCurrentHealth[allies[2]]);
            Assert.Equal(45f, store.PlayerCurrentHealth[allies[3]]);

            var blocked = Store();
            for (int i = 0; i < allies.Length; i++)
            {
                blocked.AddPlayer(i + 1, 10f, 1f, 1f, 1);
                blocked.PlayerMaxHealth[i + 1] = 100f;
                blocked.PlayerCurrentHealth[i + 1] = 10f + i * 10f;
                blocked.PositionX[i + 1] = i + 1;
            }
            blocked.ResourceResolver.EnableDeferred(true);
            var source = blocked.GetEntityHandle(0);
            int prefilled = ResourceResolver.MaxPendingRequests - 3;
            for (int i = 0; i < prefilled; i++)
                Assert.True(blocked.ResourceResolver.TryApply(new ResourceRequest(source, source,
                    new AttributeKey(7), 1f, i + 1, ownerPlayerId: 0)).Accepted);
            Assert.True(TargetingRuntime.TryCollectAllyTargets(blocked, 0, targeting, targets, scales));
            var blockedTimers = new float[1];
            var rejected = GameplayAbilityRuntime.ActivateTargets(blocked, catalog, blockedTimers,
                new AbilityActivationRequest(0, 0, 1f, 0, new AbilityId(0), ownerPlayerId: 0), targets, scales);
            Assert.False(rejected.Accepted);
            Assert.Equal(prefilled, blocked.ResourceResolver.PendingRequestCount);
            Assert.Equal(0f, blockedTimers[0]);
            for (int i = 0; i < allies.Length; i++)
                Assert.Equal(10f + i * 10f, blocked.PlayerCurrentHealth[i + 1]);
        }

        private static ComponentStore Store()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 100f, 1f, 1f, 1);
            store.PositionX[0] = 0f;
            store.PositionY[0] = 0f;
            return store;
        }

        private static int Enemy(ComponentStore store, float x, float y, float health) =>
            store.AddEnemy(x, y, 0f, health, health, 0f, 0, 1);

        private static GameplayCatalog Catalog(TargetingDefinition targeting,
            params ExecutionDefinition[] executions)
        {
            var ids = new ExecutionId[executions.Length];
            for (int i = 0; i < executions.Length; i++) ids[i] = executions[i].Id;
            var ability = new AbilityDefinition(new AbilityId(0), "batch", targeting, ClockId.Combat, 1f,
                GameplayPhaseMask.Wave, Array.Empty<EffectId>(), Array.Empty<ModifierDefinition>(),
                CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer, executions: ids);
            return new GameplayCatalog(new[] { ability }, new[] { targeting },
                Array.Empty<GameplayEffectDefinition>(), executions, Array.Empty<TriggerDefinition>(),
                Array.Empty<ModifierDefinition>(), new Dictionary<string, AbilityId> { ["batch"] = ability.Id });
        }
    }
}
