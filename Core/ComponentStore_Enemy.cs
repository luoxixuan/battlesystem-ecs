using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Core;

namespace BattleSystemECS.Core
{
    public partial class ComponentStore
    {
        // ==================== 敌人组件访问 ====================

        // ── O(1) enemy affix flag helpers ─────────────────────────────────
        public bool HasAffix(int enemyId, BuffType affix)
        {
            if (!IsValidEntity(enemyId)) return false;
            return (EnemyAffixFlags[enemyId] & affix) != 0;
        }

        public int AddEnemy(float startX, float startY, float moveSpeed, float health, float maxHealth, float damage, int goldReward, int waveNumber, string fullName = null, float armor = 0f, float shield = 0f, float magicResist = 0f)
        {
            int entityId = CreateEntity();

            if (!IsValidEntity(entityId)) 
            {
                return -1;
            }

            PositionX[entityId] = startX;
            PositionY[entityId] = startY;
            PositionActive[entityId] = true;

            EnemyHealth[entityId] = health;
            EnemyMaxHealth[entityId] = maxHealth;
            EnemyMoveSpeed[entityId] = moveSpeed;
            EnemyMoveSpeedBase[entityId] = moveSpeed;
            EnemyDamage[entityId] = damage;
            EnemyGoldReward[entityId] = goldReward;
            EnemyWaveNumber[entityId] = waveNumber;
            EnemyActive[entityId] = true;
            // Path/waypoint: default -1 = no path (use straight Y-axis movement)
            EnemyPathId[entityId] = -1;
            EnemyPathNodeIndex[entityId] = 0;
            EnemySpawnFrame[entityId] = CurrentFrame;
            EnemyArmor[entityId] = armor;
            EnemyMagicResist[entityId] = magicResist;
            EnemyShield[entityId] = shield;  // configurable initial shield
            EnemyEvasion[entityId] = 0f;  // default to no evasion
            // Vanguard: default not a vanguard (false = not protecting anyone)
            EnemyIsVanguard[entityId] = false;
            EnemyVanguardCoverRange[entityId] = 0f;
            EnemyVanguardDmgTransfer[entityId] = 0f;
            EnemyVanguardCoverCount[entityId] = 0;
            // Thief: default not a gold thief
            EnemyCanStealGold[entityId] = false;
            EnemyStealAmount[entityId] = 0f;
            EnemyStolenGold[entityId] = 0f;
            EnemyGoldOnReturn[entityId] = 0f;
            EnemyHasStolenGold[entityId] = false;
            // Teleport: default no cooldown (ready), no destination, type=0 (none)
            EnemyTeleportCooldown[entityId] = 0f;
            EnemyTeleportDestinationX[entityId] = 0f;
            EnemyTeleportDestinationY[entityId] = 0f;
            EnemyTeleportType[entityId] = 0;

            // 缓存怪物类型名（如 "NormalL1W1E0" -> "Normal"），避免每帧解析
            // 同时检测 [ELITE]/[BOSS] 前缀来正确标记精英/首领
            if (fullName != null)
            {
                bool isElite = fullName.StartsWith("[ELITE]");
                bool isBoss = fullName.StartsWith("[BOSS]");
                bool isFlying = false; // default: enemies are ground units
                EnemyIsElite[entityId] = isElite;
                EnemyIsFlying[entityId] = isFlying;
                // 剥除 [BOSS]/[ELITE] 前缀，保留基础类型名
                string nameToStore = fullName;
                if (isElite || isBoss)
                {
                    int spaceIdx = fullName.IndexOf(' ');
                    nameToStore = (spaceIdx > 0) ? fullName.Substring(spaceIdx + 1) : fullName;
                }
                int sepIdx = nameToStore.IndexOf('L');
                EnemyTypeName[entityId] = (sepIdx > 0) ? nameToStore.Substring(0, sepIdx) : nameToStore;
            }

            // H-race fix: lock Add to match Remove in DestroyEntity which uses lock(activeIdsLock)
            lock (activeIdsLock) { _activeEnemyIds.Add(entityId); _enemyIndexInList[entityId] = _activeEnemyIds.Count - 1; }
            return entityId;
        }

        // ==================== 敌人基础属性访问 ====================

