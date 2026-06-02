using System;
using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Core;

namespace BattleSystemECS.Core
{
    public partial class ComponentStore
    {
        #region Player Components
        public float[] PlayerAttackRange = new float[MAX_PLAYERS];
        public float[] PlayerAttackSpeed = new float[MAX_PLAYERS];
        public float[] PlayerAttackDamage = new float[MAX_PLAYERS];
        public float[] PlayerMaxHealth = new float[MAX_PLAYERS];  // 玩家最大生命值
        public float[] PlayerCurrentHealth = new float[MAX_PLAYERS];  // 玩家当前生命值
        public float[] PlayerArmor = new float[MAX_PLAYERS];  // 玩家护甲：减少受到伤害
        // Player shield: absorbs damage before health, independent of armor
        public float[] PlayerShield = new float[MAX_PLAYERS];
        public float[] PlayerShieldDuration = new float[MAX_PLAYERS]; // seconds remaining
        // Player thorns: reflects a fraction of damage taken back to the attacking enemy.
        public float[] PlayerThornsRatio = new float[MAX_PLAYERS];
public int[] PlayerCurrentLevel = new int[MAX_PLAYERS];
        // Player damage type: determines which resistance enemies use for mitigation.
        public DamageType[] PlayerDamageType = new DamageType[MAX_PLAYERS];
        public float[] PlayerGold = new float[MAX_PLAYERS];
        public float[] PlayerUpgradeThreshold = new float[MAX_PLAYERS];
        // ==================== 法力/能量池资源系统 (Mana Pool) ====================
        // PlayerMana: current mana points for each player
        public float[] PlayerMana = new float[MAX_PLAYERS];
        // PlayerMaxMana: maximum mana cap
        public float[] PlayerMaxMana = new float[MAX_PLAYERS];
        // PlayerManaRegen: mana regeneration rate per second
        public float[] PlayerManaRegen = new float[MAX_PLAYERS];
        // PlayerManaCost: cost multiplier for skill mana consumption
        public float[] PlayerManaCost = new float[MAX_PLAYERS];
        // PlayerMaxMana initialized to default value (can be configured via GameConfig)
        private float _playerMaxManaDefault = 100f;
        public float PlayerMaxManaDefault { get => _playerMaxManaDefault; set => _playerMaxManaDefault = value; }
        // ==================== 玩家全局技能/终极技能 (Global Skills / Ultimates) ====================
        // PlayerGlobalSkillUnlocked: bit-flag of which global skills are unlocked per player (indexed by playerId * MAX_GLOBAL_SKILLS + skillIdx)
        public bool[] PlayerGlobalSkillUnlocked = new bool[MAX_PLAYERS * 8];
        // PlayerGlobalSkillCooldown: remaining cooldown in seconds per global skill
        public float[] PlayerGlobalSkillCooldown = new float[MAX_PLAYERS * 8];
        // PlayerGlobalSkillPressed: hotkey pressed signal this frame (consumed by GlobalSkillSystem)
        public bool[] PlayerGlobalSkillPressed = new bool[MAX_PLAYERS];
        // PlayerGlobalSkillHotkey: hotkey string per skill for UI display
        public string[] PlayerGlobalSkillHotkey = new string[MAX_PLAYERS * 8];
        // ── Kill-Triggered Skill Cooldown Reset ───────────────────────────────
        // PlayerSkillResetOnKill: 0=None, 1=Full (reset all skill cooldowns to 0), 2=Partial (reduce by PlayerSkillResetAmount seconds).
        // Default 0 = disabled (backward compatible).
        public int[] PlayerSkillResetOnKill = new int[MAX_PLAYERS];
        // PlayerSkillResetAmount: for Partial mode, seconds to subtract from each skill's cooldown (clamped at 0).
        // For Full mode, value is ignored. Default 0.
        public float[] PlayerSkillResetAmount = new float[MAX_PLAYERS];
        private float _goldKillMultiplier = 1.0f;
        public float GoldKillMultiplier { get => _goldKillMultiplier; set => _goldKillMultiplier = value; }
        // all_income_mult: extra multiplier layered on top of gold kill multiplier
        private float _allIncomeMultKill = 1.0f;
        public float AllIncomeMultKill { get => _allIncomeMultKill; set => _allIncomeMultKill = value; }
        // flat bonus awarded once per elite kill
        private float _goldOnEliteKill = 0f;
        public float GoldOnEliteKill { get => _goldOnEliteKill; set => _goldOnEliteKill = value; }
        public List<string>[] PlayerBuffs = new List<string>[MAX_PLAYERS];

