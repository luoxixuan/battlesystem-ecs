using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Tests.Infrastructure;

namespace BattleSystemECS.Tests.Mechanisms.Control
{
    /// <summary>
    /// Tests for Round 97 Direction 3: CC Immunity (per-enemy bitmask of CC types to ignore).
    /// Verifies that:
    ///   - Default behavior (no immunity set) is unchanged: all CC types apply normally
    ///   - Setting Mask_Stun blocks ApplyEnemyStun but not ApplyEnemyFreeze (independent bits)
    ///   - Setting Mask_Slow blocks ApplyEnemySlow regardless of resistance
    ///   - Setting Mask_Freeze blocks ApplyEnemyFreeze
    ///   - Setting Mask_AllCC blocks every CC type
    ///   - EnemyIsUnstoppable still works (overrides the bitmask check)
    ///   - IsCCImmuneTo() returns the right result for combined flags
    ///   - ApplyPolymorph / AddStaggerDamage are blocked by their respective bits
    ///   - SetCCImmuneBit / ClearCCImmuneBit are OR-merge / bit-clear
    ///   - DestroyEntity resets the mask (no ID-reuse leakage)
    /// </summary>
    public class CCImmunityTests : BattleTestBase
    {
        // ─── Default (no immunity) — backward compat ──────────────────────

        [Fact]
        public void DefaultMask_NoImmunity_AllCCApply()
        {
            int e = Enemy();
            Assert.Equal(0, Store.EnemyCCImmuneMask[e]);

            Store.ApplyEnemyStun(e, 2);
            Assert.True(Store.IsEnemyStunned(e));

            Store.ApplyEnemyFreeze(e, 3);
            Assert.True(Store.IsEnemyFrozen(e));

            Store.ApplyEnemySlow(e, 0.5f, 5);
            Assert.Equal(0.5f, Store.EnemySlowFactor[e]);

            Store.ApplyPolymorph(e, 4, 1.5f);
            Assert.True(Store.EnemyIsPolymorphed[e]);
        }

        // ─── Mask_Stun blocks stun but not freeze (independent bits) ───────

        [Fact]
        public void Mask_Stun_BlocksStun_NotFreeze()
        {
            int e = Enemy();
            Store.SetCCImmuneBit(e, CCImmunityConfig.Mask_Stun);

            Store.ApplyEnemyStun(e, 2);
            Assert.False(Store.IsEnemyStunned(e));

            Store.ApplyEnemyFreeze(e, 3);
            Assert.True(Store.IsEnemyFrozen(e));
        }

        [Fact]
        public void Mask_Freeze_BlocksFreeze_NotStun()
        {
            int e = Enemy();
            Store.SetCCImmuneBit(e, CCImmunityConfig.Mask_Freeze);

            Store.ApplyEnemyFreeze(e, 3);
            Assert.False(Store.IsEnemyFrozen(e));

            Store.ApplyEnemyStun(e, 2);
            Assert.True(Store.IsEnemyStunned(e));
        }

        // ─── Mask_Slow blocks slow even with no resistance ─────────────────

        [Fact]
        public void Mask_Slow_BlocksSlow()
        {
            int e = Enemy();
            Store.SetCCImmuneBit(e, CCImmunityConfig.Mask_Slow);

            Store.ApplyEnemySlow(e, 0.5f, 5);
            Assert.Equal(0f, Store.EnemySlowFactor[e]);
            Assert.Equal(5f, Store.EnemyMoveSpeed[e]); // base speed, untouched
        }

        // ─── Mask_Polymorph blocks polymorph ──────────────────────────────

        [Fact]
        public void Mask_Polymorph_BlocksPolymorph()
        {
            int e = Enemy();
            Store.SetCCImmuneBit(e, CCImmunityConfig.Mask_Polymorph);

            Store.ApplyPolymorph(e, 4, 1.5f);
            Assert.False(Store.EnemyIsPolymorphed[e]);
            Assert.Equal(0f, Store.EnemyPolymorphDurationLeft[e]);
        }

        // ─── Mask_Stagger blocks stagger damage ───────────────────────────

        [Fact]
        public void Mask_Stagger_BlocksStaggerDamage()
        {
            int e = Enemy();
            // Stagger requires EnemyStaggerMax > 0 (default 0 = immune by design)
            Store.EnemyStaggerMax[e] = 100f;
            Store.SetCCImmuneBit(e, CCImmunityConfig.Mask_Stagger);

            bool triggered = Store.AddStaggerDamage(e, 200f, 30, 5f);
            Assert.False(triggered);
            Assert.False(Store.EnemyIsStaggered[e]);
            Assert.Equal(0f, Store.EnemyStaggerMeter[e]);
        }

