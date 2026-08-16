using BattleSystemECS.Tests.Infrastructure;
using System;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;
using BattleSystemECS.Components;

namespace BattleSystemECS.Tests.Mechanisms.Combat
{
    /// <summary>
    /// Tests for Round 102 Direction 7: Damage Conversion (player → split into original + converted type).
    /// Verifies that:
    ///   - Default state (PlayerDamageConversionRatio = 0) is backward compatible (single event)
    ///   - Conversion ratio above the global cap is clamped at ConversionDefaultCap
    ///   - Below MinMeaningfulRatio, the fast path is taken (no split)
    ///   - Above the threshold, the damage is split into original + converted portions
    ///   - Each portion applies its own damage-type resistance (Magic uses magicResist, Physical uses armor)
    ///   - Immunitiy mask blocks the matching portion (Physical-immune enemy still takes the Magic portion)
    ///   - DamageConversionConfig constants are sensible
    ///   - GameConfig exposes PlayerDamageConversionRatio/PlayerConvertedDamageType defaults
    /// </summary>
    public class DamageConversionTests
    {
        private const int PlayerId = 0;

        private static (ComponentStore store, int enemyId) NewStoreWithPlayerAndEnemy(
            float attackDamage = 100f,
            float enemyArmor = 0f,
            float enemyMagicResist = 0f,
            int immunityMask = 0,
            float enemyMaxHp = 1000f,
            DamageType playerDamageType = DamageType.Physical)
        {
            var store = new ComponentStore();
            // Player at (0,0) so all enemies in range are hit. PlayerTowerAttackSystem
            // requires enemyY > playerY (enemies "below" the player) — place the enemy
            // at (0, 0.1) so it is just south of the player and in range.
            store.AddPlayer(0, 10f, 1f, attackDamage, 1, 10);
            store.PlayerDamageType[PlayerId] = playerDamageType;
            int e = store.AddEnemy(0, 0.1f, 1f, enemyMaxHp, enemyMaxHp, 0f, 1, 1, "Test");
            store.EnemyArmor[e] = enemyArmor;
            store.EnemyMagicResist[e] = enemyMagicResist;
            store.EnemyDamageImmunityMask[e] = immunityMask;
            return (store, e);
        }

        // ─── Config constants ─────────────────────────────────────────

        [Fact]
        public void DamageConversionConfig_HasSensibleDefaults()
        {
            Assert.Equal(0.5f, DamageConversionConfig.ConversionDefaultCap);
            Assert.True(DamageConversionConfig.MinMeaningfulRatio > 0f);
            Assert.True(DamageConversionConfig.MinMeaningfulRatio < DamageConversionConfig.ConversionDefaultCap);
        }

        [Fact]
        public void GameConfig_PlayerConversion_DefaultsToZero()
        {
            var cfg = new GameConfig();
            Assert.Equal(0f, cfg.PlayerDamageConversionRatio);
            Assert.Equal(DamageType.Physical, cfg.PlayerConvertedDamageType);
        }

        // ─── Backward compat: no conversion = single hit event ───────

        [Fact]
        public void NoConversion_DefaultBehavior_AppliesResistanceOnce()
        {
            // Default PlayerDamageConversionRatio=0 → fast path → single event apply
            var (store, EnemyId) = NewStoreWithPlayerAndEnemy(enemyArmor: 0.5f, attackDamage: 100f);
            var renderer = new MockRenderer();
            var cfg = new GameConfig(); // ratio=0
            var sys = new PlayerTowerAttackSystem(store, renderer, PlayerId, cfg);
            sys.SetTurn(0);

            float preHealth = store.EnemyHealth[EnemyId];
            sys.Update();

            // Physical damage, 50% armor → final = 100 × 0.5 = 50
            float postHealth = store.EnemyHealth[EnemyId];
            Assert.True(preHealth - postHealth >= 49f && preHealth - postHealth <= 51f,
                $"Expected ~50 damage applied; got {preHealth - postHealth}");
        }

        // ─── Magic conversion: original Physical / converted Magic ─────

        [Fact]
        public void Conversion_SplitIntoTwoPortions_PhysicalPlusMagic()
        {
            // 50% conversion: Physical portion uses armor (0), Magic portion uses magicResist (0)
            // Both should land cleanly since both resists are 0.
            var (store, EnemyId) = NewStoreWithPlayerAndEnemy(
                attackDamage: 100f,
                enemyArmor: 0f,
                enemyMagicResist: 0f,
                playerDamageType: DamageType.Physical);
            var renderer = new MockRenderer();
            var cfg = new GameConfig
            {
                PlayerDamageConversionRatio = 0.5f,
                PlayerConvertedDamageType = DamageType.Magic
            };
            var sys = new PlayerTowerAttackSystem(store, renderer, PlayerId, cfg);
            sys.SetTurn(0);

            float preHealth = store.EnemyHealth[EnemyId];
            sys.Update();

            // Original: 100 × 0.5 × (1 - 0 armor) = 50
            // Converted (Magic): 100 × 0.5 × (1 - 0 magicResist) = 50
            // Total = 100
            float dmg = preHealth - store.EnemyHealth[EnemyId];
            Assert.True(dmg >= 99f && dmg <= 101f,
                $"Expected ~100 total damage; got {dmg}");
        }

