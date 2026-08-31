#nullable enable
using System;
using System.Threading.Tasks;
using BattleSystemECS.Core;
using BattleSystemECS.Components;
using BattleSystemECS.Core.GAS;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Hero / Mercenary System — mobile player-controlled units with attack capabilities.
    /// 
    /// Heroes are deployed from the player's position to a target location on the map.
    /// Once deployed, they move toward waypoints (using pathfinding or direct movement)
    /// and attack nearby enemies within their attack range.
    /// 
    /// Heroes are distinct from towers:
    /// - Mobile: can move to different positions after deployment
    /// - Manual control: player clicks to deploy/move
    /// - Skills: can have active abilities (future extension)
    /// 
    /// Two-stage parallel execution (consistent with tower/enemy attack patterns):
    /// - Parallel: collect damage events for heroes attacking enemies
    /// - Serial: resolve damage at end of frame
    /// 
    /// Integration points:
    /// - BuildGroup: deployment command (hero placement)
    /// - CombatGroup: movement + attack (when deployed)
    /// - ComponentStore_Player: hero fields (HeroIsDeployed, HeroPosX/Y, HeroTargetX/Y, HeroMoveSpeed, HeroAttackRange, HeroDamage, HeroAttackSpeed, HeroCooldown)
    /// </summary>
    public class HeroSystem
    {
        private readonly ComponentStore store;
        private readonly int playerId;
        
        // Damage queue for two-stage parallel attack resolution
        private readonly int[] _damageQueue = new int[ComponentStore.MAX_ENTITIES];
        private readonly int[] _targetQueue = new int[ComponentStore.MAX_ENTITIES];
        private int _damageQueueIdx;
        private readonly object _damageQueueLock = new object();
        
        public HeroSystem(ComponentStore store, int playerId)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.playerId = playerId;
        }

        public void SetTurn(int turn)
        {
            // Per-turn cache reset — nothing to cache for hero system
        }

        /// <summary>
        /// Main update: handles hero deployment, movement, and attacks.
        /// Only active for deployed heroes.
        /// </summary>
        public void Update(float deltaTime)
        {
            // Phase 1: Move deployed heroes toward their target positions
            MoveHeroes();
            
            // Phase 2: Collect hero attack damage (parallel safe)
            CollectHeroAttacks();
            
            // Phase 3: Resolve collected damage (serial)
            ResolveHeroDamage();
        }

        /// <summary>
        /// Move deployed heroes toward their target positions.
        /// Uses direct Euclidean movement (no pathfinding for simplicity).
        /// Heroes that reach their target stop moving.
        /// </summary>
        private void MoveHeroes()
        {
            // Iterate all potential hero slots (MAX_HEROES = 5)
            for (int i = 0; i < ComponentStore.MAX_HEROES; i++)
            {
                if (!store.HeroIsDeployed[i]) continue;
                
                float heroX = store.HeroPosX[i];
                float heroY = store.HeroPosY[i];
                float targetX = store.HeroTargetX[i];
                float targetY = store.HeroTargetY[i];
                float moveSpeed = store.HeroMoveSpeed[i];
                
                // Compute direction to target
                float dx = targetX - heroX;
                float dy = targetY - heroY;
                float distSq = dx * dx + dy * dy;
                
                if (distSq < 0.01f)
                {
                    // Already at target — snap to target position
                    store.HeroPosX[i] = targetX;
                    store.HeroPosY[i] = targetY;
                    continue;
                }
                
                float dist = (float)Math.Sqrt(distSq);
                float moveAmount = moveSpeed; // units per frame (deltaTime already applied upstream if needed)
                
                if (moveAmount >= dist)
                {
                    // Reached target
                    store.HeroPosX[i] = targetX;
                    store.HeroPosY[i] = targetY;
                }
                else
                {
                    // Move toward target
                    float normX = dx / dist;
                    float normY = dy / dist;
                    store.HeroPosX[i] = heroX + normX * moveAmount;
                    store.HeroPosY[i] = heroY + normY * moveAmount;
                }
            }
        }

        /// <summary>
        /// Phase 2 (parallel): collect damage from heroes attacking nearby enemies.
        /// Uses SpatialGrid for O(cells) range queries instead of O(enemies) full scan.
        /// No structural mutations in this phase — only collect damage events.
        /// </summary>
        private void CollectHeroAttacks()
        {
            int queueIdx = _damageQueueIdx;
            int count = 0;
            object lockObj = _damageQueueLock;
            
            // Iterate all potential hero slots
            Parallel.For(0, ComponentStore.MAX_HEROES, ParallelOptionsCache.HotPath, i =>
            {
                if (!store.HeroIsDeployed[i]) return;
                
                float heroX = store.HeroPosX[i];
                float heroY = store.HeroPosY[i];
                int attackRange = store.HeroAttackRange[i];
                float attackSpeed = store.HeroAttackSpeed[i];
                float damage = store.HeroDamage[i];
                float cooldown = store.HeroCooldown[i];
                
                // Cooldown check — hero must wait between attacks
                if (cooldown > 0f) return;
                
                // Get enemies in range using SpatialGrid
                var candidates = new int[ComponentStore.MAX_ENTITIES];
                int candidateCount = 0;
                store.SpatialGrid.GetEnemiesInRange(store, heroX, heroY, attackRange, candidates, ref candidateCount);
                
                if (candidateCount == 0) return;
                
                // Find nearest enemy (within range)
                int bestTarget = -1;
                float bestDist = float.MaxValue;
                
                for (int j = 0; j < candidateCount; j++)
                {
                    int enemyId = candidates[j];
                    if (!store.EnemyActive[enemyId]) continue;
                    
                    float ex = store.PositionX[enemyId];
                    float ey = store.PositionY[enemyId];
                    float dSq = (ex - heroX) * (ex - heroX) + (ey - heroY) * (ey - heroY);
                    if (dSq < bestDist)
                    {
                        bestDist = dSq;
                        bestTarget = enemyId;
                    }
                }
                
                if (bestTarget < 0) return;
                
                // Compute attack interval from speed
                float attackInterval = 1.0f / Math.Max(0.1f, attackSpeed);
                if (cooldown <= 0f)
                {
                    // Reset cooldown on attack
                    store.HeroCooldown[i] = attackInterval;
                }
                
                // Collect damage event — use lock for thread-safe queue append
                lock (lockObj)
                {
                    _damageQueue[count] = (int)damage;
                    _targetQueue[count] = bestTarget;
                    count++;
                }
            });
            
            // Flip queue index for next frame (ping-pong)
            _damageQueueIdx = 1 - _damageQueueIdx;
        }

        /// <summary>
        /// Phase 3 (serial): resolve collected hero attack damage.
        /// Must be called after CollectHeroAttacks in the same frame.
        /// </summary>
        private void ResolveHeroDamage()
        {
            int queueIdx = 1 - _damageQueueIdx; // read from the queue we just wrote to
            int count = 0;
            
            // Count entries (simple approach — iterate up to MAX_ENTITIES)
            for (int i = 0; i < ComponentStore.MAX_ENTITIES; i++)
            {
                if (_damageQueue[i] == 0 && _targetQueue[i] == 0) break;
                count++;
            }
            
            for (int i = 0; i < count; i++)
            {
                int damage = _damageQueue[i];
                int targetId = _targetQueue[i];
                
                if (targetId < 0 || targetId >= ComponentStore.MAX_ENTITIES) continue;
                if (!store.EnemyActive[targetId]) continue;
                
                // Apply raw damage to enemy (no last-write-wins stacking)
                var source = store.GetEntityHandle(store.PlayerEntityId);
                var target = store.GetEntityHandle(targetId);
                if (source.IsValid)
                    store.DamageResolver.TryApply(new Core.GAS.DamageRequest(source, target, damage, DamageType.True,
                        ElementType.None, DamageFlags.None, DamageAmountStage.Raw, DamageCommitBoundary.GameplayResolve,
                        store.AllocateGameplaySequence(targetId), ownerPlayerId: playerId));
            }
            
            // Clear queues
            Array.Clear(_damageQueue, 0, ComponentStore.MAX_ENTITIES);
            Array.Clear(_targetQueue, 0, ComponentStore.MAX_ENTITIES);
        }

        /// <summary>
        /// Deploy a hero at the player's position, targeting a map location.
        /// Called from TowerPlacementSystem when player uses "deploy hero" command.
        /// </summary>
        public void DeployHero(int heroSlot, float targetX, float targetY)
        {
            if (heroSlot < 0 || heroSlot >= ComponentStore.MAX_HEROES) return;
            store.HeroIsDeployed[heroSlot] = true;
            store.HeroTargetX[heroSlot] = targetX;
            store.HeroTargetY[heroSlot] = targetY;
            // Position starts at player's position
            store.HeroPosX[heroSlot] = store.PositionX[playerId];
            store.HeroPosY[heroSlot] = store.PositionY[playerId];
            store.HeroCooldown[heroSlot] = 0f;
        }

        /// <summary>
        /// Recall a deployed hero (cancel movement, mark as undeployed).
        /// </summary>
        public void RecallHero(int heroSlot)
        {
            if (heroSlot < 0 || heroSlot >= ComponentStore.MAX_HEROES) return;
            store.HeroIsDeployed[heroSlot] = false;
            store.HeroPosX[heroSlot] = 0f;
            store.HeroPosY[heroSlot] = 0f;
            store.HeroTargetX[heroSlot] = 0f;
            store.HeroTargetY[heroSlot] = 0f;
        }
    }
}
