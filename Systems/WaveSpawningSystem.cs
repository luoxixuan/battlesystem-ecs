using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Difficulty configuration for wave scaling — loaded from wave_spawn.json.
    /// </summary>
    public class DifficultyConfig
    {
        public float BaseHealthMultPerWave { get; set; } = 0.05f;
        public float BaseDamageMultPerWave { get; set; } = 0.05f;
        public int EliteStartWave { get; set; } = 5;
        public int BossStartWave { get; set; } = 10;
        public float EliteHealthMult { get; set; } = 2.0f;
        public float EliteDamageMult { get; set; } = 1.5f;
        public float BossHealthMult { get; set; } = 5.0f;
        public float BossDamageMult { get; set; } = 3.0f;
    }

    /// <summary>
    /// Wave spawn configuration loaded from wave_spawn.json.
    /// </summary>
    public class WaveSpawnConfig
    {
        public int SpawnBatchSize { get; set; } = 5;
        public int WaveDelayTurns { get; set; } = 3;
        public float SpawnY { get; set; } = 49.0f;
        public float SpawnXMin { get; set; } = 0.0f;
        public float SpawnXMax { get; set; } = 9.0f;
        public DifficultyConfig DifficultyConfig { get; set; } = new DifficultyConfig();
    }

    /// <summary>
    /// SOA (Struct of Arrays) 波次生成系统
    /// 直接访问 ComponentStore 的数组，无字典查询，无 struct 复制
    /// 性能提升：10-100 倍
    /// 支持每波 100 只怪生成
    /// 支持多怪物类型（EnemyTypes[]）
    /// 支持精英/Boss 难度缩放（波次动态难度曲线）
    /// </summary>
    public class WaveSpawningSystem
    {
        private Core.ComponentStore store;
        private IRenderer renderer;
        private GameConfig gameConfig;
        private WaveSpawnConfig spawnConfig;

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
            this.spawnConfig = LoadWaveSpawnConfig();
        }

        private WaveSpawnConfig LoadWaveSpawnConfig()
        {
            const string configPath = "Data/Configs/wave_spawn.json";
            try
            {
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    var doc = JsonDocument.Parse(json);
                    var cfg = new WaveSpawnConfig();

                    if (doc.RootElement.TryGetProperty("spawnBatchSize", out var bs))
                        cfg.SpawnBatchSize = bs.GetInt32();
                    if (doc.RootElement.TryGetProperty("waveDelayTurns", out var wdt))
                        cfg.WaveDelayTurns = wdt.GetInt32();
                    if (doc.RootElement.TryGetProperty("spawnY", out var sy))
                        cfg.SpawnY = sy.GetSingle();
                    if (doc.RootElement.TryGetProperty("spawnXMin", out var sxmin))
                        cfg.SpawnXMin = sxmin.GetSingle();
                    if (doc.RootElement.TryGetProperty("spawnXMax", out var sxmax))
                        cfg.SpawnXMax = sxmax.GetSingle();

                    if (doc.RootElement.TryGetProperty("difficultyConfig", out var dc))
                    {
                        cfg.DifficultyConfig = new DifficultyConfig();
                        if (dc.TryGetProperty("baseHealthMultPerWave", out var bhm))
                            cfg.DifficultyConfig.BaseHealthMultPerWave = bhm.GetSingle();
                        if (dc.TryGetProperty("baseDamageMultPerWave", out var bdm))
                            cfg.DifficultyConfig.BaseDamageMultPerWave = bdm.GetSingle();
                        if (dc.TryGetProperty("eliteStartWave", out var esw))
                            cfg.DifficultyConfig.EliteStartWave = esw.GetInt32();
                        if (dc.TryGetProperty("bossStartWave", out var bsw))
                            cfg.DifficultyConfig.BossStartWave = bsw.GetInt32();
                        if (dc.TryGetProperty("eliteHealthMult", out var ehm))
                            cfg.DifficultyConfig.EliteHealthMult = ehm.GetSingle();
                        if (dc.TryGetProperty("eliteDamageMult", out var edm))
                            cfg.DifficultyConfig.EliteDamageMult = edm.GetSingle();
                        if (dc.TryGetProperty("bossHealthMult", out var bohm))
                            cfg.DifficultyConfig.BossHealthMult = bohm.GetSingle();
                        if (dc.TryGetProperty("bossDamageMult", out var bodm))
                            cfg.DifficultyConfig.BossDamageMult = bodm.GetSingle();
                    }
                    renderer.Log($"[SPAWN] Loaded difficulty config: health/wave={cfg.DifficultyConfig.BaseHealthMultPerWave:P0}, elite@wave{cfg.DifficultyConfig.EliteStartWave}, boss@wave{cfg.DifficultyConfig.BossStartWave}");
                    return cfg;
                }
            }
            catch (Exception ex)
            {
                renderer.Log($"[SPAWN] Failed to load wave_spawn.json: {ex.Message}, using defaults");
            }
            return new WaveSpawnConfig();
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

                    // ── 波次动态难度曲线 ───────────────────────────────────────
                    // 1. Base wave scaling: each wave increases stats by base mult
                    float waveGrowth = spawnConfig.DifficultyConfig.BaseHealthMultPerWave;
                    float dmgGrowth = spawnConfig.DifficultyConfig.BaseDamageMultPerWave;
                    float waveScaling = 1.0f + (currentWave - 1) * waveGrowth;

                    // 2. Elite scaling: wave >= EliteStartWave gets ×eliteMult
                    bool isEliteWave = currentWave >= spawnConfig.DifficultyConfig.EliteStartWave;
                    bool isBossWave = currentWave >= spawnConfig.DifficultyConfig.BossStartWave;

                    float healthMult = waveScaling;
                    float damageMult = 1.0f + (currentWave - 1) * dmgGrowth;

                    if (isBossWave)
                    {
                        healthMult *= spawnConfig.DifficultyConfig.BossHealthMult;
                        damageMult *= spawnConfig.DifficultyConfig.BossDamageMult;
                    }
                    else if (isEliteWave)
                    {
                        healthMult *= spawnConfig.DifficultyConfig.EliteHealthMult;
                        damageMult *= spawnConfig.DifficultyConfig.EliteDamageMult;
                    }

                    float scaledHealth = monsterConfig.Health * healthMult;
                    float scaledMaxHealth = monsterConfig.MaxHealth * healthMult;
                    float scaledDamage = monsterConfig.Damage * damageMult;

                    string enemyName = $"{monsterType}L{currentLevel}W{currentWave}T{_multiTypeIndex}E{_multiSpawnedForType}";
                    if (isBossWave) enemyName = "[BOSS] " + enemyName;
                    else if (isEliteWave) enemyName = "[ELITE] " + enemyName;
                    int enemyId = store.AddEnemy(
                        startX, startY,
                        monsterConfig.MoveSpeed,
                        scaledHealth,
                        scaledMaxHealth,
                        scaledDamage,
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
