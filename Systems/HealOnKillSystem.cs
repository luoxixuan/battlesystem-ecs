using System;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Kill-Triggered Player Sustain System — RPG "X 杀后回复 Y 血/蓝" mechanic.
    ///
    /// Subscribes to ComponentStore.OnTowerKill (per-tower heal/mana on kill).
    /// This event fires serially inside ResolveEnemiesKilledThisFrame, so direct
    /// SOA writes to PlayerCurrentHealth / PlayerMana are safe (no parallel-write race).
    ///
    /// Behavior:
    /// - For each (enemyId, playerId, towerId) kill event, inspect the tower's
    ///   HealOnKillAmount and ManaOnKillAmount fields. If non-zero, restore the
    ///   owning player's resources. HP is capped at PlayerMaxHealth; mana is
    ///   capped at PlayerMaxMana via AddPlayerMana.
    /// - All fields default to 0 (disabled) — fully backward compatible with
    ///   existing tower configs and towers in-flight.
    ///
    /// Symmetric with EnemyAffixSystem.OnVampiricKill (which heals the enemy on kill).
    /// This is the player-side counterpart: keep the player topped up during long waves.
    /// </summary>
    public class HealOnKillSystem
    {
        private readonly ComponentStore store;
        // Idempotency guard against WireDependencies re-init / test reset paths
        // stacking duplicate handlers.
        private bool _subscribed;

        public HealOnKillSystem(ComponentStore store)
        {
            this.store = store;
        }

        /// <summary>
        /// Subscribe to OnTowerKill. Called once by SystemRegistry.WireDependencies().
        /// </summary>
        public void SubscribeToEvents()
        {
            if (_subscribed) return;
            _subscribed = true;
            store.OnTowerKill += HandleTowerKill;
        }

        /// <summary>
        /// OnTowerKill handler: restore HP / mana to the owning player based on the
        /// tower's HealOnKillAmount / ManaOnKillAmount fields. No-op if both are 0.
        /// </summary>
        private void HandleTowerKill(int enemyId, int playerId, int towerId)
        {
            if (!ComponentStore.IsValidEntity(playerId)) return;
            if (towerId < 0 || towerId >= ComponentStore.MAX_ENTITIES) return;
            if (!store.TowerActive[towerId]) return;

            float healAmount = store.TowerHealOnKillAmount[towerId];
            float manaAmount = store.TowerManaOnKillAmount[towerId];

            // Fast path: tower has no sustain configured — skip the whole handler.
            if (healAmount <= 0f && manaAmount <= 0f) return;

            // HP heal (clamped to PlayerMaxHealth by SetPlayerCurrentHealth callers / Wisp path).
            if (healAmount > 0f)
            {
                float currentHp = store.PlayerCurrentHealth[playerId];
                float maxHp = store.PlayerMaxHealth[playerId];
                if (maxHp > 0f)
                {
                    float newHp = Math.Min(currentHp + healAmount, maxHp);
                    store.SetPlayerCurrentHealth(playerId, newHp);
                }
            }

            // Mana restore (clamped to PlayerMaxMana by AddPlayerMana).
            if (manaAmount > 0f)
            {
                store.AddPlayerMana(playerId, manaAmount);
            }
        }
    }
}
