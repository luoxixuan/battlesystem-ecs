using System;
using System.Collections.Generic;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Tower Silence System — handles enemy-cast silence effects on towers.
    /// Enemies with `silence_tower` ability apply silence to all towers in range,
    /// preventing them from attacking for the duration.
    /// 
    /// Two-phase: enemies cast silence in EnemyAbilitySystem (parallel collect),
    /// TowerSilenceSystem resolves the silence state and applies it to towers (serial apply).
    /// Runs in Phase 6.2 after AuraTower, before Projectile/EnemyProjectile.
    /// </summary>
    public class TowerSilenceSystem
    {
        private ComponentStore store;

        public TowerSilenceSystem(ComponentStore store)
        {
            this.store = store;
        }

        /// <summary>
        /// Called once per turn from FrameScheduler.Tick() (Phase 6.2).
        /// Decrement silence timers and clear expired silences.
        /// </summary>
        public void Update(float deltaTime)
        {
            var activeTowerIds = store.ActiveTowerIds;
            for (int i = 0; i < activeTowerIds.Count; i++)
            {
                int towerId = activeTowerIds[i];
                if (store.TowerIsSilenced[towerId])
                {
                    // Decrement timer (1 turn per frame, so subtract 1)
                    store.TowerSilenceTimer[towerId] -= 1f;
                    if (store.TowerSilenceTimer[towerId] <= 0f)
                    {
                        // Silence expired — clear state
                        store.TowerIsSilenced[towerId] = false;
                        store.TowerSilenceTimer[towerId] = 0f;
                        store.TowerSilenceSourceId[towerId] = -1;
                    }
                }
            }
        }

        /// <summary>
        /// Apply silence to a tower (called from EnemyAbilitySystem when enemy uses silence_tower ability).
        /// Silences tower for the specified duration (turns). Overwrites existing silence.
        /// </summary>
        /// <param name="towerId">Target tower ID</param>
        /// <param name="duration">Silence duration in turns</param>
        /// <param name="sourceEnemyId">Enemy that applied the silence</param>
        public void ApplySilence(int towerId, float duration, int sourceEnemyId)
        {
            if (towerId < 0 || towerId >= ComponentStore.MAX_ENTITIES) return;
            store.TowerIsSilenced[towerId] = true;
            store.TowerSilenceTimer[towerId] = duration;
            store.TowerSilenceSourceId[towerId] = sourceEnemyId;
        }
    }
}