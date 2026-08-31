using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;
using BattleSystemECS.Tests.Infrastructure;

namespace BattleSystemECS.Tests.Mechanisms.Combat
{
    /// <summary>
    /// Tests for Round 101 Direction 10: Mana Drain (tower → enemy).
    /// 吸蓝语义一律走 TowerAttackSystem 真实攻击路径验证：
    /// 放塔 → RebuildSpatialGrid → SetTurn → Update，再断言法力变化。
    /// 测试内不再复刻吸蓝公式、不做字段写读回环。
    /// </summary>
    public class ManaDrainSystemTests : BattleTestBase
    {
        private const float PlayerMaxMana = 500f;

        /// <summary>真实攻击一次，返回 (attack, towerId, enemyId)。</summary>
        private (TowerAttackSystem attack, int towerId, int enemyId) AttackWithDrain(
            float drainPct, float towerCap, float enemyMaxMana, float enemyCurrentMana)
        {
            int playerId = Store.PlayerEntityId;
            if (!Store.GetEntityHandle(playerId).IsValid)
                Store.AddPlayer(playerId, 1f, 1f, 10f, 1);
            Store.PlayerMaxMana[playerId] = PlayerMaxMana;
            Store.PlayerMana[playerId] = 0f;

            int towerId = Tower(0, 0, TowerType.Basic, t =>
            {
                t.Damage = 10f;
                t.Range = 10;
                t.Speed = 10f;
            });
            Store.TowerManaDrainPct[towerId] = drainPct;
            Store.TowerManaDrainCap[towerId] = towerCap; // 0 = 使用全局 cap

            int enemyId = Enemy(e => { e.Name = "ManaWielder"; e.Health = 1000f; });
            Store.PositionX[enemyId] = 0f;
            Store.PositionY[enemyId] = 1f;
            Store.EnemyMaxMana[enemyId] = enemyMaxMana;
            Store.EnemyCurrentMana[enemyId] = enemyCurrentMana;

            Store.RebuildSpatialGrid();
            var attack = new TowerAttackSystem(Store, Renderer);
            attack.SetTurn(0);
            attack.Update(1f);
            return (attack, towerId, enemyId);
        }

        // ─── Default state — backward compat ─────────────────────────────

        [Fact]
        public void DefaultState_AllManaFieldsZero()
        {
            int e = Enemy();
            Assert.Equal(0f, Store.EnemyMaxMana[e]);
            Assert.Equal(0f, Store.EnemyCurrentMana[e]);
        }

        [Fact]
        public void DefaultState_NewComponentStore_TowerDrainZero()
        {
            for (int i = 0; i < 10; i++)
            {
                Assert.Equal(0f, Store.TowerManaDrainPct[i]);
                Assert.Equal(0f, Store.TowerManaDrainCap[i]);
            }
        }

        [Fact]
        public void ManaDrainConfig_HasSensibleDefaults()
        {
            Assert.True(ManaDrainConfig.DefaultManaDrainPct > 0f);
            Assert.True(ManaDrainConfig.ManaDrainCap > 0f);
            Assert.Equal(0f, ManaDrainConfig.DefaultEnemyMaxMana);
        }

        // ─── 真实攻击路径：吸蓝与 cap ────────────────────────────────────

        [Fact]
        public void Attack_DrainsPercentOfCurrentMana()
        {
            // 注入 pct=0.5、当前法力 60：真实攻击后按当前值吸取 30。
            var (_, _, enemyId) = AttackWithDrain(drainPct: 0.5f, towerCap: 0f,
                enemyMaxMana: 200f, enemyCurrentMana: 60f);
            int playerId = Store.PlayerEntityId;

            Assert.Equal(30f, Store.EnemyCurrentMana[enemyId], 3);
            Assert.Equal(30f, Store.PlayerMana[playerId], 3);
            // finalDmg > 0 真实命中（10 点塔伤已落地），吸蓝随命中发生。
            Assert.Equal(990f, Store.EnemyHealth[enemyId], 3);
        }

        [Fact]
        public void Attack_RespectsGlobalCap()
        {
            // 塔 cap=0 → 使用全局 ManaDrainCap；pct=1 时吸取量恰好等于全局 cap。
            float expectedCap = ManaDrainConfig.ManaDrainCap;
            var (_, _, enemyId) = AttackWithDrain(drainPct: 1f, towerCap: 0f,
                enemyMaxMana: 1000f, enemyCurrentMana: 1000f);
            int playerId = Store.PlayerEntityId;

            Assert.Equal(expectedCap, 1000f - Store.EnemyCurrentMana[enemyId], 3);
            Assert.Equal(expectedCap, Store.PlayerMana[playerId], 3);
        }

