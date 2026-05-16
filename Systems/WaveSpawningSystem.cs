using System;
using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// SOA (Struct of Arrays) 波次生成系统
    /// 直接访问 ComponentStore 的数组，无字典查询，无 struct 复制
    /// 性能提升：10-100 倍
    /// 支持每波 100 只怪生成
    /// </summary>
    public class WaveSpawningSystem
    {
        private Core.ComponentStore store;
        private IRenderer renderer;
        private GameConfig gameConfig;

        private int currentWave = 1;
        private int currentLevel = 1;
        private int enemiesSpawnedInWave = 0;
        private int totalEnemiesSpawned = 0;
        private Random _spawnRandom;

        /// <summary>
        /// Fired when a wave completes (not level complete).
        /// </summary>
        public event System.Action OnWaveComplete;

        public WaveSpawningSystem(Core.ComponentStore store, IRenderer renderer, GameConfig gameConfig)
        {
            this.store = store;
            this.renderer = renderer;
            this.gameConfig = gameConfig;
        }

        public int GetCurrentWave() => currentWave;
        public int GetCurrentLevel() => currentLevel;
        public int GetTotalEnemiesSpawned() => totalEnemiesSpawned;

        public void SetLevel(int levelNumber)
        {
            currentLevel = levelNumber;
            currentWave = 1;
            enemiesSpawnedInWave = 0;
            totalEnemiesSpawned = 0;
        }

        public void Update()
        {
            var levelConfig = gameConfig.GetLevelConfig(currentLevel);
            if (levelConfig == null)
            {
                renderer.Log("[SPAWN] Level " + currentLevel + " not found!");
                return;
            }

            if (currentWave - 1 >= levelConfig.Waves.Count)
            {
                renderer.Log("[SPAWN] Level " + currentLevel + " complete!");
                renderer.Log("[SPAWN] Total enemies spawned: " + totalEnemiesSpawned);
                return;
            }

            var waveConfig = levelConfig.Waves[currentWave - 1];
            if (waveConfig == null)
            {
                renderer.Log("[SPAWN] Wave " + currentWave + " not found!");
                return;
            }

            if (enemiesSpawnedInWave < waveConfig.EnemyCount)
            {
                // 批量生成敌人：每波 100 只怪
                var monsterConfig = gameConfig.GetMonsterConfig(waveConfig.MonsterType);
                if (monsterConfig == null)
                {
                    renderer.Log("[SPAWN] Monster type '" + waveConfig.MonsterType + "' not found!");
                    return;
                }

                _spawnRandom ??= new Random();
                Random random = _spawnRandom;

                // 批量生成 5 个敌人
                for (int i = 0; i < 5; i++)
                {
                    // 计算随机位置（X：0-9，Y：19）
                    float startX = (float)random.Next(0, 10);
                    float startY = 19f;  // 放在地图中间位置

                    string enemyName = $"{waveConfig.MonsterType}L{currentLevel}W{currentWave}E{enemiesSpawnedInWave + i}";
                    // SOA: 直接数组访问，无字典查询，无 struct 复制
                    int enemyId = store.AddEnemy(
                        startX, startY,
                        monsterConfig.MoveSpeed,
                        monsterConfig.Health,
                        monsterConfig.MaxHealth,
                        monsterConfig.Damage,
                        monsterConfig.GoldReward,
                        currentWave,
                        enemyName
                    );
                    if (enemyId < 0)
                    {
                        renderer.Log($"[SPAWN] Failed to spawn enemy (entity pool exhausted)");
                        continue;
                    }
                    store.SetEntityName(enemyId, enemyName);
                    // Cache the behavior tree on the enemy — O(1) array access per frame instead of Dictionary+string lookup
                    store.EnemyBehaviorTree[enemyId] = gameConfig.GetCachedBehaviorTree(waveConfig.MonsterType);
                    enemiesSpawnedInWave++;
                }

                totalEnemiesSpawned += 5;

                renderer.Log($"[SPAWN] Spawned {enemiesSpawnedInWave} enemies (batch 5) for Wave {currentWave}");
            }
            else
            {
                // Wave complete
                renderer.Log($"[SPAWN] Wave {currentWave} complete! Spawned {enemiesSpawnedInWave} enemies (batch 100 per wave)");
                enemiesSpawnedInWave = 0;
                currentWave++;
                OnWaveComplete?.Invoke();

                if (currentWave > levelConfig.WaveCount)
                {
                    renderer.Log($"[SPAWN] Level {currentLevel} complete! Total enemies spawned: {totalEnemiesSpawned}");
                    currentLevel++;
                    currentWave = 1;
                }
            }
        }
    }
}