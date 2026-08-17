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
    /// Tests for Round 117 Direction 1: Per-Element Resistance (Fire / Ice / Lightning).
    /// Verifies that:
    ///   - AddEnemy seeds the three new SOA fields from constructor params
    ///   - SetElementalResist clamps inputs to [0, 1] (handles negative + >1.0)
    ///   - GetElementResist returns the correct field for each DamageType
    ///   - GetElementResist returns 0 for Physical / Magic / True (bypass)
    ///   - DestroyEntity resets all three fields to 0 (ID-reuse leakage prevention)
    ///   - Out-of-bounds enemyId returns 0 safely
    ///   - PlayerTowerAttackSystem applies Fire/Ice/Lightning damage reduction
    ///     proportional to the matching resist (e.g. 30% resist → 70% damage)
    ///   - True damage still bypasses all elemental resists
    ///   - Physical damage is unaffected by elemental resist
    ///   - EnemyDamageImmunityMask (binary) takes priority over fractional resist:
    ///     if both are set, immunity wins (damage = 0)
    /// </summary>
    public class ElementalResistanceTests : BattleTestBase
    {
        private const int PlayerId = 0;

        // ══════════════════════════════════════════════════════════════
        //  AddEnemy seeds the three new SOA fields
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void AddEnemy_SeedsAllThreeElementalResistsFromParams()
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test",
                armor: 0f, shield: 0f, magicResist: 0f,
                fireResist: 0.3f, iceResist: 0.5f, lightningResist: 0.7f);
            Assert.Equal(0.3f, Store.EnemyFireResist[eid]);
            Assert.Equal(0.5f, Store.EnemyIceResist[eid]);
            Assert.Equal(0.7f, Store.EnemyLightningResist[eid]);
        }

        [Fact]
        public void AddEnemy_DefaultsToZeroResist()
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test");
            Assert.Equal(0f, Store.EnemyFireResist[eid]);
            Assert.Equal(0f, Store.EnemyIceResist[eid]);
            Assert.Equal(0f, Store.EnemyLightningResist[eid]);
        }

        [Fact]
        public void AddEnemy_ClampsOutOfRangeInputsToUnitInterval()
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test",
                fireResist: 1.5f, iceResist: -0.3f, lightningResist: 99f);
            Assert.Equal(1f, Store.EnemyFireResist[eid]);   // 1.5 → 1
            Assert.Equal(0f, Store.EnemyIceResist[eid]);    // -0.3 → 0
            Assert.Equal(1f, Store.EnemyLightningResist[eid]); // 99 → 1
        }

        // ══════════════════════════════════════════════════════════════
        //  SetElementalResist clamps + safe accessors
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void SetElementalResist_ClampsNegativeToZero()
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test");
            Store.SetElementalResist(eid, -0.1f, -0.5f, -1f);
            Assert.Equal(0f, Store.EnemyFireResist[eid]);
            Assert.Equal(0f, Store.EnemyIceResist[eid]);
            Assert.Equal(0f, Store.EnemyLightningResist[eid]);
        }

        [Fact]
        public void SetElementalResist_ClampsAboveOneToOne()
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test");
            Store.SetElementalResist(eid, 1.1f, 2f, 9999f);
            Assert.Equal(1f, Store.EnemyFireResist[eid]);
            Assert.Equal(1f, Store.EnemyIceResist[eid]);
            Assert.Equal(1f, Store.EnemyLightningResist[eid]);
        }

        [Fact]
        public void SetElementalResist_OnInvalidEnemyId_NoOp()
        {
            // 先给合法槽位写入已知抗性，作为“越界写入不得串扰”的观察哨。
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test");
            Store.SetElementalResist(eid, 0.4f, 0.5f, 0.6f);

            // -1 与远超 MAX_ENTITIES 的 id 都是越界，应安全 no-op 且不影响合法槽位。
            Store.SetElementalResist(-1, 0.5f, 0.5f, 0.5f);
            Store.SetElementalResist(ComponentStore.MAX_ENTITIES + 5, 0.5f, 0.5f, 0.5f);

            Assert.Equal(0.4f, Store.EnemyFireResist[eid]);
            Assert.Equal(0.5f, Store.EnemyIceResist[eid]);
            Assert.Equal(0.6f, Store.EnemyLightningResist[eid]);
        }

        // ══════════════════════════════════════════════════════════════
        //  GetElementResist dispatches by DamageType
        // ══════════════════════════════════════════════════════════════

        [Theory]
        [InlineData(DamageType.Fire, 0.4f)]
        [InlineData(DamageType.Ice, 0.2f)]
        [InlineData(DamageType.Lightning, 0.1f)]
        public void GetElementResist_ElementalType_ReturnsMatchingResist(DamageType damageType, float expected)
        {
            // 三种元素类型各读各的 SOA 字段，行为同构，合并为理论驱动。
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test",
                fireResist: 0.4f, iceResist: 0.2f, lightningResist: 0.1f);
            Assert.Equal(expected, Store.GetElementResist(eid, damageType));
        }

        [Fact]
        public void GetElementResist_NonElementalTypesReturnZero()
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test",
                fireResist: 0.4f, iceResist: 0.2f, lightningResist: 0.1f);
            // True damage always bypasses; Physical/Magic do not consult elemental arrays.
            Assert.Equal(0f, Store.GetElementResist(eid, DamageType.True));
            Assert.Equal(0f, Store.GetElementResist(eid, DamageType.Physical));
            Assert.Equal(0f, Store.GetElementResist(eid, DamageType.Magic));
        }

        [Fact]
        public void GetElementResist_OutOfBoundsEnemyIdReturnsZero()
        {
            Assert.Equal(0f, Store.GetElementResist(-1, DamageType.Fire));
            Assert.Equal(0f, Store.GetElementResist(99999, DamageType.Ice));
        }

        // ══════════════════════════════════════════════════════════════
        //  DestroyEntity resets all three fields to 0 (ID-reuse safety)
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void DestroyEntity_ResetsAllElementalResistsToZero()
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test",
                fireResist: 0.8f, iceResist: 0.6f, lightningResist: 0.4f);
            Store.DestroyEntity(eid);
            Assert.Equal(0f, Store.EnemyFireResist[eid]);
            Assert.Equal(0f, Store.EnemyIceResist[eid]);
            Assert.Equal(0f, Store.EnemyLightningResist[eid]);
        }

        [Fact]
        public void DestroyEntity_FollowedByRespawn_NoStaleResist()
        {
            // ID-reuse scenario: spawn boss with high resists, destroy, respawn peon.
            // The peon must NOT inherit the boss's elemental resists.
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Boss",
                fireResist: 0.95f, iceResist: 0.95f, lightningResist: 0.95f);
            Store.DestroyEntity(eid);

            // Spawn a fresh enemy — likely reuses the same entity slot.
            int newEid = Store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Peon");
            Assert.Equal(0f, Store.EnemyFireResist[newEid]);
            Assert.Equal(0f, Store.EnemyIceResist[newEid]);
            Assert.Equal(0f, Store.EnemyLightningResist[newEid]);
        }

        // ══════════════════════════════════════════════════════════════
        //  Integration: PlayerTowerAttackSystem applies Fire/Ice/Lightning
        //  damage reduction proportional to the matching resist
        // ══════════════════════════════════════════════════════════════

        private int NewStoreWithPlayerAndEnemy(
            float attackDamage,
            float enemyMaxHp,
            float fireResist,
            float iceResist,
            float lightningResist,
            DamageType playerDamageType)
        {
            // Player at (0,0); place enemy just south at (0, 0.1) for in-range.
            Player(p =>
            {
                p.AttackRange = 10f;
                p.AttackSpeed = 1f;
                p.AttackDamage = attackDamage;
                p.Level = 1;
                p.BaseLives = 10;
            });
            Store.PlayerDamageType[PlayerId] = playerDamageType;
            int e = Enemy(e =>
            {
                e.X = 0f;
                e.Y = 0.1f;
                e.MoveSpeed = 1f;
                e.Health = enemyMaxHp;
                e.Damage = 0f;
                e.GoldReward = 1;
                e.WaveNumber = 1;
                e.Name = "Test";
                e.FireResist = fireResist;
                e.IceResist = iceResist;
                e.LightningResist = lightningResist;
            });
            return e;
        }

        private void RunOneHit(
            int enemyId,
            float attackDamage, DamageType dmgType)
        {
            var sys = new PlayerTowerAttackSystem(Store, Renderer, PlayerId, Config);
            Store.PlayerDamageType[PlayerId] = dmgType;
            sys.SetTurn(0);
            sys.Update();
        }

        [Fact]
        public void FireDamage_30PercentResist_Applies70PercentDamage()
        {
            // baseDmg=100, fireResist=0.3 → final = 100 × 0.7 = 70（浮点用带精度 Assert.Equal）
            int eid = NewStoreWithPlayerAndEnemy(
                attackDamage: 100f, enemyMaxHp: 1000f,
                fireResist: 0.3f, iceResist: 0f, lightningResist: 0f,
                playerDamageType: DamageType.Physical);
            float preHp = Store.EnemyHealth[eid];
            RunOneHit(eid, 100f, DamageType.Fire);
            float dmg = preHp - Store.EnemyHealth[eid];
            Assert.Equal(70f, dmg, 3);
        }

        [Fact]
        public void IceDamage_50PercentResist_Applies50PercentDamage()
        {
            int eid = NewStoreWithPlayerAndEnemy(
                attackDamage: 100f, enemyMaxHp: 1000f,
                fireResist: 0f, iceResist: 0.5f, lightningResist: 0f,
                playerDamageType: DamageType.Physical);
            float preHp = Store.EnemyHealth[eid];
            RunOneHit(eid, 100f, DamageType.Ice);
            float dmg = preHp - Store.EnemyHealth[eid];
            Assert.Equal(50f, dmg, 3);
        }

        [Fact]
        public void LightningDamage_70PercentResist_Applies30PercentDamage()
        {
            int eid = NewStoreWithPlayerAndEnemy(
                attackDamage: 100f, enemyMaxHp: 1000f,
                fireResist: 0f, iceResist: 0f, lightningResist: 0.7f,
                playerDamageType: DamageType.Physical);
            float preHp = Store.EnemyHealth[eid];
            RunOneHit(eid, 100f, DamageType.Lightning);
            float dmg = preHp - Store.EnemyHealth[eid];
            Assert.Equal(30f, dmg, 3);
        }

        [Fact]
        public void FireDamage_ZeroResist_AppliesFullDamage()
        {
            int eid = NewStoreWithPlayerAndEnemy(
                attackDamage: 100f, enemyMaxHp: 1000f,
                fireResist: 0f, iceResist: 0f, lightningResist: 0f,
                playerDamageType: DamageType.Physical);
            float preHp = Store.EnemyHealth[eid];
            RunOneHit(eid, 100f, DamageType.Fire);
            float dmg = preHp - Store.EnemyHealth[eid];
            Assert.Equal(100f, dmg, 3);
        }

        [Fact]
        public void FireDamage_99PercentResist_StillTakesOnePercentFloor()
        {
            // 99% resist would yield 1% damage (1 of 100). The Math.Max(0.01f, 1-0.99)=0.01 floor
            // guarantees >= 1% damage lands — even at 99.9% resist, enemy still takes 1% of base.
            int eid = NewStoreWithPlayerAndEnemy(
                attackDamage: 100f, enemyMaxHp: 1000f,
                fireResist: 0.99f, iceResist: 0f, lightningResist: 0f,
                playerDamageType: DamageType.Physical);
            float preHp = Store.EnemyHealth[eid];
            RunOneHit(eid, 100f, DamageType.Fire);
            float dmg = preHp - Store.EnemyHealth[eid];
            Assert.Equal(1f, dmg, 3);
        }

        [Fact]
        public void TrueDamage_BypassesAllElementalResists()
        {
            // 100% fire resist would normally zero out fire damage, but True damage
            // takes its own branch in TowerAttackSystem and ignores EnemyFireResist.
            int eid = NewStoreWithPlayerAndEnemy(
                attackDamage: 100f, enemyMaxHp: 1000f,
                fireResist: 1f, iceResist: 1f, lightningResist: 1f,
                playerDamageType: DamageType.Physical);
            float preHp = Store.EnemyHealth[eid];
            RunOneHit(eid, 100f, DamageType.True);
            float dmg = preHp - Store.EnemyHealth[eid];
            Assert.Equal(100f, dmg, 3);
        }

        [Fact]
        public void PhysicalDamage_IgnoresElementalResists()
        {
            // Setting high elemental resists should NOT affect Physical damage (uses armor).
            // Here armor=0 and resists=1.0, so physical damage should land at 100.
            int eid = NewStoreWithPlayerAndEnemy(
                attackDamage: 100f, enemyMaxHp: 1000f,
                fireResist: 1f, iceResist: 1f, lightningResist: 1f,
                playerDamageType: DamageType.Physical);
            float preHp = Store.EnemyHealth[eid];
            RunOneHit(eid, 100f, DamageType.Physical);
            float dmg = preHp - Store.EnemyHealth[eid];
            Assert.Equal(100f, dmg, 3);
        }

        [Fact]
        public void FireDamage_ImmunityMaskTakesPriorityOverFractionalResist()
        {
            // If the binary immunity mask has the Fire bit set, fire damage is zeroed
            // BEFORE the fractional resist branch runs. So 0.5 resist on top of immunity
            // still yields 0 damage.
            int eid = NewStoreWithPlayerAndEnemy(
                attackDamage: 100f, enemyMaxHp: 1000f,
                fireResist: 0.5f, iceResist: 0.5f, lightningResist: 0.5f,
                playerDamageType: DamageType.Physical);
            Store.EnemyDamageImmunityMask[eid] = (int)DamageType.Fire;
            float preHp = Store.EnemyHealth[eid];
            RunOneHit(eid, 100f, DamageType.Fire);
            float dmg = preHp - Store.EnemyHealth[eid];
            Assert.Equal(0f, dmg);
        }

        [Fact]
        public void FireDamage_NonMatchingImmunityMask_StillAppliesResist()
        {
            // Ice-immunity should NOT block fire damage; fire resist still applies.
            int eid = NewStoreWithPlayerAndEnemy(
                attackDamage: 100f, enemyMaxHp: 1000f,
                fireResist: 0.3f, iceResist: 0f, lightningResist: 0f,
                playerDamageType: DamageType.Physical);
            Store.EnemyDamageImmunityMask[eid] = (int)DamageType.Ice; // ice-only immunity
            float preHp = Store.EnemyHealth[eid];
            RunOneHit(eid, 100f, DamageType.Fire);
            float dmg = preHp - Store.EnemyHealth[eid];
            Assert.Equal(70f, dmg, 3);
        }

        // ══════════════════════════════════════════════════════════════
        //  MonsterConfig JSON bridge (FireResist / IceResist / LightningResist)
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void MonsterConfig_ElementalResistFields_DefaultToZero()
        {
            var cfg = new MonsterConfig();
            Assert.Equal(0f, cfg.FireResist);
            Assert.Equal(0f, cfg.IceResist);
            Assert.Equal(0f, cfg.LightningResist);
        }
    }
}
