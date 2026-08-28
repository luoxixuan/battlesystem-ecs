using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 地图固定传送门系统。
    /// 敌人进入入口后从出口点涌出，用于路径分叉、地形穿越等玩法。
    /// 传送门通过 Data/Configs/portals.json 配置（运行时从 GameConfigLoader 加载）。
    /// </summary>
    public class PortalSystem
    {
        private readonly ComponentStore store;
        private readonly int playerId;
        private readonly Random _rand = Rng.Shared;

        // Portal data (loaded from config)
        private PortalDef[] _portalDefs;
        private int _portalCount;

        public PortalSystem(ComponentStore store, int playerId)
        {
            this.store = store;
            this.playerId = playerId;
        }

        /// <summary>
        /// Load portal definitions from config (called once at game start).
        /// </summary>
        public void LoadPortals(PortalDef[] portalDefs)
        {
            _portalDefs = portalDefs;
            _portalCount = portalDefs != null ? portalDefs.Length : 0;
        }

        public void SetTurn(int turn)
        {
        }

        /// <summary>
        /// Update: check enemies standing on portal entries, trigger teleport.
        /// Call after EnemyMovementSystem.Update() each frame.
        /// </summary>
        public void Update()
        {
            if (_portalDefs == null || _portalCount == 0)
                return;

            var activeEnemyIds = store.GetCachedActiveEnemyIds();
            int count = activeEnemyIds.Count;

            Parallel.For(0, count, ParallelOptionsCache.HotPath, i =>
            {
                int enemyId = activeEnemyIds[i];
                if (!store.EnemyActive[enemyId])
                    return;

                float ex = store.PositionX[enemyId];
                float ey = store.PositionY[enemyId];

                // Check each portal for proximity to entry
                for (int p = 0; p < _portalCount; p++)
                {
                    PortalDef portal = _portalDefs[p];
                    float dx = ex - portal.EntryX;
                    float dy = ey - portal.EntryY;
                    float distSq = dx * dx + dy * dy;

                    if (distSq <= portal.TriggerRadius * portal.TriggerRadius)
                    {
                        // Trigger teleport to exit
                        store.PositionX[enemyId] = portal.ExitX;
                        store.PositionY[enemyId] = portal.ExitY;
                        return;
                    }
                }
            });
        }

        /// <summary>
        /// Get the exit position for a given portal index.
        /// Returns (exitX, exitY) or (0,0) if invalid portal.
        /// </summary>
        public (float x, float y) GetPortalExitPosition(int portalIndex)
        {
            if (_portalDefs == null || portalIndex < 0 || portalIndex >= _portalCount)
                return (0f, 0f);
            var portal = _portalDefs[portalIndex];
            return (portal.ExitX, portal.ExitY);
        }

        public int PortalCount => _portalCount;
    }

    /// <summary>
    /// Portal definition loaded from portals.json.
    /// </summary>
    public struct PortalDef
    {
        public int Id;
        public float EntryX;
        public float EntryY;
        public float ExitX;
        public float ExitY;
        public float TriggerRadius;  // how close enemy must be to trigger
        public int MaxThroughCount;  // max enemies that can pass through (0 = unlimited)
        public int CurrentThroughCount;
    }
}
