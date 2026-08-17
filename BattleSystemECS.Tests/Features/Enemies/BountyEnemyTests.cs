using BattleSystemECS.Tests.Infrastructure;
using Xunit;
using BattleSystemECS.Core;

namespace BattleSystemECS.Tests.Features.Enemies
{
    /// <summary>
    /// Tests for the Bounty enemy (Round 179 Direction 3) — high-value high-risk target
    /// that pays BountyGoldMult × the normal gold reward on death. The risk is the player's
    /// attention being diverted from the wave while chasing the bonus. Verifies:
    /// 1. Default AddEnemy produces a non-bounty (zero-overhead fast path)
    /// 2. SetEnemyBounty configures the 2 bounty fields correctly
    /// 3. goldMult clamps to [1.0, 20.0]
    /// 4. DestroyEntity reset prevents leakage across slot reuse
    /// 5. Killing a Bounty enemy actually multiplies gold via ResolveEnemiesKilledThisFrame
    /// </summary>
    public class BountyEnemyTests : BattleTestBase
    {
        [Fact]
        public void DefaultEnemy_BountyFields_AreInert()
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            // Both bounty fields should be in inert defaults — hot path fast-returns
            Assert.False(Store.EnemyIsBounty[eid]);
            Assert.Equal(1f, Store.EnemyBountyGoldMult[eid]);
        }

        [Fact]
        public void SetEnemyBounty_ConfiguresFields()
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            Store.SetEnemyBounty(eid, goldMult: 5.0f);
            Assert.True(Store.EnemyIsBounty[eid]);
            Assert.Equal(5.0f, Store.EnemyBountyGoldMult[eid]);
        }

        [Fact]
        public void SetEnemyBounty_ClampsGoldMult_ToValidRange()
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            // Clamp upper bound: 50.0f should clamp to 20.0f (no game-breaking economy)
            Store.SetEnemyBounty(eid, 50.0f);
            Assert.Equal(20.0f, Store.EnemyBountyGoldMult[eid]);
            // Clamp lower bound: -3.0f should clamp to 1.0f (no reduced reward)
            Store.SetEnemyBounty(eid, -3.0f);
            Assert.Equal(1.0f, Store.EnemyBountyGoldMult[eid]);
        }

        [Fact]
        public void SetEnemyBounty_InvalidEntity_NoOp()
        {
            // 先写入合法实体的已知值作为对照，再用无效 id 调用并断言合法槽位不变。
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            Store.SetEnemyBounty(eid, 5.0f);
            Assert.True(Store.EnemyIsBounty[eid]);
            Assert.Equal(5.0f, Store.EnemyBountyGoldMult[eid]);

            Store.SetEnemyBounty(-1, 7.0f);
            Store.SetEnemyBounty(99999, 7.0f);

            Assert.True(Store.EnemyIsBounty[eid]);
            Assert.Equal(5.0f, Store.EnemyBountyGoldMult[eid]);
        }

        [Fact]
        public void RecycleEntity_BountyFields_AreReset()
        {
            // Critical: when an entity id is recycled (DestroyEntity + AddEnemy),
            // the bounty state must NOT leak from the prior slot occupant. A
            // freshly-spawned enemy must start as a normal enemy, never inherit
            // a 5× gold multiplier from a prior slot occupant.
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            Store.SetEnemyBounty(eid, 5.0f);
            // Now recycle: destroy then re-add at same id (ComponentStore reuses ids)
            Store.DestroyEntity(eid);
            int newEid = Store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            // Slot must be reset to inert defaults — NOT carry over old bounty state
            Assert.False(Store.EnemyIsBounty[newEid]);
            Assert.Equal(1f, Store.EnemyBountyGoldMult[newEid]);
        }

        [Fact]
        public void Killing_BountyEnemy_AwardsMultipliedGold()
        {
            // End-to-end: a Bounty enemy with 10 base gold and 5× mult should award
            // 50 gold (10 × 1.0 _goldKillMultiplier × 1.0 _allIncomeMultKill × 5.0 bounty)
            Store.SetPlayerGold(0, 0f);

            int bountyEid = Store.AddEnemy(5f, 5f, 1f, 10f, 10f, 0f, 10, 99);
            Store.SetEnemyBounty(bountyEid, 5.0f);
            Store.EnemyActive[bountyEid] = true;
            Store.AddActiveEnemyId(bountyEid);

            // Kill the bounty enemy (resolve-on-death uses the death queue, not the HP check)
            Store.SetEnemyHealth(bountyEid, 0f);
            Store.QueueEnemyDeath(bountyEid, 0);
            Store.ResolveEnemiesKilledThisFrame();

            // Gold should be 50 (10 base × 5 bounty mult, no other multipliers active)
            Assert.Equal(50f, Store.GetPlayerGold(0), 0.001f);
        }

        [Fact]
        public void Killing_NonBountyEnemy_AwardsBaseGold()
        {
            // Sanity check: a non-bounty enemy with 10 base gold awards exactly 10
            // (no bounty multiplier applied).
            Store.SetPlayerGold(0, 0f);

            int eid = Store.AddEnemy(5f, 5f, 1f, 10f, 10f, 0f, 10, 99);
            Store.EnemyActive[eid] = true;
            Store.AddActiveEnemyId(eid);
            Assert.False(Store.EnemyIsBounty[eid]);

            Store.SetEnemyHealth(eid, 0f);
            Store.QueueEnemyDeath(eid, 0);
            Store.ResolveEnemiesKilledThisFrame();

            Assert.Equal(10f, Store.GetPlayerGold(0), 0.001f);
        }
    }
}