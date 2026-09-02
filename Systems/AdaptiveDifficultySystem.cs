using System;
using BattleSystemECS.Config;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 自适应难度系统：根据玩家表现动态调整波次难度。
    ///
    /// 每波采集的表现信号：
    ///   - 敌人漏过：提高难度
    ///   - 击杀数量：降低难度
    ///   - 剩余金币：效率奖励
    ///   - 剩余生命：未受伤奖励
    ///
    /// 表现指标在波次期间采集，并在波次完成时计算。
    /// 生成端通过 IWaveSpawningPort 读取 AdaptiveDifficultyLevel，以缩放敌人属性。
    ///
    /// 集成点：
    ///   - FrameScheduler.Tick() 在 WavePhase 每回合调用 Update()
    ///   - 生成敌人时由 IWaveSpawningPort 读取难度等级
    ///   - OnWaveComplete 重置本波计数并计算新的难度等级
    /// </summary>
    public class AdaptiveDifficultySystem : global::BattleSystemECS.Content.Contracts.IWaveScalingState
    {
        private readonly ComponentStore _store;
        private readonly GameConfig _gameConfig;

        // 波次级击杀统计（每波重置）。
        private int[] _killsThisWave = new int[ComponentStore.MAX_PLAYERS];
        private float[] _damageTakenThisWave = new float[ComponentStore.MAX_PLAYERS];

        // 难度配置（来自 game_config.json，缺失时使用默认值）。
        private float _difficultyGrowthPerLeak = 0.10f;   // 每次漏怪难度增加 10%
        private float _difficultyShrinkPerKill = 0.005f;  // 每次击杀难度降低 0.5%
        private float _minDifficulty = 0.5f;              // 下限：基准难度的 50%
        private float _maxDifficulty = 3.0f;              // 上限：基准难度的 3 倍
        private float _initialDifficulty = 1.0f;           // 基准倍率

        public AdaptiveDifficultySystem(ComponentStore store, GameConfig gameConfig)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _gameConfig = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
            LoadConfig();
        }

        private void LoadConfig()
        {
            _difficultyGrowthPerLeak = _gameConfig.DifficultyGrowthPerWave > 0
                ? _gameConfig.DifficultyGrowthPerWave * 2f  // 比静态增长更积极。
                : 0.10f;
            _initialDifficulty = 1.0f;
            _minDifficulty = 0.5f;
            _maxDifficulty = 3.0f;
        }

        /// <summary>
        /// 波次阶段每回合调用，记录表现信号。
        /// </summary>
        public void Update(float deltaTime)
        {
            for (int playerId = 0; playerId < ComponentStore.MAX_PLAYERS; playerId++)
            {
                if (_store.PlayerCurrentHealth[playerId] <= 0) continue;

                // 泄漏由 BenchmarkSystem/GameManager 更新，本方法只保留活跃状态检查。
            }
        }

        /// <summary>
        /// 记录一次击杀，供自适应难度系统使用。
        /// </summary>
        public void RecordKill(int playerId)
        {
            if (playerId < 0 || playerId >= ComponentStore.MAX_PLAYERS) return;
            _killsThisWave[playerId]++;
        }

        /// <summary>
        /// 记录玩家本波承受的伤害。
        /// </summary>
        public void RecordDamageTaken(int playerId, float damage)
        {
            if (playerId < 0 || playerId >= ComponentStore.MAX_PLAYERS) return;
            _damageTakenThisWave[playerId] += damage;
        }

        /// <summary>
        /// 由 WaveSpawningSystem 的 OnWaveComplete 事件调用，计算新的难度等级。
        /// 使用本波泄漏、击杀、承伤和剩余金币等信号。
        /// <paramref name="expectedKills"/> 是设计器为本波设置的击杀基准值
        ///（来自 <c>WaveConfig.ExpectedKillCount</c>）。当该值小于等于 0 时，
        /// 橡皮筋生成倍率保持 1.0，兼容未在 JSON 中启用该功能的波次。
        /// </summary>
        public void OnWaveComplete(int playerId, int expectedKills = 0)
        {
            if (playerId < 0 || playerId >= ComponentStore.MAX_PLAYERS) return;

            int leaks = _store.EnemiesLeakedThisWave[playerId];
            int kills = _killsThisWave[playerId];

            // 计算表现分差值：漏怪少且击杀多时降低难度，反之提高难度。
            float currentLevel = _store.AdaptiveDifficultyLevel[playerId];
            float performanceScore = 0f;

            // 漏怪惩罚：每次漏怪都会提高难度。
            performanceScore += leaks * _difficultyGrowthPerLeak;

            // 击杀奖励：每次击杀都会降低难度。
            performanceScore -= kills * _difficultyShrinkPerKill;

            // 计算并裁剪新的难度等级。
            float newLevel = currentLevel + performanceScore;
            newLevel = Math.Clamp(newLevel, _minDifficulty, _maxDifficulty);

            _store.AdaptiveDifficultyLevel[playerId] = newLevel;

            // 更新用于显示和调试的累计分数。
            _store.AdaptiveDifficultyScore[playerId] += (kills > 0 || leaks > 0)
                ? (kills * 0.5f) - (leaks * 1.0f)
                : 0f;

            // 重置本波计数器。
            _killsThisWave[playerId] = 0;
            _damageTakenThisWave[playerId] = 0f;
            _store.EnemiesLeakedThisWave[playerId] = 0;

            // Round 120 Dir 3 — rubber-band spawn multiplier for the NEXT wave.
            // Only compute when the just-finished wave opted in (expectedKills > 0).
            // Sensitivity defaults to AdaptiveSpawnConfig.DefaultSpawnSensitivity.
            // When sensitivity is 0 OR expectedKills is 0, multiplier stays at 1.0.
            if (expectedKills > 0 && AdaptiveSpawnConfig.DefaultSpawnSensitivity > 0f)
            {
                // rawDelta = (actual - expected) / expected; >0 means player over-killed.
                float rawDelta = (kills - expectedKills) / (float)expectedKills;
                float mult = 1.0f + rawDelta * AdaptiveSpawnConfig.DefaultSpawnSensitivity;
            // 说明：IWaveSpawningPort.SetPerformanceSpawnMultiplier 负责最终裁剪。
                // and the near-1 snap, so we write the unclamped value here for transparency.
                _waveSpawningSystemRef?.SetPerformanceSpawnMultiplier(mult);
            }
        }

        // 第 120 轮方向 3：保存波次生成端口回引，以便完成波次时写入缩放倍率。
        private global::BattleSystemECS.Content.Contracts.IWaveSpawningPort _waveSpawningSystemRef;
        public void SetWaveSpawningSystem(global::BattleSystemECS.Content.Contracts.IWaveSpawningPort waveSpawningSystem)
        {
            _waveSpawningSystemRef = waveSpawningSystem;
        }

        /// <summary>
        /// Called at level start — resets all adaptive difficulty state.
        /// </summary>
        public void Reset()
        {
            for (int i = 0; i < ComponentStore.MAX_PLAYERS; i++)
            {
                _killsThisWave[i] = 0;
                _damageTakenThisWave[i] = 0f;
                _store.AdaptiveDifficultyLevel[i] = _initialDifficulty;
                _store.AdaptiveDifficultyScore[i] = 0f;
                _store.EnemiesLeakedThisWave[i] = 0;
            }
        }

        /// <summary>
        /// Returns the current difficulty multiplier for a player.
        /// 生成敌人时由 IWaveSpawningPort 读取。
        /// </summary>
        public float GetDifficultyMult(int playerId)
        {
            if (playerId < 0 || playerId >= ComponentStore.MAX_PLAYERS) return 1.0f;
            return _store.AdaptiveDifficultyLevel[playerId];
        }
    }
}