        [Fact]
        public void Attack_PerTowerCapOverridesGlobal()
        {
            // 注入 towerCap=25（小于全局 cap），吸取量被每塔 cap 封顶。
            var (_, _, enemyId) = AttackWithDrain(drainPct: 1f, towerCap: 25f,
                enemyMaxMana: 1000f, enemyCurrentMana: 1000f);
            int playerId = Store.PlayerEntityId;

            Assert.Equal(975f, Store.EnemyCurrentMana[enemyId], 3);
            Assert.Equal(25f, Store.PlayerMana[playerId], 3);
        }

        [Fact]
        public void Attack_PlayerManaClampedToMax()
        {
            int playerId = Store.PlayerEntityId;
            if (!Store.GetEntityHandle(playerId).IsValid)
                Store.AddPlayer(playerId, 1f, 1f, 10f, 1);
            Store.PlayerMaxMana[playerId] = PlayerMaxMana;
            Store.PlayerMana[playerId] = PlayerMaxMana - 5f; // 几乎满蓝

            int towerId = Tower(0, 0, TowerType.Basic, t =>
            {
                t.Damage = 10f;
                t.Range = 10;
                t.Speed = 10f;
            });
            Store.TowerManaDrainPct[towerId] = 1f;
            Store.TowerManaDrainCap[towerId] = 20f;

            int enemyId = Enemy(e => { e.Name = "ManaWielder"; e.Health = 1000f; });
            Store.PositionX[enemyId] = 0f;
            Store.PositionY[enemyId] = 1f;
            Store.EnemyMaxMana[enemyId] = 200f;
            Store.EnemyCurrentMana[enemyId] = 200f;

            Store.RebuildSpatialGrid();
            var attack = new TowerAttackSystem(Store, Renderer);
            attack.SetTurn(0);
            attack.Update(1f);

            // 495 + 20 = 515 → 钳制到 500；敌人仍完整损失 20。
            Assert.Equal(PlayerMaxMana, Store.PlayerMana[playerId], 3);
            Assert.Equal(180f, Store.EnemyCurrentMana[enemyId], 3);
        }

        // ─── 零法力边界（真实路径，非纯字段读取） ───────────────────────

        [Fact]
        public void Attack_ZeroCurrentMana_NoDrainButDamageStillLands()
        {
            var (_, _, enemyId) = AttackWithDrain(drainPct: 0.5f, towerCap: 0f,
                enemyMaxMana: 200f, enemyCurrentMana: 0f);
            int playerId = Store.PlayerEntityId;

            Assert.Equal(0f, Store.EnemyCurrentMana[enemyId]);
            Assert.Equal(0f, Store.PlayerMana[playerId]);
            // 同一帧塔伤真实落地，证明攻击路径已执行而吸蓝被守卫跳过。
            Assert.Equal(990f, Store.EnemyHealth[enemyId], 3);
        }

        [Fact]
        public void Attack_ZeroMaxMana_NoDrainButDamageStillLands()
        {
            var (_, _, enemyId) = AttackWithDrain(drainPct: 0.5f, towerCap: 0f,
                enemyMaxMana: 0f, enemyCurrentMana: 0f);
            int playerId = Store.PlayerEntityId;

            Assert.Equal(0f, Store.EnemyCurrentMana[enemyId]);
            Assert.Equal(0f, Store.PlayerMana[playerId]);
            // 同一帧塔伤真实落地，证明攻击路径已执行而吸蓝被守卫跳过。
            Assert.Equal(990f, Store.EnemyHealth[enemyId], 3);
        }

        // ─── ID-reuse safety ────────────────────────────────────────────

        [Fact]
        public void DestroyEntity_ResetsEnemyManaFields()
        {
            int e = Enemy();
            Store.EnemyMaxMana[e] = 500f;
            Store.EnemyCurrentMana[e] = 250f;
            Store.DestroyEntity(e);
            Assert.Equal(0f, Store.EnemyMaxMana[e]);
            Assert.Equal(0f, Store.EnemyCurrentMana[e]);
        }
    }
}
