using System;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 游戏状态系统 - SOA (Struct of Arrays) 优化
    /// 管理游戏状态
    /// </summary>
    public class GameStateSystem
    {
        private Core.ComponentStore store;
        private IRenderer renderer;
        private GameConfig gameConfig;

        public GameStateSystem(Core.ComponentStore store, IRenderer renderer, GameConfig gameConfig)
        {
            this.store = store;
            this.renderer = renderer;
            this.gameConfig = gameConfig;
        }

        /// <summary>
        /// 初始化游戏状态
        /// </summary>
        public void Initialize()
        {
            // 设置初始游戏状态
            store.SetGameStateCurrentWave(store.PlayerEntityId, 1);
            store.SetGameStateTotalWaves(store.PlayerEntityId, gameConfig.Levels.Count);
            store.SetGameStateIsGameRunning(store.PlayerEntityId, true);
            store.SetGameStatePlayerHealth(store.PlayerEntityId, gameConfig.Player.MaxHealth);
            store.SetGameStatePlayerMaxHealth(store.PlayerEntityId, gameConfig.Player.MaxHealth);

            renderer.Log("[GAME] 游戏状态初始化完成");
            renderer.Log($"[GAME] 当前波次: 1/{gameConfig.Levels.Count}");
            renderer.Log($"[GAME] 玩家生命值: {gameConfig.Player.MaxHealth}/{gameConfig.Player.MaxHealth}");
        }

        /// <summary>
        /// 更新游戏状态
        /// </summary>
        public void Update()
        {
            // 检查游戏是否结束
            if (!store.IsPlayerAlive(store.PlayerEntityId))
            {
                renderer.Log("[GAME] 玩家死亡！游戏结束！");
                store.SetGameStateIsGameRunning(store.PlayerEntityId, false);
            }

            // 检查是否完成所有波次
            int currentWave = store.GetGameStateCurrentWave(store.PlayerEntityId);
            int totalWaves = store.GetGameStateTotalWaves(store.PlayerEntityId);
            if (currentWave > totalWaves && store.GetActiveEnemyCount() == 0)
            {
                renderer.Log("[GAME] 所有波次完成！游戏胜利！");
                store.SetGameStateIsGameRunning(store.PlayerEntityId, false);
            }
        }

        /// <summary>
        /// 获取游戏状态
        /// </summary>
        public bool IsGameRunning()
        {
            return store.GetGameStateIsGameRunning(store.PlayerEntityId);
        }

        /// <summary>
        /// 获取当前波次
        /// </summary>
        public int GetCurrentWave()
        {
            return store.GetGameStateCurrentWave(store.PlayerEntityId);
        }

        /// <summary>
        /// 获取总波次
        /// </summary>
        public int GetTotalWaves()
        {
            return store.GetGameStateTotalWaves(store.PlayerEntityId);
        }

        /// <summary>
        /// 获取玩家生命值
        /// </summary>
        public float GetPlayerHealth()
        {
            return store.GetGameStatePlayerHealth(store.PlayerEntityId);
        }

        /// <summary>
        /// 获取玩家最大生命值
        /// </summary>
        public float GetPlayerMaxHealth()
        {
            return store.GetGameStatePlayerMaxHealth(store.PlayerEntityId);
        }
    }
}