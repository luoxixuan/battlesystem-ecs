using System;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 塔被动资源生产系统 — 管理经济塔（金矿/银行/炼金塔）的金币产出。
    /// 
    /// 设计：
    /// - 经济塔不参与攻击（跳过 TowerAttackSystem 的攻击逻辑）
    /// - 每帧根据 TowerGoldPerSecond 累加产金量
    /// - 产金在帧末统一发放给玩家（与其他金币来源一致）
    /// - 可配置：每秒产金量、是否受位置/邻接影响
    /// </summary>
    public class TowerIncomeSystem
    {
        private ComponentStore store;
        private IRenderer renderer;
        private readonly int playerId;

        public TowerIncomeSystem(ComponentStore store, IRenderer renderer, int playerId = 0)
        {
            this.store = store;
            this.renderer = renderer;
            this.playerId = playerId;
        }

        public void SetTurn()
        {
            // Nothing to cache per-turn for this system
        }

        /// <summary>
        /// 每帧处理所有经济塔的金币产出。
        /// 累加每个经济塔的产金，帧末统一发放。
        /// </summary>
        public void Update(float deltaTime)
        {
            var activeTowerIds = store.ActiveTowerIds;
            float totalGold = 0f;

            for (int i = 0; i < activeTowerIds.Count; i++)
            {
                int towerId = activeTowerIds[i];
                if (!store.TowerActive[towerId])
                    continue;

                // Only process income towers
                if (!store.TowerIsIncomeTower[towerId])
                    continue;

                float goldPerSecond = store.TowerGoldPerSecond[towerId];
                if (goldPerSecond <= 0f)
                    continue;

                // Accumulate gold production
                totalGold += goldPerSecond * deltaTime;
            }

            // Award gold at end of frame (if any accumulated)
            if (totalGold > 0f)
            {
                float currentGold = store.GetPlayerGold(playerId);
                store.SetPlayerGold(playerId, currentGold + totalGold);
                // Note: don't log every frame — only log significant milestones
                // renderer.Log($"[INCOME] +{totalGold:F2} gold from income towers");
            }
        }

        /// <summary>
        /// Get total gold per second from all income towers.
        /// Used for UI display.
        /// </summary>
        public float GetTotalIncomePerSecond()
        {
            var activeTowerIds = store.ActiveTowerIds;
            float total = 0f;

            for (int i = 0; i < activeTowerIds.Count; i++)
            {
                int towerId = activeTowerIds[i];
                if (!store.TowerActive[towerId])
                    continue;

                if (store.TowerIsIncomeTower[towerId])
                {
                    total += store.TowerGoldPerSecond[towerId];
                }
            }

            return total;
        }
    }
}