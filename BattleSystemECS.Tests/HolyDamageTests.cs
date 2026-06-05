using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
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
    public class HolyDamageTests
    {
        private const int PlayerId = 0;

        private static ComponentStore NewStore()
        {
            return new ComponentStore();
        }

        // ══════════════════════════════════════════════════════════════
        //  EnemyHolyResist SOA field
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void AddEnemy_SeedsHolyResistFromParam()
        {
            var store = NewStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test",
                armor: 0f, shield: 0f, magicResist: 0f,
                fireResist: 0.3f, iceResist: 0.5f, lightningResist: 0.7f, holyResist: 0.6f);
            Assert.Equal(0.6f, store.EnemyHolyResist[eid]);
        }

        [Fact]
        public void AddEnemy_DefaultsToZeroHolyResist()
        {
            var store = NewStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test");
            Assert.Equal(0f, store.EnemyHolyResist[eid]);
        }

        [Fact]
        public void AddEnemy_ClampsHolyResistToUnitInterval()
        {
            var store = NewStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test",
                holyResist: 1.5f);
            Assert.Equal(1f, store.EnemyHolyResist[eid]);
            int eid2 = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test2",
                holyResist: -0.3f);
            Assert.Equal(0f, store.EnemyHolyResist[eid2]);
        }

        // ══════════════════════════════════════════════════════════════
        //  SetElementalResist (4-arg overload) seeds EnemyHolyResist
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void SetElementalResist_DefaultsHolyToZero_BackCompat()
        {
            // The 3-arg form (pre-existing) must still compile and default holy=0f
            // (backward compat for callers that didn't know about Holy yet).
            var store = NewStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test");
            store.SetElementalResist(eid, 0.3f, 0.5f, 0.7f);
            Assert.Equal(0.3f, store.EnemyFireResist[eid]);
            Assert.Equal(0.5f, store.EnemyIceResist[eid]);
            Assert.Equal(0.7f, store.EnemyLightningResist[eid]);
            Assert.Equal(0f, store.EnemyHolyResist[eid]);  // default
        }

        [Fact]
        public void SetElementalResist_4Arg_AppliesHoly()
        {
            var store = NewStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test");
            store.SetElementalResist(eid, 0.1f, 0.2f, 0.3f, 0.4f);
            Assert.Equal(0.4f, store.EnemyHolyResist[eid]);
        }

        [Fact]
        public void SetElementalResist_ClampsHolyInputs()
        {
            var store = NewStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test");
            store.SetElementalResist(eid, 0f, 0f, 0f, -0.5f);
            Assert.Equal(0f, store.EnemyHolyResist[eid]);
            store.SetElementalResist(eid, 0f, 0f, 0f, 2.0f);
            Assert.Equal(1f, store.EnemyHolyResist[eid]);
        }

        // ══════════════════════════════════════════════════════════════
        //  GetElementResist(DamageType.Holy) returns the right field
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void GetElementResist_Holy_ReturnsField()
        {
            var store = NewStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test",
                holyResist: 0.42f);
            Assert.Equal(0.42f, store.GetElementResist(eid, DamageType.Holy));
        }

        [Fact]
        public void GetElementResist_OutOfBounds_ReturnsZero()
        {
            var store = NewStore();
            Assert.Equal(0f, store.GetElementResist(99999, DamageType.Holy));
            Assert.Equal(0f, store.GetElementResist(-1, DamageType.Holy));
        }

        [Fact]
        public void GetElementResist_NonElementalTypes_ReturnZero()
        {
            // True / Physical / Magic must all return 0 (bypass elemental resist).
            var store = NewStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 0f, 1, 1, "Test",
                holyResist: 0.99f);  // even with holy resist set, non-elemental types return 0
            Assert.Equal(0f, store.GetElementResist(eid, DamageType.Physical));
            Assert.Equal(0f, store.GetElementResist(eid, DamageType.Magic));
            Assert.Equal(0f, store.GetElementResist(eid, DamageType.True));
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
        //  PlayerTowerAttackSystem Holy damage branch
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void PlayerTowerAttack_HolyDamage_FullDamageAtZeroResist()
        {
            // 0% Holy resist → 100% damage taken.
            var store = NewStore();
            store.AddPlayer(PlayerId, 10f, 1f, 100f, 1, 10);
            store.PlayerDamageType[PlayerId] = DamageType.Holy;
            int eid = store.AddEnemy(0f, 0.1f, 1f, 1000f, 1000f, 0f, 1, 1, "Undead0",
                holyResist: 0f);
            var renderer = new MockRenderer();
            var cfg = new GameConfig();
            var sys = new PlayerTowerAttackSystem(store, renderer, PlayerId, cfg);
            sys.SetTurn(0);
            float pre = store.EnemyHealth[eid];
            sys.Update();
            // 100 dmg * 1.0 (0% resist) = 100 dmg
            Assert.Equal(100f, pre - store.EnemyHealth[eid], 1);
        }

        [Fact]
        public void PlayerTowerAttack_HolyDamage_ReducedByHolyResist()
        {
            // 50% Holy resist → 50% damage taken.
            var store = NewStore();
            store.AddPlayer(PlayerId, 10f, 1f, 100f, 1, 10);
            store.PlayerDamageType[PlayerId] = DamageType.Holy;
            int eid = store.AddEnemy(0f, 0.1f, 1f, 1000f, 1000f, 0f, 1, 1, "Demon0",
                holyResist: 0.5f);
            var renderer = new MockRenderer();
            var cfg = new GameConfig();
            var sys = new PlayerTowerAttackSystem(store, renderer, PlayerId, cfg);
            sys.SetTurn(0);
            float pre = store.EnemyHealth[eid];
            sys.Update();
            // 100 * (1 - 0.5) = 50 dmg
            Assert.Equal(50f, pre - store.EnemyHealth[eid], 1);
        }

        [Fact]
        public void PlayerTowerAttack_TrueDamage_BypassesHolyResist()
        {
            // True damage ignores HolyResist (consistent with all other resists).
            var store = NewStore();
            store.AddPlayer(PlayerId, 10f, 1f, 100f, 1, 10);
            store.PlayerDamageType[PlayerId] = DamageType.True;
            int eid = store.AddEnemy(0f, 0.1f, 1f, 1000f, 1000f, 0f, 1, 1, "HighHolyResist",
                holyResist: 0.99f);
            var renderer = new MockRenderer();
            var cfg = new GameConfig();
            var sys = new PlayerTowerAttackSystem(store, renderer, PlayerId, cfg);
            sys.SetTurn(0);
            float pre = store.EnemyHealth[eid];
            sys.Update();
            Assert.Equal(100f, pre - store.EnemyHealth[eid], 1);  // full 100 dmg
        }

        [Fact]
        public void PlayerTowerAttack_PhysicalDamage_UnaffectedByHolyResist()
        {
            // Physical damage doesn't consult EnemyHolyResist.
            var store = NewStore();
            store.AddPlayer(PlayerId, 10f, 1f, 100f, 1, 10);
            store.PlayerDamageType[PlayerId] = DamageType.Physical;
            int eid = store.AddEnemy(0f, 0.1f, 1f, 1000f, 1000f, 0f, 1, 1, "Test",
                holyResist: 0.99f);  // would block 99% of Holy, but Physical ignores it
            var renderer = new MockRenderer();
            var cfg = new GameConfig();
            var sys = new PlayerTowerAttackSystem(store, renderer, PlayerId, cfg);
            sys.SetTurn(0);
            float pre = store.EnemyHealth[eid];
            sys.Update();
            // Physical uses armor formula; with armor=0, full 100 dmg applied.
            Assert.Equal(100f, pre - store.EnemyHealth[eid], 1);
        }

        [Fact]
        public void PlayerTowerAttack_FireDamage_UnaffectedByHolyResist()
        {
            // Fire damage only consults EnemyFireResist, not HolyResist.
            var store = NewStore();
            store.AddPlayer(PlayerId, 10f, 1f, 100f, 1, 10);
            store.PlayerDamageType[PlayerId] = DamageType.Fire;
            int eid = store.AddEnemy(0f, 0.1f, 1f, 1000f, 1000f, 0f, 1, 1, "Test",
                holyResist: 0.99f, fireResist: 0f);
            var renderer = new MockRenderer();
            var cfg = new GameConfig();
            var sys = new PlayerTowerAttackSystem(store, renderer, PlayerId, cfg);
            sys.SetTurn(0);
            float pre = store.EnemyHealth[eid];
            sys.Update();
            Assert.Equal(100f, pre - store.EnemyHealth[eid], 1);
        }

        [Fact]
        public void PlayerTowerAttack_HolyImmunity_BlocksAllHolyDamage()
        {
            // EnemyDamageImmunityMask with Holy bit → 0 damage (binary immunity wins over fractional).
            var store = NewStore();
            store.AddPlayer(PlayerId, 10f, 1f, 100f, 1, 10);
            store.PlayerDamageType[PlayerId] = DamageType.Holy;
            int eid = store.AddEnemy(0f, 0.1f, 1f, 1000f, 1000f, 0f, 1, 1, "HolyBoss");
            store.SetDamageImmunityMask(eid, (int)DamageType.Holy);
            store.EnemyHolyResist[eid] = 0.3f;  // would reduce by 30%, but immunity wins
            var renderer = new MockRenderer();
            var cfg = new GameConfig();
            var sys = new PlayerTowerAttackSystem(store, renderer, PlayerId, cfg);
            sys.SetTurn(0);
            float pre = store.EnemyHealth[eid];
            sys.Update();
            Assert.Equal(1000f, store.EnemyHealth[eid]);  // no damage
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
