using System;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Content.Contracts;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Round 199 Direction 6 — Crafting System.
    ///
    /// Consumes inventory items per a recipe (CraftingRecipeDef) and produces new
    /// 道具系统是 IInventoryCommandPort 的薄层封装；输出复用 AddItem()，输入移除由本系统处理。
    /// (the inventory has no direct "remove count" API, so crafting iterates slots
    /// and decrements one at a time).
    ///
    /// Three probabilities govern the outcome of a single TryCraft call:
    ///   1. SuccessRate   — chance the inputs are consumed AND outputs are produced.
    ///   2. RefundRate    — on failure, fraction of each consumed input that comes back.
    ///                       RefundRate=0 means full loss on failure; =1.0 means all inputs
    ///                       return (functionally equivalent to no-op).
    ///   3. RareBonusRate — on success, chance the player ALSO receives RareBonusOutputs
    ///                       (or double Outputs if RareBonusOutputs is empty). Stacks on
    ///                       top of base outputs.
    ///
    /// Pre-flight check: TryCraft() verifies the player owns enough of each input item
    /// (sum across all slots) before any mutation. If any input is short, no items are
    /// touched and the call returns CraftingResult.MissingInputs.
    ///
    /// Output overflow: AddItem() may refuse if inventory is full. In that case the
    /// craft is treated as successful (consumes inputs) but the output is dropped.
    /// This is the design choice documented in the cron direction file — it prevents
    /// a stranded "all inputs consumed, no outputs delivered" state where the player
    /// could spam TryCraft to lose items. Failed delivery is logged via IRenderer.
    /// </summary>
    public class CraftingSystem : global::BattleSystemECS.Content.Contracts.ICraftingService
    {
        private readonly ComponentStore store;
        private readonly GameConfig gameConfig;
        private readonly global::BattleSystemECS.Content.Contracts.IInventoryCommandPort inventory;
        private readonly IRenderer renderer;

        // O(1) telemetry counters — exposed for tests / HUD / quest completion checks.
        public int TotalAttempts = 0;
        public int TotalSuccesses = 0;
        public int TotalFailures = 0;
        public int TotalRareBonuses = 0;
        public int TotalRejectedMissingInputs = 0;
        public int TotalRejectedBadRecipe = 0;

        public CraftingSystem(
            ComponentStore store,
            GameConfig gameConfig,
            global::BattleSystemECS.Content.Contracts.IInventoryCommandPort inventory,
            IRenderer renderer,
            int seed = 0)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.gameConfig = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
            this.inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
            this.renderer = renderer;
            // seed≠0 仅测试播种 store 流；生产 seed=0 不 Reset、不另开墙钟 Random。
            if (seed != 0) store.Determinism.Reset(seed);
        }

        /// <summary>
        /// Outcome of a single TryCraft call. The reason codes let callers (UI,
        /// quest systems) distinguish between "lucky" and "unlucky" failures.
        /// </summary>
        /// <summary>
        /// Attempt to craft using the given recipe id. See class doc for full semantics.
        /// Returns a CraftingResult describing the outcome. Always safe to call —
        /// invalid player / recipe ids return BadRecipe or MissingInputs without mutation.
        /// </summary>
        public CraftingResult TryCraft(int playerId, int recipeId)
        {
            if ((uint)playerId >= ComponentStore.MAX_PLAYERS)
            {
                TotalRejectedBadRecipe++;
                return CraftingResult.BadRecipe;
            }
            if (recipeId < 0 || recipeId >= gameConfig.CraftingRecipes.Length)
            {
                TotalRejectedBadRecipe++;
                return CraftingResult.BadRecipe;
            }

            var recipe = gameConfig.CraftingRecipes[recipeId];
            if (recipe == null)
            {
                TotalRejectedBadRecipe++;
                return CraftingResult.BadRecipe;
            }

            // All early-exit validation passed — count this as a real attempt now.
            TotalAttempts++;

            // Pre-flight: count available inputs across all inventory slots.
            if (!HasAllInputs(playerId, recipe))
            {
                TotalRejectedMissingInputs++;
                renderer?.Log($"[CRAFTING] P{playerId} tried recipe {recipeId} ({recipe.Type}) — missing inputs, no-op");
                return CraftingResult.MissingInputs;
            }

            // Consume inputs first. Inputs are decremented slot-by-slot using the
            // 与 IInventoryCommandPort 使用相同的线性索引；每个输入项单独处理。
            // counter rather than removing slots wholesale so partial stacks survive
            // (e.g. 5 healing potions in 2 slots → 4 potions in 2 slots after crafting).
            ConsumeInputs(playerId, recipe);

            // Roll success.
            bool success = NextFloat() <= recipe.SuccessRate;

            if (success)
            {
                TotalSuccesses++;
                bool rareBonus = recipe.RareBonusRate > 0f && NextFloat() <= recipe.RareBonusRate;
                bool delivered = DeliverOutputs(playerId, recipe.Outputs);
                if (rareBonus)
                {
                    TotalRareBonuses++;
                    var bonus = recipe.RareBonusOutputs != null && recipe.RareBonusOutputs.Length > 0
                        ? recipe.RareBonusOutputs
                        : recipe.Outputs;
                    delivered = DeliverOutputs(playerId, bonus) && delivered;
                }
                if (!delivered)
                {
                    // Inputs were already consumed, but at least one output couldn't be
                    // placed. Treat as FullInventory so the caller can surface a HUD
                    // warning ("crafting succeeded but inventory full — output lost").
                    renderer?.Log($"[CRAFTING] P{playerId} recipe {recipeId} ({recipe.Type}) succeeded but inventory full — output lost");
                    return CraftingResult.FullInventory;
                }
                renderer?.Log($"[CRAFTING] P{playerId} recipe {recipeId} ({recipe.Type}) SUCCESS" +
                              (rareBonus ? " (rare bonus!)" : ""));
                return rareBonus ? CraftingResult.SuccessRareBonus : CraftingResult.Success;
            }
            else
            {
                TotalFailures++;
                // Refund partial inputs.
                if (recipe.RefundRate > 0f)
                {
                    RefundInputs(playerId, recipe);
                }
                renderer?.Log($"[CRAFTING] P{playerId} recipe {recipeId} ({recipe.Type}) FAIL (refund {recipe.RefundRate:P0})");
                return CraftingResult.Failure;
            }
        }

        // ── input checking ──────────────────────────────────────────────

        // Sum count of a single item id across all inventory slots of a player.
        // O(MAX_INVENTORY_SLOTS = 8) per call. Used by HasAllInputs and could be
        // reused by other inventory queries in the future. Note: empty slots
        // have PlayerInventoryItemId == 0 by default (int[] zero-init), so when
        // itemId == 0 an empty slot's Count (also 0) contributes nothing to the
        // sum — the math is still correct. We rely on this convention; mutating
        // the inventory reset path to use -1 as the empty sentinel is a separate
        // refactor tracked elsewhere.
        private int CountInventory(int playerId, int itemId)
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

        // Sum counts of each input item across all inventory slots. Returns true
        // if every input has at least its required count.
        //
        // Claude bug scan fix: when a recipe lists the same ItemId in multiple
        // input entries (e.g. grenade_crate has {ItemId:5,Count:1} twice), the
        // old per-entry CountInventory pass would see "have=1 >= need=1" for
        // both rows, then ConsumeInputs would fail to find the second copy and
        // log a false "concurrent mutation" warning while the player still
        // lost their only grenade. We now aggregate required counts by ItemId
        // FIRST, then do a single CountInventory lookup per unique id, so the
        // check matches what ConsumeInputs will actually try to take.
        private bool HasAllInputs(int playerId, CraftingRecipeDef recipe)
        {
            var inputs = recipe.Inputs;
            if (inputs == null) return true; // empty input list = always craftable
            // Pass 1: aggregate required counts per ItemId, ignoring malformed
            // entries (null, ItemId<0, Count<=0). A small stack-allocated array
            // covers recipes with up to 8 unique input ids (recipes rarely have
            // more than 3-4 inputs, so 8 is generous headroom).
            Span<int> uniqueIds = stackalloc int[8];
            Span<int> required = stackalloc int[8];
            int uniqueCount = 0;
            for (int i = 0; i < inputs.Length; i++)
            {
                var need = inputs[i];
                if (need == null || need.ItemId < 0 || need.Count <= 0) continue;
                int existingIdx = -1;
                for (int u = 0; u < uniqueCount; u++)
                {
                    if (uniqueIds[u] == need.ItemId) { existingIdx = u; break; }
                }
                if (existingIdx >= 0)
                {
                    // Guard against int overflow when summing a malformed config
                    // (e.g. 2^30 entries with Count=2^30 each). Clamp to 1M which
                    // is way above any realistic recipe requirement.
                    long sum = (long)required[existingIdx] + need.Count;
                    required[existingIdx] = sum > 1_000_000 ? 1_000_000 : (int)sum;
                }
                else
                {
                    if (uniqueCount >= uniqueIds.Length)
                    {
                        renderer?.Log($"[CRAFTING] recipe '{recipe.Type}' has more than 8 unique input ids; truncating pre-flight check");
                        continue;
                    }
                    uniqueIds[uniqueCount] = need.ItemId;
                    required[uniqueCount] = need.Count;
                    uniqueCount++;
                }
            }
            // Pass 2: compare aggregated required vs inventory stock.
            for (int u = 0; u < uniqueCount; u++)
            {
                int have = CountInventory(playerId, uniqueIds[u]);
                if (have < required[u]) return false;
            }
            return true;
        }

        // Same dedup logic as HasAllInputs, but writes the consumption so the
        // cumulative consumption respects the aggregated count. This fixes the
        // grenade_crate case where {ItemId:5,Count:1}×2 must take 2 grenades,
        // not stop after 1 with a misleading "concurrent mutation" log.
        private void ConsumeInputs(int playerId, CraftingRecipeDef recipe)
        {
            var inputs = recipe.Inputs;
            if (inputs == null) return;
            // Aggregate required counts by ItemId (same logic as HasAllInputs).
            Span<int> uniqueIds = stackalloc int[8];
            Span<int> required = stackalloc int[8];
            int uniqueCount = 0;
            for (int i = 0; i < inputs.Length; i++)
            {
                var need = inputs[i];
                if (need == null || need.ItemId < 0 || need.Count <= 0) continue;
                int existingIdx = -1;
                for (int u = 0; u < uniqueCount; u++)
                {
                    if (uniqueIds[u] == need.ItemId) { existingIdx = u; break; }
                }
                if (existingIdx >= 0)
                {
                    long sum = (long)required[existingIdx] + need.Count;
                    required[existingIdx] = sum > 1_000_000 ? 1_000_000 : (int)sum;
                }
                else
                {
                    if (uniqueCount >= uniqueIds.Length)
                    {
                        renderer?.Log($"[CRAFTING] recipe '{recipe.Type}' has more than 8 unique input ids; truncating consumption");
                        continue;
                    }
                    uniqueIds[uniqueCount] = need.ItemId;
                    required[uniqueCount] = need.Count;
                    uniqueCount++;
                }
            }
            // Now consume the aggregated amount for each unique ItemId.
            for (int u = 0; u < uniqueCount; u++)
            {
                int itemId = uniqueIds[u];
                int remaining = required[u];
                for (int s = 0; s < ComponentStore.MAX_INVENTORY_SLOTS && remaining > 0; s++)
                {
                    int idx = playerId * ComponentStore.MAX_INVENTORY_SLOTS + s;
                    if (store.PlayerInventoryItemId[idx] != itemId) continue;
                    int cur = store.PlayerInventoryCount[idx];
                    if (cur <= 0) continue;
                    int take = Math.Min(cur, remaining);
                    int newCount = cur - take;
                    if (newCount <= 0)
                    {
                        store.PlayerInventoryItemId[idx] = -1;
                        store.PlayerInventoryCount[idx] = 0;
                        if (store.PlayerInventoryUsed[playerId] > 0)
                            store.PlayerInventoryUsed[playerId]--;
                    }
                    else
                    {
                        store.PlayerInventoryCount[idx] = newCount;
                    }
                    remaining -= take;
                }
                if (remaining > 0)
                {
                    renderer?.Log($"[CRAFTING] WARN: P{playerId} consume itemId {itemId} short by {remaining} (concurrent mutation?)");
                }
            }
        }

        // Refund a fraction of each consumed input. RefundRate=0.5 means the player
        // gets 50% back (rounded down). RefundRate=1.0 means all inputs return.
        // RefundRate=0 means no refund (full loss).
        //
        // Refund uses AddItem so the inventory state machine (stack-merge, empty-slot
        // search) stays consistent. If the inventory is full at refund time, the
        // refund is silently dropped — players still lose some value, but the game
        // doesn't crash or duplicate items.
        private void RefundInputs(int playerId, CraftingRecipeDef recipe)
        {
            var inputs = recipe.Inputs;
            if (inputs == null) return;
            for (int i = 0; i < inputs.Length; i++)
            {
                var need = inputs[i];
                if (need == null || need.ItemId < 0 || need.Count <= 0) continue;
                int refundCount = (int)Math.Floor(need.Count * recipe.RefundRate);
                if (refundCount <= 0) continue;
                for (int k = 0; k < refundCount; k++)
                {
                    inventory.AddItem(playerId, need.ItemId);
                }
            }
        }

        // ── output delivery ────────────────────────────────────────────

        // Try to deliver each output to the inventory. Returns false if AddItem
        // refused for ANY output (the caller treats that as FullInventory). Outputs
        // are delivered in order, so if output[0] was added but output[1] was
        // rejected, the player keeps output[0] — partial delivery is acceptable.
        private bool DeliverOutputs(int playerId, CraftingItemStack[] outputs)
        {
            if (outputs == null || outputs.Length == 0) return true;
            bool allDelivered = true;
            for (int i = 0; i < outputs.Length; i++)
            {
                var outp = outputs[i];
                if (outp == null || outp.ItemId < 0 || outp.Count <= 0) continue;
                for (int k = 0; k < outp.Count; k++)
                {
                    if (!inventory.AddItem(playerId, outp.ItemId))
                    {
                        allDelivered = false;
                        renderer?.Log($"[CRAFTING] P{playerId} could not deliver output itemId={outp.ItemId} (inventory full?)");
                        break; // stop trying to add this output entry
                    }
                }
            }
            return allDelivered;
        }

        // ── RNG ────────────────────────────────────────────────────────

        // [0, 1) float for probability rolls. Wraps a System.Random so tests can
        // pass a fixed seed for determinism.
        private float NextFloat()
        {
            return (float)store.Determinism.NextDouble();
        }

        // ── read helpers ────────────────────────────────────────────────

        public int GetRecipeCount() => gameConfig.CraftingRecipes?.Length ?? 0;

        public CraftingRecipeDef GetRecipe(int recipeId)
        {
            if (recipeId < 0 || recipeId >= gameConfig.CraftingRecipes.Length) return null;
            return gameConfig.CraftingRecipes[recipeId];
        }
    }
}
