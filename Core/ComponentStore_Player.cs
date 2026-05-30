using System;
using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Core;

namespace BattleSystemECS.Core
{
    public partial class ComponentStore
    {
        // ==================== 玩家组件访问 ====================

        public void AddPlayer(int entityId, float attackRange, float attackSpeed, float attackDamage, int currentLevel, int baseLives = 10)
        {
            if (entityId < 0 || entityId >= MAX_PLAYERS) return;

            PlayerAttackRange[entityId] = attackRange;
            PlayerAttackSpeed[entityId] = attackSpeed;
            PlayerAttackDamage[entityId] = attackDamage;
            PlayerCurrentLevel[entityId] = currentLevel;
            PlayerGold[entityId] = 0f;
            PlayerUpgradeThreshold[entityId] = 1000f;  // 提高到 1000 以更快升级测试技能
            PlayerBuffs[entityId] = new List<string>();
            PlayerBuffFlags[entityId] = BuffType.None;
            PlayerBaseLives[entityId] = baseLives;
            PlayerMaxBaseLives[entityId] = baseLives;
            // Weather: default to clear (type 0), intensity 0
            CurrentWeather[entityId] = 0;
            WeatherIntensity[entityId] = 0f;
            WeatherTimer[entityId] = -1f;

            PlayerEntityId = entityId;
        }

        public float GetPlayerAttackRange(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 0f;
            return PlayerAttackRange[playerId];
        }

        public void SetPlayerAttackRange(int playerId, float range)
        {
            if (!IsValidPlayer(playerId)) return;
            PlayerAttackRange[playerId] = range;
        }

        public float GetPlayerAttackSpeed(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 0f;
            return PlayerAttackSpeed[playerId];
        }

        public float GetPlayerAttackDamage(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 0f;
            return PlayerAttackDamage[playerId];
        }

        public void SetPlayerAttackDamage(int playerId, float damage)
        {
            if (!IsValidPlayer(playerId)) return;
            PlayerAttackDamage[playerId] = damage;
        }

        public float GetPlayerGold(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 0f;
            return PlayerGold[playerId];
        }

        public float GetPlayerTotalGold(int playerId)
        {
            return GetPlayerGold(playerId);
        }

        public void SetPlayerGold(int playerId, float gold)
        {
            if (!IsValidPlayer(playerId)) return;
            PlayerGold[playerId] = gold;
        }

        /// <summary>
        /// Remove gold from player (thief steal, penalty, etc.). Clamps to 0.
        /// </summary>
        public void LoseGold(int playerId, float amount)
        {
            if (!IsValidPlayer(playerId) || amount <= 0f) return;
            float current = PlayerGold[playerId];
            float newGold = Math.Max(0f, current - amount);
            PlayerGold[playerId] = newGold;
        }

        public int GetPlayerLevel(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 0;
            return PlayerCurrentLevel[playerId];
        }

        public void SetPlayerLevel(int playerId, int level)
        {
            if (!IsValidPlayer(playerId)) return;
            PlayerCurrentLevel[playerId] = level;
        }

        public List<string> GetPlayerBuffs(int playerId)
        {
            if (!IsValidPlayer(playerId)) return new List<string>();
            // ✅ Bug#17 fix: return a defensive copy to prevent external mutation
            return new List<string>(PlayerBuffs[playerId]);
        }

        public void AddPlayerBuff(int playerId, string buff)
        {
            if (!IsValidPlayer(playerId)) return;
            PlayerBuffs[playerId].Add(buff);
        }

        // ── O(1) buff flag helpers (perf: eliminates per-frame GC) ──────────
        public void AddBuff(int playerId, BuffType buff)
        {
            if (!IsValidPlayer(playerId)) return;
            PlayerBuffFlags[playerId] |= buff;
        }

        public bool HasBuff(int playerId, BuffType buff)
        {
            if (!IsValidPlayer(playerId)) return false;
            return (PlayerBuffFlags[playerId] & buff) != 0;
        }

        public float GetAttackBuffMultiplier(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 1f;
            return (PlayerBuffFlags[playerId] & BuffType.AttackBoost) != 0 ? 1.1f : 1f;
        }

