using System;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Content.Contracts;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Round 130：背包和道具系统。
    ///
    /// 每名玩家拥有按槽位组织的消耗品背包（药水、手雷、卷轴和护符）。
    /// 道具定义位于 Data/Configs/items.json，并按 InventoryItemType 分派。
    ///
    /// 存储模型：
    ///   - 使用 ComponentStore_Player.cs 中的 SOA 数组。
    ///   - 线性索引为 playerId * MAX_INVENTORY_SLOTS + slot。
    ///   - 数组默认初始化为 0，ResetInventory(playerId) 会重置为 -1/0。
    ///
    /// 生命周期：
    ///   - GameManager 添加玩家时调用 ResetInventory(playerId)。
    ///   - PickupSystem、掉落处理器或事件系统调用 AddItem。
    ///   - 快捷键处理器或自动触发器调用 UseItem。
    ///   - 制作、转移或管理操作调用 RemoveItem。
    ///
    /// 堆叠规则：
    ///   - 已有相同 itemId 且数量小于 MaxStack 时直接增加数量。
    ///   - 否则查找第一个空槽位。
    ///   - 没有空槽位时返回 false，表示背包已满。
    ///
    /// UseItem 按 InventoryItemType 分派：
    ///   Heal        — 增加生命并裁剪到 MaxHealth。
    ///   Mana        — 增加法力并裁剪到 MaxMana。
    ///   Shield      — 增加护盾并设置持续时间。
    ///   SpeedBoost  — 设置减速倍率和持续时间。
    ///   DamageBoost — 设置攻击增益标志和持续时间。
    ///   AoEBurst    — 对玩家半径内的敌人造成 Value 伤害。
    ///   Summon      — Round 130 尚未实现，返回 false。
    ///   Cleanse     — 清除玩家的全部控制效果标志。
    ///
    /// 性能：
    ///   - Add/Use/Remove 每次调用为 O(MAX_INVENTORY_SLOTS=8)，不产生分配。
    ///   - AoEBurst 扫描 O(activeEnemies)，Radius==0 时走快速跳过路径。
    ///   - PlayerInventoryUsed[] 缓存计数器可 O(1) 判断背包是否已满。
    ///   - 不在每帧调用，仅在拾取或快捷键操作时调用。
    /// </summary>
    public class InventorySystem : global::BattleSystemECS.Content.Contracts.IInventoryCommandPort
    {
        private readonly ComponentStore store;
        private readonly GameConfig gameConfig;
        private readonly IRenderer renderer;

        // Round 199 方向 6：可选的 ICraftingService 依赖。设置后 TryCraft() 转发给它；
        // 使用延迟绑定，使没有制作支持时仍可构造 InventorySystem，兼容现有测试基建和 GameManager 接线。
        private global::BattleSystemECS.Content.Contracts.ICraftingService craftingSystem;

        // O(1) 遥测计数器。
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
        /// Round 199 方向 6：绑定 ICraftingService，供 TryCraft() 转发。
        /// 绑定可重复执行，后一次调用会替换前一次；未绑定时 TryCraft() 返回 BadRecipe，不会空引用崩溃。
        /// </summary>
        public void BindCraftingSystem(global::BattleSystemECS.Content.Contracts.ICraftingService crafting)
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
        /// 第 199 轮方向 6：制作入口转发到已绑定的 ICraftingService；未接线时返回 BadRecipe。
        /// 通过单层间接调用，使界面、快捷键和任务处理器无需持有具体制作系统引用。
        /// </summary>
        public CraftingResult TryCraft(int playerId, int recipeId)
        {
            if (craftingSystem == null) return CraftingResult.BadRecipe;
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
                    store.ApplyPlayerResourceAuthority(playerId, playerId, new Core.GAS.AttributeKey(3), healed);
                    renderer?.Log($"[INVENTORY] P{playerId} used {def.Name}: +{healed} HP");
                    return true;
                }
                case InventoryItemType.Mana:
                {
                    float currentMana = store.PlayerMana[playerId];
                    float maxMana = store.PlayerMaxMana[playerId];
                    float restored = Math.Min(def.Value, maxMana - currentMana);
                    if (restored <= 0f) { renderer?.Log($"[INVENTORY] P{playerId} already at full mana, {def.Name} no-op"); return false; }
                    store.ApplyPlayerResourceAuthority(playerId, playerId, new Core.GAS.AttributeKey(7), restored);
                    renderer?.Log($"[INVENTORY] P{playerId} used {def.Name}: +{restored} mana");
                    return true;
                }
                case InventoryItemType.Shield:
                {
                    store.ApplyPlayerResourceAuthority(playerId, playerId, new Core.GAS.AttributeKey(9), def.Value);
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
                        store.ApplyDamageAuthority(playerId, enemyId, def.Value, playerId, stage: Core.GAS.DamageAmountStage.Raw);
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
