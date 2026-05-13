using System;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 金币系统 - 负责管理金币获取和花费
    /// 金币奖励逻辑已迁移到 PlayerTowerAttackSystem 和 TowerAttackSystem
    /// </summary>
    public class GoldSystem
    {
        private ComponentStore store;
        private IRenderer renderer;

        public GoldSystem(ComponentStore store, IRenderer renderer)
        {
            this.store = store;
            this.renderer = renderer;
        }

        public void SetTurn(int turn)
        {
            // Gold rewards for kills are handled by PlayerTowerAttackSystem and TowerAttackSystem.
        }

        public void Update()
        {
            // Gold reward logic moved to PlayerTowerAttackSystem and TowerAttackSystem
        }

        public bool SpendGold(float amount)
        {
            float currentGold = store.GetPlayerGold(store.PlayerEntityId);
            if (currentGold < amount)
            {
                renderer.Log($"[GOLD] 金币不足，需要 {amount}，当前只有 {currentGold}");
                return false;
            }
            store.SetPlayerGold(store.PlayerEntityId, currentGold - amount);
            renderer.Log($"[GOLD] 花费 {amount} 金币");
            return true;
        }
    }
}
