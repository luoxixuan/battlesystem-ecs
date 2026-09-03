using System;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Systems;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Integration
{
    /// <summary>
    /// Phase 3 容量取证：sealed FrameScheduler.Tick 下接触近战不得溢出 Resource 事件队列。
    /// </summary>
    public sealed class PlayerDamageCapacityFlowTests : BattleTestBase
    {
        [Fact]
        public void SealedFrameTick_ContactMelee_NoResourceEventOverflow()
        {
            Config.ManaShield.Enabled = false;
            Config.Levels.Clear();
            int player = Player(p =>
            {
                p.Health = 10000f;
                p.X = 0f;
                p.Y = 0f;
            });
            const int contact = 48;
            for (int i = 0; i < contact; i++)
            {
                int eid = Enemy(e =>
                {
                    e.X = 0f;
                    e.Y = 0f;
                    e.Health = 100f;
                    e.Damage = 2f;
                    e.MoveSpeed = 0f;
                });
                Store.SetEnemyAttackInterval(eid, 0.5f);
                Store.EnemyBehaviorTree[eid] = MeleeTree();
            }

            var registry = new SystemRegistry();
            registry.CreateAll(Store, Config, Renderer, player, new StateMachine());
            registry.WireDependencies(Store, player);
            var scheduler = new FrameScheduler(Store, Config);
            registry.AssignToGroups(scheduler);
            Assert.True(scheduler.IsCompositionSealed);
            scheduler.Phase = GameState.WavePhase;
            Store.ResourceResolver.Events.ResetDiagnostics();

            float hpBefore = Store.PlayerCurrentHealth[player];
            for (int frame = 0; frame < 10; frame++)
                scheduler.Tick(0.1f, frame);

            Assert.True(Store.PlayerCurrentHealth[player] < hpBefore);
            Assert.Equal(0, Store.ResourceResolver.EventOverflowCount);
            Assert.Equal(0, Store.ResourceResolver.GetRejectionCount(ResourceRejectionReason.RequestQueueOverflow));
            Assert.True(Store.ResourceResolver.Events.PeakCount > 0);
            Assert.True(Store.ResourceResolver.Events.PeakCount < Store.ResourceResolver.Events.Capacity,
                $"PeakCount={Store.ResourceResolver.Events.PeakCount} must stay under Capacity={Store.ResourceResolver.Events.Capacity}");
        }

        private static BTCachedTree MeleeTree()
        {
            var node = new BTCachedNode
            {
                Id = "attack_melee",
                Type = BTNodeType.Action,
                Action = "attack_melee",
                PrecomputedActionEnum = EnemyActionType.AttackMelee,
                Children = Array.Empty<int>()
            };
            return new BTCachedTree { MonsterType = "attack_melee", Root = node, Nodes = new[] { node } };
        }
    }
}
