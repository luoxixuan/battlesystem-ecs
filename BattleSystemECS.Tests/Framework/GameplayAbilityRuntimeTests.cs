using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using Xunit;
using System;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class GameplayAbilityRuntimeTests
    {
        [Fact]
        public void TryActivateDoesNotMutateUntilAbilityCommit()
        {
            var store = new ComponentStore();
            int player = 0;
            store.AddPlayer(player, 10f, 1f, 1f, 1);
            var def = new GameplayAbilityDef("runtime", "", 3f, 0f, -1, 1f, AbilityActivation.Instant, AreaShapeType.Single, 1);
            Assert.True(store.TryAddAbility(player, def));

            Assert.True(GameplayAbilityRuntime.TryActivate(store, player, 0, out var pending));
            Assert.Equal(0f, pending.CurrentCooldown);
            Assert.Equal(0f, store.GetAbility(player, 0).CurrentCooldown);
            Assert.True(GameplayAbilityRuntime.AbilityCommit(store, player, 0));
            Assert.Equal(def.Cooldown, store.GetAbility(player, 0).CurrentCooldown);
        }

        [Fact]
        public void AbilityCommitRejectsCooldownSlot()
        {
            var store = new ComponentStore();
            int player = 0;
            store.AddPlayer(player, 10f, 1f, 1f, 1);
            var def = new GameplayAbilityDef("runtime", "", 3f, 0f, -1, 1f, AbilityActivation.Instant, AreaShapeType.Single, 1);
            Assert.True(store.TryAddAbility(player, def));
            Assert.True(GameplayAbilityRuntime.AbilityCommit(store, player, 0));
            Assert.False(GameplayAbilityRuntime.AbilityCommit(store, player, 0));
            Assert.Equal(def.Cooldown, store.GetAbility(player, 0).CurrentCooldown);
        }

        [Fact]
        public void CatalogActivationCommitsDamageAndPublishesAbilityEvent()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 20f, 1f, 1f, 1);
            int enemy = store.AddEnemy(0, 0, 1, 10f, 10f, 1, 1, 1);
            var targeting = new TargetingDefinition(new TargetingId(0), TargetingShape.Single, 10, 1, 1, 1);
            var execution = new ExecutionDefinition(new ExecutionId(0), EffectPayloadKind.Damage, 3f, new TagId(0));
            var ability = new AbilityDefinition(new AbilityId(0), "typed", targeting, ClockId.Combat, 2f,
                GameplayPhaseMask.Wave, Array.Empty<EffectId>(), Array.Empty<ModifierDefinition>(),
                CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer, executions: new[] { execution.Id });
            var catalog = new GameplayCatalog(new[] { ability }, new[] { targeting }, Array.Empty<GameplayEffectDefinition>(),
                new[] { execution }, Array.Empty<TriggerDefinition>(), Array.Empty<ModifierDefinition>(),
                new System.Collections.Generic.Dictionary<string, AbilityId> { ["typed"] = ability.Id });
            var timers = new float[1];
            var result = GameplayAbilityRuntime.Activate(store, catalog, timers,
                new AbilityActivationRequest(0, 0, 0f, enemy, ability: ability.Id));
            Assert.True(result.Accepted);
            Assert.Equal(1, result.AppliedEffects);
            Assert.Equal(2f, timers[0]);
            bool published = false;
            for (int i = 0; i < store.DamageResolver.Events.Count; i++)
                if (store.DamageResolver.Events.Get(i).Type == GameplayEventType.AbilityActivated) published = true;
            Assert.True(published);
        }

        [Fact]
        public void CatalogActivationRejectsUnmappedEffectReference()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 10f, 1f, 1f, 1);
            var targeting = new TargetingDefinition(new TargetingId(0), TargetingShape.Single, 1, 1, 1, 1);
            var ability = new AbilityDefinition(new AbilityId(0), "typed", targeting, ClockId.Combat, 1f,
                GameplayPhaseMask.Wave, new[] { new EffectId(4) }, Array.Empty<ModifierDefinition>(),
                CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer);
            var catalog = new GameplayCatalog(new[] { ability }, new[] { targeting }, Array.Empty<GameplayEffectDefinition>(),
                Array.Empty<ExecutionDefinition>(), Array.Empty<TriggerDefinition>(), Array.Empty<ModifierDefinition>(),
                new System.Collections.Generic.Dictionary<string, AbilityId>());
            var result = GameplayAbilityRuntime.Activate(store, catalog, new float[1],
                new AbilityActivationRequest(0, 0, 1f, 0, ability: ability.Id));
            Assert.False(result.Accepted);
            Assert.Equal(AbilityActivationRejectReason.InvalidRequest, result.Reason);
        }
    }
}
