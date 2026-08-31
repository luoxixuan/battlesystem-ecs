#nullable enable
using System;
using System.Threading.Tasks;
using BattleSystemECS.Core;
using BattleSystemECS.Components;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Deployable Trap Tower system — passive "tower" type that triggers an effect
    /// (stun / damage / slow) on enemies that walk into its trigger radius.
    /// Trap towers do not actively attack; each enemy trigger consumes one charge.
    /// When charges hit 0, the trap tower is auto-destroyed (removed from active list).
    /// Lives in MovementGroup (post-movement) so enemies can be detected after they
    /// move into a trap's tile. No allocations in hot path.
    /// </summary>
    public class DeployableTrapSystem
    {
        private readonly ComponentStore _store;
        // Per-enemy, per-trap cooldown frames. Prevents a single trap from re-triggering
        // on the same enemy every frame (would be unfair for stun-locks).
        // 0 = ready, >0 = frames remaining. Decremented per frame in Tick.
        private const int TRAP_COOLDOWN_FRAMES = 5; // 5 frames between re-triggers per enemy-trap pair
        // Reused damage queue to apply trap damage to enemies — no per-frame allocation.
        // Same pattern as ObstacleSystem._trapDamageQueue.
        private readonly System.Collections.Generic.List<(int enemyId, float damage)> _damageQueue
            = new System.Collections.Generic.List<(int, float)>(64);

        public DeployableTrapSystem(ComponentStore store)
        {
            _store = store;
        }

        /// <summary>
        /// Iterate all active trap towers, check enemies in trigger radius, apply effect.
        /// Called once per frame from MovementGroup after EnemyMovementSystem.Update().
        /// </summary>
        public void Update()
        {
            // First, tick down all per-enemy trap cooldowns.
            // (We mutate the dictionary values in place — no new allocations.)
            var cooldownDict = _store.EnemyTrapCooldownTick;
            if (cooldownDict != null && cooldownDict.Count > 0)
            {
                // ToList() is required because we may mutate the dictionary if an enemy dies
                // and we want to prune entries. For now, just decrement in place.
                // KeyCollection enumerator is over the live dictionary, so we snapshot the keys.
                var keyArr = new int[cooldownDict.Count];
                cooldownDict.Keys.CopyTo(keyArr, 0);
                for (int k = 0; k < keyArr.Length; k++)
                {
                    int enemyId = keyArr[k];
                    if (!_store.EnemyActive[enemyId])
                    {
                        // Enemy died — drop its cooldown entry to bound memory.
                        cooldownDict.Remove(enemyId);
                        continue;
                    }
                    int[] perTrap = cooldownDict[enemyId];
                    if (perTrap == null) { cooldownDict.Remove(enemyId); continue; }
                    bool anyActive = false;
                    for (int i = 0; i < perTrap.Length; i++)
                    {
                        if (perTrap[i] > 0)
                        {
                            perTrap[i]--;
                            if (perTrap[i] > 0) anyActive = true;
                        }
                    }
                    if (!anyActive) cooldownDict.Remove(enemyId);
                }
            }

            // Get active tower list (zero-alloc span).
            var activeTowerSpan = _store.GetActiveTowerSpan();
            if (activeTowerSpan.Length == 0) return;
            var activeEnemySpan = _store.GetActiveEnemySpan();

            _damageQueue.Clear();

            // For each active trap tower, scan active enemies in trigger radius.
            for (int t = 0; t < activeTowerSpan.Length; t++)
            {
                int trapId = activeTowerSpan[t];
                if (!_store.TowerIsTrap[trapId]) continue;
                int charges = _store.TowerTrapCharges[trapId];
                if (charges == 0) continue; // 0 = no charges left, inactive
                float radius = _store.TowerTrapTriggerRadius[trapId];
                if (radius <= 0f) continue;
                int effectType = _store.TowerTrapEffectType[trapId];
                float effectValue = _store.TowerTrapEffectValue[trapId];
                float tx = _store.PositionX[trapId];
                float ty = _store.PositionY[trapId];
                float radiusSq = radius * radius;
                // Charge capacity — find all enemies in range and trigger on the first eligible.
                // We do NOT trigger on multiple enemies per frame (would be too powerful).
                // Instead, a trap triggers at most once per frame, on the closest enemy.
                int bestEnemy = -1;
                float bestDistSq = float.MaxValue;
                for (int e = 0; e < activeEnemySpan.Length; e++)
                {
                    int enemyId = activeEnemySpan[e];
                    if (!_store.EnemyActive[enemyId]) continue;
                    // Check per-enemy per-trap cooldown
                    if (cooldownDict != null
                        && cooldownDict.TryGetValue(enemyId, out var perTrap)
                        && trapId < perTrap.Length
                        && perTrap[trapId] > 0)
                    {
                        continue;
                    }
                    float dx = _store.PositionX[enemyId] - tx;
                    float dy = _store.PositionY[enemyId] - ty;
                    float distSq = dx * dx + dy * dy;
                    if (distSq <= radiusSq && distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestEnemy = enemyId;
                    }
                }
                if (bestEnemy < 0) continue;
                // Trigger on bestEnemy.
                // 1) Apply effect
                if (effectType == 1) // stun
                {
                    // Trap effectValue is float (sec); ApplyEnemyStun takes int (turns). Round up so a 0.5s stun still triggers a frame.
                    int stunTurns = (int)Math.Ceiling(effectValue);
                    if (stunTurns < 1) stunTurns = 1;
                    _store.ApplyEnemyStun(bestEnemy, stunTurns);
                }
                else if (effectType == 2) // damage
                {
                    _damageQueue.Add((bestEnemy, effectValue));
                }
                else if (effectType == 3) // slow
                {
                    _store.ApplyEnemySlow(bestEnemy, effectValue, TRAP_COOLDOWN_FRAMES);
                }
                // 2) Set per-enemy per-trap cooldown
                if (cooldownDict == null)
                {
                    // First-time allocation — allocated lazily (see ComponentStore_Enemy field).
                    // Note: we mutate the field directly because ComponentStore.EnemyTrapCooldownTick
                    // is a reference type field.
                    _store.EnemyTrapCooldownTick = cooldownDict
                        = new System.Collections.Generic.Dictionary<int, int[]>(64);
                }
                if (!cooldownDict.TryGetValue(bestEnemy, out var trapArr)
                    || trapArr == null
                    || trapArr.Length <= trapId)
                {
                    // Allocate / grow inner array — sparse, only enemies that stepped on traps.
                    int needed = trapId + 1;
                    var newArr = new int[needed];
                    if (trapArr != null)
                        Array.Copy(trapArr, newArr, Math.Min(trapArr.Length, newArr.Length));
                    cooldownDict[bestEnemy] = newArr;
                    trapArr = newArr;
                }
                trapArr[trapId] = TRAP_COOLDOWN_FRAMES;
                // 3) Consume one charge (unless unlimited = -1)
                if (charges > 0)
                {
                    _store.TowerTrapCharges[trapId] = charges - 1;
                    if (charges - 1 == 0)
                    {
                        // Trap exhausted — destroy it now.
                        _store.DestroyEntity(trapId);
                    }
                }
            }

            // Apply damage outside the loop (frame-end convention: only queue, no direct mutation).
            for (int i = 0; i < _damageQueue.Count; i++)
            {
                var (enemyId, damage) = _damageQueue[i];
                // Use existing damage application pattern: subtract from current HP, queue death if <= 0.
                _store.ApplyDamageAuthority(_store.PlayerEntityId, enemyId, damage, 0, stage: Core.GAS.DamageAmountStage.Raw);
            }
        }
    }
}

