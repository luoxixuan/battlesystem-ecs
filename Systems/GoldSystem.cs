using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 金币系统 - 负责管理金币获取和花费
    /// 金币奖励逻辑已迁移到 PlayerTowerAttackSystem 和 TowerAttackSystem
    /// 科技树击杀金币倍率通过 GoldKillMultiplier 同步到 ComponentStore
    /// </summary>
    public class GoldSystem
    {
        private ComponentStore store;
        private IRenderer renderer;
        private TechTreeSystem techTreeSystem;
        private readonly bool hasTechTreeSystem;

        /// <summary>
        /// Full constructor with TechTreeSystem — enables gold-on-kill multiplier sync.
        /// </summary>
        public GoldSystem(ComponentStore store, IRenderer renderer, TechTreeSystem techTreeSystem)
        {
            this.store = store;
            this.renderer = renderer;
            this.techTreeSystem = techTreeSystem;
            this.hasTechTreeSystem = true;
        }

        /// <summary>
        /// Backwards-compatible constructor without TechTreeSystem.
        /// Defaults multiplier to 1.0 (no bonus).
        /// </summary>
        public GoldSystem(ComponentStore store, IRenderer renderer)
        {
            this.store = store;
            this.renderer = renderer;
            this.hasTechTreeSystem = false;
        }

        public void SetTurn(int turn)
        {
            // Gold rewards for kills are handled by PlayerTowerAttackSystem and TowerAttackSystem.
        }

        public void Update()
        {
            // Sync tech tree gold-on-kill multiplier to ComponentStore every frame
            if (hasTechTreeSystem)
            {
                store.GoldKillMultiplier = techTreeSystem.GetGoldOnKillMult();
            }
            else
            {
                store.GoldKillMultiplier = 1.0f;
            }
        }

        /// <summary>
        /// Award gold bonus when a wave completes, applying tech tree wave bonus multiplier.
        /// </summary>
        public void AwardGoldForWave(float baseGold, int playerId)
        {
            if (playerId < 0 || playerId >= 10) return;
            float bonus = 0f;
            if (hasTechTreeSystem)
            {
                bonus = Math.Max(0f, techTreeSystem.GetGoldOnWaveBonus());
            }
            float totalGold = baseGold + bonus;
            if (totalGold > 0f)
            {
                float currentGold = store.GetPlayerGold(playerId);
                store.SetPlayerGold(playerId, currentGold + totalGold);
                store.PlayerWaveCompleteGold[playerId] = totalGold;
                renderer.Log($"[GOLD] Wave complete: +{totalGold} gold (base {baseGold}, bonus {bonus})");
            }
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
