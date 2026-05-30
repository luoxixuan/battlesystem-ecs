using System;
using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;

namespace BattleSystemECS.Core
{
    public partial class ComponentStore
    {
        // ==================== 路障管理 ====================
        public void AddObstacle(int obstacleId, int typeId, float x, float y, float maxHealth)
        {
            if (obstacleId < 0 || obstacleId >= MAX_OBSTACLES) return;
            ObstacleActive[obstacleId] = true;
            ObstacleType[obstacleId] = typeId;
            ObstacleX[obstacleId] = x;
            ObstacleY[obstacleId] = y;
            ObstacleHealth[obstacleId] = maxHealth;
            ObstacleMaxHealth[obstacleId] = maxHealth;
            _activeObstacleIds.Add(obstacleId);
        }

        public void RemoveObstacle(int obstacleId)
        {
            if (obstacleId < 0 || obstacleId >= MAX_OBSTACLES) return;
            ObstacleActive[obstacleId] = false;
            ObstacleHealth[obstacleId] = 0f;
            ObstacleMaxHealth[obstacleId] = 0f;
            ObstacleX[obstacleId] = 0f;
            ObstacleY[obstacleId] = 0f;
            ObstacleType[obstacleId] = -1;
            _activeObstacleIds.Remove(obstacleId);
        }

        // ==================== 持久性地面 HazardZone 管理 ====================
        /// <summary>Add a hazard zone at the given position with specified type and parameters.</summary>
        public int AddHazardZone(float x, float y, float radius, int hazardType, float duration, float damagePerSec = 0f, int ownerTowerId = -1)
        {
            int zoneId = -1;
            lock (activeIdsLock)
            {
                // Find a free slot
                for (int i = 0; i < MAX_HAZARD_ZONES; i++)
                {
                    int candidateId = (_nextHazardZoneId + i) % MAX_HAZARD_ZONES;
                    if (!HazardZoneActive[candidateId])
                    {
                        zoneId = candidateId;
                        _nextHazardZoneId = (candidateId + 1) % MAX_HAZARD_ZONES;
                        break;
                    }
                }
            }
            if (zoneId < 0) return -1; // no free slots

            HazardZoneActive[zoneId] = true;
            HazardZoneX[zoneId] = x;
            HazardZoneY[zoneId] = y;
            HazardZoneRadius[zoneId] = radius;
            HazardZoneMaxRadius[zoneId] = radius;
            HazardZoneType[zoneId] = hazardType;
            HazardZoneDuration[zoneId] = duration;
            HazardZoneDamagePerSec[zoneId] = damagePerSec;
            HazardZoneOwnerTowerId[zoneId] = ownerTowerId;
            _activeHazardZoneIds.Add(zoneId);
            return zoneId;
        }

        /// <summary>Remove a hazard zone by ID.</summary>
        public void RemoveHazardZone(int zoneId)
        {
            if (zoneId < 0 || zoneId >= MAX_HAZARD_ZONES) return;
            if (!HazardZoneActive[zoneId]) return;
            HazardZoneActive[zoneId] = false;
            HazardZoneX[zoneId] = 0f;
            HazardZoneY[zoneId] = 0f;
            HazardZoneRadius[zoneId] = 0f;
            HazardZoneMaxRadius[zoneId] = 0f;
            HazardZoneType[zoneId] = 0;
            HazardZoneDuration[zoneId] = 0f;
            HazardZoneDamagePerSec[zoneId] = 0f;
            HazardZoneOwnerTowerId[zoneId] = -1;
            _activeHazardZoneIds.Remove(zoneId);
        }

        /// <summary>Get list of active hazard zone IDs. O(n) over active zones, zero GC.</summary>
        public List<int> GetCachedActiveHazardZoneIds()
        {
            return _activeHazardZoneIds;
        }

        // ==================== 尸体残留效果（CorpseEffect）管理 API ====================

        /// <summary>
        /// Queue a corpse ground effect at a position when an enemy dies.
        /// Called from EnemyFissionSystem or ResolveEnemiesKilledThisFrame.
        /// Returns zone ID or -1 if no free slots.
        /// </summary>
        public int AddCorpseEffect(float x, float y, int effectType, float radius, float duration, float damagePerTick = 0f, float slowAmount = 1f, float tickInterval = 1f)
        {
            int zoneId = -1;
            for (int i = 0; i < MAX_CORPSE_EFFECTS; i++)
            {
                int candidateId = (_nextCorpseEffectId + i) % MAX_CORPSE_EFFECTS;
                if (!CorpseEffectActive[candidateId])
                {
                    zoneId = candidateId;
                    _nextCorpseEffectId = (candidateId + 1) % MAX_CORPSE_EFFECTS;
                    break;
                }
            }
            if (zoneId < 0) return -1; // no free slots

            CorpseEffectActive[zoneId] = true;
            CorpseEffectX[zoneId] = x;
            CorpseEffectY[zoneId] = y;
            CorpseEffectType[zoneId] = effectType;
            CorpseEffectRadius[zoneId] = radius;
            CorpseEffectDuration[zoneId] = duration;
            CorpseEffectDamagePerTick[zoneId] = damagePerTick;
            CorpseEffectSlowAmount[zoneId] = slowAmount;
            CorpseEffectTickTimer[zoneId] = 0f;
            CorpseEffectTickInterval[zoneId] = tickInterval;
            _activeCorpseEffectIds.Add(zoneId);
            return zoneId;
        }

