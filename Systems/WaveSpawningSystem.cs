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
        public float ArmorGrowthPerWave { get; set; } = 0.02f;
        public float SpeedGrowthPerWave { get; set; } = 0.01f;
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
        private readonly EnemyAffixSystem _enemyAffixSystem;

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

        public WaveSpawningSystem(Core.ComponentStore store, IRenderer renderer, GameConfig gameConfig, EnemyAffixSystem enemyAffixSystem = null)
        {
            this.store = store;
            this.renderer = renderer;
            this.gameConfig = gameConfig;
            this.spawnConfig = LoadWaveSpawnConfig();
            this._enemyAffixSystem = enemyAffixSystem;
        }

        private AscensionSystem _ascensionSystem;
        public void SetAscensionSystem(AscensionSystem ascensionSystem)
        {
            _ascensionSystem = ascensionSystem;
        }

        private AdaptiveDifficultySystem _adaptiveDifficulty;
        public void SetAdaptiveDifficulty(AdaptiveDifficultySystem adaptiveDifficulty)
        {
            _adaptiveDifficulty = adaptiveDifficulty;
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
                        if (dc.TryGetProperty("armorGrowthPerWave", out var agp))
                            cfg.DifficultyConfig.ArmorGrowthPerWave = agp.GetSingle();
                        if (dc.TryGetProperty("speedGrowthPerWave", out var sgp))
                            cfg.DifficultyConfig.SpeedGrowthPerWave = sgp.GetSingle();
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

        /// <summary>
        /// Injects extra enemies mid-wave (for Ambush random event).
        /// Adds count enemies immediately without resetting multi-type state.
        /// </summary>
        public void InjectExtraEnemies(int count)
        {
            if (count <= 0) return;
            var random = GetSpawnRandom();
            for (int i = 0; i < count; i++)
            {
                float startX = (float)random.Next(0, 10);
                float startY = 19f;
                float waveScaling = 1.0f + (currentWave - 1) * spawnConfig.DifficultyConfig.BaseHealthMultPerWave;
                float dmgGrowth = spawnConfig.DifficultyConfig.BaseDamageMultPerWave;
                float damageMult = 1.0f + (currentWave - 1) * dmgGrowth;
                bool isEliteWave = currentWave >= spawnConfig.DifficultyConfig.EliteStartWave;
                bool isBossWave = currentWave >= spawnConfig.DifficultyConfig.BossStartWave;

                float healthMult = waveScaling;
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

                var monsterConfig = gameConfig.GetMonsterConfig("Normal");
                if (monsterConfig == null) continue;

                float scaledHealth = monsterConfig.Health * healthMult;
                float scaledMaxHealth = monsterConfig.MaxHealth * healthMult;
                float scaledDamage = monsterConfig.Damage * damageMult;
                float scaledArmor = monsterConfig.Armor * (1.0f + (currentWave - 1) * spawnConfig.DifficultyConfig.ArmorGrowthPerWave);
                float scaledSpeed = monsterConfig.MoveSpeed * (1.0f + (currentWave - 1) * spawnConfig.DifficultyConfig.SpeedGrowthPerWave);

                string enemyName = $"[AMBUSH] NormalL{currentLevel}W{currentWave}";
                int enemyId = store.AddEnemy(startX, startY, scaledSpeed, scaledHealth, scaledMaxHealth, scaledDamage, monsterConfig.GoldReward, currentWave, enemyName, scaledArmor, monsterConfig.Shield, monsterConfig.MagicResist);
                if (enemyId < 0) continue;
                store.SetEntityName(enemyId, enemyName);
                store.EnemyBehaviorTree[enemyId] = gameConfig.GetCachedBehaviorTree("Normal");
                totalEnemiesSpawned++;
            }
        }

        /// <summary>
        /// Injects a mini-boss mid-wave (for BossRush random event).
        /// </summary>
        public void InjectMiniBoss()
        {
            float startX = (float)GetSpawnRandom().Next(0, 10);
            float startY = 19f;
            var monsterConfig = gameConfig.GetMonsterConfig("Normal");
            if (monsterConfig == null) return;

            float waveScaling = 1.0f + (currentWave - 1) * spawnConfig.DifficultyConfig.BaseHealthMultPerWave;
            float dmgGrowth = spawnConfig.DifficultyConfig.BaseDamageMultPerWave;
            float scaledHealth = monsterConfig.Health * waveScaling * spawnConfig.DifficultyConfig.BossHealthMult * 0.5f;
            float scaledMaxHealth = monsterConfig.MaxHealth * waveScaling * spawnConfig.DifficultyConfig.BossHealthMult * 0.5f;
            float scaledDamage = monsterConfig.Damage * (1.0f + (currentWave - 1) * dmgGrowth) * spawnConfig.DifficultyConfig.BossDamageMult * 0.5f;
            float scaledArmor = monsterConfig.Armor;
            float scaledSpeed = monsterConfig.MoveSpeed;

            string enemyName = $"[BOSS RUSH] NormalL{currentLevel}W{currentWave}";
            int enemyId = store.AddEnemy(startX, startY, scaledSpeed, scaledHealth, scaledMaxHealth, scaledDamage, monsterConfig.GoldReward * 3, currentWave, enemyName, scaledArmor, monsterConfig.Shield, monsterConfig.MagicResist);
            if (enemyId < 0) return;
            store.SetEntityName(enemyId, enemyName);
            store.EnemyBehaviorTree[enemyId] = gameConfig.GetCachedBehaviorTree("Normal");
            store.EnemyIsElite[enemyId] = true;
            totalEnemiesSpawned++;
        }

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

        /// <summary>
        /// Returns the currently active WaveConfig for this wave.
        /// If a branch was selected, this reflects the AppliedBranchOption.
        /// </summary>
        public WaveConfig GetCurrentWaveConfig()
        {
            var levelConfig = gameConfig.GetLevelConfig(currentLevel);
            if (levelConfig == null) return null;
            int idx = currentWave - 1;
            if (idx < 0 || idx >= levelConfig.Waves.Count) return null;
            return levelConfig.Waves[idx];
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

            if (enemiesSpawnedInWave < waveConfig.GetTotalEnemyCount())
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

                    // 3. Armor & Speed scaling per wave
                    float armorGrowth = spawnConfig.DifficultyConfig.ArmorGrowthPerWave;
                    float speedGrowth = spawnConfig.DifficultyConfig.SpeedGrowthPerWave;
                    float armorMult = 1.0f + (currentWave - 1) * armorGrowth;
                    float speedMult = 1.0f + (currentWave - 1) * speedGrowth;

                    // 4. Wave rhythm — Breather eases the wave, Surge/Climax push it
                    // (Count is already scaled via WaveConfig.GetEnemyCountForType; here we scale stats.)
                    float rhythmStatMult = waveConfig.GetRhythmStatMult();
                    if (rhythmStatMult != 1.0f)
                    {
                        healthMult *= rhythmStatMult;
                        damageMult *= rhythmStatMult;
                        armorMult *= rhythmStatMult;
                        speedMult *= rhythmStatMult;
                    }

                    float scaledHealth = monsterConfig.Health * healthMult;
                    float scaledMaxHealth = monsterConfig.MaxHealth * healthMult;
                    float scaledDamage = monsterConfig.Damage * damageMult;
                    float scaledArmor = monsterConfig.Armor * armorMult;
                    float scaledMagicResist = monsterConfig.MagicResist * armorMult;
                    float scaledSpeed = monsterConfig.MoveSpeed * speedMult;

                    // ── 动态难度缩放（Adaptive Difficulty）──
                    // Apply adaptive difficulty multiplier to enemy stats
                    if (_adaptiveDifficulty != null)
                    {
                        float adaptMult = _adaptiveDifficulty.GetDifficultyMult(0); // player 0
                        scaledHealth *= adaptMult;
                        scaledMaxHealth *= adaptMult;
                        scaledDamage *= adaptMult;
                        scaledSpeed *= adaptMult; // speed scales slightly
                    }

                    string enemyName = $"{monsterType}L{currentLevel}W{currentWave}T{_multiTypeIndex}E{_multiSpawnedForType}";
                    if (isBossWave) enemyName = "[BOSS] " + enemyName;
                    else if (isEliteWave) enemyName = "[ELITE] " + enemyName;
                    int enemyId = store.AddEnemy(
                        startX, startY,
                        scaledSpeed,
                        scaledHealth,
                        scaledMaxHealth,
                        scaledDamage,
                        monsterConfig.GoldReward,
                        currentWave,
                        enemyName,
                        scaledArmor,
                        monsterConfig.Shield,
                        scaledMagicResist
                    );
                    if (enemyId < 0)
                    {
                        renderer.Log($"[SPAWN] Failed to spawn enemy (entity pool exhausted)");
                        continue;
                    }
                    store.SetEntityName(enemyId, enemyName);
                    store.EnemyBehaviorTree[enemyId] = gameConfig.GetCachedBehaviorTree(monsterType);

                    // Initialize flying enemy properties from monster config
                    if (monsterConfig.IsFlying)
                    {
                        store.EnemyIsFlying[enemyId] = true;
                        store.EnemyFlightHeight[enemyId] = monsterConfig.FlightHeight;
                        store.EnemyCanLand[enemyId] = monsterConfig.CanLand;
                    }

                    // Initialize fission capability (split-on-death)
                    int fissionDefId = gameConfig.GetFissionDefIdBySourceType(monsterType);
                    store.EnemyFissionDefId[enemyId] = fissionDefId;
                    store.EnemyFissionGeneration[enemyId] = 0;

                    // Initialize morph capability (transform mid-wave)
                    int morphDefId = gameConfig.GetMorphDefIdBySourceType(monsterType);
                    store.EnemyMorphDefId[enemyId] = morphDefId;
                    store.EnemyIsMorphed[enemyId] = false;
                    store.EnemyMorphTriggered[enemyId] = false;

                    // Initialize gold-stealing thief properties
                    if (monsterConfig.IsThief)
                    {
                        store.EnemyCanStealGold[enemyId] = true;
                        store.EnemyStealAmount[enemyId] = monsterConfig.StealAmount;
                        store.EnemyGoldOnReturn[enemyId] = monsterConfig.GoldOnReturn;
                    }

                    // Initialize burrow/underground enemy properties
                    if (monsterConfig.CanBurrow)
                    {
                        store.EnemyIsBurrowed[enemyId] = false;
                        store.EnemyBurrowTimer[enemyId] = 0f;
                        store.EnemyBurrowCooldown[enemyId] = 0f; // ready to burrow (cooldown starts on first emerge)
                        store.EnemyBurrowCooldownRef[enemyId] = monsterConfig.BurrowCooldown;
                        store.EnemyBurrowSpeedMult[enemyId] = monsterConfig.BurrowSpeedMult;
                        store.EnemyBurrowEmergeDamage[enemyId] = monsterConfig.BurrowEmergeDamage;
                        store.EnemyBurrowRadius[enemyId] = monsterConfig.BurrowRadius;
                    }
                    else
                    {
                        // Mark as non-burrowable (cooldown = -1)
                        store.EnemyBurrowCooldown[enemyId] = -1f;
                        store.EnemyBurrowCooldownRef[enemyId] = -1f;
                    }

                    // Initialize necromancer enemy properties
                    if (monsterConfig.IsNecromancer)
                    {
                        store.EnemyCanResurrect[enemyId] = true;
                        store.EnemyResurrectRange[enemyId] = monsterConfig.ResurrectRange;
                        store.EnemyResurrectCooldown[enemyId] = 0f; // ready to resurrect
                        store.EnemyResurrectCooldownRef[enemyId] = monsterConfig.ResurrectCooldown;
                        store.EnemyResurrectHpMult[enemyId] = monsterConfig.ResurrectHpMult;
                        store.EnemyMaxResurrectCount[enemyId] = monsterConfig.MaxResurrectCount;
                        store.EnemyResurrectCorpseAgeLimit[enemyId] = monsterConfig.ResurrectCorpseAgeLimit > 0f
                            ? monsterConfig.ResurrectCorpseAgeLimit
                            : ComponentStore.MAX_CORPSE_AGE_SEC;
                        store.EnemyIsReanimated[enemyId] = false;
                        store.EnemyOwnerId[enemyId] = -1;
                    }
                    else
                    {
                        store.EnemyCanResurrect[enemyId] = false;
                        store.EnemyResurrectRange[enemyId] = 0f;
                        store.EnemyResurrectCooldown[enemyId] = 0f;
                        store.EnemyResurrectCooldownRef[enemyId] = 0f;
                        store.EnemyResurrectHpMult[enemyId] = 0f;
                        store.EnemyMaxResurrectCount[enemyId] = 0;
                        store.EnemyResurrectCorpseAgeLimit[enemyId] = 0f;
                        store.EnemyIsReanimated[enemyId] = false;
                        store.EnemyOwnerId[enemyId] = -1;
                    }

                    // Assign per-enemy affixes (1-3 random affixes from EnemyAffixSystem)
                    _enemyAffixSystem?.AssignAffixesAtSpawn(enemyId, scaledMaxHealth);

                    // Apply ascension/difficulty modifier scaling
                    _ascensionSystem?.ApplyEnemyScaling(enemyId);

                    // Initialize N-hit shield from monster config
                    if (monsterConfig.HitShieldCount > 0f)
                    {
                        store.EnemyHitShieldCount[enemyId] = monsterConfig.HitShieldCount;
                        store.EnemyHitShieldMax[enemyId] = monsterConfig.HitShieldCount;
                        store.EnemyHitShieldRegenInterval[enemyId] = monsterConfig.HitShieldRegenInterval;
                        store.EnemyHitShieldTimer[enemyId] = monsterConfig.HitShieldRegenInterval > 0f
                            ? monsterConfig.HitShieldRegenInterval : 0f;
                    }

                    // Initialize path deviation (lateral X drift) from monster config.
                    // Type 0 = no deviation (default). Type 1 = sine wave. Type 2 = random per turn.
                    if (monsterConfig.PathDeviationType != 0 && monsterConfig.PathDeviationAmplitude > 0f)
                    {
                        store.EnemyPathDeviationType[enemyId] = monsterConfig.PathDeviationType;
                        store.EnemyPathDeviationAmplitude[enemyId] = monsterConfig.PathDeviationAmplitude;
                        // Per-enemy random phase/seed to de-synchronize the wave (no synchronised bobbing)
                        var rng = new System.Random(enemyId * 7919 + currentWave * 31);
                        store.EnemyPathDeviationPhase[enemyId] = (float)(rng.NextDouble() * Math.PI * 2.0);
                        store.EnemyPathDeviationSeed[enemyId] = rng.Next(1, int.MaxValue);
                    }

                    // Initialize stat-drain fields from monster config. Drains are gated on
                    // DrainRatio > 0 (otherwise the enemy has no drain ability and stays at 0).
                    // All three fields are zero-initialized in AddEnemy() for default case.
                    if (monsterConfig.DrainRatio > 0f && monsterConfig.DrainRadius > 0f)
                    {
                        store.EnemyDrainRatio[enemyId] = monsterConfig.DrainRatio;
                        store.EnemyDrainRadius[enemyId] = monsterConfig.DrainRadius;
                        store.EnemyDrainRate[enemyId] = monsterConfig.DrainRate;
                    }

                    // Initialize elemental shield from monster config (only meaningful if Shield > 0 and ShieldElement is set)
                    if (monsterConfig.Shield > 0f && !string.IsNullOrEmpty(monsterConfig.ShieldElement))
                    {
                        store.EnemyShieldType[enemyId] = ParseElementType(monsterConfig.ShieldElement);
                        store.EnemyShieldWeakMult[enemyId] = monsterConfig.ShieldWeakMult;
                        store.EnemyShieldResistMult[enemyId] = monsterConfig.ShieldResistMult;
                        store.EnemyShieldBreakReaction[enemyId] = string.IsNullOrEmpty(monsterConfig.ShieldBreakReaction)
                            ? ElementType.None
                            : ParseElementType(monsterConfig.ShieldBreakReaction);
                        store.EnemyShieldBreakElementDuration[enemyId] = monsterConfig.ShieldBreakElementDuration;
                    }

                    _multiSpawnedForType++;
                    enemiesSpawnedInWave++;
                    totalEnemiesSpawned++;

                    // Initialize boss phase fields if this is a boss enemy
                    if (isBossWave && monsterConfig.IsBoss)
                    {
                        // Build CSV string from Phases thresholds: "0.75,0.50,0.25"
                        if (monsterConfig.Phases != null && monsterConfig.Phases.Count > 0)
                        {
                            var thresholds = new System.Text.StringBuilder();
                            for (int p = 0; p < monsterConfig.Phases.Count; p++)
                            {
                                if (p > 0) thresholds.Append(',');
                                thresholds.Append(monsterConfig.Phases[p].Threshold.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            }
                            store.EnemyPhaseThresholds[enemyId] = thresholds.ToString();
                        }
                        // Initialize enrage timer from config
                        if (monsterConfig.Enrage != null && monsterConfig.Enrage.EnrageAfterSeconds > 0f)
                        {
                            store.EnemyEnrageTimer[enemyId] = monsterConfig.Enrage.EnrageAfterSeconds;
                        }
                    }
                }

                renderer.Log($"[SPAWN] Spawned {enemiesSpawnedInWave} enemies (batch 5) for Wave {currentWave}");
            }
            else
            {
                renderer.Log($"[SPAWN] Wave {currentWave} complete! Spawned {enemiesSpawnedInWave} enemies (batch 100 per wave)");
                ClearMultiTypeState();
                enemiesSpawnedInWave = 0;
                currentWave++;

                // Trigger adaptive difficulty evaluation (before OnWaveComplete so new difficulty is ready for next wave)
                _adaptiveDifficulty?.OnWaveComplete(0); // player 0

                OnWaveComplete?.Invoke();

                if (currentWave > levelConfig.Waves.Count)
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
                    // Apply rhythm scaling to per-type counts (Breather ×0.6, Surge ×1.3, Climax ×1.5).
                    // GetEnemyCountForType returns the scaled count with a floor of 1.
                    _multiCounts[i] = waveConfig.GetEnemyCountForType(entry.MonsterType ?? "");
                }
            }
            else
            {
                // Fallback: single type from MonsterType field
                _multiTypes = new string[] { waveConfig.MonsterType ?? "Normal" };
                _multiCounts = new int[] { waveConfig.GetEnemyCountForType(waveConfig.MonsterType ?? "Normal") };
            }
            _multiTypeIndex = 0;
            _multiSpawnedForType = 0;
        }

        /// <summary>
        /// Parse a string (from JSON config) into an ElementType enum value.
        /// Returns ElementType.None for null/empty/unknown strings (safe default).
        /// </summary>
        private static ElementType ParseElementType(string s)
        {
            if (string.IsNullOrEmpty(s)) return ElementType.None;
            // Case-insensitive match against enum names
            if (Enum.TryParse<ElementType>(s, ignoreCase: true, out var result))
                return result;
            return ElementType.None;
        }
    }
}
