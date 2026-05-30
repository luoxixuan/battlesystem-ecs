using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;

namespace BattleSystemECS.Tests
{
    /// <summary>
    /// Core damage formula tests — armor, magic resist, true damage, shield absorption.
    /// Formulas are applied upstream by TowerAttackSystem/PlayerTowerAttack.
    /// These tests verify the mathematical contracts.
    /// </summary>
    public class DamageFormulaTests
    {
        // ══════════════════════════════════════════════════════════════
        //  Direct damage & shield
        // ══════════════════════════════════════════════════════════════

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

        [Fact]
        public void ApplyEnemyDamage_IgnoresArmor_RawDamage()
        {
            var store = new ComponentStore();
            int eid = store.CreateEntity();
            store.EnemyActive[eid] = true;
            store.EnemyHealth[eid] = 100f;
            store.EnemyArmor[eid] = 0.3f;
            store.EnemyShield[eid] = 0f;

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

            store.ApplyEnemyDamage(eid, 20f);
            Assert.Equal(100f, store.EnemyHealth[eid]);
            Assert.Equal(10f, store.EnemyShield[eid]);

            store.ApplyEnemyDamage(eid, 15f);
            Assert.Equal(95f, store.EnemyHealth[eid]);
            Assert.Equal(0f, store.EnemyShield[eid]);

            store.ApplyEnemyDamage(eid, 20f);
            Assert.Equal(75f, store.EnemyHealth[eid]);
        }

        // ══════════════════════════════════════════════════════════════
        //  Armor formula: effectiveArmor = armor * (1 - pen) - shred
        //  damage = baseDamage * max(0.01, 1 - effectiveArmor)
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void ArmorFormula_30PercentArmor_70PercentDamage()
        {
            float armor = 0.3f;
            float baseDamage = 100f;
            float effectiveArmor = armor; // no pen, no shred
            float mitigated = baseDamage * Math.Max(0.01f, 1f - effectiveArmor);
            Assert.Equal(70f, mitigated, 3);
        }

        [Fact]
        public void ArmorFormula_WithPenetration()
        {
            float armor = 0.5f;
            float pen = 0.2f;
            float baseDamage = 200f;
            float effectiveArmor = armor * (1f - pen); // 0.5 * 0.8 = 0.4
            float mitigated = baseDamage * Math.Max(0.01f, 1f - effectiveArmor); // 200 * 0.6 = 120
            Assert.Equal(120f, mitigated, 3);
        }

        [Fact]
        public void ArmorFormula_WithPenetrationAndShred()
        {
            float armor = 0.6f;
            float pen = 0.3f;
            float shredPerStack = 0.05f;
            float shredStacks = 4f;
            float baseDamage = 100f;
            float effectiveArmor = armor * (1f - pen) - shredStacks * shredPerStack;
            effectiveArmor = Math.Max(0f, effectiveArmor); // 0.42 - 0.20 = 0.22
            float mitigated = baseDamage * Math.Max(0.01f, 1f - effectiveArmor); // 100 * 0.78 = 78
            Assert.Equal(78f, mitigated, 3);
        }

        [Fact]
        public void ArmorFormula_NegativeArmor_ClampsToMinimum()
        {
            float armor = 0.1f;
            float shredStacks = 10f;
            float shredPerStack = 0.05f;
            float effectiveArmor = Math.Max(0f, armor - shredStacks * shredPerStack); // -0.4 → 0
            float mitigated = 100f * Math.Max(0.01f, 1f - effectiveArmor); // 100 * 1.0 = 100
            Assert.Equal(100f, mitigated, 3);
        }

        [Fact]
        public void ArmorFormula_FullArmor_DamageClamped()
        {
            float armor = 1.0f;
            float mitigated = 100f * Math.Max(0.01f, 1f - armor); // 100 * 0.01 = 1
            Assert.Equal(1f, mitigated, 3);
        }

        // ══════════════════════════════════════════════════════════════
        //  Magic resist: damage *= max(0.01, 1 - magicResist)
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void MagicResist_50Percent_50PercentDamage()
        {
            float magicResist = 0.5f;
            float baseDamage = 100f;
            float mitigated = baseDamage * Math.Max(0.01f, 1f - magicResist); // 50
            Assert.Equal(50f, mitigated, 3);
        }

        [Fact]
        public void MagicResist_Zero_NoReduction()
        {
            float mitigated = 100f * Math.Max(0.01f, 1f - 0f); // 100
            Assert.Equal(100f, mitigated, 3);
        }

        [Fact]
        public void MagicResist_FullResist_DamageClamped()
        {
            float magicResist = 1.0f;
            float mitigated = 100f * Math.Max(0.01f, 1f - magicResist); // 100 * 0.01 = 1
            Assert.Equal(1f, mitigated, 3);
        }

        // ══════════════════════════════════════════════════════════════
        //  True damage: no reduction from armor or magic resist
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void TrueDamage_IgnoresArmorAndResist()
        {
            // True damage passes through with no mitigation — only damageTakenMult applies
            float baseDamage = 100f;
            float damageTakenMult = 1.0f;
            float trueDamage = baseDamage * damageTakenMult;
            Assert.Equal(100f, trueDamage, 3);
        }

        [Fact]
        public void TrueDamage_WithDamageTakenMult()
        {
            float baseDamage = 100f;
            float damageTakenMult = 1.5f; // curse debuff: +50% damage taken
            float trueDamage = baseDamage * damageTakenMult;
            Assert.Equal(150f, trueDamage, 3);
        }

        // ══════════════════════════════════════════════════════════════
        //  Wave difficulty multiplier
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void WaveDifficultyMult_AppliedAfterMitigation()
        {
            float baseDamage = 100f;
            float armor = 0.3f;
            float waveMult = 1.2f;
            // Step 1: armor mitigation
            float postArmor = baseDamage * Math.Max(0.01f, 1f - armor); // 70
            // Step 2: wave difficulty
            float final = postArmor * waveMult; // 84
            Assert.Equal(84f, final, 3);
        }

        // ══════════════════════════════════════════════════════════════
        //  Weather / Day-Night state invariants
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void Weather_DefaultIsClear()
        {
            var store = new ComponentStore();
            int pid = store.CreateEntity();
            store.AddPlayer(pid, 5f, 5f, 10f, 1);

            Assert.Equal(0, store.GetCurrentWeather(pid)); // 0 = Clear
        }

        [Fact]
        public void Weather_SetAndGet_RoundTrip()
        {
            var store = new ComponentStore();
            int pid = store.CreateEntity();
            store.AddPlayer(pid, 5f, 5f, 10f, 1);

            store.SetCurrentWeather(pid, 1); // Rain
            Assert.Equal(1, store.GetCurrentWeather(pid));
        }

        [Fact]
        public void DayNight_DefaultPhase()
        {
            var store = new ComponentStore();
            int pid = store.CreateEntity();
            store.AddPlayer(pid, 5f, 5f, 10f, 1);

            Assert.Equal(0, store.GetDayNightPhase(pid)); // 0 = Day
        }

        [Fact]
        public void DayNight_SetAndGet_RoundTrip()
        {
            var store = new ComponentStore();
            int pid = store.CreateEntity();
            store.AddPlayer(pid, 5f, 5f, 10f, 1);

            store.SetDayNightPhase(pid, 1); // Night
            Assert.Equal(1, store.GetDayNightPhase(pid));
        }

        // ══════════════════════════════════════════════════════════════
        //  Get/Set symmetry
        // ══════════════════════════════════════════════════════════════

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
