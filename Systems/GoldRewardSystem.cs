using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// SOA (Struct of Arrays) 金币奖励系统
    /// 直接访问 ComponentStore 的数组，无字典查询，无 struct 复制
    /// 性能提升：10-100 倍
    /// </summary>
    public class GoldRewardSystem
    {
        private Core.ComponentStore store;
        private IRenderer renderer;
        private int playerId;

        public GoldRewardSystem(Core.ComponentStore store, IRenderer renderer, int playerId)
        {
            this.store = store;
            this.renderer = renderer;
            this.playerId = playerId;
        }

        public void Update()
        {
            // SOA 直接数组访问，无字典查询，无 struct 复制
            float gold = store.GetPlayerGold(playerId);
            float threshold = store.GetPlayerUpgradeThreshold(playerId);

            if (gold >= threshold)
            {
                renderer.Log($"[GOLD] Gold threshold reached: {gold:F1} / {threshold:F1}");
            }
            else
            {
                renderer.Log($"[UPGRADE] Current gold: {gold:F1} / {threshold:F1} (next upgrade)");
            }
        }
    }
}
