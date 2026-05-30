using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;

namespace BattleSystemECS.Tests
{
    /// <summary>
    /// Core damage formula tests — armor, magic resist, true damage, shield absorption.
    /// </summary>
    public class DamageFormulaTests
    {
        [Fact]
        public void ApplyEnemyDamage_DirectDamage_ReducesHealth()
        {
            var store = new ComponentStore();
            int eid = store.CreateEntity();
            store.EnemyActive[eid] = true;
            store.EnemyHealth[eid] = 100f;
            store.EnemyShield[eid] = 0f;

            store.ApplyEnemyDamage(eid, 30f);
            Assert.Equal(70f, store.EnemyHealth[eid]);
        }

        [Fact]
        public void ApplyEnemyDamage_ShieldAbsorbsFirst()
        {
            var store = new ComponentStore();
            int eid = store.CreateEntity();
            store.EnemyActive[eid] = true;
            store.EnemyHealth[eid] = 100f;
            store.EnemyShield[eid] = 25f;

            store.ApplyEnemyDamage(eid, 20f);
            // Shield absorbs all: shield=5, health unchanged
            Assert.Equal(100f, store.EnemyHealth[eid]);
            Assert.Equal(5f, store.EnemyShield[eid]);
        }

        [Fact]
        public void ApplyEnemyDamage_ShieldPartial()
        {
            var store = new ComponentStore();
            int eid = store.CreateEntity();
            store.EnemyActive[eid] = true;
            store.EnemyHealth[eid] = 100f;
            store.EnemyShield[eid] = 10f;

            store.ApplyEnemyDamage(eid, 40f);
            // Shield absorbs 10, remaining 30 hits health
            Assert.Equal(70f, store.EnemyHealth[eid]);
            Assert.Equal(0f, store.EnemyShield[eid]);
        }

        [Fact]
        public void ApplyEnemyDamage_ZeroDamage_NoEffect()
        {
            var store = new ComponentStore();
            int eid = store.CreateEntity();
            store.EnemyActive[eid] = true;
            store.EnemyHealth[eid] = 100f;
            store.EnemyShield[eid] = 50f;

            store.ApplyEnemyDamage(eid, 0f);
            Assert.Equal(100f, store.EnemyHealth[eid]);
            Assert.Equal(50f, store.EnemyShield[eid]);
        }

        [Fact]
        public void ApplyEnemyDamage_NegativeDamage_Ignored()
        {
            var store = new ComponentStore();
            int eid = store.CreateEntity();
            store.EnemyActive[eid] = true;
            store.EnemyHealth[eid] = 100f;

            store.ApplyEnemyDamage(eid, -10f);
            Assert.Equal(100f, store.EnemyHealth[eid]);
        }

        [Fact]
        public void ApplyEnemyDamage_ExactKill()
        {
            var store = new ComponentStore();
            int eid = store.CreateEntity();
            store.EnemyActive[eid] = true;
            store.EnemyHealth[eid] = 50f;

            store.ApplyEnemyDamage(eid, 50f);
            Assert.Equal(0f, store.EnemyHealth[eid]);
        }

        [Fact]
        public void ApplyEnemyDamage_Overkill()
        {
            var store = new ComponentStore();
            int eid = store.CreateEntity();
            store.EnemyActive[eid] = true;
            store.EnemyHealth[eid] = 30f;

            store.ApplyEnemyDamage(eid, 100f);
            Assert.Equal(-70f, store.EnemyHealth[eid]);
        }

        // ─── Armor formula is applied upstream by TowerAttackSystem/PlayerTowerAttack ───
        // ApplyEnemyDamage applies raw (post-mitigation) damage to enemy.
        // This is an invariant test: armor does NOT affect ApplyEnemyDamage.

        [Fact]
        public void ApplyEnemyDamage_IgnoresArmor_RawDamage()
        {
            var store = new ComponentStore();
            int eid = store.CreateEntity();
            store.EnemyActive[eid] = true;
            store.EnemyHealth[eid] = 100f;
            store.EnemyArmor[eid] = 0.3f;
            store.EnemyShield[eid] = 0f;

            // ApplyEnemyDamage applies raw damage — armor is NOT applied here.
            // Armor is applied upstream by TowerAttackSystem/PlayerTowerAttack.
            store.ApplyEnemyDamage(eid, 100f);
            Assert.Equal(0f, store.EnemyHealth[eid]);
        }

        [Fact]
        public void Shield_BreakAndDamage_HandlesMultipleApplications()
        {
            var store = new ComponentStore();
            int eid = store.CreateEntity();
            store.EnemyActive[eid] = true;
            store.EnemyHealth[eid] = 100f;
            store.EnemyShield[eid] = 30f;

            // First hit: shield absorbs 20
            store.ApplyEnemyDamage(eid, 20f);
            Assert.Equal(100f, store.EnemyHealth[eid]);
            Assert.Equal(10f, store.EnemyShield[eid]);

            // Second hit: 10 to shield, 5 to health
            store.ApplyEnemyDamage(eid, 15f);
            Assert.Equal(95f, store.EnemyHealth[eid]);
            Assert.Equal(0f, store.EnemyShield[eid]);

            // Third hit: direct to health
            store.ApplyEnemyDamage(eid, 20f);
            Assert.Equal(75f, store.EnemyHealth[eid]);
        }

        // ─── Get/Set symmetry ───

        [Fact]
        public void EnemyHealth_GetSet_RoundTrip()
        {
            var store = new ComponentStore();
            int eid = store.CreateEntity();
            store.EnemyActive[eid] = true;

            store.SetEnemyHealth(eid, 75f);
            Assert.Equal(75f, store.GetEnemyHealth(eid));
        }

        [Fact]
        public void EnemyArmor_GetSet_RoundTrip()
        {
            var store = new ComponentStore();
            int eid = store.CreateEntity();
            store.EnemyActive[eid] = true;

            store.SetEnemyArmor(eid, 0.5f);
            Assert.Equal(0.5f, store.GetEnemyArmor(eid));
        }
    }
}
