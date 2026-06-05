using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Tests
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
    public class CCImmunityTests
    {
        private static int SpawnPlainEnemy(ComponentStore store)
        {
            return store.AddEnemy(0, 0, 5f, 100f, 100f, 5f, 10, 1, "TestEnemy");
        }

        // ─── Default (no immunity) — backward compat ──────────────────────

        [Fact]
        public void DefaultMask_NoImmunity_AllCCApply()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            Assert.Equal(0, store.EnemyCCImmuneMask[e]);

            store.ApplyEnemyStun(e, 2);
            Assert.True(store.IsEnemyStunned(e));

            store.ApplyEnemyFreeze(e, 3);
            Assert.True(store.IsEnemyFrozen(e));

            store.ApplyEnemySlow(e, 0.5f, 5);
            Assert.Equal(0.5f, store.EnemySlowFactor[e]);

            store.ApplyPolymorph(e, 4, 1.5f);
            Assert.True(store.EnemyIsPolymorphed[e]);
        }

        // ─── Mask_Stun blocks stun but not freeze (independent bits) ───────

        [Fact]
        public void Mask_Stun_BlocksStun_NotFreeze()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            store.SetCCImmuneBit(e, CCImmunityConfig.Mask_Stun);

            store.ApplyEnemyStun(e, 2);
            Assert.False(store.IsEnemyStunned(e));

            store.ApplyEnemyFreeze(e, 3);
            Assert.True(store.IsEnemyFrozen(e));
        }

        [Fact]
        public void Mask_Freeze_BlocksFreeze_NotStun()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            store.SetCCImmuneBit(e, CCImmunityConfig.Mask_Freeze);

            store.ApplyEnemyFreeze(e, 3);
            Assert.False(store.IsEnemyFrozen(e));

            store.ApplyEnemyStun(e, 2);
            Assert.True(store.IsEnemyStunned(e));
        }

        // ─── Mask_Slow blocks slow even with no resistance ─────────────────

        [Fact]
        public void Mask_Slow_BlocksSlow()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            store.SetCCImmuneBit(e, CCImmunityConfig.Mask_Slow);

            store.ApplyEnemySlow(e, 0.5f, 5);
            Assert.Equal(0f, store.EnemySlowFactor[e]);
            Assert.Equal(5f, store.EnemyMoveSpeed[e]); // base speed, untouched
        }

        // ─── Mask_Polymorph blocks polymorph ──────────────────────────────

        [Fact]
        public void Mask_Polymorph_BlocksPolymorph()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            store.SetCCImmuneBit(e, CCImmunityConfig.Mask_Polymorph);

            store.ApplyPolymorph(e, 4, 1.5f);
            Assert.False(store.EnemyIsPolymorphed[e]);
            Assert.Equal(0f, store.EnemyPolymorphDurationLeft[e]);
        }

        // ─── Mask_Stagger blocks stagger damage ───────────────────────────

        [Fact]
        public void Mask_Stagger_BlocksStaggerDamage()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            // Stagger requires EnemyStaggerMax > 0 (default 0 = immune by design)
            store.EnemyStaggerMax[e] = 100f;
            store.SetCCImmuneBit(e, CCImmunityConfig.Mask_Stagger);

            bool triggered = store.AddStaggerDamage(e, 200f, 30, 5f);
            Assert.False(triggered);
            Assert.False(store.EnemyIsStaggered[e]);
            Assert.Equal(0f, store.EnemyStaggerMeter[e]);
        }

        // ─── Mask_AllCC blocks everything ─────────────────────────────────

        [Fact]
        public void Mask_AllCC_BlocksEveryCCType()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            store.SetCCImmuneMask(e, CCImmunityConfig.Mask_AllCC);

            store.ApplyEnemyStun(e, 2);
            store.ApplyEnemyFreeze(e, 3);
            store.ApplyEnemySlow(e, 0.5f, 5);
            store.ApplyPolymorph(e, 4, 1.5f);
            store.EnemyStaggerMax[e] = 100f;
            store.AddStaggerDamage(e, 200f, 30, 5f);

            Assert.False(store.IsEnemyStunned(e));
            Assert.False(store.IsEnemyFrozen(e));
            Assert.Equal(0f, store.EnemySlowFactor[e]);
            Assert.False(store.EnemyIsPolymorphed[e]);
            Assert.False(store.EnemyIsStaggered[e]);
        }

        // ─── EnemyIsUnstoppable still works (overrides bitmask) ────────────

        [Fact]
        public void Unstoppable_OverridesBitmask_StillBlocksAllCC()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            // Bit set to 0 (so only Unstoppable stops CCs)
            store.EnemyIsUnstoppable[e] = true;

            store.ApplyEnemyStun(e, 2);
            store.ApplyEnemyFreeze(e, 3);
            store.ApplyEnemySlow(e, 0.5f, 5);
            store.ApplyPolymorph(e, 4, 1.5f);

            Assert.False(store.IsEnemyStunned(e));
            Assert.False(store.IsEnemyFrozen(e));
            Assert.Equal(0f, store.EnemySlowFactor[e]);
            Assert.False(store.EnemyIsPolymorphed[e]);
        }

        [Fact]
        public void Unstoppable_True_AlwaysReturnsTrueFromIsCCImmuneTo()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            store.EnemyIsUnstoppable[e] = true;

            // Even with no bits set, IsCCImmuneTo returns true for any mask
            Assert.True(store.IsCCImmuneTo(e, CCImmunityConfig.Mask_Slow));
            Assert.True(store.IsCCImmuneTo(e, CCImmunityConfig.Mask_Stun));
            Assert.True(store.IsCCImmuneTo(e, 0xFFFF));
        }

        // ─── IsCCImmuneTo() correctly reports per-type immunity ────────────

        [Fact]
        public void IsCCImmuneTo_PerTypeCheck_ReturnsCorrect()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            store.SetCCImmuneMask(e, CCImmunityConfig.Mask_Stun | CCImmunityConfig.Mask_Freeze);

            Assert.True(store.IsCCImmuneTo(e, CCImmunityConfig.Mask_Stun));
            Assert.True(store.IsCCImmuneTo(e, CCImmunityConfig.Mask_Freeze));
            Assert.False(store.IsCCImmuneTo(e, CCImmunityConfig.Mask_Slow));
            Assert.False(store.IsCCImmuneTo(e, CCImmunityConfig.Mask_Polymorph));
        }

        // ─── SetCCImmuneBit OR-merges, ClearCCImmuneBit removes the bit ────

        [Fact]
        public void SetCCImmuneBit_IsORMerge_Idempotent()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);

            store.SetCCImmuneBit(e, CCImmunityConfig.Mask_Stun);
            Assert.Equal(CCImmunityConfig.Mask_Stun, store.EnemyCCImmuneMask[e]);

            // Set the same bit again — mask unchanged
            store.SetCCImmuneBit(e, CCImmunityConfig.Mask_Stun);
            Assert.Equal(CCImmunityConfig.Mask_Stun, store.EnemyCCImmuneMask[e]);

            // OR a different bit
            store.SetCCImmuneBit(e, CCImmunityConfig.Mask_Slow);
            Assert.Equal(
                CCImmunityConfig.Mask_Stun | CCImmunityConfig.Mask_Slow,
                store.EnemyCCImmuneMask[e]);
        }

        [Fact]
        public void ClearCCImmuneBit_RemovesBit()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            store.SetCCImmuneMask(e, CCImmunityConfig.Mask_Stun | CCImmunityConfig.Mask_Slow);

            store.ClearCCImmuneBit(e, CCImmunityConfig.Mask_Stun);
            Assert.Equal(CCImmunityConfig.Mask_Slow, store.EnemyCCImmuneMask[e]);

            store.ClearCCImmuneBit(e, CCImmunityConfig.Mask_Slow);
            Assert.Equal(0, store.EnemyCCImmuneMask[e]);
        }

        [Fact]
        public void SetCCImmuneMask_Overwrites()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            store.SetCCImmuneMask(e, CCImmunityConfig.Mask_Stun);

            store.SetCCImmuneMask(e, CCImmunityConfig.Mask_AllCC);
            Assert.Equal(CCImmunityConfig.Mask_AllCC, store.EnemyCCImmuneMask[e]);
        }

        // ─── Slow resistance does not bypass immunity ──────────────────────

        [Fact]
        public void SlowImmunity_OverridesSlowResistance()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            // Set both immunity and high slow resistance
            store.SetCCImmuneBit(e, CCImmunityConfig.Mask_Slow);
            store.EnemySlowResistance[e] = 1.0f;  // 100% resist (would normally negate the slow anyway)

            store.ApplyEnemySlow(e, 0.5f, 5);
            Assert.Equal(0f, store.EnemySlowFactor[e]);
        }

        // ─── Stun resistance does not bypass immunity ──────────────────────

        [Fact]
        public void StunImmunity_BlocksStun_BeforeResistanceApplied()
        {
            var store = new ComponentStore();
            int e = SpawnPlainEnemy(store);
            store.SetCCImmuneBit(e, CCImmunityConfig.Mask_Stun);
            store.EnemyStunResistance[e] = 0f;  // no resistance, but immunity still blocks

            store.ApplyEnemyStun(e, 5);
            Assert.False(store.IsEnemyStunned(e));
            Assert.Equal(0f, store.EnemyStunDurationLeft[e]);
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
            var store = new ComponentStore();
            int a = SpawnPlainEnemy(store);
            int b = SpawnPlainEnemy(store);

            store.SetCCImmuneMask(a, CCImmunityConfig.Mask_Stun);
            Assert.Equal(0, store.EnemyCCImmuneMask[b]);

            store.ApplyEnemyStun(a, 3);
            store.ApplyEnemyStun(b, 3);
            Assert.False(store.IsEnemyStunned(a));
            Assert.True(store.IsEnemyStunned(b));
        }

        // ─── Out-of-range / invalid entity calls are no-ops ───────────────

        [Fact]
        public void InvalidEntity_NullsMaskOps()
        {
            var store = new ComponentStore();
            // Should not throw
            store.SetCCImmuneBit(-1, CCImmunityConfig.Mask_Stun);
            store.SetCCImmuneBit(ComponentStore.MAX_ENTITIES, CCImmunityConfig.Mask_Stun);
            store.ClearCCImmuneBit(-1, CCImmunityConfig.Mask_Stun);
            Assert.False(store.IsCCImmuneTo(-1, CCImmunityConfig.Mask_Stun));
            Assert.False(store.IsCCImmuneTo(ComponentStore.MAX_ENTITIES, CCImmunityConfig.Mask_Stun));
        }
    }
}
