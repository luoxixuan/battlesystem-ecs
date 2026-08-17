using BattleSystemECS.Tests.Infrastructure;
using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Mechanisms.Combat
{
    /// <summary>
    /// Tests for Round 135 Direction 1: Holy / Smite / Divine damage.
    /// Verifies that:
    ///   - AddEnemy seeds the new EnemyHolyResist SOA field from constructor param
    ///   - SetElementalResist also seeds EnemyHolyResist (4th positional arg, default 0f for back-compat)
    ///   - GetElementResist returns the correct field for DamageType.Holy
    ///   - PlayerTowerAttackSystem applies Holy damage reduction proportional to HolyResist
    ///   - True damage still bypasses Holy resist
    ///   - Physical / Fire / Ice / Lightning damage is unaffected by HolyResist
    ///   - HolyResist clamps to [0, 1] (negative → 0, >1 → 1)
    ///   - Out-of-bounds enemyId safely returns 0
    /// </summary>
    public class HolyDamageTests : BattleTestBase
    {
        private const int PlayerId = 0;
        private const float InjectedAttackDamage = 100f;

        // ══════════════════════════════════════════════════════════════
        //  EnemyHolyResist SOA field
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void AddEnemy_SeedsHolyResistFromParam()
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test",
                armor: 0f, shield: 0f, magicResist: 0f,
                fireResist: 0.3f, iceResist: 0.5f, lightningResist: 0.7f, holyResist: 0.6f);
            Assert.Equal(0.6f, Store.EnemyHolyResist[eid]);
        }

        [Fact]
        public void AddEnemy_DefaultsToZeroHolyResist()
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test");
            Assert.Equal(0f, Store.EnemyHolyResist[eid]);
        }

        [Fact]
        public void AddEnemy_ClampsHolyResistToUnitInterval()
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test",
                holyResist: 1.5f);
            Assert.Equal(1f, Store.EnemyHolyResist[eid]);
            int eid2 = Store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test2",
                holyResist: -0.3f);
            Assert.Equal(0f, Store.EnemyHolyResist[eid2]);
        }

        // ══════════════════════════════════════════════════════════════
        //  SetElementalResist (4-arg overload) seeds EnemyHolyResist
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void SetElementalResist_DefaultsHolyToZero_BackCompat()
        {
            // The 3-arg form (pre-existing) must still compile and default holy=0f
            // (backward compat for callers that didn't know about Holy yet).
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test");
            Store.SetElementalResist(eid, 0.3f, 0.5f, 0.7f);
            Assert.Equal(0.3f, Store.EnemyFireResist[eid]);
            Assert.Equal(0.5f, Store.EnemyIceResist[eid]);
            Assert.Equal(0.7f, Store.EnemyLightningResist[eid]);
            Assert.Equal(0f, Store.EnemyHolyResist[eid]);  // default
        }

        [Fact]
        public void SetElementalResist_4Arg_AppliesHoly()
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test");
            Store.SetElementalResist(eid, 0.1f, 0.2f, 0.3f, 0.4f);
            Assert.Equal(0.4f, Store.EnemyHolyResist[eid]);
        }

        [Fact]
        public void SetElementalResist_ClampsHolyInputs()
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test");
            Store.SetElementalResist(eid, 0f, 0f, 0f, -0.5f);
            Assert.Equal(0f, Store.EnemyHolyResist[eid]);
            Store.SetElementalResist(eid, 0f, 0f, 0f, 2.0f);
            Assert.Equal(1f, Store.EnemyHolyResist[eid]);
        }

        // ══════════════════════════════════════════════════════════════
        //  GetElementResist(DamageType.Holy) returns the right field
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void GetElementResist_Holy_ReturnsField()
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test",
                holyResist: 0.42f);
            Assert.Equal(0.42f, Store.GetElementResist(eid, DamageType.Holy));
        }

        [Fact]
        public void GetElementResist_OutOfBounds_ReturnsZero()
        {
            Assert.Equal(0f, Store.GetElementResist(99999, DamageType.Holy));
            Assert.Equal(0f, Store.GetElementResist(-1, DamageType.Holy));
        }

        [Fact]
        public void GetElementResist_NonElementalTypes_ReturnZero()
        {
            // True / Physical / Magic must all return 0 (bypass elemental resist).
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test",
                holyResist: 0.99f);  // even with holy resist set, non-elemental types return 0
            Assert.Equal(0f, Store.GetElementResist(eid, DamageType.Physical));
            Assert.Equal(0f, Store.GetElementResist(eid, DamageType.Magic));
            Assert.Equal(0f, Store.GetElementResist(eid, DamageType.True));
        }

        // ══════════════════════════════════════════════════════════════
        //  DamageType enum has Holy value = 64
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void DamageType_Holy_HasBitValue64()
        {
            // Bit-mask compatibility: Holy must be 64 (bit 6) so the immunity mask
            // switch in TowerAttackSystem / PlayerTowerAttackSystem continues to work.
            Assert.Equal(64, (int)DamageType.Holy);
        }

        // ══════════════════════════════════════════════════════════════
        //  PlayerTowerAttackSystem 伤害类型契约（同构用例合并）
        // ══════════════════════════════════════════════════════════════

        [Theory(DisplayName = "PlayerTowerAttackSystem 各伤害类型对圣光抗性的契约")]
        [InlineData(DamageType.Holy, 0f, 1f, false)]      // 圣光 0 抗性 → 全额
        [InlineData(DamageType.Holy, 0.5f, 0.5f, false)]  // 圣光 50% 抗性 → 半伤
        [InlineData(DamageType.True, 0.99f, 1f, false)]   // 真实伤害无视圣光抗性
        [InlineData(DamageType.Physical, 0.99f, 1f, false)] // 物理不读圣光抗性
        [InlineData(DamageType.Fire, 0.99f, 1f, false)]   // 火焰不读圣光抗性
        [InlineData(DamageType.Holy, 0.3f, 0f, true)]     // 圣光免疫位 → 全挡
        public void PlayerTowerAttack_DamageTypeContract(
            DamageType damageType, float holyResist, float expectedFraction, bool holyImmune)
        {
            Player(p =>
            {
                p.AttackRange = 10f;
                p.AttackSpeed = 1f;
                p.AttackDamage = InjectedAttackDamage;
                p.Level = 1;
                p.BaseLives = 10;
            });
            Store.PlayerDamageType[PlayerId] = damageType;
            int eid = Enemy(e =>
            {
                e.X = 0f;
                e.Y = 0.1f;
                e.MoveSpeed = 1f;
                e.Health = 1000f;
                e.Damage = 0f;
                e.GoldReward = 1;
                e.WaveNumber = 1;
                e.Name = "Target";
                e.HolyResist = holyResist;
                e.FireResist = 0f;
            });
            if (holyImmune)
            {
                Store.SetDamageImmunityMask(eid, (int)DamageType.Holy);
            }

            var sys = new PlayerTowerAttackSystem(Store, Renderer, PlayerId, Config);
            sys.SetTurn(0);
            float pre = Store.EnemyHealth[eid];
            sys.Update();

            // 期望伤害 = 显式注入的 attackDamage × 契约分数，不硬编码 100。
            float expectedDamage = InjectedAttackDamage * expectedFraction;
            Assert.Equal(pre - expectedDamage, Store.EnemyHealth[eid], 1);
        }

        // ══════════════════════════════════════════════════════════════
        //  MonsterConfig JSON parsing (HolyResist field)
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void MonsterConfig_HolyResist_DefaultsToZero()
        {
            // JSON parsing: missing HolyResist → default 0 (back-compat with old monster JSONs).
            var mc = new MonsterConfig();
            Assert.Equal(0f, mc.HolyResist);
        }

        [Fact]
        public void MonsterConfig_DamageImmunities_AcceptsHoly()
        {
            // The ComputeDamageImmunityMask switch must accept "Holy" as a valid string.
            var mc = new MonsterConfig { DamageImmunities = new System.Collections.Generic.List<string> { "Holy" } };
            int mask = mc.ComputeDamageImmunityMask();
            Assert.Equal((int)DamageType.Holy, mask);
        }
    }
}
