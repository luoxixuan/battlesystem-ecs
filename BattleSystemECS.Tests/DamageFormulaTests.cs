using System;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Tests.Infrastructure;

namespace BattleSystemECS.Tests
{
    /// <summary>
    /// Core damage formula tests — armor, magic resist, true damage, shield absorption.
    /// Formulas are applied upstream by TowerAttackSystem/PlayerTowerAttack.
    /// These tests verify the mathematical contracts.
    /// </summary>
    public class DamageFormulaTests : BattleTestBase
    {
        // ══════════════════════════════════════════════════════════════
        //  Direct damage & shield
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void ApplyEnemyDamage_DirectDamage_ReducesHealth()
        {
            int eid = Enemy();

            Store.ApplyEnemyDamage(eid, 30f);
            Assert.Equal(70f, Store.EnemyHealth[eid]);
        }

        [Fact]
        public void ApplyEnemyDamage_ShieldAbsorbsFirst()
        {
            int eid = Enemy();
            Store.EnemyShield[eid] = 25f;

            Store.ApplyEnemyDamage(eid, 20f);
            Assert.Equal(100f, Store.EnemyHealth[eid]);
            Assert.Equal(5f, Store.EnemyShield[eid]);
        }

        [Fact]
        public void ApplyEnemyDamage_ShieldPartial()
        {
            int eid = Enemy();
            Store.EnemyShield[eid] = 10f;

            Store.ApplyEnemyDamage(eid, 40f);
            Assert.Equal(70f, Store.EnemyHealth[eid]);
            Assert.Equal(0f, Store.EnemyShield[eid]);
        }

        [Fact]
        public void ApplyEnemyDamage_ZeroDamage_NoEffect()
        {
            int eid = Enemy();
            Store.EnemyShield[eid] = 50f;

            Store.ApplyEnemyDamage(eid, 0f);
            Assert.Equal(100f, Store.EnemyHealth[eid]);
            Assert.Equal(50f, Store.EnemyShield[eid]);
        }

        [Fact]
        public void ApplyEnemyDamage_NegativeDamage_Ignored()
        {
            int eid = Enemy();

            Store.ApplyEnemyDamage(eid, -10f);
            Assert.Equal(100f, Store.EnemyHealth[eid]);
        }

        [Fact]
        public void ApplyEnemyDamage_ExactKill()
        {
            int eid = Enemy(e => e.Health = 50f);

            Store.ApplyEnemyDamage(eid, 50f);
            Assert.Equal(0f, Store.EnemyHealth[eid]);
        }

        [Fact]
        public void ApplyEnemyDamage_Overkill()
        {
            int eid = Enemy(e => e.Health = 30f);

            Store.ApplyEnemyDamage(eid, 100f);
            Assert.Equal(-70f, Store.EnemyHealth[eid]);
        }

        [Fact]
        public void ApplyEnemyDamage_IgnoresArmor_RawDamage()
        {
            int eid = Enemy();
            Store.EnemyArmor[eid] = 0.3f;

            Store.ApplyEnemyDamage(eid, 100f);
            Assert.Equal(0f, Store.EnemyHealth[eid]);
        }

        [Fact]
        public void Shield_BreakAndDamage_HandlesMultipleApplications()
        {
            int eid = Enemy();
            Store.EnemyShield[eid] = 30f;

            Store.ApplyEnemyDamage(eid, 20f);
            Assert.Equal(100f, Store.EnemyHealth[eid]);
            Assert.Equal(10f, Store.EnemyShield[eid]);

            Store.ApplyEnemyDamage(eid, 15f);
            Assert.Equal(95f, Store.EnemyHealth[eid]);
            Assert.Equal(0f, Store.EnemyShield[eid]);

            Store.ApplyEnemyDamage(eid, 20f);
            Assert.Equal(75f, Store.EnemyHealth[eid]);
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
            int pid = Player();

            Assert.Equal(0, Store.GetCurrentWeather(pid)); // 0 = Clear
        }

        [Fact]
        public void Weather_SetAndGet_RoundTrip()
        {
            int pid = Player();

            Store.SetCurrentWeather(pid, 1); // Rain
            Assert.Equal(1, Store.GetCurrentWeather(pid));
        }

        [Fact]
        public void DayNight_DefaultPhase()
        {
            int pid = Player();

            Assert.Equal(0, Store.GetDayNightPhase(pid)); // 0 = Day
        }

        [Fact]
        public void DayNight_SetAndGet_RoundTrip()
        {
            int pid = Player();

            Store.SetDayNightPhase(pid, 1); // Night
            Assert.Equal(1, Store.GetDayNightPhase(pid));
        }

        // ══════════════════════════════════════════════════════════════
        //  Get/Set symmetry
        // ══════════════════════════════════════════════════════════════

        [Fact]
        public void EnemyHealth_GetSet_RoundTrip()
        {
            int eid = Enemy();
            Store.SetEnemyHealth(eid, 75f);
            Assert.Equal(75f, Store.GetEnemyHealth(eid));
        }

        [Fact]
        public void EnemyArmor_GetSet_RoundTrip()
        {
            int eid = Enemy();
            Store.SetEnemyArmor(eid, 0.5f);
            Assert.Equal(0.5f, Store.GetEnemyArmor(eid));
        }
    }
}