        /// <summary>Remove a corpse effect by ID.</summary>
        public void RemoveCorpseEffect(int zoneId)
        {
            if (zoneId < 0 || zoneId >= MAX_CORPSE_EFFECTS) return;
            if (!CorpseEffectActive[zoneId]) return;
            CorpseEffectActive[zoneId] = false;
            CorpseEffectX[zoneId] = 0f;
            CorpseEffectY[zoneId] = 0f;
            CorpseEffectType[zoneId] = 0;
            CorpseEffectRadius[zoneId] = 0f;
            CorpseEffectDuration[zoneId] = 0f;
            CorpseEffectDamagePerTick[zoneId] = 0f;
            CorpseEffectSlowAmount[zoneId] = 1f;
            CorpseEffectTickTimer[zoneId] = 0f;
            CorpseEffectTickInterval[zoneId] = 1f;
            _activeCorpseEffectIds.Remove(zoneId);
        }

        /// <summary>Get list of active corpse effect IDs. O(n) over active zones, zero GC.</summary>
        public List<int> GetCachedActiveCorpseEffectIds()
        {
            return _activeCorpseEffectIds;
        }

        // ==================== 亡灵法师尸体队列 API ====================
        /// <summary>
        /// Queue a killed enemy as a corpse for potential necromancer resurrection.
        /// Uses a circular buffer. Returns corpse slot index (0 to MAX_CORPSE_QUEUE-1), or -1 if full.
        /// </summary>
        public int NecromancerQueueCorpse(int enemyId, float x, float y, string monsterType, float hpPercent, float simTime)
        {
            for (int i = 0; i < MAX_CORPSE_QUEUE; i++)
            {
                int candidateId = (_nextCorpseId + i) % MAX_CORPSE_QUEUE;
                if (CorpseActive[candidateId]) continue;

                CorpseX[candidateId] = x;
                CorpseY[candidateId] = y;
                CorpseMonsterType[candidateId] = monsterType;
                CorpseOwnerId[candidateId] = -1; // unclaimed
                CorpseHealth[candidateId] = hpPercent;
                CorpseDeathTime[candidateId] = simTime;
                CorpseActive[candidateId] = true;
                CorpseReanimated[candidateId] = false;
                _nextCorpseId = (candidateId + 1) % MAX_CORPSE_QUEUE;
                return candidateId;
            }
            return -1; // queue full
        }

        /// <summary>
        /// Expire old corpses past the age limit. Called from NecromancerSystem or cleanup.
        /// </summary>
        public void ExpireCorpse(int corpseId)
        {
            if (corpseId < 0 || corpseId >= MAX_CORPSE_QUEUE) return;
            if (!CorpseActive[corpseId]) return;
            CorpseActive[corpseId] = false;
            CorpseX[corpseId] = 0f;
            CorpseY[corpseId] = 0f;
            CorpseMonsterType[corpseId] = null;
            CorpseOwnerId[corpseId] = -1;
            CorpseHealth[corpseId] = 0f;
            CorpseDeathTime[corpseId] = 0f;
            CorpseReanimated[corpseId] = false;
        }

        // ==================== 技能组件 SOA 访问方法 ====================

        public string GetSkillName(int playerId)
        {
            if (!IsValidPlayer(playerId)) return "";
            return SkillName[playerId];
        }

        public void SetSkillName(int playerId, string name)
        {
            if (!IsValidPlayer(playerId)) return;
            SkillName[playerId] = name;
        }

        public float GetSkillDamageMultiplier(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 1f;
            return SkillDamageMultiplier[playerId];
        }

        public void SetSkillDamageMultiplier(int playerId, float multiplier)
        {
            if (!IsValidPlayer(playerId)) return;
            SkillDamageMultiplier[playerId] = multiplier;
        }

        public int GetSkillAreaWidth(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 1;
            return SkillAreaWidth[playerId];
        }

        public void SetSkillAreaWidth(int playerId, int width)
        {
            if (!IsValidPlayer(playerId)) return;
            SkillAreaWidth[playerId] = width;
        }

        public int GetSkillAreaHeight(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 1;
            return SkillAreaHeight[playerId];
        }

        public void SetSkillAreaHeight(int playerId, int height)
        {
            if (!IsValidPlayer(playerId)) return;
            SkillAreaHeight[playerId] = height;
        }

