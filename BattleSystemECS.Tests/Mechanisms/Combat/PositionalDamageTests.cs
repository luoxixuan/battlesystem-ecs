using BattleSystemECS.Tests.Infrastructure;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Mechanisms.Combat
{
    /// <summary>
    /// 全局朝向伤害层（game_config.json "PositionalDamage" 段，默认关）—— 真实攻击路径测试。
    /// 坐标/朝向约定与 BackstabSystemTests 一致：敌人面朝 = EnemyMoveDirX/Y；
    /// dot = (塔→敌 单位向量)·(敌面朝单位向量)。
    /// 测试注入 B=120/F=60：背刺锥 dot &gt; cos(60°)=0.5；侧袭带 dot &gt; cos(90°)=0 且未达背刺阈值。
    /// 塔基础伤害 50：预期伤害 50（无加成）/ 75（侧袭 ×1.5）/ 100（背刺 ×2.0）均由注入值推导。
    /// </summary>
    public class PositionalDamageTests : BattleTestBase
    {
        private static PositionalDamageConfig EnabledConfig() => new PositionalDamageConfig
        {
            Enabled = true,
            BackstabAngleDegrees = 120f,
            FlankAngleDegrees = 60f,
            BackstabDamageMultiplier = 2f,
            FlankDamageMultiplier = 1.5f,
        };

        /// <summary>放置 (0,0) 塔 + 指定位置/朝向的敌人，驱动一次真实塔攻击，返回造成的伤害。</summary>
        private float AttackOnceAndGetDamage(PositionalDamageConfig pos, float enemyX, float enemyY, float dirX, float dirY)
        {
            int towerId = RawTower(0, 0, TowerType.Basic, 50f, 10, 10f, 1, 50f);
            Assert.True(towerId >= 0);

            int enemyId = Enemy(e =>
            {
                e.X = enemyX;
                e.Y = enemyY;
                e.MoveSpeed = 1f;
                e.Health = 1000f;
                e.Damage = 0f;
                e.GoldReward = 1;
                e.Name = "PositionalTarget";
            });
            Store.EnemyMoveDirX[enemyId] = dirX;
            Store.EnemyMoveDirY[enemyId] = dirY;
            RebuildGrid();

            Config.PositionalDamage = pos;
            // 隔离每塔 opt-in 的 Round 174 backstab 层（本层与其相互独立）
            Config.Backstab = new BackstabConfig { Enabled = false };

            var attack = new TowerAttackSystem(Store, Renderer);
            attack.SetGameConfig(Config);
            attack.SetTurn(0);
            attack.Update(1f);

            return 1000f - Store.EnemyHealth[enemyId];
        }

        // 场景：敌人位于 (0,1) 朝 +Y（塔在其正后方，dot=1 → 背刺锥）；
        //       (3,1) 朝 +Y（dot=1/√10≈0.316 → 侧袭带）；(0,1) 朝 -Y（面向塔，dot=-1 → 正面）。
        // 注意塔索敌有方向性（敌人须在塔上方 Y>塔Y），正面场景用"朝向塔的敌人"而非"塔下方的敌人"。
        [Theory(DisplayName = "朝向伤害层：背刺锥/侧袭带/正面/关闭/非法配置")]
        [InlineData("disabled", 0f, 1f, 0f, 1f, 50f)]   // Enabled=false：正后方也只有基础伤害
        [InlineData("backstab", 0f, 1f, 0f, 1f, 100f)]  // 背刺锥内 ×2.0
        [InlineData("flank", 3f, 1f, 0f, 1f, 75f)]      // 侧袭带 ×1.5
        [InlineData("front", 0f, 1f, 0f, -1f, 50f)]     // 正面（敌人面向塔）无加成
        [InlineData("invalid", 0f, 1f, 0f, 1f, 50f)]    // 角度非法（B=200>180）→ 视为关闭
        public void PositionalDamage_AppliesOrientationMultiplier(string mode, float ex, float ey, float dirX, float dirY, float expected)
        {
            PositionalDamageConfig pos = mode switch
            {
                "disabled" => new PositionalDamageConfig { Enabled = false, BackstabAngleDegrees = 120f, FlankAngleDegrees = 60f, BackstabDamageMultiplier = 2f, FlankDamageMultiplier = 1.5f },
                "backstab" => EnabledConfig(),
                "flank" => EnabledConfig(),
                "front" => EnabledConfig(),
                "invalid" => new PositionalDamageConfig { Enabled = true, BackstabAngleDegrees = 200f, FlankAngleDegrees = 60f, BackstabDamageMultiplier = 2f, FlankDamageMultiplier = 1.5f },
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知场景"),
            };

            float damage = AttackOnceAndGetDamage(pos, ex, ey, dirX, dirY);
            Assert.Equal(expected, damage, 3);
        }

        // 与 Round 174 每塔 opt-in backstab 层的独立性：本层关闭时，per-tower 倍率仍按自身开关生效。
        [Fact]
        public void PositionalDamage_IndependentFromPerTowerBackstabLayer()
        {
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
                e.Name = "LayerIsolation";
            });
            Store.EnemyMoveDirX[enemyId] = 0f;
            Store.EnemyMoveDirY[enemyId] = 1f;
            RebuildGrid();

            Config.PositionalDamage = new PositionalDamageConfig(); // 默认关
            Config.Backstab = new BackstabConfig { Enabled = true };

            var attack = new TowerAttackSystem(Store, Renderer);
            attack.SetGameConfig(Config);
            attack.SetTurn(0);
            attack.Update(1f);

            // 仅 per-tower 层生效：50 × 2.0 = 100（若两层叠加会变成 200）
            Assert.Equal(100f, 1000f - Store.EnemyHealth[enemyId], 3);
        }
    }
}