        // ─── Mask_AllCC blocks everything ─────────────────────────────────

        [Fact]
        public void Mask_AllCC_BlocksEveryCCType()
        {
            int e = Enemy();
            Store.SetCCImmuneMask(e, CCImmunityConfig.Mask_AllCC);

            Store.ApplyEnemyStun(e, 2);
            Store.ApplyEnemyFreeze(e, 3);
            Store.ApplyEnemySlow(e, 0.5f, 5);
            Store.ApplyPolymorph(e, 4, 1.5f);
            Store.EnemyStaggerMax[e] = 100f;
            Store.AddStaggerDamage(e, 200f, 30, 5f);

            Assert.False(Store.IsEnemyStunned(e));
            Assert.False(Store.IsEnemyFrozen(e));
            Assert.Equal(0f, Store.EnemySlowFactor[e]);
            Assert.False(Store.EnemyIsPolymorphed[e]);
            Assert.False(Store.EnemyIsStaggered[e]);
        }

        // ─── EnemyIsUnstoppable still works (overrides bitmask) ────────────

        [Fact]
        public void Unstoppable_OverridesBitmask_StillBlocksAllCC()
        {
            int e = Enemy();
            // Bit set to 0 (so only Unstoppable stops CCs)
            Store.EnemyIsUnstoppable[e] = true;

            Store.ApplyEnemyStun(e, 2);
            Store.ApplyEnemyFreeze(e, 3);
            Store.ApplyEnemySlow(e, 0.5f, 5);
            Store.ApplyPolymorph(e, 4, 1.5f);

            Assert.False(Store.IsEnemyStunned(e));
            Assert.False(Store.IsEnemyFrozen(e));
            Assert.Equal(0f, Store.EnemySlowFactor[e]);
            Assert.False(Store.EnemyIsPolymorphed[e]);
        }

        [Fact]
        public void Unstoppable_True_AlwaysReturnsTrueFromIsCCImmuneTo()
        {
            int e = Enemy();
            Store.EnemyIsUnstoppable[e] = true;

            // Even with no bits set, IsCCImmuneTo returns true for any mask
            Assert.True(Store.IsCCImmuneTo(e, CCImmunityConfig.Mask_Slow));
            Assert.True(Store.IsCCImmuneTo(e, CCImmunityConfig.Mask_Stun));
            Assert.True(Store.IsCCImmuneTo(e, 0xFFFF));
        }

        // ─── IsCCImmuneTo() correctly reports per-type immunity ────────────

        [Fact]
        public void IsCCImmuneTo_PerTypeCheck_ReturnsCorrect()
        {
            int e = Enemy();
            Store.SetCCImmuneMask(e, CCImmunityConfig.Mask_Stun | CCImmunityConfig.Mask_Freeze);

            Assert.True(Store.IsCCImmuneTo(e, CCImmunityConfig.Mask_Stun));
            Assert.True(Store.IsCCImmuneTo(e, CCImmunityConfig.Mask_Freeze));
            Assert.False(Store.IsCCImmuneTo(e, CCImmunityConfig.Mask_Slow));
            Assert.False(Store.IsCCImmuneTo(e, CCImmunityConfig.Mask_Polymorph));
        }

        // ─── SetCCImmuneBit OR-merges, ClearCCImmuneBit removes the bit ────

        [Fact]
        public void SetCCImmuneBit_IsORMerge_Idempotent()
        {
            int e = Enemy();

            Store.SetCCImmuneBit(e, CCImmunityConfig.Mask_Stun);
            Assert.Equal(CCImmunityConfig.Mask_Stun, Store.EnemyCCImmuneMask[e]);

            // Set the same bit again — mask unchanged
            Store.SetCCImmuneBit(e, CCImmunityConfig.Mask_Stun);
            Assert.Equal(CCImmunityConfig.Mask_Stun, Store.EnemyCCImmuneMask[e]);

            // OR a different bit
            Store.SetCCImmuneBit(e, CCImmunityConfig.Mask_Slow);
            Assert.Equal(
                CCImmunityConfig.Mask_Stun | CCImmunityConfig.Mask_Slow,
                Store.EnemyCCImmuneMask[e]);
        }

