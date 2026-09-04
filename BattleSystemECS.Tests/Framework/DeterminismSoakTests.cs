using System;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Systems;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class DeterminismSoakTests
    {
        [Fact]
        public void FixedSeedFissionSoak_MatchesStateAndEventSequenceDigest()
        {
            GameplayObservationSnapshot first = RunFissionSoak(11);
            GameplayObservationSnapshot second = RunFissionSoak(11);
            GameplayObservationSnapshot otherSeed = RunFissionSoak(12);

            Assert.Equal(first.StateDigest, second.StateDigest);
            Assert.Equal(first.GameplayEventSequenceDigest, second.GameplayEventSequenceDigest);
            Assert.Equal(first.GameplayEventPublishedCount, second.GameplayEventPublishedCount);
            Assert.Equal(2, first.ActiveEnemies);
            Assert.NotEqual(first.StateDigest, otherSeed.StateDigest);
        }

        [Fact]
        public void CommitSerialGuard_RejectsDrawOutsideSerialOwnerThread()
        {
            var context = new DeterminismContext(3);
            context.BeginStrictFrame();
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => context.NextDouble());
            Assert.Contains("CommitSerial", error.Message, StringComparison.Ordinal);
            context.EnterCommitSerial();
            double value = context.NextDouble();
            Assert.InRange(value, 0.0, 1.0);
            context.ExitCommitSerial();
            Assert.Throws<InvalidOperationException>(() => context.Next());
            context.EndStrictFrame();
            Assert.InRange(context.NextDouble(), 0.0, 1.0);
        }

        [Fact]
        public void SameSeedStreams_AreIdentical()
        {
            var left = new DeterminismContext(21);
            var right = new DeterminismContext(21);
            for (int i = 0; i < 64; i++)
                Assert.Equal(left.Next(1000), right.Next(1000));
        }

        private static GameplayObservationSnapshot RunFissionSoak(int seed)
        {
            using var store = new ComponentStore();
            store.Determinism.Reset(seed);
            GameplayObservation.EnableDigests(store);
            store.AddPlayer(0, 10f, 1f, 0f, 1);
            int parent = store.AddEnemy(4f, 4f, 1f, 20f, 20f, 2f, 3, 1);
            store.EnemyFissionDefId[parent] = 0;
            store.EnemyFissionGeneration[parent] = 0;
            var definition = new GameplayEffectDefinition(
                new EffectId(9100 + seed),
                EffectType.Duration,
                Array.Empty<ModifierDefinition>(),
                4f,
                0f,
                ClockId.Combat,
                StackingBehavior.None,
                1,
                RefreshPolicy.None,
                SourceDeathPolicy.Persist,
                EffectPayloadKind.Status,
                default(TagId),
                Array.Empty<ExecutionId>());
            Assert.True(store.GameplayEffectsRuntime.TryApply(definition.Id, definition,
                store.GetEntityHandle(0), store.GetEntityHandle(parent), out _, ownerPlayerId: 0));

            var config = new GameConfig
            {
                FissionDefs = new[]
                {
                    new FissionDef
                    {
                        ChildrenCount = 2,
                        MaxGeneration = 2,
                        HealthScale = 1f,
                        DamageScale = 1f,
                        SpeedScale = 1f,
                        GoldScale = 1f,
                        ChildMonsterType = "Slime"
                    }
                }
            };
            var fission = new EnemyFissionSystem(store, config, new MockRenderer());
            store.QueueEnemyDeath(parent, 0);
            store.ResolveEnemiesKilledThisFrame();
            fission.Update();
            return GameplayObservation.Capture(store);
        }
    }
}
