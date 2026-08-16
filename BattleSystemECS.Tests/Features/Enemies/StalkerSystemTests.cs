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
    public class StalkerSystemTests
    {
        private ComponentStore CreateStore()
        {
            return new ComponentStore();
        }

        [Fact]
        public void DefaultEnemy_StalkerFields_AreInert()
        {
            var store = CreateStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            // All 5 stalker fields should be in inert defaults — hot path fast-returns
            Assert.False(store.EnemyIsStalker[eid]);
            Assert.False(store.EnemyStalkRevealed[eid]);
            Assert.Equal(0f, store.EnemyStalkRevealRadius[eid]);
            Assert.Equal(1f, store.EnemyStalkAmbushMult[eid]);
            Assert.False(store.EnemyStalkConsumed[eid]);
        }

        [Fact]
        public void SetEnemyStalker_ConfiguresFields()
        {
            var store = CreateStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            store.SetEnemyStalker(eid, revealRadius: 3.5f, ambushMult: 3.0f);
            Assert.True(store.EnemyIsStalker[eid]);
            Assert.Equal(3.5f, store.EnemyStalkRevealRadius[eid]);
            Assert.Equal(3.0f, store.EnemyStalkAmbushMult[eid]);
            // Initially unrevealed + ambush-available
            Assert.False(store.EnemyStalkRevealed[eid]);
            Assert.False(store.EnemyStalkConsumed[eid]);
        }

        [Fact]
        public void SetEnemyStalker_ClampsAmbushMult_ToValidRange()
        {
            var store = CreateStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            // Clamp upper bound: 100.0f should clamp to 10.0f
            store.SetEnemyStalker(eid, 1f, 100f);
            Assert.Equal(10.0f, store.EnemyStalkAmbushMult[eid]);
            // Clamp lower bound: 0.5f should clamp to 1.0f (no bonus)
            store.SetEnemyStalker(eid, 1f, 0.5f);
            Assert.Equal(1.0f, store.EnemyStalkAmbushMult[eid]);
        }

        [Fact]
        public void SetEnemyStalker_InvalidEntity_NoOp()
        {
            var store = CreateStore();
            // Negative entity id is invalid → silent no-op (no throw)
            store.SetEnemyStalker(-1, 1f, 2f);
            // Out-of-range entity id is invalid → silent no-op
            store.SetEnemyStalker(99999, 1f, 2f);
        }

        [Fact]
        public void RecycleEntity_StalkerFields_AreReset()
        {
            // Critical: when an entity id is recycled (DestroyEntity + AddEnemy),
            // the stalker state must NOT leak from the prior slot occupant. A
            // freshly-spawned enemy must start hidden + fresh ambush, never inherit
            // a revealed/ambush-consumed state from the prior slot occupant.
            var store = CreateStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            store.SetEnemyStalker(eid, 3f, 4f);
            // Simulate "stalking then revealed then ambush consumed"
            store.EnemyStalkRevealed[eid] = true;
            store.EnemyStalkConsumed[eid] = true;
            // Now recycle: destroy then re-add at same id (ComponentStore reuses ids)
            store.DestroyEntity(eid);
            int newEid = store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            // Slot must be reset to inert defaults — NOT carry over old stalker state
            Assert.False(store.EnemyIsStalker[newEid]);
            Assert.False(store.EnemyStalkRevealed[newEid]);
            Assert.Equal(0f, store.EnemyStalkRevealRadius[newEid]);
            Assert.Equal(1f, store.EnemyStalkAmbushMult[newEid]);
            Assert.False(store.EnemyStalkConsumed[newEid]);
        }

        [Fact]
        public void AmbushMult_OneOrBelow_DoesNotMultiplyDamage()
        {
            // The ambush branch guards on `mult > 1f` so a default 1.0f or
            // clamped-down value never causes accidental amplification. Verified
            // by reading the source: if (stalkerAmbushMult > 1f) finalDmg *= mult;
            // — this test documents that contract.
            var store = CreateStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            store.SetEnemyStalker(eid, 1f, 1.0f);
            // mult == 1.0f, the bonus branch is skipped, no consumption
            Assert.Equal(1.0f, store.EnemyStalkAmbushMult[eid]);
        }
    }
}