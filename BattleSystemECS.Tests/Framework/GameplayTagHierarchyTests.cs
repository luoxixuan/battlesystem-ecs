using System;
using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class GameplayTagHierarchyTests
    {
        [Fact]
        public void VocabularyCompilesStunInsideControlInsideDebuff()
        {
            Assert.True(GameplayTagVocabulary.TryGetParent(CatalogRegistries.StunTag, out var control));
            Assert.Equal(CatalogRegistries.ControlTag, control);
            Assert.True(GameplayTagVocabulary.TryGetParent(CatalogRegistries.ControlTag, out var debuff));
            Assert.Equal(CatalogRegistries.DebuffTag, debuff);
            Assert.False(GameplayTagVocabulary.TryGetParent(CatalogRegistries.DebuffTag, out _));
            Assert.Empty(GameplayTagVocabulary.AncestorsOf(CatalogRegistries.TowerSilencedTag));

            var ancestors = GameplayTagVocabulary.AncestorsOf(CatalogRegistries.StunTag);
            Assert.Equal(2, ancestors.Count);
            Assert.Equal(CatalogRegistries.ControlTag, ancestors[0]);
            Assert.Equal(CatalogRegistries.DebuffTag, ancestors[1]);
            Assert.True(CatalogRegistries.TryTag("Stun", out var resolved));
            Assert.Equal(CatalogRegistries.StunTag, resolved);
        }

        [Fact]
        public void GrantingStunIncrementsControlAndDebuffCounts()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 10f, 1f, 1f, 1);
            int target = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            var grant = LeafGrant(20, CatalogRegistries.StunTag, CatalogRegistries.StunTag);

            Assert.True(store.GameplayEffectsRuntime.TryApply(grant.Id, grant,
                store.GetEntityHandle(0), store.GetEntityHandle(target), out _));

            Assert.True(GameplayTagRuntime.HasTag(store, target, CatalogRegistries.StunTag));
            Assert.True(GameplayTagRuntime.HasTag(store, target, CatalogRegistries.ControlTag));
            Assert.True(GameplayTagRuntime.HasTag(store, target, CatalogRegistries.DebuffTag));
            Assert.Equal(1, store.TagState.GetCount(target, CatalogRegistries.StunTag));
            Assert.Equal(1, store.TagState.GetCount(target, CatalogRegistries.ControlTag));
            Assert.Equal(1, store.TagState.GetCount(target, CatalogRegistries.DebuffTag));
            Assert.False(GameplayTagRuntime.HasTag(store, target, CatalogRegistries.TowerSilencedTag));
        }

        [Fact]
        public void RemovingOneStunSourceKeepsAncestorCountsFromTheOther()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 10f, 1f, 1f, 1);
            int target = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            var first = LeafGrant(21, CatalogRegistries.StunTag, new TagId(21));
            var second = LeafGrant(22, CatalogRegistries.StunTag, new TagId(22));
            Assert.True(store.GameplayEffectsRuntime.TryApply(first.Id, first,
                store.GetEntityHandle(0), store.GetEntityHandle(target), out var firstHandle));
            Assert.True(store.GameplayEffectsRuntime.TryApply(second.Id, second,
                store.GetEntityHandle(0), store.GetEntityHandle(target), out var secondHandle));
            Assert.Equal(2, store.TagState.GetCount(target, CatalogRegistries.StunTag));
            Assert.Equal(2, store.TagState.GetCount(target, CatalogRegistries.ControlTag));
            Assert.Equal(2, store.TagState.GetCount(target, CatalogRegistries.DebuffTag));

            Assert.True(store.GameplayEffectsRuntime.Remove(store.GetEntityHandle(target), firstHandle));
            Assert.True(GameplayTagRuntime.HasTag(store, target, CatalogRegistries.StunTag));
            Assert.True(GameplayTagRuntime.HasTag(store, target, CatalogRegistries.ControlTag));
            Assert.Equal(1, store.TagState.GetCount(target, CatalogRegistries.StunTag));
            Assert.Equal(1, store.TagState.GetCount(target, CatalogRegistries.ControlTag));
            Assert.Equal(1, store.TagState.GetCount(target, CatalogRegistries.DebuffTag));

            Assert.True(store.GameplayEffectsRuntime.Remove(store.GetEntityHandle(target), secondHandle));
            Assert.False(GameplayTagRuntime.HasTag(store, target, CatalogRegistries.StunTag));
            Assert.False(GameplayTagRuntime.HasTag(store, target, CatalogRegistries.ControlTag));
            Assert.False(GameplayTagRuntime.HasTag(store, target, CatalogRegistries.DebuffTag));
            Assert.Equal(0, store.TagState.GetCount(target, CatalogRegistries.ControlTag));
        }

        [Fact]
        public void AncestorMatchLeavesStackKeyAndEffectAppliedTagAsLeaf()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 10f, 1f, 1f, 1);
            int target = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            var stun = LeafGrant(23, CatalogRegistries.StunTag, CatalogRegistries.StunTag);
            var control = LeafGrant(24, CatalogRegistries.ControlTag, CatalogRegistries.ControlTag);
            Assert.True(store.GameplayEffectsRuntime.TryApply(stun.Id, stun,
                store.GetEntityHandle(0), store.GetEntityHandle(target), out _));
            Assert.True(store.GameplayEffectsRuntime.TryApply(control.Id, control,
                store.GetEntityHandle(0), store.GetEntityHandle(target), out _));

            Assert.True(GameplayTagRuntime.HasTag(store, target, CatalogRegistries.DebuffTag));
            Assert.Equal(2, store.GetEffectCount(target));
            bool sawStunLeaf = false, sawControlLeaf = false;
            for (int slot = 0; slot < store.GetEffectCount(target); slot++)
            {
                Assert.True(store.TryGetActiveEffectAt(target, slot, out _, out var definition, out _));
                Assert.False(definition.Tag.Equals(CatalogRegistries.DebuffTag));
                Assert.False(definition.StackKey.Equals(CatalogRegistries.DebuffTag));
                if (definition.Tag.Equals(CatalogRegistries.StunTag) && definition.StackKey.Equals(CatalogRegistries.StunTag))
                    sawStunLeaf = true;
                if (definition.Tag.Equals(CatalogRegistries.ControlTag) && definition.StackKey.Equals(CatalogRegistries.ControlTag))
                    sawControlLeaf = true;
            }
            Assert.True(sawStunLeaf);
            Assert.True(sawControlLeaf);

            int applied = 0;
            var events = store.GameplayEffectsRuntime.Events;
            for (int i = 0; i < events.Count; i++)
            {
                var gameplayEvent = events.Get(i);
                if (gameplayEvent.Type != GameplayEventType.EffectApplied) continue;
                applied++;
                Assert.False(gameplayEvent.Tag.Equals(CatalogRegistries.DebuffTag));
                Assert.True(gameplayEvent.Tag.Equals(CatalogRegistries.StunTag) ||
                            gameplayEvent.Tag.Equals(CatalogRegistries.ControlTag));
            }
            Assert.Equal(2, applied);
        }

        [Fact]
        public void RequiredControlTagIsSatisfiedByGrantedStun()
        {
            var store = WaveStore();
            store.AddPlayer(0, 10f, 1f, 1f, 1);
            store.PlayerMaxMana[0] = 10f;
            var targeting = new TargetingDefinition(new TargetingId(0), TargetingShape.Shield, 0, 1, 1, 1,
                relation: RelationFilter.Self, maxTargetsMode: MaxTargetsPolicy.Fixed);
            var execution = new ExecutionDefinition(new ExecutionId(0), EffectPayloadKind.Shield, 2f, new TagId(0),
                operation: ExecutionOperation.ApplyShield);
            var ability = new AbilityDefinition(new AbilityId(0), "needs-control", targeting, ClockId.Combat, 2f,
                GameplayPhaseMask.Wave, Array.Empty<EffectId>(), Array.Empty<ModifierDefinition>(),
                CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer,
                executions: new[] { execution.Id }, requiredTags: new[] { CatalogRegistries.ControlTag });
            var catalog = new GameplayCatalog(new[] { ability }, new[] { targeting }, Array.Empty<GameplayEffectDefinition>(),
                new[] { execution }, Array.Empty<TriggerDefinition>(), Array.Empty<ModifierDefinition>(),
                new Dictionary<string, AbilityId>());
            var timers = new float[1];
            var missing = GameplayAbilityRuntime.Activate(store, catalog, timers, new AbilityActivationRequest(0, 0, 0f, 0, ability.Id));
            Assert.Equal(AbilityActivationRejectReason.TagRequirementsNotMet, missing.Reason);

            var grant = LeafGrant(25, CatalogRegistries.StunTag, CatalogRegistries.StunTag);
            Assert.True(store.GameplayEffectsRuntime.TryApply(grant.Id, grant,
                store.GetEntityHandle(0), store.GetEntityHandle(0), out _));
            var accepted = GameplayAbilityRuntime.Activate(store, catalog, timers, new AbilityActivationRequest(0, 0, 0f, 0, ability.Id));
            Assert.True(accepted.Accepted, accepted.Reason.ToString());
        }

        private static ComponentStore WaveStore()
        {
            var store = new ComponentStore();
            store.GameplayPhaseContext = new PhaseContext(PhaseContextKind.Wave);
            return store;
        }

        private static GameplayEffectDefinition LeafGrant(int id, TagId leaf, TagId stackKey) =>
            new GameplayEffectDefinition(new EffectId(id), EffectType.Duration,
                Array.Empty<ModifierDefinition>(), 5f, 0f, ClockId.Combat, StackingBehavior.None, 1,
                RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent,
                leaf, Array.Empty<ExecutionId>(), grantedTags: new[] { leaf }, stackKey: stackKey);
    }
}
