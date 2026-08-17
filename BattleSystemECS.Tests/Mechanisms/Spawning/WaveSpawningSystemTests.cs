using BattleSystemECS.Tests.Infrastructure;
using System;
using System.Reflection;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Mechanisms.Spawning
{
    public class WaveSpawningSystemTests : BattleTestBase
    {
        private WaveSpawningSystem Env()
        {
            int pid = Store.CreateEntity();
            Store.PlayerMaxHealth[pid] = 200f;
            Store.PlayerCurrentHealth[pid] = 200f;
            return new WaveSpawningSystem(Store, Renderer, Config);
        }

        // WaveSpawningSystem 没有公开 SpawnBatchSize，测试从系统实例的私有
        // spawnConfig 读取真实注入值来推导期望（不钉 JSON 里的具体 5）。
        private static int ReadConfiguredBatchSize(WaveSpawningSystem sys)
        {
            FieldInfo field = typeof(WaveSpawningSystem)
                .GetField("spawnConfig", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var config = (WaveSpawnConfig)field.GetValue(sys)!;
            return config.SpawnBatchSize;
        }

        [Fact] public void NewSystem_StartsAtWaveOne()
        {
            var sys = Env();
            Assert.Equal(1, sys.GetCurrentWave());
            Assert.Equal(1, sys.GetCurrentLevel());
            Assert.Equal(0, sys.GetTotalEnemiesSpawned());
        }

        [Fact] public void FirstUpdate_SpawnsExactlyOneConfiguredBatch()
        {
            var sys = Env();
            int expectedBatch = ReadConfiguredBatchSize(sys);

            sys.Update();

            // 精确等于系统读取的 batch 大小，且与 store 中实际活跃敌人数一致。
            Assert.Equal(expectedBatch, sys.GetTotalEnemiesSpawned());
            Assert.Equal(expectedBatch, Store.GetActiveEnemyCount());
        }
    }
}
