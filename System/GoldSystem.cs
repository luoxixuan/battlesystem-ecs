using System;
using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 金币系统 - SOA (Struct of Arrays) 优化
    /// 管理金币获取和花费
    /// </summary>
    public class GoldSystem
    {
        private Core.ComponentStore store;
        private IRenderer renderer;

        public GoldSystem(Core.ComponentStore store, IRenderer renderer)
        {
            this.store = store;
            this.renderer = renderer;
        }

        public void SetTurn(int turn)
        {
            // Gold rewards for kills are handled by PlayerTowerAttackSystem and TowerAttackSystem.
            // This system no longer scans the enemy list per turn.
        }

        /// <summary>
        /// 更新金币系统
        /// </summary>
        public void Update()
        {
            // Gold reward logic moved to PlayerTowerAttackSystem and TowerAttackSystem
            // to avoid double-counting and redundant scanning.
        }

        /// <summary>
        /// 花费金币
        /// </summary>
        public bool SpendGold(float amount)
        {
            float currentGold = store.GetPlayerGold(store.PlayerEntityId);
            if (currentGold < amount)
            {
                renderer.Log($"[GOLD] 金币不足，需要 {amount} 金币，当前只有 {currentGold}");
                return false;
            }

            store.SetPlayerGold(store.PlayerEntityId, currentGold - amount);
            return true;
        }

        /// <summary>
        /// 获取当前金币
        /// </summary>
        public float GetCurrentGold()
        {
            return store.GetPlayerGold(store.PlayerEntityId);
        }

        /// <summary>
        /// 增加金币
        /// </summary>
        public void AddGold(float amount)
        {
            float currentGold = store.GetPlayerGold(store.PlayerEntityId);
            store.SetPlayerGold(store.PlayerEntityId, currentGold + amount);
            renderer.Log($"[GOLD] 获得 {amount} 金币，当前金币: {currentGold + amount:F1}");
        }
    }
}