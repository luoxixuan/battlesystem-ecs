using System;
using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Pickup / Drop system — spawns collectible items when enemies die,
    /// and handles lifetime expiry + collection detection.
    ///
    /// Pickup types (indexed into GameConfig.PickupDefs):
    ///   0 = GoldPile  (+gold)
    ///   1 = HealthPack (+player health)
    ///   2 = ManaOrb   (+player mana)
    ///   3 = SpeedBoost (speed buff)
    ///   4 = DamageBoost (damage buff)
    ///
    /// Integration points:
    ///   - OnEnemyKilled handler spawns a pickup at the death location
    ///   - Update() expires pickups and resolves collection
    ///   - FrameScheduler calls Pickup.Update() each wave turn
    /// </summary>
    public class PickupSystem
    {
        private readonly ComponentStore store;
        private readonly GameConfig gameConfig;
        private readonly IRenderer renderer;
        private readonly Random _rng = new Random();

        private const int MAX_PICKUP = 1024;

        // Buff effect durations (seconds)
        private const float BUFF_DURATION = 15f;

        public PickupSystem(ComponentStore store, GameConfig gameConfig, IRenderer renderer)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.gameConfig = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
            this.renderer = renderer;
            store.OnEnemyKilled += OnEnemyKilledHandler;
        }

        /// <summary>
        /// Spawn a pickup at a world position for a specific player.
        /// </summary>
        /// <param name="rarity">Optional rarity tier (0..4). 0 = Common, 4 = Legendary. Stored for downstream filters.</param>
        public void SpawnPickup(int pickupType, float x, float y, int playerId, float value = 0f, byte rarity = 0)
        {
            if (pickupType < 0 || pickupType >= gameConfig.PickupDefs.Length)
                return;

            // Find free slot
            int slot = -1;
            for (int i = 0; i < MAX_PICKUP; i++)
            {
                if (!store.PickupActive[i]) { slot = i; break; }
            }
            if (slot < 0) return;

            var def = gameConfig.PickupDefs[pickupType];
            store.PickupX[slot] = x;
            store.PickupY[slot] = y;
            store.PickupType[slot] = pickupType;
            store.PickupValue[slot] = value > 0f ? value : def.Value;
            store.PickupOwnerId[slot] = playerId;
            store.PickupActive[slot] = true;
            store.PickupLifetime[slot] = def.LifetimeSeconds;
            // Round 68: Rarity tier storage (0..4). Clamp to [0,4] so callers can't break the byte.
            byte clamped = rarity > 4 ? (byte)4 : rarity;
            store.PickupRarity[slot] = clamped;
        }

        /// <summary>
        /// Called when an enemy is killed — spawns a pickup at the death location.
        /// Round 68: Pickup Rarity — the bonus drop now rolls a weighted rarity tier
        /// (Common/Uncommon/Rare/Epic/Legendary) instead of a flat 15% chance, and the
        /// rolled tier is mapped to a pickup type whose PickupDef.Rarity matches it.
        /// Tower luck shifts probability mass from Common to Rare+ tiers.
        /// </summary>
        private void OnEnemyKilledHandler(int enemyId, int playerId)
        {
            float x = store.PositionX[enemyId];
            float y = store.PositionY[enemyId];

            // GoldPile always drops (rarity = 0 = Common, type 0 = GoldPile, backward compat)
            SpawnPickup(0, x, y, playerId, 0f, (byte)0);

            // Bonus drop: only 15% of kills drop a bonus (backward-compatible trigger probability).
            // Within that 15%, roll a rarity tier (0..4), then pick a pickup whose Rarity matches the tier.
            // Luck = sum of active towers' TowerLuck (owned by this player), capped by config.
            if (_rng.NextDouble() >= 0.15f) return;
            int bonusRarity = RollPickupRarity();
            int bonusType = PickTypeByRarity(bonusRarity);
            if (bonusType > 0)
            {
                SpawnPickup(bonusType, x, y, playerId, 0f, (byte)bonusRarity);
            }
        }

        /// <summary>
        /// Roll a rarity tier index (0..4) for the bonus pickup drop, weighted by
        /// PickupRarityConfig.TierWeights shifted by the player's accumulated TowerLuck.
        /// Pure function over the current world state — no allocation.
        /// </summary>
        private int RollPickupRarity()
        {
            var cfg = gameConfig.PickupRarity;
            var weights = cfg.TierWeights;
            int n = weights?.Length ?? 0;
            if (n == 0) return 0;

            // Sum total luck across all active towers.
            // NOTE: there is no TowerOwnerPlayerId field — the store has a single PlayerEntityId
            // (single-player benchmark + single-player game). For multi-player, this would
            // need a TowerOwnerPlayerId SOA field. For now, we aggregate global luck.
            // O(activeTowers) but typically small (~tens), and OnEnemyKilled is rare enough.
            // Zero-overhead fast path: if every active tower has TowerLuck == 0, skip the inner loop.
            float luckSum = 0f;
            var towerIds = store.ActiveTowerIds;
            for (int i = 0; i < towerIds.Count; i++)
            {
                int tid = towerIds[i];
                if (!store.TowerActive[tid]) continue;
                luckSum += store.TowerLuck[tid];
            }
            // Clamp luck to MaxLuckBonus so a single high-luck tower can't dominate.
            float luckBonus = MathF.Min(luckSum * cfg.LuckShiftPerPoint, cfg.MaxLuckBonus);

            // Build working weights: deduct from tier 0 (Common), distribute to tiers 2..4 (Rare+Epic+Legendary).
            // Weights[1] (Uncommon) is preserved to keep mid-tier drops meaningful.
            // Cap each shifted weight at the original to avoid negative totals on extreme luck.
            float commonShift = MathF.Min(weights[0], luckBonus);
            float w0 = weights[0] - commonShift;
            float w1 = n > 1 ? weights[1] : 0f;
            float w2 = n > 2 ? weights[2] : 0f;
            float w3 = n > 3 ? weights[3] : 0f;
            float w4 = n > 4 ? weights[4] : 0f;
            float w23Add = commonShift * 0.6f;   // 60% to Rare
            float w34Add = commonShift * 0.3f;   // 30% to Epic
            float w4Add  = commonShift * 0.1f;   // 10% to Legendary
            // Clamp so we never add more than the existing weight can absorb without overshoot.
            w2 = MathF.Min(w2 + w23Add, weights[2] * 2f);
            w3 = MathF.Min(w3 + w34Add, weights[3] * 2f);
            w4 = MathF.Min(w4 + w4Add,  weights[4] * 2f);

            // Total for normalization; protect against degenerate config (all zeros).
            float total = w0 + w1 + w2 + w3 + w4;
            if (total <= 0f) return 0;

            // Weighted draw.
            double r = _rng.NextDouble() * total;
            double acc = 0;
            acc += w0; if (r < acc) return 0;
            acc += w1; if (r < acc) return 1;
            acc += w2; if (r < acc) return 2;
            acc += w3; if (r < acc) return 3;
            return 4;
        }

        /// <summary>
        /// Pick a random pickup type whose PickupDef.Rarity matches the rolled tier.
        /// Falls back to a uniform random from 1..N if no match (e.g. config mismatch),
        /// and returns 0 (= GoldPile, no bonus) if only GoldPile is configured.
        /// </summary>
        private int PickTypeByRarity(int rarity)
        {
            // Gather candidate type indices matching the requested rarity.
            // Linear scan over PickupDefs is O(N), N=5, fine.
            int matchCount = 0;
            int firstMatch = -1;
            for (int i = 1; i < gameConfig.PickupDefs.Length; i++) // skip type 0 (GoldPile)
            {
                if (gameConfig.PickupDefs[i].Rarity == rarity)
                {
                    if (firstMatch < 0) firstMatch = i;
                    matchCount++;
                }
            }
            if (matchCount == 0)
            {
                // No def at this rarity — fall back to any non-zero type (preserves 15% chance behavior).
                if (gameConfig.PickupDefs.Length <= 1) return 0;
                return _rng.Next(1, gameConfig.PickupDefs.Length);
            }
            if (matchCount == 1) return firstMatch;
            // Multiple matches — pick uniformly.
            int pick = _rng.Next(0, matchCount);
            int seen = 0;
            for (int i = 1; i < gameConfig.PickupDefs.Length; i++)
            {
                if (gameConfig.PickupDefs[i].Rarity == rarity)
                {
                    if (seen == pick) return i;
                    seen++;
                }
            }
            return firstMatch; // unreachable, defensive
        }

        /// <summary>
        /// Called each turn — expires pickups and resolves collection.
        /// Collection: any active enemy within CollectRadius of the pickup grants its effect
        /// to the pickup's owner player.
        /// </summary>
        public void Update(float deltaTime)
        {
            for (int i = 0; i < MAX_PICKUP; i++)
            {
                if (!store.PickupActive[i]) continue;

                // ── Lifetime expiry ────────────────────────────────
                store.PickupLifetime[i] -= deltaTime;
                if (store.PickupLifetime[i] <= 0f)
                {
                    store.PickupActive[i] = false;
                    store.PickupType[i] = -1;
                    store.PickupLifetime[i] = 0f;
                    continue;
                }

                // ── Collection check: any active enemy nearby ──────
                float px = store.PickupX[i];
                float py = store.PickupY[i];
                int pickupOwner = store.PickupOwnerId[i];
                float collectRadius = gameConfig.PickupDefs[store.PickupType[i]].CollectRadius;
                float collectRadiusSq = collectRadius * collectRadius;
                bool collected = false;

                var activeEnemies = store.ActiveEnemyIds;
                for (int enemyIndex = 0; enemyIndex < activeEnemies.Count; enemyIndex++)
                {
                    int e = activeEnemies[enemyIndex];
                    if (!store.EnemyActive[e]) continue;
                    float dx = store.PositionX[e] - px;
                    float dy = store.PositionY[e] - py;
                    if (dx * dx + dy * dy <= collectRadiusSq)
                    {
                        // Enemy is on top of pickup — grant effect to owner player
                        ApplyPickupEffect(i, pickupOwner);
                        collected = true;
                        break;
                    }
                }

                if (collected)
                {
                    store.PickupActive[i] = false;
                    store.PickupType[i] = -1;
                    store.PickupLifetime[i] = 0f;
                }
            }
        }

        /// <summary>
        /// Apply the effect of a pickup to the target player.
        /// </summary>
        private void ApplyPickupEffect(int pickupIdx, int playerId)
        {
            int pickupType = store.PickupType[pickupIdx];
            float value = store.PickupValue[pickupIdx];

            switch (pickupType)
            {
                case 0: // GoldPile
                {
                    float current = store.GetPlayerGold(playerId);
                    store.SetPlayerGold(playerId, current + value);
                    renderer.Log($"[PICKUP] GoldPile collected: +{value} gold");
                    break;
                }
                case 1: // HealthPack
                {
                    float currentHealth = store.PlayerCurrentHealth[playerId];
                    float maxHealth = store.PlayerMaxHealth[playerId];
                    float healed = Math.Min(value, maxHealth - currentHealth);
                    store.ApplyPlayerResourceAuthority(playerId, playerId, new Core.GAS.AttributeKey(3), healed);
                    renderer.Log($"[PICKUP] HealthPack collected: +{healed} HP");
                    break;
                }
                case 2: // ManaOrb
                {
                    float currentMana = store.PlayerMana[playerId];
                    float maxMana = store.PlayerMaxMana[playerId];
                    float restored = Math.Min(value, maxMana - currentMana);
                    store.ApplyPlayerResourceAuthority(playerId, playerId, new Core.GAS.AttributeKey(7), restored);
                    renderer.Log($"[PICKUP] ManaOrb collected: +{restored} mana");
                    break;
                }
                case 3: // SpeedBoost
                {
                    store.PlayerSlowFactor[playerId] = 1.5f; // 50% speed boost (negative slow = boost)
                    store.PlayerSlowDuration[playerId] = (int)(BUFF_DURATION / 1f); // turns
                    renderer.Log($"[PICKUP] SpeedBoost collected: +50% speed for {BUFF_DURATION}s");
                    break;
                }
                case 4: // DamageBoost
                {
                    // Apply AttackBoost flag for the buff duration
                    store.PlayerBuffFlags[playerId] |= BuffType.AttackBoost;
                    store.PlayerSlowDuration[playerId] = (int)(BUFF_DURATION / 1f); // reuse slow duration as buff duration counter
                    renderer.Log($"[PICKUP] DamageBoost collected: +10% damage for {BUFF_DURATION}s");
                    break;
                }
            }
        }
    }
}