        // Perf: bit-flag buff storage — O(1) lookup, no GC allocation per frame
        public BuffType[] PlayerBuffFlags = new BuffType[MAX_PLAYERS];
        // Player stun duration counter (turns remaining). 0 = not stunned.
        public int[] PlayerStunDuration = new int[MAX_PLAYERS];
        // Player slow: tracks remaining slow turns and factor
        public float[] PlayerSlowFactor = new float[MAX_PLAYERS];
        public int[] PlayerSlowDuration = new int[MAX_PLAYERS];
// Base lives: number of leaks allowed before game over (independent of health)
        public int[] PlayerBaseLives = new int[MAX_PLAYERS];
        public int[] PlayerMaxBaseLives = new int[MAX_PLAYERS];

        // ==================== 塔部署数量限制 (Tower Placement Cap, SOA) ====================
        // PlayerMaxTowers: maximum number of towers player can place (configurable, can be expanded via tech tree)
        public int[] PlayerMaxTowers = new int[MAX_PLAYERS];
        // PlayerTowerCount: current number of towers placed by this player
        public int[] PlayerTowerCount = new int[MAX_PLAYERS];

        // ==================== 波次预览/侦查等级 (Wave Preview / Scouting Level) ====================
        // PlayerWavePreviewLevel: 0=None, 1=Vague (only count + type names, no stats), 2=Precise (full stats + skills).
        // Set externally by tech tree unlocks (e.g. "scouting_i" / "scouting_ii"). Default 0 = no preview.
        public int[] PlayerWavePreviewLevel = new int[MAX_PLAYERS];

        // ==================== 科技树组件的 SOA 存储 ====================
        public int[] PlayerResearchPoints = new int[MAX_PLAYERS];
        public HashSet<string>[] PlayerUnlockedTechs = new HashSet<string>[MAX_PLAYERS];
        // ==================== Combo Kill 连击组件（SOA） ====================
        // ComboCount: current consecutive kill streak within combo window
        public float[] PlayerComboCount = new float[MAX_PLAYERS];
        // ComboTimer: seconds since last kill (resets combo when > ComboWindowSeconds)
        public float[] PlayerComboTimer = new float[MAX_PLAYERS];
        // ComboDamageMult: current damage multiplier = min(1 + ComboCount * ComboDamageBonusPerKill, ComboMaxMultiplier)
        public float[] PlayerComboDamageMult = new float[MAX_PLAYERS];
        // ComboKillStreak: max combo achieved this wave (for UI/achievement tracking)
        public float[] PlayerComboKillStreak = new float[MAX_PLAYERS];
        // ComboGoldMult: current gold bonus multiplier = min(1 + ComboCount * ComboGoldBonusPerKill, ComboMaxMultiplier)
        public float[] PlayerComboGoldMult = new float[MAX_PLAYERS];

        // ==================== Bank / Interest System 组件（SOA） ====================
        // PlayerBankedGold: gold stored in the bank (earns interest each wave)
        public float[] PlayerBankedGold = new float[MAX_PLAYERS];
        // PlayerInterestRate: interest rate multiplier (0.05f = 5% per wave, capped at InterestRateCap)
        public float[] PlayerInterestRate = new float[MAX_PLAYERS];

        // ==================== Tower Placement Cost Scaling（每类型放置计数） ====================
        // PlacementCountByType: tracks how many towers of each type this player has placed (for cost scaling)
        public int[] PlacementCountByType = new int[9]; // index = (int)TowerType, size = 9 (Basic..Firewall)

        // ==================== Cooldown Reduction (CDR) 系统 ====================
        // PlayerCooldownReduction: global CDR multiplier per player (0 = no reduction, 0.3 = 30% faster cooldowns)
        // Multiplicative diminishing returns: effectiveCooldown = baseCooldown * (1 - cdr)
        // Capped at 60% (0.6) to avoid zero-duration cooldowns
        public float[] PlayerCooldownReduction = new float[MAX_PLAYERS];

        // ==================== Breather Wave Reward (SOA) ====================
        // PlayerHealOnBreatherWave: percentage of max HP restored when a Breather-rhythm wave completes.
        // Default 0 = no heal. Example: 0.3f = heal 30% of max HP. Applied via SetPlayerCurrentHealth with clamp to max.
        public float[] PlayerHealOnBreatherWave = new float[MAX_PLAYERS];
        // PlayerCooldownReduceOnBreather: seconds subtracted from each global skill cooldown when a Breather wave completes.
        // Default 0 = no CDR. Example: 5f = -5s on every active skill cooldown (clamped at 0).
        public float[] PlayerCooldownReduceOnBreather = new float[MAX_PLAYERS];
        // PlayerBreatherGoldBonus: flat gold awarded on top of any per-wave gold when a Breather wave completes.
        // Default 0 = no extra gold. The Breather x2 effect in GoldSystem multiplies this by 2.
        public float[] PlayerBreatherGoldBonus = new float[MAX_PLAYERS];