        public bool HasCritRateBuff(int playerId)
        {
            if (!IsValidPlayer(playerId)) return false;
            return (PlayerBuffFlags[playerId] & BuffType.CritRateBoost) != 0;
        }

        public float GetPlayerUpgradeThreshold(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 0f;
            return PlayerUpgradeThreshold[playerId];
        }

        public void SetPlayerUpgradeThreshold(int playerId, float threshold)
        {
            if (!IsValidPlayer(playerId)) return;
            PlayerUpgradeThreshold[playerId] = threshold;
        }

        // ==================== 玩家 CC (Crowd Control) ====================
        /// <summary>Returns true if the player is currently stunned.</summary>
        public bool IsPlayerStunned(int playerId)
        {
            if (!IsValidPlayer(playerId)) return false;
            return PlayerStunDuration[playerId] > 0;
        }

        /// <summary>Returns true if the player is currently slowed.</summary>
        public bool IsPlayerSlowed(int playerId)
        {
            if (!IsValidPlayer(playerId)) return false;
            return PlayerSlowFactor[playerId] > 0f;
        }

        /// <summary>Applies a stun to the player for N turns.</summary>
        public void ApplyPlayerStun(int playerId, int turns)
        {
            if (!IsValidPlayer(playerId)) return;
            if (turns <= 0) return;
            if (PlayerStunDuration[playerId] < turns)
                PlayerStunDuration[playerId] = turns;
        }

        /// <summary>Applies slow to the player. factor is a speed multiplier (0.5 = 50% speed).</summary>
        public void ApplyPlayerSlow(int playerId, float factor, int duration)
        {
            if (!IsValidPlayer(playerId)) return;
            if (factor <= 0f || factor >= 1f) return;
            // Take the stronger slow if stacking
            if (factor < PlayerSlowFactor[playerId])
            {
                PlayerSlowFactor[playerId] = factor;
                PlayerSlowDuration[playerId] = duration;
            }
            else if (PlayerSlowFactor[playerId] <= 0f)
            {
                PlayerSlowFactor[playerId] = factor;
                PlayerSlowDuration[playerId] = duration;
            }
        }

        /// <summary>Applies a shield to the player. Shield absorbs damage before health.</summary>
        public void ApplyPlayerShield(int playerId, float amount, float duration)
        {
            if (!IsValidPlayer(playerId)) return;
            if (amount <= 0f) return;
            // Stack shields (keep the larger one + add the new amount)
            PlayerShield[playerId] += amount;
            if (duration > PlayerShieldDuration[playerId])
                PlayerShieldDuration[playerId] = duration;
        }

        /// <summary>Returns the current shield value for a player.</summary>
        public float GetPlayerShield(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 0f;
            return PlayerShield[playerId];
        }

        /// <summary>
        /// Called at the start of each turn: clears enemy stun flags and decrements player CC durations.
        /// Enemy stun flags are cleared by EnemyMovementSystem.SetTurn; this method handles player CC only.
        /// Thread-safety note: called in the serial phase (GameManager.Run frame-end), so no additional
        /// synchronization is needed for MAX_PLAYERS=10 CC field access.
        /// </summary>
        public void SetTurnCCFlags()
        {
            // Decrement player CC durations (MAX_PLAYERS = 10, so simple loop is fast)
            for (int i = 0; i < MAX_PLAYERS; i++)
            {
                if (PlayerStunDuration[i] > 0) PlayerStunDuration[i]--;
                if (PlayerSlowDuration[i] > 0)
                {
                    PlayerSlowDuration[i]--;
                    if (PlayerSlowDuration[i] <= 0) PlayerSlowFactor[i] = 0f;
                }
                // Shield duration decrements per turn (1 second per turn in this engine)
                if (PlayerShieldDuration[i] > 0f)
                {
                    PlayerShieldDuration[i] -= 1f;
                    if (PlayerShieldDuration[i] <= 0f)
                    {
                        PlayerShieldDuration[i] = 0f;
                        PlayerShield[i] = 0f;
                        // Log shield dissipation — use static no-op to avoid Console.WriteLine/IO overhead in hot path
                        FileLogger.LogHotPath($"[SHIELD] 护盾消散！ playerId={i}");
                    }
                }
            }
        }

