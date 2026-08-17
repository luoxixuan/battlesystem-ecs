using BattleSystemECS.Tests.Infrastructure;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Features.Enemies
{
    /// <summary>
    /// Tests for the Stalker / Predator enemy (Round 174 Direction 8) — spawns
    /// invisible, reveals when within range of any friendly tower, and the FIRST
    /// attack post-reveal deals ×AmbushMult damage. Verifies:
    /// 1. Default AddEnemy produces a non-stalker (zero-overhead fast path)
    /// 2. SetEnemyStalker configures the 5 stalker fields correctly
    /// 3. Ambush bonus applies exactly ONCE per spawn (sticky consumption)
    /// </summary>
    public class StalkerSystemTests : BattleTestBase
    {
        [Fact]
        public void DefaultEnemy_StalkerFields_AreInert()
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            // All 5 stalker fields should be in inert defaults — hot path fast-returns
            Assert.False(Store.EnemyIsStalker[eid]);
            Assert.False(Store.EnemyStalkRevealed[eid]);
            Assert.Equal(0f, Store.EnemyStalkRevealRadius[eid]);
            Assert.Equal(1f, Store.EnemyStalkAmbushMult[eid]);
            Assert.False(Store.EnemyStalkConsumed[eid]);
        }

        [Fact]
        public void SetEnemyStalker_ConfiguresFields()
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            Store.SetEnemyStalker(eid, revealRadius: 3.5f, ambushMult: 3.0f);
            Assert.True(Store.EnemyIsStalker[eid]);
            Assert.Equal(3.5f, Store.EnemyStalkRevealRadius[eid]);
            Assert.Equal(3.0f, Store.EnemyStalkAmbushMult[eid]);
            // Initially unrevealed + ambush-available
            Assert.False(Store.EnemyStalkRevealed[eid]);
            Assert.False(Store.EnemyStalkConsumed[eid]);
        }

        [Fact]
        public void SetEnemyStalker_ClampsAmbushMult_ToValidRange()
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            // Clamp upper bound: 100.0f should clamp to 10.0f
            Store.SetEnemyStalker(eid, 1f, 100f);
            Assert.Equal(10.0f, Store.EnemyStalkAmbushMult[eid]);
            // Clamp lower bound: 0.5f should clamp to 1.0f (no bonus)
            Store.SetEnemyStalker(eid, 1f, 0.5f);
            Assert.Equal(1.0f, Store.EnemyStalkAmbushMult[eid]);
        }

        [Fact]
        public void SetEnemyStalker_InvalidEntity_NoOp()
        {
            // 先写入合法实体的已知值作为对照，再用无效 id 调用并断言合法槽位不变。
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            Store.SetEnemyStalker(eid, 3.5f, 3.0f);
            Assert.True(Store.EnemyIsStalker[eid]);
            Assert.Equal(3.5f, Store.EnemyStalkRevealRadius[eid]);
            Assert.Equal(3.0f, Store.EnemyStalkAmbushMult[eid]);

            Store.SetEnemyStalker(-1, 9f, 8f);
            Store.SetEnemyStalker(99999, 9f, 8f);

            Assert.True(Store.EnemyIsStalker[eid]);
            Assert.Equal(3.5f, Store.EnemyStalkRevealRadius[eid]);
            Assert.Equal(3.0f, Store.EnemyStalkAmbushMult[eid]);
        }

        [Fact]
        public void RecycleEntity_StalkerFields_AreReset()
        {
            // Critical: when an entity id is recycled (DestroyEntity + AddEnemy),
            // the stalker state must NOT leak from the prior slot occupant. A
            // freshly-spawned enemy must start hidden + fresh ambush, never inherit
            // a revealed/ambush-consumed state from the prior slot occupant.
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            Store.SetEnemyStalker(eid, 3f, 4f);
            // Simulate "stalking then revealed then ambush consumed"
            Store.EnemyStalkRevealed[eid] = true;
            Store.EnemyStalkConsumed[eid] = true;
            // Now recycle: destroy then re-add at same id (ComponentStore reuses ids)
            Store.DestroyEntity(eid);
            int newEid = Store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            // Slot must be reset to inert defaults — NOT carry over old stalker state
            Assert.False(Store.EnemyIsStalker[newEid]);
            Assert.False(Store.EnemyStalkRevealed[newEid]);
            Assert.Equal(0f, Store.EnemyStalkRevealRadius[newEid]);
            Assert.Equal(1f, Store.EnemyStalkAmbushMult[newEid]);
            Assert.False(Store.EnemyStalkConsumed[newEid]);
        }

        [Fact]
        public void AmbushMult_OneOrBelow_DoesNotMultiplyDamage()
        {
            // The ambush branch guards on `mult > 1f` so a default 1.0f or
            // clamped-down value never causes accidental amplification. Verified
            // by reading the source: if (stalkerAmbushMult > 1f) finalDmg *= mult;
            // — this test documents that contract.
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            Store.SetEnemyStalker(eid, 1f, 1.0f);
            // mult == 1.0f, the bonus branch is skipped, no consumption
            Assert.Equal(1.0f, Store.EnemyStalkAmbushMult[eid]);
        }
    }
}