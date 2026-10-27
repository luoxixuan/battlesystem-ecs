using System;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Systems;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Mechanisms.Combat
{
    public sealed class PlayerDamageAuthorityTests : BattleTestBase
    {
        private const int PlayerId = 0;

        private EnemyAISystem CreateAi(EventBus? bus = null)
        {
            var ability = new EnemyAbilitySystem(Store, Renderer, PlayerId, Config);
            return new EnemyAISystem(Store, Renderer, PlayerId, Config, ability, eventBus: bus);
        }

        [Fact]
        public void TowerThorns_DamageAppliedSourceIsEnemyNotPlayer()
        {
            int pid = Player(p =>
            {
                p.Health = 200f;
                p.X = 0f;
                p.Y = 0f;
            });
            Assert.Equal(PlayerId, pid);

            int eid = Enemy(e =>
            {
                e.X = 1f;
                e.Y = 0f;
                e.Health = 200f;
                e.Damage = 0f;
            });
            Store.EnemyThornsRatio[eid] = 0.5f;

            int tid = RawTower(0, 0, TowerType.Basic, damage: 40f, range: 5, speed: 10f);
            // Update 先 += deltaTime 再比 interval；0 起步 + Update(1f) 即可开火。
            Store.TowerLastAttackTime[tid] = 0f;

            float hpBefore = Store.PlayerCurrentHealth[pid];
            var attack = new TowerAttackSystem(Store, Renderer);
            Store.BeginFrame();
            RebuildGrid();
            attack.SetTurn(1);
            attack.Update(1f);

            Assert.True(Store.PlayerCurrentHealth[pid] < hpBefore);
            Assert.True(Store.ResourceResolver.Events.Count > 0);

            bool foundEnemySourced = false;
            for (int i = 0; i < Store.ResourceResolver.Events.Count; i++)
            {
                var evt = Store.ResourceResolver.Events.Get(i);
                if (evt.Type != GameplayEventType.DamageApplied) continue;
                if (evt.Target.Index != pid) continue;
                Assert.Equal(eid, evt.Source.Index);
                Assert.NotEqual(pid, evt.Source.Index);
                foundEnemySourced = true;
            }

            Assert.True(foundEnemySourced);
        }

        [Fact]
        public void MeleeRejected_DoesNotConsumeEnemyStealthMultiplier()
        {
            Player(p =>
            {
                p.Health = 50f;
                p.X = 0f;
                p.Y = 0f;
            });
            Store.PlayerCurrentHealth[PlayerId] = 0f;

            int eid = Enemy(e =>
            {
                e.X = 0f;
                e.Y = 0f;
                e.Health = 100f;
                e.Damage = 10f;
            });
            Store.EnemyStealthMultiplier[eid] = 2.5f;
            Store.SetEnemyAttackInterval(eid, 0f);

            var ai = CreateAi();
            ai.SetTurn(1, 0.016f);
            ai.InvokeExecuteActionEnum(eid, EnemyActionType.AttackMelee);

            Assert.Equal(2.5f, Store.EnemyStealthMultiplier[eid], 3);
            Assert.Equal(0f, Store.PlayerCurrentHealth[PlayerId]);
            Assert.Equal(0, Store.ResourceResolver.Events.Count);
        }

        [Fact]
        public void MeleeRejectedOnEventOverflow_DoesNotConsumeEnemyStealthMultiplier()
        {
            Player(p =>
            {
                p.Health = 200f;
                p.X = 0f;
                p.Y = 0f;
            });
            var filler = new GameplayEvent(GameplayEventType.AbilityRejected, default(EntityHandle), default(EntityHandle), 9001L);
            while (Store.ResourceResolver.Events.TryPublish(filler, true)) { }

            int eid = Enemy(e =>
            {
                e.X = 0f;
                e.Y = 0f;
                e.Health = 100f;
                e.Damage = 10f;
            });
            Store.EnemyStealthMultiplier[eid] = 2.5f;
            Store.SetEnemyAttackInterval(eid, 0f);
            float hpBefore = Store.PlayerCurrentHealth[PlayerId];

            Assert.False(Store.CanApplyPlayerDamageAuthority(eid, PlayerId, 10f));

            var ai = CreateAi();
            ai.SetTurn(1, 0.016f);
            ai.InvokeExecuteActionEnum(eid, EnemyActionType.AttackMelee);

            Assert.Equal(2.5f, Store.EnemyStealthMultiplier[eid], 3);
            Assert.Equal(hpBefore, Store.PlayerCurrentHealth[PlayerId], 3);
            Assert.Equal(0f, Store.EnemyAttackCooldownLeft[eid]);
        }

        [Fact]
        public void AttackInterval_GatesMeleeUntilCooldownExpires()
        {
            Player(p =>
            {
                p.Health = 200f;
                p.X = 0f;
                p.Y = 0f;
            });
            int eid = Enemy(e =>
            {
                e.X = 0f;
                e.Y = 0f;
                e.Health = 50f;
                e.Damage = 5f;
            });
            Store.SetEnemyAttackInterval(eid, 1f);
            Assert.True(Store.IsEnemyAttackReady(eid));

            var ai = CreateAi();
            ai.SetTurn(1, 0.016f);
            ai.InvokeExecuteActionEnum(eid, EnemyActionType.AttackMelee);
            Assert.False(Store.IsEnemyAttackReady(eid));
            float hpAfterFirst = Store.PlayerCurrentHealth[PlayerId];

            ai.InvokeExecuteActionEnum(eid, EnemyActionType.AttackMelee);
            Assert.Equal(hpAfterFirst, Store.PlayerCurrentHealth[PlayerId], 3);
            Assert.Equal(1f, Store.EnemyAttackCooldownLeft[eid], 3);

            // 冷却递减走 Update 入口；先 stun 避免 fallback BT 再次开火把冷却重新 Commit。
            Store.EnemyStunFlag[eid] = true;
            ai.SetTurn(2, 1.1f);
            ai.Update();
            Assert.Equal(0f, Store.EnemyAttackCooldownLeft[eid], 3);
            Assert.True(Store.IsEnemyAttackReady(eid));

            Store.EnemyStunFlag[eid] = false;
            ai.InvokeExecuteActionEnum(eid, EnemyActionType.AttackMelee);
            Assert.True(Store.PlayerCurrentHealth[PlayerId] < hpAfterFirst);
        }

        [Fact]
        public void CanAttackCondition_RequiresAttackReady()
        {
            Player(p => { p.X = 0f; p.Y = 0f; p.Health = 100f; });
            int eid = Enemy(e =>
            {
                e.X = 0f;
                e.Y = 1f;
                e.Health = 50f;
                e.Damage = 1f;
            });
            var node = new BTCachedNode
            {
                Type = BTNodeType.Condition,
                Condition = "can_attack",
                Operator = "<=",
                Value = 1.5f,
            };

            Store.SetEnemyAttackInterval(eid, 2f);
            Assert.True(BTCachedTreeEvaluator.EvaluateCondition(node, eid, Store, PlayerId));

            Store.CommitEnemyAttackCooldown(eid);
            Assert.False(BTCachedTreeEvaluator.EvaluateCondition(node, eid, Store, PlayerId));
        }
    }
}
