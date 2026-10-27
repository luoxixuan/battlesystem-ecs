using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;
using BattleSystemECS.Tests.Infrastructure;

namespace BattleSystemECS.Tests.Mechanisms.Combat
{
    /// <summary>
    /// 玩家攻击目标上限：每帧只命中沿进攻轴最近的 MaxTargetsPerFrame 个目标，同距按敌人 id；0 = 不限。
    /// </summary>
    public class PlayerAttackTargetCapTests : BattleTestBase
    {
        private (int pid, int far, int nearest, int midLow, int midHigh) Setup()
        {
            int pid = Player(p => { p.X = 5f; p.Y = 0f; p.Health = 200f; p.AttackDamage = 10f; p.AttackRange = 3f; });
            int far = Enemy(e => { e.X = 5f; e.Y = 3f; e.Health = 100f; e.MoveSpeed = 0f; });
            int nearest = Enemy(e => { e.X = 6f; e.Y = 1f; e.Health = 100f; e.MoveSpeed = 0f; });
            int midLow = Enemy(e => { e.X = 4f; e.Y = 2f; e.Health = 100f; e.MoveSpeed = 0f; });
            int midHigh = Enemy(e => { e.X = 5f; e.Y = 2f; e.Health = 100f; e.MoveSpeed = 0f; });
            return (pid, far, nearest, midLow, midHigh);
        }

        private void Attack(int pid)
        {
            var attack = new PlayerTowerAttackSystem(Store, Renderer, pid, Config, null, new EventBus());
            attack.SetTurn(0);
            attack.Update();
        }

        [Fact]
        public void CapKeepsNearestTargetsAndBreaksTiesByEnemyId()
        {
            var (pid, far, nearest, midLow, midHigh) = Setup();
            Assert.True(midLow < midHigh);
            Config.Player.MaxTargetsPerFrame = 2;
            Attack(pid);

            Assert.True(Store.EnemyHealth[nearest] < 100f);
            Assert.True(Store.EnemyHealth[midLow] < 100f);
            Assert.Equal(100f, Store.EnemyHealth[midHigh]);
            Assert.Equal(100f, Store.EnemyHealth[far]);
        }

        [Fact]
        public void ZeroCapHitsEveryEnemyInBand()
        {
            var (pid, far, nearest, midLow, midHigh) = Setup();
            Config.Player.MaxTargetsPerFrame = 0;
            Attack(pid);

            foreach (int id in new[] { far, nearest, midLow, midHigh })
                Assert.True(Store.EnemyHealth[id] < 100f);
        }
    }
}
