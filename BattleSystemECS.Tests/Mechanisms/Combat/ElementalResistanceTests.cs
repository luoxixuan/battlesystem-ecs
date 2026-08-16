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
    public class ElementalResistanceTests
    {
        private const int PlayerId = 0;

        private static ComponentStore NewStore()
        {
            return new ComponentStore();
        }

        // ══════════════════════════════════════════════════════════════
        //  AddEnemy seeds the three new SOA fields
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void AddEnemy_SeedsAllThreeElementalResistsFromParams()
        {
            var store = NewStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test",
                armor: 0f, shield: 0f, magicResist: 0f,
                fireResist: 0.3f, iceResist: 0.5f, lightningResist: 0.7f);
            Assert.Equal(0.3f, store.EnemyFireResist[eid]);
            Assert.Equal(0.5f, store.EnemyIceResist[eid]);
            Assert.Equal(0.7f, store.EnemyLightningResist[eid]);
        }

        [Fact]
        public void AddEnemy_DefaultsToZeroResist()
        {
            var store = NewStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test");
            Assert.Equal(0f, store.EnemyFireResist[eid]);
            Assert.Equal(0f, store.EnemyIceResist[eid]);
            Assert.Equal(0f, store.EnemyLightningResist[eid]);
        }

        [Fact]
        public void AddEnemy_ClampsOutOfRangeInputsToUnitInterval()
        {
            var store = NewStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test",
                fireResist: 1.5f, iceResist: -0.3f, lightningResist: 99f);
            Assert.Equal(1f, store.EnemyFireResist[eid]);   // 1.5 → 1
            Assert.Equal(0f, store.EnemyIceResist[eid]);    // -0.3 → 0
            Assert.Equal(1f, store.EnemyLightningResist[eid]); // 99 → 1
        }

        // ══════════════════════════════════════════════════════════════
        //  SetElementalResist clamps + safe accessors
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void SetElementalResist_ClampsNegativeToZero()
        {
            var store = NewStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test");
            store.SetElementalResist(eid, -0.1f, -0.5f, -1f);
            Assert.Equal(0f, store.EnemyFireResist[eid]);
            Assert.Equal(0f, store.EnemyIceResist[eid]);
            Assert.Equal(0f, store.EnemyLightningResist[eid]);
        }

        [Fact]
        public void SetElementalResist_ClampsAboveOneToOne()
        {
            var store = NewStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test");
            store.SetElementalResist(eid, 1.1f, 2f, 9999f);
            Assert.Equal(1f, store.EnemyFireResist[eid]);
            Assert.Equal(1f, store.EnemyIceResist[eid]);
            Assert.Equal(1f, store.EnemyLightningResist[eid]);
        }

        [Fact]
        public void SetElementalResist_OnInvalidEnemyId_NoOp()
        {
            var store = NewStore();
            // -1 is out of bounds. Should not throw and should not corrupt other slots.
            store.SetElementalResist(-1, 0.5f, 0.5f, 0.5f);
            // 99999 is also out of bounds.
            store.SetElementalResist(99999, 0.5f, 0.5f, 0.5f);
            // No assertion needed — just verifying no throw / no crash.
        }

        // ══════════════════════════════════════════════════════════════
        //  GetElementResist dispatches by DamageType
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void GetElementResist_FireReturnsFireResist()
        {
            var store = NewStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test",
                fireResist: 0.4f, iceResist: 0.2f, lightningResist: 0.1f);
            Assert.Equal(0.4f, store.GetElementResist(eid, DamageType.Fire));
        }

        [Fact]
        public void GetElementResist_IceReturnsIceResist()
        {
            var store = NewStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test",
                fireResist: 0.4f, iceResist: 0.2f, lightningResist: 0.1f);
            Assert.Equal(0.2f, store.GetElementResist(eid, DamageType.Ice));
        }

        [Fact]
        public void GetElementResist_LightningReturnsLightningResist()
        {
            var store = NewStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test",
                fireResist: 0.4f, iceResist: 0.2f, lightningResist: 0.1f);
            Assert.Equal(0.1f, store.GetElementResist(eid, DamageType.Lightning));
        }

        [Fact]
        public void GetElementResist_NonElementalTypesReturnZero()
        {
            var store = NewStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test",
                fireResist: 0.4f, iceResist: 0.2f, lightningResist: 0.1f);
            // True damage always bypasses; Physical/Magic do not consult elemental arrays.
            Assert.Equal(0f, store.GetElementResist(eid, DamageType.True));
            Assert.Equal(0f, store.GetElementResist(eid, DamageType.Physical));
            Assert.Equal(0f, store.GetElementResist(eid, DamageType.Magic));
        }

        [Fact]
        public void GetElementResist_OutOfBoundsEnemyIdReturnsZero()
        {
            var store = NewStore();
            Assert.Equal(0f, store.GetElementResist(-1, DamageType.Fire));
            Assert.Equal(0f, store.GetElementResist(99999, DamageType.Ice));
        }

        // ══════════════════════════════════════════════════════════════
        //  DestroyEntity resets all three fields to 0 (ID-reuse safety)
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void DestroyEntity_ResetsAllElementalResistsToZero()
        {
            var store = NewStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test",
                fireResist: 0.8f, iceResist: 0.6f, lightningResist: 0.4f);
            store.DestroyEntity(eid);
            Assert.Equal(0f, store.EnemyFireResist[eid]);
            Assert.Equal(0f, store.EnemyIceResist[eid]);
            Assert.Equal(0f, store.EnemyLightningResist[eid]);
        }

        [Fact]
        public void DestroyEntity_FollowedByRespawn_NoStaleResist()
        {
            // ID-reuse scenario: spawn boss with high resists, destroy, respawn peon.
            // The peon must NOT inherit the boss's elemental resists.
            var store = NewStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Boss",
                fireResist: 0.95f, iceResist: 0.95f, lightningResist: 0.95f);
            store.DestroyEntity(eid);

            // Spawn a fresh enemy — likely reuses the same entity slot.
            int newEid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Peon");
            Assert.Equal(0f, store.EnemyFireResist[newEid]);
            Assert.Equal(0f, store.EnemyIceResist[newEid]);
            Assert.Equal(0f, store.EnemyLightningResist[newEid]);
        }

        // ══════════════════════════════════════════════════════════════
        //  Integration: PlayerTowerAttackSystem applies Fire/Ice/Lightning
        //  damage reduction proportional to the matching resist
        // ══════════════════════════════════════════════════════════════

        private static (ComponentStore store, int enemyId) NewStoreWithPlayerAndEnemy(
            float attackDamage,
            float enemyMaxHp,
            float fireResist,
            float iceResist,
            float lightningResist,
            DamageType playerDamageType)
        {
            var store = NewStore();
            // Player at (0,0); place enemy just south at (0, 0.1) for in-range.
            store.AddPlayer(0, 10f, 1f, attackDamage, 1, 10);
            store.PlayerDamageType[PlayerId] = playerDamageType;
            int e = store.AddEnemy(0, 0.1f, 1f, enemyMaxHp, enemyMaxHp, 0f, 1, 1, "Test",
                fireResist: fireResist, iceResist: iceResist, lightningResist: lightningResist);
            return (store, e);
        }

        private static void RunOneHit(
            ComponentStore store, int enemyId,
            float attackDamage, DamageType dmgType)
        {
            var renderer = new MockRenderer();
            var cfg = new GameConfig();
            var sys = new PlayerTowerAttackSystem(store, renderer, PlayerId, cfg);
            store.PlayerDamageType[PlayerId] = dmgType;
            sys.SetTurn(0);
            sys.Update();
        }

        [Fact]
        public void FireDamage_30PercentResist_Applies70PercentDamage()
        {
            // baseDmg=100, fireResist=0.3 → final = 100 × 0.7 = 70 (allow tiny float error)
            var (store, eid) = NewStoreWithPlayerAndEnemy(
                attackDamage: 100f, enemyMaxHp: 1000f,
                fireResist: 0.3f, iceResist: 0f, lightningResist: 0f,
                playerDamageType: DamageType.Physical);
            float preHp = store.EnemyHealth[eid];
            RunOneHit(store, eid, 100f, DamageType.Fire);
            float dmg = preHp - store.EnemyHealth[eid];
            Assert.True(dmg >= 69f && dmg <= 71f, $"Expected ~70 fire damage, got {dmg}");
        }

        [Fact]
        public void IceDamage_50PercentResist_Applies50PercentDamage()
        {
            var (store, eid) = NewStoreWithPlayerAndEnemy(
                attackDamage: 100f, enemyMaxHp: 1000f,
                fireResist: 0f, iceResist: 0.5f, lightningResist: 0f,
                playerDamageType: DamageType.Physical);
            float preHp = store.EnemyHealth[eid];
            RunOneHit(store, eid, 100f, DamageType.Ice);
            float dmg = preHp - store.EnemyHealth[eid];
            Assert.True(dmg >= 49f && dmg <= 51f, $"Expected ~50 ice damage, got {dmg}");
        }

        [Fact]
        public void LightningDamage_70PercentResist_Applies30PercentDamage()
        {
            var (store, eid) = NewStoreWithPlayerAndEnemy(
                attackDamage: 100f, enemyMaxHp: 1000f,
                fireResist: 0f, iceResist: 0f, lightningResist: 0.7f,
                playerDamageType: DamageType.Physical);
            float preHp = store.EnemyHealth[eid];
            RunOneHit(store, eid, 100f, DamageType.Lightning);
            float dmg = preHp - store.EnemyHealth[eid];
            Assert.True(dmg >= 29f && dmg <= 31f, $"Expected ~30 lightning damage, got {dmg}");
        }

        [Fact]
        public void FireDamage_ZeroResist_AppliesFullDamage()
        {
            var (store, eid) = NewStoreWithPlayerAndEnemy(
                attackDamage: 100f, enemyMaxHp: 1000f,
                fireResist: 0f, iceResist: 0f, lightningResist: 0f,
                playerDamageType: DamageType.Physical);
            float preHp = store.EnemyHealth[eid];
            RunOneHit(store, eid, 100f, DamageType.Fire);
            float dmg = preHp - store.EnemyHealth[eid];
            Assert.True(dmg >= 99f && dmg <= 101f, $"Expected ~100 fire damage, got {dmg}");
        }

        [Fact]
        public void FireDamage_99PercentResist_StillTakesOnePercentFloor()
        {
            // 99% resist would yield 1% damage (1 of 100). The Math.Max(0.01f, 1-0.99)=0.01 floor
            // guarantees >= 1% damage lands — even at 99.9% resist, enemy still takes 1% of base.
            var (store, eid) = NewStoreWithPlayerAndEnemy(
                attackDamage: 100f, enemyMaxHp: 1000f,
                fireResist: 0.99f, iceResist: 0f, lightningResist: 0f,
                playerDamageType: DamageType.Physical);
            float preHp = store.EnemyHealth[eid];
            RunOneHit(store, eid, 100f, DamageType.Fire);
            float dmg = preHp - store.EnemyHealth[eid];
            Assert.True(dmg >= 0.9f && dmg <= 1.1f, $"Expected ~1 fire damage (1% floor), got {dmg}");
        }

        [Fact]
        public void TrueDamage_BypassesAllElementalResists()
        {
            // 100% fire resist would normally zero out fire damage, but True damage
            // takes its own branch in TowerAttackSystem and ignores EnemyFireResist.
            var (store, eid) = NewStoreWithPlayerAndEnemy(
                attackDamage: 100f, enemyMaxHp: 1000f,
                fireResist: 1f, iceResist: 1f, lightningResist: 1f,
                playerDamageType: DamageType.Physical);
            float preHp = store.EnemyHealth[eid];
            RunOneHit(store, eid, 100f, DamageType.True);
            float dmg = preHp - store.EnemyHealth[eid];
            Assert.True(dmg >= 99f && dmg <= 101f, $"Expected ~100 true damage, got {dmg}");
        }

        [Fact]
        public void PhysicalDamage_IgnoresElementalResists()
        {
            // Setting high elemental resists should NOT affect Physical damage (uses armor).
            // Here armor=0 and resists=1.0, so physical damage should land at 100.
            var (store, eid) = NewStoreWithPlayerAndEnemy(
                attackDamage: 100f, enemyMaxHp: 1000f,
                fireResist: 1f, iceResist: 1f, lightningResist: 1f,
                playerDamageType: DamageType.Physical);
            float preHp = store.EnemyHealth[eid];
            RunOneHit(store, eid, 100f, DamageType.Physical);
            float dmg = preHp - store.EnemyHealth[eid];
            Assert.True(dmg >= 99f && dmg <= 101f, $"Expected ~100 physical damage, got {dmg}");
        }

        [Fact]
        public void FireDamage_ImmunityMaskTakesPriorityOverFractionalResist()
        {
            // If the binary immunity mask has the Fire bit set, fire damage is zeroed
            // BEFORE the fractional resist branch runs. So 0.5 resist on top of immunity
            // still yields 0 damage.
            var (store, eid) = NewStoreWithPlayerAndEnemy(
                attackDamage: 100f, enemyMaxHp: 1000f,
                fireResist: 0.5f, iceResist: 0.5f, lightningResist: 0.5f,
                playerDamageType: DamageType.Physical);
            store.EnemyDamageImmunityMask[eid] = (int)DamageType.Fire;
            float preHp = store.EnemyHealth[eid];
            RunOneHit(store, eid, 100f, DamageType.Fire);
            float dmg = preHp - store.EnemyHealth[eid];
            Assert.Equal(0f, dmg);
        }

        [Fact]
        public void FireDamage_NonMatchingImmunityMask_StillAppliesResist()
        {
            // Ice-immunity should NOT block fire damage; fire resist still applies.
            var (store, eid) = NewStoreWithPlayerAndEnemy(
                attackDamage: 100f, enemyMaxHp: 1000f,
                fireResist: 0.3f, iceResist: 0f, lightningResist: 0f,
                playerDamageType: DamageType.Physical);
            store.EnemyDamageImmunityMask[eid] = (int)DamageType.Ice; // ice-only immunity
            float preHp = store.EnemyHealth[eid];
            RunOneHit(store, eid, 100f, DamageType.Fire);
            float dmg = preHp - store.EnemyHealth[eid];
            Assert.True(dmg >= 69f && dmg <= 71f, $"Expected ~70 fire damage, got {dmg}");
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