        public float GetEnemyHealth(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return 0f;
            return EnemyHealth[enemyId];
        }

        public void SetEnemyHealth(int enemyId, float health)
        {
            if (!IsValidEntity(enemyId)) return;
            EnemyHealth[enemyId] = health;
        }

        public float GetEnemyMaxHealth(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return 0f;
            return EnemyMaxHealth[enemyId];
        }

        public float GetEnemyArmor(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return 0f;
            return EnemyArmor[enemyId];
        }

        public void SetEnemyArmor(int enemyId, float armor)
        {
            if (!IsValidEntity(enemyId)) return;
            EnemyArmor[enemyId] = armor;
        }

        /// <summary>
        /// Applies damage to an enemy, with shield absorbing damage before it reaches health.
        /// </summary>
        public void ApplyEnemyDamage(int enemyId, float damage)
        {
            if (!IsValidEntity(enemyId)) return;
            if (damage <= 0f) return;

            float shield = EnemyShield[enemyId];
            if (shield <= 0f)
            {
                EnemyHealth[enemyId] -= damage;
                return;
            }
            if (shield >= damage)
            {
                EnemyShield[enemyId] = shield - damage;
                return;
            }
            float remaining = damage - shield;
            EnemyShield[enemyId] = 0f;
            EnemyHealth[enemyId] -= remaining;
        }

        public float GetEnemyMoveSpeed(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return 0f;
            return EnemyMoveSpeed[enemyId];
        }

        public float GetEnemyDamage(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return 0f;
            return EnemyDamage[enemyId];
        }

        public int GetEnemyGoldReward(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return 0;
            return EnemyGoldReward[enemyId];
        }

        // ==================== CC (Crowd Control) helpers ====================
        /// <summary>Returns true if the enemy is currently stunned.</summary>
        public bool IsEnemyStunned(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return false;
            // Primary check: duration-based stun (set by ApplyEnemyStun, decremented by EnemyMovementSystem.Update)
            if (EnemyStunDurationLeft[enemyId] > 0f) return true;
            // Fallback: legacy flag (set by external systems, cleared by EnemyMovementSystem.SetTurn)
            return EnemyStunFlag[enemyId];
        }

        /// <summary>Applies a stun to the enemy for the current frame. Stun clears automatically at start of each frame via SetTurnCCFlags.</summary>
        public void ApplyStun(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return;
            EnemyStunFlag[enemyId] = true;
        }

        /// <summary>Applies a slow to the enemy. factor is a multiplier (e.g. 0.5 = 50% speed). Duration in turns tracked by EnemySlowDurationLeft.</summary>
        public void ApplySlow(int enemyId, float factor, int duration)
        {
            if (!IsValidEntity(enemyId)) return;
            if (factor <= 0f || factor >= 1f) return; // only valid slow factors

            float baseSpeed = EnemyMoveSpeedBase[enemyId];
            if (baseSpeed <= 0f) baseSpeed = EnemyMoveSpeed[enemyId];

            EnemySlowFactor[enemyId] = factor;
            EnemyMoveSpeed[enemyId] = baseSpeed * factor;
            EnemySlowDurationLeft[enemyId] = duration;
        }

        /// <summary>Clears slow effect and restores original speed.</summary>
        public void ClearSlow(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return;
            if (EnemySlowFactor[enemyId] <= 0f) return; // no slow active

            float baseSpeed = EnemyMoveSpeedBase[enemyId];
            if (baseSpeed > 0f)
                EnemyMoveSpeed[enemyId] = baseSpeed;
            EnemySlowFactor[enemyId] = 0f;
        }

        /// <summary>Applies stun to the enemy for `duration` turns. Stored in EnemyStunDurationLeft (not EnemyStunFlag) so it persists across frames.</summary>
        public void ApplyEnemyStun(int enemyId, int duration)
        {
            if (!IsValidEntity(enemyId)) return;
            // Use duration-based stun so it survives the EnemyMovementSystem.SetTurn() clear
            if (duration > EnemyStunDurationLeft[enemyId])
                EnemyStunDurationLeft[enemyId] = duration;
            // Also set legacy flag for backward compat with IsEnemyStunned fallback
            EnemyStunFlag[enemyId] = true;
        }

