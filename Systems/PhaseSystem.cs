using System;
using System.Collections.Generic;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Phase System — ghost/phase-through enemies that ignore tower attacks and obstacles.
    /// 
    /// Mechanics:
    /// - Phased enemies are immune to normal tower attacks (TowerIsPhased = true)
    /// - Phase state has a duration (countdown timer) — when it expires, enemy becomes solid again
    /// - Anti-phase towers (magic towers) can still damage phased enemies via TowerIsAntiPhase flag
    /// - Phase has a cooldown — once expired, enemy must wait before phasing again
    /// 
    /// Usage:
    /// - PhaseSystem.Update() runs each frame, decrements phase timers
    /// - TowerAttackSystem should skip enemies where EnemyIsPhased[enemyId] = true
    ///   (unless the tower has TowerIsAntiPhase[attackerTowerId] = true)
    /// - WaveSpawningSystem initializes phase fields on enemy spawn
    /// </summary>
    public class PhaseSystem
    {
        private readonly ComponentStore store;
        private readonly int playerId;

        // Cached active enemy IDs per turn (avoid repeated GetActiveEnemyIds() calls)
        private List<int> _cachedActiveEnemyIds;
        private int _cachedCount;
        private bool _turnCached;

        public PhaseSystem(ComponentStore store, int playerId)
        {
            this.store = store;
            this.playerId = playerId;
        }

        /// <summary>
        /// Called once per turn at the start of AI group to cache active enemy list.
        /// </summary>
        public void SetTurn(int currentTurn)
        {
            _cachedActiveEnemyIds = store.GetCachedActiveEnemyIds();
            _cachedCount = _cachedActiveEnemyIds.Count;
            _turnCached = true;
        }

        /// <summary>
        /// Updates all phase timers — decrements timers each frame, ends phase when timer reaches 0.
        /// Also decrements phase cooldowns for enemies that have re-phasing ability.
        /// </summary>
        public void Update(float deltaTime)
        {
            if (!_turnCached || _cachedCount == 0) return;

            for (int i = 0; i < _cachedCount; i++)
            {
                int enemyId = _cachedActiveEnemyIds[i];
                if (!ComponentStore.IsValidEntity(enemyId)) continue;
                if (!store.EnemyActive[enemyId]) continue;

                // Decrement phase cooldown (tracks when enemy can phase again)
                float cooldown = store.EnemyPhaseCooldown[enemyId];
                if (cooldown > 0f)
                {
                    store.EnemyPhaseCooldown[enemyId] = Math.Max(0f, cooldown - deltaTime);
                }

                // Decrement phase timer (tracks remaining time in phased state)
                float phaseTimer = store.EnemyPhaseTimer[enemyId];
                if (phaseTimer > 0f)
                {
                    store.EnemyPhaseTimer[enemyId] = Math.Max(0f, phaseTimer - deltaTime);
                    // When timer hits 0, phase ends
                    if (store.EnemyPhaseTimer[enemyId] <= 0f && store.EnemyIsPhased[enemyId])
                    {
                        store.EnemyIsPhased[enemyId] = false;
                        // Reset cooldown so enemy can phase again in the future
                        if (store.EnemyPhaseCooldown[enemyId] <= 0f)
                        {
                            store.EnemyPhaseCooldown[enemyId] = 0f;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Activates phase state for an enemy for the specified duration (in turns).
        /// If already phased, resets the timer to the full duration.
        /// </summary>
        public void ActivatePhase(int enemyId, float duration)
        {
            if (!ComponentStore.IsValidEntity(enemyId)) return;
            if (!store.EnemyActive[enemyId]) return;

            store.EnemyIsPhased[enemyId] = true;
            store.EnemyPhaseDuration[enemyId] = duration;
            store.EnemyPhaseTimer[enemyId] = duration;
        }

        /// <summary>
        /// Deactivates phase state for an enemy (e.g., when anti-phase tower hits it).
        /// </summary>
        public void DeactivatePhase(int enemyId)
        {
            if (!ComponentStore.IsValidEntity(enemyId)) return;
            store.EnemyIsPhased[enemyId] = false;
            store.EnemyPhaseTimer[enemyId] = 0f;
        }

        /// <summary>
        /// Returns true if the given enemy is currently in phased state.
        /// </summary>
        public bool IsPhased(int enemyId)
        {
            if (!ComponentStore.IsValidEntity(enemyId)) return false;
            return store.EnemyIsPhased[enemyId];
        }

        /// <summary>
        /// Returns true if the given tower can damage phased enemies.
        /// </summary>
        public bool CanDamagePhased(int towerId)
        {
            if (!ComponentStore.IsValidEntity(towerId)) return false;
            return store.TowerIsAntiPhase[towerId];
        }
    }
}
