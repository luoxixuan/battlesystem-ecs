#nullable enable
using System;
using System.Threading.Tasks;
using BattleSystemECS.Core;
using BattleSystemECS.Components;
using BattleSystemECS.Core.GAS;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Suicide Bomber / Kamikaze System — triggers AoE explosion when suicide enemies reach tower range.
    /// 
    /// Suicide enemies are fast-moving enemies that seek the nearest tower and explode on contact,
    /// dealing AoE damage to towers and nearby enemies. This creates a high-risk/high-reward dynamic
    /// where suicide enemies can damage multiple targets but must get close to do so.
    /// 
    /// Execution: runs in CombatGroup after TowerAttack (enemies have been hit, may be low HP).
    /// The two-phase pattern:
    ///   Phase 1 (parallel): scan suicide enemies near towers, collect into active-index-owned buffers
    ///   Phase 2 (serial): apply collected explosion damage to towers and enemies
    /// </summary>
    public class SuicideBombSystem
    {
        private readonly ComponentStore store;
        private readonly int playerId;
        private readonly global::BattleSystemECS.Content.Contracts.IReflectionCommandPort? _reflectTowerSystem;
        private readonly global::BattleSystemECS.Content.Contracts.ITowerTargetingView? _towerStealthSystem;
        private SuicideExplosionEvent[] _explosionEvents=Array.Empty<SuicideExplosionEvent>();
        private bool[] _hasExplosionEvent=Array.Empty<bool>();
        private int _explosionCollectCount;
        private int _currentTurn;
        
        // Cached active enemy list per turn
        private System.Collections.Generic.List<int> _activeEnemyList = null!;

        public SuicideBombSystem(ComponentStore store, int playerId, global::BattleSystemECS.Content.Contracts.IReflectionCommandPort? reflectTowerSystem = null, global::BattleSystemECS.Content.Contracts.ITowerTargetingView? towerStealthSystem = null)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.playerId = playerId;
            this._reflectTowerSystem = reflectTowerSystem;
            this._towerStealthSystem = towerStealthSystem;
        }

        public void SetTurn(int turn)
        {
            _currentTurn=turn;
            _activeEnemyList = store.GetCachedActiveEnemyIds();
        }

        public void Update()
        {
            if (_activeEnemyList == null)
                _activeEnemyList = store.GetCachedActiveEnemyIds();
            
            // Phase 1: collect explosion events in parallel
            CollectExplosionEvents();
            
            // Phase 2: apply explosion damage serially (includes reflect tower damage if towers have reflect)
            ApplyExplosionDamage();
        }

        /// <summary>
        /// Phase 1: Scan suicide enemies for explosion triggers.
        /// Trigger condition: within trigger range of a tower OR HP below death threshold.
        /// Collected as explosion events for serial processing.
        /// </summary>
        private void CollectExplosionEvents()
        {
            var activeEnemyIds = _activeEnemyList;
            var count = activeEnemyIds.Count;
            EnsureExplosionCapacity(count);
            Array.Clear(_hasExplosionEvent,0,count);
            _explosionCollectCount=count;

            Parallel.For(0, count, ParallelOptionsCache.HotPath, i =>
            {
                int enemyId = activeEnemyIds[i];
                if (!store.EnemyActive[enemyId])
                    return;

                // Check if this enemy is a suicide bomber
                if (!store.EnemyIsSuicide[enemyId])
                    return;

                float triggerRange = store.EnemySuicideTriggerRange[enemyId];
                float dmgRadius = store.EnemySuicideDmgRadius[enemyId];
                float dmgAmount = store.EnemySuicideDmgAmount[enemyId];
                
                if (dmgAmount <= 0f)
                    return;

                // Suicide enemies seek the nearest tower — find closest tower
                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];
                
                // Search for nearest tower within trigger range
                float nearestDistSq = float.MaxValue;
                float nearestTowerX = 0f;
                float nearestTowerY = 0f;
                bool foundTarget = false;

                for (int towerId = 0; towerId < ComponentStore.MAX_ENTITIES; towerId++)
                {
                    if (!store.TowerActive[towerId])
                        continue;

                    // Stealth filter: skip stealthed towers unless this enemy has True Sight
                    if (_towerStealthSystem != null && !_towerStealthSystem.CanTargetTower(towerId, enemyId))
                        continue;

                    float towerX = store.PositionX[towerId];
                    float towerY = store.PositionY[towerId];
                    float dx = enemyX - towerX;
                    float dy = enemyY - towerY;
                    float distSq = dx * dx + dy * dy;
                    
                    // Track nearest tower
                    if (distSq < nearestDistSq)
                    {
                        nearestDistSq = distSq;
                        nearestTowerX = towerX;
                        nearestTowerY = towerY;
                        foundTarget = true;
                    }
                }

                // Trigger if nearest tower is within trigger range
                if (foundTarget && nearestDistSq <= triggerRange * triggerRange)
                {
                    // Queue explosion event (to be processed serially)
                    _explosionEvents[i]=new SuicideExplosionEvent
                    {
                        EnemyId = enemyId,
                        ExplosionX = enemyX,
                        ExplosionY = enemyY,
                        DamageRadius = dmgRadius,
                        DamageAmount = dmgAmount,
                        TargetTowerX = nearestTowerX,
                        TargetTowerY = nearestTowerY
                    };
                    _hasExplosionEvent[i]=true;
                }
            });
        }

        /// <summary>
        /// Phase 2: Apply collected explosion damage serially.
        /// AoE damage to towers (primary) and nearby enemies (secondary).
        /// Also handles the suicide enemy's own death (queue it).
        /// </summary>
        private void ApplyExplosionDamage()
        {
            // 按活跃敌人索引顺序应用爆炸，不依赖并行调度。
            for(int i=0;i<_explosionCollectCount;i++)
            {
                if(!_hasExplosionEvent[i])continue;
                SuicideExplosionEvent evt=_explosionEvents[i];
                // Damage to towers within explosion radius (primary target is the nearest tower)
                ApplyDamageToTowers(evt);
                
                // Damage to enemies within explosion radius (splash to nearby enemies)
                ApplyDamageToEnemies(evt);
                
                // Queue the suicide enemy's own death (it dies in the explosion)
                store.QueueEnemyDeath(evt.EnemyId, playerId);
            }
            _explosionCollectCount=0;
        }

        /// <summary>
        /// Apply AoE damage to towers within the explosion radius.
        /// Primary target (nearest tower) takes full damage, others take partial (falloff).
        /// </summary>
        private void ApplyDamageToTowers(SuicideExplosionEvent evt)
        {
            float dmgRadius = evt.DamageRadius;
            float dmgRadiusSq = dmgRadius * dmgRadius;
            
            for (int towerId = 0; towerId < ComponentStore.MAX_ENTITIES; towerId++)
            {
                if (!store.TowerActive[towerId])
                    continue;

                float towerX = store.PositionX[towerId];
                float towerY = store.PositionY[towerId];
                float dx = evt.ExplosionX - towerX;
                float dy = evt.ExplosionY - towerY;
                float distSq = dx * dx + dy * dy;

                if (distSq > dmgRadiusSq)
                    continue;

                // Calculate damage with falloff (full damage at center, linear falloff to edge)
                float dist = (float)Math.Sqrt(distSq);
                float falloffRatio = dmgRadius > 0f ? dist / dmgRadius : 0f; // 0 at center, 1 at edge
                float falloffMult = 1f - falloffRatio * 0.5f; // 1.0 at center, 0.5 at edge
                float finalDamage = evt.DamageAmount * falloffMult;
                // Apply stealth damage reduction for semi-stealth (type 3) towers
                if (_towerStealthSystem != null)
                    finalDamage *= _towerStealthSystem.GetStealthDamageMultiplier(towerId);
                // Apply damage directly to player health
                var playerHandle = store.GetEntityHandle(store.PlayerEntityId);
                if (playerHandle.IsValid)
                    store.ResourceResolver.TryApply(new Core.GAS.ResourceRequest(playerHandle, playerHandle, new Core.GAS.AttributeKey(3), -finalDamage, Core.GAS.ResourceOperation.Add, evt.EnemyId, store.AllocateGameplaySequence(playerId), ownerPlayerId: playerId));

                // Reflect tower: if this tower has reflect, queue reflect damage back to the suicide bomber
                if (_reflectTowerSystem != null && store.TowerReflectRatio[towerId] > 0f)
                {
                    _reflectTowerSystem.QueueReflect(towerId, evt.EnemyId, finalDamage);
                }

                // Retaliate: if this tower has retaliate chance > 0, roll the dice. Retaliate
                // is independent of Reflect — both can fire on the same hit. Retaliate deals
                // a single independent strike based on the tower's base damage, not the
                // incoming hit size, so it's a frequency-based counter to high-attack-speed enemies.
                if (_reflectTowerSystem != null)
                {
                    float retaliateChance = store.TowerRetaliateChance[towerId];
                    if (retaliateChance > 0f)
                    {
                        if (DeterministicRoll(evt.EnemyId,towerId,_currentTurn) < retaliateChance)
                        {
                            float retaliateMult = store.TowerRetaliateDamageMult[towerId];
                            if (retaliateMult > 0f)
                            {
                                float baseDmg = store.TowerBaseDamage[towerId];
                                if (baseDmg > 0f)
                                {
                                    _reflectTowerSystem.QueueRetaliate(
                                        towerId,
                                        evt.EnemyId,
                                        baseDmg * retaliateMult);
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Apply AoE damage to enemies within the explosion radius.
        /// Damage to the suicide bomber's target enemy (nearest) and splash to others.
        /// </summary>
        private void ApplyDamageToEnemies(SuicideExplosionEvent evt)
        {
            float dmgRadius = evt.DamageRadius;
            float dmgRadiusSq = dmgRadius * dmgRadius;
            
            var activeEnemyIds = _activeEnemyList;
            var count = activeEnemyIds.Count;

            for (int i = 0; i < count; i++)
            {
                int enemyId = activeEnemyIds[i];
                if (!store.EnemyActive[enemyId])
                    continue;
                
                // Skip the suicide bomber itself (it's already queued for death)
                if (enemyId == evt.EnemyId)
                    continue;

                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];
                float dx = evt.ExplosionX - enemyX;
                float dy = evt.ExplosionY - enemyY;
                float distSq = dx * dx + dy * dy;

                if (distSq > dmgRadiusSq)
                    continue;

                // Calculate damage with falloff
                float dist = (float)Math.Sqrt(distSq);
                float falloffRatio = dmgRadius > 0f ? dist / dmgRadius : 0f;
                float falloffMult = 1f - falloffRatio * 0.5f;
                float finalDamage = evt.DamageAmount * falloffMult;

                // Apply damage to enemy health (raw damage, not newHealth for two-phase correctness)
                var source = store.GetEntityHandle(evt.EnemyId);
                var target = store.GetEntityHandle(enemyId);
                if (source.IsValid && target.IsValid)
                    store.DamageResolver.TryApply(new Core.GAS.DamageRequest(source, target, finalDamage, DamageType.True, ElementType.None, DamageFlags.None, DamageAmountStage.Raw, DamageCommitBoundary.GameplayResolve, store.AllocateGameplaySequence(enemyId), ownerPlayerId: playerId));
            }
        }

        private readonly struct SuicideExplosionEvent
        {
            public int EnemyId { get; init; }
            public float ExplosionX { get; init; }
            public float ExplosionY { get; init; }
            public float DamageRadius { get; init; }
            public float DamageAmount { get; init; }
            public float TargetTowerX { get; init; }
            public float TargetTowerY { get; init; }
        }

        private void EnsureExplosionCapacity(int count)
        {
            if(_explosionEvents.Length>=count)return;
            int capacity=Math.Max(count,Math.Max(16,_explosionEvents.Length*2));
            Array.Resize(ref _explosionEvents,capacity);
            Array.Resize(ref _hasExplosionEvent,capacity);
        }

        private static double DeterministicRoll(int enemyId,int towerId,int turn)
        {
            uint value=(uint)(enemyId*73856093)^(uint)(towerId*19349663)^(uint)(turn*83492791);
            value^=value<<13;value^=value>>17;value^=value<<5;
            return (value&0x00FFFFFF)/16777216.0;
        }
    }
}
