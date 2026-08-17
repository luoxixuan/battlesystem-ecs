using BattleSystemECS.Tests.Infrastructure;
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
            int expectedBatch = ReadConfiguredWaveSpawnBatchSize(sys);

            sys.Update();

            // 精确等于系统读取的 batch 大小，且与 store 中实际活跃敌人数一致。
            Assert.Equal(expectedBatch, sys.GetTotalEnemiesSpawned());
            Assert.Equal(expectedBatch, Store.GetActiveEnemyCount());
        }
    }
}
