#nullable enable
using System;
using System.Threading.Tasks;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Wind / Air Push System — applies global and local wind forces to enemies.
    /// 
    /// Wind affects enemy movement by adding a directional push force each frame.
    /// Two types of wind sources:
    /// - Global wind: constant wind affecting all enemies on the map (e.g. weather storms)
    /// - Local wind sources: tower-created wind zones with position and radius
    /// 
    /// Execution: runs after EnemyMovement in the Movement group, so wind push
    /// is applied as a persistent force rather than instant displacement.
    /// </summary>
    public class WindSystem
    {
        private readonly ComponentStore store;
        private readonly int playerId;

        public WindSystem(ComponentStore store, int playerId)
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
            // Update global wind duration and gust timers
            UpdateGlobalWind(deltaTime);

            // Apply wind push to all active enemies
            ApplyWindToEnemies(deltaTime);

            // Decay local wind source durations and remove expired sources
            UpdateLocalWindSources(deltaTime);
        }

        /// <summary>
        /// Decrement global wind timers and handle gust logic.
        /// </summary>
        private void UpdateGlobalWind(float deltaTime)
        {
            if (!store.GlobalWindActive[playerId])
                return;

            // Decrement duration
            float duration = store.GlobalWindDuration[playerId];
            if (duration > 0f)
            {
                store.GlobalWindDuration[playerId] = duration - deltaTime;
                if (store.GlobalWindDuration[playerId] <= 0f)
                {
                    store.GlobalWindDuration[playerId] = 0f;
                    store.GlobalWindActive[playerId] = false;
                    return;
                }
            }

            // Gust timer logic
            float gustInterval = store.GlobalWindGustInterval[playerId];
            if (gustInterval > 0f)
            {
                float gustTimer = store.GlobalWindGustTimer[playerId];
                gustTimer -= deltaTime;
                if (gustTimer <= 0f)
                {
                    // Trigger gust: apply bonus strength for one frame
                    store.GlobalWindGustTimer[playerId] = gustInterval;
                    // Gust adds 50% of base strength as bonus
                    store.GlobalWindGustStrength[playerId] = store.GlobalWindStrength[playerId] * 0.5f;
                }
                else
                {
                    store.GlobalWindGustTimer[playerId] = gustTimer;
                    // Decay gust strength linearly over the interval
                    float gustStrength = store.GlobalWindGustStrength[playerId];
                    if (gustStrength > 0f)
                    {
                        float decayRate = gustStrength / gustInterval;
                        store.GlobalWindGustStrength[playerId] = Math.Max(0f, gustStrength - decayRate * deltaTime);
                    }
                }
            }
        }

        /// <summary>
        /// Apply wind push force to all enemies. Called after EnemyMovement so wind
        /// acts as a persistent force rather than a one-time displacement.
        /// </summary>
        private void ApplyWindToEnemies(float deltaTime)
        {
            var activeEnemies = store.GetCachedActiveEnemyIds();
            int count = activeEnemies.Count;
            if (count == 0)
                return;

            // Collect wind parameters
            float globalDir = store.GlobalWindDirection[playerId];
            float globalStrength = store.GlobalWindStrength[playerId];
            bool globalActive = store.GlobalWindActive[playerId];
            float gustBonus = globalActive ? store.GlobalWindGustStrength[playerId] : 0f;
            float totalGlobalStrength = globalStrength + gustBonus;

            // Early exit if no wind at all
            if (!globalActive && store.GetActiveWindSourceCount() == 0)
                return;

            // Parallel wind application — read-heavy, minimal branching
            Parallel.For(0, count, ParallelOptionsCache.HotPath, i =>
            {
                int enemyId = activeEnemies[i];
                if (!store.EnemyActive[enemyId])
                    return;

                float pushX = 0f;
                float pushY = 0f;

                // Global wind push: direction vector × strength
                if (globalActive && totalGlobalStrength > 0f)
                {
                    pushX += (float)Math.Cos(globalDir) * totalGlobalStrength;
                    pushY += (float)Math.Sin(globalDir) * totalGlobalStrength;
                }

                // Local wind sources: check each source and apply if enemy is within radius
                ApplyLocalWindPush(enemyId, ref pushX, ref pushY);

                // Apply push to enemy position (wind pushes enemies in the given direction)
                // Wind strength is in tiles/sec, converted to per-frame via deltaTime
                store.PositionX[enemyId] += pushX * deltaTime;
                store.PositionY[enemyId] += pushY * deltaTime;

                // Update enemy move direction to reflect wind push direction for backstab calculations
                float len = (float)Math.Sqrt(pushX * pushX + pushY * pushY);
                if (len > 0.001f)
                {
                    store.EnemyMoveDirX[enemyId] = pushX / len;
                    store.EnemyMoveDirY[enemyId] = pushY / len;
                }
            });
        }

        /// <summary>
        /// Check each active local wind source and accumulate push if enemy is within radius.
        /// </summary>
        private void ApplyLocalWindPush(int enemyId, ref float pushX, ref float pushY)
        {
            float ex = store.PositionX[enemyId];
            float ey = store.PositionY[enemyId];

            for (int sourceId = 0; sourceId < ComponentStore.MAX_WIND_SOURCES; sourceId++)
            {
                if (!store.WindSourceActive[sourceId])
                    continue;

                // Check if enemy is within this wind source's radius
                float dx = ex - store.WindSourceX[sourceId];
                float dy = ey - store.WindSourceY[sourceId];
                float distSq = dx * dx + dy * dy;
                float radius = store.WindSourceRadius[sourceId];
                if (distSq > radius * radius)
                    continue;

                // Linear falloff: full strength at center, zero at edge
                float falloff = 1f - (float)Math.Sqrt(distSq) / radius;
                float strength = store.WindSourceStrength[sourceId] * falloff;
                float dir = store.WindSourceDirection[sourceId];

                pushX += (float)Math.Cos(dir) * strength;
                pushY += (float)Math.Sin(dir) * strength;
            }
        }

        /// <summary>
        /// Decrement wind source durations and remove expired sources.
        /// </summary>
        private void UpdateLocalWindSources(float deltaTime)
        {
            for (int sourceId = 0; sourceId < ComponentStore.MAX_WIND_SOURCES; sourceId++)
            {
                if (!store.WindSourceActive[sourceId])
                    continue;

                float duration = store.WindSourceDuration[sourceId];
                if (duration <= 0f)
                    continue; // permanent source

                store.WindSourceDuration[sourceId] = duration - deltaTime;
                if (store.WindSourceDuration[sourceId] <= 0f)
                {
                    store.WindSourceDuration[sourceId] = 0f;
                    store.RemoveWindSource(sourceId);
                }
            }
        }

        /// <summary>
        /// Create a wind tower effect at the specified position.
        /// Returns the wind source ID, or -1 if no free slots.
        /// </summary>
        public int CreateTowerWind(float x, float y, float radius, float direction, float strength, float duration, int towerId)
        {
            return store.AddWindSource(x, y, radius, direction, strength, duration, playerId, towerId);
        }

        /// <summary>
        /// Create a global storm wind event.
        /// </summary>
        public void CreateGlobalStorm(float direction, float strength, float duration, float gustInterval = 10f)
        {
            store.SetGlobalWind(playerId, direction, strength, duration, gustInterval);
        }

        /// <summary>
        /// Clear any active global wind.
        /// </summary>
        public void ClearGlobalWind()
        {
            store.ClearGlobalWind(playerId);
        }

        /// <summary>
        /// Get current global wind strength for this player (including gust bonus).
        /// </summary>
        public float GetGlobalWindEffectiveStrength()
        {
            if (!store.GlobalWindActive[playerId])
                return 0f;
            return store.GlobalWindStrength[playerId] + store.GlobalWindGustStrength[playerId];
        }
    }
}