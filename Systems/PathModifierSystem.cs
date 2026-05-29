using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Components;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Path Modifier System — manages path modification nodes that force enemies to reroute.
    /// When an enemy enters a path modifier's influence zone, its EnemyPathId is updated to
    /// the modifier's target path, causing it to follow a different waypoint sequence.
    /// 
    /// Two-phase design:
    ///   Phase 1 (SetTurn): scan all active path modifiers, build affected enemy list
    ///   Phase 2 (Update): assign target path IDs to enemies in influence zones
    /// 
    /// Enemies that leave the influence zone can be reassigned to their original path
    /// (if we track originalPathId per-enemy) or stay on the modified path.
    /// </summary>
    public class PathModifierSystem
    {
        private readonly ComponentStore store;
        // Cached list of modifier entity IDs that are active this frame
        private List<int> _activeModifierIds;

        public PathModifierSystem(ComponentStore store)
        {
            this.store = store;
            _activeModifierIds = new List<int>(64);
        }

        /// <summary>
        /// Discover all active path modifiers. Called once per frame after spatial grid rebuild.
        /// </summary>
        public void SetTurn()
        {
            _activeModifierIds.Clear();
            for (int i = 0; i < ComponentStore.MAX_ENTITIES; i++)
            {
                if (store.PathModifierActive[i])
                    _activeModifierIds.Add(i);
            }
        }

        /// <summary>
        /// Apply path modifications: for each active modifier, find enemies in its influence
        /// zone and update their EnemyPathId to the modifier's target path.
        /// 
        /// For enemies that were previously affected by a different modifier, the most recently
        /// processed modifier wins (last-in-list). For stable ordering, modifiers are processed
        /// in entity ID order.
        /// 
        /// Enemies with no path assigned (EnemyPathId &lt; 0) are skipped — they use default
        /// direction-based movement and are not affected by path modifiers.
        /// </summary>
        public void Update(float deltaTime)
        {
            if (_activeModifierIds.Count == 0) return;

            var activeEnemyIds = store.GetCachedActiveEnemyIds();
            if (activeEnemyIds == null) return;

            for (int m = 0; m < _activeModifierIds.Count; m++)
            {
                int modifierId = _activeModifierIds[m];
                float modX = store.PathModifierX[modifierId];
                float modY = store.PathModifierY[modifierId];
                float radius = store.PathModifierRadius[modifierId];
                int targetPathId = store.PathModifierTargetPathId[modifierId];
                float radiusSq = radius * radius;

                // Decrement duration timer if this modifier has a limited duration
                float turnsRemaining = store.PathModifierTurnsRemaining[modifierId];
                if (turnsRemaining > 0f)
                {
                    turnsRemaining -= deltaTime;
                    store.PathModifierTurnsRemaining[modifierId] = turnsRemaining;
                    if (turnsRemaining <= 0f)
                    {
                        // Expire the modifier
                        store.DeactivatePathModifier(modifierId);
                        continue;
                    }
                }

                // Scan all active enemies and reassign path for those within influence zone
                for (int i = 0; i < activeEnemyIds.Count; i++)
                {
                    int enemyId = activeEnemyIds[i];
                    if (!store.EnemyActive[enemyId]) continue;

                    // Only affect enemies that already have a waypoint path assigned
                    if (store.EnemyPathId[enemyId] < 0) continue;

                    float ex = store.PositionX[enemyId];
                    float ey = store.PositionY[enemyId];
                    float dx = modX - ex;
                    float dy = modY - ey;
                    float distSq = dx * dx + dy * dy;

                    if (distSq <= radiusSq)
                    {
                        // Enemy is inside this modifier's zone — assign new path
                        store.EnemyPathId[enemyId] = targetPathId;
                        // Reset waypoint index to start from the beginning of the new path
                        store.EnemyPathNodeIndex[enemyId] = 0;
                    }
                }
            }
        }
    }
}