        // ==================== Shop Reroll System (SOA) ====================
        // PlayerShopRerollCount: number of rerolls performed in the current BuildPhase (resets each phase).
        public int[] PlayerShopRerollCount = new int[MAX_PLAYERS];
        // PlayerShopOfferTypeId: 1D-flat offer slot store, indexed by playerId * MAX_OFFER_SLOTS + slotIdx.
        // Stores the entity type id of the offer (tower type or skill id, both as int).
        // 0 = empty slot. Default 0f/0 per C# spec — uninitialized slots are inert.
        public int[] PlayerShopOfferTypeId = new int[MAX_PLAYERS * 8];
        // PlayerShopOfferIsTower: 0=skill offer, 1=tower offer. 1D-flat parallel array.
        public int[] PlayerShopOfferIsTower = new int[MAX_PLAYERS * 8];
        // PlayerShopPityRare: consecutive offer count without a Rare (RarityTier>=1) since last Rare.
        public int[] PlayerShopPityRare = new int[MAX_PLAYERS];
        // PlayerShopPityEpic: consecutive offer count without an Epic (RarityTier=2) since last Epic.
        public int[] PlayerShopPityEpic = new int[MAX_PLAYERS];
        // ShopRerollMaxSlots: cap for offer slot storage (matches ShopRerollConfig.OfferSlotCount, default 3)
        public const int MAX_SHOP_OFFER_SLOTS = 8;

        #endregion

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
            // Kill-triggered skill cooldown reset: default to disabled (0/0)
            PlayerSkillResetOnKill[entityId] = 0;
            PlayerSkillResetAmount[entityId] = 0f;

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

        // ==================== 玩家法力访问方法 ====================
        public float GetPlayerMana(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 0f;
            return PlayerMana[playerId];
        }

        public float GetPlayerMaxMana(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 0f;
            return PlayerMaxMana[playerId];
        }

        public void SetPlayerMaxMana(int playerId, float maxMana)
        {
            if (!IsValidPlayer(playerId)) return;
            PlayerMaxMana[playerId] = maxMana;
        }

        public void SetPlayerMana(int playerId, float mana)
        {
            if (!IsValidPlayer(playerId)) return;
            PlayerMana[playerId] = Math.Max(0f, Math.Min(mana, PlayerMaxMana[playerId]));
        }

        public void DecreasePlayerMana(int playerId, float amount)
        {
            if (!IsValidPlayer(playerId) || amount <= 0f) return;
            PlayerMana[playerId] = Math.Max(0f, PlayerMana[playerId] - amount);
        }

        public void AddPlayerMana(int playerId, float amount)
        {
            if (!IsValidPlayer(playerId) || amount <= 0f) return;
            PlayerMana[playerId] = Math.Min(PlayerMaxMana[playerId], PlayerMana[playerId] + amount);
        }

        public float GetPlayerManaRegen(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 0f;
            return PlayerManaRegen[playerId];
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

        // ==================== Hero / Mercenary System components (SOA) ====================
        // MAX_HEROES: maximum number of hero units per player (5 heroes max)
        public const int MAX_HEROES = 5;
        // HeroIsDeployed: whether hero slot i is currently deployed on the map
        public bool[] HeroIsDeployed = new bool[MAX_HEROES];
        // HeroPosX/Y: current world position of hero i
        public float[] HeroPosX = new float[MAX_HEROES];
        public float[] HeroPosY = new float[MAX_HEROES];
        // HeroTargetX/Y: target position hero i is moving toward
        public float[] HeroTargetX = new float[MAX_HEROES];
        public float[] HeroTargetY = new float[MAX_HEROES];
        // HeroMoveSpeed: movement speed (units per frame)
        public float[] HeroMoveSpeed = new float[MAX_HEROES];
        // HeroAttackRange: attack range in cells
        public int[] HeroAttackRange = new int[MAX_HEROES];
        // HeroDamage: base damage per attack
        public float[] HeroDamage = new float[MAX_HEROES];
        // HeroAttackSpeed: attacks per second
        public float[] HeroAttackSpeed = new float[MAX_HEROES];
        // HeroCooldown: remaining cooldown in seconds (0 = ready to attack)
        public float[] HeroCooldown = new float[MAX_HEROES];
        // HeroTypeId: which hero definition (index into heroes.json config)
        public int[] HeroTypeId = new int[MAX_HEROES];
    }
}
