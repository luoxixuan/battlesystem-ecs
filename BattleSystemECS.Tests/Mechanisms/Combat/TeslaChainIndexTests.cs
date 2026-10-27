using System;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Mechanisms.Combat
{
    /// <summary>
    /// Tesla 跳链邻域索引差分测试：索引路径与线性扫描对同一布局的选取（含同距平局、地图外坐标）逐敌人一致。
    /// </summary>
    public sealed class TeslaChainIndexTests
    {
        private const float StartHealth = 100000f;

        private static float[] RunChainScenario(bool forceLinear, int seed)
        {
            using var world = new TestWorld();
            var random = new Random(seed);
            var enemies = new int[120];
            for (int i = 0; i < enemies.Length; i++)
            {
                // 前 100 个落在塔附近小方格的整数坐标上（同点、同距平局密集）；
                // 其余 0.5 步长散布，部分落在 10×20 地图之外
                bool dense = i < 100;
                float x = dense ? random.Next(2, 9) : random.Next(-6, 26) * 0.5f;
                float y = dense ? random.Next(5, 13) : random.Next(-4, 46) * 0.5f;
                enemies[i] = world.Enemy(e => { e.X = x; e.Y = y; e.MoveSpeed = 0f; e.Health = StartHealth; e.Damage = 0f; });
            }
            world.RawTower(5, 4, TowerType.Tesla, 20f, 4, 1f);
            world.RawTower(3, 10, TowerType.Tesla, 20f, 3, 1f);

            var attack = new TowerAttackSystem(world.Store, world.Renderer) { ForceLinearChainScan = forceLinear };
            for (int frame = 0; frame < 4; frame++)
            {
                world.Store.BeginFrame();
                world.Store.RebuildSpatialGrid();
                attack.SetTurn(frame);
                attack.Update(1f);
                world.Store.ResolveEnemiesKilledThisFrame();
            }

            var health = new float[enemies.Length];
            for (int i = 0; i < enemies.Length; i++) health[i] = world.Store.EnemyHealth[enemies[i]];
            return health;
        }

        [Theory]
        [InlineData(1)]
        [InlineData(7)]
        [InlineData(42)]
        public void IndexedChainHopsMatchLinearScan(int seed)
        {
            float[] indexed = RunChainScenario(forceLinear: false, seed);
            float[] linear = RunChainScenario(forceLinear: true, seed);

            Assert.Equal(linear, indexed);
            // 场景须真正产生跳链：被命中的敌人多于两座塔的主目标数
            int damaged = 0;
            foreach (float h in indexed) if (h < StartHealth) damaged++;
            Assert.True(damaged > 2, $"expected chain hops to damage more than the primaries, got {damaged}");
        }
    }
}