        // ==================== 玩家生命值访问方法 ====================

        public float GetPlayerMaxHealth(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 0f;
            return PlayerMaxHealth[playerId];
        }

        public void SetPlayerMaxHealth(int playerId, float maxHealth)
        {
            if (!IsValidPlayer(playerId)) return;
            PlayerMaxHealth[playerId] = maxHealth;
        }

        public float GetPlayerCurrentHealth(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 0f;
            return PlayerCurrentHealth[playerId];
        }

        public int GetPlayerBaseLives(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 0;
            return PlayerBaseLives[playerId];
        }

        public void SetPlayerBaseLives(int playerId, int lives)
        {
            if (!IsValidPlayer(playerId)) return;
            PlayerBaseLives[playerId] = lives;
        }

        public void DecrementPlayerBaseLives(int playerId)
        {
            if (!IsValidPlayer(playerId)) return;
            if (PlayerBaseLives[playerId] > 0)
                PlayerBaseLives[playerId]--;
        }

        public void SetPlayerCurrentHealth(int playerId, float currentHealth)
        {
            if (!IsValidPlayer(playerId)) return;
            PlayerCurrentHealth[playerId] = currentHealth;
        }

        public void DecreasePlayerHealth(int playerId, float damage)
        {
            if (!IsValidPlayer(playerId)) return;
            // Shield absorbs damage before health (independent of armor)
            float shield = PlayerShield[playerId];
            if (shield > 0f)
            {
                float absorbed = System.Math.Min(shield, damage);
                PlayerShield[playerId] = shield - absorbed;
                damage -= absorbed;
                if (damage <= 0f) return;
            }
            float armor = PlayerArmor[playerId];
            float mitigatedDamage = damage * (1f - armor);
            PlayerCurrentHealth[playerId] = System.Math.Max(0f, PlayerCurrentHealth[playerId] - mitigatedDamage);
        }

        public bool IsPlayerAlive(int playerId)
        {
            if (!IsValidPlayer(playerId)) return false;
            return PlayerCurrentHealth[playerId] > 0f;
        }

        // ==================== 天气系统访问方法 ====================
        public int GetCurrentWeather(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 0;
            return CurrentWeather[playerId];
        }

        public void SetCurrentWeather(int playerId, int weatherType)
        {
            if (!IsValidPlayer(playerId)) return;
            CurrentWeather[playerId] = weatherType;
        }

        public float GetWeatherIntensity(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 0f;
            return WeatherIntensity[playerId];
        }

        public void SetWeatherIntensity(int playerId, float intensity)
        {
            if (!IsValidPlayer(playerId)) return;
            WeatherIntensity[playerId] = intensity;
        }

        public float GetWeatherTimer(int playerId)
        {
            if (!IsValidPlayer(playerId)) return -1f;
            return WeatherTimer[playerId];
        }

        public void SetWeatherTimer(int playerId, float timer)
        {
            if (!IsValidPlayer(playerId)) return;
            WeatherTimer[playerId] = timer;
        }

        // ==================== 昼夜循环系统访问方法 ====================
        public int GetDayNightPhase(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 0;
            return GlobalDayNightPhase[playerId];
        }

        public void SetDayNightPhase(int playerId, int phase)
        {
            if (!IsValidPlayer(playerId)) return;
            GlobalDayNightPhase[playerId] = phase;
        }

        public float GetDayNightTimer(int playerId)
        {
            if (!IsValidPlayer(playerId)) return -1f;
            return GlobalDayNightTimer[playerId];
        }

        public void SetDayNightTimer(int playerId, float timer)
        {
            if (!IsValidPlayer(playerId)) return;
            GlobalDayNightTimer[playerId] = timer;
        }

        public int GetDayNightCycleCount(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 0;
            return GlobalDayNightCycleCount[playerId];
        }

        public void IncrementDayNightCycleCount(int playerId)
        {
            if (!IsValidPlayer(playerId)) return;
            GlobalDayNightCycleCount[playerId]++;
        }
    }
}
