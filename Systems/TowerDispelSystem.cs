using System;
using System.Collections.Generic;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Tower Dispel System — handles enemy-cast dispel effects on towers.
    /// Enemies with `dispel_tower` ability release a purification wave that clears
    /// all tower aura/synergy buffs within radius and prevents new buffs for a duration.
    /// 
    /// Two-phase: enemies cast dispel in EnemyAbilitySystem (parallel collect),
    /// TowerDispelSystem resolves the dispel state and applies immunity (serial apply).
    /// Runs in Phase 6.2 after TowerSilenceSystem.
    /// </summary>
    public class TowerDispelSystem
    {
        private ComponentStore store;

        public TowerDispelSystem(ComponentStore store)
        {
            this.store = store;
        }

        /// <summary>
        /// Called once per turn from FrameScheduler.Tick() (Phase 6.2).
        /// Decrement dispel timers, clear expired dispels, manage immunity period.
        /// </summary>
        public void Update(float deltaTime)
        {
            var activeTowerIds = store.ActiveTowerIds;
            for (int i = 0; i < activeTowerIds.Count; i++)
            {
                int towerId = activeTowerIds[i];

                // Handle active dispel state
                if (store.TowerIsDispelled[towerId])
                {
                    // Decrement dispel duration (1 turn per frame)
                    store.TowerDispelTimer[towerId] -= 1f;
                    if (store.TowerDispelTimer[towerId] <= 0f)
                    {
                        // Dispel expired — transition to immunity period
                        store.TowerIsDispelled[towerId] = false;
                        store.TowerDispelTimer[towerId] = 0f;
                        // Set immunity duration (stored as DispelImmunityDuration in ability, reused here)
                        // We recover the immunity from the original ability — but since we only stored the timer,
                        // we use a fixed 2-turn immunity as a sensible default (per DispelEnemyDef spec)
                        store.TowerDispelImmunityTimer[towerId] = 2f; // 2 turns immunity
                    }
                }
                // Handle immunity period
                else if (store.TowerDispelImmunityTimer[towerId] > 0f)
                {
                    store.TowerDispelImmunityTimer[towerId] -= 1f;
                    if (store.TowerDispelImmunityTimer[towerId] <= 0f)
                    {
                        store.TowerDispelImmunityTimer[towerId] = 0f;
                    }
                }
            }
        }
    }
}