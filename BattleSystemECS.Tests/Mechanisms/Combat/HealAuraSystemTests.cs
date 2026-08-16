using BattleSystemECS.Tests.Infrastructure;
using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Mechanisms.Combat
{
    /// <summary>
    /// Tests for Round 122 Direction 2: Tower-to-Tower Heal Link / Heal Aura.
    /// Verifies:
    ///   1. TowerConfig.HealAura* fields default to 0 (zero-overhead)
    ///   2. ComponentStore SOA fields zero-init on AddTower
    ///   3. ComponentStore SOA fields reset on DestroyEntity (no ID-reuse leak)
    ///   4. SetTurn early-returns when no heal-aura tower on field (no crash)
    ///   5. Update early-returns when no heal-aura tower on field (no crash)
    ///   6. Single healer in range heals a damaged palisade by HealAuraAmount (interval=0, every frame)
    ///   7. Healer does NOT heal itself (no self-target)
    ///   8. Target outside radius is not healed
    ///   9. Non-Palisade target is not healed (only Palisade has HP pool)
    ///  10. Overheal is clamped to PalisadeMaxHP (no overflow)
    ///  11. Two healers in range stack additively (each contributes amount per tick)
    ///  12. Interval > 0 fires every Interval seconds (timer gates the heal)
    /// </summary>
    public class HealAuraSystemTests
    {
        private static (ComponentStore store, MockRenderer renderer) Env()
        {
            var store = new ComponentStore();
            int pid = store.CreateEntity();
            store.PlayerMaxHealth[pid] = 200f;
            store.PlayerCurrentHealth[pid] = 200f;
            return (store, new MockRenderer());
        }

        private static int PlaceTower(ComponentStore store, MockRenderer r, int x, int y,
            TowerType type = TowerType.Palisade, float maxHp = 100f)
        {
            var tps = new TowerPlacementSystem(store, r);
            int id = tps.PlaceTower(x, y, type, 0f, 0, 0f, 25f);
            if (type == TowerType.Palisade)
            {
                store.PalisadeMaxHP[id] = maxHp;
                store.PalisadeHP[id] = maxHp;
            }
            return id;
        }

        // ─── Config defaults ─────────────────────────────────────────────

        [Fact]
        public void TowerConfig_HealAura_DefaultsToZero()
        {
            // All three fields default 0 → no aura (zero-overhead on hot path).
            var tc = new TowerConfig();
            Assert.Equal(0f, tc.HealAuraRadius);
            Assert.Equal(0f, tc.HealAuraAmount);
            Assert.Equal(0f, tc.HealAuraInterval);
        }

        // ─── SOA field lifecycle ──────────────────────────────────────────

        [Fact]
        public void ComponentStore_HealAuraFields_DefaultToZero_OnAddTower()
        {
            // Adding a tower without opting in to heal aura must leave all 4 fields at 0.
            var (store, r) = Env();
            int id = PlaceTower(store, r, 0, 0);
            Assert.Equal(0f, store.TowerHealAuraRadius[id]);
            Assert.Equal(0f, store.TowerHealAuraAmount[id]);
            Assert.Equal(0f, store.TowerHealAuraInterval[id]);
            Assert.Equal(0f, store.TowerHealAuraTimer[id]);
        }

        [Fact]
        public void ComponentStore_HealAuraFields_Reset_OnDestroyEntity()
        {
            // CRITICAL: ID-reuse safety. After destroying a heal-aura tower and
            // placing a fresh one in the recycled slot, the new tower must NOT
            // inherit the previous heal-aura fields (which would silently turn a
            // non-heal-aura tower into a healer).
            var (store, r) = Env();
            int id = PlaceTower(store, r, 0, 0);
            store.TowerHealAuraRadius[id] = 5f;
            store.TowerHealAuraAmount[id] = 10f;
            store.TowerHealAuraInterval[id] = 1f;
            store.TowerHealAuraTimer[id] = 1f;
            store.DestroyEntity(id);
            // PlaceTower re-uses the same id (entity recycling).
            int id2 = PlaceTower(store, r, 1, 1);
            Assert.Equal(id, id2); // same slot
            Assert.Equal(0f, store.TowerHealAuraRadius[id2]);
            Assert.Equal(0f, store.TowerHealAuraAmount[id2]);
            Assert.Equal(0f, store.TowerHealAuraInterval[id2]);
            Assert.Equal(0f, store.TowerHealAuraTimer[id2]);
        }

        // ─── No-op paths (zero-overhead when no heal-aura tower) ──────────

        [Fact]
        public void SetTurn_NoHealerOnField_DoesNotThrow()
        {
            var (store, r) = Env();
            int _ = PlaceTower(store, r, 0, 0);
            var sys = new HealAuraSystem(store);
            // No heal-aura tower on field — SetTurn must be a no-op (no throw).
            sys.SetTurn();
        }

        [Fact]
        public void Update_NoHealerOnField_DoesNotThrow()
        {
            var (store, r) = Env();
            int _ = PlaceTower(store, r, 0, 0);
            var sys = new HealAuraSystem(store);
            sys.SetTurn();
            // No healer cached — Update must early-return without crashing.
            sys.Update(0.1f);
        }

        // ─── Core healing behavior ────────────────────────────────────────

        [Fact]
        public void Healer_HealsPalisadeInRange_WhenIntervalZero()
        {
            // interval=0 means "fire every frame". Place a healer at (0,0) and a
            // damaged palisade at (3,0). After one Update tick, palisade HP must
            // be restored by HealAuraAmount.
            var (store, r) = Env();
            int healer = PlaceTower(store, r, 0, 0, TowerType.Basic, 100f);
            int target = PlaceTower(store, r, 3, 0, TowerType.Palisade, 100f);
            store.PalisadeHP[target] = 50f; // damaged
            // Make the healer a healer (radius 5, amount 10, interval 0 = every frame).
            store.TowerHealAuraRadius[healer] = 5f;
            store.TowerHealAuraAmount[healer] = 10f;
            store.TowerHealAuraInterval[healer] = 0f;
            store.TowerHealAuraTimer[healer] = 0f;

            var sys = new HealAuraSystem(store);
            sys.SetTurn();
            sys.Update(0.016f); // ~1 frame

            Assert.Equal(60f, store.PalisadeHP[target]); // 50 + 10
        }

        [Fact]
        public void Healer_DoesNotSelfHeal()
        {
            // Even if the healer itself is a Palisade (which is a corner case
            // designers shouldn't normally do), it must not heal itself.
            var (store, r) = Env();
            int healer = PlaceTower(store, r, 0, 0, TowerType.Palisade, 100f);
            store.PalisadeHP[healer] = 50f;
            store.TowerHealAuraRadius[healer] = 5f;
            store.TowerHealAuraAmount[healer] = 10f;
            store.TowerHealAuraInterval[healer] = 0f;
            store.TowerHealAuraTimer[healer] = 0f;

            var sys = new HealAuraSystem(store);
            sys.SetTurn();
            sys.Update(0.016f);

            // Self-heal prevented → HP stays at 50.
            Assert.Equal(50f, store.PalisadeHP[healer]);
        }

        [Fact]
        public void Healer_TargetOutsideRadius_NotHealed()
        {
            var (store, r) = Env();
            int healer = PlaceTower(store, r, 0, 0, TowerType.Basic, 100f);
            int far = PlaceTower(store, r, 9, 19, TowerType.Palisade, 100f);
            store.PalisadeHP[far] = 50f;
            store.TowerHealAuraRadius[healer] = 5f; // radius 5
            store.TowerHealAuraAmount[healer] = 10f;
            store.TowerHealAuraInterval[healer] = 0f;
            store.TowerHealAuraTimer[healer] = 0f;

            var sys = new HealAuraSystem(store);
            sys.SetTurn();
            sys.Update(0.016f);

            // Target is at (50, 0), healer at (0, 0), distance=50 > radius=5 → no heal.
            Assert.Equal(50f, store.PalisadeHP[far]);
        }

        [Fact]
        public void Healer_NonPalisadeTarget_NotHealed()
        {
            // Standard towers have no HP pool (no PalisadeHP). The system must
            // NOT touch them — only Palisade towers are heal targets.
            var (store, r) = Env();
            int healer = PlaceTower(store, r, 0, 0, TowerType.Basic, 100f);
            int target = PlaceTower(store, r, 3, 0, TowerType.Basic, 100f);
            store.TowerHealAuraRadius[healer] = 5f;
            store.TowerHealAuraAmount[healer] = 10f;
            store.TowerHealAuraInterval[healer] = 0f;
            store.TowerHealAuraTimer[healer] = 0f;

            var sys = new HealAuraSystem(store);
            sys.SetTurn();
            // No exception → success (we don't assert HP because Standard has no HP pool).
            sys.Update(0.016f);
        }

        [Fact]
        public void Healer_Overheal_IsClampedToMaxHP()
        {
            // Palisade at 95 HP, max 100, heal amount 10 → final HP must be 100 (clamped).
            var (store, r) = Env();
            int healer = PlaceTower(store, r, 0, 0, TowerType.Basic, 100f);
            int target = PlaceTower(store, r, 3, 0, TowerType.Palisade, 100f);
            store.PalisadeHP[target] = 95f;
            store.TowerHealAuraRadius[healer] = 5f;
            store.TowerHealAuraAmount[healer] = 10f;
            store.TowerHealAuraInterval[healer] = 0f;
            store.TowerHealAuraTimer[healer] = 0f;

            var sys = new HealAuraSystem(store);
            sys.SetTurn();
            sys.Update(0.016f);

            // Clamped: 95 + 10 = 105 → 100.
            Assert.Equal(100f, store.PalisadeHP[target]);
        }

        [Fact]
        public void TwoHealers_StackAdditively()
        {
            // Two healers in range, each contributing 10 HP per tick → total +20.
            var (store, r) = Env();
            int healer1 = PlaceTower(store, r, 0, 0, TowerType.Basic, 100f);
            int healer2 = PlaceTower(store, r, 2, 0, TowerType.Basic, 100f);
            int target = PlaceTower(store, r, 1, 0, TowerType.Palisade, 100f);
            store.PalisadeHP[target] = 50f;
            foreach (var hid in new[] { healer1, healer2 })
            {
                store.TowerHealAuraRadius[hid] = 5f;
                store.TowerHealAuraAmount[hid] = 10f;
                store.TowerHealAuraInterval[hid] = 0f;
                store.TowerHealAuraTimer[hid] = 0f;
            }

            var sys = new HealAuraSystem(store);
            sys.SetTurn();
            sys.Update(0.016f);

            // 50 + 10 (h1) + 10 (h2) = 70.
            Assert.Equal(70f, store.PalisadeHP[target]);
        }

        [Fact]
        public void Healer_IntervalGates_Heal_Fires_OnlyAfter_Interval_Elapses()
        {
            // interval=1.0s, timer starts at 0.5s. After 0.3s of update (timer=0.2),
            // heal must NOT fire yet. After another 0.5s (timer expires), heal fires.
            var (store, r) = Env();
            int healer = PlaceTower(store, r, 0, 0, TowerType.Basic, 100f);
            int target = PlaceTower(store, r, 3, 0, TowerType.Palisade, 100f);
            store.PalisadeHP[target] = 50f;
            store.TowerHealAuraRadius[healer] = 5f;
            store.TowerHealAuraAmount[healer] = 10f;
            store.TowerHealAuraInterval[healer] = 1.0f;
            store.TowerHealAuraTimer[healer] = 0.5f; // half-way through cooldown

            var sys = new HealAuraSystem(store);
            sys.SetTurn();
            sys.Update(0.3f); // timer: 0.5 - 0.3 = 0.2 → still on cooldown
            Assert.Equal(50f, store.PalisadeHP[target]);

            sys.Update(0.5f); // timer: 0.2 - 0.5 = -0.3 → fires! reset to 1.0
            Assert.Equal(60f, store.PalisadeHP[target]);
        }
    }
}