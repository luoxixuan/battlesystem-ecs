using System;
using System.Collections.Generic;
using BattleSystemECS.Config;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Shop Reroll System — Slay-the-Spire / Monster Train style offer pool refresh.
    ///
    /// Design:
    /// - During BuildPhase, the player has 3 offer slots (configurable) showing towers/skills.
    /// - The player can spend gold (CostCurve) to re-roll the 3 slots, picking from the
    ///   available tower configs and skill configs with a rarity-weighted random pick.
    /// - Cost scales by CostCurve (e.g. 5g → 10g → 20g) and is capped at MaxRerollsPerPhase.
    /// - Pity timers guarantee a Rare appears after PityRareThreshold offers without one,
    ///   and an Epic after PityEpicThreshold offers. Pity counters reset each new BuildPhase.
    ///
    /// Storage (SOA, all in ComponentStore.Player fields):
    /// - PlayerShopRerollCount: rerolls performed this phase
    /// - PlayerShopOfferTypeId: typeId of each offer (1D-flat, indexed by player*8 + slot)
    /// - PlayerShopOfferIsTower: 0=skill, 1=tower
    /// - PlayerShopPityRare / PityEpic: consecutive offer counter since last Rare/Epic
    ///
    /// Hot-path impact: zero (BuildPhase only, no per-frame work).
    /// </summary>
    public class ShopRerollSystem
    {
        private readonly ComponentStore store;
        private readonly IRenderer renderer;
        private readonly GameConfig gameConfig;
        private readonly int playerId;
        private readonly Random rng; // BuildPhase 商店，非模拟；固定种子私有流，不进 digest。

        public ShopRerollSystem(ComponentStore store, IRenderer renderer, GameConfig gameConfig, int playerId, int seed = 12345)
        {
            this.store = store;
            this.renderer = renderer;
            this.gameConfig = gameConfig;
            this.playerId = playerId;
            this.rng = new Random(seed);
            this.cfgCached = gameConfig.ShopReroll; // may be null — Config returns a safe default
        }

        // Cached at construction time — avoids re-allocating a default ShopRerollConfig
        // on every Config access AND ensures the fallback default has Enabled=false
        // (matching the JSON loader behavior) so the system is safely inert when
        // gameConfig.ShopReroll is unset.
        private readonly ShopRerollConfig cfgCached;
        private ShopRerollConfig Config => cfgCached ?? new ShopRerollConfig { Enabled = false };

        /// <summary>
        /// Per-BuildPhase tick — currently a no-op (the system is event-driven
        /// via OnEnterBuildPhase and explicit RerollOffers() calls). Kept for
        /// BuildGroup pipeline symmetry with InterestSystem.Update().
        /// </summary>
        public void Update()
        {
            // No per-frame work needed. State is mutated only at phase entry
            // and on explicit RerollOffers() calls.
        }

        /// <summary>
        /// Initialize the offer pool at the start of a new BuildPhase.
        /// Resets reroll count and pity counters, then rolls initial offers.
        /// </summary>
        public void OnEnterBuildPhase()
        {
            if (!Config.Enabled) return;
            store.PlayerShopRerollCount[playerId] = 0;
            store.PlayerShopPityRare[playerId] = 0;
            store.PlayerShopPityEpic[playerId] = 0;
            ClearOffers();
            RollOffers(Config.OfferSlotCount);
            renderer?.Log($"[SHOP] Initial offers rolled: {Config.OfferSlotCount} slots.");
        }

        /// <summary>
        /// Re-roll all offer slots. Charges the player gold according to CostCurve and
        /// respects MaxRerollsPerPhase. Returns true if reroll succeeded.
        /// </summary>
        public bool RerollOffers()
        {
            if (!Config.Enabled) return false;
            int rerollCount = store.PlayerShopRerollCount[playerId];
            if (rerollCount >= Config.MaxRerollsPerPhase)
            {
                renderer?.Log($"[SHOP] Reroll cap reached ({Config.MaxRerollsPerPhase}).");
                return false;
            }

            float cost = GetRerollCost(rerollCount);
            float currentGold = store.GetPlayerGold(playerId);
            if (currentGold < cost)
            {
                renderer?.Log($"[SHOP] Not enough gold to reroll (need {cost:F1}, have {currentGold:F1}).");
                return false;
            }

            store.SetPlayerGold(playerId, currentGold - cost);
            store.PlayerShopRerollCount[playerId] = rerollCount + 1;

            ClearOffers();
            RollOffers(Config.OfferSlotCount);

            int slot = rerollCount + 1;
            renderer?.Log($"[SHOP] Reroll #{slot}/{Config.MaxRerollsPerPhase} complete (cost {cost:F1}g).");
            return true;
        }

        /// <summary>
        /// Compute the cost of the (idx+1)-th reroll this phase. Uses the last value of
        /// CostCurve if idx exceeds the configured curve length.
        /// </summary>
        public float GetRerollCost(int idx)
        {
            if (idx < 0) idx = 0;
            float[] curve = Config.CostCurve;
            if (curve == null || curve.Length == 0) return 5f * (idx + 1);
            if (idx >= curve.Length) idx = curve.Length - 1;
            return curve[idx];
        }

        /// <summary>
        /// Read the current offer at the given slot index (0-based). Returns a struct
        /// describing the offer so the UI / placement system can consume it.
        /// </summary>
        public ShopOffer GetOffer(int slotIdx)
        {
            int baseIdx = playerId * ComponentStore.MAX_SHOP_OFFER_SLOTS;
            int maxSlot = Math.Min(Config.OfferSlotCount, ComponentStore.MAX_SHOP_OFFER_SLOTS);
            if (slotIdx < 0 || slotIdx >= maxSlot)
                return new ShopOffer { IsValid = false };

            int typeId = store.PlayerShopOfferTypeId[baseIdx + slotIdx];
            if (typeId <= 0)
                return new ShopOffer { IsValid = false };

            bool isTower = store.PlayerShopOfferIsTower[baseIdx + slotIdx] != 0;
            return new ShopOffer
            {
                IsValid = true,
                IsTower = isTower,
                TypeId = typeId,
                RarityTier = InferRarityTier(typeId, isTower)
            };
        }

        /// <summary>
        /// Number of rerolls remaining this phase (read-only convenience).
        /// </summary>
        public int GetRemainingRerolls()
        {
            int used = store.PlayerShopRerollCount[playerId];
            int max = Config.MaxRerollsPerPhase;
            int rem = max - used;
            return rem < 0 ? 0 : rem;
        }

        // ════════════════════════════════════════════════════════════════════
        //  Internals
        // ════════════════════════════════════════════════════════════════════

        private void ClearOffers()
        {
            int baseIdx = playerId * ComponentStore.MAX_SHOP_OFFER_SLOTS;
            int slots = Math.Min(Config.OfferSlotCount, ComponentStore.MAX_SHOP_OFFER_SLOTS);
            for (int i = 0; i < slots; i++)
            {
                store.PlayerShopOfferTypeId[baseIdx + i] = 0;
                store.PlayerShopOfferIsTower[baseIdx + i] = 0;
            }
        }

        private void RollOffers(int count)
        {
            int baseIdx = playerId * ComponentStore.MAX_SHOP_OFFER_SLOTS;
            int slots = Math.Min(count, ComponentStore.MAX_SHOP_OFFER_SLOTS);
            for (int i = 0; i < slots; i++)
            {
                int rarityTier = RollRarity();
                PickOffer(rarityTier, out bool isTower, out int typeId);
                store.PlayerShopOfferTypeId[baseIdx + i] = typeId;
                store.PlayerShopOfferIsTower[baseIdx + i] = isTower ? 1 : 0;
            }
        }

        /// <summary>
        /// Roll a rarity tier (0=Common, 1=Rare, 2=Epic) using RarityWeights and
        /// pity timers. Pity forces Rare/Epic if counters exceed thresholds.
        /// </summary>
        private int RollRarity()
        {
            if (store.PlayerShopPityEpic[playerId] >= Config.PityEpicThreshold) return 2;
            if (store.PlayerShopPityRare[playerId] >= Config.PityRareThreshold) return 1;

            float[] w = Config.RarityWeights;
            if (w == null || w.Length < 3) w = new float[] { 70f, 25f, 5f };
            float total = w[0] + w[1] + w[2];
            if (total <= 0f) return 0;

            double r = rng.NextDouble() * total;
            if (r < w[0]) return 0;
            if (r < w[0] + w[1]) return 1;
            return 2;
        }

        private void PickOffer(int rarityTier, out bool isTower, out int typeId)
        {
            isTower = false;
            typeId = 0;
            int towerCount = gameConfig.TowerTypes?.Count ?? 0;
            int skillCount = gameConfig.Skills?.Count ?? 0;

            // Choose category with available candidates only — never 0.
            if (towerCount == 0 && skillCount == 0) return;
            if (skillCount == 0) isTower = true;
            else if (towerCount == 0) isTower = false;
            else isTower = rng.NextDouble() < 0.6; // 60% towers, 40% skills

            if (isTower && towerCount > 0)
            {
                var towers = gameConfig.TowerTypes;
                int idx = PickByCost<TowerConfig>(towers, t => t.Cost, rarityTier);
                if (idx >= 0 && idx < towers.Count)
                {
                    typeId = (int)towers[idx].Type;
                }
            }
            else if (!isTower && skillCount > 0)
            {
                var skills = gameConfig.Skills;
                int idx = PickByCost<SkillConfig>(skills, s => s.ManaCost, rarityTier);
                if (idx >= 0 && idx < skills.Count)
                {
                    typeId = idx + 1; // SkillId is 1-based
                }
            }

            // Update pity counters
            if (rarityTier >= 1) store.PlayerShopPityRare[playerId] = 0;
            else store.PlayerShopPityRare[playerId]++;
            if (rarityTier >= 2) store.PlayerShopPityEpic[playerId] = 0;
            else store.PlayerShopPityEpic[playerId]++;
        }

        /// <summary>
        /// Sample a random index in [0, count) preferring candidates whose cost falls
        /// within the rarity tier's bucket. Common (0) picks cheaper items, Rare (1)
        /// mid-priced, Epic (2) most expensive. Falls back to uniform if the candidate
        /// collection is empty for a particular bucket.
        /// </summary>
        private int PickByCost<T>(List<T> items, Func<T, float> costAccessor, int rarityTier)
        {
            if (items == null || items.Count == 0) return -1;
            // Tier-bucketed sampling: 0=Common (low cost), 1=Rare (mid), 2=Epic (high).
            // Compute thresholds from observed cost distribution so the buckets auto-fit
            // the configured tower/skill pool without hardcoded magic numbers.
            float minCost = float.MaxValue, maxCost = float.MinValue;
            for (int i = 0; i < items.Count; i++)
            {
                float c = costAccessor(items[i]);
                if (c < minCost) minCost = c;
                if (c > maxCost) maxCost = c;
            }
            float lo, hi;
            if (maxCost <= minCost) { lo = minCost; hi = maxCost; }
            else
            {
                float third = (maxCost - minCost) / 3f;
                switch (rarityTier)
                {
                    case 2: lo = minCost + 2f * third; hi = maxCost; break;
                    case 1: lo = minCost + third;        hi = minCost + 2f * third; break;
                    default: lo = minCost;               hi = minCost + third; break;
                }
            }
            // Try in-tier candidates first.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int idx = rng.Next(items.Count);
                float c = costAccessor(items[idx]);
                bool inRange = (attempt == 0) ? (c >= lo && c <= hi)
                            : (attempt == 1) ? (c <= hi)
                                             : true; // final fallback: any
                if (inRange) return idx;
            }
            return rng.Next(items.Count);
        }

        private int InferRarityTier(int typeId, bool isTower)
        {
            if (typeId <= 0) return 0;
            if (isTower)
            {
                var towers = gameConfig.TowerTypes;
                if (towers == null) return 0;
                foreach (var t in towers)
                {
                    if ((int)t.Type == typeId)
                    {
                        // Use cost thresholds to infer tier
                        if (t.Cost >= 200f) return 2;
                        if (t.Cost >= 120f) return 1;
                        return 0;
                    }
                }
                return 0;
            }
            else
            {
                var skills = gameConfig.Skills;
                if (skills == null || typeId - 1 < 0 || typeId - 1 >= skills.Count) return 0;
                var s = skills[typeId - 1];
                if (s.ManaCost >= 50f) return 2;
                if (s.ManaCost >= 25f) return 1;
                return 0;
            }
        }
    }

    /// <summary>
    /// Snapshot of a single shop offer slot — produced by ShopRerollSystem.GetOffer().
    /// </summary>
    public struct ShopOffer
    {
        public bool IsValid;
        public bool IsTower;   // true=tower, false=skill
        public int TypeId;     // TowerType enum int, or skillId (1-based)
        public int RarityTier; // 0=Common, 1=Rare, 2=Epic
    }
}
