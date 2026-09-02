#nullable enable
using System.Collections.Generic;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;

namespace BattleSystemECS.Content.Contracts
{
    public enum SkillDamageRejectReason { None, PhaseNotAllowed, UnsupportedCommitBoundary, NoPending }
    public enum CraftingResult { Success, SuccessRareBonus, Failure, MissingInputs, BadRecipe, FullInventory }

    public readonly struct DodgeFact
    {
        public int EnemyId { get; }
        public float Distance { get; }
        public int Direction { get; }
        public DodgeFact(int enemyId, float distance, int direction)
        { EnemyId = enemyId; Distance = distance; Direction = direction; }
    }

    public static class TelegraphShape { public const int Circle = 0; }

    public static class AbilityPhaseRules
    {
        public static bool IsBuildAllowed(int shape) =>
            shape == AreaShapeType.Heal || shape == AreaShapeType.Shield ||
            shape == AreaShapeType.ChainHeal || shape == AreaShapeType.TimeRewind;
    }

    public static class PathNavigationRules
    {
        public static int EvaluateJunction(JunctionDef def, float currentHp, float maxHp,
            bool isBossType, int towerCountInRadius)
        {
            if (def == null) return -1;
            switch (def.Policy)
            {
                case JunctionPolicy.HpBased:
                    float ratio = maxHp > 0f ? currentHp / maxHp : 0f;
                    return ratio > def.HpLongPathThreshold ? def.LongPathId : def.ShortPathId;
                case JunctionPolicy.TowerDensityBased:
                    return towerCountInRadius > def.TowerDensityShortPathThreshold
                        ? def.ShortPathId : def.LongPathId;
                case JunctionPolicy.TypeBased:
                    return isBossType ? def.LongPathId : def.ShortPathId;
                default:
                    return def.ShortPathId;
            }
        }
    }

