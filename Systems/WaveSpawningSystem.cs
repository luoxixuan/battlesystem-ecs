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
    /// 支持多怪物类型（EnemyTypes[]）
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
        private readonly object _spawnRandomLock = new object();

        // Multi-type support
        private string[] _multiTypes = Array.Empty<string>();
        private int[] _multiCounts = Array.Empty<int>();
        private int _multiTypeIndex = 0;
        private int _multiSpawnedForType = 0;

        /// <summary>
        /// Fired when a wave completes (not level complete).
        /// </summary>
        public event System.Action OnWaveComplete;

        /// <summary>
        /// Fired when a new wave starts (before enemies spawn that wave).
        /// </summary>
        public event System.Action OnWaveStart;

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
            ClearMultiTypeState();
            OnWaveStart?.Invoke();
        }

        private void ClearMultiTypeState()
        {
            _multiTypes = Array.Empty<string>();
            _multiCounts = Array.Empty<int>();
            _multiTypeIndex = 0;
            _multiSpawnedForType = 0;
        }

        private Random GetSpawnRandom()
        {
            if (_spawnRandom != null) return _spawnRandom;
            lock (_spawnRandomLock)
            {
                _spawnRandom ??= new Random();
                return _spawnRandom;
            }
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

            // Lazy-init multi-type state at wave start
            if (_multiTypes.Length == 0)
            {
                InitMultiTypeState(waveConfig);
                enemiesSpawnedInWave = 0;
                OnWaveStart?.Invoke();
            }

            if (enemiesSpawnedInWave < waveConfig.EnemyCount)
            {
                Random random = GetSpawnRandom();

                // Batch spawn 5 enemies
                for (int i = 0; i < 5; i++)
                {
                    // Advance to next type if current is exhausted
                    while (_multiTypeIndex < _multiTypes.Length)
                    {
                        if (_multiSpawnedForType >= _multiCounts[_multiTypeIndex])
                        {
                            _multiTypeIndex++;
                            _multiSpawnedForType = 0;
                        }
                        else
                        {
                            break;
                        }
                    }
                    if (_multiTypeIndex >= _multiTypes.Length)
                        break;

                    string monsterType = _multiTypes[_multiTypeIndex];
                    var monsterConfig = gameConfig.GetMonsterConfig(monsterType);
                    if (monsterConfig == null)
                    {
                        renderer.Log("[SPAWN] Monster type '" + monsterType + "' not found!");
                        _multiTypeIndex++;
                        _multiSpawnedForType = 0;
                        continue;
                    }

                    float startX = (float)random.Next(0, 10);
                    float startY = 19f;

                    string enemyName = $"{monsterType}L{currentLevel}W{currentWave}T{_multiTypeIndex}E{_multiSpawnedForType}";
                    int enemyId = store.AddEnemy(
                        startX, startY,
                        monsterConfig.MoveSpeed,
                        monsterConfig.Health,
                        monsterConfig.MaxHealth,
                        monsterConfig.Damage,
                        monsterConfig.GoldReward,
                        currentWave,
                        enemyName,
                        monsterConfig.Armor
                    );
                    if (enemyId < 0)
                    {
                        renderer.Log($"[SPAWN] Failed to spawn enemy (entity pool exhausted)");
                        continue;
                    }
                    store.SetEntityName(enemyId, enemyName);
                    store.EnemyBehaviorTree[enemyId] = gameConfig.GetCachedBehaviorTree(monsterType);
                    _multiSpawnedForType++;
                    enemiesSpawnedInWave++;
                    totalEnemiesSpawned++;
                }

                renderer.Log($"[SPAWN] Spawned {enemiesSpawnedInWave} enemies (batch 5) for Wave {currentWave}");
            }
            else
            {
                renderer.Log($"[SPAWN] Wave {currentWave} complete! Spawned {enemiesSpawnedInWave} enemies (batch 100 per wave)");
                ClearMultiTypeState();
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

        private void InitMultiTypeState(WaveConfig waveConfig)
        {
            // Check if EnemyTypes[] is configured
            if (waveConfig.EnemyTypes != null && waveConfig.EnemyTypes.Count > 0)
            {
                int count = waveConfig.EnemyTypes.Count;
                _multiTypes = new string[count];
                _multiCounts = new int[count];
                for (int i = 0; i < count; i++)
                {
                    var entry = waveConfig.EnemyTypes[i];
                    _multiTypes[i] = entry.MonsterType ?? "";
                    _multiCounts[i] = entry.Count;
                }
            }
            else
            {
                // Fallback: single type from MonsterType field
                _multiTypes = new string[] { waveConfig.MonsterType ?? "Normal" };
                _multiCounts = new int[] { waveConfig.EnemyCount };
            }
            _multiTypeIndex = 0;
            _multiSpawnedForType = 0;
        }
    }
}
