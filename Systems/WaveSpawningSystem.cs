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
    ///
    /// Round 127 Direction 1 — Curve-based scaling. Each `*CurveId` field is an
    /// optional id into the global <see cref="Core.CurveTable"/>. If non-empty,
    /// the curve evaluator is used; if empty, the legacy linear formula
    /// <c>1.0f + (currentWave - 1) * &lt;Growth&gt;</c> is used unchanged. This
    /// keeps every existing JSON config and unit test bit-identical to the
    /// pre-curve codebase while letting designers opt in to richer shapes
    /// (linear / exponential / logarithmic / sigmoid / piecewise) per stat.
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

        // Optional curve ids (Round 127). Empty string = fall back to legacy linear.
        public string HealthCurveId { get; set; } = "";
        public string DamageCurveId { get; set; } = "";
        public string ArmorCurveId { get; set; } = "";
        public string SpeedCurveId { get; set; } = "";
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
        /// Fired when a Breather-rhythm wave completes (fires alongside OnWaveComplete).
        /// Subscribers (GoldSystem, heal handler, CDR handler) apply the post-wave bonus.
        /// </summary>
        public event System.Action<int> OnBreatherWaveComplete;

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
            // Round 127 Dir 1 — lazy-load the global curve registry. Idempotent and
            // thread-safe; a missing curves.json is logged but never throws, so the
            // spawn loop falls back to the legacy linear formulas.
            Core.CurveTable.Load("Data/Configs/curves.json", renderer);
        }

        // ── Round 127 Direction 1 — Curve-based scaling helpers ───────────────
        // These four helpers centralize the "use curve if set, else fall back to
        // legacy linear" branch. They're called once per enemy spawn (NOT on the
        // per-frame hot path), so the cost of an extra dictionary lookup is
        // negligible. Hot-path note: when the curve id is empty AND the legacy
        // wave-1 branch is taken, we get the same `1.0f` the codebase had before
        // — there is zero regression for unmodified configs.
        private float GetHealthMult()
        {
            var id = spawnConfig.DifficultyConfig.HealthCurveId;
            if (!string.IsNullOrEmpty(id))
                return Core.CurveTable.Evaluate(id, currentWave);
            return 1.0f + (currentWave - 1) * spawnConfig.DifficultyConfig.BaseHealthMultPerWave;
        }

        private float GetDamageMult()
        {
            var id = spawnConfig.DifficultyConfig.DamageCurveId;
            if (!string.IsNullOrEmpty(id))
                return Core.CurveTable.Evaluate(id, currentWave);
            return 1.0f + (currentWave - 1) * spawnConfig.DifficultyConfig.BaseDamageMultPerWave;
        }

        private float GetArmorMult()
        {
            var id = spawnConfig.DifficultyConfig.ArmorCurveId;
            if (!string.IsNullOrEmpty(id))
                return Core.CurveTable.Evaluate(id, currentWave);
            return 1.0f + (currentWave - 1) * spawnConfig.DifficultyConfig.ArmorGrowthPerWave;
        }

        private float GetSpeedMult()
        {
            var id = spawnConfig.DifficultyConfig.SpeedCurveId;
            if (!string.IsNullOrEmpty(id))
                return Core.CurveTable.Evaluate(id, currentWave);
            return 1.0f + (currentWave - 1) * spawnConfig.DifficultyConfig.SpeedGrowthPerWave;
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

        // ── Round 120 Direction 3 — Adaptive Spawn Count (Rubber-band Spawn Pacing) ──
        // Multiplier applied to the per-type baseline enemy count at each spawn site.
        // Written by AdaptiveDifficultySystem.OnWaveComplete (1.0 = no scaling, default).
        // The first wave of a level always uses 1.0 (no performance data yet).
        // Clamped by AdaptiveSpawnConfig to [MinSpawnMultiplier, MaxSpawnMultiplier] when written.
        private float _performanceSpawnMultiplier = 1.0f;

        /// <summary>
        /// Read-only view of the current rubber-band spawn multiplier. Public so tests
        /// (and AdaptiveDifficultySystem) can verify the value after OnWaveComplete.
        /// Defaults to 1.0 (no scaling) and resets to 1.0 at the start of every level.
        /// </summary>
        public float PerformanceSpawnMultiplier => _performanceSpawnMultiplier;

        /// <summary>
        /// Sets the rubber-band spawn multiplier. Called by <c>AdaptiveDifficultySystem.OnWaveComplete</c>
        /// after computing the raw kill-vs-expected delta. Clamped to
        /// <c>[AdaptiveSpawnConfig.MinSpawnMultiplier, MaxSpawnMultiplier]</c> on the way in
        /// so a misbehaving caller can't push the value out of range.
        /// </summary>
        public void SetPerformanceSpawnMultiplier(float multiplier)
        {
            if (multiplier < AdaptiveSpawnConfig.MinSpawnMultiplier)
                multiplier = AdaptiveSpawnConfig.MinSpawnMultiplier;
            else if (multiplier > AdaptiveSpawnConfig.MaxSpawnMultiplier)
                multiplier = AdaptiveSpawnConfig.MaxSpawnMultiplier;
            // Snap near-1 values to exactly 1.0 so the hot-path branch stays cheap and test
            // comparisons are exact. Threshold = 1e-4 is well below any meaningful sensitivity
            // product (sensitivity 0.5 × delta 0.0002 = 1e-4 — i.e. a sub-0.02% deviation).
            if (Math.Abs(multiplier - 1.0f) < 1e-4f) multiplier = 1.0f;
            _performanceSpawnMultiplier = multiplier;
        }

        /// <summary>
        /// Applies <see cref="PerformanceSpawnMultiplier"/> to a base count, with a floor of 0
        /// (zero or negative multipliers would still yield 0 enemies, which is a valid no-op).
        /// Centralized here so all three spawn sites share the same rounding policy.
        /// </summary>
        private int ApplySpawnMultiplier(int baseCount)
        {
            if (baseCount <= 0) return 0;
            // Hot-path: multiplier == 1.0 → no scaling. This is the default state between
            // wave 1 start and the first OnWaveComplete call, so it MUST be zero-overhead.
            if (_performanceSpawnMultiplier == 1.0f) return baseCount;
            int scaled = (int)Math.Round(baseCount * _performanceSpawnMultiplier);
            if (scaled < 0) scaled = 0;
            return scaled;
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
                        // Round 127 Dir 1 — optional curve ids. Empty string (the
                        // default) keeps the legacy linear formula. Setting any of
                        // these to a non-empty id from curves.json switches the
                        // corresponding stat to a curve-driven multiplier.
                        if (dc.TryGetProperty("healthCurveId", out var hci))
                            cfg.DifficultyConfig.HealthCurveId = hci.GetString() ?? "";
                        if (dc.TryGetProperty("damageCurveId", out var dci))
                            cfg.DifficultyConfig.DamageCurveId = dci.GetString() ?? "";
                        if (dc.TryGetProperty("armorCurveId", out var aci))
                            cfg.DifficultyConfig.ArmorCurveId = aci.GetString() ?? "";
                        if (dc.TryGetProperty("speedCurveId", out var sci))
                            cfg.DifficultyConfig.SpeedCurveId = sci.GetString() ?? "";
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
            // Round 120 Dir 3 — rubber-band scaling for mid-wave ambush. Guarded by
            // AdaptiveSpawnConfig.ApplyToMidWaveSpawns so designers can opt out without
            // touching the call site. ApplySpawnMultiplier returns the same value when
            // the multiplier is 1.0 (default), so this is zero-overhead in the common case.
            if (AdaptiveSpawnConfig.ApplyToMidWaveSpawns)
            {
                count = ApplySpawnMultiplier(count);
                if (count <= 0) return;
            }
            var random = GetSpawnRandom();
            for (int i = 0; i < count; i++)
            {
                float startX = (float)random.Next(0, 10);
                float startY = 19f;
                // Round 127 Dir 1 — curve-aware multipliers; fall back to legacy linear
                // when no curveId is set (the default for existing configs).
                float waveScaling = GetHealthMult();
                float damageMult = GetDamageMult();
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
                float scaledArmor = monsterConfig.Armor * GetArmorMult();
                float scaledSpeed = monsterConfig.MoveSpeed * GetSpeedMult();

                string enemyName = $"[AMBUSH] NormalL{currentLevel}W{currentWave}";
                int enemyId = store.AddEnemy(startX, startY, scaledSpeed, scaledHealth, scaledMaxHealth, scaledDamage, monsterConfig.GoldReward, currentWave, enemyName, scaledArmor, monsterConfig.Shield, monsterConfig.MagicResist);
                if (enemyId < 0) continue;
                store.SetEntityName(enemyId, enemyName);
                store.SetDamageImmunityMask(enemyId, monsterConfig.ComputeDamageImmunityMask());
                // Elemental Resistance (Round 117): apply per-element fractional reduction.
                store.SetElementalResist(enemyId, monsterConfig.FireResist, monsterConfig.IceResist, monsterConfig.LightningResist, monsterConfig.HolyResist);
                store.SetLastStandConfig(enemyId,
                    monsterConfig.LastStand?.HpFraction ?? 0f,
                    monsterConfig.LastStand?.SpeedMult ?? 1f,
                    monsterConfig.LastStand?.DamageMult ?? 1f);
                store.SetPierceResist(enemyId, monsterConfig.PierceResist, monsterConfig.PierceImmune);
                // Crit Resistance: suppress fraction of incoming crit chance (Boss/Elite = 0.5, normal = 0)
                store.SetCritResistance(enemyId, monsterConfig.CritResist);
                // Deflect Chance: probability of deflecting an incoming projectile (Boss-tier 0.2, normal 0)
                store.SetDeflectChance(enemyId, monsterConfig.DeflectChance);
                // Faction / Infighting (Round 90): 0 = immune, >0 = share with allies
                store.SetFactionId(enemyId, monsterConfig.FactionId);
                if (monsterConfig.FactionId > 0) store.FactionInfightEnabled = 1;
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

            // Round 127 Dir 1 — curve-aware multipliers; fall back to legacy linear
            // when no curveId is set.
            float waveScaling = GetHealthMult();
            float scaledHealth = monsterConfig.Health * waveScaling * spawnConfig.DifficultyConfig.BossHealthMult * 0.5f;
            float scaledMaxHealth = monsterConfig.MaxHealth * waveScaling * spawnConfig.DifficultyConfig.BossHealthMult * 0.5f;
            float scaledDamage = monsterConfig.Damage * GetDamageMult() * spawnConfig.DifficultyConfig.BossDamageMult * 0.5f;
            float scaledArmor = monsterConfig.Armor;
            float scaledSpeed = monsterConfig.MoveSpeed;

            string enemyName = $"[BOSS RUSH] NormalL{currentLevel}W{currentWave}";
            int enemyId = store.AddEnemy(startX, startY, scaledSpeed, scaledHealth, scaledMaxHealth, scaledDamage, monsterConfig.GoldReward * 3, currentWave, enemyName, scaledArmor, monsterConfig.Shield, monsterConfig.MagicResist);
            if (enemyId < 0) return;
            store.SetEntityName(enemyId, enemyName);
            store.SetDamageImmunityMask(enemyId, monsterConfig.ComputeDamageImmunityMask());
            // Elemental Resistance (Round 117): apply per-element fractional reduction.
            store.SetElementalResist(enemyId, monsterConfig.FireResist, monsterConfig.IceResist, monsterConfig.LightningResist, monsterConfig.HolyResist);
            store.SetLastStandConfig(enemyId,
                monsterConfig.LastStand?.HpFraction ?? 0f,
                monsterConfig.LastStand?.SpeedMult ?? 1f,
                monsterConfig.LastStand?.DamageMult ?? 1f);
            store.SetPierceResist(enemyId, monsterConfig.PierceResist, monsterConfig.PierceImmune);
            // Crit Resistance: suppress fraction of incoming crit chance (Boss/Elite = 0.5, normal = 0)
            store.SetCritResistance(enemyId, monsterConfig.CritResist);
            // Deflect Chance: probability of deflecting an incoming projectile (Boss-tier 0.2, normal 0)
            store.SetDeflectChance(enemyId, monsterConfig.DeflectChance);
            // Faction / Infighting (Round 90): 0 = immune, >0 = share with allies
            store.SetFactionId(enemyId, monsterConfig.FactionId);
            if (monsterConfig.FactionId > 0) store.FactionInfightEnabled = 1;
            store.EnemyBehaviorTree[enemyId] = gameConfig.GetCachedBehaviorTree("Normal");
            store.EnemyIsElite[enemyId] = true;
            totalEnemiesSpawned++;
        }

        /// <summary>
        /// Round 119 Dir 3 — Boss phase minion spawn. Spawns up to <paramref name="count"/>
        /// copies of the MonsterTypes[<paramref name="typeId"/>] entry in a 1.5-unit ring
        /// around (centerX, centerY). The ring placement is deterministic per call index (k
        /// * 60deg in [0,360)) so multiple summons from the same phase don't overlap. Skips
        /// silently when: typeId is out of range, MonsterConfig is missing, or AddEnemy
        /// returns -1 (entity pool exhausted). All standard spawn-site initialisation
        /// (DamageImmunity / ElementalResist / FactionId / BehaviorTree / Burrow / Fission /
        /// Morph / Thief / Lifesteal / etc.) is applied just like the regular WavePhase
        /// spawn path so the new minion behaves identically to a wave-spawned copy.
        /// </summary>
        public int SpawnMinionNearPosition(int typeId, int count, float centerX, float centerY)
        {
            // 4-arg legacy form: no boss element affinity = no themed bonus. Delegates to the
            // 6-arg form with bossElementAffinity=0 (None). Round 137 Dir 6.
            return SpawnMinionNearPosition(typeId, count, centerX, centerY, 0);
        }

        /// <summary>
        /// Round 137 Dir 6 — overload that accepts the boss's ElementType int (0=None) for
        /// themed-summon synergy. When bossElementAffinity > 0 AND the spawned minion's
        /// MonsterConfig.ElementAffinity (parsed case-insensitively) matches the boss's
        /// element name, the minion gets a +10% HP bonus (themed resonance). No bonus
        /// when bossElementAffinity == 0 (None) or minion's affinity is empty/unknown.
        /// </summary>
        public int SpawnMinionNearPosition(int typeId, int count, float centerX, float centerY, int bossElementAffinity)
        {
            if (count <= 0) return 0;
            // Round 120 Dir 3 — rubber-band scaling for boss-phase minion summon. Guarded
            // by AdaptiveSpawnConfig.ApplyToMidWaveSpawns. Zero-overhead when multiplier
            // is 1.0 (the default between wave 1 start and the first OnWaveComplete call).
            if (AdaptiveSpawnConfig.ApplyToMidWaveSpawns)
            {
                count = ApplySpawnMultiplier(count);
                if (count <= 0) return 0;
            }
            if (typeId < 0 || typeId >= gameConfig.MonsterTypes.Count) return 0;
            var monsterConfig = gameConfig.GetMonsterConfigByTypeId(typeId);
            if (monsterConfig == null) return 0;

            // Round 127 Dir 1 — curve-aware multipliers; fall back to legacy linear
            // when no curveId is set. The minion path uses the same curve resolution
            // as the regular wave spawn so curve-driven difficulty scales minions too.
            float waveScaling = GetHealthMult();
            bool isBossWave = currentWave >= spawnConfig.DifficultyConfig.BossStartWave;
            float healthMult = waveScaling;
            float damageMult = GetDamageMult();
            if (isBossWave)
            {
                healthMult *= spawnConfig.DifficultyConfig.BossHealthMult * 0.5f; // minions get half-boss HP
                damageMult *= spawnConfig.DifficultyConfig.BossDamageMult * 0.5f;
            }
            float armorMult = GetArmorMult();
            float speedMult = GetSpeedMult();

            float scaledHealth = monsterConfig.Health * healthMult;
            float scaledMaxHealth = monsterConfig.MaxHealth * healthMult;
            // Round 137 Dir 6 — Themed Boss Summon bonus. If the boss declared an element
            // affinity and the minion's MonsterConfig.ElementAffinity matches (case-insensitive
            // string compare against the ElementType name), apply a +10% HP bonus. This is
            // applied AFTER all other health multipliers (wave/difficulty/boss) so the bonus
            // is a clean relative bump. Mismatch or no boss affinity = no bonus (1.0f).
            float themedHpMult = 1.0f;
            if (bossElementAffinity > 0)
            {
                ElementType bossElem = (ElementType)bossElementAffinity;
                string minionAffinity = monsterConfig.ElementAffinity;
                if (!string.IsNullOrEmpty(minionAffinity) &&
                    string.Equals(minionAffinity, bossElem.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    themedHpMult = 1.10f;
                }
            }
            scaledHealth *= themedHpMult;
            scaledMaxHealth *= themedHpMult;
            float scaledDamage = monsterConfig.Damage * damageMult;
            float scaledArmor = monsterConfig.Armor * armorMult;
            float scaledMagicResist = monsterConfig.MagicResist * armorMult;
            float scaledSpeed = monsterConfig.MoveSpeed * speedMult;

            int spawned = 0;
            const float SummonRingRadius = 1.5f;
            for (int k = 0; k < count; k++)
            {
                // Deterministic ring placement: k * 60deg. No RNG so multiple minions from the
                // same phase spread evenly. Within BOSS_PHASE_SUMMON_CAP (8) this covers 360deg.
                float angleRad = (k * 60f) * (float)Math.PI / 180f;
                float spawnX = centerX + SummonRingRadius * (float)Math.Cos(angleRad);
                float spawnY = centerY + SummonRingRadius * (float)Math.Sin(angleRad);

                string enemyName = $"[PHASE-SUMMON] {monsterConfig.Type}L{currentLevel}W{currentWave}#{k}";
                int enemyId = store.AddEnemy(
                    spawnX, spawnY,
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
                if (enemyId < 0) continue; // pool exhausted; bail out for this slot
                store.SetEntityName(enemyId, enemyName);
                store.SetDamageImmunityMask(enemyId, monsterConfig.ComputeDamageImmunityMask());
                store.SetElementalResist(enemyId, monsterConfig.FireResist, monsterConfig.IceResist, monsterConfig.LightningResist, monsterConfig.HolyResist);
                store.SetLastStandConfig(enemyId,
                    monsterConfig.LastStand?.HpFraction ?? 0f,
                    monsterConfig.LastStand?.SpeedMult ?? 1f,
                    monsterConfig.LastStand?.DamageMult ?? 1f);
                store.SetPierceResist(enemyId, monsterConfig.PierceResist, monsterConfig.PierceImmune);
                store.SetCritResistance(enemyId, monsterConfig.CritResist);
                store.SetDeflectChance(enemyId, monsterConfig.DeflectChance);
                store.SetFactionId(enemyId, monsterConfig.FactionId);
                if (monsterConfig.FactionId > 0) store.FactionInfightEnabled = 1;
                store.EnemyBehaviorTree[enemyId] = gameConfig.GetCachedBehaviorTree(monsterConfig.Type);
                if (monsterConfig.IsFlying)
                {
                    store.EnemyIsFlying[enemyId] = true;
                    store.EnemyFlightHeight[enemyId] = monsterConfig.FlightHeight;
                    store.EnemyCanLand[enemyId] = monsterConfig.CanLand;
                }
                int fissionDefId = gameConfig.GetFissionDefIdBySourceType(monsterConfig.Type);
                store.EnemyFissionDefId[enemyId] = fissionDefId;
                store.EnemyFissionGeneration[enemyId] = 0;
                int morphDefId = gameConfig.GetMorphDefIdBySourceType(monsterConfig.Type);
                store.EnemyMorphDefId[enemyId] = morphDefId;
                store.EnemyIsMorphed[enemyId] = false;
                store.EnemyMorphTriggered[enemyId] = false;
                totalEnemiesSpawned++;
                spawned++;
            }
            return spawned;
        }

        public void SetLevel(int levelNumber)
        {
            currentLevel = levelNumber;
            currentWave = 1;
            enemiesSpawnedInWave = 0;
            totalEnemiesSpawned = 0;
            // Round 120 Dir 3 — reset rubber-band multiplier at level start. Each new level
            // gets a fresh slate (no carry-over from previous level's kill performance).
            _performanceSpawnMultiplier = 1.0f;
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
                    //    (Round 127 Dir 1: GetHealthMult/GetDamageMult honor optional
                    //     curveId; if empty, returns the legacy 1 + (wave-1) * growth.)
                    float waveScaling = GetHealthMult();

                    // 2. Elite scaling: wave >= EliteStartWave gets ×eliteMult
                    bool isEliteWave = currentWave >= spawnConfig.DifficultyConfig.EliteStartWave;
                    bool isBossWave = currentWave >= spawnConfig.DifficultyConfig.BossStartWave;

                    float healthMult = waveScaling;
                    float damageMult = GetDamageMult();

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

                    // 3. Armor & Speed scaling per wave (Round 127 Dir 1: curve-aware)
                    float armorMult = GetArmorMult();
                    float speedMult = GetSpeedMult();

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

                    // ── Threat Score 缩放 (Round 99 Direction 5) ──
                    // PlayerRecentDPS feeds back into enemy HP: high DPS → tougher enemies.
                    // Capped by ThreatScoreConfig.MaxThreatMultiplier so the system can never
                    // make enemies weaker than their base (MinThreatMultiplier=1.0f).
                    // O(1) lookup, called once per spawn (not hot-path).
                    {
                        float recentDps = store.PlayerRecentDPS[0]; // single-player game → player 0
                        float threatMult = 1.0f + recentDps * ThreatScoreConfig.ThreatScalingRate;
                        if (threatMult > ThreatScoreConfig.MaxThreatMultiplier)
                            threatMult = ThreatScoreConfig.MaxThreatMultiplier;
                        if (threatMult < ThreatScoreConfig.MinThreatMultiplier)
                            threatMult = ThreatScoreConfig.MinThreatMultiplier;
                        if (threatMult > 1.0f) // skip when no scaling (common case, no overhead)
                        {
                            scaledHealth *= threatMult;
                            scaledMaxHealth *= threatMult;
                        }
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
                    store.SetDamageImmunityMask(enemyId, monsterConfig.ComputeDamageImmunityMask());
                    // Elemental Resistance (Round 117): apply per-element fractional reduction.
                    store.SetElementalResist(enemyId, monsterConfig.FireResist, monsterConfig.IceResist, monsterConfig.LightningResist, monsterConfig.HolyResist);
                    store.SetLastStandConfig(enemyId,
                        monsterConfig.LastStand?.HpFraction ?? 0f,
                        monsterConfig.LastStand?.SpeedMult ?? 1f,
                        monsterConfig.LastStand?.DamageMult ?? 1f);
                    store.SetPierceResist(enemyId, monsterConfig.PierceResist, monsterConfig.PierceImmune);
                    // Crit Resistance: suppress fraction of incoming crit chance (Boss/Elite = 0.5, normal = 0)
                    store.SetCritResistance(enemyId, monsterConfig.CritResist);
                    // Deflect Chance: probability of deflecting an incoming projectile (Boss-tier 0.2, normal 0)
                    store.SetDeflectChance(enemyId, monsterConfig.DeflectChance);
                    // Faction / Infighting (Round 90): 0 = immune, >0 = share with allies
                    store.SetFactionId(enemyId, monsterConfig.FactionId);
                    if (monsterConfig.FactionId > 0) store.FactionInfightEnabled = 1;
                    store.EnemyBehaviorTree[enemyId] = gameConfig.GetCachedBehaviorTree(monsterType);
                    // Round 134 Direction 3 — Boss HP natural regen. Opt-in: 0 = legacy no regen.
                    // PhaseRegenMult is stored as a flat array indexed by phase (max BOSS_PHASE_MAX).
                    // Length-0 array → TickBossRegen falls back to 1.0× per phase.
                    store.EnemyHealthRegenPerSec[enemyId] = monsterConfig.HealthRegenPerSec;
                    if (monsterConfig.PhaseRegenMult != null && monsterConfig.PhaseRegenMult.Length > 0)
                    {
                        int ph = store.EnemyBossPhase[enemyId];
                        if (ph >= 0 && ph < monsterConfig.PhaseRegenMult.Length)
                            store.EnemyHealthRegenMult[enemyId] = monsterConfig.PhaseRegenMult[ph];
                        // else leave at 1.0 (default from AddEnemy)
                    }

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

                    // Initialize Bounty enemy properties (Round 179 Direction 3) — high-value
                    // high-risk target that pays BountyGoldMult × gold on death. The risk is
                    // the player's attention being diverted from the wave while chasing the
                    // bonus. Bounty monsters are NOT tougher; the multiplier is the value.
                    if (monsterConfig.IsBounty)
                    {
                        store.SetEnemyBounty(enemyId, monsterConfig.BountyGoldMult);
                    }

                    // Initialize Phaser enemy properties (Round 181 Direction 9) — periodically
                    // immune to physical damage. Cycle: vulnerable for PhaserInterval seconds,
                    // then immune for PhaserPhaseDuration seconds, then repeat. Magic / True
                    // damage bypass the immunity entirely, so magic-heavy compositions shred
                    // phasers while physical-only compositions have to time their burst windows
                    // to the vulnerable gaps.
                    if (monsterConfig.IsPhaser)
                    {
                        store.SetEnemyPhaser(enemyId, monsterConfig.PhaserInterval, monsterConfig.PhaserPhaseDuration);
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

                    // Initialize leap / jump-attack capability (driven by monsterConfig.Type
                    // archetype, NOT by a dedicated MonsterConfig class field — keeps the
                    // schema additive-free so existing JSON files are unaffected). Default
                    // archetype 0 = no leap ability (zero overhead on the hot path).
                    // Recognized types: "Leaper" -> archetype 1 (short, fast, low damage)
                    //                   "Troll"  -> archetype 2 (long, slow, high damage + stun)
                    // Leap parameters are derived from the monster's base stats (MoveSpeed
                    // scales duration, Damage scales landing AoE, etc.) so per-archetype tuning
                    // is encapsulated here without requiring GameConfig.cs changes.
                    string leaperType = monsterConfig.Type ?? "";
                    if (leaperType == "Leaper")
                    {
                        // Leaping Spider: short 4-cell jump, fast 0.6s flight, modest AoE
                        store.EnemyLeaperArchetype[enemyId] = 1;
                        store.EnemyLeapDistance[enemyId] = 4f;
                        store.EnemyLeapCooldown[enemyId] = 0f; // ready immediately
                        store.EnemyLeapCooldownRef[enemyId] = 6f; // 6-frame cooldown between leaps
                        store.EnemyLeapDuration[enemyId] = 6f; // 6-frame parabola
                        store.EnemyLeapDamage[enemyId] = monsterConfig.Damage * 1.5f;
                        store.EnemyLeapRadius[enemyId] = 1.5f;
                        store.EnemyLeapStunDuration[enemyId] = 0f;
                    }
                    else if (leaperType == "Troll")
                    {
                        // Mountain Troll: long 8-cell jump, slow 1.5s flight, big AoE + stun
                        store.EnemyLeaperArchetype[enemyId] = 2;
                        store.EnemyLeapDistance[enemyId] = 8f;
                        store.EnemyLeapCooldown[enemyId] = 0f; // ready immediately
                        store.EnemyLeapCooldownRef[enemyId] = 12f; // 12-frame cooldown
                        store.EnemyLeapDuration[enemyId] = 15f; // 15-frame parabola (1.5s at 10fps-ish)
                        store.EnemyLeapDamage[enemyId] = monsterConfig.Damage * 3f;
                        store.EnemyLeapRadius[enemyId] = 2.5f;
                        store.EnemyLeapStunDuration[enemyId] = 3f; // 3-frame stun on landing
                    }

                    // Initialize free-roam (off-path) enemy properties (Round 84 Direction 6).
                    // Monsters with Type "FreeRoam" do not follow the waypoint path. Instead
                    // they wander the map freely and chase the nearest tower within aggro
                    // range, or head toward the player base otherwise. The actual movement
                    // steering is done by WanderRoamSystem (target selection) and
                    // EnemyMovementSystem's Wandering action branch (position update).
                    // Setting EnemyIsFreeRoam = true is the single opt-in signal; all other
                    // fields default to safe values (target 0,0 / reroll 0 = reroll on first
                    // frame after spawn).
                    if (leaperType == "FreeRoam")
                    {
                        store.EnemyIsFreeRoam[enemyId] = true;
                        store.EnemyWanderTargetX[enemyId] = 0f;
                        store.EnemyWanderTargetY[enemyId] = 0f;
                        store.EnemyWanderRerollTimer[enemyId] = 0f; // 0 = reroll on first frame
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
                        // Build CSV string from Phases thresholds: "0.75,0.50,0.25" (legacy path)
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
                        // Round 111 Direction 1 — populate the structured phase fields so
                        // EnemyAISystem can (a) trigger phase AbilityId via EnemyAbilitySystem,
                        // (b) apply SpeedMult / DamageMult one-shot, (c) skip already-fired
                        // phases via EnemyPhaseFiredMask bitmask. Capped at BOSS_PHASE_MAX (4);
                        // any extra phases defined in JSON are silently ignored to keep the
                        // SOA arrays small and cache-friendly.
                        int phaseCount = monsterConfig.Phases?.Count ?? 0;
                        if (phaseCount > ComponentStore.BOSS_PHASE_MAX)
                            phaseCount = ComponentStore.BOSS_PHASE_MAX;
                        store.EnemyPhaseCount[enemyId] = phaseCount;
                        if (phaseCount > 0)
                        {
                            for (int ph = 0; ph < phaseCount; ph++)
                            {
                                var phaseDef = monsterConfig.Phases[ph];
                                int idx = ph * ComponentStore.MAX_ENTITIES + enemyId;
                                store.EnemyPhaseThresholdsFlat[idx] = phaseDef.Threshold;
                                // Defaults: 1.0 = no change. Storing the literal config value
                                // (which may be 0 from JSON) is fine because the AI check
                                // compares against 1.0f before applying.
                                store.EnemyPhaseSpeedMults[idx] = phaseDef.SpeedMult > 0f ? phaseDef.SpeedMult : 1f;
                                store.EnemyPhaseDamageMults[idx] = phaseDef.DamageMult > 0f ? phaseDef.DamageMult : 1f;
                                // Pre-store per-phase abilityId directly into 2D array (no CSV, no
                                // per-frame string.Split). Null = no-op; empty string also no-op.
                                store.EnemyPhaseAbilityIdsFlat[ph, enemyId] =
                                    string.IsNullOrEmpty(phaseDef.AbilityId) ? null : phaseDef.AbilityId;
                                // Round 119 Dir 3 — Boss phase minion summon config. Both fields
                                // are 0 by default in BossPhaseDef; SetEnemyPhaseMinion() will treat
                                // (MinionTypeId <= 0, MinionCount <= 0) as "no summon" (writes -1/0).
                                // No need to validate MinionTypeId here — the AI system does the
                                // typeId-vs-MonsterTypes.Count bounds check at fire time.
                                store.SetEnemyPhaseMinion(enemyId, ph, phaseDef.MinionTypeId, phaseDef.MinionCount);
                                // Round 137 Dir 6 — Themed Boss Summon. Parse the string affinity
                                // ("Fire" / "Ice" / "Lightning" / "Poison" / "") to ElementType int.
                                // Empty string → None → no themed bonus. Unknown strings → None
                                // (no match at spawn time, no bonus applied). Pre-storing here
                                // means the AI system reads the int from the SOA without per-frame
                                // string allocation. ParseElementType already exists at the
                                // bottom of this file (returns None for null/empty/unknown).
                                store.SetEnemyPhaseElementAffinity(enemyId, ph,
                                    (int)ParseElementType(phaseDef.BossElementAffinity));
                            }
                        }
                        // Initialize enrage timer from config
                        if (monsterConfig.Enrage != null && monsterConfig.Enrage.EnrageAfterSeconds > 0f)
                        {
                            store.EnemyEnrageTimer[enemyId] = monsterConfig.Enrage.EnrageAfterSeconds;
                        }
                        // Initialize LastStand / DeathRattle config (HP-threshold trigger).
                        // Note: SetLastStandConfig was already called above (along with SetDamageImmunityMask),
                        // so values from monsterConfig.LastStand are already wired into the SOA fields.
                        // The HP-threshold check is performed each frame in EnemyAISystem.
                        // Round 124 — Direction 1: Boss Path Trail AoE. Wire the per-monster
                        // boss-trail config into the enemy's SOA fields. When BossTrailProgressInterval
                        // > 0 AND BossTrailRadius > 0 AND BossTrailDamage > 0, the boss drops one
                        // trail AoE per "Interval" worth of path progress. 0/false = no trail
                        // (zero overhead on the hot path).
                        if (monsterConfig.BossTrailProgressInterval > 0f
                            && monsterConfig.BossTrailRadius > 0f
                            && monsterConfig.BossTrailDamage > 0f)
                        {
                            store.EnemyIsBossTrail[enemyId] = true;
                            store.EnemyBossTrailRadius[enemyId] = monsterConfig.BossTrailRadius;
                            store.EnemyBossTrailDamage[enemyId] = monsterConfig.BossTrailDamage;
                            store.EnemyBossTrailSlow[enemyId] = monsterConfig.BossTrailSlow;
                            store.EnemyBossTrailProgressInterval[enemyId] = monsterConfig.BossTrailProgressInterval;
                            store.EnemyBossTrailLastTriggerProgress[enemyId] = 0f;
                        }
                    }
                }

                renderer.Log($"[SPAWN] Spawned {enemiesSpawnedInWave} enemies (batch 5) for Wave {currentWave}");
            }
            else
            {
                // Capture the rhythm of the wave that is completing right now (BEFORE currentWave++).
                // The wave index is currentWave - 1 because currentWave is 1-based and was set when this wave started.
                // Guard with `>= 0` to defend against any future code path where currentWave could be 0.
                var completedLevelConfig = gameConfig.GetLevelConfig(currentLevel);
                int completedIdx = currentWave - 1;
                WaveConfig completedWaveConfig = (completedLevelConfig != null && completedIdx >= 0 && completedIdx < completedLevelConfig.Waves.Count)
                    ? completedLevelConfig.Waves[completedIdx]
                    : null;
                bool wasBreather = completedWaveConfig != null && completedWaveConfig.GetRhythmEnum() == WaveRhythm.Breather;
                int completedWaveNumber = currentWave;

                renderer.Log($"[SPAWN] Wave {currentWave} complete! Spawned {enemiesSpawnedInWave} enemies (batch 100 per wave)");
                ClearMultiTypeState();
                enemiesSpawnedInWave = 0;
                currentWave++;

                // Trigger adaptive difficulty evaluation (before OnWaveComplete so new difficulty is ready for next wave)
                // Round 120 Dir 3 — pass ExpectedKillCount from the just-completed wave so
                // AdaptiveDifficultySystem can compute the rubber-band spawn multiplier.
                int expectedKills = completedWaveConfig?.ExpectedKillCount ?? 0;
                _adaptiveDifficulty?.OnWaveComplete(0, expectedKills); // player 0

                // Fire Breather event before the generic event so subscribers (gold/heal/CDR) run first
                // and are observable in logs before the next-wave hooks. Always non-null when rhythm == Breather.
                if (wasBreather)
                {
                    OnBreatherWaveComplete?.Invoke(completedWaveNumber);
                }

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
                    // Round 120 Dir 3 — then apply rubber-band multiplier (1.0 = no change).
                    // ApplySpawnMultiplier is the central site so all three spawn paths share policy.
                    _multiCounts[i] = ApplySpawnMultiplier(waveConfig.GetEnemyCountForType(entry.MonsterType ?? ""));
                }
            }
            else
            {
                // Fallback: single type from MonsterType field
                _multiTypes = new string[] { waveConfig.MonsterType ?? "Normal" };
                _multiCounts = new int[] { ApplySpawnMultiplier(waveConfig.GetEnemyCountForType(waveConfig.MonsterType ?? "Normal")) };
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
