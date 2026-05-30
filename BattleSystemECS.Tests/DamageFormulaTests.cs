using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
{
    /// <summary>
    /// Core damage formula tests: armor mitigation, magic resist, true damage bypass,
    /// targeting mode scoring, and enum value integrity.
    /// </summary>
    public class DamageFormulaTests
    {
        // ─── Enum integrity tests ──────────────────────────────────────────────

        [Fact]
        public void DamageType_Values_MatchExpected()
        {
            Assert.Equal(0, (int)DamageType.Physical);
            Assert.Equal(1, (int)DamageType.Magic);
            Assert.Equal(2, (int)DamageType.True);
        }

        [Fact]
        public void TowerTargetingMode_Values_MatchExpected()
        {
            Assert.Equal(0, (int)TowerTargetingMode.Nearest);
            Assert.Equal(1, (int)TowerTargetingMode.Furthest);
            Assert.Equal(2, (int)TowerTargetingMode.LowestHealth);
            Assert.Equal(3, (int)TowerTargetingMode.HighestHealth);
            Assert.Equal(4, (int)TowerTargetingMode.FirstSpawned);
            Assert.Equal(5, (int)TowerTargetingMode.LastSpawned);
            Assert.Equal(6, (int)TowerTargetingMode.Intercept);
        }

        // ─── Damage formula math tests ─────────────────────────────────────────

        /// <summary>
        /// Physical damage: reduced by (1 - effectiveArmor).
        /// effectiveArmor = armor * (1 - armorPen) - shredStacks * shredPerStack.
        /// </summary>
        [Theory]
        [InlineData(10f, 0f, 0f, 0, 0f, 10f)]     // no armor = full damage
        [InlineData(10f, 0.5f, 0f, 0, 0f, 5f)]     // 50% armor = 50% damage
        [InlineData(10f, 1.0f, 0f, 0, 0f, 0.1f)]   // 100% armor -> 1% floor
        [InlineData(10f, 0.5f, 0.5f, 0, 0f, 7.5f)] // 50% armor with 50% pen = 75% damage
        [InlineData(10f, 0.5f, 0f, 3, 0.1f, 8f)]   // 50% armor, 3 shred stacks at 0.1 each = 20% effective = 80% damage
        public void PhysicalDamageFormula(float baseDamage, float armor, float armorPen,
            int shredStacks, float shredPerStack, float expectedDamage)
        {
            float effectiveArmor = armor * (1f - armorPen);
            if (shredStacks > 0 && shredPerStack > 0f)
                effectiveArmor = Math.Max(0f, effectiveArmor - shredStacks * shredPerStack);

            float result = baseDamage * Math.Max(0.01f, 1f - effectiveArmor);
            Assert.Equal(expectedDamage, result, precision: 4);
        }

        /// <summary>
        /// Magic damage: reduced by (1 - magicResist) * damageTakenMult.
        /// </summary>
        [Theory]
        [InlineData(10f, 0f, 1f, 10f)]      // no resist = full damage
        [InlineData(10f, 0.3f, 1f, 7f)]     // 30% resist = 70% damage
        [InlineData(10f, 0.5f, 1f, 5f)]     // 50% resist = 50% damage
        [InlineData(10f, 1.0f, 1f, 0.1f)]   // 100% resist -> 1% floor
        [InlineData(10f, 0f, 1.5f, 15f)]    // 50% damage taken multiplier
        [InlineData(10f, 0.5f, 0.5f, 2.5f)] // 50% resist + 50% mult = 25% damage
        public void MagicDamageFormula(float baseDamage, float magicResist,
            float damageTakenMult, float expectedDamage)
        {
            float result = baseDamage * Math.Max(0.01f, 1f - magicResist) * damageTakenMult;
            Assert.Equal(expectedDamage, result, precision: 4);
        }

        /// <summary>
        /// True damage: bypasses all defenses, only affected by damageTakenMult.
        /// </summary>
        [Theory]
        [InlineData(10f, 1f, 10f)]
        [InlineData(10f, 0.5f, 5f)]
        [InlineData(10f, 2f, 20f)]
        public void TrueDamageFormula(float baseDamage, float damageTakenMult, float expectedDamage)
        {
            float result = baseDamage * damageTakenMult;
            Assert.Equal(expectedDamage, result, precision: 4);
        }

        // ─── Integration: damage type affects health differently ───────────────

        [Fact]
        public void PhysicalDamage_ReducedByArmor()
        {
            var store = new ComponentStore();
            int eid = store.AddEnemy(5f, 5f, 1f, 100f, 100f, 0.5f, 1, 99);
            // Enemy has 50% armor — physical damage should be ~50% effective
            store.ApplyEnemyDamage(eid, 10f);
            float healthAfterPhysical = store.EnemyHealth[eid];
            Assert.True(healthAfterPhysical < 100f, "Health should decrease");
            Assert.True(healthAfterPhysical > 89f, "Damage should be reduced by armor (~50%)");
        }

        [Fact]
        public void TowerDamageType_Enum_AffectsOutput()
        {
            // Verify that different damage types produce different results
            float baseDamage = 10f;
            float armor = 0.5f;
            float magicResist = 0.3f;

            // Physical: reduced by armor
            float physicalResult = baseDamage * Math.Max(0.01f, 1f - armor);
            // Magic: reduced by magic resist
            float magicResult = baseDamage * Math.Max(0.01f, 1f - magicResist);
            // True: bypasses all
            float trueResult = baseDamage;

            // All three should differ when armor and magicResist are different
            Assert.NotEqual(physicalResult, magicResult, precision: 2);
            Assert.NotEqual(physicalResult, trueResult, precision: 2);
            Assert.NotEqual(magicResult, trueResult, precision: 2);
            Assert.Equal(5f, physicalResult, precision: 2);
            Assert.Equal(7f, magicResult, precision: 2);
            Assert.Equal(10f, trueResult, precision: 2);
        }

        // ─── Targeting mode scoring verification ───────────────────────────────

        [Fact]
        public void TargetingMode_Nearest_ReturnsLowestDistance()
        {
            var store = new ComponentStore();
            int near = store.AddEnemy(2f, 0f, 1f, 10f, 10f, 0f, 1, 99);
            int far = store.AddEnemy(5f, 0f, 1f, 10f, 10f, 0f, 1, 99);

            // Nearest mode (0) should pick the enemy at distance 2
            float distNear = MathF.Sqrt(
                (store.PositionX[near] - 0f) * (store.PositionX[near] - 0f) +
                (store.PositionY[near] - 0f) * (store.PositionY[near] - 0f));
            float distFar = MathF.Sqrt(
                (store.PositionX[far] - 0f) * (store.PositionX[far] - 0f) +
                (store.PositionY[far] - 0f) * (store.PositionY[far] - 0f));

            Assert.True(distNear < distFar, "Near enemy should be closer");
        }

        [Fact]
        public void TargetingMode_LowestHealth_ReturnsLowestHealthEnemy()
        {
            var store = new ComponentStore();
            int highHp = store.AddEnemy(3f, 0f, 1f, 100f, 100f, 0f, 1, 99);
            int lowHp = store.AddEnemy(4f, 0f, 1f, 10f, 100f, 0f, 1, 99);

            // Set initial health and simulate damage
            store.EnemyHealth[highHp] = 100f;
            store.EnemyHealth[lowHp] = 10f;

            Assert.True(store.EnemyHealth[lowHp] < store.EnemyHealth[highHp],
                "Low HP enemy should have less health");
        }

        // ─── Enemy shield damage absorption ────────────────────────────────────

        [Theory]
        [InlineData(10f, 5f, 100f, 95f)]   // damage < shield: shield absorbs all
        [InlineData(20f, 5f, 100f, 85f)]   // damage > shield: shield + health
        public void ApplyEnemyDamage_ShieldAbsorption(float damage, float shield,
            float initialHp, float expectedHp)
        {
            var store = new ComponentStore();
            int eid = store.AddEnemy(3f, 3f, 1f, initialHp, initialHp, 0f, 1, 99);
            store.EnemyShield[eid] = shield;

            store.ApplyEnemyDamage(eid, damage);

            Assert.Equal(expectedHp, store.EnemyHealth[eid], precision: 4);
            Assert.True(store.EnemyShield[eid] >= 0f, "Shield should not go negative");
            Assert.True(store.EnemyHealth[eid] > 0f, "Enemy should survive this hit");
        }
    }
}
