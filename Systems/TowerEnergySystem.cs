#nullable enable
using System;
using BattleSystemECS.Core;
using BattleSystemECS.Components;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Tower Energy / Mana Resource System.
    /// 
    /// Towers consume energy per shot. When energy is depleted, the tower cannot fire
    /// until it recharges (passive regen or proximity to an Energy Tower).
    /// 
    /// Two-phase model:
    ///   SetTurn: cache energy config for each active tower
    ///   Update: regenerate energy over time, apply energy drain from firing
    /// 
    /// TowerAttackSystem reads the energy check directly in its hot path
    /// (zero additional per-attack overhead when energy is sufficient).
    /// </summary>
    public class TowerEnergySystem
    {
        private ComponentStore store;
        private int _turn = 0;

        public TowerEnergySystem(ComponentStore store)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public void SetTurn(int turn)
        {
            _turn = turn;
        }

        /// <summary>
        /// Consume energy for a tower when it fires a shot.
        /// Called by TowerAttackSystem after damage is queued.
        /// Returns true if energy was sufficient and consumption occurred.
        /// </summary>
        public bool ConsumeEnergy(int towerId)
        {
            if (!store.TowerActive[towerId]) return false;
            
            float maxEnergy = store.TowerMaxEnergy[towerId];
            if (maxEnergy <= 0f) return true; // no energy system, always ok

            float energyCost = store.TowerEnergyPerShot[towerId];
            if (energyCost <= 0f) return true; // no cost, always ok

            float currentEnergy = store.TowerEnergy[towerId];
            if (currentEnergy < energyCost) return false; // not enough energy

            store.TowerEnergy[towerId] = currentEnergy - energyCost;
            return true;
        }

        /// <summary>
        /// Called each frame to regenerate energy for all towers.
        /// Energy towers also regenerate nearby towers' energy.
        /// </summary>
        public void Update(float deltaTime)
        {
            var towerIds = store.ActiveTowerIds;

            for (int i = 0; i < towerIds.Count; i++)
            {
                int towerId = towerIds[i];
                if (!store.TowerActive[towerId]) continue;

                float maxEnergy = store.TowerMaxEnergy[towerId];
                if (maxEnergy <= 0f) continue; // no energy system

                float regenRate = store.TowerEnergyRegen[towerId];
                if (regenRate > 0f)
                {
                    // Passive energy regeneration
                    store.TowerEnergy[towerId] = Math.Min(
                        maxEnergy,
                        store.TowerEnergy[towerId] + regenRate * deltaTime
                    );
                }

                // Energy tower: regenerate nearby towers' energy
                if (store.TowerIsEnergyTower[towerId])
                {
                    float regenRadius = store.TowerEnergyRegenRadius[towerId];
                    if (regenRadius <= 0f) continue;

                    float tx = store.PositionX[towerId];
                    float ty = store.PositionY[towerId];

                    // Scan nearby towers (O(n) — acceptable for energy towers which are few)
                    for (int j = 0; j < towerIds.Count; j++)
                    {
                        int nearbyId = towerIds[j];
                        if (nearbyId == towerId) continue;
                        if (!store.TowerActive[nearbyId]) continue;

                        float nearbyMaxEnergy = store.TowerMaxEnergy[nearbyId];
                        if (nearbyMaxEnergy <= 0f) continue;

                        float nx = store.PositionX[nearbyId];
                        float ny = store.PositionY[nearbyId];
                        float dx = tx - nx;
                        float dy = ty - ny;
                        float distSq = dx * dx + dy * dy;
                        float radiusSq = regenRadius * regenRadius;

                        if (distSq <= radiusSq)
                        {
                            // Regenerate nearby tower (use this tower's regen rate as the boost rate)
                            float boostRate = regenRate;
                            if (boostRate > 0f)
                            {
                                store.TowerEnergy[nearbyId] = Math.Min(
                                    nearbyMaxEnergy,
                                    store.TowerEnergy[nearbyId] + boostRate * deltaTime
                                );
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Returns true if the tower has sufficient energy to fire.
        /// </summary>
        public bool HasEnergy(int towerId)
        {
            if (!store.TowerActive[towerId]) return false;
            
            float maxEnergy = store.TowerMaxEnergy[towerId];
            if (maxEnergy <= 0f) return true; // no energy system

            float energyCost = store.TowerEnergyPerShot[towerId];
            if (energyCost <= 0f) return true; // no cost

            return store.TowerEnergy[towerId] >= energyCost;
        }

        /// <summary>
        /// Returns the current energy fraction (0.0 to 1.0+) for UI display.
        /// </summary>
        public float GetEnergyFraction(int towerId)
        {
            if (!store.TowerActive[towerId]) return 0f;
            
            float maxEnergy = store.TowerMaxEnergy[towerId];
            if (maxEnergy <= 0f) return 1f; // no energy system = full

            return store.TowerEnergy[towerId] / maxEnergy;
        }
    }
}