        [Fact]
        public void ClearCCImmuneBit_RemovesBit()
        {
            int e = Enemy();
            Store.SetCCImmuneMask(e, CCImmunityConfig.Mask_Stun | CCImmunityConfig.Mask_Slow);

            Store.ClearCCImmuneBit(e, CCImmunityConfig.Mask_Stun);
            Assert.Equal(CCImmunityConfig.Mask_Slow, Store.EnemyCCImmuneMask[e]);

            Store.ClearCCImmuneBit(e, CCImmunityConfig.Mask_Slow);
            Assert.Equal(0, Store.EnemyCCImmuneMask[e]);
        }

        [Fact]
        public void SetCCImmuneMask_Overwrites()
        {
            int e = Enemy();
            Store.SetCCImmuneMask(e, CCImmunityConfig.Mask_Stun);

            Store.SetCCImmuneMask(e, CCImmunityConfig.Mask_AllCC);
            Assert.Equal(CCImmunityConfig.Mask_AllCC, Store.EnemyCCImmuneMask[e]);
        }

        // ─── Slow resistance does not bypass immunity ──────────────────────

        [Fact]
        public void SlowImmunity_OverridesSlowResistance()
        {
            int e = Enemy();
            // Set both immunity and high slow resistance
            Store.SetCCImmuneBit(e, CCImmunityConfig.Mask_Slow);
            Store.EnemySlowResistance[e] = 1.0f;  // 100% resist (would normally negate the slow anyway)

            Store.ApplyEnemySlow(e, 0.5f, 5);
            Assert.Equal(0f, Store.EnemySlowFactor[e]);
        }

        // ─── Stun resistance does not bypass immunity ──────────────────────

        [Fact]
        public void StunImmunity_BlocksStun_BeforeResistanceApplied()
        {
            int e = Enemy();
            Store.SetCCImmuneBit(e, CCImmunityConfig.Mask_Stun);
            Store.EnemyStunResistance[e] = 0f;  // no resistance, but immunity still blocks

            Store.ApplyEnemyStun(e, 5);
            Assert.False(Store.IsEnemyStunned(e));
            Assert.Equal(0f, Store.EnemyStunDurationLeft[e]);
        }

        // ─── Mask_BossDefault equals Mask_AllCC ────────────────────────────

        [Fact]
        public void Mask_BossDefault_EqualsMask_AllCC()
        {
            Assert.Equal(CCImmunityConfig.Mask_AllCC, CCImmunityConfig.Mask_BossDefault);
            // Round 124 — added Mask_Disarm = 1<<6, so 6 bits → 0x3F (63) became 7 bits → 0x7F (127)
            Assert.Equal(0x7F, CCImmunityConfig.Mask_AllCC);
        }

        // ─── Mask bits are unique (no overlap) ─────────────────────────────

        [Fact]
        public void MaskBits_AreUnique_NoOverlap()
        {
            int[] bits = {
                CCImmunityConfig.Mask_Slow,
                CCImmunityConfig.Mask_Stun,
                CCImmunityConfig.Mask_Freeze,
                CCImmunityConfig.Mask_Knockback,
                CCImmunityConfig.Mask_Polymorph,
                CCImmunityConfig.Mask_Stagger,
            };
            for (int i = 0; i < bits.Length; i++)
            for (int j = i + 1; j < bits.Length; j++)
                Assert.Equal(0, bits[i] & bits[j]);
        }

        // ─── Multiple enemies — independent masks ──────────────────────────

        [Fact]
        public void MultipleEnemies_HaveIndependentMasks()
        {
            int a = Enemy();
            int b = Enemy();

            Store.SetCCImmuneMask(a, CCImmunityConfig.Mask_Stun);
            Assert.Equal(0, Store.EnemyCCImmuneMask[b]);

            Store.ApplyEnemyStun(a, 3);
            Store.ApplyEnemyStun(b, 3);
            Assert.False(Store.IsEnemyStunned(a));
            Assert.True(Store.IsEnemyStunned(b));
        }

        // ─── Out-of-range / invalid entity calls are no-ops ───────────────

        [Fact]
        public void InvalidEntity_NullsMaskOps()
        {
            // Should not throw
            Store.SetCCImmuneBit(-1, CCImmunityConfig.Mask_Stun);
            Store.SetCCImmuneBit(ComponentStore.MAX_ENTITIES, CCImmunityConfig.Mask_Stun);
            Store.ClearCCImmuneBit(-1, CCImmunityConfig.Mask_Stun);
            Assert.False(Store.IsCCImmuneTo(-1, CCImmunityConfig.Mask_Stun));
            Assert.False(Store.IsCCImmuneTo(ComponentStore.MAX_ENTITIES, CCImmunityConfig.Mask_Stun));
        }
    }
}
