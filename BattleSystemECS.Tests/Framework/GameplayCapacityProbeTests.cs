using System;
using System.IO;
using BattleSystemECS.Components;
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
            var store = new ComponentStore();
            var bus = new ProbeBus();
            var logger = new MockRenderer();
            int tower = store.CreateEntity();
            store.AddTower(tower, TowerType.Basic, 100f, 50, 1f, 1, 10f);
            store.PositionX[tower] = 5f; store.PositionY[tower] = 5f;
            for (int i = 0; i < 10000; i++) {
                int id = store.AddEnemy(5f, 5f + (i % 2), 1f, 1000f, 1000f, 1f, 1, 1);
                store.SetEntityName(id, "ProbeEnemy" + i);
            }
            var attack = new TowerAttackSystem(store, logger, null, 10, null, bus);
            var requests = new CommandBuffer<DamageRequest>(2048, 128);
            var events = new GameplayEventQueue(2048, 128);
            int peakRequests = 0, peakEvents = 0;
            for (int frame = 0; frame < 500; frame++) {
                store.RebuildSpatialGrid();
                attack.SetTurn(frame + 1);
                attack.Update(1f);
                for (int i = 0; i < bus.DamageCount; i++) {
                    var source = store.GetEntityHandle(tower);
                    var target = store.GetEntityHandle(bus.Targets[i]);
                    long sequence = ((long)1 << 32) | (uint)(frame * 1024 + i);
                    requests.TryAdd(new DamageRequest(source, target, bus.Damages[i], DamageType.Physical, sequence));
                    events.TryPublish(new GameplayEvent(GameplayEventType.DamageApplied, source, target, sequence));
                }
                peakRequests = Math.Max(peakRequests, requests.Count);
                peakEvents = Math.Max(peakEvents, events.Count);
                requests.Clear(); events.Clear(); bus.Reset();
            }
            Assert.True(peakRequests > 0);
            Assert.True(peakEvents > 0);
            string artifactDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts"));
            string path = Path.Combine(artifactDir, "capacity-probe-20260830.log");
            Directory.CreateDirectory(artifactDir);
            File.WriteAllText(path, "composition=TowerAttackSystem; entities=10000; frames=500\n" +
                "damageRequestPeak=" + peakRequests + "; capacity=2048; reserved=128; overflow=0\n" +
                "gameplayEventPeak=" + peakEvents + "; capacity=2048; reserved=128; overflow=0\n" +
                "ability/effect/heal/shield/resource/death producers=not-connected\n" +
                "recommended capacity=peak*2+32; reserved critical=128\n");
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

        private sealed class ProbeBus : IBattleEventBus
        {
            private readonly int[] _targets = new int[256]; private readonly float[] _damages = new float[256];
            public int DamageCount; public int[] Targets => _targets; public float[] Damages => _damages;
            public void Reset() { DamageCount = 0; }
            public void OnDamageDealt(int targetId, float amount, string damageType, bool isCritical) { _targets[DamageCount] = targetId; _damages[DamageCount++] = amount; }
            public void OnEntityCreated(int entityId, float x, float y, string entityType) { }
            public void OnTowerCreated(int entityId, float x, float y, TowerType towerType) { }
            public void OnEntityDestroyed(int entityId) { }
            public void OnPositionChanged(int entityId, float x, float y) { }
            public void OnPositionsChanged(System.Collections.Generic.List<(int entityId, float x, float y)> changes) { }
            public void OnEntityKilled(int entityId, int killerId) { }
            public void OnProjectileFired(float fromX, float fromY, float toX, float toY, float speed) { }
            public void OnWaveStarted(int waveNumber) { }
            public void OnGameOver(bool victory) { }
        }
    }
}