        /// <summary>Applies freeze to the enemy for `duration` turns. Alias for ApplyEnemyStun — freeze uses the same stun infrastructure.</summary>
        public void ApplyEnemyFreeze(int enemyId, int duration)
        {
            ApplyEnemyStun(enemyId, duration);
        }

        /// <summary>Returns true if the enemy is currently frozen. Alias for IsEnemyStunned — freeze shares the stun mechanism.</summary>
        public bool IsEnemyFrozen(int enemyId)
        {
            return IsEnemyStunned(enemyId);
        }

        /// <summary>Applies slow to the enemy. factor is a speed multiplier (e.g. 0.5 = 50% speed). Duration in turns tracked by EnemySlowDurationLeft.</summary>
        public void ApplyEnemySlow(int enemyId, float factor, int duration)
        {
            if (!IsValidEntity(enemyId)) return;
            if (factor <= 0f || factor >= 1f) return;
            // Take the stronger slow if stacking
            if (factor < EnemySlowFactor[enemyId])
            {
                EnemySlowFactor[enemyId] = factor;
                float baseSpeed = EnemyMoveSpeedBase[enemyId];
                if (baseSpeed <= 0f) baseSpeed = EnemyMoveSpeed[enemyId];
                EnemyMoveSpeed[enemyId] = baseSpeed * factor;
                EnemySlowDurationLeft[enemyId] = duration;
            }
            else if (EnemySlowFactor[enemyId] <= 0f)
            {
                EnemySlowFactor[enemyId] = factor;
                float baseSpeed = EnemyMoveSpeedBase[enemyId];
                if (baseSpeed <= 0f) baseSpeed = EnemyMoveSpeed[enemyId];
                EnemyMoveSpeed[enemyId] = baseSpeed * factor;
                EnemySlowDurationLeft[enemyId] = duration;
            }
        }

        /// <summary>Clears slow effect on enemy and restores original speed.</summary>
        public void ClearEnemySlow(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return;
            if (EnemySlowFactor[enemyId] <= 0f) return;
            float baseSpeed = EnemyMoveSpeedBase[enemyId];
            if (baseSpeed > 0f)
                EnemyMoveSpeed[enemyId] = baseSpeed;
            EnemySlowFactor[enemyId] = 0f;
        }

        /// <summary>Clears wound slow effect on enemy and restores speed from wound state.</summary>
        public void ClearEnemyWound(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return;
            if (!EnemyIsWounded[enemyId]) return;
            EnemyIsWounded[enemyId] = false;
            // Restore from base speed (wound applied additional multiplier on top of base)
            float baseSpeed = EnemyMoveSpeedBase[enemyId];
            if (baseSpeed > 0f)
                EnemyMoveSpeed[enemyId] = baseSpeed;
        }

        /// <summary>Applies knockback force to an enemy. Force is applied instantly and consumed in ResolveKnockback.</summary>
        public void ApplyEnemyKnockback(int enemyId, float force)
        {
            if (!IsValidEntity(enemyId)) return;
            if (force <= 0f) return;
            // Add to existing force (in case multiple towers hit simultaneously)
            EnemyKnockbackForceLeft[enemyId] += force;
        }

        /// <summary>
        /// Decrement EnemySlowDurationLeft for all active enemies and clear expired slow effects.
        /// Called once per turn from EnemyMovementSystem.SetTurn() to expire tower-slow durations.
        /// Uses _activeEnemyIds which is safe for read during the serial phase.
        /// </summary>
        public void DecrementEnemySlowDurations()
        {
            for (int i = 0; i < _activeEnemyIds.Count; i++)
            {
                int enemyId = _activeEnemyIds[i];
                float dur = EnemySlowDurationLeft[enemyId];
                if (dur > 0f)
                {
                    EnemySlowDurationLeft[enemyId] = dur - 1f;
                    if (EnemySlowDurationLeft[enemyId] <= 0f)
                    {
                        EnemySlowDurationLeft[enemyId] = 0f;
                        ClearEnemySlow(enemyId);
                    }
                }
            }
        }

        // ==================== 敌人 AI 组件访问 ====================

        public string GetEnemyAIAction(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return "";
            return EnemyAIAction[enemyId];
        }

        public string GetEnemyTypeName(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return "";
            return EnemyTypeName[enemyId] ?? "";
        }