namespace BattleSystemECS.Core
{
    /// <summary>Enemy movement, pathfinding, wound, path modifiers, healer, summons, steal gold, pull, path blocks, deployable traps.</summary>
    public class MovementGroup : ISystemGroup
    {
        public Systems.EnemyWoundSystem? Wound { get; set; }
        public Systems.PathfindingSystem? Pathfinding { get; set; }
        public Systems.EnemyMovementSystem? EnemyMovement { get; set; }
        public Systems.PathModifierSystem? PathModifier { get; set; }
        public Systems.PullSystem? Pull { get; set; }
        public Systems.EnemyHealerSystem? EnemyHealer { get; set; }
        public Systems.EnemyStealGoldSystem? StealGold { get; set; }
        public Systems.PlayerSummonSystem? Summon { get; set; }
        public Systems.PathBlockSystem? PathBlock { get; set; }
        // ── Deployable traps ── lazy-initialized: assigned by SystemRegistry.AssignToGroups,
        // or auto-created on first Execute() call if not pre-wired (zero-config fallback).
        public Systems.DeployableTrapSystem? DeployableTrap { get; set; }

        public void Execute(ComponentStore store, float deltaTime, int turn)
        {
            Wound?.SetTurn(turn);
            Wound?.Update();
            Pathfinding?.SetTurn(turn);
            EnemyMovement?.SetTurn(turn);

            // Path blocks: enemies on block cells damage them (runs before movement so frame damage is applied)
            PathBlock?.Update();

            EnemyMovement?.Update();

            // Deployable traps: passive triggers after movement so newly-stepped-on tiles
            // are detected this frame. Lazy-init if not pre-wired.
            if (DeployableTrap == null)
                DeployableTrap = new Systems.DeployableTrapSystem(store);
            DeployableTrap.Update();

            PathModifier?.SetTurn();
            PathModifier?.Update(deltaTime);

            Pull?.SetTurn(turn);
            Pull?.Update(deltaTime);

            EnemyHealer?.SetTurn(turn);
            EnemyHealer?.Update(deltaTime);

            StealGold?.Update();

            Summon?.SetTurn(turn);
            Summon?.Update(deltaTime);
        }
    }
}
