using BattleSystemECS.Systems;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    /// <summary>
    /// 敌人能力冷却 / 缴械计时推进：小规模串行路径与大规模（批量并行）路径逐敌人结果一致。
    /// </summary>
    public sealed class EnemyAbilityCooldownTickTests : BattleTestBase
    {
        private (float[] cooldowns, float[] disarms) Run(int enemyCount)
        {
            int player = Player();
            var ability = new EnemyAbilitySystem(Store, Renderer, player, Config);
            var ids = new int[enemyCount];
            for (int i = 0; i < enemyCount; i++) ids[i] = Enemy(e => { e.MoveSpeed = 0f; });
            // 首次推进为每个敌人分配能力槽，再写入各不相同的冷却与缴械剩余时间
            ability.UpdateCooldowns(0.5f);
            for (int i = 0; i < enemyCount; i++)
            {
                var inst = Store.GetAbility(ids[i], 0);
                inst.CurrentCooldown = i % 5;
                Store.SetAbility(ids[i], 0, inst);
                Store.EnemyDisarmDurationLeft[ids[i]] = (i % 3) * 0.75f;
            }

            ability.UpdateCooldowns(1f);

            var cooldowns = new float[enemyCount];
            var disarms = new float[enemyCount];
            for (int i = 0; i < enemyCount; i++)
            {
                cooldowns[i] = Store.GetAbility(ids[i], 0).CurrentCooldown;
                disarms[i] = Store.EnemyDisarmDurationLeft[ids[i]];
            }
            return (cooldowns, disarms);
        }

        private static (float[] cooldowns, float[] disarms) Expected(int enemyCount)
        {
            var cooldowns = new float[enemyCount];
            var disarms = new float[enemyCount];
            for (int i = 0; i < enemyCount; i++)
            {
                cooldowns[i] = System.Math.Max(0f, (i % 5) - 1f);
                float disarm = (i % 3) * 0.75f;
                disarms[i] = disarm > 0f ? System.Math.Max(0f, disarm - 1f) : 0f;
            }
            return (cooldowns, disarms);
        }

        [Theory]
        [InlineData(200)]   // 低于并行阈值：串行路径
        [InlineData(5000)]  // 高于并行阈值：批量并行 + 串行回退合并
        public void CooldownAndDisarmTickMatchPerEnemyExpectation(int enemyCount)
        {
            var (cooldowns, disarms) = Run(enemyCount);
            var (expectedCooldowns, expectedDisarms) = Expected(enemyCount);
            Assert.Equal(expectedCooldowns, cooldowns);
            Assert.Equal(expectedDisarms, disarms);
        }
    }
}
