using System;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Round 130 — Inventory / Item system.
    ///
    /// Per-player slot-based inventory of consumable items (potions / grenades / scrolls / sigils).
    /// Items are defined in Data/Configs/items.json and dispatched by InventoryItemType.
    ///
    /// Storage model:
    ///   - ComponentStore.PlayerInventoryItemId/Count/Used SOA arrays (see ComponentStore_Player.cs).
    ///   - Flat indexing: PlayerInventoryXxx[playerId * MAX_INVENTORY_SLOTS + slot].
    ///   - Default 0-initialized arrays; ResetInventory(playerId) flips to -1/0.
    ///
    /// Lifecycle:
    ///   - ResetInventory(playerId) called by GameManager on player add.
    ///   - AddItem(playerId, itemType) called by PickupSystem / drop handler / event system.
    ///   - UseItem(playerId, slot) called by hotkey handler / auto-trigger.
    ///   - RemoveItem(playerId, slot) called by craft/transfer or admin.
    ///
    /// Stack semantics:
    ///   - If existing slot has same itemId and count < MaxStack, increment.
    ///   - Else, find first empty slot.
    ///   - Else, return false (inventory full).
    ///
    /// UseItem dispatch (by InventoryItemType):
    ///   Heal        — PlayerCurrentHealth += Value (clamped to MaxHealth)
    ///   Mana        — PlayerMana += Value (clamped to MaxMana)
    ///   Shield      — PlayerShield += Value, PlayerShieldDuration = BuffDuration
    ///   SpeedBoost  — PlayerSlowFactor = 1.5, PlayerSlowDuration = (int)BuffDuration
    ///   DamageBoost — PlayerBuffFlags |= BuffType.AttackBoost, PlayerSlowDuration = (int)BuffDuration
    ///   AoEBurst    — apply Value damage to enemies within Radius of player (direct HP write, O(n_enemies) scan)
    ///   Summon      — not yet implemented in Round 130 (returns false, marks "TODO future round")
    ///   Cleanse     — PlayerCCFlags = 0 (clear all CC; BuffType bitfield)
    ///
    /// Performance:
    ///   - Add/Use/Remove are O(MAX_INVENTORY_SLOTS=8) per call; zero allocation.
    ///   - AoEBurst does an O(activeEnemies) scan, gated by Radius==0 → fast-path skip.
    ///   - PlayerInventoryUsed[] cached counter is O(1) for "is inventory full" checks.
    ///   - Not called per frame; only on pickup collection / hotkey press.
    /// </summary>
    public class InventorySystem
    {
        private readonly ComponentStore store;
        private readonly GameConfig gameConfig;
        private readonly IRenderer renderer;

        // Round 199 Direction 6 — optional CraftingSystem dependency. When set,
        // TryCraft() forwards to it. Lazy binding so InventorySystem can still be
        // constructed without crafting support (back-compat with existing test
        // scaffolding and GameManager wire-up).
        private CraftingSystem craftingSystem;

        // O(1) telemetry counters
        public int TotalAddCalls = 0;
        public int TotalUseCalls = 0;
        public int TotalDroppedFullInv = 0;
        public int TotalRejectedUnknown = 0;

        public InventorySystem(ComponentStore store, GameConfig gameConfig, IRenderer renderer)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.gameConfig = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
            this.renderer = renderer;
        }

        /// <summary>
        /// Round 199 Direction 6 — bind a CraftingSystem so TryCraft() can forward to it.
        /// Idempotent: a second call replaces the binding. Tests can leave this unset
        /// and TryCraft() returns BadRecipe cleanly (no null-ref crash).
        /// </summary>
        public void BindCraftingSystem(CraftingSystem crafting)
        {
            craftingSystem = crafting;
        }

        /// <summary>
        /// Try to add an item to the player's inventory.
        /// Returns true if added (new slot or stack merge); false if inventory full or item def unknown.
        /// Stacking: if an existing slot has the same itemId and Count < MaxStack, increment; else first empty slot.
        /// </summary>
        public bool AddItem(int playerId, int itemTypeId)
        {
            if (playerId < 0 || playerId >= ComponentStore.MAX_PLAYERS) return false;
            if (itemTypeId < 0 || itemTypeId >= gameConfig.ItemDefs.Length) return false;
            var def = gameConfig.ItemDefs[itemTypeId];
            if (def == null) return false;
            // Claude bug scan fix: reject Unknown items at AddItem time so they don't
            // permanently occupy a slot (UseItem would later refuse to consume them).
            if (def.ItemType == InventoryItemType.Unknown) return false;

            TotalAddCalls++;

            // Stack-merge pass: scan slots for matching itemId with room.
            for (int s = 0; s < ComponentStore.MAX_INVENTORY_SLOTS; s++)
            {
                int idx = playerId * ComponentStore.MAX_INVENTORY_SLOTS + s;
                if (store.PlayerInventoryItemId[idx] == itemTypeId)
                {
                    int cur = store.PlayerInventoryCount[idx];
                    if (cur < def.MaxStack)
                    {
                        store.PlayerInventoryCount[idx] = cur + 1;
                        renderer?.Log($"[INVENTORY] P{playerId} stacked {def.Name} ({cur + 1}/{def.MaxStack})");
                        return true;
                    }
                }
            }

            // Empty-slot pass.
            for (int s = 0; s < ComponentStore.MAX_INVENTORY_SLOTS; s++)
            {
                int idx = playerId * ComponentStore.MAX_INVENTORY_SLOTS + s;
                if (store.PlayerInventoryItemId[idx] == -1)
                {
                    store.PlayerInventoryItemId[idx] = itemTypeId;
                    store.PlayerInventoryCount[idx] = 1;
                    store.PlayerInventoryUsed[playerId]++;
                    renderer?.Log($"[INVENTORY] P{playerId} added {def.Name}");
                    return true;
                }
            }

            TotalDroppedFullInv++;
            renderer?.Log($"[INVENTORY] P{playerId} inventory full, dropped {def.Name}");
            return false;
        }

        /// <summary>
        /// Try to use (consume) the item at the given slot.
        /// Returns true if used (effect applied + count decremented / slot cleared).
        /// Returns false if slot is empty, out-of-range, or item def is Unknown.
        /// Unknown item type is logged but does not consume the slot (defensive).
        /// </summary>
        public bool UseItem(int playerId, int slot)
        {
            if (playerId < 0 || playerId >= ComponentStore.MAX_PLAYERS) return false;
            if (slot < 0 || slot >= ComponentStore.MAX_INVENTORY_SLOTS) return false;
            int idx = playerId * ComponentStore.MAX_INVENTORY_SLOTS + slot;
            int itemId = store.PlayerInventoryItemId[idx];
            if (itemId < 0) return false;
            if (itemId >= gameConfig.ItemDefs.Length) return false;
            var def = gameConfig.ItemDefs[itemId];
            if (def == null || def.ItemType == InventoryItemType.Unknown)
            {
                TotalRejectedUnknown++;
                renderer?.Log($"[INVENTORY] P{playerId} slot {slot} item def is Unknown, refusing to use");
                return false;
            }

            TotalUseCalls++;
            bool applied = DispatchUse(playerId, slot, idx, def);

            if (applied)
            {
                // Decrement / clear slot.
                int cur = store.PlayerInventoryCount[idx];
                if (cur > 1)
                {
                    store.PlayerInventoryCount[idx] = cur - 1;
                }
                else
                {
                    store.PlayerInventoryItemId[idx] = -1;
                    store.PlayerInventoryCount[idx] = 0;
                    if (store.PlayerInventoryUsed[playerId] > 0)
                        store.PlayerInventoryUsed[playerId]--;
                }
                store.PlayerInventoryUsedTotal[playerId]++;
            }
            return applied;
        }

        /// <summary>
        /// Remove an item from a slot without applying effect. Returns true on success.
        /// Useful for craft, transfer, or admin cleanup.
        /// </summary>
        public bool RemoveItem(int playerId, int slot)
        {
            if (playerId < 0 || playerId >= ComponentStore.MAX_PLAYERS) return false;
            if (slot < 0 || slot >= ComponentStore.MAX_INVENTORY_SLOTS) return false;
            int idx = playerId * ComponentStore.MAX_INVENTORY_SLOTS + slot;
            if (store.PlayerInventoryItemId[idx] == -1) return false;
            store.PlayerInventoryItemId[idx] = -1;
            store.PlayerInventoryCount[idx] = 0;
            if (store.PlayerInventoryUsed[playerId] > 0)
                store.PlayerInventoryUsed[playerId]--;
            return true;
        }

        /// <summary>Look up an item's display name (or empty string if id is invalid).</summary>
        public string GetItemName(int itemId)
        {
            if (itemId < 0 || itemId >= gameConfig.ItemDefs.Length) return "";
            var def = gameConfig.ItemDefs[itemId];
            return def?.Name ?? "";
        }

        /// <summary>Total used items across all slots (lifetime stat for telemetry/achievements).</summary>
        public int GetUsedTotal(int playerId)
        {
            if (playerId < 0 || playerId >= ComponentStore.MAX_PLAYERS) return 0;
            return store.PlayerInventoryUsedTotal[playerId];
        }

        /// <summary>
        /// Round 199 Direction 6 — Crafting entry point. Forwards to the bound CraftingSystem
        /// when one is registered; returns BadRecipe cleanly when crafting is not wired
        /// (e.g. tests that don't exercise the recipe system). Single indirection so the
        /// crafting code path is reachable from UI / hotkey / quest handlers without
        /// needing a direct CraftingSystem reference.
        /// </summary>
        public CraftingSystem.CraftingResult TryCraft(int playerId, int recipeId)
        {
            if (craftingSystem == null) return CraftingSystem.CraftingResult.BadRecipe;
            return craftingSystem.TryCraft(playerId, recipeId);
        }

        // ── internal dispatch ────────────────────────────────────────────
        private bool DispatchUse(int playerId, int slot, int idx, ItemDef def)
        {
            switch (def.ItemType)
            {
                case InventoryItemType.Heal:
                {
                    float currentHealth = store.PlayerCurrentHealth[playerId];
                    float maxHealth = store.PlayerMaxHealth[playerId];
                    float healed = Math.Min(def.Value, maxHealth - currentHealth);
                    if (healed <= 0f) { renderer?.Log($"[INVENTORY] P{playerId} already at full HP, {def.Name} no-op"); return false; }
                    store.PlayerCurrentHealth[playerId] = currentHealth + healed;
                    renderer?.Log($"[INVENTORY] P{playerId} used {def.Name}: +{healed} HP");
                    return true;
                }
                case InventoryItemType.Mana:
                {
                    float currentMana = store.PlayerMana[playerId];
                    float maxMana = store.PlayerMaxMana[playerId];
                    float restored = Math.Min(def.Value, maxMana - currentMana);
                    if (restored <= 0f) { renderer?.Log($"[INVENTORY] P{playerId} already at full mana, {def.Name} no-op"); return false; }
                    store.PlayerMana[playerId] = currentMana + restored;
                    renderer?.Log($"[INVENTORY] P{playerId} used {def.Name}: +{restored} mana");
                    return true;
                }
                case InventoryItemType.Shield:
                {
                    store.PlayerShield[playerId] += def.Value;
                    int dur = def.BuffDuration > 0f ? (int)def.BuffDuration : 0;
                    // Reuse shield duration field; 0 means "permanent until depleted".
                    if (dur > 0) store.PlayerShieldDuration[playerId] = dur;
                    renderer?.Log($"[INVENTORY] P{playerId} used {def.Name}: +{def.Value} shield for {dur}s");
                    return true;
                }
                case InventoryItemType.SpeedBoost:
                {
                    // Speed is stored as slowFactor; 1.5 = +50% speed (negative slow = boost, consistent with PickupSystem).
                    store.PlayerSlowFactor[playerId] = 1.5f;
                    int dur = def.BuffDuration > 0f ? (int)def.BuffDuration : 0;
                    store.PlayerSlowDuration[playerId] = dur;
                    renderer?.Log($"[INVENTORY] P{playerId} used {def.Name}: +50% speed for {dur}s");
                    return true;
                }
                case InventoryItemType.DamageBoost:
                {
                    // Claude bug scan fix: use PlayerDamageBoostDuration (own field) instead of
                    // PlayerSlowDuration, which is shared with SpeedBoost. Sharing would cause
                    // SpeedBoost→DamageBoost sequence to clobber the speed buff timer.
                    store.PlayerBuffFlags[playerId] |= BuffType.AttackBoost;
                    int dur = def.BuffDuration > 0f ? (int)def.BuffDuration : 0;
                    store.PlayerDamageBoostDuration[playerId] = dur;
                    // BUG scan fix: persist the per-item magnitude (e.g., 0.2 = +20%) into
                    // PlayerDamageBoostMultiplier so the actual damage multiplier reflects the
                    // configured Value. Without this, every DamageBoost item behaves identically.
                    // Take max so a stronger potion used during a weaker buff upgrades magnitude
                    // rather than overwriting it (e.g., +20% active, use +50% → still +50%).
                    if (def.Value > store.PlayerDamageBoostMultiplier[playerId])
                        store.PlayerDamageBoostMultiplier[playerId] = def.Value;
                    renderer?.Log($"[INVENTORY] P{playerId} used {def.Name}: +{(int)(def.Value * 100)}% attack for {dur}s");
                    return true;
                }
                case InventoryItemType.AoEBurst:
                {
                    if (def.Radius <= 0f) { renderer?.Log($"[INVENTORY] P{playerId} {def.Name} has no radius, no-op"); return false; }
                    float px = store.PositionX[playerId];
                    float py = store.PositionY[playerId];
                    float rSq = def.Radius * def.Radius;
                    int hitCount = 0;
                    var enemyIds = store.ActiveEnemyIds;
                    for (int e = 0; e < enemyIds.Count; e++)
                    {
                        int enemyId = enemyIds[e];
                        if (!store.EnemyActive[enemyId]) continue;
                        float dx = store.PositionX[enemyId] - px;
                        float dy = store.PositionY[enemyId] - py;
                        if (dx * dx + dy * dy > rSq) continue;
                        if (store.EnemyIsInvulnerable[enemyId]) continue;
                        store.EnemyHealth[enemyId] -= def.Value;
                        if (store.EnemyHealth[enemyId] <= 0f)
                            store.QueueEnemyDeath(enemyId, playerId);
                        hitCount++;
                    }
                    renderer?.Log($"[INVENTORY] P{playerId} used {def.Name}: hit {hitCount} enemies for {def.Value} dmg");
                    return true;
                }
                case InventoryItemType.Summon:
                {
                    // TODO: wire into NecromancerSystem or PlayerSummon. For now, log and refuse.
                    renderer?.Log($"[INVENTORY] P{playerId} used {def.Name}: Summon not yet implemented (TODO future round)");
                    return false;
                }
                case InventoryItemType.Cleanse:
                {
                    // Claude bug scan fix: log message previously claimed "all CC" but only
                    // PlayerStunDuration was cleared. Other CC effects (slow, root, silence) are
                    // not tracked as per-status duration counters in the player store — a full
                    // cleanse would require a PlayerCCFlags bitfield (out of scope for Round 130).
                    // We clear the one CC field that exists and acknowledge the limitation.
                    store.PlayerStunDuration[playerId] = 0;
                    renderer?.Log($"[INVENTORY] P{playerId} used {def.Name}: cleansed stun (other CCs not tracked at this layer)");
                    return true;
                }
                default:
                {
                    TotalRejectedUnknown++;
                    renderer?.Log($"[INVENTORY] P{playerId} slot {slot} item def {def.ItemType} not handled in DispatchUse");
                    return false;
                }
            }
        }
    }
}
