#nullable enable
using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Components;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Tower Overcharge / Overdrive system.
    /// 
    /// Allows players to temporarily boost individual towers (damage ×2, attack speed ×1.5, range ×1.2)
    /// at the cost of mana. Creates active decision-making: when to overcharge which tower.
    /// 
    /// Two-phase model:
    ///   SetTurn:  cache overcharge multiplier for each active tower
    ///   Update:   decrement overcharge duration and cooldown timers
    /// 
    /// TowerAttackSystem reads the cached overcharge flags directly in its hot path
    /// (zero额外的 per-attack overhead when not overcharged).
    /// </summary>
    public class TowerOverchargeSystem
    {
        private ComponentStore store;
        private GameConfig gameConfig;
        private int _turn = 0;

        public TowerOverchargeSystem(ComponentStore store, GameConfig gameConfig)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.gameConfig = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
        }

        public void SetTurn(int turn)
        {
            _turn = turn;
        }

        /// <summary>
        /// Try to activate overcharge on a tower. Fails silently if:
        ///   - tower does not exist or is not active
        ///   - tower type does not support overcharge
        ///   - tower is already overcharged
        ///   - tower is in cooldown
        ///   - player does not have enough mana
        /// </summary>
        public void TryActivateOvercharge(int towerId, int playerId)
        {
            if (!store.TowerActive[towerId]) return;
            if (!store.TowerCanOvercharge[towerId]) return;
            if (store.TowerIsOvercharged[towerId]) return;
            if (store.TowerOverchargeCooldown[towerId] > 0f) return;

            var cfg = gameConfig.TowerOvercharge;
            if (store.PlayerMana[playerId] < cfg.ManaCost) return;
            if (store.PlayerMana[playerId] < cfg.MinManaRequired) return;

            // Consume mana
            store.PlayerMana[playerId] -= cfg.ManaCost;

            // Activate overcharge
            store.TowerIsOvercharged[towerId] = true;
            store.TowerOverchargeDuration[towerId] = cfg.Duration;
            store.TowerOverchargeCooldown[towerId] = 0f; // cooldown starts after duration ends
        }

        public void Update(float deltaTime)
        {
            var activeTowerIds = store.ActiveTowerIds;
            var cfg = gameConfig.TowerOvercharge;

            for (int i = 0; i < activeTowerIds.Count; i++)
            {
                int towerId = activeTowerIds[i];

                // Skip towers that cannot overcharge or are not overcharged
                if (!store.TowerCanOvercharge[towerId]) continue;
                if (!store.TowerIsOvercharged[towerId])
                {
                    // Still decrement cooldown even when not active
                    if (store.TowerOverchargeCooldown[towerId] > 0f)
                    {
                        store.TowerOverchargeCooldown[towerId] -= deltaTime;
                        if (store.TowerOverchargeCooldown[towerId] < 0f)
                            store.TowerOverchargeCooldown[towerId] = 0f;
                    }
                    continue;
                }

                // Decrement overcharge duration
                store.TowerOverchargeDuration[towerId] -= deltaTime;

                if (store.TowerOverchargeDuration[towerId] <= 0f)
                {
                    // Overcharge expired — enter cooldown
                    store.TowerIsOvercharged[towerId] = false;
                    store.TowerOverchargeDuration[towerId] = 0f;
                    store.TowerOverchargeCooldown[towerId] = cfg.Cooldown;
                }
            }
        }
    }
}
