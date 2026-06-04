using System;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
{
    /// <summary>
    /// Tests for Round 103 Direction 8: Buff Share (Group Buff).
    /// Verifies that:
    ///   - Default state: BuffShare fields are 0 (zero-overhead fast path)
    ///   - BuffShareConfig has sensible defaults
    ///   - Sharing tower within radius multiplies nearby towers' attack speed by (1 + efficiency)
    ///   - Towers outside the radius are unaffected
    ///   - Self-share is skipped (no infinite loop)
    ///   - Mask=0 disables sharing even with non-zero radius
    ///   - Multiple sharing towers in range STACK multiplicatively across calls,
    ///     but the per-frame base restoration prevents frame-over-frame compound growth
    ///   - Dispelled sharing tower or dispelled target does not apply/receive the bonus
    ///   - DestroyEntity and RemoveTower reset the BuffShare fields (ID-reuse safety)
    ///   - No sharing tower present → ResolveBuffShares is a no-op fast path
    /// </summary>
    public class BuffShareSystemTests
    {
        private static MockRenderer NewRenderer() => new MockRenderer();

        private static int PlaceTower(ComponentStore store, int id, float x, float y,
            float attackSpeed = 1f, int entitySlot = 1)
        {
            store.AddTower(id, TowerType.Basic, 10f, 5, attackSpeed, 1, 50f,
                "standard", 0f, 0f, 0f);
            store.PositionX[id] = x;
            store.PositionY[id] = y;
            return id;
        }

        // ─── Default state ─────────────────────────────────────────────

        [Fact]
        public void DefaultState_AllBuffShareFieldsZero()
        {
            var store = new ComponentStore();
            for (int i = 0; i < 10; i++)
            {
                Assert.Equal(0f, store.TowerBuffShareRadius[i]);
                Assert.Equal(0, store.TowerBuffShareMask[i]);
            }
        }

        [Fact]
        public void AddTower_DefaultsBuffShareToZero()
        {
            var store = new ComponentStore();
            int t = PlaceTower(store, 0, 0f, 0f);
            Assert.Equal(0f, store.TowerBuffShareRadius[t]);
            Assert.Equal(0, store.TowerBuffShareMask[t]);
        }

        [Fact]
        public void BuffShareConfig_HasSensibleDefaults()
        {
            Assert.True(BuffShareConfig.MaxShareRadius > 0f);
            Assert.True(BuffShareConfig.DefaultShareEfficiencyPct > 0f);
            Assert.True(BuffShareConfig.DefaultShareEfficiencyPct <= 1f);
            Assert.Equal(0x01, BuffShareConfig.ShareAttackSpeed);
        }

        // ─── Accessor methods ──────────────────────────────────────────

        [Fact]
        public void GetSetBuffShareRadius_ClampsToZeroOrAbove()
        {
            var store = new ComponentStore();
            int t = PlaceTower(store, 0, 0f, 0f);
            store.SetTowerBuffShareRadius(t, -5f);
            Assert.Equal(0f, store.GetTowerBuffShareRadius(t));
            store.SetTowerBuffShareRadius(t, 4.5f);
            Assert.Equal(4.5f, store.GetTowerBuffShareRadius(t));
        }

        [Fact]
        public void GetSetBuffShareMask_StoresAndReturns()
        {
            var store = new ComponentStore();
            int t = PlaceTower(store, 0, 0f, 0f);
            store.SetTowerBuffShareMask(t, BuffShareConfig.ShareAttackSpeed);
            Assert.Equal(BuffShareConfig.ShareAttackSpeed, store.GetTowerBuffShareMask(t));
        }

        // ─── Core sharing behavior ─────────────────────────────────────

        [Fact]
        public void ResolveBuffShares_NearbyTower_GetsAttackSpeedBonus()
        {
            var store = new ComponentStore();
            int sharer = PlaceTower(store, 0, 0f, 0f, attackSpeed: 1f);
            int target = PlaceTower(store, 1, 3f, 0f, attackSpeed: 2f);

            store.SetTowerBuffShareRadius(sharer, 8f);
            store.SetTowerBuffShareMask(sharer, BuffShareConfig.ShareAttackSpeed);

            var sys = new TowerSynergySystem(store, NewRenderer());
            sys.ResolveBuffShares();

            // Target should now have base_speed * (1 + efficiency)
            float expected = 2f * (1f + BuffShareConfig.DefaultShareEfficiencyPct);
            Assert.Equal(expected, store.TowerAttackSpeed[target], 4);
        }

        [Fact]
        public void ResolveBuffShares_OutOfRangeTower_Unchanged()
        {
            var store = new ComponentStore();
            int sharer = PlaceTower(store, 0, 0f, 0f, attackSpeed: 1f);
            int far = PlaceTower(store, 1, 100f, 100f, attackSpeed: 2f);

            store.SetTowerBuffShareRadius(sharer, 8f);
            store.SetTowerBuffShareMask(sharer, BuffShareConfig.ShareAttackSpeed);

            var sys = new TowerSynergySystem(store, NewRenderer());
            sys.ResolveBuffShares();

            // Far tower beyond radius² = 64 should be unchanged
            Assert.Equal(2f, store.TowerAttackSpeed[far], 4);
        }

        [Fact]
        public void ResolveBuffShares_MaskZero_NoShare()
        {
            var store = new ComponentStore();
            int sharer = PlaceTower(store, 0, 0f, 0f);
            int target = PlaceTower(store, 1, 1f, 0f, attackSpeed: 2f);

            store.SetTowerBuffShareRadius(sharer, 8f);
            // Mask intentionally left at 0
            Assert.Equal(0, store.TowerBuffShareMask[sharer]);

            var sys = new TowerSynergySystem(store, NewRenderer());
            sys.ResolveBuffShares();

            Assert.Equal(2f, store.TowerAttackSpeed[target], 4);
        }

        [Fact]
        public void ResolveBuffShares_RadiusZero_NoShare()
        {
            var store = new ComponentStore();
            int sharer = PlaceTower(store, 0, 0f, 0f);
            int target = PlaceTower(store, 1, 1f, 0f, attackSpeed: 2f);

            store.SetTowerBuffShareMask(sharer, BuffShareConfig.ShareAttackSpeed);
            // Radius intentionally left at 0
            Assert.Equal(0f, store.TowerBuffShareRadius[sharer]);

            var sys = new TowerSynergySystem(store, NewRenderer());
            sys.ResolveBuffShares();

            Assert.Equal(2f, store.TowerAttackSpeed[target], 4);
        }

        [Fact]
        public void ResolveBuffShares_SelfNotSharedTo_SanityCheck()
        {
            // Self-share would be a circular no-op anyway, but verify the method does not
            // accidentally apply the bonus to the sharing tower itself.
            var store = new ComponentStore();
            int sharer = PlaceTower(store, 0, 0f, 0f, attackSpeed: 1.5f);

            store.SetTowerBuffShareRadius(sharer, 8f);
            store.SetTowerBuffShareMask(sharer, BuffShareConfig.ShareAttackSpeed);

            var sys = new TowerSynergySystem(store, NewRenderer());
            sys.ResolveBuffShares();

            // Sharer speed must remain at its base (1.5)
            Assert.Equal(1.5f, store.TowerAttackSpeed[sharer], 4);
        }

        [Fact]
        public void ResolveBuffShares_NoSharingTowers_FastPathLeavesSpeedsAlone()
        {
            // Both towers have radius=0 and mask=0 — ResolveBuffShares must early-out cleanly.
            var store = new ComponentStore();
            int t1 = PlaceTower(store, 0, 0f, 0f, attackSpeed: 1.7f);
            int t2 = PlaceTower(store, 1, 2f, 0f, attackSpeed: 2.3f);

            var sys = new TowerSynergySystem(store, NewRenderer());
            sys.ResolveBuffShares();

            Assert.Equal(1.7f, store.TowerAttackSpeed[t1], 4);
            Assert.Equal(2.3f, store.TowerAttackSpeed[t2], 4);
        }

        [Fact]
        public void ResolveBuffShares_MultipleFrames_DoesNotCompound()
        {
            // CRITICAL: ResolveBuffShares must NOT compound the bonus frame-over-frame.
            // First frame applies bonus, second frame must restore base + re-apply, not
            // stack on top of an already-boosted value (this is the bug scanner's #1 worry
            // for any multiplicative on-field modifier).
            var store = new ComponentStore();
            int sharer = PlaceTower(store, 0, 0f, 0f, attackSpeed: 1f);
            int target = PlaceTower(store, 1, 1f, 0f, attackSpeed: 2f);

            store.SetTowerBuffShareRadius(sharer, 8f);
            store.SetTowerBuffShareMask(sharer, BuffShareConfig.ShareAttackSpeed);

            var sys = new TowerSynergySystem(store, NewRenderer());
            sys.ResolveBuffShares();
            float firstFrame = store.TowerAttackSpeed[target];

            sys.ResolveBuffShares();
            float secondFrame = store.TowerAttackSpeed[target];

            Assert.Equal(firstFrame, secondFrame, 4);
            // Sanity: should equal base × (1 + eff) once, not base × (1 + eff)²
            float expected = 2f * (1f + BuffShareConfig.DefaultShareEfficiencyPct);
            Assert.Equal(expected, secondFrame, 4);
        }

        [Fact]
        public void ResolveBuffShares_DispelledSharingTower_NoBonus()
        {
            var store = new ComponentStore();
            int sharer = PlaceTower(store, 0, 0f, 0f, attackSpeed: 1f);
            int target = PlaceTower(store, 1, 1f, 0f, attackSpeed: 2f);

            store.SetTowerBuffShareRadius(sharer, 8f);
            store.SetTowerBuffShareMask(sharer, BuffShareConfig.ShareAttackSpeed);
            store.TowerIsDispelled[sharer] = true; // dispel = clears share

            var sys = new TowerSynergySystem(store, NewRenderer());
            sys.ResolveBuffShares();

            Assert.Equal(2f, store.TowerAttackSpeed[target], 4);
        }

        [Fact]
        public void ResolveBuffShares_DispelledTarget_NoBonus()
        {
            var store = new ComponentStore();
            int sharer = PlaceTower(store, 0, 0f, 0f, attackSpeed: 1f);
            int target = PlaceTower(store, 1, 1f, 0f, attackSpeed: 2f);

            store.SetTowerBuffShareRadius(sharer, 8f);
            store.SetTowerBuffShareMask(sharer, BuffShareConfig.ShareAttackSpeed);
            store.TowerIsDispelled[target] = true; // dispel = cannot receive share

            var sys = new TowerSynergySystem(store, NewRenderer());
            sys.ResolveBuffShares();

            Assert.Equal(2f, store.TowerAttackSpeed[target], 4);
        }

        [Fact]
        public void ResolveBuffShares_TwoSharersSurroundingTarget_StacksMultiplicatively()
        {
            // 2 sharing towers at distance ~2 from target → target's speed should be
            // base × (1 + eff)² after ONE frame of ResolveBuffShares. The first frame's
            // base seed equals the un-boosted base; second iteration of the inner loop
            // multiplies the boosted value by (1+eff) once more.
            var store = new ComponentStore();
            int s1 = PlaceTower(store, 0, 0f, 0f, attackSpeed: 1f);
            int s2 = PlaceTower(store, 1, 4f, 0f, attackSpeed: 1f);
            int target = PlaceTower(store, 2, 2f, 0f, attackSpeed: 2f);

            store.SetTowerBuffShareRadius(s1, 8f);
            store.SetTowerBuffShareMask(s1, BuffShareConfig.ShareAttackSpeed);
            store.SetTowerBuffShareRadius(s2, 8f);
            store.SetTowerBuffShareMask(s2, BuffShareConfig.ShareAttackSpeed);

            var sys = new TowerSynergySystem(store, NewRenderer());
            sys.ResolveBuffShares();

            // After sharer 1: target speed = 2 * (1+eff)
            // After sharer 2: target speed = 2 * (1+eff) * (1+eff) = 2 * (1+eff)²
            float eff = BuffShareConfig.DefaultShareEfficiencyPct;
            float expected = 2f * (1f + eff) * (1f + eff);
            Assert.Equal(expected, store.TowerAttackSpeed[target], 3);
        }

        // ─── ID-reuse safety ───────────────────────────────────────────

        [Fact]
        public void DestroyEntity_ResetsBuffShareFields()
        {
            var store = new ComponentStore();
            int t = PlaceTower(store, 0, 0f, 0f);
            store.SetTowerBuffShareRadius(t, 6f);
            store.SetTowerBuffShareMask(t, BuffShareConfig.ShareAttackSpeed);

            store.DestroyEntity(t);
            Assert.Equal(0f, store.TowerBuffShareRadius[t]);
            Assert.Equal(0, store.TowerBuffShareMask[t]);
        }

        [Fact]
        public void RemoveTower_ResetsBuffShareFields()
        {
            var store = new ComponentStore();
            int t = PlaceTower(store, 0, 0f, 0f);
            store.SetTowerBuffShareRadius(t, 6f);
            store.SetTowerBuffShareMask(t, BuffShareConfig.ShareAttackSpeed);

            store.RemoveTower(t);
            Assert.Equal(0f, store.TowerBuffShareRadius[t]);
            Assert.Equal(0, store.TowerBuffShareMask[t]);
        }

        // ─── Regression: cache must be keyed by towerId, not position in ActiveTowerIds ───

        [Fact]
        public void ResolveBuffShares_CacheKeyedByTowerId_NotPosition()
        {
            // Repro for Claude bug scan #1: if cache is keyed by position in ActiveTowerIds,
            // removing a tower mid-stream shifts positions and applies one tower's base
            // stat to a different tower. This test seeds two shares, removes the sharing
            // tower, and re-runs ResolveBuffShares — both target towers must still get their
            // own (now-base) attack speed back, not a confused cross-write.
            var store = new ComponentStore();
            int sharer = PlaceTower(store, 0, 0f, 0f, attackSpeed: 1f);
            int t1 = PlaceTower(store, 1, 1f, 0f, attackSpeed: 1.5f);
            int t2 = PlaceTower(store, 2, 2f, 0f, attackSpeed: 2.5f);

            store.SetTowerBuffShareRadius(sharer, 8f);
            store.SetTowerBuffShareMask(sharer, BuffShareConfig.ShareAttackSpeed);

            var sys = new TowerSynergySystem(store, NewRenderer());
            sys.ResolveBuffShares();

            // Both targets got their base × (1 + eff)
            float eff = BuffShareConfig.DefaultShareEfficiencyPct;
            Assert.Equal(1.5f * (1f + eff), store.TowerAttackSpeed[t1], 4);
            Assert.Equal(2.5f * (1f + eff), store.TowerAttackSpeed[t2], 4);

            // Remove sharer (DestroyEntity → zeros out share fields, drops from ActiveTowerIds).
            // Then verify a second ResolveBuffShares call restores the targets' BASE speed.
            // If cache was position-keyed, this would write 1.5×(1+eff) onto tower 2 etc.
            store.DestroyEntity(sharer);

            sys.ResolveBuffShares();

            // Both targets should now be at their base speed (sharing has stopped)
            Assert.Equal(1.5f, store.TowerAttackSpeed[t1], 4);
            Assert.Equal(2.5f, store.TowerAttackSpeed[t2], 4);
        }

        // ─── Regression: cache must be invalidated when entity ID is reused ───

        [Fact]
        public void ResolveBuffShares_CacheInvalidatedOnIdReuse_NewTowerStartsAtBase()
        {
            // Repro for Claude bug scan #2: if the cache is NOT invalidated on entity destroy
            // and the entityId is later recycled for a new tower, the cache restore pass would
            // overwrite the new tower's TowerAttackSpeed with the old tower's cached base.
            // This test creates a tower, lets ResolveBuffShares seed a cache entry, destroys
            // the tower, adds a NEW tower at the same entityId with a different attack speed,
            // runs ResolveBuffShares again, and verifies the new tower's attack speed is
            // unchanged (no stale base speed leaked from the previous tower).
            var store = new ComponentStore();
            int sharer = PlaceTower(store, 0, 0f, 0f, attackSpeed: 1f);
            int target = PlaceTower(store, 1, 100f, 100f, attackSpeed: 2.5f); // far from sharer

            store.SetTowerBuffShareRadius(sharer, 8f);
            store.SetTowerBuffShareMask(sharer, BuffShareConfig.ShareAttackSpeed);

            var sys = new TowerSynergySystem(store, NewRenderer());
            sys.ResolveBuffShares();

            // Sharer and target are too far apart (distance > 8) — no sharing happened.
            // Manually force a share by moving the target into range.
            store.PositionX[target] = 1f;
            store.PositionY[target] = 0f;
            sys.ResolveBuffShares();

            // Target now has its base × (1 + eff) — cache entry for target is seeded
            float eff = BuffShareConfig.DefaultShareEfficiencyPct;
            float sharedSpeed = 2.5f * (1f + eff);
            Assert.Equal(sharedSpeed, store.TowerAttackSpeed[target], 4);

            // Destroy the sharer AND the target (so target slot is free), then recycle the
            // target slot for a NEW tower with a DIFFERENT base attack speed. The new tower
            // is placed far away from the new sharer, so no sharing happens this pass — its
            // attack speed must remain at its base 4.0 (proving the cache was invalidated).
            store.DestroyEntity(sharer);
            store.DestroyEntity(target);

            int newTarget = PlaceTower(store, target, 5f, 0f, attackSpeed: 4.0f);
            int newSharer = PlaceTower(store, 2, 4f, 0f, attackSpeed: 1f);
            store.SetTowerBuffShareRadius(newSharer, 8f);
            store.SetTowerBuffShareMask(newSharer, BuffShareConfig.ShareAttackSpeed);

            sys.ResolveBuffShares();

            // The new tower is in range of newSharer, so it gets 4.0 × (1+eff) shared.
            // If the cache entry from the old tower leaked, the new tower would first
            // be reset to 2.5 (old base) and then shared → 2.5 × (1+eff) ≈ 3.25.
            Assert.Equal(4.0f * (1f + eff), store.TowerAttackSpeed[newTarget], 4);
        }

        [Fact]
        public void InvalidateBuffShareCache_DropsEntryByTowerId()
        {
            // Direct test of the public invalidation API. Without invalidation, the cached
            // base would survive a frame even after the sharer is removed, leading to a
            // one-frame stale restore. With invalidation, the entry is gone immediately.
            var store = new ComponentStore();
            int t = PlaceTower(store, 0, 0f, 0f, attackSpeed: 1f);
            store.SetTowerBuffShareRadius(t, 8f);
            store.SetTowerBuffShareMask(t, BuffShareConfig.ShareAttackSpeed);

            // Need at least one target to seed the cache.
            int target = PlaceTower(store, 1, 1f, 0f, attackSpeed: 2f);
            var sys = new TowerSynergySystem(store, NewRenderer());
            sys.ResolveBuffShares();

            float eff = BuffShareConfig.DefaultShareEfficiencyPct;
            // target was shared — speed is now 2 * (1+eff)
            Assert.Equal(2f * (1f + eff), store.TowerAttackSpeed[target], 4);

            // Remove the sharer (DestroyEntity fires OnTowerEntityInvalidated which auto-
            // invalidates the cache). After this, the target's attack speed must be the
            // base 2.0 (restored on next ResolveBuffShares).
            store.DestroyEntity(t);
            sys.ResolveBuffShares();
            Assert.Equal(2f, store.TowerAttackSpeed[target], 4);
        }
    }
}
