using System;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Tests.Infrastructure;
using BattleSystemECS.Systems;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class GameplayCapacityProbeTests
    {
        [Fact]
        public void ProductionCompositionProbeRecordsNonZeroPeaks()
        {
            using var world = new TestWorld();
            int player = world.Player(p => { p.AttackDamage = 0f; p.Health = 100000f; });
            world.RawTower(5, 5, damage: 1f, range: 50, speed: 10f);
            for (int i = 0; i < 256; i++)
            {
                int id = world.Enemy(e =>
                {
                    e.X = 5f + (i % 8);
                    e.Y = 5f + (i / 8);
                    e.MoveSpeed = 0f;
                    e.Health = 100000f;
                    e.MaxHealth = 100000f;
                    e.Damage = 0f;
                });
                Assert.True(id >= 0);
            }
            var runtime = BenchmarkCompositionFactory.Create(world.Store, world.Config, world.Renderer,
                player, scenarioKind: FrameScenarioKind.FixedPopulationBenchmark);
            runtime.StateMachine.TransitionTo(GameState.BuildPhase);
            runtime.StateMachine.TransitionTo(GameState.WavePhase);

            for (int frame = 0; frame < 120; frame++)
            {
                runtime.Scheduler.Tick(0.016f, frame);
            }

            GameplayObservationSnapshot observation = GameplayObservation.Capture(world.Store);
            Assert.True(runtime.Scheduler.IsCompositionSealed);
            Assert.True(observation.DamagePendingPeak > 0);
            Assert.True(observation.DamageEventPeak > 0);
            Assert.True(observation.DamageAccepted > 0);
            Assert.Equal(0, observation.DamageLegacyApplied);
            Assert.Equal(0, observation.DamageRequestOverflows);
            Assert.Equal(0, observation.DamageEventOverflows);
            Assert.Equal(0, observation.DamageUnconsumedRequests);
        }

        [Fact]
        public void ContractAdapterProbeRecordsEachUnwiredIntentCategory()
        {
            var store = new ComponentStore();
            int entity = store.AddEnemy(1, 1, 1, 10, 10, 1, 1, 1);
            var handle = store.GetEntityHandle(entity);
            var ability = new CommandBuffer<AbilityRequest>(4);
            var effect = new CommandBuffer<EffectRequest>(4);
            var heal = new CommandBuffer<HealRequest>(4);
            var shield = new CommandBuffer<ShieldRequest>(4);
            var resource = new CommandBuffer<ResourceRequest>(4);
            var death = new GameplayEventQueue(4, 1);
            long sequence = 100;
            Assert.True(ability.TryAdd(new AbilityRequest(handle, new AbilityId(1), handle, sequence++)));
            Assert.True(effect.TryAdd(new EffectRequest(handle, handle, new EffectId(1), 1, ClockId.Combat, new BattleSystemECS.Core.GAS.ExecutionContext(handle, handle, default(AbilityId), new EffectId(1), ClockId.Combat, sequence++))));
            Assert.True(heal.TryAdd(new HealRequest(handle, handle, 1, sequence++)));
            Assert.True(shield.TryAdd(new ShieldRequest(handle, handle, 1, 1, ClockId.Combat, sequence++)));
            Assert.True(resource.TryAdd(new ResourceRequest(handle, handle, new AttributeKey(1), 1, sequence++)));
            Assert.True(death.TryPublish(new GameplayEvent(GameplayEventType.DeathQueued, handle, handle, sequence++), true));
            Assert.Equal(1, ability.Count); Assert.Equal(1, effect.Count); Assert.Equal(1, heal.Count);
            Assert.Equal(1, shield.Count); Assert.Equal(1, resource.Count); Assert.Equal(1, death.Count);
        }
    }
}
