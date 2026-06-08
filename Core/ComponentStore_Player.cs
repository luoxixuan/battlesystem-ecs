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
        // PlayerMinHealthFloor: minimum HP floor for one-shot protection. Health won't drop below this value
        // (only when floor > 0). One-shot protection is the "floor" model: HP never drops below floor.
        public float[] PlayerMinHealthFloor = new float[MAX_PLAYERS];
        // PlayerReincarnationCharges: number of times this player can auto-revive on death.
        // 0 = no reincarnation (default). Set to 1 in config for "one-time save".
        public int[] PlayerReincarnationCharges = new int[MAX_PLAYERS];
        // PlayerReincarnationHealFraction: HP fraction restored on reincarnation (0-1).
        // 0.5 = revive at 50% MaxHP. Default 0.5f. Clamped to [0, 1] in setter.
        public float[] PlayerReincarnationHealFraction = new float[MAX_PLAYERS];
        // PlayerHasReincarnated: per-game flag. True after one reincarnation is used.
        // Prevents multiple revives within a single game. Reset on game start (AddPlayer).
        public bool[] PlayerHasReincarnated = new bool[MAX_PLAYERS];
        // ==================== Bullet Time / Slow Motion 组件 (SOA) ====================
        // PlayerBulletTimeTurnsLeft: remaining turns the bullet-time effect is active. 0 = inactive (default).
        // While > 0, FrameScheduler passes dt * PlayerBulletTimeScale to enemy/AI/Movement/Spatial phases
        // and the FULL dt to Combat/SkillBuff/PostDeath — i.e. enemies + projectiles run slow, towers don't.
        public float[] PlayerBulletTimeTurnsLeft = new float[MAX_PLAYERS];
        // PlayerBulletTimeScale: per-player slow-mo multiplier (0.3 = 30% enemy speed, 1.0 = normal). Default 1.0f.
        // Clamped to (0, 1] by setter so we never accidentally speed enemies up via this path.
        public float[] PlayerBulletTimeScale = new float[MAX_PLAYERS];
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
        // ==================== 法力护盾 (Mana Shield) ====================
        // PlayerManaShield: current mana-shield points. Absorbs damage BEFORE PlayerShield
        //   and PlayerCurrentHealth. Replenished continuously from excess mana when the
        //   player mana pool is above PlayerManaShield[playerId]'s `triggerThresholdPercent`
        //   of PlayerMaxMana (Round 175 Direction 1: Mana Shield — see ManaShieldSystem).
        //   Defaults to 0 (no shield) and only fills when ManaShieldConfig.Enabled = true.
        public float[] PlayerManaShield = new float[MAX_PLAYERS];
        // PlayerManaShieldCap: maximum mana-shield cap, recomputed each frame as
        //   PlayerMaxMana * ManaShieldConfig.MaxShieldPercent. Stored per-player so
        //   the cap is cheap to read in the damage hot-path.
        public float[] PlayerManaShieldCap = new float[MAX_PLAYERS];
        // PlayerManaShieldAbsorbRatio: how much of the current mana-shield is consumed
        //   per 1 point of damage absorbed. 1.0 = full damage converted to shield-pool
        //   loss; 2.0 = shield is twice as efficient (1 shield = 2 HP absorbed).
        //   Default 1.0. 0 / negative = mana shield inert (backward compatible).
        public float[] PlayerManaShieldAbsorbRatio = new float[MAX_PLAYERS];
        // PlayerManaShieldTriggered: latches true once mana shield has ever absorbed
        //   damage this game. Used by tests to verify the path was hit. Stays true
        //   until AddPlayer resets it. Default false.
        public bool[] PlayerManaShieldTriggered = new bool[MAX_PLAYERS];
 // ==================== Pre-fight Buff (Round178 Direction6) ====================
 // PlayerPreFightSelectedBuffId: the buff option Id the player picked for
 // the current wave. Empty string = no selection yet. Reset on
 // AddPlayer (game start) and on WaveComplete (wave-scoped, no
 // cross-wave carry).
 public string[] PlayerPreFightSelectedBuffId = new string[MAX_PLAYERS];
 // PlayerPreFightOption{1,2,3}Id: the three buff Ids offered this
 // BuildPhase (one slot per UI card). Empty strings until the next
 // PreFightBuffSystem.Update rolls. Read by UI / tests via the
 // public GetPreFightOptions API.
 public string[] PlayerPreFightOption1Id = new string[MAX_PLAYERS];
 public string[] PlayerPreFightOption2Id = new string[MAX_PLAYERS];
 public string[] PlayerPreFightOption3Id = new string[MAX_PLAYERS];
 // PlayerPreFightOptionsRolled: latch set true after the first
 // BuildPhase tick of a new wave-pending state. Read by tests to
 // verify the roll happened; cleared on WaveComplete.
 public bool[] PlayerPreFightOptionsRolled = new bool[MAX_PLAYERS];
 // PlayerPreFightCritBonus: cached additive crit-chance from the
 // selected buff.0 = no crit bonus (fast path). Default0f.
 public float[] PlayerPreFightCritBonus = new float[MAX_PLAYERS];
 // PlayerPreFightMaxHpMult: cached multiplicative max-HP bonus
 // from the selected buff.1.0 = no change (fast path). Default1f.
 public float[] PlayerPreFightMaxHpMult = new float[MAX_PLAYERS];

        // ==================== Momentum (Round174+ Direction 3) ====================
        // PlayerMomentumTimer: per-player accumulated wave-time in seconds. Default 0f.
        //   Incremented by MomentumSystem.Update each frame while a wave is in
        //   progress. Read by MomentumSystem to compute the current tier
        //   (floor(timer / TierDuration), capped at MaxTiers). Reset to 0 on
        //   OnWaveStart when MomentumConfig.ResetOnWave == true.
        public float[] PlayerMomentumTimer = new float[MAX_PLAYERS];
        // PlayerMomentumCurrentTier: cached current tier (0..MaxTiers) per player.
        //   Default 0 = no bonus. Updated by MomentumSystem.Update. Exposed
        //   for tests + UI / debug overlays. Indexing parallels PlayerId.
        public int[] PlayerMomentumCurrentTier = new int[MAX_PLAYERS];

        // ====================玩家全局技能/终极技能 (Global Skills / Ultimates) ====================
 // PlayerGlobalSkillUnlocked: bit-flag of which global skills are unlocked per player (indexed by playerId * MAX_GLOBAL_SKILLS + skillIdx)
 public bool[] PlayerGlobalSkillUnlocked = new bool[MAX_PLAYERS *8];
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

        // ── Decaying Wave Bounty tunables (Round 86) ──
        // _waveGoldDecayRate: linear decay per kill. 0.02 means each kill reduces the multiplier by 2%.
        // _waveGoldDecayFloor: lower bound for the multiplier. 0.3 = even at 100 kills, gold never drops below 30%.
        // Both default to the documented "DecayRate=0.02f / DecayFloor=0.3f" values from the design spec.
        // GoldSystem mutates these via WaveGoldDecayRate/WaveGoldDecayFloor setters (config-driven).
        private float _waveGoldDecayRate = 0.02f;
        private float _waveGoldDecayFloor = 0.3f;
        public float WaveGoldDecayRate { get => _waveGoldDecayRate; set => _waveGoldDecayRate = value; }
        public float WaveGoldDecayFloor { get => _waveGoldDecayFloor; set => _waveGoldDecayFloor = value; }
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

        // ==================== Build Queue (BuildPhase 预排多个塔位) ====================
        // PlayerBuildQueue: SOA queue of pending tower placements (per player, indexed by playerId * MAX_BUILD_QUEUE + slot).
        // Each slot holds (x, y, TowerType, damage, range, speed, cost, active) — a queued request is
        // drained in FIFO order by TowerPlacementSystem.ProcessBuildQueue() at WavePhase start.
        // Capacity is fixed at MAX_BUILD_QUEUE (16 by default) for zero-GC hot-path access.
        public struct BuildQueueSlot
        {
            public int X;
            public int Y;
            public int TowerType;  // (int)TowerType enum
            public float Damage;
            public int Range;
            public float Speed;
            public float Cost;
            public bool Active;     // true = slot occupied
        }
        public const int MAX_BUILD_QUEUE = 16;
        public BuildQueueSlot[] PlayerBuildQueue = new BuildQueueSlot[MAX_PLAYERS * MAX_BUILD_QUEUE];
        // PlayerBuildQueueCount: number of active slots per player (0..MAX_BUILD_QUEUE)
        public int[] PlayerBuildQueueCount = new int[MAX_PLAYERS];
        // PlayerBuildQueueTimer: per-player drain timer (seconds). Each tick, when >= BuildQueueInterval,
        // the head of the queue is consumed via PlaceTower. Default 0 = ready to drain on next tick.
        public float[] PlayerBuildQueueTimer = new float[MAX_PLAYERS];

        // ==================== Time Rewind Snapshot Ring (Round 109) ====================
        // PlayerStateSnapshot stores periodic samples of player HP / Mana / Shield so the
        // "Time Rewind" ability can restore them. The buffer is a fixed-size ring indexed by
        // (playerId * MAX_SNAPSHOTS + slot). Slot 0 is the oldest sample, slot MAX_SNAPSHOTS-1
        // is the newest. Capacity MAX_SNAPSHOTS=20 × 0.25s sampling interval = 5s lookback.
        public const int MAX_SNAPSHOTS = 20;
        public const float SNAPSHOT_INTERVAL = 0.25f;
        public const float DEFAULT_REWIND_SECONDS = 3.0f;
        public float[] PlayerSnapshotHP = new float[MAX_PLAYERS * MAX_SNAPSHOTS];
        public float[] PlayerSnapshotMana = new float[MAX_PLAYERS * MAX_SNAPSHOTS];
        public float[] PlayerSnapshotShield = new float[MAX_PLAYERS * MAX_SNAPSHOTS];
        // PlayerSnapshotHead: next write index for the ring (0..MAX_SNAPSHOTS-1). When the ring
        // wraps, the oldest entry is overwritten. The newest sample is always at (head - 1) mod MAX_SNAPSHOTS.
        public int[] PlayerSnapshotHead = new int[MAX_PLAYERS];
        // PlayerSnapshotFilled: how many slots in the ring are valid (0..MAX_SNAPSHOTS). When < MAX_SNAPSHOTS,
        // the skill is allowed to use a partial-buffer restore.
        public int[] PlayerSnapshotFilled = new int[MAX_PLAYERS];
        // PlayerSnapshotTick: per-player frame-counter accumulator. When >= SNAPSHOT_INTERVAL, a new
        // sample is taken and the accumulator is reset. Default 0 (samples taken on first tick).
        public float[] PlayerSnapshotTick = new float[MAX_PLAYERS];

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

        // ── Round 130 Inventory ──────────────────────────────────────────────
        // Per-player slot-based inventory. Slot count is fixed (MAX_INVENTORY_SLOTS = 8).
        // ItemId indexes into GameConfig.ItemDefs (-1 = empty slot, default 0-initialized → must call ResetInventory() on init).
        // Count is 0..MaxStack (default 0 = empty; MaxStack from def at load time).
        // All arrays are 0-initialized; default state = empty inventory. Zero-overhead fast path.
        public const int MAX_INVENTORY_SLOTS = 8;
        public int[] PlayerInventoryItemId = new int[MAX_PLAYERS * MAX_INVENTORY_SLOTS];
        public int[] PlayerInventoryCount = new int[MAX_PLAYERS * MAX_INVENTORY_SLOTS];
        // Total non-empty slot count (denormalized O(1) "is full" check).
        public int[] PlayerInventoryUsed = new int[MAX_PLAYERS];
        // Total items used (cumulative lifetime stat) for telemetry/achievements.
        public int[] PlayerInventoryUsedTotal = new int[MAX_PLAYERS];
        // Round 130 — DamageBoost item uses its OWN duration field (PlayerSlowDuration is shared
        // with SpeedBoost). Without separation, applying a DamageBoost would clobber an active
        // SpeedBoost timer (or vice versa). Tracked in turns, decremented by status tick.
        public int[] PlayerDamageBoostDuration = new int[MAX_PLAYERS];
        // DamageBoost magnitude (e.g., 0.2 = +20% attack). Default 0 = no boost active.
        // DamageSystem multiplies base attack by (1 + this). Stays at 0 when no Rage Draught active.
        public float[] PlayerDamageBoostMultiplier = new float[MAX_PLAYERS];

        // ==================== Bank / Interest System 组件（SOA） ====================
        // PlayerBankedGold: gold stored in the bank (earns interest each wave)
        public float[] PlayerBankedGold = new float[MAX_PLAYERS];
        // PlayerInterestRate: interest rate multiplier (0.05f = 5% per wave, capped at InterestRateCap)
        public float[] PlayerInterestRate = new float[MAX_PLAYERS];

        // ==================== Tower Placement Cost Scaling（每类型放置计数） ====================
        // PlacementCountByType: tracks how many towers of each type this player has placed (for cost scaling)
        public int[] PlacementCountByType = new int[9]; // index = (int)TowerType, size = 9 (Basic..Firewall)

        // ==================== Per-Type Placement Cap (Round 139) ====================
        // PlayerTowersOfType: how many towers of each TowerType this player currently has placed.
        // Indexed as [playerId * MAX_TOWER_TYPES + (int)TowerType]. 0 = none.
        // Enforces the maxPerTypeByType config (e.g. Sniper ≤ 4, EMP ≤ 3) so players can't
        // spam a single dominant type and must mix-and-match.
        // Cleared on DestroyEntity when the entity was a tower to prevent ID-reuse leakage.
        public const int MAX_TOWER_TYPES = 12; // Basic..Shrine (must match TowerType enum count)
        public int[] PlayerTowersOfType = new int[MAX_PLAYERS * MAX_TOWER_TYPES];
        // PlayerTowersOfTypeCap: per-player, per-type cap (loaded from tower_placement.json maxPerTypeByType).
        // 0 = no cap. Default-initialized to 0 in constructor; LoadPerTypeCaps populates from JSON.
        public int[] PlayerTowersOfTypeCap = new int[MAX_PLAYERS * MAX_TOWER_TYPES];

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

        // ==================== Decaying Wave Bounty (SOA) ====================
        // PlayerWaveKillCount: number of enemies THIS player has killed in the current wave.
        // Resets to 0 when OnWaveStart fires (see GoldSystem.SubscribeToWaveStart).
        // Used by ResolveEnemiesKilledThisFrame to apply a diminishing-returns multiplier
        // to gold rewards: finalGold = baseGold * max(DecayFloor, 1.0 - PlayerWaveKillCount[pid] * DecayRate).
        // This rewards fast wave clear (first kills pay full) and discourages slow trickle.
        public int[] PlayerWaveKillCount = new int[MAX_PLAYERS];

        // ==================== Wisp System (SOA) ====================
        // PlayerWispType: which wisp is currently active for each player.
        // 0 = None, 1 = Heal Wisp (passive HP regen), 2 = Slow Wisp (AoE slow on nearby enemies),
        // 3 = Curse Wisp (AoE armor shred on nearby enemies). Default 0 = no wisp active.
        // Only ONE wisp can be active per player (mutually exclusive — see WispSystem.SpawnWisp).
        public int[] PlayerWispType = new int[MAX_PLAYERS];
        // PlayerWispDurationLeft: seconds remaining for the active wisp. 0 = wisp expired/inactive.
        // Decremented each frame in WispSystem.Update; when it reaches 0, the wisp auto-expires.
        public float[] PlayerWispDurationLeft = new float[MAX_PLAYERS];
        // PlayerWispCooldown: seconds until the next wisp can be summoned (after expiration).
        // Default 0 = no cooldown (off-cooldown, can summon immediately). Used to throttle re-summon.
        public float[] PlayerWispCooldown = new float[MAX_PLAYERS];

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

        // ==================== Wave Skip Reward System (SOA) ====================
        // PlayerWaveSkipsUsed: number of times this player has used a "skip wave" reward
        // option in the current level. Capped at WaveSkipConfig.MaxSkipsPerLevel (default 3).
        // Default 0 = no skips used yet. Reset on AddPlayer (fresh game).
        public int[] PlayerWaveSkipsUsed = new int[MAX_PLAYERS];
        // PlayerSkipBonusDamagePct: additive damage multiplier bonus accumulated from all
        // wave-skip purchases this level (e.g. 0.30f = +30% damage for the rest of the level).
        // Stacks additively: 3 skips @ 0.10 each = 0.30. Default 0 = no bonus.
        // Applied multiplicatively at damage apply time via GetPlayerAttackDamage().
        public float[] PlayerSkipBonusDamagePct = new float[MAX_PLAYERS];

        // ==================== Combo Chain Bonus (SOA) ====================
        // PlayerChainKillCount: consecutive kill count within the chain window (resets when window expires).
        // Increments on each OnEnemyKilled event; when count >= ChainKillThreshold, fires a global
        // damage buff on all of this player's towers (PlayerChainKillBuffTimer = ChainKillBuffDuration).
        // Default 0 = no chain active. Reset on AddPlayer (fresh game).
        public int[] PlayerChainKillCount = new int[MAX_PLAYERS];
        // PlayerChainKillBuffTimer: seconds remaining on the active chain damage buff.
        // Decremented in ComboSystem.Update; when it reaches 0, PlayerChainKillCount resets to 0.
        // O(1) guard in TowerAttackSystem damage apply: if > 0, multiply finalDmg by (1 + bonus).
        // Default 0f = no buff active.
        public float[] PlayerChainKillBuffTimer = new float[MAX_PLAYERS];

        // ── Round 187 Direction 4 — Rally Buff (SOA) — Round 187 Direction 4 ====================
        // PlayerRallyActive: true if this player has an active Rally buff (player was hit, nearby
        // towers are getting +atk speed). Default false = no rally. Decremented each frame in
        // RallySystem.Update. The hot path in TowerAttackSystem reads TowerRallyAtkSpdBonus which
        // is re-derived every frame from the live player set.
        public bool[] PlayerRallyActive = new bool[MAX_PLAYERS];
        // PlayerRallyDurationLeft: seconds remaining on the active rally. Decremented by deltaTime
        // in RallySystem.Update; when it reaches 0, PlayerRallyActive is set to false. Default 0f.
        public float[] PlayerRallyDurationLeft = new float[MAX_PLAYERS];
        // PlayerRallyCooldown: seconds until the next rally can be triggered (after the previous one
        // expires). Decremented by deltaTime in RallySystem.Update; gated at 0f in the PlayerDamaged
        // event handler. Default 0f = "off cooldown, can trigger immediately".
        public float[] PlayerRallyCooldown = new float[MAX_PLAYERS];

        // ==================== Soul Harvest System (SOA) — Round 196 Direction 3 ====================
        // PlayerSoulCount: current accumulated soul currency. Each enemy kill adds EnemySoulValue
        // souls (Boss kills add 100× via EnemySoulValue on the Boss config). Decremented when the
        // player spends souls to cast a soul-cost skill. Clamped to [0, PlayerSoulCap] on every
        // add and per-frame regen tick. Default 0f = no soul balance.
        public float[] PlayerSoulCount = new float[MAX_PLAYERS];
        // PlayerSoulCap: per-player soul cap (default 999f). Souls cannot exceed this value.
        // Configurable via GameConfig.SoulHarvest.DefaultCap (loaded from soul_harvest.json
        // in a future round; currently hardcoded constant in SoulHarvestSystem).
        public float[] PlayerSoulCap = new float[MAX_PLAYERS];
        // PlayerSoulRegen: passive souls regenerated per second. Default 0f = no regen.
        // Decremented toward 0 by SoulHarvestSystem.Update via deltaTime. Useful for
        // "soul-drain towers" or level-up rewards that grant a slow trickle of souls.
        public float[] PlayerSoulRegen = new float[MAX_PLAYERS];
        // PlayerSoulSpentTotal: lifetime cumulative souls spent by this player (telemetry /
        // achievements). Incremented only on successful spend (when SoulCost was actually
        // deducted). Default 0f = no spending yet. Reset on AddPlayer.
        public float[] PlayerSoulSpentTotal = new float[MAX_PLAYERS];
        // PlayerSoulEarnedTotal: lifetime cumulative souls earned (telemetry / achievements).
 // Mirrors PlayerSoulSpentTotal but on the income side. Default0f.
 public float[] PlayerSoulEarnedTotal = new float[MAX_PLAYERS];

 // ==================== Side Quest System (SOA) — Round201 Direction7 ====================
 // Side quests are optional bonus objectives per level. Up to MAX_SIDE_QUESTS (8) per player.
 // Progress is tracked PER quest (by index in LevelConfig.SideQuests) so multiple quests can
 // advance simultaneously. All arrays default0/false → ObjectiveSystem fast-paths to zero
 // overhead when the level has no side quests (level.SideQuests.Count ==0).
 // ── MAX_SIDE_QUESTS =8: enough for the documented design ("1-3 per level" + buffer for
 // future quest types). Indexing is by LevelConfig.SideQuests list position, not by Id —
 // the system reads the definition list at InitializeFromLevel time and tracks progress
 // in the same order. Stable across levels because we reset on AddPlayer.
 public const int MAX_SIDE_QUESTS =8;
 // PlayerSideQuestProgress[i] = current accumulated progress for quest slot i.
 // For Type=KillCount, this is the kill count; for Type=Speed it's the elapsed seconds;
 // for Type=MinimalTowers it's the towers placed; for Type=NoDeath/NoHeal it's0/1.
 public int[] PlayerSideQuestProgress = new int[MAX_PLAYERS * MAX_SIDE_QUESTS];
 // PlayerSideQuestCompleted[i] = whether quest slot i has been completed (latch).
 // Once set to true, the quest never re-triggers SideQuestCompleted event.
 public bool[] PlayerSideQuestCompleted = new bool[MAX_PLAYERS * MAX_SIDE_QUESTS];
 // PlayerRunElapsedTime: total seconds elapsed since AddPlayer (for Speed
 // side quest + future "time bonus" calculations). Incremented by
 // ObjectiveSystem.Update during WavePhase. Default0f = start of run.
 public float[] PlayerRunElapsedTime = new float[MAX_PLAYERS];

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
            // Bullet-time: default to inactive (turns=0 → never enters SplitDeltaForBulletTime branch).
            // Reset both fields to avoid leaking prior slot occupant's active bullet-time into a new game.
            PlayerBulletTimeTurnsLeft[entityId] = 0f;
            PlayerBulletTimeScale[entityId] = 1f;
            // Wave Skip Reward: reset both counters to 0 so each new game starts fresh.
            PlayerWaveSkipsUsed[entityId] = 0;
            // Time Rewind snapshot ring: reset head/filled/tick so the new game starts with an empty buffer.
            // Existing snapshot HP/Mana/Shield are simply overwritten on the first tick.
            PlayerSnapshotHead[entityId] = 0;
            PlayerSnapshotFilled[entityId] = 0;
            PlayerSnapshotTick[entityId] = 0f;
            PlayerSkipBonusDamagePct[entityId] = 0f;
            // Combo Chain: reset both fields to 0 so a new game starts with no chain active.
            PlayerChainKillCount[entityId] = 0;
            PlayerChainKillBuffTimer[entityId] = 0f;

            // Round 196 Direction 3 — Soul Harvest: reset all soul state to defaults
            // so a recycled player entity (AddPlayer on the same slot) doesn't
            // inherit a stale soul balance / cap / regen from a prior game.
            // The SoulHarvestConfig defaults (DefaultCap / DefaultRegenPerSecond) are
            // applied by SoulHarvestSystem.ResetPlayer() — called from GameManager
            // initialization if the SoulHarvest system is wired. Here we defensively
            // zero the per-slot fields so a game without SoulHarvest wired still
            // starts with all soul arrays at 0.
            PlayerSoulCount[entityId] = 0f;
            PlayerSoulCap[entityId] = 0f; // 0 sentinel → SoulHarvestSystem uses config.DefaultCap
            PlayerSoulRegen[entityId] = 0f;
            PlayerSoulSpentTotal[entityId] = 0f;
            PlayerSoulEarnedTotal[entityId] =0f;

 // Round201 Direction7 — Side Quest progress + completed mask reset for this
 // player slot. Without this, a recycled player entity would inherit stale
 // quest state from the previous game. Defensive zero-out even when the
 // new level has no side quests (zero-cost1-write loop, MAX_SIDE_QUESTS=8).
 for (int q =0; q < MAX_SIDE_QUESTS; q++)
 {
 PlayerSideQuestProgress[entityId * MAX_SIDE_QUESTS + q] =0;
 PlayerSideQuestCompleted[entityId * MAX_SIDE_QUESTS + q] = false;
 }
 PlayerRunElapsedTime[entityId] =0f;

 // Round130 Inventory: reset all slots to empty (-1 item,0 count).
            ResetInventory(entityId);
            // Round 130 DamageBoost timer + multiplier must be zeroed; otherwise a recycled
            // player entity would inherit a stale attack buff (BUG scan finding).
            PlayerDamageBoostDuration[entityId] = 0;
            PlayerDamageBoostMultiplier[entityId] = 0f;

            PlayerEntityId = entityId;
            // Round175 Direction1 — Mana Shield: zero out the mana-shield pool,
 // cap and absorb ratio on game start. The ManaShieldSystem.Update()
 // will populate the cap from PlayerMaxMana on the first frame; the
 // pool and trigger latch stay at0 / false until mana flows in.
 PlayerManaShield[entityId] =0f;
 PlayerManaShieldCap[entityId] =0f;
 PlayerManaShieldAbsorbRatio[entityId] =1f; //1.0 = full-conversion baseline
 PlayerManaShieldTriggered[entityId] = false;
 // Round178 Direction6 — Pre-fight Buff: clear all selection state and
 // option slots so a recycled player entity does not inherit a stale
 // choice from the previous game. The PreFightBuffSystem.Update() will
 // re-roll options on the next BuildPhase tick.
 PlayerPreFightSelectedBuffId[entityId] = "";
 PlayerPreFightOption1Id[entityId] = "";
 PlayerPreFightOption2Id[entityId] = "";
 PlayerPreFightOption3Id[entityId] = "";
 PlayerPreFightOptionsRolled[entityId] = false;
 PlayerPreFightCritBonus[entityId] =0f;
 PlayerPreFightMaxHpMult[entityId] =1f;
 // Round174+ Direction3 — Momentum: fresh player starts at tier 0 / timer 0.
 // MomentumSystem will accumulate the timer as wave-time elapses.
 PlayerMomentumTimer[entityId] =0f;
 PlayerMomentumCurrentTier[entityId] =0;
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
            // Wave-skip reward: apply additive bonus (default 0 = no-op, backward compatible).
            // Skip-purchased bonus stacks additively so 3 skips @ 0.10 each = 0.30 (i.e. +30% dmg).
            float baseDmg = PlayerAttackDamage[playerId];
            float bonus = PlayerSkipBonusDamagePct[playerId];
            return bonus > 0f ? baseDmg * (1f + bonus) : baseDmg;
        }

        public void SetPlayerAttackDamage(int playerId, float damage)
        {
            if (!IsValidPlayer(playerId)) return;
            PlayerAttackDamage[playerId] = damage;
        }

        // ==================== Wave Skip Reward accessors ====================
        /// <summary>Returns how many wave-skip rewards this player has purchased this level.</summary>
        public int GetPlayerWaveSkipsUsed(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 0;
            return PlayerWaveSkipsUsed[playerId];
        }

        /// <summary>Sets the wave-skip purchase count (used by WaveSkipSystem at purchase time).</summary>
        public void SetPlayerWaveSkipsUsed(int playerId, int count)
        {
            if (!IsValidPlayer(playerId)) return;
            PlayerWaveSkipsUsed[playerId] = Math.Max(0, count);
        }

        /// <summary>Returns the cumulative additive damage bonus from wave-skip purchases (0 = none).</summary>
        public float GetPlayerSkipBonusDamagePct(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 0f;
            return PlayerSkipBonusDamagePct[playerId];
        }

        /// <summary>Sets the cumulative skip damage bonus (used by WaveSkipSystem at purchase time).</summary>
        public void SetPlayerSkipBonusDamagePct(int playerId, float pct)
        {
            if (!IsValidPlayer(playerId)) return;
            PlayerSkipBonusDamagePct[playerId] = Math.Max(0f, pct);
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

        // ==================== 一击必杀保护 (One-shot Protection) ====================
        public float GetPlayerMinHealthFloor(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 0f;
            return PlayerMinHealthFloor[playerId];
        }

        public void SetPlayerMinHealthFloor(int playerId, float floor)
        {
            if (!IsValidPlayer(playerId)) return;
            PlayerMinHealthFloor[playerId] = System.Math.Max(0f, floor);
        }

        // ==================== Reincarnation (Player 死后复活一次) ====================
        // Reincarnation is an "event" model (vs. one-shot protection's "floor" model):
        //   - Charges > 0 + not yet reincarnated → on HP<=0, restore HP to HealFraction * MaxHP, decrement charges, mark flag.
        //   - HealFraction clamped to [0, 1]. Charges clamped to >= 0.
        //   - HasReincarnated is reset on every call so SetPlayerReincarnationConfig (called on
        //     game start in GameManager.InitializePlayer) restores the "fresh save" state.
        public void SetPlayerReincarnationConfig(int playerId, int charges, float healFraction)
        {
            if (!IsValidPlayer(playerId)) return;
            PlayerReincarnationCharges[playerId] = System.Math.Max(0, charges);
            PlayerReincarnationHealFraction[playerId] = System.Math.Clamp(healFraction, 0f, 1f);
            PlayerHasReincarnated[playerId] = false;
        }

        // ==================== Bullet Time setter (direction 10) ====================
        // Activates bullet-time for the given player.
        // - turns: number of remaining turns (clamped to >= 0; 0 = no-op / immediate clear).
        // - scale: enemy/physics slow-mo factor (clamped to (0, 1] — 0.3 = enemies at 30% speed).
        //          The player's tower/attack systems still receive full dt; only enemy/AI/movement/spatial
        //          phases consume the scaled dt (see FrameScheduler.RunWavePhase).
        // Refreshing an active bullet-time with a new (turns, scale) overwrites both fields (no max).
        public void ActivateBulletTime(int playerId, float turns, float scale)
        {
            if (!IsValidPlayer(playerId)) return;
            PlayerBulletTimeTurnsLeft[playerId] = System.Math.Max(0f, turns);
            // Clamp scale to (0, 1] — never speed up enemies via this path; small epsilon avoids 0/divide issues.
            PlayerBulletTimeScale[playerId] = System.Math.Clamp(scale, 0.01f, 1.0f);
        }

        public int GetPlayerReincarnationCharges(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 0;
            return PlayerReincarnationCharges[playerId];
        }

        public bool GetPlayerHasReincarnated(int playerId)
        {
            if (!IsValidPlayer(playerId)) return false;
            return PlayerHasReincarnated[playerId];
        }

        public void DecreasePlayerHealth(int playerId, float damage)
        {
            if (!IsValidPlayer(playerId)) return;
            // Mana Shield (Round 175 Direction 1) absorbs damage BEFORE PlayerShield
            //   and PlayerCurrentHealth. Sentinel-gated: ratio ≤ 0 / shield ≤ 0 → inert
            //   (zero-cost path for the default 0 state, full backward compatibility).
            //   Effective damage taken from the pool is damage / ratio (so ratio = 2.0
            //   means 1 shield point absorbs 2 HP of damage).
            float manaShield = PlayerManaShield[playerId];
            float ratio = PlayerManaShieldAbsorbRatio[playerId];
            if (manaShield > 0f && ratio > 0f)
            {
                float poolDrain = damage / ratio;
                float absorbed = System.Math.Min(manaShield, poolDrain);
                PlayerManaShield[playerId] = manaShield - absorbed;
                damage -= absorbed * ratio;
                PlayerManaShieldTriggered[playerId] = true;
                if (damage <= 0f) return;
            }
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
            // One-shot protection: clamp current health so it never drops below PlayerMinHealthFloor.
            // Excess damage is absorbed (HP stays at floor). Floor=0 disables protection (backward compatible).
            float newHealth = PlayerCurrentHealth[playerId] - mitigatedDamage;
            float floor = PlayerMinHealthFloor[playerId];
            if (floor > 0f && newHealth < floor) newHealth = floor;
            float finalHealth = System.Math.Max(0f, newHealth);
            // Reincarnation: if HP would drop to 0 and we have unused charges, revive at HealFraction*MaxHP
            // instead. Charges decrement and HasReincarnated latches true. Modeled as an "event"
            // (vs. one-shot's "floor") — one revive per game per configured charge count.
            if (finalHealth <= 0f
                && PlayerReincarnationCharges[playerId] > 0
                && !PlayerHasReincarnated[playerId])
            {
                float maxHp = PlayerMaxHealth[playerId];
                finalHealth = System.Math.Max(1f, maxHp * PlayerReincarnationHealFraction[playerId]);
                PlayerReincarnationCharges[playerId]--;
                PlayerHasReincarnated[playerId] = true;
            }
            PlayerCurrentHealth[playerId] = finalHealth;
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

        // ==================== Totem System (SOA, pool-based) ====================
        // MAX_TOTEMS: global pool size shared across all players. A totem is a
        // placed stationary object (healing/mana/fire/stun) — small pool because
        // they're expensive and short-lived (10-15s).
        public const int MAX_TOTEMS = 32;
        // TotemActive: whether this pool slot currently has a live totem.
        public bool[] TotemActive = new bool[MAX_TOTEMS];
        // TotemOwnerId: which player placed this totem (-1 = none).
        public int[] TotemOwnerId = new int[MAX_TOTEMS];
        // TotemType: 0=None, 1=Healing, 2=Mana, 3=Searing (fire DoT), 4=Tremor (stun).
        public int[] TotemType = new int[MAX_TOTEMS];
        // TotemPosX/Y: world position the totem was placed at.
        public float[] TotemPosX = new float[MAX_TOTEMS];
        public float[] TotemPosY = new float[MAX_TOTEMS];
        // TotemDurationLeft: seconds remaining before auto-expire.
        public float[] TotemDurationLeft = new float[MAX_TOTEMS];
        // TotemChargesLeft: remaining trigger count (0 = unlimited time-based only).
        public int[] TotemChargesLeft = new int[MAX_TOTEMS];
        // TotemCooldown: seconds until next trigger (per-totem tick interval).
        public float[] TotemCooldown = new float[MAX_TOTEMS];
        // TotemPlayerCooldown: per-player cooldown after placing a totem (throttles spam).
        public float[] PlayerTotemCooldown = new float[MAX_PLAYERS];

        // ── Totem accessors (called by TotemSystem) ───────────────────────
        /// <summary>Allocate a totem slot from the pool. Returns -1 if full.</summary>
        public int AddTotem(int ownerId, int totemType, float x, float y, float duration, int charges, float triggerInterval)
        {
            for (int i = 0; i < MAX_TOTEMS; i++)
            {
                if (TotemActive[i]) continue;
                TotemActive[i] = true;
                TotemOwnerId[i] = ownerId;
                TotemType[i] = totemType;
                TotemPosX[i] = x;
                TotemPosY[i] = y;
                TotemDurationLeft[i] = duration;
                TotemChargesLeft[i] = charges;
                TotemCooldown[i] = triggerInterval; // first trigger after this many seconds
                return i;
            }
            return -1; // pool full
        }

        /// <summary>Remove a totem by slot id. No-op if already inactive.</summary>
        public void RemoveTotem(int totemId)
        {
            if (totemId < 0 || totemId >= MAX_TOTEMS) return;
            TotemActive[totemId] = false;
            TotemOwnerId[totemId] = -1;
            TotemType[totemId] = 0;
            TotemDurationLeft[totemId] = 0f;
            TotemChargesLeft[totemId] = 0;
            TotemCooldown[totemId] = 0f;
        }

        // ── Round 130 Inventory accessors ─────────────────────────────────────
        // Compute flat index into per-player inventory arrays.
        // Returns -1 on out-of-range inputs (caller must check). Mirrors the safety
        // contract of GetInventoryItemId/GetInventoryCount, so callers that bypass
        // the instance accessors (e.g., hot paths) get a sentinel instead of a
        // silently corrupted valid-looking index (BUG scan finding).
        public static int InventoryIndex(int playerId, int slot)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return -1;
            if (slot < 0 || slot >= MAX_INVENTORY_SLOTS) return -1;
            return playerId * MAX_INVENTORY_SLOTS + slot;
        }

        /// <summary>Reset all inventory slots for a player to empty (-1 item, 0 count).
        /// Call on player add / wave start. Defensive: also clamps out-of-range playerId.</summary>
        public void ResetInventory(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            for (int s = 0; s < MAX_INVENTORY_SLOTS; s++)
            {
                int idx = playerId * MAX_INVENTORY_SLOTS + s;
                PlayerInventoryItemId[idx] = -1;
                PlayerInventoryCount[idx] = 0;
            }
            PlayerInventoryUsed[playerId] = 0;
        }

        /// <summary>Get the item id at (playerId, slot) or -1 if empty/invalid.</summary>
        public int GetInventoryItemId(int playerId, int slot)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return -1;
            if (slot < 0 || slot >= MAX_INVENTORY_SLOTS) return -1;
            return PlayerInventoryItemId[playerId * MAX_INVENTORY_SLOTS + slot];
        }

        /// <summary>Get the stack count at (playerId, slot) or 0 if empty/invalid.</summary>
        public int GetInventoryCount(int playerId, int slot)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 0;
            if (slot < 0 || slot >= MAX_INVENTORY_SLOTS) return 0;
            return PlayerInventoryCount[playerId * MAX_INVENTORY_SLOTS + slot];
        }

        /// <summary>Number of non-empty slots for player (O(1) cached counter).</summary>
        public int GetInventoryUsed(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 0;
            return PlayerInventoryUsed[playerId];
        }
    }
}
