using BattleSystemECS.Tests.Infrastructure;
using System;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Mechanisms.Spawning
{
    public class WaveSpawningSystemTests
    {
        private (ComponentStore store, GameConfig config) Env()
        {
            var store = new ComponentStore();
            int pid = store.CreateEntity();
            store.PlayerMaxHealth[pid] = 200f;
            store.PlayerCurrentHealth[pid] = 200f;
            return (store, new GameConfig());
        }

        [Fact] public void NewSystem_StartsAtWaveOne()
        {
            var (store, config) = Env();
            var r = new MockRenderer();
            var sys = new WaveSpawningSystem(store, r, config);
            Assert.Equal(1, sys.GetCurrentWave());
            Assert.Equal(1, sys.GetCurrentLevel());
            Assert.Equal(0, sys.GetTotalEnemiesSpawned());
        }

        [Fact] public void FirstUpdate_SpawnsEnemies()
        {
            var (store, config) = Env();
            var r = new MockRenderer();
            var sys = new WaveSpawningSystem(store, r, config);
            sys.Update();
            Assert.True(sys.GetTotalEnemiesSpawned() > 0);
        }

        [Fact] public void BatchSize_IsFive()
        {
            var (store, config) = Env();
            var r = new MockRenderer();
            var sys = new WaveSpawningSystem(store, r, config);
            sys.Update();
            Assert.Equal(5, sys.GetTotalEnemiesSpawned());
        }
    }
}