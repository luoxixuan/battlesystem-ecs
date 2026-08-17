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
    public class HealAuraSystemTests : BattleTestBase
    {
        private void InitEnv()
        {
            int pid = Store.CreateEntity();
            Store.PlayerMaxHealth[pid] = 200f;
            Store.PlayerCurrentHealth[pid] = 200f;
        }

        private int PlaceTower(int x, int y,
            TowerType type = TowerType.Palisade, float maxHp = 100f)
        {
            int id = Tower(x, y, type, t =>
            {
                t.Damage = 0f;
                t.Range = 0;
                t.Speed = 0f;
                t.Cost = 25f;
            });
            if (type == TowerType.Palisade)
            {
                Store.PalisadeMaxHP[id] = maxHp;
                Store.PalisadeHP[id] = maxHp;
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
            InitEnv();
            int id = PlaceTower(0, 0);
            Assert.Equal(0f, Store.TowerHealAuraRadius[id]);
            Assert.Equal(0f, Store.TowerHealAuraAmount[id]);
            Assert.Equal(0f, Store.TowerHealAuraInterval[id]);
            Assert.Equal(0f, Store.TowerHealAuraTimer[id]);
        }

        [Fact]
        public void ComponentStore_HealAuraFields_Reset_OnDestroyEntity()
        {
            // CRITICAL: ID-reuse safety. After destroying a heal-aura tower and
            // placing a fresh one in the recycled slot, the new tower must NOT
            // inherit the previous heal-aura fields (which would silently turn a
            // non-heal-aura tower into a healer).
            InitEnv();
            int id = PlaceTower(0, 0);
            Store.TowerHealAuraRadius[id] = 5f;
            Store.TowerHealAuraAmount[id] = 10f;
            Store.TowerHealAuraInterval[id] = 1f;
            Store.TowerHealAuraTimer[id] = 1f;
            Store.DestroyEntity(id);
            // PlaceTower re-uses the same id (entity recycling).
            int id2 = PlaceTower(1, 1);
            Assert.Equal(id, id2); // same slot
            Assert.Equal(0f, Store.TowerHealAuraRadius[id2]);
            Assert.Equal(0f, Store.TowerHealAuraAmount[id2]);
            Assert.Equal(0f, Store.TowerHealAuraInterval[id2]);
            Assert.Equal(0f, Store.TowerHealAuraTimer[id2]);
        }

        // ─── No-op paths (zero-overhead when no heal-aura tower) ──────────

        [Fact]
        public void SetTurn_NoHealerOnField_DoesNotWriteTowerFields()
        {
            InitEnv();
            PlaceTower(0, 0); // 普通塔，无治疗光环
            int palisade = PlaceTower(3, 0, TowerType.Palisade, 100f);
            Store.PalisadeHP[palisade] = 50f; // 已知受伤状态

            var sys = new HealAuraSystem(Store);
            // No heal-aura tower on field — SetTurn 只建缓存，不得改写任何塔字段。
            sys.SetTurn();

            Assert.Equal(50f, Store.PalisadeHP[palisade]);
        }

        [Fact]
        public void Update_NoHealerOnField_DoesNotHeal()
        {
            InitEnv();
            PlaceTower(0, 0); // 普通塔，无治疗光环
            int palisade = PlaceTower(3, 0, TowerType.Palisade, 100f);
            Store.PalisadeHP[palisade] = 50f; // 已知受伤状态

            var sys = new HealAuraSystem(Store);
            sys.SetTurn();
            // No healer cached — Update 早退，受伤 Palisade 必须精确保持原值。
            sys.Update(0.1f);

            Assert.Equal(50f, Store.PalisadeHP[palisade]);
        }

        // ─── Core healing behavior ────────────────────────────────────────

        [Fact]
        public void Healer_HealsPalisadeInRange_WhenIntervalZero()
        {
            // interval=0 means "fire every frame". Place a healer at (0,0) and a
            // damaged palisade at (3,0). After one Update tick, palisade HP must
            // be restored by HealAuraAmount.
            InitEnv();
            int healer = PlaceTower(0, 0, TowerType.Basic, 100f);
            int target = PlaceTower(3, 0, TowerType.Palisade, 100f);
            Store.PalisadeHP[target] = 50f; // damaged
            // Make the healer a healer (radius 5, amount 10, interval 0 = every frame).
            Store.TowerHealAuraRadius[healer] = 5f;
            Store.TowerHealAuraAmount[healer] = 10f;
            Store.TowerHealAuraInterval[healer] = 0f;
            Store.TowerHealAuraTimer[healer] = 0f;

            var sys = new HealAuraSystem(Store);
            sys.SetTurn();
            sys.Update(0.016f); // ~1 frame

            Assert.Equal(60f, Store.PalisadeHP[target]); // 50 + 10
        }

        [Fact]
        public void Healer_DoesNotSelfHeal()
        {
            // Even if the healer itself is a Palisade (which is a corner case
            // designers shouldn't normally do), it must not heal itself.
            InitEnv();
            int healer = PlaceTower(0, 0, TowerType.Palisade, 100f);
            Store.PalisadeHP[healer] = 50f;
            Store.TowerHealAuraRadius[healer] = 5f;
            Store.TowerHealAuraAmount[healer] = 10f;
            Store.TowerHealAuraInterval[healer] = 0f;
            Store.TowerHealAuraTimer[healer] = 0f;

            var sys = new HealAuraSystem(Store);
            sys.SetTurn();
            sys.Update(0.016f);

            // Self-heal prevented → HP stays at 50.
            Assert.Equal(50f, Store.PalisadeHP[healer]);
        }

        [Fact]
        public void Healer_TargetOutsideRadius_NotHealed()
        {
            InitEnv();
            int healer = PlaceTower(0, 0, TowerType.Basic, 100f);
            int far = PlaceTower(9, 19, TowerType.Palisade, 100f);
            Store.PalisadeHP[far] = 50f;
            Store.TowerHealAuraRadius[healer] = 5f; // radius 5
            Store.TowerHealAuraAmount[healer] = 10f;
            Store.TowerHealAuraInterval[healer] = 0f;
            Store.TowerHealAuraTimer[healer] = 0f;

            var sys = new HealAuraSystem(Store);
            sys.SetTurn();
            sys.Update(0.016f);

            // Target is at (50, 0), healer at (0, 0), distance=50 > radius=5 → no heal.
            Assert.Equal(50f, Store.PalisadeHP[far]);
        }

        [Fact]
        public void Healer_NonPalisadeTarget_NotHealed()
        {
            // Standard towers have no HP pool; even though the SOA 槽位存在，系统必须
            // 跳过非 Palisade 目标，不得碰 PalisadeHP 字段。
            InitEnv();
            int healer = PlaceTower(0, 0, TowerType.Basic, 100f);
            int target = PlaceTower(3, 0, TowerType.Basic, 100f);
            Store.PalisadeMaxHP[target] = 100f;
            Store.PalisadeHP[target] = 37f; // 已知值：非 Palisade 目标也必须保持不变
            Store.TowerHealAuraRadius[healer] = 5f;
            Store.TowerHealAuraAmount[healer] = 10f;
            Store.TowerHealAuraInterval[healer] = 0f;
            Store.TowerHealAuraTimer[healer] = 0f;

            var sys = new HealAuraSystem(Store);
            sys.SetTurn();
            sys.Update(0.016f);

            Assert.Equal(37f, Store.PalisadeHP[target]);
        }

        [Fact]
        public void Healer_Overheal_IsClampedToMaxHP()
        {
            // Palisade at 95 HP, max 100, heal amount 10 → final HP must be 100 (clamped).
            InitEnv();
            int healer = PlaceTower(0, 0, TowerType.Basic, 100f);
            int target = PlaceTower(3, 0, TowerType.Palisade, 100f);
            Store.PalisadeHP[target] = 95f;
            Store.TowerHealAuraRadius[healer] = 5f;
            Store.TowerHealAuraAmount[healer] = 10f;
            Store.TowerHealAuraInterval[healer] = 0f;
            Store.TowerHealAuraTimer[healer] = 0f;

            var sys = new HealAuraSystem(Store);
            sys.SetTurn();
            sys.Update(0.016f);

            // Clamped: 95 + 10 = 105 → 100.
            Assert.Equal(100f, Store.PalisadeHP[target]);
        }

        [Fact]
        public void TwoHealers_StackAdditively()
        {
            // Two healers in range, each contributing 10 HP per tick → total +20.
            InitEnv();
            int healer1 = PlaceTower(0, 0, TowerType.Basic, 100f);
            int healer2 = PlaceTower(2, 0, TowerType.Basic, 100f);
            int target = PlaceTower(1, 0, TowerType.Palisade, 100f);
            Store.PalisadeHP[target] = 50f;
            foreach (var hid in new[] { healer1, healer2 })
            {
                Store.TowerHealAuraRadius[hid] = 5f;
                Store.TowerHealAuraAmount[hid] = 10f;
                Store.TowerHealAuraInterval[hid] = 0f;
                Store.TowerHealAuraTimer[hid] = 0f;
            }

            var sys = new HealAuraSystem(Store);
            sys.SetTurn();
            sys.Update(0.016f);

            // 50 + 10 (h1) + 10 (h2) = 70.
            Assert.Equal(70f, Store.PalisadeHP[target]);
        }

        [Fact]
        public void Healer_IntervalGates_Heal_Fires_OnlyAfter_Interval_Elapses()
        {
            // interval=1.0s, timer starts at 0.5s. After 0.3s of update (timer=0.2),
            // heal must NOT fire yet. After another 0.5s (timer expires), heal fires.
            InitEnv();
            int healer = PlaceTower(0, 0, TowerType.Basic, 100f);
            int target = PlaceTower(3, 0, TowerType.Palisade, 100f);
            Store.PalisadeHP[target] = 50f;
            Store.TowerHealAuraRadius[healer] = 5f;
            Store.TowerHealAuraAmount[healer] = 10f;
            Store.TowerHealAuraInterval[healer] = 1.0f;
            Store.TowerHealAuraTimer[healer] = 0.5f; // half-way through cooldown

            var sys = new HealAuraSystem(Store);
            sys.SetTurn();
            sys.Update(0.3f); // timer: 0.5 - 0.3 = 0.2 → still on cooldown
            Assert.Equal(50f, Store.PalisadeHP[target]);

            sys.Update(0.5f); // timer: 0.2 - 0.5 = -0.3 → fires! reset to 1.0
            Assert.Equal(60f, Store.PalisadeHP[target]);
        }
    }
}
