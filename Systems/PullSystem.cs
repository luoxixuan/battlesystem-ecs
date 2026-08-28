#nullable enable
using System;
using System.Threading.Tasks;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Pull / Vacuum / Gravity Well System — applies attractive force to enemies.
    /// 
    /// Pull pulls enemies toward a source point (tower or global gravity well).
    /// Unlike Wind (push), Pull is an attractive force that draws enemies inward.
    /// Execution: runs after EnemyMovement in the Movement group, applying persistent pull force.
    /// </summary>
    public class PullSystem
    {
        private readonly ComponentStore store;
        private readonly int playerId;

        public PullSystem(ComponentStore store, int playerId)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.playerId = playerId;
        }

        public void SetTurn(int turn)
        {
            // Nothing per-turn to cache — all data comes directly from ComponentStore arrays
        }

        public void Update(float deltaTime)
        {
            // Update global pull timers
            UpdateGlobalPull(deltaTime);

            // Apply pull force to all active enemies
            ApplyPullToEnemies(deltaTime);

            // Decrement local pull source durations and remove expired sources
            UpdateLocalPullSources(deltaTime);
        }

        /// <summary>
        /// Decrement global pull duration and handle expiration.
        /// </summary>
        private void UpdateGlobalPull(float deltaTime)
        {
            if (!store.GlobalPullActive[playerId])
                return;

            float duration = store.GlobalPullDuration[playerId];
            if (duration > 0f)
            {
                store.GlobalPullDuration[playerId] = duration - deltaTime;
                if (store.GlobalPullDuration[playerId] <= 0f)
                {
                    store.GlobalPullDuration[playerId] = 0f;
                    store.GlobalPullActive[playerId] = false;
                }
            }
        }

        /// <summary>
        /// Apply pull force to all enemies. Pull is an attractive force toward pull sources.
        /// </summary>
        private void ApplyPullToEnemies(float deltaTime)
        {
            var activeEnemies = store.GetCachedActiveEnemyIds();
            int count = activeEnemies.Count;
            if (count == 0)
                return;

            // Collect global pull parameters
            float globalCenterX = store.GlobalPullCenterX[playerId];
            float globalCenterY = store.GlobalPullCenterY[playerId];
            float globalStrength = store.GlobalPullStrength[playerId];
            bool globalActive = store.GlobalPullActive[playerId];
            int activeLocalSources = store.GetActivePullSourceCount();

            // Early exit if no pull at all
            if (!globalActive && activeLocalSources == 0)
                return;

            // Parallel pull application — read-heavy, minimal branching
            Parallel.For(0, count, ParallelOptionsCache.HotPath, i =>
            {
                int enemyId = activeEnemies[i];
                if (!store.EnemyActive[enemyId])
                    return;

                float pullX = 0f;
                float pullY = 0f;

                // Global pull: attractive force toward center point
                if (globalActive && globalStrength > 0f)
                {
                    float ex = store.PositionX[enemyId];
                    float ey = store.PositionY[enemyId];
                    float dx = globalCenterX - ex;
                    float dy = globalCenterY - ey;
                    float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                    if (dist > 0.001f)
                    {
                        // Normalize and scale by strength
                        pullX += (dx / dist) * globalStrength;
                        pullY += (dy / dist) * globalStrength;
                    }
                }

                // Local pull sources: tower-based vacuum effects
                ApplyLocalPull(enemyId, ref pullX, ref pullY);

                // Apply pull to enemy position
                store.PositionX[enemyId] += pullX * deltaTime;
                store.PositionY[enemyId] += pullY * deltaTime;

                // Update enemy move direction to reflect pull direction for backstab calculations
                float len = (float)Math.Sqrt(pullX * pullX + pullY * pullY);
                if (len > 0.001f)
                {
                    store.EnemyMoveDirX[enemyId] = pullX / len;
                    store.EnemyMoveDirY[enemyId] = pullY / len;
                }
            });
        }

        /// <summary>
        /// Check each active local pull source and accumulate pull if enemy is within radius.
        /// </summary>
        private void ApplyLocalPull(int enemyId, ref float pullX, ref float pullY)
        {
            float ex = store.PositionX[enemyId];
            float ey = store.PositionY[enemyId];

            for (int sourceId = 0; sourceId < ComponentStore.MAX_PULL_SOURCES; sourceId++)
            {
                if (!store.PullSourceActive[sourceId])
                    continue;

                float dx = store.PullSourceX[sourceId] - ex;
                float dy = store.PullSourceY[sourceId] - ey;
                float distSq = dx * dx + dy * dy;
                float radius = store.PullSourceRadius[sourceId];
                if (distSq > radius * radius)
                    continue;

                float dist = (float)Math.Sqrt(distSq);
                if (dist < 0.001f)
                    continue;

                // Linear falloff: full strength at center, zero at edge
                float falloff = 1f - dist / radius;
                float strength = store.PullSourceStrength[sourceId] * falloff;

                // Normalize direction and apply strength
                pullX += (dx / dist) * strength;
                pullY += (dy / dist) * strength;
            }
        }

        /// <summary>
        /// Decrement pull source durations and remove expired sources.
        /// </summary>
        private void UpdateLocalPullSources(float deltaTime)
        {
            for (int sourceId = 0; sourceId < ComponentStore.MAX_PULL_SOURCES; sourceId++)
            {
                if (!store.PullSourceActive[sourceId])
                    continue;

                float duration = store.PullSourceDuration[sourceId];
                if (duration <= 0f)
                    continue; // permanent source

                store.PullSourceDuration[sourceId] = duration - deltaTime;
                if (store.PullSourceDuration[sourceId] <= 0f)
                {
                    store.PullSourceDuration[sourceId] = 0f;
                    store.RemovePullSource(sourceId);
                }
            }
        }

        /// <summary>
        /// Create a tower-based vacuum pull effect at the specified position.
        /// Returns the pull source ID, or -1 if no free slots.
        /// </summary>
        public int CreateTowerPull(float x, float y, float radius, float strength, float duration, int towerId)
        {
            return store.AddPullSource(x, y, radius, strength, duration, playerId, towerId);
        }

        /// <summary>
        /// Create a global gravity well pull effect.
        /// </summary>
        public void CreateGlobalGravityWell(float centerX, float centerY, float strength, float duration)
        {
            store.SetGlobalPull(playerId, centerX, centerY, strength, duration);
        }

        /// <summary>
        /// Clear any active global pull.
        /// </summary>
        public void ClearGlobalPull()
        {
            store.ClearGlobalPull(playerId);
        }

        /// <summary>
        /// Get current global pull strength for this player.
        /// </summary>
        public float GetGlobalPullEffectiveStrength()
        {
            if (!store.GlobalPullActive[playerId])
                return 0f;
            return store.GlobalPullStrength[playerId];
        }
    }
}