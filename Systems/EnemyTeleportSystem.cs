using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 敌人传送/跃迁系统：支持多种传送类型
    /// - Blink: 直接传送到预设坐标
    /// - Portal entry/exit: 进入传送门从出口出现
    /// - Random phase ahead: 随机跳到路径前方
    /// - Retreat to player: 传送到玩家附近（突袭型）
    /// </summary>
    public class EnemyTeleportSystem
    {
        private readonly ComponentStore store;
        private readonly int playerId;

        // Teleport type constants
        public const int TYPE_NONE = 0;
        public const int TYPE_BLINK = 1;           // 直接传送到目标坐标
        public const int TYPE_PORTAL_ENTRY = 2;    // 进入传送门（由 PortalSystem 处理出口）
        public const int TYPE_RANDOM_PHASE_AHEAD = 3; // 随机跳到路径前方某点
        public const int TYPE_RETREAT_TO_PLAYER = 4; // 传送到玩家附近

        // Default cooldown in turns (0 = teleport is ready)
        private const float DEFAULT_COOLDOWN = 0f;

        public EnemyTeleportSystem(ComponentStore store, int playerId)
        {
            this.store = store;
            this.playerId = playerId;
        }

        public void SetTurn(int turn)
        {
            // No per-turn state to cache — fields are accessed directly
        }

        /// <summary>
        /// Decrement teleport cooldowns for all active enemies.
        /// Call once per turn before movement.
        /// </summary>
        public void Update()
        {
            var activeEnemyIds = store.GetCachedActiveEnemyIds();
            int count = activeEnemyIds.Count;

            Parallel.For(0, count, ParallelOptionsCache.HotPath, i =>
            {
                int enemyId = activeEnemyIds[i];
                if (!store.EnemyActive[enemyId])
                    return;

                // Decrement cooldown if > 0
                float cd = store.EnemyTeleportCooldown[enemyId];
                if (cd > 0f)
                {
                    store.EnemyTeleportCooldown[enemyId] = cd - 1f;
                }
            });
        }

        /// <summary>
        /// Execute teleport for a single enemy — reads EnemyTeleportType/DestinationX/Y,
        /// validates cooldown, applies position warp, resets cooldown.
        /// Returns true if teleport was executed this frame.
        /// </summary>
        public bool ExecuteTeleport(int enemyId)
        {
            if (!store.EnemyActive[enemyId])
                return false;

            int teleportType = store.EnemyTeleportType[enemyId];
            if (teleportType == TYPE_NONE)
                return false;

            // Cooldown must be 0 (ready)
            if (store.EnemyTeleportCooldown[enemyId] > 0f)
                return false;

            float destX = store.EnemyTeleportDestinationX[enemyId];
            float destY = store.EnemyTeleportDestinationY[enemyId];

            switch (teleportType)
            {
                case TYPE_BLINK:
                    // Instant warp to destination
                    store.PositionX[enemyId] = destX;
                    store.PositionY[enemyId] = destY;
                    break;

                case TYPE_PORTAL_ENTRY:
                    // PortalSystem reads PortalEntryX/Y to find exit;
                    // Here we snap to portal entry position (handled by PortalSystem)
                    store.PositionX[enemyId] = destX;
                    store.PositionY[enemyId] = destY;
                    break;

                case TYPE_RANDOM_PHASE_AHEAD:
                    // Warp to a random point ahead in the path (destX = ahead offset range)
                    float aheadY = store.PositionY[enemyId] + (float)(store.Determinism.NextDouble() * destX * 2 - destX);
                    float maxY = 20f; // map height limit
                    if (aheadY > maxY) aheadY = maxY;
                    if (aheadY < 0) aheadY = 0;
                    store.PositionY[enemyId] = aheadY;
                    // X stays same for forward phase, or scatter slightly
                    float scatterX = (float)(store.Determinism.NextDouble() * 2 - 1); // ±1 grid unit
                    store.PositionX[enemyId] = store.PositionX[enemyId] + scatterX;
                    break;

                case TYPE_RETREAT_TO_PLAYER:
                    // Warp to near player position (destX = max retreat distance)
                    float px = store.PositionX[playerId];
                    float py = store.PositionY[playerId];
                    float retreatRange = destX; // use destination X as range parameter
                    float angle = (float)(store.Determinism.NextDouble() * Math.PI * 2);
                    float dist = (float)(store.Determinism.NextDouble() * retreatRange);
                    float newX = px + (float)Math.Cos(angle) * dist;
                    float newY = py + (float)Math.Sin(angle) * dist;
                    // Clamp to map bounds
                    if (newX < 0) newX = 0;
                    if (newX > 9) newX = 9;
                    if (newY < 0) newY = 0;
                    if (newY > 20) newY = 20;
                    store.PositionX[enemyId] = newX;
                    store.PositionY[enemyId] = newY;
                    break;

                default:
                    return false;
            }

            // Reset teleport state after execution
            store.EnemyTeleportType[enemyId] = TYPE_NONE;
            store.EnemyTeleportCooldown[enemyId] = 0f;
            return true;
        }

        /// <summary>
        /// Queue a teleport for an enemy — sets type + destination + cooldown.
        /// This is called by enemy abilities or AI when they decide to teleport.
        /// </summary>
        public void QueueTeleport(int enemyId, int teleportType, float destX, float destY, float cooldownTurns)
        {
            if (!store.EnemyActive[enemyId])
                return;
            store.EnemyTeleportType[enemyId] = teleportType;
            store.EnemyTeleportDestinationX[enemyId] = destX;
            store.EnemyTeleportDestinationY[enemyId] = destY;
            store.EnemyTeleportCooldown[enemyId] = cooldownTurns;
        }

        /// <summary>
        /// Check if an enemy is currently able to teleport (type set and cooldown = 0).
        /// </summary>
        public bool CanTeleportNow(int enemyId)
        {
            return store.EnemyTeleportType[enemyId] != TYPE_NONE
                && store.EnemyTeleportCooldown[enemyId] <= 0f;
        }
    }
}