    public interface IWaveSpawningPort
    {
        void SetPerformanceSpawnMultiplier(float multiplier);
        void InjectExtraEnemies(int count);
        void InjectMiniBoss();
        int SpawnMinionNearPosition(int typeId, int count, float centerX, float centerY);
        int SpawnMinionNearPosition(int typeId, int count, float centerX, float centerY, int bossElementAffinity);
    }
    public interface IAbilityActivationPort
    {
        SkillDamageRejectReason LastRejectReason { get; }
        bool CastSkill(string skillName);
    }
    public interface IEffectCommandPort
    {
        void ApplyDot(int targetId, float damagePerTick, int duration);
        void ApplyDot(int targetId, GameplayEffectDef dotDef);
        void HealPlayer(float healPercent);
    }
    public interface IInventoryCommandPort { bool AddItem(int playerId, int itemTypeId); }
    public interface ITelegraphCommandPort
    {
        bool CanQueueTelegraphZone(float duration);
        bool TryQueueTelegraphZone(EntityHandle source, EntityHandle target,
            float x, float y, float radius, float duration, float damage, AbilityId ability,
            int ownerPlayerId, int shape = TelegraphShape.Circle, float coneAngle = 60f,
            float coneDir = 0f, int colorHint = 0);
        void QueueTelegraphZone(int enemyId, float x, float y, float radius,
            float duration, float damage, int playerId, int shape = TelegraphShape.Circle,
            float coneAngle = 60f, float coneDir = 0f, int colorHint = 0);
    }
    public interface IEnemyAbilityCommandPort { void EnqueueAbility(int enemyId, string abilityId); }
    public interface ICombatTuningView
    {
        float GetFinalAttackDamage();
        float GetAttackDamageMult();
        float GetDamageTakenMult();
        float GetCritRateBonus();
        float GetCritDamageMult();
        float GetGoldOnKillMult();
        float GetAllIncomeMult();
        float GetGoldOnEliteKill();
        float GetArmorPenetration();
        float GetArmorShredPerStack();
        float GetGoldOnWaveBonus();
        bool GetKnockbackImmunity();
        float GetStunResistance();
        float GetFreezeResistance();
        float GetSlowResistance();
        float GetMaxManaBonus();
        float GetManaRegenBonus();
        float GetManaCostMultiplier();
        float GetWaveDifficultyMultiplier(int waveNumber);
    }
    public interface IBossTrailCollector
    {
        void BeginCollect(int count);
        void TryQueueTrail(int enemyId, float progress);
        void TryQueueTrail(int activeIndex, int enemyId, float progress);
        void ResolveTrailEvents();
    }
    public interface IEnemySpeedModifierView
    {
        float GetEnemySpeedMultiplier(int playerId);
    }
    public interface ITowerRangeModifierView
    {
        float GetTowerRangeMultiplier(int playerId);
    }
    public interface ITowerEnvironmentView : ITowerRangeModifierView
    {
        float GetTowerDamageMultiplier(int playerId);
    }
    public interface IPathNavigationView
    {
        bool HasJunctions { get; }
        (float dx, float dy) GetDirectionToNextNode(int enemyId);
        int GetPathWaypointCount(int pathId);
        JunctionDef GetJunction(int sourcePathId, int nodeIndex);
    }
    public interface ICraftingService { CraftingResult TryCraft(int playerId, int recipeId); }
    public interface IResourcePort
    {
        bool TryConsumeMana(float baseCost);
        float GetCurrentMana();
    }
    public interface ILinkDamageResolver
    { (float primaryDamage, float linkedDamage, int linkedEnemyId) ComputeLinkedDamage(int enemyId, float totalDamage); }
    public interface IHitShieldResolver { bool ConsumeHitShield(int enemyId); }
    public interface IEnemyProjectilePort
    {
        void GetProjectilesInRange(float cx, float cy, int rangeSq, List<int> result);
        void DestroyProjectile(int projId);
    }
    public interface IProjectileCommandPort
    {
        void Fire(int towerId, int targetId, float damage, int playerId, float speed,
            bool isHoming = false, int pierceCount = 0, float pierceDmgFalloff = 1f,
            int fragmentCount = 0, float fragmentRange = 0f, float fragmentDmgMult = 1f,
            float leadAimFactor = 0f);
    }
    public interface IGoldRewardPort { }
    public interface IMerchantModifierPort
    {
        void ApplyMerchantDiscount(int playerId, float discountMultiplier);
        void ResetMerchantDiscount(int playerId);
    }
    public interface IPickupCommandPort
    { void SpawnPickup(int pickupType, float x, float y, int playerId, float value = 0f, byte rarity = 0); }
    public interface IHealingZoneCommandPort
    { int AddHealingZone(float x, float y, float radius, float duration, float healPerSec); }
    public interface IResurrectionPort
    {
        void SetTurn(int turn, float simTime);
        bool CanMassResurrect(float centerX, float centerY, float radius);
        int MassResurrect(int playerId, float centerX, float centerY, float radius, float hpFraction);
    }
    public interface ISummonCommandPort { int SummonUnit(int playerId, SummonDef def); }
    public interface ISnapshotRestorePort
    {
        int GetSampleCount(int playerId);
        float RestoreFromSnapshot(int playerId, float secondsBack);
        float RestoreFromSnapshot(int sourceEntityId, int playerId, float secondsBack);
    }
    public interface IReflectionCommandPort
    {
        void QueueReflect(int towerId, int attackingEnemyId, float damageReceived);
        void QueueRetaliate(int towerId, int attackingEnemyId, float damage);
    }
    public interface ITowerTargetingView
    {
        bool CanTargetTower(int towerId, int enemyId);
        float GetStealthDamageMultiplier(int towerId);
    }
    public interface IBleedCommandPort
    { void ApplyBleedFromTower(int towerId, int targetId, float stacksToApply, float dmgPerStack, float duration); }
    public interface ICullingPass { void ScanAndCull(); }
    public interface IDesperationView
    {
        float DamageBonus { get; }
        float SpeedBonus { get; }
    }
    public interface IDodgeResolver
    {
        bool TryQueueDodge(int enemyId, int attackDirection, int salt, out DodgeFact fact);
        void ApplyQueuedDodge(DodgeFact fact);
    }
    public interface IFireTrailCommandPort
    { int SpawnTrail(float x, float y, float radius = 1.5f, float dps = 8f, float duration = 2f, float tickInterval = 0.5f); }
    public interface ITowerHeatPort
    {
        bool IsOverheated(int towerId);
        void AccumulateHeat(int towerId);
    }
    public interface ITowerEnergyPort
    {
        bool TryConsumeEnergy(int towerId);
    }
    public interface ITowerModifierRoller
    {
        int RollAtPlacement(int towerId);
        float GetModifierMagnitude(int towerId);
        string GetModifierStat(int towerId);
        string GetModifierName(int towerId);
    }
    public interface ITowerUpgradeService { bool UpgradeTower(int towerId); }
    public interface IWaveScalingState
    {
        void OnWaveComplete(int playerId, int expectedKills = 0);
        float GetDifficultyMult(int playerId);
    }
    public interface IAscensionDecorator { void ApplyEnemyScaling(int enemyId); }
    public interface IEnemyAffixDecorator { void AssignAffixesAtSpawn(int enemyId, float maxHealth); }
}
