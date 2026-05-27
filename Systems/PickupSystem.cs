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
        public void SpawnPickup(int pickupType, float x, float y, int playerId, float value = 0f)
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
        }

        /// <summary>
        /// Called when an enemy is killed — spawns a pickup at the death location.
        /// </summary>
        private void OnEnemyKilledHandler(int enemyId, int playerId)
        {
            float x = store.PositionX[enemyId];
            float y = store.PositionY[enemyId];

            // GoldPile always drops
            SpawnPickup(0, x, y, playerId);

            // 15% chance for bonus drop
            if (_rng.NextDouble() < 0.15f)
            {
                int bonusType = _rng.Next(1, gameConfig.PickupDefs.Length); // 1-4
                SpawnPickup(bonusType, x, y, playerId);
            }
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

                for (int e = 0; e < ComponentStore.MAX_ENTITIES; e++)
                {
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
                    store.PlayerCurrentHealth[playerId] = currentHealth + healed;
                    renderer.Log($"[PICKUP] HealthPack collected: +{healed} HP");
                    break;
                }
                case 2: // ManaOrb
                {
                    float currentMana = store.PlayerMana[playerId];
                    float maxMana = store.PlayerMaxMana[playerId];
                    float restored = Math.Min(value, maxMana - currentMana);
                    store.PlayerMana[playerId] = currentMana + restored;
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