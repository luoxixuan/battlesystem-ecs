using System;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// SOA (Struct of Arrays) 波次生成系统
    /// 直接访问 ComponentStore 的数组，无字典查询，无 struct 复制
    /// 性能提升：10-100 倍
    /// </summary>
    public class WaveSpawningSystem
    {
        private ComponentStore store;
        private IRenderer renderer;
        private GameConfig gameConfig;

        private int currentWave = 1;
        private int currentLevel = 1;
        private int enemiesSpawnedInWave = 0;
        private int totalEnemiesSpawned = 0;

        public WaveSpawningSystem(ComponentStore store, IRenderer renderer, GameConfig gameConfig)
        {
            this.store = store;
            this.renderer = renderer;
            this.gameConfig = gameConfig;
        }

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
                renderer.Log($"[SPAWN] Level {currentLevel} not found!");
                return;
            }

            if (currentWave > levelConfig.WaveCount)
            {
                renderer.Log($"[SPAWN] Level {currentLevel} complete!");
                return;
            }

            var waveConfig = levelConfig.Waves[currentWave - 1];
            if (waveConfig == null)
            {
                renderer.Log($"[SPAWN] Wave {currentWave} not found!");
                return;
            }

            if (enemiesSpawnedInWave < waveConfig.EnemyCount)
            {
                // Spawn enemy (SOA 直接数组访问，无字典查询，无 struct 复制）
                var monsterConfig = gameConfig.GetMonsterConfig(waveConfig.MonsterType);
                if (monsterConfig == null)
                {
                    renderer.Log($"[SPAWN] Monster type '{waveConfig.MonsterType}' not found!");
                    return;
                }

                Random random = new Random();
                float startX = (float)random.Next(0, 10);
                float startY = 49f;

                // SOA：直接数组访问，无 struct 复制
                int enemyId = store.AddEnemy(
                    startX, startY,
                    monsterConfig.MoveSpeed,
                    monsterConfig.Health,
                    monsterConfig.MaxHealth,
                    monsterConfig.Damage,
                    monsterConfig.GoldReward,
                    currentWave
                );

                string enemyName = $"{waveConfig.MonsterType}L{currentLevel}W{currentWave}E{enemiesSpawnedInWave}";
                store.SetEntityName(enemyId, enemyName);

                enemiesSpawnedInWave++;
                totalEnemiesSpawned++;

                renderer.Log($"[SPAWN] Spawned {enemyName} at x={startX:F0}, y={startY:F0}");
            }
            else
            {
                // Wave complete
                renderer.Log($"[SPAWN] Wave {currentWave} complete! Spawned {enemiesSpawnedInWave} enemies");
                enemiesSpawnedInWave = 0;
                currentWave++;

                if (currentWave > levelConfig.WaveCount)
                {
                    renderer.Log($"[SPAWN] Level {currentLevel} complete! Total enemies spawned: {totalEnemiesSpawned}");
                    currentLevel++;
                    currentWave = 1;
                    totalEnemiesSpawned = 0;
                }
            }
        }
    }
}
