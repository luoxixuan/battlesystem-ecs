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

        public TowerPlacementSystem(ComponentStore store, IRenderer logger)
        {
            this.store = store;
            this.logger = logger;
            LoadSellConfig();
        }

        /// <summary>
        /// Overload accepting GameConfig so debuff fields can be looked up from TowerConfig.
        /// </summary>
        public TowerPlacementSystem(ComponentStore store, IRenderer logger, GameConfig gameConfig)
        {
            this.store = store;
            this.logger = logger;
            this.gameConfig = gameConfig;
            LoadSellConfig();
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
                }
                catch { /* use defaults */ }
            }
        }

        /// <summary>
        /// Calculate the effective sell ratio for a given tower level.
        /// Ratio decreases per level but never drops below minSellRatio.
        /// </summary>
        private float GetEffectiveSellRatio(int towerLevel)
        {
            float ratio = sellRatio - (towerLevel - 1) * sellRatioDecreasePerLevel;
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

            // 3. Check if position already has a tower
            foreach (int tid in store.ActiveTowerIds)
            {
                if (store.PositionX[tid] == x && store.PositionY[tid] == y)
                {
                    logger.Log($"[TOWER] PlaceTower failed: position ({x},{y}) already has a tower");
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

            // 4. Create tower entity
            int towerId = store.CreateEntity();
            if (towerId == -1)
            {
                logger.Log("[TOWER] PlaceTower failed: entity creation failed (entity pool exhausted)");
                return -1;
            }

            store.AddPosition(towerId, x, y);
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
                    // Apply intercept rate for PointDefense towers
                    store.SetTowerInterceptRate(towerId, tc.InterceptRate);
                    // Apply bounce projectile settings
                    store.TowerBouncesRemaining[towerId] = tc.Bounces;
                    store.TowerBounceRange[towerId] = tc.BounceRange;
                    store.TowerBounceDamageFalloff[towerId] = tc.BounceDamageFalloff;
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
                    // Apply chrono tower properties (time dilation field)
                    if (tc.IsChronoTower)
                    {
                        store.TowerIsChronoTower[towerId] = true;
                        store.TowerTimeFieldRadius[towerId] = tc.TimeFieldRadius;
                        store.TowerTimeScale[towerId] = tc.TimeScale;
                        logger.Log($"[TOWER] {tc.Name} 时间塔: 半径 {tc.TimeFieldRadius}, 时间缩放 {tc.TimeScale:F1}x");
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

            // Increment tower count for cap enforcement
            store.PlayerTowerCount[playerId]++;

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
            float baseRatio = GetEffectiveSellRatio(level);
            float placeTime = store.TowerPlaceTime[towerId];
            float effectiveRatio = GetDecayedSellRatio(placeTime, baseRatio);
            float sellGold = store.TowerUpgradeCost[towerId] * effectiveRatio;
            int goldInt = (int)sellGold;

            // Refund gold to player
            float currentGold = store.GetPlayerGold(playerId);
            store.SetPlayerGold(playerId, currentGold + sellGold);

            // Decrement tower count for cap enforcement
            store.PlayerTowerCount[playerId]--;

            // Destroy tower entity (handles ActiveTowerIds removal and state cleanup)
            store.DestroyEntity(towerId);

            float age = Time.TotalTime - placeTime;
            logger.Log($"[TOWER] 出售塔 #{towerId} (Lv.{level})，已放置 {age:F1}s，退款率 {effectiveRatio:F2}（基础 {baseRatio:F2}），返还 {goldInt} 金币");
            return sellGold;
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

            // Check if new position is already occupied
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

            logger.Log($"[TOWER] 塔 #{towerId} ({store.TowerType[towerId]}, Lv.{level}) 从 ({oldX},{oldY}) 移动到 ({newX},{newY})，花费 {cost} 金币");
            return cost;
        }
    }
}
