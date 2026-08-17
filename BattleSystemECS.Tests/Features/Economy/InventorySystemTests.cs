using BattleSystemECS.Tests.Infrastructure;
using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Features.Economy
{
    /// <summary>
    /// Tests for Round 130 Direction 6: Inventory / Item system.
    /// Verifies:
    ///   1. ItemDefs default empty (zero-overhead)
    ///   2. ResetInventory clears all slots to (-1, 0) and resets Used counter
    ///   3. AddItem places in first empty slot
    ///   4. AddItem stacks in existing slot when same item + Count<MaxStack
    ///   5. AddItem returns false when inventory full
    ///   6. AddItem returns false for out-of-range playerId / itemId
    ///   7. UseItem on empty slot returns false (no consumption)
    ///   8. UseItem decrements Count, clears slot when Count==0
    ///   9. UseItem decrements Used counter; PlayerInventoryUsedTotal increments
    ///  10. UseItem on Heal restores HP clamped to MaxHealth (no overheal)
    ///  11. UseItem on Mana restores mana clamped to MaxMana
    ///  12. UseItem on Shield sets shield + duration
    ///  13. UseItem on SpeedBoost sets SlowFactor=1.5 + SlowDuration
    ///  14. UseItem on DamageBoost sets AttackBoost bit + duration
    ///  15. UseItem on AoEBurst damages enemies in radius, queues death on HP<=0
    ///  16. UseItem on AoEBurst skips invulnerable enemies
    ///  17. UseItem on Cleanse clears PlayerStunDuration
    ///  18. UseItem on Summon returns false (TODO future round, slot not consumed)
    ///  19. UseItem on Unknown item returns false
    ///  20. RemoveItem clears slot without applying effect
    ///  21. Stacking respects MaxStack cap
    ///  22. GetInventoryUsed is O(1) cached counter
    ///  23. Out-of-range playerId/slot in accessors returns safe defaults (-1 / 0)
    ///  24. AddPlayer calls ResetInventory (init path)
    /// </summary>
    public class InventorySystemTests : BattleTestBase
    {
        private const int PlayerId = 0;

        private InventorySystem CreateEnv()
        {
            Store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            // Default player to fully-rested state so Heal/Mana tests don't no-op.
            Store.PlayerMaxHealth[0] = 1000f;
            Store.PlayerCurrentHealth[0] = 500f;  // half HP — heals can fire
            Store.PlayerMaxMana[0] = 1000f;
            Store.PlayerMana[0] = 500f;  // half mana — mana pots can fire
            // Inject 3 test items.
            Config.ItemDefs = new ItemDef[]
            {
                new ItemDef { Type = "potion_heal", Name = "Heal Potion", ItemType = InventoryItemType.Heal, Value = 50f, MaxStack = 2 },
                new ItemDef { Type = "potion_mana", Name = "Mana Potion", ItemType = InventoryItemType.Mana, Value = 30f, MaxStack = 3 },
                new ItemDef { Type = "grenade",     Name = "Grenade",     ItemType = InventoryItemType.AoEBurst, Value = 80f, Radius = 3.5f, MaxStack = 1 },
            };
            return new InventorySystem(Store, Config, null);
        }

        // ── 1. ItemDefs default empty ──────────────────────────────────────
        [Fact]
        public void ItemDefs_DefaultEmpty()
        {
            var cfg = Config;
            Assert.NotNull(cfg.ItemDefs);
            Assert.Empty(cfg.ItemDefs);
        }

        // ── 2. ResetInventory ──────────────────────────────────────────────
        [Fact]
        public void ResetInventory_ClearsAllSlots()
        {
            var inv = CreateEnv();
            inv.AddItem(0, 0);
            inv.AddItem(0, 1);
            Assert.Equal(2, Store.GetInventoryUsed(0));

            Store.ResetInventory(0);
            Assert.Equal(0, Store.GetInventoryUsed(0));
            for (int s = 0; s < ComponentStore.MAX_INVENTORY_SLOTS; s++)
            {
                Assert.Equal(-1, Store.GetInventoryItemId(0, s));
                Assert.Equal(0, Store.GetInventoryCount(0, s));
            }
        }

        [Fact]
        public void ResetInventory_OutOfRange_LeavesValidSlotsUntouched()
        {
            var inv = CreateEnv();
            // 先写入合法槽位作为对照。
            Assert.True(inv.AddItem(0, 0));
            Assert.Equal(0, Store.GetInventoryItemId(0, 0));
            Assert.Equal(1, Store.GetInventoryCount(0, 0));
            Assert.Equal(1, Store.GetInventoryUsed(0));

            Store.ResetInventory(-1);
            Store.ResetInventory(99);

            // 越界调用必须保持合法玩家 0 的槽位不变。
            Assert.Equal(0, Store.GetInventoryItemId(0, 0));
            Assert.Equal(1, Store.GetInventoryCount(0, 0));
            Assert.Equal(1, Store.GetInventoryUsed(0));
        }

        // ── 3. AddItem places in first empty slot ──────────────────────────
        [Fact]
        public void AddItem_FirstEmptySlot()
        {
            var inv = CreateEnv();
            Assert.True(inv.AddItem(0, 0));
            Assert.Equal(0, Store.GetInventoryItemId(0, 0));
            Assert.Equal(1, Store.GetInventoryCount(0, 0));
            Assert.Equal(1, Store.GetInventoryUsed(0));
        }

        [Fact]
        public void AddItem_SecondEmptySlot_AfterFirstFilled()
        {
            var inv = CreateEnv();
            inv.AddItem(0, 0);
            inv.AddItem(0, 1);
            Assert.Equal(0, Store.GetInventoryItemId(0, 0));
            Assert.Equal(1, Store.GetInventoryItemId(0, 1));
            Assert.Equal(2, Store.GetInventoryUsed(0));
        }

        // ── 4. AddItem stacks when same item + room ────────────────────────
        [Fact]
        public void AddItem_StacksSameItem_WhenRoomAvailable()
        {
            var inv = CreateEnv();
            inv.AddItem(0, 0);
            inv.AddItem(0, 0);
            // MaxStack=2, so second add should stack to 2 in slot 0.
            Assert.Equal(0, Store.GetInventoryItemId(0, 0));
            Assert.Equal(2, Store.GetInventoryCount(0, 0));
            Assert.Equal(1, Store.GetInventoryUsed(0));
        }

        [Fact]
        public void AddItem_StacksUpToMaxStackThenNewSlot()
        {
            var inv = CreateEnv();
            inv.AddItem(0, 0);  // slot 0, count 1
            inv.AddItem(0, 0);  // slot 0, count 2 (max)
            inv.AddItem(0, 0);  // slot 1, count 1
            Assert.Equal(2, Store.GetInventoryCount(0, 0));
            Assert.Equal(0, Store.GetInventoryItemId(0, 1));
            Assert.Equal(1, Store.GetInventoryCount(0, 1));
            Assert.Equal(2, Store.GetInventoryUsed(0));
        }

        // ── 5. AddItem returns false when full ─────────────────────────────
        [Fact]
        public void AddItem_ReturnsFalse_WhenFull()
        {
            // Use a single-use item to ensure each add consumes a new slot.
            var cfg = Config;
            cfg.ItemDefs = new ItemDef[]
            {
                new ItemDef { Type = "grenade", Name = "Grenade", ItemType = InventoryItemType.AoEBurst, Value = 80f, Radius = 3.5f, MaxStack = 1 },
            };
            Store.AddPlayer(0, 1f, 1f, 1f, 1);
            var inv = new InventorySystem(Store, cfg, null);
            // Fill all 8 slots with single-use grenades.
            for (int i = 0; i < ComponentStore.MAX_INVENTORY_SLOTS; i++)
            {
                Assert.True(inv.AddItem(0, 0));
            }
            Assert.Equal(ComponentStore.MAX_INVENTORY_SLOTS, Store.GetInventoryUsed(0));
            // 9th add must fail (full).
            Assert.False(inv.AddItem(0, 0));
            Assert.Equal(1, inv.TotalDroppedFullInv);
        }

        // ── 6. AddItem out-of-range guards ────────────────────────────────
        [Fact]
        public void AddItem_OutOfRange_ReturnsFalse()
        {
            var inv = CreateEnv();
            Assert.False(inv.AddItem(-1, 0));
            Assert.False(inv.AddItem(99, 0));
            Assert.False(inv.AddItem(0, -1));
            Assert.False(inv.AddItem(0, 999));
        }

        // ── 7. UseItem on empty slot ───────────────────────────────────────
        [Fact]
        public void UseItem_EmptySlot_ReturnsFalse()
        {
            var inv = CreateEnv();
            Assert.False(inv.UseItem(0, 0));
            Assert.False(inv.UseItem(0, 5));
        }

        [Fact]
        public void UseItem_OutOfRange_ReturnsFalse()
        {
            var inv = CreateEnv();
            Assert.False(inv.UseItem(-1, 0));
            Assert.False(inv.UseItem(99, 0));
            Assert.False(inv.UseItem(0, -1));
            Assert.False(inv.UseItem(0, 99));
        }

        // ── 8. UseItem decrements Count, clears slot when 0 ────────────────
        [Fact]
        public void UseItem_DecrementsCount_ClearsWhenZero()
        {
            var inv = CreateEnv();
            inv.AddItem(0, 0);  // MaxStack=2
            inv.AddItem(0, 0);  // stacked to 2
            Assert.True(inv.UseItem(0, 0));
            Assert.Equal(1, Store.GetInventoryCount(0, 0));
            Assert.Equal(0, Store.GetInventoryItemId(0, 0));
            Assert.True(inv.UseItem(0, 0));  // second use clears
            Assert.Equal(-1, Store.GetInventoryItemId(0, 0));
            Assert.Equal(0, Store.GetInventoryCount(0, 0));
            Assert.Equal(0, Store.GetInventoryUsed(0));
        }

        // ── 9. UseItem updates PlayerInventoryUsedTotal ───────────────────
        [Fact]
        public void UseItem_IncrementsUsedTotal()
        {
            var inv = CreateEnv();
            inv.AddItem(0, 0);
            inv.AddItem(0, 0);
            inv.UseItem(0, 0);
            inv.UseItem(0, 0);
            Assert.Equal(2, Store.PlayerInventoryUsedTotal[0]);
        }

        // ── 10. Heal clamps to MaxHealth ──────────────────────────────────
        [Fact]
        public void UseItem_Heal_ClampsToMaxHealth()
        {
            var inv = CreateEnv();
            Store.PlayerMaxHealth[0] = 100f;
            Store.PlayerCurrentHealth[0] = 80f;
            inv.AddItem(0, 0);  // heal potion, value=50
            inv.UseItem(0, 0);
            Assert.Equal(100f, Store.PlayerCurrentHealth[0]);  // 80+50=130, clamped to 100
        }

        [Fact]
        public void UseItem_Heal_FullHP_NoOp()
        {
            var inv = CreateEnv();
            Store.PlayerMaxHealth[0] = 100f;
            Store.PlayerCurrentHealth[0] = 100f;
            inv.AddItem(0, 0);
            Assert.False(inv.UseItem(0, 0));  // already full → no-op
            Assert.Equal(1, Store.GetInventoryCount(0, 0));  // not consumed
        }

        // ── 11. Mana clamps to MaxMana ────────────────────────────────────
        [Fact]
        public void UseItem_Mana_ClampsToMaxMana()
        {
            var inv = CreateEnv();
            Store.PlayerMana[0] = 80f;
            Store.PlayerMaxMana[0] = 100f;
            inv.AddItem(0, 1);  // mana potion, value=30
            inv.UseItem(0, 0);
            Assert.Equal(100f, Store.PlayerMana[0]);
        }

        // ── 12. Shield sets value + duration ──────────────────────────────
        [Fact]
        public void UseItem_Shield_SetsValueAndDuration()
        {
            var cfg = Config;
            cfg.ItemDefs = new ItemDef[]
            {
                new ItemDef { Type = "shield_sigil", Name = "Shield", ItemType = InventoryItemType.Shield, Value = 25f, BuffDuration = 12f, MaxStack = 1 },
            };
            Store.AddPlayer(0, 1f, 1f, 1f, 1);
            var inv = new InventorySystem(Store, cfg, null);
            inv.AddItem(0, 0);
            Store.PlayerShield[0] = 0f;
            Store.PlayerShieldDuration[0] = 0;
            Assert.True(inv.UseItem(0, 0));
            Assert.Equal(25f, Store.PlayerShield[0]);
            Assert.Equal(12, Store.PlayerShieldDuration[0]);
        }

        // ── 13. SpeedBoost sets SlowFactor=1.5 + SlowDuration ─────────────
        [Fact]
        public void UseItem_SpeedBoost_SetsSlowFactorAndDuration()
        {
            var cfg = Config;
            cfg.ItemDefs = new ItemDef[]
            {
                new ItemDef { Type = "tonic", Name = "Tonic", ItemType = InventoryItemType.SpeedBoost, BuffDuration = 8f, MaxStack = 1 },
            };
            Store.AddPlayer(0, 1f, 1f, 1f, 1);
            var inv = new InventorySystem(Store, cfg, null);
            inv.AddItem(0, 0);
            Store.PlayerSlowFactor[0] = 1f;
            Store.PlayerSlowDuration[0] = 0;
            Assert.True(inv.UseItem(0, 0));
            Assert.Equal(1.5f, Store.PlayerSlowFactor[0]);
            Assert.Equal(8, Store.PlayerSlowDuration[0]);
        }

        // ── 14. DamageBoost sets AttackBoost bit + duration ──────────────
        [Fact]
        public void UseItem_DamageBoost_SetsAttackBoostFlag()
        {
            var cfg = Config;
            cfg.ItemDefs = new ItemDef[]
            {
                new ItemDef { Type = "rage", Name = "Rage", ItemType = InventoryItemType.DamageBoost, Value = 0.2f, BuffDuration = 10f, MaxStack = 1 },
            };
            Store.AddPlayer(0, 1f, 1f, 1f, 1);
            var inv = new InventorySystem(Store, cfg, null);
            inv.AddItem(0, 0);
            Store.PlayerBuffFlags[0] = BuffType.None;
            Store.PlayerDamageBoostDuration[0] = 0;
            Assert.True(inv.UseItem(0, 0));
            Assert.True((Store.PlayerBuffFlags[0] & BuffType.AttackBoost) != 0);
            Assert.Equal(10, Store.PlayerDamageBoostDuration[0]);
            // Verify Claude bug scan fix: DamageBoost no longer touches PlayerSlowDuration.
            Assert.Equal(0, Store.PlayerSlowDuration[0]);
            // BUG scan fix: per-item Value magnitude (0.2 = +20%) is now persisted to
            // PlayerDamageBoostMultiplier so the damage system can apply the correct multiplier.
            Assert.Equal(0.2f, Store.PlayerDamageBoostMultiplier[0]);
        }

        // ── 14b. DamageBoost does NOT clobber SpeedBoost timer ──────────────
        [Fact]
        public void UseItem_DamageBoost_DoesNotClobberSpeedBoostTimer()
        {
            var cfg = Config;
            cfg.ItemDefs = new ItemDef[]
            {
                new ItemDef { Type = "tonic", Name = "Tonic", ItemType = InventoryItemType.SpeedBoost, BuffDuration = 8f, MaxStack = 1 },
                new ItemDef { Type = "rage",  Name = "Rage",  ItemType = InventoryItemType.DamageBoost, Value = 0.2f, BuffDuration = 10f, MaxStack = 1 },
            };
            Store.AddPlayer(0, 1f, 1f, 1f, 1);
            var inv = new InventorySystem(Store, cfg, null);
            inv.AddItem(0, 0); inv.UseItem(0, 0);  // SpeedBoost sets SlowDuration=8
            Assert.Equal(8, Store.PlayerSlowDuration[0]);
            inv.AddItem(0, 1); inv.UseItem(0, 0);  // DamageBoost (lands in slot 0 since slot 0 was cleared)
            Assert.Equal(8, Store.PlayerSlowDuration[0]);
            Assert.Equal(10, Store.PlayerDamageBoostDuration[0]);
        }

        // ── 15. AoEBurst damages enemies in radius, queues death on HP<=0 ─
        [Fact]
        public void UseItem_AoEBurst_DamagesEnemiesInRadius()
        {
            var inv = CreateEnv();
            Store.PositionX[0] = 0f; Store.PositionY[0] = 0f;
            int e1 = Store.AddEnemy(1f, 1f, 1f, 100f, 100f, 1f, 1, 1, "goblin");
            int e2 = Store.AddEnemy(50f, 50f, 1f, 100f, 100f, 1f, 1, 1, "ogre");
            inv.AddItem(0, 2);  // grenade: 80 dmg, radius 3.5
            Assert.True(inv.UseItem(0, 0));
            // e1 in radius (sqrt(2) ≈ 1.4), e2 outside.
            Assert.Equal(100f - 80f, Store.EnemyHealth[e1], 0.001f);
            Assert.Equal(100f, Store.EnemyHealth[e2]);
        }

        [Fact]
        public void UseItem_AoEBurst_HitsAndQueuesDeathOnKill()
        {
            var inv = CreateEnv();
            Store.PositionX[0] = 0f; Store.PositionY[0] = 0f;
            int e1 = Store.AddEnemy(1f, 1f, 1f, 50f, 50f, 1f, 1, 1, "goblin");
            Assert.Contains(e1, Store.GetCachedActiveEnemyIds());

            inv.AddItem(0, 2);  // grenade: 80 dmg
            Assert.True(inv.UseItem(0, 0));

            // 精确血量：50 - 80 = -30，并已进入死亡队列（UseItem 直接 QueueEnemyDeath）。
            Assert.Equal(50f - 80f, Store.EnemyHealth[e1], 0.001f);
            Store.ResolveEnemiesKilledThisFrame();
            // 帧末结算后死亡实体必须从活跃列表移除。
            Assert.DoesNotContain(e1, Store.GetCachedActiveEnemyIds());
            Assert.False(Store.EnemyActive[e1]);
        }

        [Fact]
        public void UseItem_AoEBurst_SkipsInvulnerableEnemies()
        {
            var inv = CreateEnv();
            Store.PositionX[0] = 0f; Store.PositionY[0] = 0f;
            int e1 = Store.AddEnemy(1f, 1f, 1f, 100f, 100f, 1f, 1, 1, "goblin");
            Store.EnemyIsInvulnerable[e1] = true;
            inv.AddItem(0, 2);
            Assert.True(inv.UseItem(0, 0));
            Assert.Equal(100f, Store.EnemyHealth[e1]);
        }

        [Fact]
        public void UseItem_AoEBurst_ZeroRadius_NoOp()
        {
            var cfg = Config;
            cfg.ItemDefs = new ItemDef[]
            {
                new ItemDef { Type = "broken", Name = "Broken", ItemType = InventoryItemType.AoEBurst, Value = 100f, Radius = 0f, MaxStack = 1 },
            };
            Store.AddPlayer(0, 1f, 1f, 1f, 1);
            var inv = new InventorySystem(Store, cfg, null);
            inv.AddItem(0, 0);
            Assert.False(inv.UseItem(0, 0));
        }

        // ── 17. Cleanse clears PlayerStunDuration ─────────────────────────
        [Fact]
        public void UseItem_Cleanse_ClearsStunDuration()
        {
            var cfg = Config;
            cfg.ItemDefs = new ItemDef[]
            {
                new ItemDef { Type = "charm", Name = "Charm", ItemType = InventoryItemType.Cleanse, MaxStack = 1 },
            };
            Store.AddPlayer(0, 1f, 1f, 1f, 1);
            var inv = new InventorySystem(Store, cfg, null);
            inv.AddItem(0, 0);
            Store.PlayerStunDuration[0] = 5;
            Assert.True(inv.UseItem(0, 0));
            Assert.Equal(0, Store.PlayerStunDuration[0]);
        }

        // ── 18. Summon returns false, slot not consumed ───────────────────
        [Fact]
        public void UseItem_Summon_ReturnsFalse_NotConsumed()
        {
            var cfg = Config;
            cfg.ItemDefs = new ItemDef[]
            {
                new ItemDef { Type = "scroll", Name = "Scroll", ItemType = InventoryItemType.Summon, Value = 3, MaxStack = 1 },
            };
            Store.AddPlayer(0, 1f, 1f, 1f, 1);
            var inv = new InventorySystem(Store, cfg, null);
            inv.AddItem(0, 0);
            Assert.False(inv.UseItem(0, 0));
            // Slot not consumed (TODO future round).
            Assert.Equal(0, Store.GetInventoryItemId(0, 0));
            Assert.Equal(1, Store.GetInventoryCount(0, 0));
        }

        // ── 19. Unknown item returns false at UseItem (after AddItem rejection) ──
        [Fact]
        public void UseItem_UnknownItemType_ReturnsFalse_AndAddItemRejectsUnknown()
        {
            var cfg = Config;
            cfg.ItemDefs = new ItemDef[]
            {
                new ItemDef { Type = "broken_def", Name = "Broken", ItemType = InventoryItemType.Unknown, MaxStack = 1 },
            };
            Store.AddPlayer(0, 1f, 1f, 1f, 1);
            var inv = new InventorySystem(Store, cfg, null);
            // Claude bug scan fix: AddItem now rejects Unknown items outright (no permanent slot).
            Assert.False(inv.AddItem(0, 0));
            Assert.Equal(0, Store.GetInventoryUsed(0));
        }

        // ── 20. RemoveItem clears slot without effect ─────────────────────
        [Fact]
        public void RemoveItem_ClearsSlot_NoEffectApplied()
        {
            var inv = CreateEnv();
            inv.AddItem(0, 0);
            Assert.Equal(0, Store.GetInventoryItemId(0, 0));
            Assert.True(inv.RemoveItem(0, 0));
            Assert.Equal(-1, Store.GetInventoryItemId(0, 0));
            Assert.Equal(0, Store.GetInventoryUsed(0));
        }

        [Fact]
        public void RemoveItem_EmptySlot_ReturnsFalse()
        {
            var inv = CreateEnv();
            Assert.False(inv.RemoveItem(0, 0));
        }

        // ── 22. GetInventoryUsed is O(1) cached counter ──────────────────
        [Fact]
        public void GetInventoryUsed_TracksFillLevel()
        {
            var inv = CreateEnv();
            Assert.Equal(0, Store.GetInventoryUsed(0));
            inv.AddItem(0, 0);
            Assert.Equal(1, Store.GetInventoryUsed(0));
            inv.AddItem(0, 1);
            Assert.Equal(2, Store.GetInventoryUsed(0));
            inv.UseItem(0, 0);
            Assert.Equal(1, Store.GetInventoryUsed(0));
        }

        // ── 23. Out-of-range accessor safety ──────────────────────────────
        [Fact]
        public void GetInventory_OutOfRange_ReturnsDefaults()
        {
            CreateEnv();
            Assert.Equal(-1, Store.GetInventoryItemId(-1, 0));
            Assert.Equal(-1, Store.GetInventoryItemId(0, -1));
            Assert.Equal(-1, Store.GetInventoryItemId(99, 0));
            Assert.Equal(-1, Store.GetInventoryItemId(0, 99));
            Assert.Equal(0, Store.GetInventoryCount(-1, 0));
            Assert.Equal(0, Store.GetInventoryCount(0, 99));
            Assert.Equal(0, Store.GetInventoryUsed(-1));
            Assert.Equal(0, Store.GetInventoryUsed(99));
        }

        [Fact]
        public void InventoryIndex_ReturnsSentinelOnOutOfRange()
        {
            // BUG scan fix: out-of-range inputs return -1 sentinel (mirrors instance accessors)
            // instead of silently clamping to player 0 (which would corrupt unrelated data).
            Assert.Equal(-1, ComponentStore.InventoryIndex(-5, 0));
            Assert.Equal(-1, ComponentStore.InventoryIndex(0, -5));
            // Out-of-range playerId: must exceed MAX_PLAYERS. Use a large value (10 * MAX_INVENTORY_SLOTS).
            Assert.Equal(-1, ComponentStore.InventoryIndex(99, 0));
            // Out-of-range slot: MAX_INVENTORY_SLOTS (8) is exactly the upper bound (excluded).
            Assert.Equal(-1, ComponentStore.InventoryIndex(0, ComponentStore.MAX_INVENTORY_SLOTS));
            Assert.Equal(-1, ComponentStore.InventoryIndex(-5, 99));
            Assert.Equal(-1, ComponentStore.InventoryIndex(10, -5));
            // In-range: returns expected flat index.
            Assert.Equal(0, ComponentStore.InventoryIndex(0, 0));
            Assert.Equal(ComponentStore.MAX_INVENTORY_SLOTS - 1, ComponentStore.InventoryIndex(0, ComponentStore.MAX_INVENTORY_SLOTS - 1));
        }

        // ── 24. AddPlayer calls ResetInventory ───────────────────────────
        [Fact]
        public void AddPlayer_ResetsInventory()
        {
            Store.AddPlayer(0, 1f, 1f, 1f, 1);
            Assert.Equal(0, Store.GetInventoryUsed(0));
            for (int s = 0; s < ComponentStore.MAX_INVENTORY_SLOTS; s++)
            {
                Assert.Equal(-1, Store.GetInventoryItemId(0, s));
                Assert.Equal(0, Store.GetInventoryCount(0, s));
            }
        }

        // ── 25. GetItemName returns name or empty ─────────────────────────
        [Fact]
        public void GetItemName_ReturnsNameOrEmpty()
        {
            var inv = CreateEnv();
            Assert.Equal("Heal Potion", inv.GetItemName(0));
            Assert.Equal("Mana Potion", inv.GetItemName(1));
            Assert.Equal("Grenade", inv.GetItemName(2));
            Assert.Equal("", inv.GetItemName(-1));
            Assert.Equal("", inv.GetItemName(999));
        }

        // ── 26. Stacking respects MaxStack cap (defense in depth) ───────
        [Fact]
        public void AddItem_StackingRespectsMaxStackCap()
        {
            var inv = CreateEnv();
            // Item 0 MaxStack=2. After 2 stacks, third add should go to slot 1.
            inv.AddItem(0, 0);
            inv.AddItem(0, 0);
            inv.AddItem(0, 0);
            Assert.Equal(2, Store.GetInventoryCount(0, 0));  // capped
            Assert.Equal(0, Store.GetInventoryItemId(0, 1));  // moved to next slot
            Assert.Equal(1, Store.GetInventoryCount(0, 1));
            Assert.Equal(2, Store.GetInventoryUsed(0));
        }

        // ── 27. Telemetry counters increment ──────────────────────────────
        [Fact]
        public void TelemetryCounters_Increment()
        {
            var inv = CreateEnv();
            inv.AddItem(0, 0);
            inv.AddItem(0, 0);
            inv.AddItem(0, 0);
            Assert.Equal(3, inv.TotalAddCalls);
            inv.UseItem(0, 0);
            Assert.Equal(1, inv.TotalUseCalls);
        }
    }
}