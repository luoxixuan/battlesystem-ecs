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

        [Fact]
        public void ProductionGraphFixedSeedSoak_MatchesStateAndEventSequenceDigest()
        {
            GameplayObservationSnapshot first = RunProductionGraphSoak(11);
            GameplayObservationSnapshot second = RunProductionGraphSoak(11);
            GameplayObservationSnapshot otherSeed = RunProductionGraphSoak(12);

            Assert.Equal(first.StateDigest, second.StateDigest);
            Assert.Equal(first.GameplayEventSequenceDigest, second.GameplayEventSequenceDigest);
            Assert.Equal(first.GameplayEventPublishedCount, second.GameplayEventPublishedCount);
            Assert.True(first.ActiveEnemies >= 2);
            Assert.NotEqual(first.StateDigest, otherSeed.StateDigest);
        }

        private static GameplayObservationSnapshot RunProductionGraphSoak(int seed)
        {
            using var store = new ComponentStore();
            store.Determinism.Reset(seed);
            GameplayObservation.EnableDigests(store);
            const int playerId = 0;
            store.AddPlayer(playerId, 10f, 1f, 10f, 1);
            var logger = new MockRenderer();
            var config = GameConfigLoader.LoadConfigStrict(logger);
            if (config.Levels != null)
            {
                for (int i = 0; i < config.Levels.Count; i++)
                {
                    config.Levels[i].Waves?.Clear();
                    config.Levels[i].DoomClockInitialWaves?.Clear();
                }
            }

            var stateMachine = new StateMachine();
            var scheduler = new FrameScheduler(store, config);
            var registry = new SystemRegistry();
            new ProductionSystemInstaller().Install(registry, store, config, logger, playerId, stateMachine, scheduler);
            scheduler.BindStateMachine(stateMachine);
            scheduler.Phase = GameState.WavePhase;

            string childType = config.MonsterTypes != null && config.MonsterTypes.Count > 0
                ? config.MonsterTypes[0].Type : "Normal";
            config.FissionDefs = new[]
            {
                new FissionDef
                {
                    ChildrenCount = 2,
                    MaxGeneration = 1,
                    HealthScale = 100f,
                    DamageScale = 1f,
                    SpeedScale = 1f,
                    GoldScale = 1f,
                    ChildMonsterType = childType
                }
            };

            int parent = store.AddEnemy(4f, 4f, 0f, 10000f, 10000f, 2f, 3, 1);
            store.EnemyFissionDefId[parent] = 0;
            store.EnemyFissionGeneration[parent] = 0;
            store.PlayerAttackDamage[playerId] = 0f;
            var spec = new PeriodicSpec(1f, FirstTickPolicy.Immediate, CatchUpPolicy.CatchUpAll,
                default(ExecutionId), DamageType.True, ElementType.None, 10000f);
            var definition = new GameplayEffectDefinition(
                new EffectId(9200 + seed),
                EffectType.Periodic,
                Array.Empty<ModifierDefinition>(),
                2f,
                ClockId.Combat,
                StackingBehavior.None,
                1,
                RefreshPolicy.None,
                SourceDeathPolicy.Persist,
                EffectPayloadKind.Damage,
                default(TagId),
                spec,
                Array.Empty<ExecutionId>());
            Assert.True(store.GameplayEffectsRuntime.TryApply(definition.Id, definition,
                store.GetEntityHandle(0), store.GetEntityHandle(parent), out _, ownerPlayerId: 0));

            const int frames = 200;
            for (int i = 0; i < frames; i++)
            {
                scheduler.Tick(0.1f, i);
                var ids = store.ActiveEnemyIds;
                for (int n = 0; n < ids.Count; n++)
                {
                    int id = ids[n];
                    store.EnemyHealth[id] = store.EnemyMaxHealth[id];
                }
            }
            return GameplayObservation.Capture(store);
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