        public int GetSkillAttackRange(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 1;
            return SkillAttackRange[playerId];
        }

        public void SetSkillAttackRange(int playerId, int range)
        {
            if (!IsValidPlayer(playerId)) return;
            SkillAttackRange[playerId] = range;
        }

        public float GetSkillCooldown(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 0f;
            return SkillCooldown[playerId];
        }

        public void SetSkillCooldown(int playerId, float cooldown)
        {
            if (!IsValidPlayer(playerId)) return;
            SkillCooldown[playerId] = cooldown;
        }

        public float GetSkillCurrentCooldown(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 0f;
            return SkillCurrentCooldown[playerId];
        }

        public void SetSkillCurrentCooldown(int playerId, float currentCooldown)
        {
            if (!IsValidPlayer(playerId)) return;
            SkillCurrentCooldown[playerId] = currentCooldown;
        }

        // ==================== GAS 组件访问方法 ====================

        public AbilityInstance GetAbility(int entityId, int slot) {
            if (!IsValidEntity(entityId)) return default;
            if (slot < 0 || slot >= MAX_ABILITIES_PER_ENTITY) return default;
            return AbilityInstances[entityId * MAX_ABILITIES_PER_ENTITY + slot];
        }

        public void SetAbility(int entityId, int slot, AbilityInstance inst) {
            if (!IsValidEntity(entityId)) return;
            if (slot < 0 || slot >= MAX_ABILITIES_PER_ENTITY) return;
            AbilityInstances[entityId * MAX_ABILITIES_PER_ENTITY + slot] = inst;
        }

        public void AddAbility(int entityId, GameplayAbilityDef def) {
            if (!IsValidEntity(entityId)) return;
            int slot = AbilityCount[entityId];
            if (slot < MAX_ABILITIES_PER_ENTITY) { SetAbility(entityId, slot, new AbilityInstance(def)); AbilityCount[entityId]++; }
        }

        // Bug#9: Reset abilities for entity — clears all slots (used before re-initializing)
        public void ResetPlayerAbilities(int entityId) {
            if (!IsValidEntity(entityId)) return;
            AbilityCount[entityId] = 0;
            ActiveEffectCount[entityId] = 0;
        }

        public AppliedEffect GetEffect(int entityId, int slot) {
            if (!IsValidEntity(entityId)) return default;
            if (slot < 0 || slot >= MAX_ACTIVE_EFFECTS_PER_ENTITY) return default;
            return ActiveEffects[entityId * MAX_ACTIVE_EFFECTS_PER_ENTITY + slot];
        }

        public void SetEffect(int entityId, int slot, AppliedEffect eff) {
            if (!IsValidEntity(entityId)) return;
            if (slot < 0 || slot >= MAX_ACTIVE_EFFECTS_PER_ENTITY) return;
            ActiveEffects[entityId * MAX_ACTIVE_EFFECTS_PER_ENTITY + slot] = eff;
        }

        public int GetEffectCount(int entityId) {
            if (!IsValidEntity(entityId)) return 0;
            return ActiveEffectCount[entityId];
        }

        public void AddEffect(int entityId, AppliedEffect eff) {
            if (!IsValidEntity(entityId)) return;
            int slot = ActiveEffectCount[entityId];
            if (slot < MAX_ACTIVE_EFFECTS_PER_ENTITY) { SetEffect(entityId, slot, eff); ActiveEffectCount[entityId]++; }
        }

        public void SetEffectCount(int entityId, int count) {
            if (!IsValidEntity(entityId)) return;
            if (count < 0) count = 0;
            if (count > MAX_ACTIVE_EFFECTS_PER_ENTITY) count = MAX_ACTIVE_EFFECTS_PER_ENTITY;
            ActiveEffectCount[entityId] = count;
        }

        // ==================== 科技树组件访问方法 ====================

        public int GetResearchPoints(int playerId)
        {
            if (!IsValidPlayer(playerId)) return 0;
            return PlayerResearchPoints[playerId];
        }

        public void AddResearchPoints(int playerId, int amount)
        {
            if (!IsValidPlayer(playerId)) return;
            PlayerResearchPoints[playerId] += amount;
        }

        public bool IsTechUnlocked(int playerId, string nodeId)
        {
            if (!IsValidPlayer(playerId)) return false;
            return PlayerUnlockedTechs[playerId].Contains(nodeId);
        }

        public void UnlockTech(int playerId, string nodeId)
        {
            if (!IsValidPlayer(playerId)) return;
            PlayerUnlockedTechs[playerId].Add(nodeId);
        }

        public HashSet<string> GetUnlockedTechs(int playerId)
        {
            if (!IsValidPlayer(playerId)) return new HashSet<string>();
            // L-1 fix: return a defensive copy to prevent external mutation
            return new HashSet<string>(PlayerUnlockedTechs[playerId]);
        }
    }
}
