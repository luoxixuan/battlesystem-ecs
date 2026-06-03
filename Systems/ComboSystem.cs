using System;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Combo Kill 连击追踪系统。
    /// 
    /// 工作方式：
    /// - 每帧调用 Update(deltaTime) 递减 ComboTimer，超时归零 ComboCount
    /// - 每次击杀调用 OnEnemyKilled(playerId) 累加 ComboCount、重置计时器、更新倍率
    /// - GetComboDamageMultiplier(playerId) 返回 min(1 + ComboCount * bonus, maxMult)
    /// - GetComboGoldBonus(playerId) 返回当前金币加成倍率
    /// 
    /// 设计原则：两阶段模式 — OnEnemyKilled 由帧末 ResolveEnemiesKilledThisFrame 后调用，
    ///           所有写操作在串行段完成，无锁竞争。
    /// </summary>
    public class ComboSystem
    {
        private ComponentStore store;
        private ComboConfig config;

        // ==================== Combo Chain (Round 81) ====================
        // Kills within the combo window (config.ComboWindowSeconds) accumulate into
        // PlayerChainKillCount. Once the count reaches ChainKillThreshold (default 3),
        // a global buff is activated for ChainKillBuffDuration (default 5s) granting
        // all of this player's towers +ChainKillDamageBonusPct (default 25%) damage.
        // Constants are hardcoded with sane defaults rather than introducing a new
        // config class — keeps the change small and matches the "≤3 files" budget.
        private const int ChainKillThreshold = 3;
        private const float ChainKillBuffDuration = 5.0f;
        private const float ChainKillDamageBonusPct = 0.25f;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="store">ComponentStore — 用于读写玩家连击字段</param>
        /// <param name="config">ComboConfig — 从 game_config.json 读取的配置（可为 null，使用默认值）</param>
        public ComboSystem(ComponentStore store, ComboConfig config = null)
        {
            this.store = store;
            this.config = config ?? new ComboConfig();
            // Subscribe to kill events for combo tracking
            store.OnEnemyKilled += OnEnemyKilledHandler;
        }

        private void OnEnemyKilledHandler(int enemyId, int playerId)
        {
            // Forward to internal handler — separates public event API from combo logic
            HandleComboIncrement(playerId);
        }

        /// <summary>
        /// 每帧调用：递减 ComboTimer，超时归零连击数。
        /// </summary>
        /// <param name="deltaTime">帧时间步长（秒）</param>
        public void Update(float deltaTime)
        {
            for (int i = 0; i < ComponentStore.MAX_PLAYERS; i++)
            {
                if (store.PlayerComboTimer[i] > 0f)
                {
                    store.PlayerComboTimer[i] -= deltaTime;
                    if (store.PlayerComboTimer[i] <= 0f)
                    {
                        // Combo 超时：归零
                        store.PlayerComboTimer[i] = 0f;
                        store.PlayerComboCount[i] = 0f;
                        store.PlayerComboDamageMult[i] = 1f;
                    }
                }

                // ── Combo Chain buff timer (Round 81) ──────────────────────────────
                // Decrement chain buff timer; when it expires, reset the chain kill
                // count so the next streak must rebuild from zero. The buff itself is
                // O(1) read in TowerAttackSystem damage apply — no allocation, no work
                // when timer is 0.
                if (store.PlayerChainKillBuffTimer[i] > 0f)
                {
                    store.PlayerChainKillBuffTimer[i] -= deltaTime;
                    if (store.PlayerChainKillBuffTimer[i] <= 0f)
                    {
                        store.PlayerChainKillBuffTimer[i] = 0f;
                        store.PlayerChainKillCount[i] = 0;
                    }
                }
            }
        }

        /// <summary>
        /// 敌人被击杀时调用（由 ComponentStore.OnEnemyKilled 事件自动触发）。
        /// 累加 ComboCount，重置计时器，更新伤害倍率。
        /// </summary>
        /// <param name="playerId">击杀方玩家 ID</param>
        public void HandleComboIncrement(int playerId)
        {
            if (playerId < 0 || playerId >= ComponentStore.MAX_PLAYERS) return;

            // 累加连击数
            store.PlayerComboCount[playerId] += 1f;

            // 重置计时器
            store.PlayerComboTimer[playerId] = config.ComboWindowSeconds;

            // 更新伤害倍率：min(1 + ComboCount * bonus, maxMult)
            float dmgMult = 1f + store.PlayerComboCount[playerId] * config.ComboDamageBonusPerKill;
            dmgMult = Math.Min(dmgMult, config.ComboMaxMultiplier);
            store.PlayerComboDamageMult[playerId] = dmgMult;

            // 更新金币倍率
            float goldMult = 1f + store.PlayerComboCount[playerId] * config.ComboGoldBonusPerKill;
            goldMult = Math.Min(goldMult, config.ComboMaxMultiplier);
            store.PlayerComboGoldMult[playerId] = goldMult;

            // 更新本波次最高连击记录
            if (store.PlayerComboCount[playerId] > store.PlayerComboKillStreak[playerId])
            {
                store.PlayerComboKillStreak[playerId] = store.PlayerComboCount[playerId];
            }

            // ── Combo Chain (Round 81) ────────────────────────────────────────────
            // Each kill inside the chain window increments the counter. When the
            // counter reaches ChainKillThreshold, activate a global damage buff
            // for ChainKillBuffDuration seconds (applies to ALL of this player's
            // towers, read O(1) in TowerAttackSystem damage apply).
            // Counter is reset to 0 only when the buff timer expires (in Update).
            store.PlayerChainKillCount[playerId] += 1;
            if (store.PlayerChainKillCount[playerId] >= ChainKillThreshold)
            {
                store.PlayerChainKillBuffTimer[playerId] = ChainKillBuffDuration;
                // Do NOT reset count here — let it accumulate further so the buff
                // can be refreshed/extended by continued chains. Reset happens on
                // timer expiry in Update().
            }
        }

        /// <summary>
        /// Returns 1.0f when no chain buff is active, or (1 + ChainKillDamageBonusPct)
        /// when the chain kill buff timer is ticking. Read O(1) per damage apply.
        /// </summary>
        public float GetChainKillDamageMultiplier(int playerId)
        {
            if (playerId < 0 || playerId >= ComponentStore.MAX_PLAYERS) return 1f;
            if (store.PlayerChainKillBuffTimer[playerId] <= 0f) return 1f;
            return 1f + ChainKillDamageBonusPct;
        }

        /// <summary>
        /// 获取当前伤害倍率。Combo=0 时返回 1.0。
        /// </summary>
        public float GetComboDamageMultiplier(int playerId)
        {
            if (playerId < 0 || playerId >= ComponentStore.MAX_PLAYERS) return 1f;
            return store.PlayerComboDamageMult[playerId];
        }

        /// <summary>
        /// 获取当前金币加成倍率。Combo=0 时返回 1.0。
        /// 计算方式与伤害倍率相同，但使用 ComboGoldBonusPerKill。
        /// </summary>
        public float GetComboGoldMultiplier(int playerId)
        {
            if (playerId < 0 || playerId >= ComponentStore.MAX_PLAYERS) return 1f;
            float mult = 1f + store.PlayerComboCount[playerId] * config.ComboGoldBonusPerKill;
            return Math.Min(mult, config.ComboMaxMultiplier);
        }

        /// <summary>
        /// 获取当前连击数（不含奖励倍率）。
        /// </summary>
        public float GetComboCount(int playerId)
        {
            if (playerId < 0 || playerId >= ComponentStore.MAX_PLAYERS) return 0f;
            return store.PlayerComboCount[playerId];
        }

        /// <summary>
        /// 获取本波次最高连击记录。
        /// </summary>
        public float GetComboKillStreak(int playerId)
        {
            if (playerId < 0 || playerId >= ComponentStore.MAX_PLAYERS) return 0f;
            return store.PlayerComboKillStreak[playerId];
        }

        /// <summary>
        /// 重置指定玩家的连击状态（波次开始时调用）。
        /// </summary>
        public void ResetCombo(int playerId)
        {
            if (playerId < 0 || playerId >= ComponentStore.MAX_PLAYERS) return;
            store.PlayerComboCount[playerId] = 0f;
            store.PlayerComboTimer[playerId] = 0f;
            store.PlayerComboDamageMult[playerId] = 1f;
            store.PlayerComboGoldMult[playerId] = 1f;
            // Round 81: also reset combo chain state so a new wave starts clean.
            store.PlayerChainKillCount[playerId] = 0;
            store.PlayerChainKillBuffTimer[playerId] = 0f;
            // 注意：ComboKillStreak 不重置，保留本波次最高记录
        }

        /// <summary>
        /// 重置所有玩家的连击状态（游戏初始化时调用）。
        /// </summary>
        public void ResetAllCombos()
        {
            for (int i = 0; i < ComponentStore.MAX_PLAYERS; i++)
            {
                store.PlayerComboCount[i] = 0f;
                store.PlayerComboTimer[i] = 0f;
                store.PlayerComboDamageMult[i] = 1f;
                store.PlayerComboKillStreak[i] = 0f;
                store.PlayerComboGoldMult[i] = 1f;
                // Round 81: also reset combo chain state.
                store.PlayerChainKillCount[i] = 0;
                store.PlayerChainKillBuffTimer[i] = 0f;
            }
        }
    }
}
