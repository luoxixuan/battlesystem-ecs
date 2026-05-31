#nullable enable
using System;
using System.Collections.Generic;
using BattleSystemECS.Core;

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
    public class EnemyStrafeSystem
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
                if (chance < 1f && RandomFloat() > chance) continue;

                // Execute periodic strafe — choose random lateral direction
                int strafeDir = RandomFloat() < 0.5f ? -1 : 1;
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
            if (!_store.EnemyActive[enemyId]) return false;

            float chance = _store.EnemyDodgeChance[enemyId];
            if (chance <= 0f) return false;

            float cooldown = _store.EnemyDodgeCooldown[enemyId];
            if (cooldown > 0f) return false; // on cooldown

            // Roll for dodge
            if (chance < 1f && RandomFloat() >= chance) return false;

            float distance = _store.EnemyDodgeDistance[enemyId];
            if (distance <= 0f) return false;

            // Strafe AWAY from attack direction (if provided), otherwise random
            int strafeDir;
            if (attackDirection != 0)
                strafeDir = -attackDirection; // opposite to incoming
            else
                strafeDir = RandomFloat() < 0.5f ? -1 : 1;

            ExecuteStrafe(enemyId, distance, strafeDir);

            // Set cooldown to 1 turn after event-driven dodge
            _store.EnemyDodgeCooldown[enemyId] = 1f;

            return true;
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

        private static readonly Random _rng = new Random();
        private static float RandomFloat()
        {
            lock (_rng)
            {
                return (float)_rng.NextDouble();
            }
        }
    }
}