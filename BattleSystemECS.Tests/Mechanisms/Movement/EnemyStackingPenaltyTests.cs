using BattleSystemECS.Systems;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Mechanisms.Movement
{
    /// <summary>
    /// 格子堆叠减速：同格（键 gx*1000+gy，(int) 截断）的其他敌人数决定 EnemyStackCount 与减速比。
    /// 覆盖稠密计数路径与键跨度过大时的字典回退路径，两者结果一致。
    /// </summary>
    public sealed class EnemyStackingPenaltyTests : BattleTestBase
    {
        private const float Penalty = 0.1f;
        private const float MaxSlow = 0.75f;

        private int Stunned(float x, float y)
        {
            // 眩晕敌人跳过移动，位置在堆叠统计时保持不变
            int id = Enemy(e => { e.X = x; e.Y = y; e.MoveSpeed = 0f; });
            Store.EnemyStunDurationLeft[id] = 10f;
            return id;
        }

        /// <summary>按 [同格四敌人×4, 键碰撞两格×2, 单独敌人] 顺序返回堆叠数与减速比。</summary>
        private (int[] counts, float[] ratios) RunStacking(bool addFarEnemy)
        {
            Player();
            Config.Stacking.PenaltyPerStack = Penalty;
            Config.Stacking.MaxStackSlow = MaxSlow;
            var ids = new[]
            {
                // 同一格 (2,3) 四个敌人
                Stunned(2.5f, 3.2f), Stunned(2.1f, 3.9f), Stunned(2.9f, 3.0f), Stunned(2.0f, 3.5f),
                // 键碰撞：(0,1000) 与 (1,0) 的 gx*1000+gy 相同，按旧实现视为同格
                Stunned(0.5f, 1000.5f), Stunned(1.2f, 0.2f),
                Stunned(6.5f, 9.5f),
            };
            if (addFarEnemy) Stunned(5f, 5_000_000f); // 键跨度远超稠密阈值 → 字典路径

            var movement = new EnemyMovementSystem(Store, 0, gameConfig: Config);
            movement.SetTurn(0);
            movement.Update();

            var counts = new int[ids.Length];
            var ratios = new float[ids.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                counts[i] = Store.EnemyStackCount[ids[i]];
                ratios[i] = Store.EnemyStackSlowRatio[ids[i]];
            }
            return (counts, ratios);
        }

        private static readonly int[] ExpectedCounts = { 3, 3, 3, 3, 1, 1, 0 };
        private static readonly float[] ExpectedRatios = { MaxSlow, MaxSlow, MaxSlow, MaxSlow, 1f - Penalty, 1f - Penalty, 1f };

        [Fact]
        public void DenseCountingMatchesCellOccupancyIncludingKeyCollisions()
        {
            var (counts, ratios) = RunStacking(addFarEnemy: false);
            Assert.Equal(ExpectedCounts, counts);
            Assert.Equal(ExpectedRatios, ratios);
        }

        [Fact]
        public void WideKeySpanFallsBackToDictionaryWithSameCounts()
        {
            var (counts, ratios) = RunStacking(addFarEnemy: true);
            Assert.Equal(ExpectedCounts, counts);
            Assert.Equal(ExpectedRatios, ratios);
        }
    }
}
