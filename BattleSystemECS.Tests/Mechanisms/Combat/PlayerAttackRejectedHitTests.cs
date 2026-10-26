using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Systems;
using BattleSystemECS.Tests.Infrastructure;

namespace BattleSystemECS.Tests.Mechanisms.Combat
{
    /// <summary>
    /// 玩家攻击：伤害权威拒绝（如延迟队列溢出）时命中未落地，不得触发命中副作用。
    /// </summary>
    public class PlayerAttackRejectedHitTests : BattleTestBase
    {
        private (int hits, float dps, float health) RunPlayerAttack(bool fillDeferredQueue)
        {
            int pid = Player(p => { p.X = 5f; p.Y = 0f; p.Health = 200f; p.AttackDamage = 10f; p.AttackRange = 3f; });
            int target = Enemy(e => { e.X = 5f; e.Y = 1f; e.Health = 100f; e.MoveSpeed = 0f; });
            // 填充用敌人位于玩家下方（y <= playerY），不在玩家攻击范围内
            int filler = Enemy(e => { e.X = 5f; e.Y = -1f; e.Health = 100f; e.MoveSpeed = 0f; });

            var events = new EventBus();
            int hits = 0;
            events.EnemyHit.Subscribe(_ => hits++);
            var attack = new PlayerTowerAttackSystem(Store, Renderer, pid, Config, null, events);

            Store.DamageResolver.EnableDeferred(true);
            if (fillDeferredQueue)
            {
                var source = Store.GetEntityHandle(pid);
                var fillerHandle = Store.GetEntityHandle(filler);
                for (int i = 0; i < DamageResolver.MaxPendingRequests; i++)
                {
                    var queued = Store.DamageResolver.TryApply(new DamageRequest(source, fillerHandle, 1f, DamageType.True,
                        ElementType.None, DamageFlags.None, DamageAmountStage.Raw, DamageCommitBoundary.GameplayResolve,
                        1000L + i, ownerPlayerId: pid));
                    Assert.True(queued.Deferred);
                }
            }

            attack.SetTurn(0);
            attack.Update();
            return (hits, Store.PlayerDPSAccumulator[pid], Store.EnemyHealth[target]);
        }

        [Fact]
        public void AcceptedPlayerHitPublishesHitEventAndAccumulatesDps()
        {
            var (hits, dps, _) = RunPlayerAttack(fillDeferredQueue: false);
            Assert.Equal(1, hits);
            Assert.True(dps > 0f);
        }

        [Fact]
        public void OverflowRejectedPlayerHitSkipsHitEventAndDps()
        {
            long overflowBefore = Store.DamageResolver.RequestOverflowCount;
            var (hits, dps, health) = RunPlayerAttack(fillDeferredQueue: true);
            Assert.Equal(overflowBefore + 1, Store.DamageResolver.RequestOverflowCount);
            Assert.Equal(0, hits);
            Assert.Equal(0f, dps);
            Assert.Equal(100f, health);
        }

        [Fact]
        public void DamageTypeNamesMatchEnumToStringForEveryDefinedValue()
        {
            foreach (DamageType type in Enum.GetValues(typeof(DamageType)))
                Assert.Equal(type.ToString(), DamageTypeNames.Get(type));
            Assert.Equal(((DamageType)3).ToString(), DamageTypeNames.Get((DamageType)3));
        }
    }
}
