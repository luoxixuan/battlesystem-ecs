using BattleSystemECS.Tests.Infrastructure;
using Xunit;
using BattleSystemECS.Core;

namespace BattleSystemECS.Tests.Features.Enemies
{
    /// <summary>
    /// Tests for the Siege / Heavy enemy (Round 176 Direction 7) — high armor
    /// (additive +80%) + slow (0.5x speed). Verifies:
    /// 1. Default AddEnemy produces a non-siege (zero-overhead fast path)
    /// 2. SetEnemySiege configures the 3 siege fields correctly
    /// 3. armorBonus clamps to [0, 0.95], speedMult clamps to [0.1, 1.0]
    /// 4. DestroyEntity reset prevents leakage across slot reuse
    /// </summary>
    public class SiegeEnemyTests : BattleTestBase
    {
        [Fact]
        public void DefaultEnemy_SiegeFields_AreInert()
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            // All 3 siege fields should be in inert defaults — hot path fast-returns
            Assert.False(Store.EnemyIsSiege[eid]);
            Assert.Equal(0f, Store.EnemySiegeArmorBonus[eid]);
            Assert.Equal(1f, Store.EnemySiegeSpeedMult[eid]);
        }

        [Fact]
        public void SetEnemySiege_ConfiguresFields()
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            Store.SetEnemySiege(eid, armorBonus: 0.8f, speedMult: 0.5f);
            Assert.True(Store.EnemyIsSiege[eid]);
            Assert.Equal(0.8f, Store.EnemySiegeArmorBonus[eid]);
            Assert.Equal(0.5f, Store.EnemySiegeSpeedMult[eid]);
        }

        [Fact]
        public void SetEnemySiege_ClampsArmorBonus_ToValidRange()
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            // Clamp upper bound: 2.0f should clamp to 0.95f (no unkillable enemy)
            Store.SetEnemySiege(eid, 2.0f, 1.0f);
            Assert.Equal(0.95f, Store.EnemySiegeArmorBonus[eid]);
            // Clamp lower bound: -0.5f should clamp to 0f
            Store.SetEnemySiege(eid, -0.5f, 1.0f);
            Assert.Equal(0f, Store.EnemySiegeArmorBonus[eid]);
        }

        [Fact]
        public void SetEnemySiege_ClampsSpeedMult_ToValidRange()
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            // Clamp upper bound: 2.0f should clamp to 1.0f (no speed-up)
            Store.SetEnemySiege(eid, 0.8f, 2.0f);
            Assert.Equal(1.0f, Store.EnemySiegeSpeedMult[eid]);
            // Clamp lower bound: 0.0f should clamp to 0.1f (no frozen enemy)
            Store.SetEnemySiege(eid, 0.8f, 0.0f);
            Assert.Equal(0.1f, Store.EnemySiegeSpeedMult[eid]);
        }

        [Fact]
        public void SetEnemySiege_InvalidEntity_NoOp()
        {
            // 先写入合法实体的已知值作为对照，再用无效 id 调用并断言合法槽位不变。
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            Store.SetEnemySiege(eid, 0.8f, 0.5f);
            Assert.True(Store.EnemyIsSiege[eid]);
            Assert.Equal(0.8f, Store.EnemySiegeArmorBonus[eid]);
            Assert.Equal(0.5f, Store.EnemySiegeSpeedMult[eid]);

            Store.SetEnemySiege(-1, 0.9f, 0.4f);
            Store.SetEnemySiege(99999, 0.9f, 0.4f);

            Assert.True(Store.EnemyIsSiege[eid]);
            Assert.Equal(0.8f, Store.EnemySiegeArmorBonus[eid]);
            Assert.Equal(0.5f, Store.EnemySiegeSpeedMult[eid]);
        }

        [Fact]
        public void RecycleEntity_SiegeFields_AreReset()
        {
            // Critical: when an entity id is recycled (DestroyEntity + AddEnemy),
            // the siege state must NOT leak from the prior slot occupant. A
            // freshly-spawned enemy must start as a normal enemy, never inherit
            // +80% damage reduction / 50% slow from a prior slot occupant.
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            Store.SetEnemySiege(eid, 0.8f, 0.5f);
            // Now recycle: destroy then re-add at same id (ComponentStore reuses ids)
            Store.DestroyEntity(eid);
            int newEid = Store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            // Slot must be reset to inert defaults — NOT carry over old siege state
            Assert.False(Store.EnemyIsSiege[newEid]);
            Assert.Equal(0f, Store.EnemySiegeArmorBonus[newEid]);
            Assert.Equal(1f, Store.EnemySiegeSpeedMult[newEid]);
        }
    }
}