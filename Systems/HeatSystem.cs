#nullable enable
using System;
using BattleSystemECS.Core;
using BattleSystemECS.Components;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Tower Heat / Overheat system.
    /// 
    /// Towers generate heat with each shot. When heat reaches max capacity, the tower
    /// enters an overheat state where it gains attack speed but suffers a damage penalty
    /// and takes tick damage. Heat dissipates passively when not firing.
    /// 
    /// Two-phase model:
    ///   SetTurn:  cache heat config for each active tower
    ///   Update:   accumulate heat on fire, dissipate over time, check overheat transition
    /// 
    /// TowerAttackSystem reads the cached overheat flags directly in its hot path
    /// (zero additional per-attack overhead when not overheated).
    /// </summary>
    public class HeatSystem
    {
        private ComponentStore store;
        private int _turn = 0;

        // Cached heat config
        private float _overheatCooldownTime = 3f;  // seconds to cool after overheat clears
        private float _heatTickDamagePercent = 0.01f; // 1% of max HP per tick when overheated

        public HeatSystem(ComponentStore store)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public void SetTurn(int turn)
        {
            _turn = turn;
        }

        /// <summary>
        /// Accumulate heat for a tower when it fires a shot.
        /// Called by TowerAttackSystem after damage is queued.
        /// </summary>
        public void AccumulateHeat(int towerId)
        {
            if (!store.TowerActive[towerId]) return;
            if (!store.TowerCanOverheat[towerId]) return;
            if (store.TowerIsOverheated[towerId]) return;  // already overheated, no more accumulation

            float heatPerShot = store.TowerHeatPerShot[towerId];
            if (heatPerShot <= 0f) return;

            store.TowerHeat[towerId] = Math.Min(
                store.TowerMaxHeat[towerId],
                store.TowerHeat[towerId] + heatPerShot
            );
        }

        /// <summary>
        /// Called each frame to dissipate heat and manage overheat state.
        /// </summary>
        public void Update(float deltaTime)
        {
            var towerIds = store.ActiveTowerIds;

            for (int i = 0; i < towerIds.Count; i++)
            {
                int towerId = towerIds[i];
                if (!store.TowerActive[towerId]) continue;
                if (!store.TowerCanOverheat[towerId]) continue;

                float maxHeat = store.TowerMaxHeat[towerId];
                if (maxHeat <= 0f) continue;

                float currentHeat = store.TowerHeat[towerId];
                float cooldownRate = store.TowerHeatCooldownRate[towerId];
                bool isOverheated = store.TowerIsOverheated[towerId];

                if (isOverheated)
                {
                    // Overheat cooling: dissipate heat even faster during overheat cooldown
                    float overheatCoolRate = Math.Max(cooldownRate * 2f, maxHeat * 0.5f); // min 2x normal rate
                    currentHeat -= overheatCoolRate * deltaTime;

                    // Check if overheat has cleared
                    if (currentHeat <= 0f)
                    {
                        currentHeat = 0f;
                        store.TowerIsOverheated[towerId] = false;
                        store.TowerOverheatTimer[towerId] = _overheatCooldownTime; // brief lockout
                    }
                }
                else
                {
                    // Normal heat dissipation when not overheated
                    currentHeat -= cooldownRate * deltaTime;
                }

                // Clamp heat
                store.TowerHeat[towerId] = Math.Max(0f, currentHeat);

                // Check if we just crossed the overheat threshold (heat >= maxHeat)
                if (!isOverheated && currentHeat >= maxHeat)
                {
                    store.TowerIsOverheated[towerId] = true;
                }

                // Update overheat timer (counts down during overheat lockout)
                if (!isOverheated && store.TowerOverheatTimer[towerId] > 0f)
                {
                    store.TowerOverheatTimer[towerId] -= deltaTime;
                }
            }
        }

        /// <summary>
        /// Returns the attack speed multiplier for a tower accounting for overheat state.
        /// When overheated, towers get a bonus but at the cost of tick damage.
        /// </summary>
        public float GetOverheatAttackSpeedMultiplier(int towerId)
        {
            if (!store.TowerActive[towerId]) return 1f;
            if (!store.TowerIsOverheated[towerId]) return 1f;
            return store.TowerOverheatBonus[towerId];
        }

        /// <summary>
        /// Returns the damage multiplier for a tower accounting for overheat state.
        /// When overheated, towers suffer a penalty.
        /// </summary>
        public float GetOverheatDamageMultiplier(int towerId)
        {
            if (!store.TowerActive[towerId]) return 1f;
            if (!store.TowerIsOverheated[towerId]) return 1f;
            return 1f - store.TowerOverheatPenalty[towerId];
        }

        /// <summary>
        /// Returns true if the tower is currently overheated and unable to fire.
        /// </summary>
        public bool IsOverheated(int towerId)
        {
            return store.TowerActive[towerId] && store.TowerIsOverheated[towerId];
        }
    }
}