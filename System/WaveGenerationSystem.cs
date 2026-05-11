using System;
using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 波次生成系统 - SOA (Struct of Arrays) 优化
    /// 管理敌人的波次生成
    /// </summary>
    public class WaveGenerationSystem
    {
        private static readonly Random _sharedRandom = new Random();

        private Core.ComponentStore store;
        private IRenderer renderer;
        private GameConfig gameConfig;

        private int currentWave = 1;
        private int enemiesSpawnedInWave = 0;
        private int totalEnemiesSpawned = 0;
        private float waveTimer = 0f;
        private float spawnInterval = 0.5f; // 每0.5秒生成一个敌人

        public WaveGenerationSystem(Core.ComponentStore store, IRenderer renderer, GameConfig gameConfig)
        {
            this.store = store;
            this.renderer = renderer;
            this.gameConfig = gameConfig;
        }

        /// <summary>
        /// 设置当前波次
        /// </summary>
        public void SetCurrentWave(int waveNumber)
        {
            currentWave = waveNumber;
            enemiesSpawnedInWave = 0;
            waveTimer = 0f;
            renderer.Log($"[WAVE] 设置当前波次为 {waveNumber}");
        }

        /// <summary>
        /// 更新波次生成
        /// </summary>
        public void Update()
        {
            waveTimer += 1f; // 每回合增加1秒

            // 检查是否需要生成新敌人
            if (waveTimer >= spawnInterval && enemiesSpawnedInWave < 100) // 每波最多100个敌人
            {
                SpawnEnemy();
                waveTimer = 0f;
            }

            // 检查波次是否完成
            if (enemiesSpawnedInWave >= 100 && store.GetActiveEnemyCount() == 0)
            {
                CompleteWave();
            }
        }

        /// <summary>
        /// 生成敌人
        /// </summary>
        private void SpawnEnemy()
        {
            var levelConfig = gameConfig.GetLevelConfig(currentWave);
            if (levelConfig == null)
            {
                renderer.Log($"[SPAWN] 关卡 {currentWave} 配置不存在");
                return;
            }

            var waveConfig = levelConfig.Waves[currentWave - 1];
            if (waveConfig == null)
            {
                renderer.Log($"[SPAWN] 波次 {currentWave} 配置不存在");
                return;
            }

            var monsterConfig = gameConfig.GetMonsterConfig(waveConfig.MonsterType);
            if (monsterConfig == null)
            {
                renderer.Log($"[SPAWN] 怪物类型 '{waveConfig.MonsterType}' 配置不存在");
                return;
            }

            // 在顶部随机位置生成敌人
            float startX = (float)_sharedRandom.Next(0, 10);
            float startY = 19f;

            // 创建敌人实体
            int enemyId = store.AddEnemy(
                startX, startY,
                monsterConfig.MoveSpeed,
                monsterConfig.Health,
                monsterConfig.MaxHealth,
                monsterConfig.Damage,
                monsterConfig.GoldReward,
                currentWave
            );

            store.SetEntityName(enemyId, $"{waveConfig.MonsterType}W{currentWave}E{enemiesSpawnedInWave + 1}");
            enemiesSpawnedInWave++;
            totalEnemiesSpawned++;

            renderer.Log($"[SPAWN] 生成 {waveConfig.MonsterType} 在 ({startX:F0}, {startY:F0})");
        }

        /// <summary>
        /// 完成波次
        /// </summary>
        private void CompleteWave()
        {
            renderer.Log($"[WAVE] 波次 {currentWave} 完成！生成了 {enemiesSpawnedInWave} 个敌人");
            
            // 增加波次
            currentWave++;
            enemiesSpawnedInWave = 0;
            waveTimer = 0f;

            // 检查是否完成所有波次
            var levelConfig = gameConfig.GetLevelConfig(currentWave);
            if (levelConfig == null)
            {
                renderer.Log("[GAME] 所有波次完成！游戏胜利！");
                store.SetGameStateIsGameRunning(store.PlayerEntityId, false);
            }
            else
            {
                renderer.Log($"[WAVE] 开始波次 {currentWave}");
            }
        }

        /// <summary>
        /// 获取当前波次
        /// </summary>
        public int GetCurrentWave()
        {
            return currentWave;
        }

        /// <summary>
        /// 获取已生成的敌人数量
        /// </summary>
        public int GetEnemiesSpawned()
        {
            return enemiesSpawnedInWave;
        }

        /// <summary>
        /// 获取总生成的敌人数量
        /// </summary>
        public int GetTotalEnemiesSpawned()
        {
            return totalEnemiesSpawned;
        }
    }
}