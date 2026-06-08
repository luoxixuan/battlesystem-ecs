using System;
using System.IO;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Tower placement system - handles tower construction, selling, and selection on the map.
    /// </summary>
    public class TowerPlacementSystem
    {
        private ComponentStore store;
        private IRenderer logger;
        private GameConfig gameConfig;
        // Round 145 Direction 3 — Per-Tower Modifier Pool reference. Optional injection;
        // when null, PlaceTower() skips the modifier roll and towers keep ModifierId=-1.
        private TowerModifierSystem? towerModifierSystem;

        // Sell ratio: fraction of upgrade cost refunded (0.5 = 50%)
        private float sellRatio = 0.5f;
        private float minSellRatio = 0.3f;
        private float sellRatioDecreasePerLevel = 0.05f;
        // Tower cost scaling: each additional tower of the same type costs more
        private float costIncrementPerCopy = 1.15f;
        private float costIncrementCap = 2.5f;
        // Sell-back value decay: refund ratio shrinks the longer a tower lives.
        // 0 disables decay; e.g. 0.005 = 0.5% of remaining ratio shaved per second.
        // 0 = no decay (legacy behavior), final ratio clamped to [minSellRatioDecayed, sellRatio].
        private float sellDecayPerSecond = 0.005f;
        private float minSellRatioDecayed = 0.2f;
        // Short grace period: towers sold within this many seconds of placement refund at full sellRatio.
        private float sellDecayGracePeriod = 2f;
        // Salvage upgrade rate: fraction of cumulative upgrade spend refunded on top of base sell ratio.
        // 0.3 = 30% of TowerTotalUpgradeSpent is returned, encouraging players to experiment with upgrades
        // knowing their gold investment is partially recoverable.
        private float salvageUpgradeRate = 0.3f;
        // Per-tower-type sell ratio override (Round 140 — Direction 7). Indexed by TowerType int value.
        // -1f = use the global sellRatio (legacy fallback). 0..1 = override that replaces the global
        // sellRatio for that type. The level-decay and time-decay are still applied on top of the
        // override value (clamped to [minSellRatio, 1]). Lets designers tune "rare → high refund",
        // "cheap → low refund" without code changes.
        private float[] sellRatioOverrideByType = new float[ComponentStore.MAX_TOWER_TYPES];

        public TowerPlacementSystem(ComponentStore store, IRenderer logger)
        {
            this.store = store;
            this.logger = logger;
            // Initialize all per-type overrides to -1f (use global). LoadSellConfig / LoadSellRatioOverrides
            // then populates any explicit entries from JSON.
            for (int i = 0; i < sellRatioOverrideByType.Length; i++) sellRatioOverrideByType[i] = -1f;
            LoadSellConfig();
            LoadPerTypeCaps();
        }

        /// <summary>
        /// Overload accepting GameConfig so debuff fields can be looked up from TowerConfig.
        /// </summary>
        public TowerPlacementSystem(ComponentStore store, IRenderer logger, GameConfig gameConfig)
        {
            this.store = store;
            this.logger = logger;
            this.gameConfig = gameConfig;
            for (int i = 0; i < sellRatioOverrideByType.Length; i++) sellRatioOverrideByType[i] = -1f;
            LoadSellConfig();
            LoadPerTypeCaps();
        }

        private void LoadSellConfig()
        {
            string basePath = AppDomain.CurrentDomain.BaseDirectory;
            string configPath = Path.Combine(basePath, "Data", "Configs", "tower_placement.json");
            if (File.Exists(configPath))
            {
                try
                {
                    string json = File.ReadAllText(configPath);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("sellRatio", out var sr)) sellRatio = sr.GetSingle();
                    if (root.TryGetProperty("minSellRatio", out var msr)) minSellRatio = msr.GetSingle();
                    if (root.TryGetProperty("sellRatioDecreasePerLevel", out var srdpl)) sellRatioDecreasePerLevel = srdpl.GetSingle();
                    if (root.TryGetProperty("costIncrementPerCopy", out var cicp)) costIncrementPerCopy = cicp.GetSingle();
                    if (root.TryGetProperty("costIncrementCap", out var cic)) costIncrementCap = cic.GetSingle();
                    // Sell-back value decay: optional, defaults preserve legacy behavior when omitted
                    if (root.TryGetProperty("sellDecayPerSecond", out var sdps)) sellDecayPerSecond = sdps.GetSingle();
                    if (root.TryGetProperty("minSellRatioDecayed", out var msrd)) minSellRatioDecayed = msrd.GetSingle();
                    if (root.TryGetProperty("sellDecayGracePeriod", out var sdgp)) sellDecayGracePeriod = sdgp.GetSingle();
                    // Salvage upgrade rate: optional, defaults to 0.3 (Round 85 direction 4)
                    if (root.TryGetProperty("salvageUpgradeRate", out var sur)) salvageUpgradeRate = sur.GetSingle();
                    // Per-tower-type sell ratio override (Round 140 — Direction 7). Optional map
                    // { "typeIdx": ratio, ... }. Entries replace the global sellRatio for that
                    // TowerType. Missing entries / -1 / out-of-range → keep the existing value
                    // (initialized to -1f in the constructor → falls back to global).
                    if (root.TryGetProperty("sellRatioOverrideByType", out var srobt)
                        && srobt.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        int loadedOverrides = 0;
                        foreach (var prop in srobt.EnumerateObject())
                        {
                            if (!int.TryParse(prop.Name, out int typeIdx)) continue;
                            if (typeIdx < 0 || typeIdx >= ComponentStore.MAX_TOWER_TYPES) continue;
                            float ratio = prop.Value.GetSingle();
                            if (ratio < 0f || ratio > 1f) continue; // ignore nonsensical values
                            sellRatioOverrideByType[typeIdx] = ratio;
                            loadedOverrides++;
                        }
                        if (loadedOverrides > 0)
                            logger.Log($"[TOWER] Per-type sell ratio overrides loaded: {loadedOverrides} entries");
                    }
                }
                catch { /* use defaults */ }
            }
        }

        /// <summary>
        /// Round 139 — Per-Type Placement Cap. Loads the per-tower-type cap matrix from
        /// tower_placement.json's `maxPerTypeByType` map. Each entry is keyed by the
        /// TowerType enum int value (0..MAX_TOWER_TYPES-1). 0 = unlimited.
        /// Loaded into <see cref="ComponentStore.PlayerTowersOfTypeCap"/> for every player
        /// (single-player game; player 0 is the only consumer).
        /// </summary>
        public void LoadPerTypeCaps()
        {
            string basePath = AppDomain.CurrentDomain.BaseDirectory;
            string configPath = Path.Combine(basePath, "Data", "Configs", "tower_placement.json");
            if (!File.Exists(configPath)) return;
            try
            {
                string json = File.ReadAllText(configPath);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("maxPerTypeByType", out var map)) return;
                if (map.ValueKind != System.Text.Json.JsonValueKind.Object) return;
                // Player 0 is the only consumer in this single-player build, but fill all players
                // for future-proofing.
                int loaded = 0;
                foreach (var prop in map.EnumerateObject())
                {
                    if (!int.TryParse(prop.Name, out int typeIdx)) continue;
                    if (typeIdx < 0 || typeIdx >= ComponentStore.MAX_TOWER_TYPES) continue;
                    int cap = prop.Value.GetInt32();
                    for (int pid = 0; pid < ComponentStore.MAX_PLAYERS; pid++)
                    {
                        store.PlayerTowersOfTypeCap[pid * ComponentStore.MAX_TOWER_TYPES + typeIdx] = cap;
                    }
                    loaded++;
                }
                logger.Log($"[TOWER] Per-type placement caps loaded: {loaded} entries");
            }
            catch (Exception ex)
            {
                logger.Log($"[TOWER] LoadPerTypeCaps failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Round 145 Direction 3 — Inject the per-tower modifier system. Called by the registry
        /// before PlaceTower() is invoked by gameplay. Optional — if never called, towers spawn
        /// with TowerModifierId=-1 (the sentinel no-op default).
        /// </summary>
        public void SetTowerModifierSystem(TowerModifierSystem modifierSystem) => towerModifierSystem = modifierSystem;

        /// <summary>
        /// Calculate the effective sell ratio for a given tower level.
        /// Ratio decreases per level but never drops below minSellRatio.
        /// Round 140 — Direction 7: if a per-type sellRatioOverride is set (>= 0), that value
        /// replaces the global sellRatio as the base before level decay is applied.
        /// </summary>
        private float GetEffectiveSellRatio(int towerLevel, int towerTypeIndex)
        {
            float baseRatio = sellRatio;
            if (towerTypeIndex >= 0 && towerTypeIndex < sellRatioOverrideByType.Length
                && sellRatioOverrideByType[towerTypeIndex] >= 0f)
            {
                baseRatio = sellRatioOverrideByType[towerTypeIndex];
            }
            float ratio = baseRatio - (towerLevel - 1) * sellRatioDecreasePerLevel;
            return Math.Max(ratio, minSellRatio);
        }

        /// <summary>
        /// Apply sell-back value decay based on how long the tower has been placed.
        /// Linear decay: ratio shrinks by sellDecayPerSecond per second of age,
        /// after a short grace period. Clamped to [minSellRatioDecayed, sellRatio].
        /// sellDecayPerSecond = 0 disables decay (legacy behavior).
        /// </summary>
        private float GetDecayedSellRatio(float placeTime, float baseRatio)
        {
            if (sellDecayPerSecond <= 0f) return baseRatio;
            float age = Time.TotalTime - placeTime;
            if (age <= sellDecayGracePeriod) return baseRatio;
            float effectiveAge = age - sellDecayGracePeriod;
            float decayed = baseRatio - sellDecayPerSecond * effectiveAge;
            // Clamp to [minSellRatioDecayed, baseRatio]
            if (decayed < minSellRatioDecayed) decayed = minSellRatioDecayed;
            if (decayed > baseRatio) decayed = baseRatio;
            return decayed;
        }

        /// <summary>
        /// Compute the scaled placement cost for a tower type.
        /// Formula: baseCost * min(costIncrementCap, costIncrementPerCopy ^ copyCount)
        /// The count is the number of towers of this type already placed (not including this one).
        /// </summary>
        private float ComputeScaledCost(float baseCost, int towerTypeIndex, int playerId)
        {
            if (towerTypeIndex < 0 || towerTypeIndex >= store.PlacementCountByType.Length)
                return baseCost; // unknown type — no scaling
            int count = store.PlacementCountByType[towerTypeIndex];
            if (count <= 0) return baseCost;

            float scale = (float)Math.Pow(costIncrementPerCopy, count);
            if (scale > costIncrementCap) scale = costIncrementCap;
            return baseCost * scale;
        }

        /// <summary>
        /// Place a tower at the specified location (legacy overload, no debuff support).
        /// </summary>
        public int PlaceTower(int x, int y, TowerType type, float damage, int range, float speed, float cost)
        {
            // 1. Check if position is valid
            if (x < 0 || x >= 10 || y < 0 || y >= 20)
            {
                logger.Log("[TOWER] PlaceTower failed: position out of map range");
                return -1;
            }

            // 2. Compute scaled cost based on how many of this type are already placed
            int towerTypeIndex = (int)type;
            float scaledCost = ComputeScaledCost(cost, towerTypeIndex, 1);
            if (scaledCost != cost)
            {
                logger.Log($"[TOWER] {type} cost scaled: {cost:F0} → {scaledCost:F0} (×{scaledCost / cost:F2}, copy #{store.PlacementCountByType[towerTypeIndex] + 1})");
            }

            // 3. Check if position already has a tower — Round 95: O(1) via TileOccupied cache.
            // The cache is the source of truth; the legacy ActiveTowerIds scan is kept as a
            // defensive fallback in case a future code path skips the cache write.
            if (store.IsTileOccupied(x, y))
            {
                logger.Log($"[TOWER] PlaceTower failed: position ({x},{y}) already has a tower");
                return -1;
            }
            foreach (int tid in store.ActiveTowerIds)
            {
                if (store.PositionX[tid] == x && store.PositionY[tid] == y)
                {
                    logger.Log($"[TOWER] PlaceTower failed: position ({x},{y}) already has a tower (defensive scan)");
                    return -1;
                }
            }

            // 3.5. Check tower cap (max towers limit per player)
            int playerId = 0; // single-player for now, 0 = player 0
            int currentTowerCount = store.PlayerTowerCount[playerId];
            int maxTowers = store.PlayerMaxTowers[playerId];
            // Default to 20 if not configured
            if (maxTowers <= 0) maxTowers = 20;
            if (currentTowerCount >= maxTowers)
            {
                logger.Log($"[TOWER] PlaceTower failed: tower cap reached ({currentTowerCount}/{maxTowers}). Sell or upgrade a tower first.");
                return -1;
            }

            // 3.6. Check per-type cap (Round 139 — Direction 2). Enforces maxPerTypeByType from
            // tower_placement.json so players can't spam a single dominant type. Cap of 0
            // means "no cap" (default before LoadPerTypeCaps is called).
            int towerTypeIdx = (int)type;
            if (towerTypeIdx >= 0 && towerTypeIdx < ComponentStore.MAX_TOWER_TYPES)
            {
                int perTypeCount = store.PlayerTowersOfType[playerId * ComponentStore.MAX_TOWER_TYPES + towerTypeIdx];
                int perTypeCap = store.PlayerTowersOfTypeCap[playerId * ComponentStore.MAX_TOWER_TYPES + towerTypeIdx];
                if (perTypeCap > 0 && perTypeCount >= perTypeCap)
                {
                    logger.Log($"[TOWER] PlaceTower failed: per-type cap reached for {type} ({perTypeCount}/{perTypeCap}). Mix tower types.");
                    return -1;
                }
            }

            // 4. Create tower entity
            int towerId = store.CreateEntity();
            if (towerId == -1)
            {
                logger.Log("[TOWER] PlaceTower failed: entity creation failed (entity pool exhausted)");
                return -1;
            }

            store.AddPosition(towerId, x, y);
            // Round 95: O(1) tile occupancy cache write. Must run AFTER AddPosition so
            // DestroyEntity can read PositionX/Y and release the same tile.
            store.SetTileOccupied(x, y, true);
            // Try to look up debuff params from gameConfig if available
            if (gameConfig != null)
            {
                var tc = gameConfig.GetTowerConfig(type.ToString());
                if (tc != null)
                {
                    // Read tower's configured upgrade path, default to "standard"
                    string upgradePath = tc.UpgradePath;
                    if (string.IsNullOrEmpty(upgradePath)) upgradePath = "standard";
                    store.AddTower(towerId, type, damage, range, speed, 1, cost, upgradePath,
                        tc.StunChance, tc.SlowAmount, tc.SlowDuration, tc.DamageType, tc.TurnRate);
                    // Round 124 — Apply disarm params from config (independent of AddTower signature;
                    // AddTower leaves both fields at 0 by default, so this is a safe post-fill).
                    store.TowerDisarmChance[towerId] = tc.DisarmChance;
                    store.TowerDisarmDuration[towerId] = tc.DisarmDuration;
                    // Apply tower targeting mode from config
                    store.SetTowerTargetingMode(towerId, tc.TargetingMode);
                    // Apply ammo system if configured (0 = unlimited)
                    if (tc.MaxAmmo > 0)
                    {
                        store.TowerMaxAmmo[towerId] = tc.MaxAmmo;
                        store.TowerCurrentAmmo[towerId] = tc.MaxAmmo;
                        store.TowerReloadTime[towerId] = tc.ReloadTime;
                        store.TowerIsReloading[towerId] = false;
                    }
                    // Apply homing projectile flag for tracking towers
                    store.SetTowerProjectileHoming(towerId, tc.ProjectileHoming);
                    // Round 114 — Predictive Aim / Lead Targeting
                    // Apply lead-aim factor for Sniper / Cannon towers (any tower that
                    // shoots slow projectiles at moving enemies). 0 = straight aim
                    // (default), > 0 = lead target based on its current motion. Capped
                    // at [0, 2] inside the accessor.
                    store.SetTowerLeadAimFactor(towerId, tc.LeadAimFactor);
                    // Apply intercept rate for PointDefense towers
                    store.SetTowerInterceptRate(towerId, tc.InterceptRate);
                    // Apply bounce projectile settings
                    store.TowerBouncesRemaining[towerId] = tc.Bounces;
                    store.TowerBounceRange[towerId] = tc.BounceRange;
                    store.TowerBounceDamageFalloff[towerId] = tc.BounceDamageFalloff;
                    // Apply Multi-Strike settings (Round 201 Direction 1) — each attack also hits
                    // N+1 nearest enemies within MultiStrikeRange of the primary target.
                    store.TowerMultiStrikeCount[towerId] = tc.MultiStrikeCount;
                    store.TowerMultiStrikeRange[towerId] = tc.MultiStrikeRange;
                    store.TowerMultiStrikeDamageMult[towerId] = tc.MultiStrikeDamageMult;
                    // Round 201 Direction 8 — Echo Clone settings. The clone is spawned on
                    // hit by TowerAttackSystem (or EchoCloneSystem) and inherits these fields.
                    // 0/0f/0.6f/5f defaults mean non-echo towers pay one write per field but
                    // EchoCloneSystem.Update fast-returns on no echo parents.
                    // Note: TowerEchoDamageMult defaults to 1f in the store (matches parent),
                    // but the config default 0.6f is applied here for consistency with designers'
                    // intuition. TowerEchoSpawnCooldown is reset to 0 (ready to fire) on placement.
                    // We deliberately do NOT touch TowerIsEcho (false unless echo is active)
                    // or TowerEchoExpireTurn (-1 sentinel) here — those are owned by EchoCloneSystem.
                    // TowerEchoParentId is initialized to -1 (sentinel: not an echo).
                    store.TowerEchoDamageMult[towerId] = tc.EchoDamageMult;
                    // The remaining echo fields (SpawnsEcho chance + duration + cooldown) are
                    // tower-config inputs — stored on the parent tower for the echo system to
                    // read at spawn time. We add them as new SOA fields below.
                    // Round 201 Direction 8 — Echo spawn-config (read at attack time by TowerAttackSystem
                    //   to roll the dice on each fired shot). Defaults to all-zero (no echo), so non-echo
                    //   towers pay a single bool+float read per attack. TowerCanSpawnEcho is the sentinel
                    //   for the hot-path fast-return; TowerEchoChance and TowerEchoDuration are the
                    //   per-shot roll probability and per-clone lifetime, respectively.
                    store.TowerCanSpawnEcho[towerId] = tc.SpawnsEcho > 0f && tc.EchoDuration > 0f;
                    store.TowerEchoChance[towerId] = tc.SpawnsEcho;
                    store.TowerEchoDuration[towerId] = tc.EchoDuration;
                    // SpawnCooldown field is the per-parent minimum seconds between echoes. Copied
                    //   here at placement; reset to 0 (=ready) so the first attack can spawn immediately.
                    //   EchoCloneSystem.Update decrements this each frame; when 0, a successful echo
                    //   roll will spawn and reset the cooldown to tc.EchoSpawnCooldown.
                    store.TowerEchoSpawnCooldown[towerId] = 0f;
                    // MaxCooldown is the upper bound the counter resets to after a successful spawn.
                    store.TowerEchoMaxCooldown[towerId] = tc.EchoSpawnCooldown;
                    // Apply kill-triggered player sustain (Leech/Vampiric/Soul-Drain tower family)
                    // Both default to 0 in TowerConfig, so non-leech towers are unaffected.
                    store.TowerHealOnKillAmount[towerId] = tc.HealOnKillAmount;
                    store.TowerManaOnKillAmount[towerId] = tc.ManaOnKillAmount;
                    // Apply elemental affinity (same-element bonus damage)
                    store.TowerElementalAffinity[towerId] = tc.ElementalAffinity;
                    store.TowerElementalAffinityBonus[towerId] = tc.ElementalAffinityBonus;
                    // Apply on-hit lifesteal (Vampire tower family). Both default 0 in
                    // TowerConfig, so non-vampire towers are unaffected.
                    store.TowerLifestealFraction[towerId] = tc.LifestealFraction;
                    store.TowerLifestealMaxPerFrame[towerId] = tc.LifestealMaxPerFrame;
                    // Round 184 Direction 7 — Volley / Multi-Pellet Tower (scatter/shotgun).
                    //   Apply ProjectileCount + PelletDamageMult + PelletConeRadius to the store
                    //   so TowerAttackSystem's scatter branch fires N pellets per attack.
                    //   All 3 default to inert (1 / 1.0 / 0.0) → single-shot, zero-overhead path.
                    store.TowerProjectileCount[towerId] = tc.ProjectileCount;
                    store.TowerPelletDamageMult[towerId] = tc.PelletDamageMult;
                    store.TowerPelletConeRadius[towerId] = tc.PelletConeRadius;
                    // Apply taunt tower properties (force-enemy-target-this-tower aura)
                    // Both default false/0 in TowerConfig, so non-taunt towers are inert.
                    store.TowerIsTaunt[towerId] = tc.IsTauntTower;
                    store.TowerTauntRadius[towerId] = tc.TauntRadius;
                    // Apply turn rate for turret rotation delay (already set via AddTower params)
                    // Initialize facing angle to point at nearest enemy (or 0 if none)
                    store.TowerFacingAngle[towerId] = 0f;
                    // Apply tower's innate special ability (e.g., chain_lightning for Tesla)
                    if (tc.SpecialAbility != null)
                    {
                        ApplyTowerSpecialAbility(store, towerId, tc.SpecialAbility);
                        logger.Log($"[TOWER] {tc.Name} 固有能力: {tc.SpecialAbility.AbilityType}");
                    }
                    // Apply demolish config if tower supports sacrifice
                    if (tc.Demolish != null)
                    {
                        store.TowerDemolishEffectRadius[towerId] = tc.Demolish.DemolishRadius;
                        store.TowerDemolishDamage[towerId] = tc.Demolish.DemolishDamage;
                        store.TowerDemolishEffectType[towerId] = tc.Demolish.DemolishEffectType;
                        store.TowerDemolishDotDamage[towerId] = tc.Demolish.DemolishDotDamagePerTick;
                        store.TowerDemolishDotDuration[towerId] = tc.Demolish.DemolishDotDuration;
                        store.TowerDemolishDotInterval[towerId] = tc.Demolish.DemolishDotInterval > 0f
                            ? tc.Demolish.DemolishDotInterval : 1f;
                        store.TowerDemolishStunDuration[towerId] = tc.Demolish.DemolishStunDuration;
                        logger.Log($"[TOWER] {tc.Name} 牺牲效果: 半径 {tc.Demolish.DemolishRadius}, 伤害 {tc.Demolish.DemolishDamage}");
                    }
                    // Apply income tower properties (passive gold generation)
                    if (tc.IsIncomeTower)
                    {
                        store.TowerIsIncomeTower[towerId] = true;
                        store.TowerGoldPerSecond[towerId] = tc.GoldPerSecond;
                        logger.Log($"[TOWER] {tc.Name} 经济塔: 每秒 +{tc.GoldPerSecond} 金币");
                    }
                    // Apply curse tower properties (debuff aura)
                    if (tc.IsCurseTower)
                    {
                        store.TowerIsCurseTower[towerId] = true;
                        store.TowerCurseRadius[towerId] = tc.CurseRadius;
                        store.TowerCurseDmgReduction[towerId] = tc.CurseDmgReduction;
                        store.TowerCurseSpeedReduction[towerId] = tc.CurseSpeedReduction;
                        store.TowerCurseArmorReduction[towerId] = tc.CurseArmorReduction;
                        store.TowerCurseDmgTakenIncrease[towerId] = tc.CurseDmgTakenIncrease;
                        logger.Log($"[TOWER] {tc.Name} 诅咒塔: 半径 {tc.CurseRadius}, 减伤 {tc.CurseDmgReduction}, 减速 {tc.CurseSpeedReduction}, 护甲削减 {tc.CurseArmorReduction}, 增伤 {tc.CurseDmgTakenIncrease}");
                    }
                    // Apply heal aura properties (Round 122 Dir 2) — passive tower-to-tower healing.
                    //   Opt-in: only writers when radius>0 && amount>0. Interval 0 = fire every frame
                    //   (discouraged; designers should pick >= 0.25s). The system targets PalisadeHP
                    //   (the only tower type with a HP pool) — non-Palisade towers are not healed.
                    if (tc.HealAuraRadius > 0f && tc.HealAuraAmount > 0f)
                    {
                        store.TowerHealAuraRadius[towerId] = tc.HealAuraRadius;
                        store.TowerHealAuraAmount[towerId] = tc.HealAuraAmount;
                        store.TowerHealAuraInterval[towerId] = tc.HealAuraInterval;
                        // Timer starts at 0 (= "fire next frame"). For interval>0 the per-tick
                        // logic resets it to interval after firing.
                        store.TowerHealAuraTimer[towerId] = 0f;
                        logger.Log($"[TOWER] {tc.Name} 治疗塔: 半径 {tc.HealAuraRadius}, 治疗 {tc.HealAuraAmount}/tick, 间隔 {tc.HealAuraInterval}s");
                    }
                    // Apply thorns aura properties (Round 126 Dir 4) — passive tower-centered damage aura.
                    //   Opt-in via tc.IsThornsTower=true. We write all 5 fields together so a thorns
                    //   tower config stays atomic (radius=0 + dps=0 + interval=0 means "no aura" and
                    //   the SetTurn filter will still cache the tower, but Update's defensive checks
                    //   bail before doing work). The system applies raw HP damage to enemies in
                    //   range; no shield, no resistance (intentionally simple — thorns is a
                    //   constant standing pressure zone, not a one-shot nuke).
                    if (tc.IsThornsTower)
                    {
                        store.TowerIsThornsTower[towerId] = true;
                        store.TowerThornsRadius[towerId] = tc.ThornsRadius;
                        store.TowerThornsDps[towerId] = tc.ThornsDps;
                        store.TowerThornsInterval[towerId] = tc.ThornsInterval;
                        // Timer starts at 0 (= "fire next frame"). For interval>0 the per-tick
                        // logic resets it to interval after firing.
                        store.TowerThornsTimer[towerId] = 0f;
                        logger.Log($"[TOWER] {tc.Name} 荆棘塔: 半径 {tc.ThornsRadius}, DPS {tc.ThornsDps}, 间隔 {tc.ThornsInterval}s");
                    }
                    // Apply pull tower properties (gravitational pull)
                    if (tc.IsPullTower)
                    {
                        store.TowerIsPullTower[towerId] = true;
                        store.TowerPullStrength[towerId] = tc.PullStrength;
                        store.TowerPullRadius[towerId] = tc.PullRadius;
                        store.TowerPullCooldown[towerId] = tc.PullCooldown;
                        store.TowerPullTimer[towerId] = 0f;
                        logger.Log($"[TOWER] {tc.Name} 牵引塔: 半径 {tc.PullRadius}, 拉力 {tc.PullStrength}, 冷却 {tc.PullCooldown}");
                    }
                    // Apply bleed tower properties (stacking physical DoT)
                    if (tc.IsBleedTower)
                    {
                        store.TowerIsBleedTower[towerId] = tc.IsBleedTower;
                        store.TowerBleedStacksPerHit[towerId] = tc.BleedStacksPerHit;
                        store.TowerBleedDmgPct[towerId] = tc.BleedDmgPct;
                        store.TowerBleedTickInterval[towerId] = tc.BleedTickInterval > 0f ? tc.BleedTickInterval : 1f;
                        store.TowerBleedMaxStacks[towerId] = tc.BleedMaxStacks;
                        store.TowerBleedDuration[towerId] = tc.BleedDuration;
                        logger.Log($"[TOWER] {tc.Name} 流血塔: 每击 {tc.BleedStacksPerHit} 层, 伤害 {tc.BleedDmgPct * 100}% HP/层, 间隔 {tc.BleedTickInterval}s, 最大 {tc.BleedMaxStacks} 层");
                    }
                    // Apply Death Mark tower properties (Round 200 Direction 5 — counter + execute)
                    if (tc.IsDeathMarkTower)
                    {
                        store.TowerIsDeathMarkTower[towerId] = true;
                        store.TowerDeathMarkChance[towerId] = tc.DeathMarkChance;
                        store.TowerDeathMarkStacksPerHit[towerId] = tc.DeathMarkStacksPerHit > 0 ? tc.DeathMarkStacksPerHit : 1;
                        logger.Log($"[TOWER] {tc.Name} 死亡印记塔: 概率 {tc.DeathMarkChance * 100}%, 每击 {tc.DeathMarkStacksPerHit} 层");
                    }
                    // Apply chrono tower properties (time dilation field)
                    if (tc.IsChronoTower)
                    {
                        store.TowerIsChronoTower[towerId] = true;
                        store.TowerTimeFieldRadius[towerId] = tc.TimeFieldRadius;
                        store.TowerTimeScale[towerId] = tc.TimeScale;
                        logger.Log($"[TOWER] {tc.Name} 时间塔: 半径 {tc.TimeFieldRadius}, 时间缩放 {tc.TimeScale:F1}x");
                    }
                    // Apply per-tower active skill (Round 138) — manual cast. Opt-in:
                    //   tc.ActiveSkillId >= 0 enables the system; we delegate to the helper
                    //   so the wiring stays in one place (reset + placement both reach here).
                    if (tc.ActiveSkillId >= 0)
                    {
                        store.SetTowerActiveSkill(towerId, tc.ActiveSkillId, tc.ActiveCooldown);
                        logger.Log($"[TOWER] {tc.Name} 主动技能: skillId={tc.ActiveSkillId}, 冷却 {tc.ActiveCooldown}s");
                    }
                    // Apply deployable trap properties (passive trigger on enemy walk-in)
                    if (tc.IsTrap)
                    {
                        store.TowerIsTrap[towerId] = true;
                        store.TowerTrapTriggerRadius[towerId] = tc.TrapTriggerRadius;
                        store.TowerTrapCharges[towerId] = tc.TrapCharges;
                        store.TowerTrapEffectType[towerId] = tc.TrapEffectType;
                        store.TowerTrapEffectValue[towerId] = tc.TrapEffectValue;
                        logger.Log($"[TOWER] {tc.Name} 陷阱塔: 触发半径 {tc.TrapTriggerRadius}, 充能 {tc.TrapCharges}, 效果 {tc.TrapEffectType} 值 {tc.TrapEffectValue}");
                    }
                    // Round 186 Direction 2 — Sapper-vulnerable HP pool. 0 = indestructible
                    // (default; legacy path). When tc.MaxHp > 0, the tower has a finite
                    // HP pool that Sapper enemies can damage, and the TowerAttackSystem
                    // hot path skips towers with TowerCurrentHp <= 0. The slow multiplier
                    // (TowerSapperSlowMult) is reset to 0 in BeginFrame each tick and
                    // re-derived by SapperSystem.RecomputeTowerSlows after attacks.
                    if (tc.MaxHp > 0f)
                    {
                        store.TowerMaxHp[towerId] = tc.MaxHp;
                        store.TowerCurrentHp[towerId] = tc.MaxHp;
                        store.TowerSapperSlowMult[towerId] = 0f;
                        logger.Log($"[TOWER] {tc.Name} 启用血量池: {tc.MaxHp:F0} HP (Sapper 可破坏)");
                    }
                    // Apply construction delay if configured (tower starts building, cannot attack)
                    if (tc.ConstructionTime > 0f)
                    {
                        store.TowerIsConstructing[towerId] = true;
                        store.TowerConstructionProgress[towerId] = 0f;
                        store.TowerConstructionTime[towerId] = tc.ConstructionTime;
                        store.TowerConstructionHP[towerId] = tc.ConstructionHP;
                        store.TowerConstructionMaxHP[towerId] = tc.ConstructionHP;
                        store.TowerIsVulnerableDuringConstruction[towerId] = tc.IsVulnerableDuringConstruction;
                        logger.Log($"[TOWER] {tc.Name} 进入建造阶段: 需 {tc.ConstructionTime}s, HP {tc.ConstructionHP}");
                    }
                    // Apply fog of war vision radius (0 = no fog restriction)
                    if (tc.VisionRadius > 0f)
                    {
                        store.TowerVisionRadius[towerId] = tc.VisionRadius;
                        logger.Log($"[TOWER] {tc.Name} 视野: 半径 {tc.VisionRadius}");
                    }
                    // Apply patrol tower properties (mobile tower on patrol path)
                    if (tc.IsMobile)
                    {
                        store.TowerIsMobile[towerId] = true;
                        store.TowerMoveSpeed[towerId] = tc.MoveSpeed > 0f ? tc.MoveSpeed : 3f;
                        store.TowerPatrolPathId[towerId] = tc.PatrolPathId >= 0 ? tc.PatrolPathId : 0;
                        store.TowerPatrolWaypointIndex[towerId] = 0;
                        store.TowerPatrolDirection[towerId] = tc.PatrolDirection >= 0 ? tc.PatrolDirection : 1;
                        store.TowerPatrolAttackSpeedPenalty[towerId] = tc.PatrolAttackSpeedPenalty > 0f
                            ? tc.PatrolAttackSpeedPenalty : 0.75f;
                        logger.Log($"[TOWER] {tc.Name} 巡逻塔: 路径 {store.TowerPatrolPathId[towerId]}, 速度 {store.TowerMoveSpeed[towerId]}, 攻速惩罚 {store.TowerPatrolAttackSpeedPenalty[towerId]}");
                    }
                    // Apply burst fire properties (salvo mode)
                    if (tc.BurstCount > 0)
                    {
                        store.TowerBurstCount[towerId] = tc.BurstCount;
                        store.TowerBurstInterval[towerId] = tc.BurstInterval > 0f ? tc.BurstInterval : 0.1f;
                        store.TowerBurstCooldown[towerId] = tc.BurstCooldown > 0f ? tc.BurstCooldown : 1f;
                        store.TowerBurstTimer[towerId] = 0f;
                        store.TowerBurstShotsFired[towerId] = 0;
                        logger.Log($"[TOWER] {tc.Name} 爆发射击: {tc.BurstCount} 发, 间隔 {tc.BurstInterval}s, 冷却 {tc.BurstCooldown}s");
                    }
                    // Apply ramp-up / spool-up damage properties
                    if (tc.RampUpRate > 0f)
                    {
                        store.TowerRampUpRate[towerId] = tc.RampUpRate;
                        store.TowerRampUpMax[towerId] = tc.RampUpMax > 1f ? tc.RampUpMax : 1f;
                        store.TowerRampUpCurrent[towerId] = 1f;
                        store.TowerRampUpTargetId[towerId] = -1;
                        store.TowerRampUpResetOnSwitch[towerId] = tc.RampUpResetOnSwitch;
                        logger.Log($"[TOWER] {tc.Name} 升温伤害: +{tc.RampUpRate * 100:F0}%/击, 上限 ×{tc.RampUpMax:F1}, 切换目标重置={tc.RampUpResetOnSwitch}");
                    }
                    // Apply damage type conversion properties
                    if (tc.DamageConversionRatio > 0f)
                    {
                        store.TowerDamageConversionRatio[towerId] = tc.DamageConversionRatio;
                        store.TowerConvertedDamageType[towerId] = tc.ConvertedDamageType;
                        logger.Log($"[TOWER] {tc.Name} 伤害转换: {tc.DamageConversionRatio * 100:F0}% → {tc.ConvertedDamageType}");
                    }
                    // Apply mana drain properties (Round 101 Direction 10)
                    if (tc.ManaDrainPct > 0f)
                    {
                        store.TowerManaDrainPct[towerId] = tc.ManaDrainPct;
                        store.TowerManaDrainCap[towerId] = tc.ManaDrainCap; // 0 = use global cap
                        logger.Log($"[TOWER] {tc.Name} 吸敌法: {tc.ManaDrainPct * 100:F0}%/击, 上限 {tc.ManaDrainCap:F0}");
                    }
                    // Apply overkill / excess damage properties
                    if (tc.OverkillType > 0 && tc.OverkillRatio > 0f && tc.OverkillRadius > 0f)
                    {
                        store.TowerOverkillType[towerId] = tc.OverkillType;
                        store.TowerOverkillRatio[towerId] = tc.OverkillRatio;
                        store.TowerOverkillRadius[towerId] = tc.OverkillRadius;
                        logger.Log($"[TOWER] {tc.Name} 过量伤害: 类型 {tc.OverkillType}, 比例 {tc.OverkillRatio * 100:F0}%, 半径 {tc.OverkillRadius}");
                    }
                }
                else
                {
                    store.AddTower(towerId, type, damage, range, speed, 1, cost);
                }
            }
            else
            {
                store.AddTower(towerId, type, damage, range, speed, 1, cost);
            }

            // Record placement timestamp for sell-back value decay (sellDecayPerSecond > 0).
            // Defaults to 0 when sellDecay is disabled, so GetDecayedSellRatio early-outs.
            store.TowerPlaceTime[towerId] = sellDecayPerSecond > 0f ? Time.TotalTime : 0f;

            // Increment placement count for cost scaling (after successful placement)
            int incType = (int)type;
            if (incType >= 0 && incType < store.PlacementCountByType.Length)
                store.PlacementCountByType[incType]++;

            logger.Log($"[TOWER] {type} placed at ({x},{y})");
            logger.Log($"[TOWER] Tower placed: {type} at ({x},{y}), damage: {damage}, range: {range}, ID: {towerId}");

            // Round 100 — Palisade tower post-place init. Sets the SOA fields that drive the
            // EnemyMovementSystem stun-on-collision check. Damage=0 + range=0 by design.
            if (type == TowerType.Palisade)
            {
                store.TowerIsPalisade[towerId] = true;
                store.PalisadeStunFrames[towerId] = PalisadeConfig.DefaultPalisadeStunFrames;
                store.PalisadeBlockRadius[towerId] = PalisadeConfig.DefaultPalisadeBlockRadius;
                store.PalisadeHP[towerId] = PalisadeConfig.DefaultPalisadeHP;
                store.PalisadeMaxHP[towerId] = PalisadeConfig.DefaultPalisadeHP;
                // Maintain ActivePalisadeCount for O(1) early-out in EnemyMovementSystem
                store.ActivePalisadeCount++;
                logger.Log($"[PALISADE] Tower #{towerId} at ({x},{y}): stunFrames={store.PalisadeStunFrames[towerId]}, blockRadius={store.PalisadeBlockRadius[towerId]}, HP={store.PalisadeHP[towerId]}");
            }

            // Round 106 Direction 2 — Mine tower post-place init. Sets the SOA fields that
            // drive the MineSystem trigger check. Mines have damage=0 + range=0 at the
            // tower level (they don't auto-attack); all damage is delivered via the
            // explosion AoE when the trigger condition is met. Mine stats are pulled
            // from MineConfig defaults (per-tower variation can be added later via
            // a towerId→MineDef lookup or extended PlaceTower signature).
            if (type == TowerType.Mine)
            {
                store.TowerIsMine[towerId] = true;
                store.MineTriggerRadius[towerId] = MineConfig.DefaultTriggerRadius;
                store.MineArmTime[towerId] = MineConfig.DefaultArmTime;
                store.MineArmProgress[towerId] = 0f;
                store.MineDamage[towerId] = MineConfig.DefaultDamage;
                store.MineExplosionRadius[towerId] = MineConfig.DefaultExplosionRadius;
                store.MineMaxStacks[towerId] = MineConfig.DefaultMaxStacks;
                store.MineStacksRemaining[towerId] = MineConfig.DefaultMaxStacks;
                store.MineTriggeredThisFrame[towerId] = false;
                // Round 172 — Chain Detonation defaults (inert; can be overridden via per-tower
                // MineDef lookup if/when the placement path resolves a mine config id).
                store.MineCanChain[towerId] = false;
                store.MineChainRadius[towerId] = 0f;
                store.MineChainDamageMult[towerId] = 0f;
                store.MineChainDepth[towerId] = 0;
                logger.Log($"[MINE] Tower #{towerId} at ({x},{y}): triggerR={store.MineTriggerRadius[towerId]}, arm={store.MineArmTime[towerId]}s, dmg={store.MineDamage[towerId]}, explR={store.MineExplosionRadius[towerId]}, stacks={store.MineMaxStacks[towerId]}");
            }

            // Round 173 Direction 1 — Shrine Tower post-place init. Sets the SOA fields
            // that drive TowerShrineSystem. Default 3-shrine template values here are
            // conservative: a single gold-buff shrine (aura type 1) that gives +0.10
            // extra gold per kill to friendly towers in 3 cells. PlaceTower callers
            // who need a different aura can overwrite the SOA fields directly after
            // this block (or extend the signature with a ShrineDef parameter).
            if (type == TowerType.Shrine)
            {
                store.TowerIsShrine[towerId] = true;
                store.TowerShrineAuraType[towerId] = 1; // 1 = Gold (default)
                store.TowerShrineRadius[towerId] = 3.0f;
                store.TowerShrinePotency[towerId] = 0.10f;
                logger.Log($"[SHRINE] Tower #{towerId} at ({x},{y}): aura=Gold, radius={store.TowerShrineRadius[towerId]}, potency={store.TowerShrinePotency[towerId]}");
            }

            // Round 177 Direction 2 — Beacon Tower post-place init. Sets the SOA fields
            // that drive TowerBeaconSystem. Default 3-beacon template values here are
            // balanced: a single broadcast beacon that gives +10% damage and +10%
            // attack-speed to every friendly tower in 3 cells. Designers can
            // overwrite the SOA fields directly after this block for different stats.
            if (type == TowerType.Beacon)
            {
                store.TowerIsBeacon[towerId] = true;
                store.TowerBeaconRadius[towerId] = 3.0f;
                store.TowerBeaconDmgBonus[towerId] = 0.10f;   // +10% damage to neighbors
                store.TowerBeaconAtkSpdBonus[towerId] = 0.10f; // +10% attack speed to neighbors
                logger.Log($"[BEACON] Tower #{towerId} at ({x},{y}): radius={store.TowerBeaconRadius[towerId]}, dmg=+{store.TowerBeaconDmgBonus[towerId]:P0}, spd=+{store.TowerBeaconAtkSpdBonus[towerId]:P0}");
            }

            // Increment tower count for cap enforcement
            store.PlayerTowerCount[playerId]++;
            // Round 139 — Per-Type Placement Cap: bump the per-type counter on successful
            // placement. Mirrors the decrement path in ComponentStore.DestroyEntity so the
            // counter always equals the live count for that (player, type) cell.
            if (towerTypeIdx >= 0 && towerTypeIdx < ComponentStore.MAX_TOWER_TYPES)
            {
                store.PlayerTowersOfType[playerId * ComponentStore.MAX_TOWER_TYPES + towerTypeIdx]++;
            }

            // Round 145 Direction 3 — Per-Tower Modifier Pool: roll ONE modifier from the
            // weighted pool at placement time. No-op when TowerModifier is null (system
            // disabled) or when the pool is empty. The rolled modifier is read lazily by
            // combat systems (TowerAttackSystem / BuffSystem / etc.) — no per-frame work.
            if (towerModifierSystem != null)
            {
                int rolledIdx = towerModifierSystem.RollAtPlacement(towerId);
                if (rolledIdx >= 0)
                {
                    string modName = towerModifierSystem.GetModifierName(towerId);
                    string modStat = towerModifierSystem.GetModifierStat(towerId);
                    float modMag = towerModifierSystem.GetModifierMagnitude(towerId);
                    logger.Log($"[MODIFIER] Tower #{towerId} ({type}) rolled '{modName}' (stat={modStat}, magnitude={modMag:F2})");
                }
            }

            return towerId;
        }

        // ─── Ghost placement (preview before commit) ──────────────────────────
        // Tracks the last preview state so ConfirmPlacement() can re-validate
        // and reuse the cached tower stats. The preview is purely a UI helper
        // — it never mutates the entity pool or spends gold.
        private int _previewX = -1;
        private int _previewY = -1;
        private TowerType _previewType;
        private float _previewDamage;
        private int _previewRange;
        private float _previewSpeed;
        private float _previewCost;
        private bool _previewValid;

        /// <summary>
        /// Tracks whether PreviewPlacement has been called and the preview is still
        /// live (i.e. ConfirmPlacement/CancelPreview have not yet cleared it).
        /// Distinct from _previewValid: a preview can be active but invalid (e.g. out of bounds),
        /// letting the caller decide whether to re-call PreviewPlacement or to Confirm.
        /// </summary>
        private bool _previewActive;

        /// <summary>
        /// Validate a candidate (x, y) for placing `type` without actually placing it.
        /// Checks map bounds, occupancy, and tower cap. Reused by both Preview and Confirm paths.
        /// </summary>
        private bool ValidatePlacementPosition(int x, int y)
        {
            if (x < 0 || x >= 10 || y < 0 || y >= 20) return false;
            // Round 95: O(1) cache lookup. Defensive fallback mirrors PlaceTower so a
            // mismatch between cache and ActiveTowerIds still blocks invalid placement.
            if (store.IsTileOccupied(x, y)) return false;
            foreach (int tid in store.ActiveTowerIds)
            {
                if ((int)store.PositionX[tid] == x && (int)store.PositionY[tid] == y) return false;
            }
            int playerId = 0;
            int maxTowers = store.PlayerMaxTowers[playerId] <= 0 ? 20 : store.PlayerMaxTowers[playerId];
            if (store.PlayerTowerCount[playerId] >= maxTowers)
                return false;
            return true;
        }

        /// <summary>
        /// Begin a ghost-placement preview at (x, y) for the given tower type.
        /// No entity is created, no gold is spent. Renders a ghost via IRenderer.
        /// </summary>
        /// <returns>True if the position is valid for placement, false otherwise.</returns>
        public bool PreviewPlacement(int x, int y, TowerType type, float damage, int range, float speed, float cost)
        {
            _previewX = x;
            _previewY = y;
            _previewType = type;
            _previewDamage = damage;
            _previewRange = range;
            _previewSpeed = speed;
            _previewCost = cost;
            _previewValid = ValidatePlacementPosition(x, y);
            _previewActive = true;
            logger.RenderGhostTower(x, y, range, _previewValid, type.ToString());
            return _previewValid;
        }

        /// <summary>
        /// Clear any in-progress ghost preview. Safe to call when no preview is active.
        /// </summary>
        public void CancelPreview()
        {
            _previewX = -1;
            _previewY = -1;
            _previewValid = false;
            _previewActive = false;
        }

        /// <summary>
        /// Commit the most recent PreviewPlacement() call. Re-validates the
        /// position (in case state changed between preview and confirm) and
        /// delegates to PlaceTower() for the actual creation. Returns -1 if
        /// no preview is active or the position is no longer valid.
        /// </summary>
        public int ConfirmPlacement()
        {
            if (!_previewActive)
            {
                logger.Log("[TOWER] ConfirmPlacement failed: no active preview");
                return -1;
            }
            if (!ValidatePlacementPosition(_previewX, _previewY))
            {
                logger.Log($"[TOWER] ConfirmPlacement failed: position ({_previewX},{_previewY}) is no longer valid");
                return -1;
            }
            int id = PlaceTower(_previewX, _previewY, _previewType, _previewDamage, _previewRange, _previewSpeed, _previewCost);
            // Clear preview state after commit (success or failure) — the caller is expected
            // to start a fresh preview for any subsequent placement.
            int committedX = _previewX;
            int committedY = _previewY;
            CancelPreview();
            if (id != -1)
            {
                logger.Log($"[TOWER] 幽灵预览已确认: 塔 #{id} 已建于 ({committedX},{committedY})");
            }
            return id;
        }

        /// <summary>
        /// Whether a ghost preview is currently active (i.e. PreviewPlacement
        /// has been called and neither ConfirmPlacement nor CancelPreview has cleared it).
        /// Returns true even for invalid previews — the user still has a live ghost
        /// to confirm or cancel. Only the validity flag is gated by position checks.
        /// </summary>
        public bool HasActivePreview => _previewActive;

        /// <summary>
        /// Read-only access to the cached preview's validity. Useful for UI binding
        /// (e.g., changing the cursor color) without re-running validation.
        /// </summary>
        public bool LastPreviewValid => _previewValid;

        // ─── Build Queue (BuildPhase 预排多塔位) ────────────────────────────────
        // Players can call EnqueueBuild() multiple times during BuildPhase to lay out
        // a build order, then ProcessBuildQueue() drains the head of the queue at a
        // paced interval (default 0.2s = 5 placements/sec) when called from the
        // WavePhase loop. Gold is deducted per placement; if gold is insufficient the
        // slot is skipped and logged (the rest of the queue is preserved).

        /// <summary>
        /// Append a (x, y, type) placement request to a player's build queue.
        /// Returns true on success, false if the queue is full (default MAX_BUILD_QUEUE=16)
        /// or the position is out of bounds. Validation against occupancy / tower cap is
        /// deferred to drain time so the player can pre-plan a full wave layout.
        /// </summary>
        public bool EnqueueBuild(int playerId, int x, int y, TowerType type, float damage, int range, float speed, float cost)
        {
            if ((uint)playerId >= 10) return false;
            if (store.PlayerBuildQueueCount[playerId] >= ComponentStore.MAX_BUILD_QUEUE)
            {
                logger.Log($"[BUILDQ] EnqueueBuild failed: player {playerId} queue is full ({ComponentStore.MAX_BUILD_QUEUE})");
                return false;
            }
            if (x < 0 || x >= 10 || y < 0 || y >= 20)
            {
                logger.Log($"[BUILDQ] EnqueueBuild failed: position ({x},{y}) out of map range");
                return false;
            }
            int slotIdx = playerId * ComponentStore.MAX_BUILD_QUEUE + store.PlayerBuildQueueCount[playerId];
            // First-fill-any-inactive-slot (defensive — should be append-only)
            while (store.PlayerBuildQueue[slotIdx].Active && slotIdx < (playerId + 1) * ComponentStore.MAX_BUILD_QUEUE)
            {
                slotIdx++;
            }
            if (slotIdx >= (playerId + 1) * ComponentStore.MAX_BUILD_QUEUE) return false;
            store.PlayerBuildQueue[slotIdx] = new ComponentStore.BuildQueueSlot
            {
                X = x,
                Y = y,
                TowerType = (int)type,
                Damage = damage,
                Range = range,
                Speed = speed,
                Cost = cost,
                Active = true
            };
            store.PlayerBuildQueueCount[playerId]++;
            logger.Log($"[BUILDQ] Player {playerId} enqueued: {type} at ({x},{y}) — slot {store.PlayerBuildQueueCount[playerId]}/{ComponentStore.MAX_BUILD_QUEUE}");
            return true;
        }

        /// <summary>
        /// Clear all queued build orders for a player. Safe to call when the queue is empty.
        /// </summary>
        public void ClearBuildQueue(int playerId)
        {
            if ((uint)playerId >= 10) return;
            int baseIdx = playerId * ComponentStore.MAX_BUILD_QUEUE;
            for (int i = 0; i < ComponentStore.MAX_BUILD_QUEUE; i++)
            {
                store.PlayerBuildQueue[baseIdx + i] = default;
            }
            store.PlayerBuildQueueCount[playerId] = 0;
            store.PlayerBuildQueueTimer[playerId] = 0f;
            logger.Log($"[BUILDQ] Player {playerId} build queue cleared");
        }

        /// <summary>
        /// Returns the number of pending build orders for a player. O(1) — backed by PlayerBuildQueueCount.
        /// </summary>
        public int GetBuildQueueCount(int playerId)
        {
            if ((uint)playerId >= 10) return 0;
            return store.PlayerBuildQueueCount[playerId];
        }

        /// <summary>
        /// Drain the head of the build queue for a single player. Called once per frame
        /// from FrameScheduler (or any WavePhase entry point). Pacing is controlled by
        /// PlayerBuildQueueTimer + GameConfig.BuildQueueInterval (default 0.2s).
        /// Returns the number of towers actually placed this tick (0 or 1).
        /// </summary>
        public int ProcessBuildQueue(int playerId, float deltaTime)
        {
            if ((uint)playerId >= 10) return 0;
            if (store.PlayerBuildQueueCount[playerId] <= 0) return 0;
            float interval = gameConfig != null ? gameConfig.BuildQueueInterval : 0.2f;
            store.PlayerBuildQueueTimer[playerId] += deltaTime;
            if (store.PlayerBuildQueueTimer[playerId] < interval) return 0;
            store.PlayerBuildQueueTimer[playerId] -= interval;
            // Find the head (lowest active slot)
            int baseIdx = playerId * ComponentStore.MAX_BUILD_QUEUE;
            int headSlot = -1;
            for (int i = 0; i < ComponentStore.MAX_BUILD_QUEUE; i++)
            {
                if (store.PlayerBuildQueue[baseIdx + i].Active)
                {
                    headSlot = baseIdx + i;
                    break;
                }
            }
            if (headSlot < 0)
            {
                store.PlayerBuildQueueCount[playerId] = 0;
                return 0;
            }
            ref var slot = ref store.PlayerBuildQueue[headSlot];
            // Gold check — skip if insufficient
            float currentGold = store.GetPlayerGold(playerId);
            if (currentGold < slot.Cost)
            {
                logger.Log($"[BUILDQ] Player {playerId} gold insufficient ({currentGold:F0} < {slot.Cost:F0}) — slot at ({slot.X},{slot.Y}) skipped");
                slot = default;
                CompactQueue(playerId);
                return 0;
            }
            // Snapshot slot fields BEFORE clearing (ref becomes stale after slot = default).
            int slotX = slot.X, slotY = slot.Y, slotType = slot.TowerType;
            float slotDmg = slot.Damage, slotSpd = slot.Speed, slotCost = slot.Cost;
            int slotRange = slot.Range;
            // Deduct gold up front (PlaceTower increments PlayerTowerCount, but gold deduction
            // is handled internally; we pre-deduct to avoid double-charge on PlaceTower's path).
            // Actually, PlaceTower does NOT deduct gold (it only increments the count). The
            // caller of PlaceTower is responsible for gold. So we deduct here.
            store.SetPlayerGold(playerId, currentGold - slot.Cost);
            int id = PlaceTower(slotX, slotY, (TowerType)slotType, slotDmg, slotRange, slotSpd, slotCost);
            // Clear the consumed slot and compact
            slot = default;
            CompactQueue(playerId);
            if (id >= 0)
            {
                logger.Log($"[BUILDQ] Player {playerId} drained: tower #{id} built at ({slotX},{slotY}) (cost {slotCost:F0})");
            }
            return id >= 0 ? 1 : 0;
        }

        // Compact the active slots in a player's queue so the head is always at the lowest
        // index. O(MAX_BUILD_QUEUE)=O(16), negligible.
        private void CompactQueue(int playerId)
        {
            int baseIdx = playerId * ComponentStore.MAX_BUILD_QUEUE;
            int writeIdx = 0;
            for (int readIdx = 0; readIdx < ComponentStore.MAX_BUILD_QUEUE; readIdx++)
            {
                if (store.PlayerBuildQueue[baseIdx + readIdx].Active)
                {
                    if (writeIdx != readIdx)
                    {
                        store.PlayerBuildQueue[baseIdx + writeIdx] = store.PlayerBuildQueue[baseIdx + readIdx];
                        store.PlayerBuildQueue[baseIdx + readIdx] = default;
                    }
                    writeIdx++;
                }
            }
            store.PlayerBuildQueueCount[playerId] = writeIdx;
        }

        private void ApplyTowerSpecialAbility(ComponentStore store, int towerId, TowerSpecialAbility ability)
        {
            if (ability == null || string.IsNullOrEmpty(ability.AbilityType)) return;

            // Store all ability parameters for TowerAttackSystem to read
            store.TowerSpecialAbilityRadius[towerId] = ability.Radius;
            store.TowerSpecialAbilityDamageMult[towerId] = ability.DamageMultiplier;
            store.TowerSpecialAbilityDotDamage[towerId] = ability.DotDamagePerTick;
            store.TowerSpecialAbilityDotInterval[towerId] = ability.DotTickInterval > 0f ? ability.DotTickInterval : 1f;

            switch (ability.AbilityType.ToLowerInvariant())
            {
                case "chain_lightning":
                    store.TowerHasChainLightning[towerId] = true;
                    break;
                case "freeze_aoe":
                    store.TowerHasFreezeAoe[towerId] = true;
                    break;
                case "splash":
                case "splash_damage":
                    store.TowerSplashRadius[towerId] = ability.Radius;
                    // Apply falloff if specified (default 1.0 = no falloff)
                    store.TowerFalloffInnerRatio[towerId] = ability.FalloffInnerRatio > 0f ? ability.FalloffInnerRatio : 1.0f;
                    store.TowerFalloffOuterMult[towerId] = ability.FalloffOuterMult > 0f ? ability.FalloffOuterMult : 1.0f;
                    break;
            }
        }

        /// <summary>
        /// Sell a single tower and refund a portion of its upgrade cost.
        /// The tower must be selected first.
        /// </summary>
        /// <returns>Gold refunded, or 0 if sell failed.</returns>
        public float SellTower(int towerId, int playerId = 1)
        {
            if (towerId < 0 || towerId >= ComponentStore.MAX_ENTITIES || !store.TowerActive[towerId])
            {
                logger.Log($"[TOWER] 出售失败: 实体 {towerId} 不是激活的防御塔");
                return 0f;
            }

            int level = store.TowerLevel[towerId];
            // Round 140 — Direction 7: pass tower type so per-type sell ratio override is honored.
            int towerTypeIdx = (int)store.TowerType[towerId];
            float baseRatio = GetEffectiveSellRatio(level, towerTypeIdx);
            float placeTime = store.TowerPlaceTime[towerId];
            float effectiveRatio = GetDecayedSellRatio(placeTime, baseRatio);
            float sellGold = store.TowerUpgradeCost[towerId] * effectiveRatio;
            // Salvage refund: recover a fraction of cumulative upgrade spend (Round 85 direction 4).
            // Encourages players to experiment with upgrades — gold isn't fully lost on sell.
            float spentBeforeDestroy = store.TowerTotalUpgradeSpent[towerId];
            float salvageRefund = spentBeforeDestroy * salvageUpgradeRate;
            sellGold += salvageRefund;
            int goldInt = (int)sellGold;

            // Refund gold to player
            float currentGold = store.GetPlayerGold(playerId);
            store.SetPlayerGold(playerId, currentGold + sellGold);

            // Decrement tower count for cap enforcement. NOTE: ComponentStore.DestroyEntity()
            // (called below) ALSO decrements PlayerTowerCount / PlayerTowersOfType as part of its
            // recycle cleanup, so we do NOT decrement here — doing so would double-decrement and
            // drive the counters negative. The per-type cap enforcement lives entirely on the
            // destroy path now, with PlaceTower's mirror increment.

            // Destroy tower entity (handles ActiveTowerIds removal and state cleanup)
            store.DestroyEntity(towerId);

            float age = Time.TotalTime - placeTime;
            logger.Log($"[TOWER] 出售塔 #{towerId} (Lv.{level})，已放置 {age:F1}s，退款率 {effectiveRatio:F2}（基础 {baseRatio:F2}），基础返还 {goldInt - (int)salvageRefund} 金币，残值返还 {(int)salvageRefund} 金币（升级累计 {spentBeforeDestroy:F0} × {salvageUpgradeRate:F2}），共 {goldInt} 金币");
            return sellGold;
        }

        /// <summary>
        /// Toggle a tower between active and player-disabled (Round 96 — Direction 2).
        /// While disabled, the tower does not attack and does not generate income.
        /// No gold is refunded on disable (this is a temporary power-save, distinct from SellTower).
        /// The flag is sticky: it persists until toggled back on. Survives across frames
        /// because it lives in the store array; cleared in ComponentStore.DestroyEntity().
        /// </summary>
        /// <returns>The new state (true = disabled, false = active). -1 on bad input.</returns>
        public int ToggleTower(int towerId)
        {
            if (towerId < 0 || towerId >= ComponentStore.MAX_ENTITIES || !store.TowerActive[towerId])
            {
                logger.Log($"[TOWER] ToggleTower 失败: 实体 {towerId} 不是激活的防御塔");
                return -1;
            }
            bool newState = !store.TowerPlayerDisabled[towerId];
            store.TowerPlayerDisabled[towerId] = newState;
            logger.Log($"[TOWER] 塔 #{towerId} (Lv.{store.TowerLevel[towerId]}) 已{(newState ? "停用" : "重新启用")}");
            return newState ? 1 : 0;
        }

        /// <summary>
        /// Demolish (sacrifice) a tower, triggering its AoE demolish effect.
        /// The tower is permanently destroyed with no gold refund.
        /// The demolish effect is processed by TowerDemolishSystem.
        /// </summary>
        /// <returns>True if demolish was triggered, false if the tower cannot be demolished.</returns>
        public bool DemolishTower(int towerId)
        {
            if (towerId < 0 || towerId >= ComponentStore.MAX_ENTITIES || !store.TowerActive[towerId])
            {
                logger.Log($"[TOWER] 拆除失败: 实体 {towerId} 不是激活的防御塔");
                return false;
            }

            float radius = store.TowerDemolishEffectRadius[towerId];
            if (radius <= 0f)
            {
                logger.Log($"[TOWER] 拆除失败: 塔 #{towerId} 没有可拆卸的 AoE 效果");
                return false;
            }

            // Mark tower for demolish — consumed by TowerDemolishSystem.Update()
            store.TowerIsMarkedForDemolish[towerId] = true;
            int level = store.TowerLevel[towerId];
            logger.Log($"[TOWER] 拆除塔 #{towerId} (Lv.{level})，AoE 半径 {radius}");

            return true;
        }

        /// <summary>
        /// Sell all currently selected towers in a batch.
        /// </summary>
        /// <returns>Total gold refunded.</returns>
        public float SellSelectedTowers(int playerId = 1)
        {
            int[] selected = store.GetSelectedTowerIds();
            if (selected.Length == 0)
            {
                logger.Log("[TOWER] 批量出售: 没有选中的塔");
                return 0f;
            }

            // Lock around ActiveTowerIds modifications for batch safety
            float totalRefunded = 0f;
            foreach (int tid in selected)
            {
                // SellTower internally calls DestroyEntity which locks activeIdsLock
                totalRefunded += SellTower(tid, playerId);
            }

            logger.Log($"[TOWER] 批量出售完成: {selected.Length} 塔，共返还 {(int)totalRefunded} 金币");
            return totalRefunded;
        }

        /// <summary>
        /// Relocate an active tower to a new position without changing its upgrade level.
        /// </summary>
        /// <param name="towerId">Tower entity ID</param>
        /// <param name="newX">New X position</param>
        /// <param name="newY">New Y position</param>
        /// <param name="playerId">Player ID for gold deduction</param>
        /// <returns>Gold deducted, or 0 if relocation failed.</returns>
        public float RelocateTower(int towerId, int newX, int newY, int playerId = 1)
        {
            if (towerId < 0 || towerId >= ComponentStore.MAX_ENTITIES || !store.TowerActive[towerId])
            {
                logger.Log($"[TOWER] 重定位失败: 塔 #{towerId} 不存在或未激活");
                return 0f;
            }

            // Check if new position is within map bounds
            if (newX < 0 || newX >= 10 || newY < 0 || newY >= 50)
            {
                logger.Log($"[TOWER] 重定位失败: 位置 ({newX},{newY}) 超出地图范围");
                return 0f;
            }

            // Check if new position is already occupied — Round 95: O(1) cache first.
            if (store.IsTileOccupied(newX, newY))
            {
                logger.Log($"[TOWER] 重定位失败: 位置 ({newX},{newY}) 已被占用 (cache)");
                return 0f;
            }
            foreach (int tid in store.ActiveTowerIds)
            {
                if (tid != towerId && (int)store.PositionX[tid] == newX && (int)store.PositionY[tid] == newY)
                {
                    logger.Log($"[TOWER] 重定位失败: 位置 ({newX},{newY}) 已被塔 #{tid} 占用");
                    return 0f;
                }
            }

            // Calculate relocate cost (same formula as in TowerRelocateSystem)
            int level = store.TowerLevel[towerId];
            float baseCost = 50f;
            float decreasePerLevel = 5f;
            float minCost = 20f;
            float cost = Math.Max(baseCost - (level - 1) * decreasePerLevel, minCost);

            float currentGold = store.GetPlayerGold(playerId);
            if (currentGold < cost)
            {
                logger.Log($"[TOWER] 重定位失败: 金币不足 (需要 {cost}, 当前 {currentGold})");
                return 0f;
            }

            // Record old position
            int oldX = (int)store.PositionX[towerId];
            int oldY = (int)store.PositionY[towerId];

            // Deduct gold
            store.SetPlayerGold(playerId, currentGold - cost);

            // Update tower position
            store.SetPosition(towerId, newX, newY);
            // Round 95: keep the O(1) tile cache in sync with the actual position.
            // Free the old tile and claim the new one. Order matters: clear old first
            // so an in-flight ValidatePlacementPosition reading the new tile during
            // a concurrent sweep still sees the cache match the ActiveTowerIds scan.
            store.SetTileOccupied(oldX, oldY, false);
            store.SetTileOccupied(newX, newY, true);

            logger.Log($"[TOWER] 塔 #{towerId} ({store.TowerType[towerId]}, Lv.{level}) 从 ({oldX},{oldY}) 移动到 ({newX},{newY})，花费 {cost} 金币");
            return cost;
        }
    }
}
