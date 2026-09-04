using System;
using BattleSystemECS.Config;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Reforge System — Diablo / ARPG style tower affix reroll (Reforge Split B).
    ///
    /// Design:
    /// - During BuildPhase, the player picks a tower and one of its affix slots, and
    ///   spends gold to roll a new affix for that slot. Cost is
    ///   `BaseCost + reforgeCount * IncrementPerReroll` and is capped at `MaxRerollsPerTower`.
    /// - Locked slots (bit s set in TowerAffixLockMask) keep their current affix during
    ///   RerollAffix — only unlocked slots get rerolled. Toggling a lock costs `LockSlotCost`.
    /// - Rarity weights (ReforgeConfig.RarityWeights[]) drive a weighted pick of
    ///   `TowerAffixDef.Rarity` (0=Common ... 4=Legendary). MinLevel of the rolled affix
    ///   is gated by the tower's current level.
    ///
    /// Storage (SOA, all in ComponentStore.Tower fields — see Round 35 Split A + B):
    /// - TowerAffixSlotCount: how many slots this tower has
    /// - TowerAffixIds: [slotIndex][towerId] = index into GameConfig.TowerAffixes[]
    /// - TowerAffixStackCount: [slotIndex][towerId] = current stack count (1..MaxStack)
    /// - TowerAffixLockMask: bitmask of locked slots (NEW in Split B)
    /// - TowerReforgeCount: how many times this tower has been reforged (NEW in Split B)
    ///
    /// Hot-path impact: zero (BuildPhase only — no per-frame work in Update()).
    /// </summary>
    public class ReforgeSystem
    {
        private readonly ComponentStore store;
        private readonly IRenderer renderer;
        private readonly GameConfig gameConfig;
        private readonly int playerId;

        public ReforgeSystem(ComponentStore store, IRenderer renderer, GameConfig gameConfig, int playerId, int seed = 0)
        {
            this.store = store;
            this.renderer = renderer;
            this.gameConfig = gameConfig;
            this.playerId = playerId;
            if (seed != 0) store.Determinism.Reset(seed);
        }

        // Cached config — keeps behavior safe when gameConfig.Reforge is null
        private ReforgeConfig Config
        {
            get
            {
                if (gameConfig?.Reforge == null)
                {
                    // Fallback: safe disabled defaults — the system becomes inert
                    return new ReforgeConfig { Enabled = false };
                }
                return gameConfig.Reforge;
            }
        }

        /// <summary>
        /// Per-frame tick — no-op. The system is event-driven via RerollAffix / SetSlotLocked
        /// / OnEnterBuildPhase calls. Kept for BuildGroup pipeline symmetry.
        /// </summary>
        public void Update()
        {
            // No per-frame work. All mutations happen on explicit API calls.
        }

        /// <summary>
        /// Reset the reforge count for the player's towers at the start of a new BuildPhase.
        /// Per-tower reforge count is also a reasonable alternative but for now we keep it
        /// persistent across phases (lifetime is the level / match).
        /// </summary>
        public void OnEnterBuildPhase()
        {
            if (!Config.Enabled) return;
            // Intentionally do NOT clear per-tower reforge counts here — those track
            // "how many times has this tower been reforged" across the run, not the phase.
            // Per-phase cap is a different concept and could be added later.
        }

        // ════════════════════════════════════════════════════════════════════
        //  Cost API
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Compute the gold cost of the (count+1)-th reroll on a tower.
        /// cost = BaseCost + count * IncrementPerReroll. Negative counts clamp to 0.
        /// </summary>
        public float GetRerollCost(int reforgeCount)
        {
            if (reforgeCount < 0) reforgeCount = 0;
            return Config.BaseCost + reforgeCount * Config.IncrementPerReroll;
        }

        /// <summary>Get the cost to lock (or unlock) a single slot. Currently a flat fee.</summary>
        public float GetLockSlotCost() => Config.LockSlotCost;

        /// <summary>Get the hard cap on rerolls per tower. Returns 0 if disabled.</summary>
        public int GetMaxRerollsPerTower()
        {
            return Config.Enabled ? Config.MaxRerollsPerTower : 0;
        }

        /// <summary>Get the current reforge count for a tower. Returns 0 if invalid.</summary>
        public int GetReforgeCount(int towerId)
        {
            if (!ComponentStore.IsValidEntity(towerId)) return 0;
            return store.GetTowerReforgeCount(towerId);
        }

        /// <summary>Get the remaining reroll budget for a tower (clamped at 0).</summary>
        public int GetRemainingRerolls(int towerId)
        {
            if (!ComponentStore.IsValidEntity(towerId)) return 0;
            int used = store.GetTowerReforgeCount(towerId);
            int rem = Config.MaxRerollsPerTower - used;
            return rem < 0 ? 0 : rem;
        }

        // ════════════════════════════════════════════════════════════════════
        //  Lock API
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Toggle the lock state of a single affix slot. Charges LockSlotCost from the player
        /// (regardless of lock direction). Returns true on success, false on insufficient gold
        /// or invalid inputs.
        /// </summary>
        public bool SetSlotLocked(int towerId, int slotIndex, bool locked)
        {
            if (!Config.Enabled) return false;
            if (!ComponentStore.IsValidEntity(towerId)) return false;
            int slotCount = store.GetTowerAffixSlotCount(towerId);
            if (slotIndex < 0 || slotIndex >= slotCount) return false;

            // If already in target state, do nothing (no charge)
            bool currentLocked = store.IsTowerAffixSlotLocked(towerId, slotIndex);
            if (currentLocked == locked) return true;

            float cost = Config.LockSlotCost;
            float gold = store.GetPlayerGold(playerId);
            if (gold < cost) return false;
            store.SetPlayerGold(playerId, gold - cost);
            store.SetTowerAffixSlotLocked(towerId, slotIndex, locked);
            return true;
        }

        /// <summary>Query whether a single slot is locked.</summary>
        public bool IsSlotLocked(int towerId, int slotIndex)
        {
            if (!ComponentStore.IsValidEntity(towerId)) return false;
            return store.IsTowerAffixSlotLocked(towerId, slotIndex);
        }

        // ════════════════════════════════════════════════════════════════════
        //  Reroll API
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Re-roll the affix in `slotIndex` of `towerId`. Charges the configured gold cost
        /// (see GetRerollCost) and increments the per-tower reforge count.
        /// Returns true on success, false on:
        ///   - system disabled
        ///   - invalid tower / slot
        ///   - reforge cap reached
        ///   - insufficient gold
        ///   - rolled affix pool empty (no eligible affix)
        /// On success, the slot's affix index + stack count are updated.
        /// Locked slots are NOT rolled (this method still works on unlocked slots only).
        /// </summary>
        public bool RerollAffix(int towerId, int slotIndex)
        {
            if (!Config.Enabled) return false;
            if (!ComponentStore.IsValidEntity(towerId)) return false;

            int slotCount = store.GetTowerAffixSlotCount(towerId);
            if (slotIndex < 0 || slotIndex >= slotCount) return false;

            int reforgeCount = store.GetTowerReforgeCount(towerId);
            if (reforgeCount >= Config.MaxRerollsPerTower)
            {
                renderer?.Log($"[REFORGE] Tower {towerId} reached reforge cap ({Config.MaxRerollsPerTower}).");
                return false;
            }

            float cost = GetRerollCost(reforgeCount);
            float gold = store.GetPlayerGold(playerId);
            if (gold < cost)
            {
                renderer?.Log($"[REFORGE] Not enough gold to reroll slot {slotIndex} of tower {towerId} (need {cost:F1}, have {gold:F1}).");
                return false;
            }

            int towerLevel = store.TowerLevel[towerId];
            int rolledAffixIndex = RollAffix(towerLevel);
            if (rolledAffixIndex < 0)
            {
                renderer?.Log($"[REFORGE] No eligible affix to roll for tower {towerId} (level {towerLevel}).");
                return false;
            }

            // Apply: charge gold, increment reforge count, assign new affix + reset stack to 1
            store.SetPlayerGold(playerId, gold - cost);
            store.IncrementTowerReforgeCount(towerId);
            store.SetTowerAffixId(towerId, slotIndex, rolledAffixIndex);
            store.SetTowerAffixStackCount(towerId, slotIndex, 1);

            int newCount = store.GetTowerReforgeCount(towerId);
            renderer?.Log($"[REFORGE] Tower {towerId} slot {slotIndex} rerolled → affix idx {rolledAffixIndex} (#{newCount}/{Config.MaxRerollsPerTower}, cost {cost:F1}g).");
            return true;
        }

        /// <summary>
        /// Reroll ALL unlocked slots in a single call. Charges gold per unlocked slot
        /// (each reroll increments the reforge count separately). Returns the number of
        /// slots successfully rerolled.
        /// Locked slots are silently skipped (no charge, no count).
        /// </summary>
        public int RerollAllUnlocked(int towerId)
        {
            if (!Config.Enabled) return 0;
            if (!ComponentStore.IsValidEntity(towerId)) return 0;
            int slotCount = store.GetTowerAffixSlotCount(towerId);
            int succeeded = 0;
            for (int s = 0; s < slotCount; s++)
            {
                if (store.IsTowerAffixSlotLocked(towerId, s)) continue;
                if (RerollAffix(towerId, s)) succeeded++;
            }
            return succeeded;
        }

        // ════════════════════════════════════════════════════════════════════
        //  Internals
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Sample a random affix index from GameConfig.TowerAffixes[] using
        /// RarityWeights and the tower-level gate. Returns -1 if no eligible affix.
        /// </summary>
        private int RollAffix(int towerLevel)
        {
            var pool = gameConfig?.TowerAffixes;
            if (pool == null || pool.Length == 0) return -1;

            // Step 1: pick a rarity tier (0..4) using weighted random
            int tier = RollRarityTier();
            if (tier < 0) tier = 0;

            // Step 2: try candidates in the chosen tier first, then progressively relax
            //         (tier-1, tier+1, tier-2, ...). Guarantees a non-empty result if any
            //         affix is available.
            int[] tierOrder = BuildTierOrder(tier);
            for (int attempt = 0; attempt < tierOrder.Length; attempt++)
            {
                int tryTier = tierOrder[attempt];
                int idx = SampleAffixInTier(pool, towerLevel, tryTier);
                if (idx >= 0) return idx;
            }
            return -1;
        }

        /// <summary>
        /// Roll a rarity tier index (0..N-1) using RarityWeights. Returns 0 on malformed config.
        /// </summary>
        private int RollRarityTier()
        {
            float[] w = Config.RarityWeights;
            if (w == null || w.Length == 0) return 0;
            float total = 0f;
            for (int i = 0; i < w.Length; i++)
            {
                float v = w[i];
                if (v < 0f) v = 0f;
                total += v;
            }
            if (total <= 0f) return 0;
            double r = store.Determinism.NextDouble() * total;
            float cum = 0f;
            for (int i = 0; i < w.Length; i++)
            {
                float v = w[i];
                if (v < 0f) v = 0f;
                cum += v;
                if (r < cum) return i;
            }
            return w.Length - 1;
        }

        /// <summary>
        /// Build a relaxed tier-search order: [tier, tier-1, tier+1, tier-2, tier+2, ...]
        /// within the [0, w.Length-1] range. Always returns at least one element.
        /// </summary>
        private int[] BuildTierOrder(int tier)
        {
            float[] w = Config.RarityWeights;
            int maxTier = (w != null && w.Length > 0) ? w.Length - 1 : 4;
            if (tier < 0) tier = 0;
            if (tier > maxTier) tier = maxTier;

            int[] order = new int[maxTier + 1];
            int n = 0;
            order[n++] = tier;
            for (int off = 1; off <= maxTier; off++)
            {
                if (tier - off >= 0) order[n++] = tier - off;
                if (tier + off <= maxTier) order[n++] = tier + off;
            }
            // Compact to n elements
            int[] compact = new int[n];
            Array.Copy(order, compact, n);
            return compact;
        }

        /// <summary>
        /// Sample a random affix index whose Rarity == tier and MinLevel &lt;= towerLevel.
        /// Returns -1 if no eligible affix in this tier.
        /// </summary>
        private int SampleAffixInTier(GameConfig.TowerAffixDef[] pool, int towerLevel, int tier)
        {
            if (towerLevel < 0) towerLevel = 0;
            // Reservoir sampling for uniform pick
            int count = 0;
            int picked = -1;
            for (int i = 0; i < pool.Length; i++)
            {
                var a = pool[i];
                if (a == null) continue;
                if (a.Rarity != tier) continue;
                if (a.MinLevel > towerLevel) continue;
                count++;
                // random pick with 1/count probability
                if (store.Determinism.Next(count) == 0) picked = i;
            }
            return picked;
        }
    }
}
