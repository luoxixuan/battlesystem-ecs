using System;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class GameplayEffectInhibitTests
    {
        [Fact]
        public void InhibitStripsModifierAndTagThenUninhibitRestoresLedger()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 1f, 1f, 1f, 1);
            int target = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            var key = new AttributeKey(8);
            store.AttributeAggregator.SetBase(target, key, 1f);
            var granted = new TagId(3);
            var def = new GameplayEffectDefinition(new EffectId(70), EffectType.Duration,
                new[] { new ModifierDefinition(key, AttributeModifierOp.Add, 0.30f) },
                8f, 0f, ClockId.Combat, StackingBehavior.None, 1, RefreshPolicy.None,
                SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent, granted,
                Array.Empty<ExecutionId>(), grantedTags: new[] { granted });
            Assert.True(store.GameplayEffectsRuntime.TryApply(def.Id, def,
                store.GetEntityHandle(0), store.GetEntityHandle(target), out var handle, ownerPlayerId: 0));
            store.AttributeAggregator.AggregateDirty();
            Assert.Equal(1.30f, store.AttributeAggregator.GetComputed(target, key, 1f), 3);
            Assert.True(GameplayTagRuntime.HasTag(store, target, granted));
            Assert.Equal(1, GameplayTagRuntime.GetCount(store, target, granted));

            Assert.True(store.GameplayEffectsRuntime.TryInhibit(store.GetEntityHandle(target), handle));
            Assert.Equal(1, store.GetEffectCount(target));
            Assert.True(store.TryGetActiveEffectAt(target, 0, out var inhibited, out _, out _));
            Assert.True(inhibited.Inhibited);
            Assert.Equal(1.00f, store.AttributeAggregator.GetComputed(target, key, 1f), 3);
            Assert.False(GameplayTagRuntime.HasTag(store, target, granted));
            Assert.Equal(0, GameplayTagRuntime.GetCount(store, target, granted));

            Assert.True(store.GameplayEffectsRuntime.TryUninhibit(store.GetEntityHandle(target), handle));
            Assert.True(store.TryGetActiveEffectAt(target, 0, out var restored, out _, out _));
            Assert.False(restored.Inhibited);
            Assert.Equal(1.30f, store.AttributeAggregator.GetComputed(target, key, 1f), 3);
            Assert.True(GameplayTagRuntime.HasTag(store, target, granted));
            Assert.Equal(1, GameplayTagRuntime.GetCount(store, target, granted));
        }
    }
}
