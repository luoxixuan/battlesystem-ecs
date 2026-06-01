using System;
using System.Collections.Generic;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Tower Sabotage System — handles enemy EMP/sabotage abilities that disable towers.
    /// 
    /// Sabotage logic:
    /// - Enemies with EnemyCanSabotage=true periodically apply AoE disable to nearby towers
    /// - Disabled towers cannot attack (TowerIsDisabled=true, skipped in TowerAttackSystem)
    /// - Timer counts down; when it reaches 0 the tower re-enables automatically
    /// 
    /// Execution point: CombatGroup, before TowerAttack (so disabled towers skip their attack)
    /// </summary>
    public class TowerSabotageSystem
    {
        private ComponentStore store;

        public TowerSabotageSystem(ComponentStore store)
        {
            this.store = store;
        }

        public void SetTurn()
        {
            // Nothing to cache per-turn for this system (stateless across turns)
        }

        /// <summary>
        /// Update sabotage timers and apply disable effects from sabotage-capable enemies.
        /// Called once per frame from CombatGroup.
        /// </summary>
        public void Update(float deltaTime)
        {
            // Phase 1: Decrement disable timers on disabled towers and re-enable expired ones
            DecrementDisableTimers(deltaTime);

            // Phase 2: Sabotage-capable enemies apply disable to nearby towers
            ApplySabotageEffects(deltaTime);

            // Phase 3: Stat-drain enemies drain nearby tower damage (capped by their DrainRatio)
            //         — passes claim back to a tower automatically when the drainer dies or
            //         walks out of range. No event subscription needed.
            ApplyDrainEffects(deltaTime);
        }

        private void DecrementDisableTimers(float deltaTime)
        {
            var activeTowerIds = store.ActiveTowerIds;
            for (int i = 0; i < activeTowerIds.Count; i++)
            {
                int towerId = activeTowerIds[i];
                if (!store.TowerIsDisabled[towerId]) continue;

                store.TowerDisabledTimer[towerId] -= deltaTime;
                if (store.TowerDisabledTimer[towerId] <= 0f)
                {
                    // Disable expired — re-enable the tower
                    store.TowerIsDisabled[towerId] = false;
                    store.TowerDisabledTimer[towerId] = 0f;
                    // TowerDisabledDuration is preserved for reference
                }
            }
        }

        private void ApplySabotageEffects(float deltaTime)
        {
            var activeEnemyIds = store.ActiveEnemyIds;
            for (int i = 0; i < activeEnemyIds.Count; i++)
            {
                int enemyId = activeEnemyIds[i];
                if (!store.EnemyCanSabotage[enemyId]) continue;

                // Decrement sabotage cooldown timer
                if (store.EnemySabotageTimer[enemyId] > 0f)
                {
                    store.EnemySabotageTimer[enemyId] -= deltaTime;
                    continue; // Still on cooldown
                }

                // Timer expired — ready to sabotage
                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];
                float radius = store.EnemySabotageRadius[enemyId];
                float duration = store.EnemySabotageDuration[enemyId];

                // Find all towers within sabotage radius and disable them
                var activeTowerIds = store.ActiveTowerIds;
                for (int j = 0; j < activeTowerIds.Count; j++)
                {
                    int towerId = activeTowerIds[j];
                    // Skip towers that are already disabled (don't reset their timer)
                    if (store.TowerIsDisabled[towerId]) continue;
                    // Skip towers that are invulnerable during construction
                    if (store.TowerIsConstructing[towerId] && !store.TowerIsVulnerableDuringConstruction[towerId]) continue;

                    float tx = store.PositionX[towerId];
                    float ty = store.PositionY[towerId];
                    float dx = tx - enemyX;
                    float dy = ty - enemyY;
                    float distSq = dx * dx + dy * dy;
                    float radiusSq = radius * radius;

                    if (distSq <= radiusSq)
                    {
                        // Disable this tower
                        store.TowerIsDisabled[towerId] = true;
                        store.TowerDisabledTimer[towerId] = duration;
                        store.TowerDisabledDuration[towerId] = duration;
                    }
                }

                // Reset cooldown for next sabotage attack
                store.EnemySabotageTimer[enemyId] = store.EnemySabotageCooldown[enemyId];
            }
        }

        /// <summary>
        /// Phase 3: Stat-drain enemies continuously drain damage from the nearest undrained
        /// tower within DrainRadius. Per-frame increment = EnemyDrainRate * deltaTime, capped
        /// at EnemyDrainRatio (e.g. 0.5 = tower damage reduced by up to 50%).
        /// When the drainer dies, walks out of range, or moves to a new tower, the original
        /// tower's damage is fully restored automatically (no event subscription required —
        /// we discover staleness via the TowerDrainedByEnemy ID each frame).
        /// </summary>
        private void ApplyDrainEffects(float deltaTime)
        {
            var activeEnemyIds = store.ActiveEnemyIds;
            var activeTowerIds = store.ActiveTowerIds;

            for (int i = 0; i < activeEnemyIds.Count; i++)
            {
                int enemyId = activeEnemyIds[i];
                float drainRatio = store.EnemyDrainRatio[enemyId];
                // Skip non-drainers (default 0 = no drain ability, fast early-out)
                if (drainRatio <= 0f) continue;

                float drainRadius = store.EnemyDrainRadius[enemyId];
                float drainRate = store.EnemyDrainRate[enemyId];
                if (drainRadius <= 0f || drainRate <= 0f) continue;

                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];
                float radiusSq = drainRadius * drainRadius;

                int claimedTower = store.EnemyDrainClaimedTower[enemyId];

                // Validate claim: tower must still be active and within range
                if (claimedTower != -1)
                {
                    bool stillValid = false;
                    if (claimedTower >= 0 && claimedTower < store.TowerDrainedByEnemy.Length
                        && store.TowerDrainedByEnemy[claimedTower] == enemyId)
                    {
                        // Tower claims us as drainer — check range
                        float dx = store.PositionX[claimedTower] - enemyX;
                        float dy = store.PositionY[claimedTower] - enemyY;
                        if (dx * dx + dy * dy <= radiusSq)
                        {
                            stillValid = true;
                        }
                    }
                    if (!stillValid)
                    {
                        // Release our claim (if tower slot still references us)
                        if (claimedTower >= 0 && claimedTower < store.TowerDrainedByEnemy.Length
                            && store.TowerDrainedByEnemy[claimedTower] == enemyId)
                        {
                            // Restore damage to the snapshot taken when this drain started,
                            // not to TowerBaseDamage. This preserves any upgrades that were
                            // applied to TowerAttackDamage during the drain window.
                            store.TowerDrainedByEnemy[claimedTower] = -1;
                            store.TowerCurrentDrain[claimedTower] = 0f;
                            store.TowerAttackDamage[claimedTower] = store.TowerDamageAtDrainStart[claimedTower];
                            store.TowerDamageAtDrainStart[claimedTower] = 0f;
                        }
                        store.EnemyDrainClaimedTower[enemyId] = -1;
                        claimedTower = -1;
                    }
                }

                // No valid claim — try to acquire a new target
                if (claimedTower == -1)
                {
                    int bestTower = -1;
                    float bestDistSq = float.MaxValue;
                    for (int j = 0; j < activeTowerIds.Count; j++)
                    {
                        int towerId = activeTowerIds[j];
                        // Skip towers already being drained by another enemy
                        if (store.TowerDrainedByEnemy[towerId] != -1) continue;
                        // Skip towers under construction (no attack damage to drain)
                        if (store.TowerIsConstructing[towerId]) continue;
                        // Skip towers with no base damage
                        if (store.TowerBaseDamage[towerId] <= 0f) continue;

                        float dx = store.PositionX[towerId] - enemyX;
                        float dy = store.PositionY[towerId] - enemyY;
                        float distSq = dx * dx + dy * dy;
                        if (distSq <= radiusSq && distSq < bestDistSq)
                        {
                            bestDistSq = distSq;
                            bestTower = towerId;
                        }
                    }

                    if (bestTower == -1) continue; // No target in range

                    // Claim this tower (bidirectional: enemy tracks tower, tower tracks enemy).
                    // Snapshot the current TowerAttackDamage so we can restore on release —
                    // upgrades applied while drained will be preserved in the snapshot.
                    store.TowerDamageAtDrainStart[bestTower] = store.TowerAttackDamage[bestTower];
                    store.TowerDrainedByEnemy[bestTower] = enemyId;
                    store.EnemyDrainClaimedTower[enemyId] = bestTower;
                    claimedTower = bestTower;
                }

                // Increment drain (clamped at drainRatio cap)
                float currentDrain = store.TowerCurrentDrain[claimedTower];
                float newDrain = currentDrain + drainRate * deltaTime;
                if (newDrain > drainRatio) newDrain = drainRatio;
                store.TowerCurrentDrain[claimedTower] = newDrain;

                // Apply drain as a multiplier on the pre-drain snapshot of damage. This keeps
                // upgrades (which may have been applied to TowerAttackDamage before this drain
                // started) intact during the drain, and ensures the release restoration is exact.
                // Note: upgrades applied WHILE the drain is in progress will not affect the
                // drained value until the drain releases; the upgrade goes into TowerAttackDamage
                // but we overwrite it here each frame. This is an accepted limitation — players
                // are unlikely to upgrade a tower that is currently being drained.
                store.TowerAttackDamage[claimedTower] = store.TowerDamageAtDrainStart[claimedTower] * (1f - newDrain);
            }

            // Recovery pass: any tower still flagged as drained by a dead drainer must be
            // restored. This catches the case where the enemy was killed this frame (or
            // walked off the map) and won't be iterated above to release its own claim.
            // Cheap O(towers * drainers) but only the drained subset pays the inner loop.
            // Bidirectional check is critical: an entity id may be RECYCLED for a new enemy
            // after the original drainer dies. The new enemy won't have this tower in its
            // EnemyDrainClaimedTower[], so we must verify the bidirectional link is intact.
            for (int t = 0; t < activeTowerIds.Count; t++)
            {
                int towerId = activeTowerIds[t];
                int drainer = store.TowerDrainedByEnemy[towerId];
                if (drainer == -1) continue;
                bool drainerAlive = false;
                for (int e = 0; e < activeEnemyIds.Count; e++)
                {
                    int candidate = activeEnemyIds[e];
                    if (candidate == drainer
                        && candidate < store.EnemyDrainClaimedTower.Length
                        && store.EnemyDrainClaimedTower[candidate] == towerId)
                    {
                        drainerAlive = true; // ID matches AND the new owner of this id claims our tower back
                        break;
                    }
                }
                if (!drainerAlive)
                {
                    // Drainer is dead or its id was recycled by a non-drainer — restore this tower's full damage
                    // to the snapshot taken when the drain started (preserves in-drain upgrades).
                    store.TowerDrainedByEnemy[towerId] = -1;
                    store.TowerCurrentDrain[towerId] = 0f;
                    store.TowerAttackDamage[towerId] = store.TowerDamageAtDrainStart[towerId];
                    store.TowerDamageAtDrainStart[towerId] = 0f;
                }
            }
        }

        /// <summary>
        /// Manually repair a tower (player-initiated, e.g. via build menu).
        /// Immediately clears the disabled state.
        /// </summary>
        public void RepairTower(int towerId)
        {
            if (!ComponentStore.IsValidEntity(towerId)) return;
            store.TowerIsDisabled[towerId] = false;
            store.TowerDisabledTimer[towerId] = 0f;
        }

        /// <summary>
        /// Check if a tower is currently disabled.
        /// </summary>
        public bool IsDisabled(int towerId)
        {
            return store.TowerIsDisabled[towerId];
        }
    }
}