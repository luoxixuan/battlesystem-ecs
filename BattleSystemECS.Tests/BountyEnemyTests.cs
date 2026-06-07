using Xunit;
using BattleSystemECS.Core;

namespace BattleSystemECS.Tests
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
    public class BountyEnemyTests
    {
        private ComponentStore CreateStore()
        {
            return new ComponentStore();
        }

        [Fact]
        public void DefaultEnemy_BountyFields_AreInert()
        {
            var store = CreateStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            // Both bounty fields should be in inert defaults — hot path fast-returns
            Assert.False(store.EnemyIsBounty[eid]);
            Assert.Equal(1f, store.EnemyBountyGoldMult[eid]);
        }

        [Fact]
        public void SetEnemyBounty_ConfiguresFields()
        {
            var store = CreateStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            store.SetEnemyBounty(eid, goldMult: 5.0f);
            Assert.True(store.EnemyIsBounty[eid]);
            Assert.Equal(5.0f, store.EnemyBountyGoldMult[eid]);
        }

        [Fact]
        public void SetEnemyBounty_ClampsGoldMult_ToValidRange()
        {
            var store = CreateStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            // Clamp upper bound: 50.0f should clamp to 20.0f (no game-breaking economy)
            store.SetEnemyBounty(eid, 50.0f);
            Assert.Equal(20.0f, store.EnemyBountyGoldMult[eid]);
            // Clamp lower bound: -3.0f should clamp to 1.0f (no reduced reward)
            store.SetEnemyBounty(eid, -3.0f);
            Assert.Equal(1.0f, store.EnemyBountyGoldMult[eid]);
        }

        [Fact]
        public void SetEnemyBounty_InvalidEntity_NoOp()
        {
            var store = CreateStore();
            // Negative entity id is invalid → silent no-op (no throw)
            store.SetEnemyBounty(-1, 5.0f);
            // Out-of-range entity id is invalid → silent no-op
            store.SetEnemyBounty(99999, 5.0f);
        }

        [Fact]
        public void RecycleEntity_BountyFields_AreReset()
        {
            // Critical: when an entity id is recycled (DestroyEntity + AddEnemy),
            // the bounty state must NOT leak from the prior slot occupant. A
            // freshly-spawned enemy must start as a normal enemy, never inherit
            // a 5× gold multiplier from a prior slot occupant.
            var store = CreateStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            store.SetEnemyBounty(eid, 5.0f);
            // Now recycle: destroy then re-add at same id (ComponentStore reuses ids)
            store.DestroyEntity(eid);
            int newEid = store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            // Slot must be reset to inert defaults — NOT carry over old bounty state
            Assert.False(store.EnemyIsBounty[newEid]);
            Assert.Equal(1f, store.EnemyBountyGoldMult[newEid]);
        }

        [Fact]
        public void Killing_BountyEnemy_AwardsMultipliedGold()
        {
            // End-to-end: a Bounty enemy with 10 base gold and 5× mult should award
            // 50 gold (10 × 1.0 _goldKillMultiplier × 1.0 _allIncomeMultKill × 5.0 bounty)
            var store = CreateStore();
            store.SetPlayerGold(0, 0f);

            int bountyEid = store.AddEnemy(5f, 5f, 1f, 10f, 10f, 0f, 10, 99);
            store.SetEnemyBounty(bountyEid, 5.0f);
            store.EnemyActive[bountyEid] = true;
            store.AddActiveEnemyId(bountyEid);

            // Kill the bounty enemy (resolve-on-death uses the death queue, not the HP check)
            store.SetEnemyHealth(bountyEid, 0f);
            store.QueueEnemyDeath(bountyEid, 0);
            store.ResolveEnemiesKilledThisFrame();

            // Gold should be 50 (10 base × 5 bounty mult, no other multipliers active)
            Assert.Equal(50f, store.GetPlayerGold(0), 0.001f);
        }

        [Fact]
        public void Killing_NonBountyEnemy_AwardsBaseGold()
        {
            // Sanity check: a non-bounty enemy with 10 base gold awards exactly 10
            // (no bounty multiplier applied).
            var store = CreateStore();
            store.SetPlayerGold(0, 0f);

            int eid = store.AddEnemy(5f, 5f, 1f, 10f, 10f, 0f, 10, 99);
            store.EnemyActive[eid] = true;
            store.AddActiveEnemyId(eid);
            Assert.False(store.EnemyIsBounty[eid]);

            store.SetEnemyHealth(eid, 0f);
            store.QueueEnemyDeath(eid, 0);
            store.ResolveEnemiesKilledThisFrame();

            Assert.Equal(10f, store.GetPlayerGold(0), 0.001f);
        }
    }
}
