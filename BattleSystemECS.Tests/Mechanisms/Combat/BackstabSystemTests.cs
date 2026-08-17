using BattleSystemECS.Tests.Infrastructure;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Mechanisms.Combat
{
    /// <summary>
    /// Round 174 Direction 4 — Backstab positional damage bonus tests.
    /// 覆盖：
    ///   - PlaceTower 的 0 → 1.0x 哨兵解析与全局默认角度写入（真实放置路径）
    ///   - SetGameConfig 缓存的主开关通过真实 TowerAttackSystem 攻击路径验证：
    ///     开启时后方塔获得倍率伤害，关闭时即使塔配置了倍率也只造成基础伤害
    ///   - 槽位回收后 backstab 字段重置（不残留幻影倍率）
    ///   - 配置默认值为相对不变量（不钉具体数值）
    /// </summary>
    public class BackstabSystemTests : BattleTestBase
    {
        // ── 1: PlaceTower 默认（无 per-tower backstab 字段）解析为 1.0x 惰性 ──
        [Fact]
        public void PlaceTower_DefaultConfig_ResolvesToInert1_0x()
        {
            var sys = new TowerPlacementSystem(Store, Renderer, Config);
            int towerId = sys.PlaceTower(1, 1, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(towerId >= 0);
            // 哨兵 0 → 1.0x（惰性快路径）。这是关键 bug-fix 契约：
            // 旧逻辑会把每座非 rogue 塔静默变成 2.0x。
            Assert.Equal(1.0f, Store.TowerBackstabDamageMult[towerId]);
        }

        // ── 2: PlaceTower 把角度哨兵解析为全局默认角度 ──
        [Fact]
        public void PlaceTower_DefaultAngle_InheritsGlobalDefault()
        {
            Config.Backstab.DefaultAngleDeg = 120f;
            var sys = new TowerPlacementSystem(Store, Renderer, Config);
            int towerId = sys.PlaceTower(2, 2, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(towerId >= 0);
            // PlaceTower 写入解析后的全局默认角度，后续打开倍率时代码可直接读取。
            Assert.Equal(120f, Store.TowerBackstabAngleDeg[towerId]);
        }

        // ── 3: SetGameConfig 主开关通过真实攻击路径可观测 ──
        // 塔在敌人正后方（塔 (0,0)，敌人在 (0,1) 且朝 +Y 移动），
        // dot(塔→敌人, 敌人朝向) = 1 > cos(90°) → 满足后方判定。
        [Theory]
        [InlineData(false, 50f)] // 开关关闭：即使塔倍率 2.0x，也只打基础伤害
        [InlineData(true, 100f)] // 开关开启：后方命中 ×2.0
        public void SetGameConfig_BackstabSwitch_DrivesRealDamage(bool enabled, float expectedDamage)
        {
            // AddTower 已自动注册 ActiveTowerIds；再 AddActiveTowerId 会造成重复开火。
            int towerId = RawTower(0, 0, TowerType.Basic, 50f, 10, 10f, 1, 50f);
            Store.TowerBackstabDamageMult[towerId] = 2.0f;
            Store.TowerBackstabAngleDeg[towerId] = 90f;

            int enemyId = Enemy(e =>
            {
                e.X = 0f;
                e.Y = 1f;
                e.MoveSpeed = 1f;
                e.Health = 1000f;
                e.Damage = 0f;
                e.GoldReward = 1;
                e.Name = "BackstabTarget";
            });
            Store.EnemyMoveDirX[enemyId] = 0f;
            Store.EnemyMoveDirY[enemyId] = 1f; // 朝 +Y 移动，塔位于其正后方
            RebuildGrid();

            Config.Backstab = new BackstabConfig { Enabled = enabled };
            var attack = new TowerAttackSystem(Store, Renderer);
            attack.SetGameConfig(Config);
            attack.SetTurn(0);
            attack.Update(1f);

            float damageDealt = 1000f - Store.EnemyHealth[enemyId];
            Assert.Equal(expectedDamage, damageDealt, 3);
        }

        // ── 4: 槽位回收后 backstab 字段重置为 1.0x（无幻影 rogue 残留） ──
        [Fact]
        public void DestroyEntity_RecycledSlot_BackstabFieldsReset()
        {
            var sys = new TowerPlacementSystem(Store, Renderer, Config);

            // 放置后直接注入 3.0x，模拟未来升级路径写入的 rogue 配置。
            int id1 = sys.PlaceTower(1, 1, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(id1 >= 0);
            Store.TowerBackstabDamageMult[id1] = 3.0f;
            Store.TowerBackstabAngleDeg[id1] = 60f;
            Assert.Equal(3.0f, Store.TowerBackstabDamageMult[id1]);

            // 销毁后槽位回收，新塔不得继承 3.0x 幻影倍率。
            Store.DestroyEntity(id1);
            int id2 = sys.PlaceTower(2, 2, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(id2 >= 0);
            Assert.Equal(1.0f, Store.TowerBackstabDamageMult[id2]);
        }

        // ── 5: BackstabConfig 默认值为相对不变量（不钉具体数值） ──
        [Fact]
        public void BackstabConfig_Defaults_AreSaneRelativeInvariants()
        {
            var b = new BackstabConfig();
            Assert.True(b.Enabled);
            Assert.True(b.DefaultDamageMult >= 1f);
            Assert.True(b.DefaultAngleDeg > 0f && b.DefaultAngleDeg <= 180f);

            // GameConfig 默认必须带一个可用实例（生产路径不再判空失败）。
            Assert.NotNull(Config.Backstab);
            Assert.True(Config.Backstab.Enabled);
        }

        // ── 6: Store 字段数组按 MAX_ENTITIES 分配（防越界） ──
        [Fact]
        public void Store_BackstabArrays_SizedForMaxEntities()
        {
            Assert.NotNull(Store.TowerBackstabDamageMult);
            Assert.NotNull(Store.TowerBackstabAngleDeg);
            Assert.Equal(ComponentStore.MAX_ENTITIES, Store.TowerBackstabDamageMult.Length);
            Assert.Equal(ComponentStore.MAX_ENTITIES, Store.TowerBackstabAngleDeg.Length);
        }

        // ── 7: 多座塔连续放置都保持 1.0x 惰性 ──
        [Fact]
        public void PlaceTower_MultipleTowers_AllInertByDefault()
        {
            var sys = new TowerPlacementSystem(Store, Renderer, Config);
            for (int i = 0; i < 5; i++)
            {
                int towerId = sys.PlaceTower(i, 0, TowerType.Basic, 50f, 3, 1f, 50f);
                Assert.True(towerId >= 0);
                Assert.Equal(1.0f, Store.TowerBackstabDamageMult[towerId]);
            }
        }
    }
}
