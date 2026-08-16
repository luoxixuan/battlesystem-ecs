using BattleSystemECS.Tests.Infrastructure;
using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Features.Towers
{
    /// <summary>
    /// Tests for Round 173 Direction 1: Shrine Tower (pure-buff aura, no attack).
    /// Verifies:
    ///   1. SOA fields default to 0 / false (zero-overhead on hot path)
    ///   2. AddTower of a Shrine populates the fields correctly
    ///   3. DestroyEntity resets all Shrine fields (ID-reuse safety)
    ///   4. BeginFrame resets the per-frame cache arrays to 0
    ///   5. SetTurn with no shrine on field is O(1) (no crash)
    ///   6. ResolveShrineBuffs with no shrine on field is O(1) (no crash)
    ///   7. Single Gold shrine buffs a nearby tower's cached gold bonus
    ///   8. Shrine does NOT buff itself
    ///   9. Target outside radius is not buffed
    ///  10. AuraType=0 (None) produces no buff
    ///  11. Potency=0 produces no buff
    ///  12. Two shrines in range stack additively
    ///  13. Damage aura populates the damage cache array
    ///  14. AttackSpeed aura populates the atk-spd cache array
    ///  15. Mana aura populates the mana regen cache array
    ///  16. Dispelled tower does not receive shrine buff
    ///  17. Inactive (TowerActive=false) tower is skipped
    ///  18. Unknown aura type is defensive default (no crash, no buff)
    ///  19. Read helpers (GetCachedGoldBonus etc.) return the right values
    /// </summary>
    public class TowerShrineSystemTests
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

        // Helper: place a non-shrine attack tower at (x,y) with given type
        private static int PlaceAttackTower(ComponentStore store, MockRenderer r, int x, int y, TowerType type)
        {
            var tps = new TowerPlacementSystem(store, r);
            TestWorld.DisablePerTypeTowerCaps(store);
            return tps.PlaceTower(x, y, type, 10f, 3, 1f, 25f);
        }

        // ─── Field defaults ────────────────────────────────────────────────

        [Fact]
        public void ComponentStore_ShrineFields_DefaultToZero_OnAddTower()
        {
            // All four config fields default to 0 / false → no aura (zero-overhead on hot path).
            var (store, r, _) = Env();
            int id = PlaceAttackTower(store, r, 0, 0, TowerType.Basic);
            Assert.False(store.TowerIsShrine[id]);
            Assert.Equal(0, store.TowerShrineAuraType[id]);
            Assert.Equal(0f, store.TowerShrineRadius[id]);
            Assert.Equal(0f, store.TowerShrinePotency[id]);
            // Per-frame caches also default to 0
            Assert.Equal(0f, store.TowerShrineCachedGoldBonus[id]);
            Assert.Equal(0f, store.TowerShrineCachedManaRegen[id]);
            Assert.Equal(0f, store.TowerShrineCachedDmgBonus[id]);
            Assert.Equal(0f, store.TowerShrineCachedAtkSpdBonus[id]);
        }

        // ─── PlaceTower post-init ──────────────────────────────────────────

        [Fact]
        public void PlaceTower_Shrine_SetsShrineFields()
        {
            var (store, r, tps) = Env();
            int id = tps.PlaceTower(5, 5, TowerType.Shrine, 0f, 0, 0f, 50f);
            Assert.True(store.TowerIsShrine[id]);
            Assert.Equal(1, store.TowerShrineAuraType[id]); // 1 = Gold (default)
            Assert.Equal(3.0f, store.TowerShrineRadius[id]);
            Assert.Equal(0.10f, store.TowerShrinePotency[id]);
        }

        // ─── DestroyEntity reset ───────────────────────────────────────────

        [Fact]
        public void ComponentStore_ShrineFields_Reset_OnDestroyEntity()
        {
            // CRITICAL: ID-reuse safety. After destroying a shrine and placing a fresh
            // tower in the recycled slot, the new tower must NOT inherit shrine state.
            var (store, r, tps) = Env();
            int shrineId = tps.PlaceTower(5, 5, TowerType.Shrine, 0f, 0, 0f, 50f);
            Assert.True(store.TowerIsShrine[shrineId]);
            store.DestroyEntity(shrineId);
            int newId = tps.PlaceTower(6, 6, TowerType.Basic, 10f, 3, 1f, 25f);
            // The new tower should be a regular Basic tower, NOT inherit shrine state
            Assert.False(store.TowerIsShrine[newId]);
            Assert.Equal(0, store.TowerShrineAuraType[newId]);
            Assert.Equal(0f, store.TowerShrineRadius[newId]);
            Assert.Equal(0f, store.TowerShrinePotency[newId]);
            Assert.Equal(0f, store.TowerShrineCachedGoldBonus[newId]);
        }

        // ─── BeginFrame per-frame reset ────────────────────────────────────

        [Fact]
        public void BeginFrame_ResetsShrinePerFrameCaches()
        {
            // BeginFrame() must wipe the per-frame cache arrays so the next frame's
            // ResolveShrineBuffs() starts from a clean slate (no accumulation drift).
            var (store, r, tps) = Env();
            int shrineId = tps.PlaceTower(5, 5, TowerType.Shrine, 0f, 0, 0f, 50f);
            int targetId = PlaceAttackTower(store, r, 5, 6, TowerType.Basic);
            // Simulate one frame's worth of shrine resolution
            store.TowerShrineCachedGoldBonus[targetId] = 0.99f;
            store.TowerShrineCachedDmgBonus[targetId] = 0.42f;
            store.BeginFrame();
            // Caches should be wiped
            Assert.Equal(0f, store.TowerShrineCachedGoldBonus[targetId]);
            Assert.Equal(0f, store.TowerShrineCachedDmgBonus[targetId]);
            // The shrine's config fields are NOT touched by BeginFrame (those are persistent)
            Assert.True(store.TowerIsShrine[shrineId]);
            Assert.Equal(0.10f, store.TowerShrinePotency[shrineId]);
        }

        // ─── No-shrine fast paths ──────────────────────────────────────────

        [Fact]
        public void SetTurn_NoShrineOnField_DoesNotCrash()
        {
            var (store, r, _) = Env();
            PlaceAttackTower(store, r, 0, 0, TowerType.Basic);
            PlaceAttackTower(store, r, 5, 5, TowerType.Sniper);
            var shrineSys = new TowerShrineSystem(store);
            shrineSys.SetTurn();
            Assert.False(shrineSys.AnyShrineOnField);
        }

        [Fact]
        public void ResolveShrineBuffs_NoShrineOnField_DoesNotCrash()
        {
            var (store, r, _) = Env();
            int targetId = PlaceAttackTower(store, r, 0, 0, TowerType.Basic);
            var shrineSys = new TowerShrineSystem(store);
            shrineSys.SetTurn();
            shrineSys.ResolveShrineBuffs();
            Assert.Equal(0f, shrineSys.GetCachedGoldBonus(targetId));
        }

        // ─── Gold aura ─────────────────────────────────────────────────────

        [Fact]
        public void GoldShrine_BuffsNearbyTower_CachedGoldBonus()
        {
            var (store, r, tps) = Env();
            int shrineId = tps.PlaceTower(5, 5, TowerType.Shrine, 0f, 0, 0f, 50f);
            int targetId = PlaceAttackTower(store, r, 5, 6, TowerType.Basic);
            var shrineSys = new TowerShrineSystem(store);
            shrineSys.SetTurn();
            shrineSys.ResolveShrineBuffs();
            Assert.Equal(0.10f, shrineSys.GetCachedGoldBonus(targetId));
        }

        // ─── Self-buff skip ────────────────────────────────────────────────

        [Fact]
        public void Shrine_DoesNotBuffItself()
        {
            var (store, r, tps) = Env();
            int shrineId = tps.PlaceTower(5, 5, TowerType.Shrine, 0f, 0, 0f, 50f);
            var shrineSys = new TowerShrineSystem(store);
            shrineSys.SetTurn();
            shrineSys.ResolveShrineBuffs();
            Assert.Equal(0f, shrineSys.GetCachedGoldBonus(shrineId));
        }

        // ─── Range gate ────────────────────────────────────────────────────

        [Fact]
        public void Target_OutsideRadius_NotBuffed()
        {
            var (store, r, tps) = Env();
            int shrineId = tps.PlaceTower(5, 5, TowerType.Shrine, 0f, 0, 0f, 50f);
            // Place a tower 10 cells away (radius is 3)
            int farTargetId = PlaceAttackTower(store, r, 15, 15, TowerType.Basic);
            var shrineSys = new TowerShrineSystem(store);
            shrineSys.SetTurn();
            shrineSys.ResolveShrineBuffs();
            Assert.Equal(0f, shrineSys.GetCachedGoldBonus(farTargetId));
        }

        // ─── Inert aura types ──────────────────────────────────────────────

        [Fact]
        public void AuraType_None_ProducesNoBuff()
        {
            var (store, r, tps) = Env();
            int shrineId = tps.PlaceTower(5, 5, TowerType.Shrine, 0f, 0, 0f, 50f);
            store.TowerShrineAuraType[shrineId] = 0; // 0 = None
            int targetId = PlaceAttackTower(store, r, 5, 6, TowerType.Basic);
            var shrineSys = new TowerShrineSystem(store);
            shrineSys.SetTurn();
            shrineSys.ResolveShrineBuffs();
            Assert.Equal(0f, shrineSys.GetCachedGoldBonus(targetId));
        }

        [Fact]
        public void Potency_Zero_ProducesNoBuff()
        {
            var (store, r, tps) = Env();
            int shrineId = tps.PlaceTower(5, 5, TowerType.Shrine, 0f, 0, 0f, 50f);
            store.TowerShrinePotency[shrineId] = 0f;
            int targetId = PlaceAttackTower(store, r, 5, 6, TowerType.Basic);
            var shrineSys = new TowerShrineSystem(store);
            shrineSys.SetTurn();
            shrineSys.ResolveShrineBuffs();
            Assert.Equal(0f, shrineSys.GetCachedGoldBonus(targetId));
        }

        // ─── Stacking ──────────────────────────────────────────────────────

        [Fact]
        public void TwoShrinesInRange_StackAdditively()
        {
            var (store, r, tps) = Env();
            int s1 = tps.PlaceTower(5, 5, TowerType.Shrine, 0f, 0, 0f, 50f);
            int s2 = tps.PlaceTower(5, 6, TowerType.Shrine, 0f, 0, 0f, 50f);
            int targetId = PlaceAttackTower(store, r, 5, 7, TowerType.Basic);
            var shrineSys = new TowerShrineSystem(store);
            shrineSys.SetTurn();
            shrineSys.ResolveShrineBuffs();
            // 0.10 + 0.10 = 0.20
            Assert.Equal(0.20f, shrineSys.GetCachedGoldBonus(targetId), 3);
        }

        // ─── Damage aura ───────────────────────────────────────────────────

        [Fact]
        public void DamageShrine_PopulatesDmgCache()
        {
            var (store, r, tps) = Env();
            int shrineId = tps.PlaceTower(5, 5, TowerType.Shrine, 0f, 0, 0f, 50f);
            store.TowerShrineAuraType[shrineId] = 3; // 3 = Damage
            store.TowerShrinePotency[shrineId] = 0.15f;
            int targetId = PlaceAttackTower(store, r, 5, 6, TowerType.Basic);
            var shrineSys = new TowerShrineSystem(store);
            shrineSys.SetTurn();
            shrineSys.ResolveShrineBuffs();
            Assert.Equal(0.15f, shrineSys.GetCachedDamageBonus(targetId));
        }

        // ─── AttackSpeed aura ─────────────────────────────────────────────

        [Fact]
        public void AttackSpeedShrine_PopulatesAtkSpdCache()
        {
            var (store, r, tps) = Env();
            int shrineId = tps.PlaceTower(5, 5, TowerType.Shrine, 0f, 0, 0f, 50f);
            store.TowerShrineAuraType[shrineId] = 4; // 4 = AttackSpeed
            store.TowerShrinePotency[shrineId] = 0.20f;
            int targetId = PlaceAttackTower(store, r, 5, 6, TowerType.Basic);
            var shrineSys = new TowerShrineSystem(store);
            shrineSys.SetTurn();
            shrineSys.ResolveShrineBuffs();
            Assert.Equal(0.20f, shrineSys.GetCachedAttackSpeedBonus(targetId));
        }

        // ─── Mana aura ─────────────────────────────────────────────────────

        [Fact]
        public void ManaShrine_PopulatesManaRegenCache()
        {
            var (store, r, tps) = Env();
            int shrineId = tps.PlaceTower(5, 5, TowerType.Shrine, 0f, 0, 0f, 50f);
            store.TowerShrineAuraType[shrineId] = 2; // 2 = Mana
            store.TowerShrinePotency[shrineId] = 0.5f;
            int targetId = PlaceAttackTower(store, r, 5, 6, TowerType.Basic);
            var shrineSys = new TowerShrineSystem(store);
            shrineSys.SetTurn();
            shrineSys.ResolveShrineBuffs();
            Assert.Equal(0.5f, shrineSys.GetCachedManaRegen(targetId));
        }

        // ─── Dispelled tower ──────────────────────────────────────────────

        [Fact]
        public void DispelledTower_DoesNotReceiveShrineBuff()
        {
            var (store, r, tps) = Env();
            int shrineId = tps.PlaceTower(5, 5, TowerType.Shrine, 0f, 0, 0f, 50f);
            int targetId = PlaceAttackTower(store, r, 5, 6, TowerType.Basic);
            store.TowerIsDispelled[targetId] = true;
            var shrineSys = new TowerShrineSystem(store);
            shrineSys.SetTurn();
            shrineSys.ResolveShrineBuffs();
            Assert.Equal(0f, shrineSys.GetCachedGoldBonus(targetId));
        }

        // ─── Inactive tower ───────────────────────────────────────────────

        [Fact]
        public void InactiveTower_IsSkipped()
        {
            var (store, r, tps) = Env();
            int shrineId = tps.PlaceTower(5, 5, TowerType.Shrine, 0f, 0, 0f, 50f);
            int targetId = PlaceAttackTower(store, r, 5, 6, TowerType.Basic);
            store.TowerActive[targetId] = false;
            var shrineSys = new TowerShrineSystem(store);
            shrineSys.SetTurn();
            shrineSys.ResolveShrineBuffs();
            Assert.Equal(0f, shrineSys.GetCachedGoldBonus(targetId));
        }

        // ─── Defensive: unknown aura type ──────────────────────────────────

        [Fact]
        public void UnknownAuraType_DefensiveDefault_NoCrashNoBuff()
        {
            var (store, r, tps) = Env();
            int shrineId = tps.PlaceTower(5, 5, TowerType.Shrine, 0f, 0, 0f, 50f);
            store.TowerShrineAuraType[shrineId] = 99; // Unknown (not 0..4)
            int targetId = PlaceAttackTower(store, r, 5, 6, TowerType.Basic);
            var shrineSys = new TowerShrineSystem(store);
            shrineSys.SetTurn();
            shrineSys.ResolveShrineBuffs();
            // All caches should remain 0
            Assert.Equal(0f, shrineSys.GetCachedGoldBonus(targetId));
            Assert.Equal(0f, shrineSys.GetCachedManaRegen(targetId));
            Assert.Equal(0f, shrineSys.GetCachedDamageBonus(targetId));
            Assert.Equal(0f, shrineSys.GetCachedAttackSpeedBonus(targetId));
        }

        // ─── Config file presence ─────────────────────────────────────────

        // ─── Dispel doesn't affect non-dispelled ──────────────────────────

        [Fact]
        public void DispelFlag_OnlySkipsDispelledTowers()
        {
            var (store, r, tps) = Env();
            int shrineId = tps.PlaceTower(5, 5, TowerType.Shrine, 0f, 0, 0f, 50f);
            int t1 = PlaceAttackTower(store, r, 5, 6, TowerType.Basic);
            int t2 = PlaceAttackTower(store, r, 5, 7, TowerType.Basic);
            store.TowerIsDispelled[t1] = true;
            var shrineSys = new TowerShrineSystem(store);
            shrineSys.SetTurn();
            shrineSys.ResolveShrineBuffs();
            Assert.Equal(0f, shrineSys.GetCachedGoldBonus(t1));     // dispelled
            Assert.Equal(0.10f, shrineSys.GetCachedGoldBonus(t2));   // not dispelled
        }
    }
}
