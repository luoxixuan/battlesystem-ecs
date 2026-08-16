using BattleSystemECS.Tests.Infrastructure;
using System;
using System.IO;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Features.Towers
{
    /// <summary>
    /// Tests for Round 177 Direction 2: Beacon Tower (active command-post broadcast buff, no attack).
    /// Verifies:
    ///   1. SOA fields default to 0 / false (zero-overhead on hot path)
    ///   2. AddTower of a Beacon populates the fields correctly
    ///   3. DestroyEntity resets all Beacon fields (ID-reuse safety)
    ///   4. BeginFrame resets the per-frame cache arrays to 0
    ///   5. SetTurn with no beacon on field is O(1) (no crash)
    ///   6. ResolveBeaconBuffs with no beacon on field is O(1) (no crash)
    ///   7. Single Beacon buffs a nearby tower's cached damage bonus
    ///   8. Single Beacon buffs a nearby tower's cached attack-speed bonus
    ///   9. Beacon does NOT buff itself
    ///  10. Target outside radius is not buffed
    ///  11. Two beacons in range stack additively
    ///  12. Dispelled tower does not receive beacon buff
    ///  13. Inactive (TowerActive=false) tower is skipped
    ///  14. Radius=0 beacon is inert (no buff applied)
    ///  15. Both-bonus-zero beacon is inert (no buff applied)
    ///  16. Read helpers (GetCachedDamageBonus / GetCachedAttackSpeedBonus) return right values
    ///  17. Beacon read helper bounds-check (negative towerId returns 0)
    ///  18. Two-beacon cluster: each in range of all 3 targets
    /// </summary>
    public class TowerBeaconSystemTests
    {
        private const int PlayerId = 0;

        private static (ComponentStore store, MockRenderer renderer, TowerPlacementSystem tps) Env()
        {
            var store = new ComponentStore();
            int pid = store.CreateEntity();
            store.PlayerMaxHealth[pid] = 200f;
            store.PlayerCurrentHealth[pid] = 200f;
            store.PositionX[pid] = 5f;
            store.PositionY[pid] = 0f;
            store.SetPlayerGold(pid, 9999f);
            var tps = new TowerPlacementSystem(store, new MockRenderer());
            TestWorld.DisablePerTypeTowerCaps(store);
            return (store, new MockRenderer(), tps);
        }

        // Helper: place a non-beacon attack tower at (x,y) with given type
        private static int PlaceAttackTower(ComponentStore store, MockRenderer r, int x, int y, TowerType type)
        {
            var tps = new TowerPlacementSystem(store, r);
            TestWorld.DisablePerTypeTowerCaps(store);
            return tps.PlaceTower(x, y, type, 10f, 3, 1f, 25f);
        }

        // ─── Field defaults ────────────────────────────────────────────────

        [Fact]
        public void ComponentStore_BeaconFields_DefaultToZero_OnAddTower()
        {
            // All four config fields default to 0 / false → no broadcast (zero-overhead on hot path).
            var (store, r, _) = Env();
            int id = PlaceAttackTower(store, r, 0, 0, TowerType.Basic);
            Assert.False(store.TowerIsBeacon[id]);
            Assert.Equal(0f, store.TowerBeaconRadius[id]);
            Assert.Equal(0f, store.TowerBeaconDmgBonus[id]);
            Assert.Equal(0f, store.TowerBeaconAtkSpdBonus[id]);
            // Per-frame caches also default to 0
            Assert.Equal(0f, store.TowerBeaconCachedDmgBonus[id]);
            Assert.Equal(0f, store.TowerBeaconCachedAtkSpdBonus[id]);
        }

        // ─── PlaceTower post-init ──────────────────────────────────────────

        [Fact]
        public void PlaceTower_Beacon_SetsBeaconFields()
        {
            var (store, r, tps) = Env();
            int id = tps.PlaceTower(5, 5, TowerType.Beacon, 0f, 0, 0f, 50f);
            Assert.True(store.TowerIsBeacon[id]);
            Assert.Equal(3.0f, store.TowerBeaconRadius[id]);
            Assert.Equal(0.10f, store.TowerBeaconDmgBonus[id]);
            Assert.Equal(0.10f, store.TowerBeaconAtkSpdBonus[id]);
        }

        // ─── DestroyEntity reset ───────────────────────────────────────────

        [Fact]
        public void ComponentStore_BeaconFields_Reset_OnDestroyEntity()
        {
            // CRITICAL: ID-reuse safety. After destroying a beacon and placing a fresh
            // tower in the recycled slot, the new tower must NOT inherit beacon state.
            var (store, r, tps) = Env();
            int beaconId = tps.PlaceTower(5, 5, TowerType.Beacon, 0f, 0, 0f, 50f);
            Assert.True(store.TowerIsBeacon[beaconId]);
            store.DestroyEntity(beaconId);
            int newId = tps.PlaceTower(6, 6, TowerType.Basic, 10f, 3, 1f, 25f);
            // The new tower should be a regular Basic tower, NOT inherit beacon state
            Assert.False(store.TowerIsBeacon[newId]);
            Assert.Equal(0f, store.TowerBeaconRadius[newId]);
            Assert.Equal(0f, store.TowerBeaconDmgBonus[newId]);
            Assert.Equal(0f, store.TowerBeaconAtkSpdBonus[newId]);
            Assert.Equal(0f, store.TowerBeaconCachedDmgBonus[newId]);
            Assert.Equal(0f, store.TowerBeaconCachedAtkSpdBonus[newId]);
        }

        // ─── BeginFrame per-frame reset ────────────────────────────────────

        [Fact]
        public void BeginFrame_ResetsBeaconPerFrameCaches()
        {
            // BeginFrame() must wipe the per-frame cache arrays so the next frame's
            // ResolveBeaconBuffs() starts from a clean slate (no accumulation drift).
            var (store, r, tps) = Env();
            int beaconId = tps.PlaceTower(5, 5, TowerType.Beacon, 0f, 0, 0f, 50f);
            int targetId = PlaceAttackTower(store, r, 5, 6, TowerType.Basic);
            // Simulate one frame's worth of beacon resolution
            store.TowerBeaconCachedDmgBonus[targetId] = 0.99f;
            store.TowerBeaconCachedAtkSpdBonus[targetId] = 0.42f;
            store.BeginFrame();
            // Caches should be wiped
            Assert.Equal(0f, store.TowerBeaconCachedDmgBonus[targetId]);
            Assert.Equal(0f, store.TowerBeaconCachedAtkSpdBonus[targetId]);
            // The beacon's config fields are NOT touched by BeginFrame (those are persistent)
            Assert.True(store.TowerIsBeacon[beaconId]);
            Assert.Equal(0.10f, store.TowerBeaconDmgBonus[beaconId]);
        }

        // ─── No-beacon fast paths ──────────────────────────────────────────

        [Fact]
        public void SetTurn_NoBeaconOnField_DoesNotCrash()
        {
            var (store, r, _) = Env();
            PlaceAttackTower(store, r, 0, 0, TowerType.Basic);
            PlaceAttackTower(store, r, 5, 5, TowerType.Sniper);
            var beaconSys = new TowerBeaconSystem(store);
            beaconSys.SetTurn();
            Assert.False(beaconSys.AnyBeaconOnField);
        }

        [Fact]
        public void ResolveBeaconBuffs_NoBeaconOnField_DoesNotCrash()
        {
            var (store, r, _) = Env();
            int targetId = PlaceAttackTower(store, r, 0, 0, TowerType.Basic);
            var beaconSys = new TowerBeaconSystem(store);
            beaconSys.SetTurn();
            beaconSys.ResolveBeaconBuffs();
            Assert.Equal(0f, beaconSys.GetCachedDamageBonus(targetId));
            Assert.Equal(0f, beaconSys.GetCachedAttackSpeedBonus(targetId));
        }

        // ─── Damage / AtkSpd aura broadcast ────────────────────────────────

        [Fact]
        public void SingleBeacon_BuffsNearbyTower_CachedDamageBonus()
        {
            var (store, r, tps) = Env();
            tps.PlaceTower(5, 5, TowerType.Beacon, 0f, 0, 0f, 50f);
            int targetId = PlaceAttackTower(store, r, 5, 6, TowerType.Basic);
            var beaconSys = new TowerBeaconSystem(store);
            beaconSys.SetTurn();
            beaconSys.ResolveBeaconBuffs();
            Assert.Equal(0.10f, beaconSys.GetCachedDamageBonus(targetId));
        }

        [Fact]
        public void SingleBeacon_BuffsNearbyTower_CachedAttackSpeedBonus()
        {
            var (store, r, tps) = Env();
            tps.PlaceTower(5, 5, TowerType.Beacon, 0f, 0, 0f, 50f);
            int targetId = PlaceAttackTower(store, r, 5, 6, TowerType.Basic);
            var beaconSys = new TowerBeaconSystem(store);
            beaconSys.SetTurn();
            beaconSys.ResolveBeaconBuffs();
            Assert.Equal(0.10f, beaconSys.GetCachedAttackSpeedBonus(targetId));
        }

        // ─── Self-buff skip ────────────────────────────────────────────────

        [Fact]
        public void Beacon_DoesNotBuffItself()
        {
            var (store, r, tps) = Env();
            int beaconId = tps.PlaceTower(5, 5, TowerType.Beacon, 0f, 0, 0f, 50f);
            var beaconSys = new TowerBeaconSystem(store);
            beaconSys.SetTurn();
            beaconSys.ResolveBeaconBuffs();
            Assert.Equal(0f, beaconSys.GetCachedDamageBonus(beaconId));
            Assert.Equal(0f, beaconSys.GetCachedAttackSpeedBonus(beaconId));
        }

        // ─── Range gate ────────────────────────────────────────────────────

        [Fact]
        public void Target_OutsideRadius_NotBuffed()
        {
            var (store, r, tps) = Env();
            tps.PlaceTower(5, 5, TowerType.Beacon, 0f, 0, 0f, 50f);
            // Place a tower 10 cells away (radius is 3)
            int farTargetId = PlaceAttackTower(store, r, 15, 15, TowerType.Basic);
            var beaconSys = new TowerBeaconSystem(store);
            beaconSys.SetTurn();
            beaconSys.ResolveBeaconBuffs();
            Assert.Equal(0f, beaconSys.GetCachedDamageBonus(farTargetId));
            Assert.Equal(0f, beaconSys.GetCachedAttackSpeedBonus(farTargetId));
        }

        // ─── Additive stacking ─────────────────────────────────────────────

        [Fact]
        public void TwoBeaconsInRange_StackAdditively()
        {
            var (store, r, tps) = Env();
            tps.PlaceTower(3, 5, TowerType.Beacon, 0f, 0, 0f, 50f);
            tps.PlaceTower(7, 5, TowerType.Beacon, 0f, 0, 0f, 50f);
            // Target at (5, 5) is within radius 3 of both beacons
            int targetId = PlaceAttackTower(store, r, 5, 5, TowerType.Basic);
            var beaconSys = new TowerBeaconSystem(store);
            beaconSys.SetTurn();
            beaconSys.ResolveBeaconBuffs();
            Assert.Equal(0.20f, beaconSys.GetCachedDamageBonus(targetId));
            Assert.Equal(0.20f, beaconSys.GetCachedAttackSpeedBonus(targetId));
        }

        [Fact]
        public void TwoBeaconsCluster_AllTargetsInRange_BothStacks()
        {
            // 2 beacons in close cluster, 3 targets all within radius of BOTH.
            // Each target should see 2× the bonus.
            var (store, r, tps) = Env();
            int b1 = tps.PlaceTower(4, 5, TowerType.Beacon, 0f, 0, 0f, 50f);
            int b2 = tps.PlaceTower(6, 5, TowerType.Beacon, 0f, 0, 0f, 50f);
            int t1 = PlaceAttackTower(store, r, 5, 5, TowerType.Basic);
            int t2 = PlaceAttackTower(store, r, 4, 6, TowerType.Basic);
            int t3 = PlaceAttackTower(store, r, 6, 6, TowerType.Basic);
            var beaconSys = new TowerBeaconSystem(store);
            beaconSys.SetTurn();
            beaconSys.ResolveBeaconBuffs();
            Assert.Equal(0.20f, beaconSys.GetCachedDamageBonus(t1));
            Assert.Equal(0.20f, beaconSys.GetCachedDamageBonus(t2));
            Assert.Equal(0.20f, beaconSys.GetCachedDamageBonus(t3));
            // Beacons receive their peer's buff (peer is 2 cells away, within radius 3)
            // but do NOT receive their own buff. b1 ← b2's 0.10 (peer), b2 ← b1's 0.10.
            Assert.Equal(0.10f, beaconSys.GetCachedDamageBonus(b1));
            Assert.Equal(0.10f, beaconSys.GetCachedDamageBonus(b2));
        }

        [Fact]
        public void Beacon_BuffsOtherBeacon_ButNotSelf()
        {
            // Two beacons in range. Verify peer-buffing works:
            //   - b1 receives b2's bonus (peer in range)
            //   - b2 receives b1's bonus (peer in range)
            //   - neither buffs itself
            var (store, r, tps) = Env();
            int b1 = tps.PlaceTower(4, 5, TowerType.Beacon, 0f, 0, 0f, 50f);
            int b2 = tps.PlaceTower(6, 5, TowerType.Beacon, 0f, 0, 0f, 50f);
            var beaconSys = new TowerBeaconSystem(store);
            beaconSys.SetTurn();
            beaconSys.ResolveBeaconBuffs();
            // b1 receives b2's 0.10 (peer, not self), b2 receives b1's 0.10 (peer, not self).
            // Self-skip means the beacon does NOT get its own bonus stacked onto itself.
            Assert.Equal(0.10f, beaconSys.GetCachedDamageBonus(b1));
            Assert.Equal(0.10f, beaconSys.GetCachedDamageBonus(b2));
        }

        // ─── Dispelled target ──────────────────────────────────────────────

        [Fact]
        public void DispelledTower_DoesNotReceiveBeaconBuff()
        {
            var (store, r, tps) = Env();
            tps.PlaceTower(5, 5, TowerType.Beacon, 0f, 0, 0f, 50f);
            int targetId = PlaceAttackTower(store, r, 5, 6, TowerType.Basic);
            store.TowerIsDispelled[targetId] = true; // mark dispelled
            var beaconSys = new TowerBeaconSystem(store);
            beaconSys.SetTurn();
            beaconSys.ResolveBeaconBuffs();
            Assert.Equal(0f, beaconSys.GetCachedDamageBonus(targetId));
            Assert.Equal(0f, beaconSys.GetCachedAttackSpeedBonus(targetId));
        }

        // ─── Inactive target ───────────────────────────────────────────────

        [Fact]
        public void InactiveTower_NotBuffed()
        {
            var (store, r, tps) = Env();
            tps.PlaceTower(5, 5, TowerType.Beacon, 0f, 0, 0f, 50f);
            int targetId = PlaceAttackTower(store, r, 5, 6, TowerType.Basic);
            store.TowerActive[targetId] = false;
            var beaconSys = new TowerBeaconSystem(store);
            beaconSys.SetTurn();
            beaconSys.ResolveBeaconBuffs();
            Assert.Equal(0f, beaconSys.GetCachedDamageBonus(targetId));
        }

        // ─── Inert beacon configs ──────────────────────────────────────────

        [Fact]
        public void RadiusZeroBeacon_IsInert()
        {
            var (store, r, tps) = Env();
            int beaconId = tps.PlaceTower(5, 5, TowerType.Beacon, 0f, 0, 0f, 50f);
            // Force radius=0 → no broadcast (even if bonus fields are non-zero)
            store.TowerBeaconRadius[beaconId] = 0f;
            int targetId = PlaceAttackTower(store, r, 5, 6, TowerType.Basic);
            var beaconSys = new TowerBeaconSystem(store);
            beaconSys.SetTurn();
            beaconSys.ResolveBeaconBuffs();
            Assert.Equal(0f, beaconSys.GetCachedDamageBonus(targetId));
            Assert.Equal(0f, beaconSys.GetCachedAttackSpeedBonus(targetId));
        }

        [Fact]
        public void ZeroBonusBeacon_IsInert()
        {
            var (store, r, tps) = Env();
            int beaconId = tps.PlaceTower(5, 5, TowerType.Beacon, 0f, 0, 0f, 50f);
            // Force both bonus fields to 0 → no buff (radius still > 0)
            store.TowerBeaconDmgBonus[beaconId] = 0f;
            store.TowerBeaconAtkSpdBonus[beaconId] = 0f;
            int targetId = PlaceAttackTower(store, r, 5, 6, TowerType.Basic);
            var beaconSys = new TowerBeaconSystem(store);
            beaconSys.SetTurn();
            beaconSys.ResolveBeaconBuffs();
            Assert.Equal(0f, beaconSys.GetCachedDamageBonus(targetId));
            Assert.Equal(0f, beaconSys.GetCachedAttackSpeedBonus(targetId));
        }

        [Fact]
        public void DamageOnlyBeacon_StillAppliesBoth_AtkSpdStaysZero()
        {
            // If only dmg bonus is non-zero, the spd bonus remains 0 (no phantom buff).
            // This documents the per-field additive semantics.
            var (store, r, tps) = Env();
            int beaconId = tps.PlaceTower(5, 5, TowerType.Beacon, 0f, 0, 0f, 50f);
            store.TowerBeaconDmgBonus[beaconId] = 0.15f;
            store.TowerBeaconAtkSpdBonus[beaconId] = 0f;
            int targetId = PlaceAttackTower(store, r, 5, 6, TowerType.Basic);
            var beaconSys = new TowerBeaconSystem(store);
            beaconSys.SetTurn();
            beaconSys.ResolveBeaconBuffs();
            Assert.Equal(0.15f, beaconSys.GetCachedDamageBonus(targetId));
            Assert.Equal(0f, beaconSys.GetCachedAttackSpeedBonus(targetId));
        }

        // ─── Read helper bounds-check ──────────────────────────────────────

        [Fact]
        public void ReadHelpers_BoundsCheck_NegativeId_ReturnsZero()
        {
            var (store, r, _) = Env();
            var beaconSys = new TowerBeaconSystem(store);
            Assert.Equal(0f, beaconSys.GetCachedDamageBonus(-1));
            Assert.Equal(0f, beaconSys.GetCachedAttackSpeedBonus(-1));
        }

        [Fact]
        public void ReadHelpers_BoundsCheck_OutOfRange_ReturnsZero()
        {
            var (store, r, _) = Env();
            var beaconSys = new TowerBeaconSystem(store);
            Assert.Equal(0f, beaconSys.GetCachedDamageBonus(int.MaxValue));
            Assert.Equal(0f, beaconSys.GetCachedAttackSpeedBonus(int.MaxValue));
        }

        // ─── JSON config file existence ────────────────────────────────────
    }
}
