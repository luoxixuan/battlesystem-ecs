#nullable enable
using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Content.Contracts;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Enemy Strafe / Dodge Movement System — handles lateral dodge movement for enemies.
    /// 
    /// Three dodge trigger modes:
    ///   1. Event-driven (默认): EnemyDodgeChance rolled by TowerAttackSystem on incoming attack.
    ///      TowerAttackSystem calls TryTriggerDodge() — if roll succeeds, strafe is queued.
    ///   2. Periodic: EnemyDodgeTimer counts down each frame; when reaches 0, triggers strafe
    ///      and resets to EnemyDodgeCooldown. EnemyDodgeTimer > 0 enables periodic mode.
    ///   3. Passive/always: EnemyDodgeCooldown = 0 and EnemyDodgeTimer = 0 — strafe-ready each frame.
    ///
    /// Integration points:
    ///   - TowerAttackSystem calls TryTriggerDodge() during the accuracy/evasion roll phase
    ///   - EnemyAISystem dodges (enemy_action = "dodge") already apply strafe movement in AI phase
    ///   - This system handles the periodic timer and cooldown decrement for mode 2
    ///   - EnemyMovementSystem reads EnemyIsDodging flag to skip regular movement during strafe
    ///
    /// Two-phase pattern:
    ///   - Phase 1 (SetTurn): decrement cooldowns and periodic timers
    ///   - Phase 2 (Update): trigger periodic strafe when timer expires
    ///
    /// Direction: 方向十 · 敌人偏移移动 (Enemy Strafing / Dodge Movement)
    /// </summary>
    public class EnemyStrafeSystem : global::BattleSystemECS.Content.Contracts.IDodgeResolver
    {
        private readonly ComponentStore _store;
        private readonly IRenderer? _logger;

        public EnemyStrafeSystem(ComponentStore store, IRenderer? logger = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger;
        }

        /// <summary>
        /// SetTurn — decrement dodge cooldowns and periodic timers for all active enemies.
        /// Called from AIGroup during Phase 3 (before EnemyAISystem).
        /// </summary>
        public void SetTurn()
        {
            var activeEnemyIds = _store.GetCachedActiveEnemyIds();
            for (int i = 0; i < activeEnemyIds.Count; i++)
            {
                int enemyId = activeEnemyIds[i];
                if (!_store.EnemyActive[enemyId]) continue;

                // Decrement cooldown (turns remaining before dodge can trigger again)
                float cd = _store.EnemyDodgeCooldown[enemyId];
                if (cd > 0f)
                {
                    _store.EnemyDodgeCooldown[enemyId] = Math.Max(0f, cd - 1f);
                }

                // Decrement periodic timer (counts down to trigger)
                float timer = _store.EnemyDodgeTimer[enemyId];
                if (timer > 0f)
                {
                    _store.EnemyDodgeTimer[enemyId] = Math.Max(0f, timer - 1f);
                }
            }
        }

        /// <summary>
        /// Update — check for periodic strafe triggers and apply strafe offset to PositionX.
        /// Called from AIGroup during Phase 3 (after SetTurn, before EnemyAISystem parallel).
        /// 
        /// For periodic mode: when EnemyDodgeTimer reaches 0, trigger strafe and reset timer.
        /// For event-driven mode: this system does nothing (event handled by TowerAttackSystem).
        /// </summary>
        public void Update()
        {
            var activeEnemyIds = _store.GetCachedActiveEnemyIds();
            for (int i = 0; i < activeEnemyIds.Count; i++)
            {
                int enemyId = activeEnemyIds[i];
                if (!_store.EnemyActive[enemyId]) continue;

                // Periodic strafe: only trigger if timer is configured (timer > 0) and expired
                float timer = _store.EnemyDodgeTimer[enemyId];
                if (timer > 0f) continue; // not expired yet

                float cooldown = _store.EnemyDodgeCooldown[enemyId];
                float chance = _store.EnemyDodgeChance[enemyId];
                float distance = _store.EnemyDodgeDistance[enemyId];

                // Skip if no dodge config or no distance
                if (chance <= 0f || distance <= 0f) continue;

                // Periodic mode: timer expired (== 0), cooldown must be ready
                if (cooldown > 0f) continue;

                // Roll for dodge (only for periodic mode; event-driven mode rolls in TowerAttackSystem)
                if (chance < 1f && DeterministicRoll(_store.CurrentFrame, enemyId, 10) > chance) continue;

                // Execute periodic strafe — choose random lateral direction
                int strafeDir = DeterministicRoll(_store.CurrentFrame, enemyId, 11) < 0.5f ? -1 : 1;
                ExecuteStrafe(enemyId, distance, strafeDir);

                // Reset cooldown (periodic strafe recharges)
                _store.EnemyDodgeCooldown[enemyId] = 1f; // reset to 1 turn
            }
        }

        /// <summary>
        /// TryTriggerDodge — called from TowerAttackSystem when an attack hits this enemy.
        /// Rolls EnemyDodgeChance; if succeeded, skips the attack and triggers strafe.
        /// Returns true if dodge was triggered (attack should be skipped).
        /// </summary>
        /// <param name="enemyId">Target enemy ID</param>
        /// <param name="attackDirection">Direction of the incoming attack (-1=left, +1=right) for strafe direction</param>
        /// <returns>True if dodge triggered (skip damage)</returns>
        public bool TryTriggerDodge(int enemyId, int attackDirection = 0)
        {
            if (!TryQueueDodge(enemyId, attackDirection, 0, out DodgeFact fact)) return false;
            ApplyQueuedDodge(fact);
            return true;
        }

        /// <summary>
        /// 并行命中阶段只读取 SOA 并生成事实；不写位置、冷却或随机状态。
        /// </summary>
        public bool TryQueueDodge(int enemyId, int attackDirection, int salt, out DodgeFact fact)
        {
            fact = default(DodgeFact);
            if (!_store.EnemyActive[enemyId]) return false;
            float chance = _store.EnemyDodgeChance[enemyId];
            if (chance <= 0f || _store.EnemyDodgeCooldown[enemyId] > 0f
                || _store.EnemyDodgeDistance[enemyId] <= 0f) return false;

            float roll = DeterministicRoll(_store.CurrentFrame, enemyId, salt);
            if (chance < 1f && roll >= chance) return false;
            int direction = attackDirection != 0 ? -attackDirection
                : (DeterministicRoll(_store.CurrentFrame, enemyId, salt + 1) < 0.5f ? -1 : 1);
            fact = new DodgeFact(enemyId, _store.EnemyDodgeDistance[enemyId], direction);
            return true;
        }

        /// <summary>屏障后的稳定串行提交；冷却使同一敌人的重复命中只移动一次。</summary>
        public void ApplyQueuedDodge(DodgeFact fact)
        {
            if (!_store.EnemyActive[fact.EnemyId] || _store.EnemyDodgeCooldown[fact.EnemyId] > 0f) return;
            ExecuteStrafe(fact.EnemyId, fact.Distance, fact.Direction);
            _store.EnemyDodgeCooldown[fact.EnemyId] = 1f;
        }

        /// <summary>
        /// Apply the strafe offset to the enemy's X position.
        /// Clamps to map bounds to prevent out-of-bounds.
        /// </summary>
        private void ExecuteStrafe(int enemyId, float distance, int strafeDir)
        {
            float currentX = _store.PositionX[enemyId];
            float newX = currentX + strafeDir * distance;

            // Clamp to map bounds (map width assumed to be 0..10 based on ComponentStore constants)
            // Use a conservative bounds check — map boundaries should be injected via constructor
            const float MIN_X = 0f;
            const float MAX_X = 10f;
            if (newX < MIN_X) newX = MIN_X;
            if (newX > MAX_X) newX = MAX_X;

            _store.PositionX[enemyId] = newX;
        }

        private static float DeterministicRoll(int frame, int entityId, int salt)
        {
            unchecked
            {
                uint x = (uint)(frame * 1103515245 + entityId * 265443576 + salt * 1013904223);
                x ^= x >> 16;
                return (x & 0x00ffffffu) / 16777216f;
            }
        }

    }
}