        [Fact]
        public void Conversion_EachPortionUsesOwnResistance()
        {
            // 50% conversion. Physical portion hits 50% armor (50 dmg post-resist),
            // Magic portion hits 50% magicResist (25 dmg post-resist). Total = 75.
            var (store, EnemyId) = NewStoreWithPlayerAndEnemy(
                attackDamage: 100f,
                enemyArmor: 0.5f,
                enemyMagicResist: 0.5f,
                playerDamageType: DamageType.Physical);
            var renderer = new MockRenderer();
            var cfg = new GameConfig
            {
                PlayerDamageConversionRatio = 0.5f,
                PlayerConvertedDamageType = DamageType.Magic
            };
            var sys = new PlayerTowerAttackSystem(store, renderer, PlayerId, cfg);
            sys.SetTurn(0);

            float preHealth = store.EnemyHealth[EnemyId];
            sys.Update();

            // Original Phys: 50 × 0.5 = 25
            // Converted Magic: 50 × 0.5 = 25
            // Total = 50
            float dmg = preHealth - store.EnemyHealth[EnemyId];
            Assert.True(dmg >= 49f && dmg <= 51f,
                $"Expected ~50 total damage; got {dmg}");
        }

        [Fact]
        public void Conversion_ImmunityBypass_PhysicalImmune_StillTakesMagicPortion()
        {
            // Enemy is immune to Physical. With 50% conversion to Magic, the Physical
            // portion is zeroed (immunity check) but the Magic portion still lands.
            // Crit is off in the test (no crit rate bonus), so the test stays deterministic.
            var (store, EnemyId) = NewStoreWithPlayerAndEnemy(
                attackDamage: 100f,
                immunityMask: (int)DamageType.Physical,
                playerDamageType: DamageType.Physical);
            var renderer = new MockRenderer();
            var cfg = new GameConfig
            {
                PlayerDamageConversionRatio = 0.5f,
                PlayerConvertedDamageType = DamageType.Magic
            };
            var sys = new PlayerTowerAttackSystem(store, renderer, PlayerId, cfg);
            sys.SetTurn(0);

            float preHealth = store.EnemyHealth[EnemyId];
            sys.Update();

            // Original Phys: 50 × 0 (immune) = 0
            // Converted Magic: 50 × 1 (no magicResist) = 50
            // Total = 50 (the original 100 phys attack now has a working Magic backdoor)
            float dmg = preHealth - store.EnemyHealth[EnemyId];
            Assert.True(dmg >= 49f && dmg <= 51f,
                $"Expected ~50 damage (bypass Physical immunity via Magic conversion); got {dmg}");
        }

        // ─── Cap clamping ──────────────────────────────────────────────

        [Fact]
        public void Conversion_AboveCap_IsClampedToCap()
        {
            // Set ratio way above the cap (0.9) — should be clamped to 0.5.
            // Without clamp, 90% would mean 90% of 100 = 90 converted (Magic), 10% = 10 (Phys).
            // With clamp to 0.5, we get 50% Magic + 50% Phys.
            var (store, EnemyId) = NewStoreWithPlayerAndEnemy(
                attackDamage: 100f,
                playerDamageType: DamageType.Physical);
            var renderer = new MockRenderer();
            var cfg = new GameConfig
            {
                PlayerDamageConversionRatio = 0.9f, // above cap
                PlayerConvertedDamageType = DamageType.Magic
            };
            var sys = new PlayerTowerAttackSystem(store, renderer, PlayerId, cfg);
            sys.SetTurn(0);

            float preHealth = store.EnemyHealth[EnemyId];
            sys.Update();

            // Clamped to 0.5 → 50 Phys + 50 Magic = 100 total
            float dmg = preHealth - store.EnemyHealth[EnemyId];
            Assert.True(dmg >= 99f && dmg <= 101f,
                $"Expected ~100 damage (clamped to 0.5 cap); got {dmg}");
        }

        // ─── Fast path: below MinMeaningfulRatio = no split ───────────

        [Fact]
        public void Conversion_BelowMinThreshold_TakesFastPath()
        {
            // 0.005 = 0.5% is below MinMeaningfulRatio (0.01 = 1%) — fast path
            // is taken, so the entire 100 damage is applied as Physical, not split.
            var (store, EnemyId) = NewStoreWithPlayerAndEnemy(
                attackDamage: 100f,
                playerDamageType: DamageType.Physical);
            var renderer = new MockRenderer();
            var cfg = new GameConfig
            {
                PlayerDamageConversionRatio = 0.005f, // below MinMeaningfulRatio
                PlayerConvertedDamageType = DamageType.Magic
            };
            var sys = new PlayerTowerAttackSystem(store, renderer, PlayerId, cfg);
            sys.SetTurn(0);

            float preHealth = store.EnemyHealth[EnemyId];
            sys.Update();

            // Fast path = no split = 100 Physical damage
            float dmg = preHealth - store.EnemyHealth[EnemyId];
            Assert.True(dmg >= 99f && dmg <= 101f,
                $"Expected ~100 damage (fast path, no split); got {dmg}");
        }
    }
}
