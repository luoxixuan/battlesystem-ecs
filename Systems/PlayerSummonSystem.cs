using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 玩家召唤战斗单位系统 — 管理玩家通过技能召唤的临时战斗单位（召唤兽/图腾/幽灵狼）。
    /// 
    /// 设计：
    /// - 召唤单位是可移动的临时战斗单位，与塔不同（固定位置），可拦截敌人、可被击杀
    /// - 三种类型：0=Melee（近战拦截者）, 1=Ranged（远程射手）, 2=Bomber（自爆单位）
    /// - 生命周期：固定时长（Duration 秒）或永久（0=permanent until killed）
    /// - 攻击逻辑：索敌 → 移动 → 攻击，两阶段并行安全模式
    /// - 帧末统一结算：伤害队列在帧末统一 apply，避免 last-write-wins
    /// </summary>
    public class PlayerSummonSystem
    {
        private ComponentStore store;
        private IRenderer renderer;
        private readonly int playerId;
        private GameConfig gameConfig;
        private List<int> _activeEnemyList;
        private List<(int unitId, float damage)>[] _damageQueue = new List<(int, float)>[2];
        private int _damageQueueIdx = 0;
        private readonly object _damageQueueLock = new object();

        // 0 = Melee, 1 = Ranged, 2 = Bomber
        private const int TYPE_MELEE = 0;
        private const int TYPE_RANGED = 1;
        private const int TYPE_BOMBER = 2;

        public PlayerSummonSystem(ComponentStore store, IRenderer renderer, int playerId, GameConfig gameConfig)
        {
            this.store = store;
            this.renderer = renderer;
            this.playerId = playerId;
            this.gameConfig = gameConfig;
            _damageQueue[0] = new List<(int, float)>(64);
            _damageQueue[1] = new List<(int, float)>(64);
        }

        public void SetTurn(int turn)
        {
            _activeEnemyList = store.GetCachedActiveEnemyIds();
            // Reset damage queue for new frame
            lock (_damageQueueLock)
            {
                _damageQueue[_damageQueueIdx].Clear();
            }
        }

        /// <summary>
        /// 更新所有召唤单位：移动、攻击、持续时间扣减、死亡检测。
        /// </summary>
        public void Update(float deltaTime)
        {
            // Two-phase parallel-safe pattern:
            // Phase 1 (parallel): collect damage events, move units, decay durations
            // Phase 2 (serial): apply all damage, process deaths

            // Collect summoned unit ids that are alive
            var activeTowerIds = store.ActiveTowerIds;

            // Phase 1: Per-unit update (parallel-friendly — read-only on shared state)
            for (int i = 0; i < activeTowerIds.Count; i++)
            {
                int unitId = activeTowerIds[i];
                if (!store.SummonedUnitActive[unitId])
                    continue;
                if (store.SummonedUnitOwnerId[unitId] != playerId)
                    continue;

                // Decay duration (permanent units have Duration = 0, skip)
                float duration = store.SummonedUnitDuration[unitId];
                if (duration > 0f)
                {
                    duration -= deltaTime;
                    if (duration <= 0f)
                    {
                        // Unit expired — mark for removal
                        store.SummonedUnitActive[unitId] = false;
                        store.PositionActive[unitId] = false;
                        store.EnemyActive[unitId] = false;
                        renderer.Log($"[SUMMON] Summoned unit {store.GetEntityName(unitId)} expired");
                        continue;
                    }
                    store.SummonedUnitDuration[unitId] = duration;
                }

                // Attack logic
                ProcessUnitAttack(unitId, deltaTime);
            }

            // Phase 2: Apply accumulated damage (serial, single-threaded)
            lock (_damageQueueLock)
            {
                var queue = _damageQueue[_damageQueueIdx];
                for (int i = 0; i < queue.Count; i++)
                {
                    var (unitId, damage) = queue[i];
                    if (!store.SummonedUnitActive[unitId])
                        continue;
                    store.SummonedUnitHealth[unitId] -= damage;
                }
                queue.Clear();
            }
        }

        /// <summary>
        /// 处理单个召唤单位的攻击：索敌、移动（在敌人群中追击）、攻击。
        /// </summary>
        private void ProcessUnitAttack(int unitId, float deltaTime)
        {
            int unitType = store.SummonedUnitType[unitId];
            float unitX = store.PositionX[unitId];
            float unitY = store.PositionY[unitId];
            int targetId = store.SummonedUnitTargetId[unitId];

            // Check if current target is still valid
            bool targetValid = (targetId >= 0)
                && store.EnemyActive[targetId]
                && store.PositionActive[targetId];

            if (!targetValid)
            {
                // Find new target: nearest enemy within attack range
                targetId = FindNearestEnemy(unitId, unitX, unitY);
                store.SummonedUnitTargetId[unitId] = targetId;
            }

            if (targetId < 0)
                return; // No target in range

            float targetX = store.PositionX[targetId];
            float targetY = store.PositionY[targetId];
            float dx = targetX - unitX;
            float dy = targetY - unitY;
            float distSq = dx * dx + dy * dy;
            int attackRange = store.SummonedUnitAttackRange[unitId];
            float attackRangeSq = attackRange * attackRange;

            if (distSq > attackRangeSq)
            {
                // Move toward target (melee/ranged track, bomber also moves)
                float moveSpeed = store.SummonedUnitMoveSpeed[unitId];
                float moveAmount = moveSpeed * deltaTime;
                // Normalize direction
                float dist = (float)Math.Sqrt(distSq);
                if (dist > 0.001f)
                {
                    float nx = dx / dist;
                    float ny = dy / dist;
                    store.PositionX[unitId] = unitX + nx * moveAmount;
                    store.PositionY[unitId] = unitY + ny * moveAmount;
                }
                // Update world position after move
                store.PositionX[unitId] = unitX + (dx / (float)Math.Sqrt(distSq)) * moveAmount;
                store.PositionY[unitId] = unitY + (dy / (float)Math.Sqrt(distSq)) * moveAmount;
            }
            else
            {
                // In range — attack if cooldown ready
                float attackSpeed = store.SummonedUnitAttackSpeed[unitId];
                float attackInterval = (attackSpeed > 0f) ? (1f / attackSpeed) : 1f;
                float timer = store.SummonedUnitAttackTimer[unitId];
                timer -= deltaTime;
                if (timer <= 0f)
                {
                    // Fire attack
                    float damage = store.SummonedUnitDamage[unitId];
                    if (unitType == TYPE_BOMBER)
                    {
                        // Bomber: AoE damage + self-destruct
                        ExecuteBomberAttack(unitId, targetId, damage);
                    }
                    else
                    {
                        // Melee/Ranged: single-target damage
                        lock (_damageQueueLock)
                        {
                            _damageQueue[_damageQueueIdx].Add((targetId, damage));
                        }
                        renderer.Log($"[SUMMON] {store.GetEntityName(unitId)} hits enemy {targetId} for {damage:F0} damage");
                    }
                    store.SummonedUnitAttackTimer[unitId] = attackInterval;
                }
                else
                {
                    store.SummonedUnitAttackTimer[unitId] = timer;
                }
            }
        }

        /// <summary>
        /// 在攻击范围内找最近的敌人。
        /// </summary>
        private int FindNearestEnemy(int unitId, float unitX, float unitY)
        {
            int attackRange = store.SummonedUnitAttackRange[unitId];
            float attackRangeSq = attackRange * attackRange;
            float bestDistSq = float.MaxValue;
            int bestEnemyId = -1;

            var activeEnemyIds = store.ActiveEnemyIds;
            for (int i = 0; i < activeEnemyIds.Count; i++)
            {
                int enemyId = activeEnemyIds[i];
                if (!store.EnemyActive[enemyId] || !store.PositionActive[enemyId])
                    continue;

                float ex = store.PositionX[enemyId];
                float ey = store.PositionY[enemyId];
                float dx = ex - unitX;
                float dy = ey - unitY;
                float distSq = dx * dx + dy * dy;
                if (distSq < attackRangeSq && distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestEnemyId = enemyId;
                }
            }

            return bestEnemyId;
        }

        /// <summary>
        /// 自爆单位的 AoE 攻击：造成范围伤害后自身死亡。
        /// </summary>
        private void ExecuteBomberAttack(int unitId, int targetId, float damage)
        {
            // Bomber explodes at target position with radius = attackRange
            float targetX = store.PositionX[targetId];
            float targetY = store.PositionY[targetId];
            int radius = store.SummonedUnitAttackRange[unitId];
            float radiusSq = radius * radius;

            // Find all enemies in blast radius
            var activeEnemyIds = store.ActiveEnemyIds;
            int hitCount = 0;
            for (int i = 0; i < activeEnemyIds.Count; i++)
            {
                int enemyId = activeEnemyIds[i];
                if (!store.EnemyActive[enemyId] || !store.PositionActive[enemyId])
                    continue;

                float ex = store.PositionX[enemyId];
                float ey = store.PositionY[enemyId];
                float dx = ex - targetX;
                float dy = ey - targetY;
                float distSq = dx * dx + dy * dy;
                if (distSq <= radiusSq)
                {
                    lock (_damageQueueLock)
                    {
                        _damageQueue[_damageQueueIdx].Add((enemyId, damage));
                    }
                    hitCount++;
                }
            }

            renderer.Log($"[SUMMON] {store.GetEntityName(unitId)} BOMBS {hitCount} enemies for {damage:F0} damage!");

            // Bomber self-destructs
            store.SummonedUnitActive[unitId] = false;
            store.PositionActive[unitId] = false;
            store.EnemyActive[unitId] = false;
        }

        /// <summary>
        /// 召唤一个新的战斗单位。
        /// 由 SkillSystem 在施放 summon_unit 类型技能时调用。
        /// </summary>
        public int SummonUnit(int playerId, SummonDef def)
        {
            int unitId = store.CreateEntity();
            if (unitId < 0)
            {
                renderer.Log($"[SUMMON] Failed to create entity for summon '{def.Name}'");
                return -1;
            }

            // Initialize summoned unit components
            store.SummonedUnitActive[unitId] = true;
            store.SummonedUnitType[unitId] = def.UnitType;
            store.SummonedUnitHealth[unitId] = def.Health;
            store.SummonedUnitMaxHealth[unitId] = def.Health;
            store.SummonedUnitDamage[unitId] = def.Damage;
            store.SummonedUnitMoveSpeed[unitId] = def.MoveSpeed;
            store.SummonedUnitAttackRange[unitId] = def.AttackRange;
            store.SummonedUnitAttackSpeed[unitId] = def.AttackSpeed;
            store.SummonedUnitAttackTimer[unitId] = 0f; // ready to attack immediately
            store.SummonedUnitDuration[unitId] = def.Duration;
            store.SummonedUnitOwnerId[unitId] = playerId;
            store.SummonedUnitTargetId[unitId] = -1;
            store.SummonedUnitGoldReward[unitId] = 0;

            // Position: spawn at player position
            store.PositionX[unitId] = store.PositionX[playerId];
            store.PositionY[unitId] = store.PositionY[playerId];
            store.PositionActive[unitId] = true;

            // Mark as active enemy so TowerAttackSystem, EnemyMovementSystem, etc. see it
            store.EnemyActive[unitId] = true;
            store.EnemyHealth[unitId] = def.Health;
            store.EnemyMaxHealth[unitId] = def.Health;
            store.EnemyMoveSpeed[unitId] = def.MoveSpeed;
            store.EnemyDamage[unitId] = def.Damage;
            store.EnemyTypeName[unitId] = $"Summon_{def.Name}";

            store.AddActiveEnemyId(unitId);
            store.SetEntityName(unitId, $"Summon_{def.Name}_{unitId}");

            renderer.Log($"[SUMMON] Player {playerId} summoned {def.Name} (HP:{def.Health:F0} DMG:{def.Damage:F0} SPD:{def.MoveSpeed:F1} RANGE:{def.AttackRange})");
            return unitId;
        }

        /// <summary>
        /// 通知召唤单位死亡。
        /// 由 ResolveEnemiesKilledThisFrame 调用。
        /// </summary>
        public void OnUnitKilled(int unitId)
        {
            if (!store.SummonedUnitActive[unitId])
                return;
            store.SummonedUnitActive[unitId] = false;
            store.SummonedUnitDuration[unitId] = 0f;
        }
    }
}