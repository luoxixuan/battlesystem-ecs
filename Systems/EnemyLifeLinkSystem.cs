using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Enemy Life Link / Damage Sharing System.
    ///
    /// Design:
    /// - LifeLinker enemies can establish bidirectional damage-sharing links with nearby allies
    /// - When active, incoming damage is split between the two linked enemies per LifeLinkDef.DamageShareRatio
    /// - Link is established at runtime (not pre-configured): LifeLinker scans for nearby non-linked enemies
    /// - When a linked enemy dies, the other end takes a break penalty damage (optional)
    ///
    /// Two-phase pattern:
    /// - Phase 1 (Update): LifeLinkers attempt to establish new links (parallel scan + serial link creation)
    /// - Phase 2: handled by TowerAttackSystem/PlayerTowerAttackSystem — damage splitting at hit resolution
    ///
    /// Frame schedule: WavePhase, after EnemyAI.SetTurn/Update (Phase 2), before EnemyMovement.
    /// </summary>
    public class EnemyLifeLinkSystem
    {
        private readonly ComponentStore store;
        private readonly GameConfig gameConfig;
        private readonly IRenderer renderer;
        private List<int> _activeEnemyList;
        private int currentTurn;
        private int _lastOwnerPlayerId;

        // Concurrent queue of link establishment events — processed serially in Update
        private readonly ConcurrentBag<(int linkerId, int targetId, int defId)> _linkQueue =
            new ConcurrentBag<(int, int, int)>();

        // Concurrent bag for break penalty damage events — processed after ResolveEnemiesKilledThisFrame
        private readonly ConcurrentBag<(int deadId, int survivorId, float damage)> _breakPenaltyQueue =
            new ConcurrentBag<(int, int, float)>();

        public EnemyLifeLinkSystem(ComponentStore store, GameConfig gameConfig, IRenderer renderer = null)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.gameConfig = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
            this.renderer = renderer;

            // Subscribe to death events to handle break penalties
            store.OnEnemyKilled += OnEnemyKilledHandler;
        }

        /// <summary>
        /// Called at the start of each turn.
        /// </summary>
        public void SetTurn(int turn)
        {
            currentTurn = turn;
            _activeEnemyList = store.GetCachedActiveEnemyIds();
        }

        /// <summary>
        /// WavePhase update: LifeLinkers attempt to establish new links with nearby enemies.
        /// Runs in parallel for scanning, serial for link creation (avoids concurrent writes).
        /// </summary>
        public void Update()
        {
            if (_activeEnemyList == null)
                _activeEnemyList = store.GetCachedActiveEnemyIds();

            var activeEnemies = _activeEnemyList;

            // Phase 1 (parallel): each LifeLinker scans for nearby link candidates and queues link events
            Parallel.For(0, activeEnemies.Count, ParallelOptionsCache.HotPath, i =>
            {
                int enemyId = activeEnemies[i];
                if (!store.EnemyActive[enemyId])
                    return;

                // Only LifeLinkers can initiate links
                if (!store.EnemyIsLifeLinker[enemyId])
                    return;

                int defId = store.EnemyLifeLinkDefId[enemyId];
                if (defId < 0 || defId >= gameConfig.LifeLinkDefs.Length)
                    return;

                LifeLinkDef def = gameConfig.LifeLinkDefs[defId];
                if (def == null)
                    return;

                // Check link cooldown: LinkCooldown > 0 means must wait; 0 means always ready
                float cooldown = store.EnemyLifeLinkCooldownLeft[enemyId];
                if (cooldown > 0f)
                {
                    // Cooldown not ready — decrement and skip
                    return;
                }

                // Count current links on this linker
                int currentLinks = CountLinksForMaster(enemyId);
                if (currentLinks >= def.MaxLinks)
                    return; // Already at max links

                // Find nearest non-linked enemy within LinkRange
                float linkerX = store.PositionX[enemyId];
                float linkerY = store.PositionY[enemyId];
                float bestDistSq = def.LinkRange * def.LinkRange;
                int bestTarget = -1;

                // Scan all active enemies for candidates
                var enemies = _activeEnemyList;
                for (int j = 0; j < enemies.Count; j++)
                {
                    int candidateId = enemies[j];
                    if (candidateId == enemyId)
                        continue;
                    if (!store.EnemyActive[candidateId])
                        continue;
                    if (store.EnemyIsLinked[candidateId])
                        continue; // Already linked
                    if (store.EnemyIsLifeLinker[candidateId])
                        continue; // Prefer linking to non-linkers (avoid master-to-master chains)
                    if (store.EnemyIsBurrowed[candidateId])
                        continue; // Can't link to underground enemies

                    float dx = store.PositionX[candidateId] - linkerX;
                    float dy = store.PositionY[candidateId] - linkerY;
                    float distSq = dx * dx + dy * dy;
                    if (distSq <= bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestTarget = candidateId;
                    }
                }

                if (bestTarget >= 0)
                {
                    // Found a candidate — queue link establishment (processed serially below)
                    _linkQueue.Add((enemyId, bestTarget, defId));
                }
            });

            // Phase 2 (serial): process queued link establishments
            while (_linkQueue.TryTake(out var linkEvent))
            {
                var (linkerId, targetId, defId) = linkEvent;
                EstablishLink(linkerId, targetId, defId);
            }
        }

        /// <summary>
        /// Establishes a bidirectional life link between two enemies.
        /// Called serially from Update loop.
        /// </summary>
        private void EstablishLink(int linkerId, int targetId, int defId)
        {
            // Double-check both are still active and unlinked (state may have changed since queuing)
            if (!store.EnemyActive[linkerId] || !store.EnemyActive[targetId])
                return;
            if (store.EnemyIsLinked[linkerId] || store.EnemyIsLinked[targetId])
                return;

            LifeLinkDef def = gameConfig.LifeLinkDefs[defId];

            // Establish link: linker → target
            store.EnemyIsLinked[linkerId] = true;
            store.EnemyLinkedEnemyId[linkerId] = targetId;
            store.EnemyLifeLinkDefId[linkerId] = defId;
            store.EnemyLifeLinkRatio[linkerId] = def.DamageShareRatio;
            store.EnemyLifeLinkCooldownLeft[linkerId] = def.LinkCooldown; // reset cooldown

            // Establish link: target → linker (reverse reference)
            store.EnemyIsLinked[targetId] = true;
            store.EnemyLinkedEnemyId[targetId] = linkerId;
            store.EnemyLifeLinkDefId[targetId] = defId;
            store.EnemyLifeLinkRatio[targetId] = def.DamageShareRatio;
            store.EnemyLifeLinkCooldownLeft[targetId] = 0f; // targets don't need cooldown tracking

            if (renderer != null)
            {
                string linkerName = store.GetEntityName(linkerId) ?? $"enemy_{linkerId}";
                string targetName = store.GetEntityName(targetId) ?? $"enemy_{targetId}";
                renderer.Log($"[LIFELINK] {linkerName} linked to {targetName} ({def.DamageShareRatio:P0} damage share)");
            }
        }

        /// <summary>
        /// Handles break penalty when a linked enemy dies.
        /// The survivor takes a fraction of the dead enemy's max HP as damage.
        /// </summary>
        private void OnEnemyKilledHandler(int deadId, int playerId)
        {
            _lastOwnerPlayerId = playerId;
            if (!store.EnemyActive[deadId] && !store.EnemyIsLinked[deadId])
                return; // Already cleaned up

            if (!store.EnemyIsLinked[deadId])
                return;

            int linkedId = store.EnemyLinkedEnemyId[deadId];
            if (linkedId < 0)
                return;

            // Check if the linked partner is still alive
            if (!store.EnemyActive[linkedId] || !store.EnemyIsLinked[linkedId])
                return;

            int defId = store.EnemyLifeLinkDefId[deadId];
            if (defId < 0 || defId >= gameConfig.LifeLinkDefs.Length)
                return;

            LifeLinkDef def = gameConfig.LifeLinkDefs[defId];
            if (def == null || !def.BreakPenalty)
                return;

            // Apply break penalty: survivor takes damage = fraction of dead enemy's max HP
            float breakDamage = store.EnemyMaxHealth[deadId] * def.BreakPenaltyDamageFraction;
            if (breakDamage > 0f)
            {
                _breakPenaltyQueue.Add((deadId, linkedId, breakDamage));
            }
        }

        /// <summary>
        /// Called after ResolveEnemiesKilledThisFrame to process break penalty damage.
        /// </summary>
        public void ResolveBreakPenalties()
        {
            while (_breakPenaltyQueue.TryTake(out var evt))
            {
                var (deadId, survivorId, breakDamage) = evt;

                // Verify survivor is still alive and linked
                if (!store.EnemyActive[survivorId] || !store.EnemyIsLinked[survivorId])
                    continue;

                // Apply break damage to survivor
                var source = store.GetEntityHandle(deadId);
                var target = store.GetEntityHandle(survivorId);
                if (source.IsValid && target.IsValid)
                    store.DamageResolver.TryApply(new Core.GAS.DamageRequest(source, target, breakDamage, Components.DamageType.True, Components.ElementType.None, Core.GAS.DamageFlags.None, Core.GAS.DamageAmountStage.Raw, Core.GAS.DamageCommitBoundary.GameplayResolve, store.AllocateGameplaySequence(survivorId), parentSequence: deadId, ownerPlayerId: _lastOwnerPlayerId));

                if (renderer != null)
                {
                    string survivorName = store.GetEntityName(survivorId) ?? $"enemy_{survivorId}";
                    renderer.Log($"[LIFELINK] Break penalty: {survivorName} took {breakDamage:F1} damage (linked enemy died)");
                }

                // If survivor dies from break penalty, queue another death
            }
        }

        /// <summary>
        /// Called when an enemy's health is about to be reduced by damage.
        /// Splits damage with linked enemy if applicable.
        /// Returns the actual damage to apply to the primary target; the shared portion
        /// is queued for the linked enemy.
        ///
        /// IMPORTANT: This returns the SPLIT damage for the primary target only.
        /// The linked enemy gets the remaining share applied directly.
        /// Call this from TowerAttackSystem / PlayerTowerAttackSystem damage resolution.
        ///
        /// Returns: (primaryDamage, linkedDamage, linkedEnemyId)
        /// - primaryDamage: damage to apply to primary target (after split)
        /// - linkedDamage: damage to apply to linked enemy
        /// - linkedEnemyId: entity ID of linked enemy (-1 if no link)
        /// </summary>
        public (float primaryDamage, float linkedDamage, int linkedEnemyId) ComputeLinkedDamage(int enemyId, float totalDamage)
        {
            if (!store.EnemyIsLinked[enemyId])
                return (totalDamage, 0f, -1);

            int linkedId = store.EnemyLinkedEnemyId[enemyId];
            if (linkedId < 0 || !store.EnemyActive[linkedId])
                return (totalDamage, 0f, -1);

            float ratio = store.EnemyLifeLinkRatio[enemyId];
            // Primary target takes (1 - ratio), linked enemy takes (ratio)
            float primaryDamage = totalDamage * (1f - ratio);
            float linkedDamage = totalDamage * ratio;
            return (primaryDamage, linkedDamage, linkedId);
        }

        /// <summary>
        /// Counts how many links a given linker currently has.
        /// </summary>
        private int CountLinksForMaster(int linkerId)
        {
            int count = 0;
            var enemies = _activeEnemyList;
            if (enemies == null) return 0;
            for (int i = 0; i < enemies.Count; i++)
            {
                int eid = enemies[i];
                if (store.EnemyActive[eid] && store.EnemyIsLinked[eid] && store.EnemyLinkedEnemyId[eid] == linkerId)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Clears all Life Link state on an enemy (e.g. when link is broken).
        /// Called by DestroyEntity or when a link expires naturally.
        /// </summary>
        public void ClearLink(int enemyId)
        {
            if (!store.EnemyIsLinked[enemyId])
                return;

            int linkedId = store.EnemyLinkedEnemyId[enemyId];

            // Clear linker side
            store.EnemyIsLinked[enemyId] = false;
            store.EnemyLinkedEnemyId[enemyId] = -1;
            store.EnemyLifeLinkDefId[enemyId] = -1;
            store.EnemyLifeLinkRatio[enemyId] = 0f;
            // Don't reset cooldown on clear — link persists until broken

            // Clear linked side (if still active)
            if (linkedId >= 0 && store.EnemyActive[linkedId] && store.EnemyLinkedEnemyId[linkedId] == enemyId)
            {
                store.EnemyIsLinked[linkedId] = false;
                store.EnemyLinkedEnemyId[linkedId] = -1;
                store.EnemyLifeLinkDefId[linkedId] = -1;
                store.EnemyLifeLinkRatio[linkedId] = 0f;
            }
        }

        /// <summary>
        /// Decrement link cooldowns for all LifeLinkers. Called once per frame from FrameScheduler.
        /// </summary>
        public void DecrementCooldowns(float deltaTime)
        {
            var enemies = _activeEnemyList;
            if (enemies == null) return;

            Parallel.For(0, enemies.Count, ParallelOptionsCache.HotPath, i =>
            {
                int enemyId = enemies[i];
                if (!store.EnemyActive[enemyId] || !store.EnemyIsLifeLinker[enemyId])
                    return;

                float cooldown = store.EnemyLifeLinkCooldownLeft[enemyId];
                if (cooldown > 0f)
                {
                    store.EnemyLifeLinkCooldownLeft[enemyId] = Math.Max(0f, cooldown - deltaTime);
                }
            });
        }
    }
}
