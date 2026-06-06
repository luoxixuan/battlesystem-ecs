using Xunit;
using BattleSystemECS.Core;

namespace BattleSystemECS.Tests
{
    /// <summary>
    /// Tests for the Siege / Heavy enemy (Round 176 Direction 7) — high armor
    /// (additive +80%) + slow (0.5x speed). Verifies:
    /// 1. Default AddEnemy produces a non-siege (zero-overhead fast path)
    /// 2. SetEnemySiege configures the 3 siege fields correctly
    /// 3. armorBonus clamps to [0, 0.95], speedMult clamps to [0.1, 1.0]
    /// 4. DestroyEntity reset prevents leakage across slot reuse
    /// </summary>
    public class SiegeEnemyTests
    {
        private ComponentStore CreateStore()
        {
            return new ComponentStore();
        }

        [Fact]
        public void DefaultEnemy_SiegeFields_AreInert()
        {
            var store = CreateStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            // All 3 siege fields should be in inert defaults — hot path fast-returns
            Assert.False(store.EnemyIsSiege[eid]);
            Assert.Equal(0f, store.EnemySiegeArmorBonus[eid]);
            Assert.Equal(1f, store.EnemySiegeSpeedMult[eid]);
        }

        [Fact]
        public void SetEnemySiege_ConfiguresFields()
        {
            var store = CreateStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            store.SetEnemySiege(eid, armorBonus: 0.8f, speedMult: 0.5f);
            Assert.True(store.EnemyIsSiege[eid]);
            Assert.Equal(0.8f, store.EnemySiegeArmorBonus[eid]);
            Assert.Equal(0.5f, store.EnemySiegeSpeedMult[eid]);
        }

        [Fact]
        public void SetEnemySiege_ClampsArmorBonus_ToValidRange()
        {
            var store = CreateStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            // Clamp upper bound: 2.0f should clamp to 0.95f (no unkillable enemy)
            store.SetEnemySiege(eid, 2.0f, 1.0f);
            Assert.Equal(0.95f, store.EnemySiegeArmorBonus[eid]);
            // Clamp lower bound: -0.5f should clamp to 0f
            store.SetEnemySiege(eid, -0.5f, 1.0f);
            Assert.Equal(0f, store.EnemySiegeArmorBonus[eid]);
        }

        [Fact]
        public void SetEnemySiege_ClampsSpeedMult_ToValidRange()
        {
            var store = CreateStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            // Clamp upper bound: 2.0f should clamp to 1.0f (no speed-up)
            store.SetEnemySiege(eid, 0.8f, 2.0f);
            Assert.Equal(1.0f, store.EnemySiegeSpeedMult[eid]);
            // Clamp lower bound: 0.0f should clamp to 0.1f (no frozen enemy)
            store.SetEnemySiege(eid, 0.8f, 0.0f);
            Assert.Equal(0.1f, store.EnemySiegeSpeedMult[eid]);
        }

        [Fact]
        public void SetEnemySiege_InvalidEntity_NoOp()
        {
            var store = CreateStore();
            // Negative entity id is invalid → silent no-op (no throw)
            store.SetEnemySiege(-1, 0.8f, 0.5f);
            // Out-of-range entity id is invalid → silent no-op
            store.SetEnemySiege(99999, 0.8f, 0.5f);
        }

        [Fact]
        public void RecycleEntity_SiegeFields_AreReset()
        {
            // Critical: when an entity id is recycled (DestroyEntity + AddEnemy),
            // the siege state must NOT leak from the prior slot occupant. A
            // freshly-spawned enemy must start as a normal enemy, never inherit
            // +80% damage reduction / 50% slow from a prior slot occupant.
            var store = CreateStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            store.SetEnemySiege(eid, 0.8f, 0.5f);
            // Now recycle: destroy then re-add at same id (ComponentStore reuses ids)
            store.DestroyEntity(eid);
            int newEid = store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            // Slot must be reset to inert defaults — NOT carry over old siege state
            Assert.False(store.EnemyIsSiege[newEid]);
            Assert.Equal(0f, store.EnemySiegeArmorBonus[newEid]);
            Assert.Equal(1f, store.EnemySiegeSpeedMult[newEid]);
        }
    }
}
