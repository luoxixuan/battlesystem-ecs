#nullable enable
using System;
using System.Threading.Tasks;
using BattleSystemECS.Core;
using BattleSystemECS.Components;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Fear / Confuse System — applies fear to enemies based on tower auras.
    /// 
    /// Fear causes enemies to run away from the player (direction = +1, toward y=max).
    /// When an enemy's fear timer expires, it returns to normal behavior.
    /// 
    /// Execution: runs in AIGroup after EnemyAI (so fear can be triggered by AI actions
    /// like the enemy entering a fear-inducing zone), but the actual reverse movement
    /// is handled by EnemyMovementSystem via EnemyActionType.Fear.
    /// 
    /// Two fear application modes:
    /// - Tower aura: towers with TowerFearRadius > 0 apply fear to enemies in range
    /// - Direct application: EnemyAISystem / abilities can set fear duration directly
    /// </summary>
    public class FearSystem
    {
        private readonly ComponentStore store;
        private readonly int playerId;

        public FearSystem(ComponentStore store, int playerId)
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
            // Phase 1: Decrement fear durations and set action enum for fearful enemies
            DecrementFearDurations(deltaTime);

            // Phase 2: Tower aura fear — apply fear to enemies near fear-inducing towers
            ApplyTowerFearAuras();

            // Phase 3: Clear fear flag for enemies whose fear expired
            ClearExpiredFearFlags();
        }

        /// <summary>
        /// Decrement fear duration for all fearful enemies.
        /// When fear expires, the enemy is no longer feared (but may still be in a fear aura, so re-check each frame).
        /// </summary>
        private void DecrementFearDurations(float deltaTime)
        {
            var activeEnemyIds = store.GetCachedActiveEnemyIds();
            var count = activeEnemyIds.Count;

            Parallel.For(0, count, ParallelOptionsCache.HotPath, i =>
            {
                int enemyId = activeEnemyIds[i];
                if (!store.EnemyActive[enemyId])
                    return;

                float fearDur = store.EnemyFearDurationLeft[enemyId];
                if (fearDur > 0f)
                {
                    // Decrement: fear duration is in frames (1 = 1 frame of fear remaining)
                    store.EnemyFearDurationLeft[enemyId] = fearDur - 1f;

                    // Set the action enum to Fear if not already set by another system
                    // (EnemyMovementSystem checks EnemyActionType.Fear for reverse movement)
                    if (store.GetEnemyActionEnum(enemyId) != EnemyActionType.Fear)
                    {
                        store.SetEnemyActionEnum(enemyId, EnemyActionType.Fear);
                    }
                }
            });
        }

        /// <summary>
        /// Tower aura fear: towers with TowerFearRadius > 0 apply fear to nearby enemies.
        /// Fear duration is set to TowerFearDuration, with a chance check per enemy per frame.
        /// </summary>
        private void ApplyTowerFearAuras()
        {
            var activeEnemyIds = store.GetCachedActiveEnemyIds();
            int enemyCount = activeEnemyIds.Count;
            if (enemyCount == 0)
                return;

            // Fast path: check if any tower has fear aura
            bool hasAnyFearTower = false;
            for (int i = 0; i < ComponentStore.MAX_ENTITIES; i++)
            {
                if (store.TowerActive[i] && store.TowerFearRadius[i] > 0f)
                {
                    hasAnyFearTower = true;
                    break;
                }
            }
            if (!hasAnyFearTower)
                return;

            // For each enemy, check all towers for fear aura
            for (int j = 0; j < enemyCount; j++)
            {
                int enemyId = activeEnemyIds[j];
                if (!store.EnemyActive[enemyId])
                    continue;

                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];

                // Scan all towers for fear aura (sparse: only active towers with fear radius > 0)
                for (int towerId = 0; towerId < ComponentStore.MAX_ENTITIES; towerId++)
                {
                    if (!store.TowerActive[towerId])
                        continue;

                    float fearRadius = store.TowerFearRadius[towerId];
                    if (fearRadius <= 0f)
                        continue;

                    float towerX = store.PositionX[towerId];
                    float towerY = store.PositionY[towerId];
                    float fearDuration = store.TowerFearDuration[towerId];
                    float fearChance = store.TowerFearChance[towerId];

                    if (fearDuration <= 0f)
                        continue;

                    // Distance check (squared)
                    float dx = enemyX - towerX;
                    float dy = enemyY - towerY;
                    float distSq = dx * dx + dy * dy;
                    if (distSq > fearRadius * fearRadius)
                        continue;

                    // Chance roll (skip if chance is 100%)
                    if (fearChance < 1f)
                    {
                        // Deterministic per-frame chance: use entity ID as seed for consistent probability
                        // Each frame this enemy is in range, it has fearChance probability of being feared
                        // Using Knuth's multiplicative hash: (enemyId * 2654435761L) % 1000 < fearChance * 1000
                        long hash = ((long)enemyId * 2654435761L) & 0x7FFFFFFFL;
                        int roll = (int)(hash % 1000);
                        if (roll >= (int)(fearChance * 1000))
                            continue;
                    }

                    // Apply fear duration (take the max if already feared)
                    float existing = store.EnemyFearDurationLeft[enemyId];
                    // Check total CC immunity (unstoppable enemies ignore all CC)
                    if (store.EnemyIsUnstoppable[enemyId])
                        continue;
                    // Apply fear resistance: reduce duration by resistance fraction
                    float effectiveFearDuration = fearDuration;
                    if (store.EnemyFearResistance[enemyId] > 0f)
                    {
                        effectiveFearDuration = fearDuration * (1f - store.EnemyFearResistance[enemyId]);
                        if (effectiveFearDuration <= 0f)
                            continue;
                    }
                    if (effectiveFearDuration > existing)
                    {
                        store.EnemyFearDurationLeft[enemyId] = effectiveFearDuration;
                        store.SetEnemyActionEnum(enemyId, EnemyActionType.Fear);
                    }
                }
            }
        }

        /// <summary>
        /// Clear EnemyIsFeared flag for enemies whose fear has expired.
        /// Called after decrement to sync the bool flag with the timer.
        /// </summary>
        private void ClearExpiredFearFlags()
        {
            var activeEnemyIds = store.GetCachedActiveEnemyIds();
            var count = activeEnemyIds.Count;

            Parallel.For(0, count, ParallelOptionsCache.HotPath, i =>
            {
                int enemyId = activeEnemyIds[i];
                if (!store.EnemyActive[enemyId])
                    return;

                if (store.EnemyFearDurationLeft[enemyId] <= 0f)
                {
                    store.EnemyIsFeared[enemyId] = false;
                }
            });
        }
    }
}
