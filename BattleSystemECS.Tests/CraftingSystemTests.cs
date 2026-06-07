using System;
using System.Reflection;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
{
    /// <summary>
    /// Tests for Round 199 Direction 6: Crafting System.
    /// Verifies:
    ///   1. Default state: empty recipes array → all calls return BadRecipe
    ///   2. Recipe load: a minimal in-memory config exposes recipes correctly
    ///   3. Successful craft: inputs consumed, outputs delivered
    ///   4. Missing inputs: pre-flight rejects, no mutation
    ///   5. Failed craft with full refund: inputs returned to inventory
    ///   6. Failed craft with zero refund: inputs are lost
    ///   7. Rare bonus: bonus outputs delivered on top of base outputs
    ///   8. Rare bonus default: when RareBonusOutputs empty, duplicates Outputs
    ///   9. Bad recipe id: returns BadRecipe cleanly
    ///  10. Bad player id: returns BadRecipe cleanly
    ///  11. Full inventory on success: inputs consumed, output dropped, returns FullInventory
    ///  12. Multi-input recipe: all inputs must be present
    ///  13. Deterministic with fixed seed: same seed → same success/fail sequence
    ///  14. Partial stack consumption: 5 potions in 2 slots → consume 2 leaves 3 in slots
    ///  15. TryCraft via InventorySystem: forwards correctly
    ///  16. Unbound InventorySystem.TryCraft: returns BadRecipe cleanly (no NRE)
    ///  17. RefundRate=0 → no refund attempt
    ///  18. RefundRate=1.0 → all inputs returned (effectively no-op on failure)
    /// </summary>
    public class CraftingSystemTests
    {
        private const int PlayerId = 0;
        private const int MaxPlayers = 10; // mirrors ComponentStore.MAX_PLAYERS (internal)
        private const int HealingPotionId = 0;   // matches items.json index 0
        private const int ManaPotionId = 1;      // matches items.json index 1
        private const int ShieldSigilId = 2;     // matches items.json index 2
        private const int SpeedTonicId = 3;      // matches items.json index 3
        private const int RageDraughtId = 4;     // matches items.json index 4
        private const int GrenadeId = 5;         // matches items.json index 5

        // ── Test helpers ────────────────────────────────────────────────

        // Build a (system, store, inventory, config) tuple. Optionally inject a
        // custom recipe set and RNG seed so individual tests can exercise
        // specific paths deterministically.
        private static (CraftingSystem system, ComponentStore store, InventorySystem inv, GameConfig cfg)
            MakeSystem(CraftingRecipeDef[] recipes = null, int seed = 12345, int maxStackOverride = 99)
        {
            var store = new ComponentStore();
            store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            // Items 0..6 from items.json — override MaxStack so tests can pre-fill
            // beyond the production 3/2 limits (crafting with 5 potions is way
            // easier when MaxStack is 99).
            var cfg = new GameConfig
            {
                ItemDefs = new ItemDef[]
                {
                    new ItemDef { Type = "healing_potion", Name = "Healing Potion", ItemType = InventoryItemType.Heal, MaxStack = maxStackOverride },
                    new ItemDef { Type = "mana_potion", Name = "Mana Potion", ItemType = InventoryItemType.Mana, MaxStack = maxStackOverride },
                    new ItemDef { Type = "shield_sigil", Name = "Shield Sigil", ItemType = InventoryItemType.Shield, MaxStack = maxStackOverride },
                    new ItemDef { Type = "speed_tonic", Name = "Speed Tonic", ItemType = InventoryItemType.SpeedBoost, MaxStack = maxStackOverride },
                    new ItemDef { Type = "rage_draught", Name = "Rage Draught", ItemType = InventoryItemType.DamageBoost, MaxStack = maxStackOverride },
                    new ItemDef { Type = "grenade", Name = "Grenade", ItemType = InventoryItemType.AoEBurst, MaxStack = maxStackOverride },
                    new ItemDef { Type = "cleanse_charm", Name = "Cleanse Charm", ItemType = InventoryItemType.Cleanse, MaxStack = maxStackOverride },
                },
                CraftingRecipes = recipes ?? Array.Empty<CraftingRecipeDef>(),
            };
            var inv = new InventorySystem(store, cfg, null);
            var system = new CraftingSystem(store, cfg, inv, null, seed: seed);
            inv.BindCraftingSystem(system);
            return (system, store, inv, cfg);
        }

        // Build a "guaranteed success, no bonus" recipe. Inputs/outputs are caller-supplied.
        private static CraftingRecipeDef MakeSuccessRecipe(
            string type,
            CraftingItemStack[] inputs,
            CraftingItemStack[] outputs,
            float successRate = 1f)
        {
            return new CraftingRecipeDef
            {
                Type = type,
                Name = type,
                Inputs = inputs,
                Outputs = outputs,
                SuccessRate = successRate,
                RefundRate = 0.5f,
                RareBonusRate = 0f,
            };
        }

        // Give the player N of a particular item by adding it N times (respects
        // MaxStack via AddItem stack-merge / empty-slot).
        private static void GiveItems(InventorySystem inv, int playerId, int itemId, int count)
        {
            for (int i = 0; i < count; i++)
            {
                Assert.True(inv.AddItem(playerId, itemId), $"setup: AddItem failed at iter {i}");
            }
        }

        // Count total of an item id in a player's inventory (sums across slots).
        private static int CountItem(ComponentStore store, int playerId, int itemId)
        {
            int sum = 0;
            for (int s = 0; s < ComponentStore.MAX_INVENTORY_SLOTS; s++)
            {
                int idx = playerId * ComponentStore.MAX_INVENTORY_SLOTS + s;
                if (store.PlayerInventoryItemId[idx] == itemId)
                {
                    sum += store.PlayerInventoryCount[idx];
                }
            }
            return sum;
        }

        // ── 1. Default state: empty recipes → BadRecipe ─────────────────
        [Fact]
        public void EmptyRecipes_AllCallsReturnBadRecipe()
        {
            var (system, _, _, _) = MakeSystem(recipes: Array.Empty<CraftingRecipeDef>());
            var result = system.TryCraft(PlayerId, 0);
            Assert.Equal(CraftingSystem.CraftingResult.BadRecipe, result);
            Assert.Equal(0, system.TotalAttempts); // not even counted
        }

        // ── 2. Recipe load: minimal in-memory config exposes recipes ───
        [Fact]
        public void RecipeLoad_ExposesCountAndDef()
        {
            var recipe = MakeSuccessRecipe("combine_heal",
                new[] { new CraftingItemStack { ItemId = HealingPotionId, Count = 2 } },
                new[] { new CraftingItemStack { ItemId = HealingPotionId, Count = 1 } });
            var (system, _, _, cfg) = MakeSystem(recipes: new[] { recipe });
            Assert.Equal(1, system.GetRecipeCount());
            Assert.Equal("combine_heal", system.GetRecipe(0).Type);
        }

        // ── 3. Successful craft: inputs consumed, outputs delivered ────
        [Fact]
        public void Success_InputsConsumedOutputsDelivered()
        {
            var recipe = MakeSuccessRecipe("combine_heal",
                new[] { new CraftingItemStack { ItemId = HealingPotionId, Count = 2 } },
                new[] { new CraftingItemStack { ItemId = HealingPotionId, Count = 1 } });
            var (system, store, inv, _) = MakeSystem(recipes: new[] { recipe });
            GiveItems(inv, PlayerId, HealingPotionId, 3);
            Assert.Equal(3, CountItem(store, PlayerId, HealingPotionId));

            var result = system.TryCraft(PlayerId, 0);
            Assert.Equal(CraftingSystem.CraftingResult.Success, result);
            // 3 - 2 inputs + 1 output = 2 left
            Assert.Equal(2, CountItem(store, PlayerId, HealingPotionId));
            Assert.Equal(1, system.TotalSuccesses);
            Assert.Equal(0, system.TotalFailures);
        }

        // ── 4. Missing inputs: pre-flight rejects, no mutation ──────────
        [Fact]
        public void MissingInputs_NoMutation()
        {
            var recipe = MakeSuccessRecipe("combine_heal",
                new[] { new CraftingItemStack { ItemId = HealingPotionId, Count = 5 } },
                new[] { new CraftingItemStack { ItemId = HealingPotionId, Count = 1 } });
            var (system, store, inv, _) = MakeSystem(recipes: new[] { recipe });
            GiveItems(inv, PlayerId, HealingPotionId, 3);

            var result = system.TryCraft(PlayerId, 0);
            Assert.Equal(CraftingSystem.CraftingResult.MissingInputs, result);
            Assert.Equal(3, CountItem(store, PlayerId, HealingPotionId)); // untouched
            Assert.Equal(1, system.TotalRejectedMissingInputs);
            Assert.Equal(0, system.TotalSuccesses);
        }

        // ── 5. Failed craft with full refund: inputs returned ─────────
        [Fact]
        public void Failure_FullRefund_AllInputsReturned()
        {
            var recipe = new CraftingRecipeDef
            {
                Type = "guaranteed_fail",
                Name = "guaranteed_fail",
                Inputs = new[] { new CraftingItemStack { ItemId = HealingPotionId, Count = 2 } },
                Outputs = new[] { new CraftingItemStack { ItemId = ManaPotionId, Count = 1 } },
                SuccessRate = 0f, // always fail
                RefundRate = 1f,  // full refund
                RareBonusRate = 0f,
            };
            var (system, store, inv, _) = MakeSystem(recipes: new[] { recipe });
            GiveItems(inv, PlayerId, HealingPotionId, 2);
            int manaBefore = CountItem(store, PlayerId, ManaPotionId);

            var result = system.TryCraft(PlayerId, 0);
            Assert.Equal(CraftingSystem.CraftingResult.Failure, result);
            Assert.Equal(2, CountItem(store, PlayerId, HealingPotionId)); // refunded
            Assert.Equal(manaBefore, CountItem(store, PlayerId, ManaPotionId)); // no output
            Assert.Equal(1, system.TotalFailures);
        }

        // ── 6. Failed craft with zero refund: inputs lost ──────────────
        [Fact]
        public void Failure_ZeroRefund_InputsLost()
        {
            var recipe = new CraftingRecipeDef
            {
                Type = "guaranteed_fail",
                Name = "guaranteed_fail",
                Inputs = new[] { new CraftingItemStack { ItemId = HealingPotionId, Count = 2 } },
                Outputs = new[] { new CraftingItemStack { ItemId = ManaPotionId, Count = 1 } },
                SuccessRate = 0f,
                RefundRate = 0f,  // no refund
                RareBonusRate = 0f,
            };
            var (system, store, inv, _) = MakeSystem(recipes: new[] { recipe });
            GiveItems(inv, PlayerId, HealingPotionId, 2);

            var result = system.TryCraft(PlayerId, 0);
            Assert.Equal(CraftingSystem.CraftingResult.Failure, result);
            Assert.Equal(0, CountItem(store, PlayerId, HealingPotionId)); // lost
        }

        // ── 7. Rare bonus: bonus outputs delivered on top of base ─────
        [Fact]
        public void RareBonus_BonusDeliveredOnTop()
        {
            var recipe = new CraftingRecipeDef
            {
                Type = "combine_with_bonus",
                Name = "combine_with_bonus",
                Inputs = new[] { new CraftingItemStack { ItemId = HealingPotionId, Count = 2 } },
                Outputs = new[] { new CraftingItemStack { ItemId = HealingPotionId, Count = 1 } },
                RareBonusOutputs = new[] { new CraftingItemStack { ItemId = ManaPotionId, Count = 1 } },
                SuccessRate = 1f,  // always succeed
                RefundRate = 0.5f,
                RareBonusRate = 1f, // always bonus
            };
            var (system, store, inv, _) = MakeSystem(recipes: new[] { recipe });
            GiveItems(inv, PlayerId, HealingPotionId, 2);

            var result = system.TryCraft(PlayerId, 0);
            Assert.Equal(CraftingSystem.CraftingResult.SuccessRareBonus, result);
            // 2 heal - 2 input + 1 output = 1 heal
            Assert.Equal(1, CountItem(store, PlayerId, HealingPotionId));
            // +1 mana bonus
            Assert.Equal(1, CountItem(store, PlayerId, ManaPotionId));
            Assert.Equal(1, system.TotalRareBonuses);
        }

        // ── 8. Rare bonus default: duplicates base Outputs ────────────
        [Fact]
        public void RareBonus_NoBonusList_DuplicatesOutputs()
        {
            var recipe = new CraftingRecipeDef
            {
                Type = "double_yield",
                Name = "double_yield",
                Inputs = new[] { new CraftingItemStack { ItemId = HealingPotionId, Count = 2 } },
                Outputs = new[] { new CraftingItemStack { ItemId = HealingPotionId, Count = 1 } },
                RareBonusOutputs = Array.Empty<CraftingItemStack>(), // explicitly empty
                SuccessRate = 1f,
                RefundRate = 0.5f,
                RareBonusRate = 1f, // always bonus
            };
            var (system, store, inv, _) = MakeSystem(recipes: new[] { recipe });
            GiveItems(inv, PlayerId, HealingPotionId, 2);

            var result = system.TryCraft(PlayerId, 0);
            Assert.Equal(CraftingSystem.CraftingResult.SuccessRareBonus, result);
            // 2 - 2 + 1 (base) + 1 (bonus duplicate) = 2
            Assert.Equal(2, CountItem(store, PlayerId, HealingPotionId));
        }

        // ── 9. Bad recipe id: returns BadRecipe cleanly ───────────────
        [Fact]
        public void BadRecipeId_ReturnsBadRecipe()
        {
            var (system, _, _, _) = MakeSystem(recipes: new[] {
                MakeSuccessRecipe("a", new[] { new CraftingItemStack { ItemId = HealingPotionId, Count = 1 } },
                                       new[] { new CraftingItemStack { ItemId = HealingPotionId, Count = 1 } }) });
            Assert.Equal(CraftingSystem.CraftingResult.BadRecipe, system.TryCraft(PlayerId, 999));
            Assert.Equal(CraftingSystem.CraftingResult.BadRecipe, system.TryCraft(PlayerId, -1));
            // Two bad id calls → two rejected-bad-recipe entries
            Assert.Equal(2, system.TotalRejectedBadRecipe);
        }

        // ── 10. Bad player id: returns BadRecipe cleanly ──────────────
        [Fact]
        public void BadPlayerId_ReturnsBadRecipe()
        {
            var (system, _, _, _) = MakeSystem(recipes: new[] {
                MakeSuccessRecipe("a", new[] { new CraftingItemStack { ItemId = HealingPotionId, Count = 1 } },
                                       new[] { new CraftingItemStack { ItemId = HealingPotionId, Count = 1 } }) });
            Assert.Equal(CraftingSystem.CraftingResult.BadRecipe, system.TryCraft(-1, 0));
            Assert.Equal(CraftingSystem.CraftingResult.BadRecipe, system.TryCraft(MaxPlayers, 0));
        }

        // ── 11. Full inventory on success: inputs consumed, output dropped ─
        [Fact]
        public void FullInventory_InputsConsumedOutputDropped()
        {
            var recipe = MakeSuccessRecipe("combine",
                new[] { new CraftingItemStack { ItemId = HealingPotionId, Count = 1 } },
                new[] { new CraftingItemStack { ItemId = ManaPotionId, Count = 99 } }); // huge output
            // Override MaxStack to 1 on the output so the test's MaxStack=99 doesn't
            // pre-fill the inventory with 99s — we need the inventory FULL of the
            // output item id before the craft runs.
            var store = new ComponentStore();
            store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            var cfg = new GameConfig
            {
                ItemDefs = new ItemDef[]
                {
                    new ItemDef { Type = "heal", Name = "Heal", ItemType = InventoryItemType.Heal, MaxStack = 99 },
                    new ItemDef { Type = "mana", Name = "Mana", ItemType = InventoryItemType.Mana, MaxStack = 1 }, // <-- stack cap 1 so 8 slots = 8 mana
                },
                CraftingRecipes = new[] { recipe },
            };
            var inv = new InventorySystem(store, cfg, null);
            var system = new CraftingSystem(store, cfg, inv, null, seed: 1);
            inv.BindCraftingSystem(system);

            // Add 1 healing potion FIRST so the input slot is reserved before
            // we fill the rest with mana. (Adding heal after a full inventory
            // would fail at AddItem and the test setup would silently miss the
            // input — we want the inventory full of mana, with the heal already
            // present and the next craft being rejected on output delivery.)
            inv.AddItem(PlayerId, HealingPotionId);
            // Pre-fill remaining 7 inventory slots with mana (max stack 1)
            for (int i = 0; i < 7; i++) inv.AddItem(PlayerId, ManaPotionId);

            var result = system.TryCraft(PlayerId, 0);
            Assert.Equal(CraftingSystem.CraftingResult.FullInventory, result);
            // Inputs were consumed (1 healing potion gone)
            Assert.Equal(0, CountItem(store, PlayerId, HealingPotionId));
            // Mana: was 7 (full minus the heal slot) + 1 from output (just barely
            // fit into the freed slot) = 8. The remaining 98 mana output entries
            // were dropped due to inventory full.
            Assert.Equal(8, CountItem(store, PlayerId, ManaPotionId));
        }

        // ── 12. Multi-input recipe: all inputs must be present ─────────
        [Fact]
        public void MultiInput_AllRequired()
        {
            var recipe = MakeSuccessRecipe("elixir",
                new[] {
                    new CraftingItemStack { ItemId = SpeedTonicId, Count = 1 },
                    new CraftingItemStack { ItemId = ManaPotionId, Count = 1 },
                },
                new[] { new CraftingItemStack { ItemId = HealingPotionId, Count = 2 } });
            var (system, store, inv, _) = MakeSystem(recipes: new[] { recipe });

            // Have tonic but no mana → missing inputs
            GiveItems(inv, PlayerId, SpeedTonicId, 1);
            Assert.Equal(CraftingSystem.CraftingResult.MissingInputs, system.TryCraft(PlayerId, 0));
            Assert.Equal(1, CountItem(store, PlayerId, SpeedTonicId)); // untouched
        }

        // ── 13. Deterministic with fixed seed ──────────────────────────
        [Fact]
        public void DeterministicSeed_SameResults()
        {
            var recipe = new CraftingRecipeDef
            {
                Type = "fifty_fifty",
                Name = "fifty_fifty",
                Inputs = new[] { new CraftingItemStack { ItemId = HealingPotionId, Count = 1 } },
                Outputs = new[] { new CraftingItemStack { ItemId = ManaPotionId, Count = 1 } },
                SuccessRate = 0.5f,
                RefundRate = 0.5f,
                RareBonusRate = 0f,
            };
            int seed = 4242;
            int successesA = 0, successesB = 0;
            int attemptsA = 0, attemptsB = 0;
            for (int i = 0; i < 100; i++)
            {
                var (sysA, _, invA, _) = MakeSystem(recipes: new[] { recipe }, seed: seed);
                invA.AddItem(PlayerId, HealingPotionId);
                if (sysA.TryCraft(PlayerId, 0) == CraftingSystem.CraftingResult.Success) successesA++;
                attemptsA++;
            }
            for (int i = 0; i < 100; i++)
            {
                var (sysB, _, invB, _) = MakeSystem(recipes: new[] { recipe }, seed: seed);
                invB.AddItem(PlayerId, HealingPotionId);
                if (sysB.TryCraft(PlayerId, 0) == CraftingSystem.CraftingResult.Success) successesB++;
                attemptsB++;
            }
            // Determinism: same seed → identical success count
            Assert.Equal(successesA, successesB);
            Assert.Equal(attemptsA, attemptsB);
            // Sanity: 100 attempts is the lower bound (never less than 0 successes)
            Assert.True(successesA >= 0 && successesA <= 100);
        }

        // ── 14. Partial stack consumption ─────────────────────────────
        [Fact]
        public void PartialStack_ConsumeLeavesRemainder()
        {
            var recipe = MakeSuccessRecipe("combine_heal",
                new[] { new CraftingItemStack { ItemId = HealingPotionId, Count = 2 } },
                new[] { new CraftingItemStack { ItemId = HealingPotionId, Count = 1 } });
            var (system, store, inv, _) = MakeSystem(recipes: new[] { recipe });
            // AddItem with MaxStack=99 puts all 5 in slot 0
            GiveItems(inv, PlayerId, HealingPotionId, 5);

            system.TryCraft(PlayerId, 0);
            // 5 - 2 input + 1 output = 4
            Assert.Equal(4, CountItem(store, PlayerId, HealingPotionId));
            // Verify it's all in slot 0 (no fragmentation)
            Assert.Equal(4, store.PlayerInventoryCount[0]);
        }

        // ── 15. TryCraft via InventorySystem: forwards correctly ───────
        [Fact]
        public void TryCraft_ViaInventorySystem_Forwards()
        {
            var recipe = MakeSuccessRecipe("combine",
                new[] { new CraftingItemStack { ItemId = HealingPotionId, Count = 1 } },
                new[] { new CraftingItemStack { ItemId = ManaPotionId, Count = 1 } });
            var (system, _, inv, _) = MakeSystem(recipes: new[] { recipe });
            GiveItems(inv, PlayerId, HealingPotionId, 1);

            var result = inv.TryCraft(PlayerId, 0);
            Assert.Equal(CraftingSystem.CraftingResult.Success, result);
        }

        // ── 16. Unbound InventorySystem.TryCraft: returns BadRecipe ────
        [Fact]
        public void TryCraft_UnboundInventory_ReturnsBadRecipe()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            var cfg = new GameConfig();
            var inv = new InventorySystem(store, cfg, null);
            // No BindCraftingSystem call → craftingSystem is null
            var result = inv.TryCraft(PlayerId, 0);
            Assert.Equal(CraftingSystem.CraftingResult.BadRecipe, result);
        }

        // ── 17. RefundRate=0 → no refund attempt ───────────────────────
        [Fact]
        public void RefundRateZero_NoAddItemCalls()
        {
            var recipe = new CraftingRecipeDef
            {
                Type = "fail_no_refund",
                Name = "fail_no_refund",
                Inputs = new[] { new CraftingItemStack { ItemId = HealingPotionId, Count = 3 } },
                Outputs = new[] { new CraftingItemStack { ItemId = ManaPotionId, Count = 1 } },
                SuccessRate = 0f,
                RefundRate = 0f,
                RareBonusRate = 0f,
            };
            var (system, store, inv, _) = MakeSystem(recipes: new[] { recipe });
            GiveItems(inv, PlayerId, HealingPotionId, 3);
            int addsBefore = inv.TotalAddCalls;
            var result = system.TryCraft(PlayerId, 0);
            Assert.Equal(CraftingSystem.CraftingResult.Failure, result);
            // No AddItem calls during refund (RefundRate=0 means refundCount=0)
            Assert.Equal(addsBefore, inv.TotalAddCalls);
            Assert.Equal(0, CountItem(store, PlayerId, HealingPotionId));
        }

        // ── 18. RefundRate=1.0 → all inputs returned ──────────────────
        [Fact]
        public void RefundRateOne_AllInputsReturned()
        {
            var recipe = new CraftingRecipeDef
            {
                Type = "fail_full_refund",
                Name = "fail_full_refund",
                Inputs = new[] { new CraftingItemStack { ItemId = HealingPotionId, Count = 3 } },
                Outputs = new[] { new CraftingItemStack { ItemId = ManaPotionId, Count = 1 } },
                SuccessRate = 0f,
                RefundRate = 1f,  // all back
                RareBonusRate = 0f,
            };
            var (system, store, inv, _) = MakeSystem(recipes: new[] { recipe });
            GiveItems(inv, PlayerId, HealingPotionId, 3);
            var result = system.TryCraft(PlayerId, 0);
            Assert.Equal(CraftingSystem.CraftingResult.Failure, result);
            // 3 - 3 (consumed) + 3 (refund) = 3
            Assert.Equal(3, CountItem(store, PlayerId, HealingPotionId));
        }
        // ── 19. Duplicate ItemId in inputs: aggregated check ────────────
        // Claude bug scan regression: a recipe listing the same ItemId in two
        // input entries (e.g. grenade_crate = {ItemId:5,Count:1} ×2) used to
        // pass HasAllInputs with only 1 grenade in stock, then ConsumeInputs
        // would log a false "concurrent mutation" warning and the player would
        // lose the only grenade. The fix aggregates required counts by ItemId
        // before pre-flight, so this case correctly returns MissingInputs.
        [Fact]
        public void DuplicateItemId_RequiresSummedCount()
        {
            var recipe = new CraftingRecipeDef
            {
                Type = "dupe_inputs",
                Name = "dupe_inputs",
                Inputs = new[] {
                    new CraftingItemStack { ItemId = GrenadeId, Count = 1 },
                    new CraftingItemStack { ItemId = GrenadeId, Count = 1 }, // dupe row
                },
                Outputs = new[] { new CraftingItemStack { ItemId = GrenadeId, Count = 1 } },
                SuccessRate = 1f,
                RefundRate = 0.5f,
                RareBonusRate = 0f,
            };
            var (system, store, inv, _) = MakeSystem(recipes: new[] { recipe });
            // Player has only 1 grenade. Recipe asks for 2 (1 + 1). Pre-flight
            // must reject — old code would have passed and silently lost it.
            GiveItems(inv, PlayerId, GrenadeId, 1);
            var result = system.TryCraft(PlayerId, 0);
            Assert.Equal(CraftingSystem.CraftingResult.MissingInputs, result);
            Assert.Equal(1, CountItem(store, PlayerId, GrenadeId)); // untouched
            Assert.Equal(1, system.TotalRejectedMissingInputs);

            // With 2 grenades in stock, the craft should now succeed.
            GiveItems(inv, PlayerId, GrenadeId, 1);
            Assert.Equal(2, CountItem(store, PlayerId, GrenadeId));
            var result2 = system.TryCraft(PlayerId, 0);
            Assert.Equal(CraftingSystem.CraftingResult.Success, result2);
            // 2 - 2 inputs + 1 output = 1
            Assert.Equal(1, CountItem(store, PlayerId, GrenadeId));
        }
    }
}
