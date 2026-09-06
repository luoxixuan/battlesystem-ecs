using Xunit;
using BattleSystemECS.Core;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class BenchmarkFixedPopulationTests
    {
        [Fact]
        public void Mode2_HoldsPopulationAcrossMeasuredFrames()
        {
            var store = new ComponentStore();
            var evidence = new Systems.BenchmarkSystem(store)
                .RunFixedPopulationSample(2, 128, 20);

            Assert.Equal(20, evidence.FramesExecuted);
            Assert.Equal(128, evidence.PopulationMin);
            Assert.Equal(128, evidence.PopulationMax);
            Assert.Equal(128, evidence.PopulationEnd);
            Assert.Equal(0, evidence.Kills);
            Assert.Equal(20, store.ActiveTowerIds.Count);
        }

        [Fact]
        public void Mode4_HoldsPopulationAcrossMeasuredFrames()
        {
            var store = new ComponentStore();
            var evidence = new Systems.BenchmarkSystem(store)
                .RunFixedPopulationSample(4, 128, 20);

            Assert.Equal(20, evidence.FramesExecuted);
            Assert.Equal(128, evidence.PopulationMin);
            Assert.Equal(128, evidence.PopulationMax);
            Assert.Equal(128, evidence.PopulationEnd);
            Assert.Equal(0, evidence.Kills);
            Assert.True(evidence.GraphSealed);
            Assert.Equal(20, store.ActiveTowerIds.Count);
        }

        [Fact]
        public void Mode5_HarnessPlacesObservationTowers()
        {
            var store = new ComponentStore();
            var evidence = new Systems.BenchmarkSystem(store).RunCompositionHarness(5);

            Assert.Equal(8, store.ActiveTowerIds.Count);
            Assert.True(evidence.GraphSealed);
        }
    }
}
