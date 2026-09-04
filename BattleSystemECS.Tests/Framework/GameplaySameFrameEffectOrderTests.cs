using System;
using System.Collections.Generic;
using System.IO;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class GameplaySameFrameEffectOrderTests
    {
        [Fact]
        public void SameFrameApplyAndRemove_BothFactsStayInDigest()
        {
            using var store = new ComponentStore();
            GameplayObservation.EnableDigests(store);
            store.AddPlayer(0, 10f, 1f, 0f, 1);
            int source = store.AddEnemy(0f, 0f, 0f, 10f, 10f, 0f, 0, 1);
            int target = store.AddEnemy(1f, 0f, 0f, 10f, 10f, 0f, 0, 1);
            var definition = Duration(8101);
            Assert.True(store.GameplayEffectsRuntime.TryApply(definition.Id, definition,
                store.GetEntityHandle(source), store.GetEntityHandle(target), out var handle, ownerPlayerId: 0));
            ulong appliedOnly = store.GameplayEffectsRuntime.Events.SequenceDigest;
            Assert.Contains(GameplayEventType.EffectApplied, Types(store.GameplayEffectsRuntime.Events));

            Assert.True(store.GameplayEffectsRuntime.Remove(store.GetEntityHandle(target), handle));
            ulong appliedAndRemoved = store.GameplayEffectsRuntime.Events.SequenceDigest;

            Assert.NotEqual(appliedOnly, appliedAndRemoved);
            var types = Types(store.GameplayEffectsRuntime.Events);
            Assert.Contains(GameplayEventType.EffectApplied, types);
            Assert.Contains(GameplayEventType.EffectRemoved, types);
            GameplayObservationSnapshot observation = GameplayObservation.Capture(store);
            Assert.True(observation.GameplayEventPublishedCount >= 2);
        }

        [Fact]
        public void DispelThenApply_MatchesRemoveFirstThenEffectCommitOrder()
        {
            using var store = new ComponentStore();
            GameplayObservation.EnableDigests(store);
            store.AddPlayer(0, 10f, 1f, 0f, 1);
            int source = store.AddEnemy(0f, 0f, 0f, 10f, 10f, 0f, 0, 1);
            int target = store.AddEnemy(1f, 0f, 0f, 10f, 10f, 0f, 0, 1);
            var first = Duration(8201, CatalogRegistries.DispellableTag);
            var second = Duration(8202);
            Assert.True(store.GameplayEffectsRuntime.TryApply(first.Id, first,
                store.GetEntityHandle(source), store.GetEntityHandle(target), out var handle, ownerPlayerId: 0));

            // AI 组批外 remove-first。
            Assert.True(store.GameplayEffectsRuntime.Remove(store.GetEntityHandle(target), handle));
            store.DeferAbilityAndEffectCommit = true;
            Assert.True(store.GameplayEffectsRuntime.EnqueueApply(second.Id, second,
                store.GetEntityHandle(source), store.GetEntityHandle(target), 0, float.NaN));
            store.GameplayEffectsRuntime.CommitQueuedEffects();

            var ordered = new List<GameplayEventType>();
            for (int i = 0; i < store.GameplayEffectsRuntime.Events.Count; i++)
                ordered.Add(store.GameplayEffectsRuntime.Events.Get(i).Type);
            Assert.Equal(new[]
            {
                GameplayEventType.EffectApplied,
                GameplayEventType.EffectRemoved,
                GameplayEventType.EffectApplied
            }, ordered);
            Assert.Equal(1, store.GetEffectCount(target));
        }

        [Fact]
        public void ProductionGraphKeepsAiDispelBeforeEffectCommit()
        {
            string graph = File.ReadAllText(Path.Combine(
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
                "Core", "FrameSystemGraph.cs"));
            int ai = graph.IndexOf("\"ai.enemy-ability.execute\"", StringComparison.Ordinal);
            int commit = graph.IndexOf("\"effect.commit\"", StringComparison.Ordinal);
            Assert.True(ai >= 0);
            Assert.True(commit >= 0);
            Assert.True(ai < commit);
        }

        private static GameplayEffectDefinition Duration(int id, TagId granted = default)
        {
            TagId[] grantedTags = granted.Value == 0 ? Array.Empty<TagId>() : new[] { granted };
            return new GameplayEffectDefinition(
                new EffectId(id),
                EffectType.Duration,
                Array.Empty<ModifierDefinition>(),
                8f,
                0f,
                ClockId.Combat,
                StackingBehavior.None,
                1,
                RefreshPolicy.None,
                SourceDeathPolicy.Persist,
                EffectPayloadKind.Status,
                granted,
                Array.Empty<ExecutionId>(),
                grantedTags: grantedTags);
        }

        private static HashSet<GameplayEventType> Types(GameplayEventQueue queue)
        {
            var result = new HashSet<GameplayEventType>();
            for (int i = 0; i < queue.Count; i++) result.Add(queue.Get(i).Type);
            return result;
        }
    }
}