        public void SetEnemyAIAction(int enemyId, string action)
        {
            if (!IsValidEntity(enemyId)) return;
            EnemyAIAction[enemyId] = action ?? "";
        }

        public int GetEnemyAIChargeCounter(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return 0;
            return EnemyAIChargeCounter[enemyId];
        }

        public void SetEnemyAIChargeCounter(int enemyId, int counter)
        {
            if (!IsValidEntity(enemyId)) return;
            EnemyAIChargeCounter[enemyId] = counter;
        }

        public int GetEnemyAILastAttackTurn(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return 0;
            return EnemyAILastAttackTurn[enemyId];
        }

        public void SetEnemyAILastAttackTurn(int enemyId, int turn)
        {
            if (!IsValidEntity(enemyId)) return;
            EnemyAILastAttackTurn[enemyId] = turn;
        }

        public EnemyActionType GetEnemyActionEnum(int enemyId)
        {
            if (!IsValidEntity(enemyId)) return EnemyActionType.None;
            return EnemyActionEnum[enemyId];
        }

        public void SetEnemyActionEnum(int enemyId, EnemyActionType action)
        {
            if (!IsValidEntity(enemyId)) return;
            EnemyActionEnum[enemyId] = action;
        }

        // ==================== 路径修改塔访问方法 ====================

        /// <summary>
        /// Activate a path modifier at the given position with the specified influence zone.
        /// </summary>
        public void ActivatePathModifier(int modifierId, float x, float y, float radius, int targetPathId, int ownerId, float turnsRemaining = 0f)
        {
            if (modifierId < 0 || modifierId >= MAX_ENTITIES) return;
            PathModifierX[modifierId] = x;
            PathModifierY[modifierId] = y;
            PathModifierRadius[modifierId] = radius;
            PathModifierTargetPathId[modifierId] = targetPathId;
            PathModifierOwnerId[modifierId] = ownerId;
            PathModifierTurnsRemaining[modifierId] = turnsRemaining;
            PathModifierActive[modifierId] = true;
            _activePathModifierCount++;
        }

        /// <summary>
        /// Deactivate a path modifier by its entity ID.
        /// </summary>
        public void DeactivatePathModifier(int modifierId)
        {
            if (modifierId < 0 || modifierId >= MAX_ENTITIES) return;
            if (!PathModifierActive[modifierId]) return;
            PathModifierActive[modifierId] = false;
            _activePathModifierCount = System.Math.Max(0, _activePathModifierCount - 1);
        }

        /// <summary>
        /// Returns true if the given position is within the influence zone of any active path modifier.
        /// </summary>
        public bool IsWithinAnyPathModifier(float x, float y)
        {
            for (int i = 0; i < MAX_ENTITIES; i++)
            {
                if (!PathModifierActive[i]) continue;
                float dx = PathModifierX[i] - x;
                float dy = PathModifierY[i] - y;
                float distSq = dx * dx + dy * dy;
                float radius = PathModifierRadius[i];
                if (distSq <= radius * radius)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Get the target path ID for the first active path modifier that covers the given position.
        /// Returns -1 if no active modifier covers the position.
        /// </summary>
        public int GetPathModifierTargetPathId(float x, float y)
        {
            for (int i = 0; i < MAX_ENTITIES; i++)
            {
                if (!PathModifierActive[i]) continue;
                float dx = PathModifierX[i] - x;
                float dy = PathModifierY[i] - y;
                float distSq = dx * dx + dy * dy;
                float radius = PathModifierRadius[i];
                if (distSq <= radius * radius)
                    return PathModifierTargetPathId[i];
            }
            return -1;
        }

        /// <summary>
        /// Returns the modifier ID of the first active path modifier covering the given position, or -1.
        /// </summary>
        public int GetPathModifierIdAt(float x, float y)
        {
            for (int i = 0; i < MAX_ENTITIES; i++)
            {
                if (!PathModifierActive[i]) continue;
                float dx = PathModifierX[i] - x;
                float dy = PathModifierY[i] - y;
                float distSq = dx * dx + dy * dy;
                float radius = PathModifierRadius[i];
                if (distSq <= radius * radius)
                    return i;
            }
            return -1;
        }
    }
}
