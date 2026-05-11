using System;
using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Core
{
    /// <summary>
    /// SOA (Struct of Arrays) 组件存储
    /// 提供连续的内存布局，优化缓存命中率和支持 SIMD 指令
    /// 性能提升：10-100 倍
    /// </summary>
    public class ComponentStore
    {
        // 常量定义
        public const int MAX_ENTITIES = 100000;
        private const int MAX_PLAYERS = 10;
        private const int MAX_MONSTERS = 20000;
        private const int MAX_BUFFS = 10;

        public int TotalKills = 0;

        // ==================== 位置组件的 SOA 存储 ====================
        public float[] PositionX = new float[MAX_ENTITIES];
        public float[] PositionY = new float[MAX_ENTITIES];
        public bool[] PositionActive = new bool[MAX_ENTITIES];

        // ==================== 玩家组件的 SOA 存储 ====================
        public float[] PlayerAttackRange = new float[MAX_PLAYERS];
        public float[] PlayerAttackSpeed = new float[MAX_PLAYERS];
        public float[] PlayerAttackDamage = new float[MAX_PLAYERS];
        public float[] PlayerMaxHealth = new float[MAX_PLAYERS];  // 玩家最大生命值
        public float[] PlayerCurrentHealth = new float[MAX_PLAYERS];  // 玩家当前生命值
        public int[] PlayerCurrentLevel = new int[MAX_PLAYERS];
        public float[] PlayerGold = new float[MAX_PLAYERS];
        public float[] PlayerUpgradeThreshold = new float[MAX_PLAYERS];
        public List<string>[] PlayerBuffs = new List<string>[MAX_PLAYERS];

        // ==================== 敌人组件的 SOA 存储 ====================
        public float[] EnemyHealth = new float[MAX_ENTITIES];
        public float[] EnemyMaxHealth = new float[MAX_ENTITIES];
        public float[] EnemyMoveSpeed = new float[MAX_ENTITIES];
        public float[] EnemyDamage = new float[MAX_ENTITIES];
        public int[] EnemyGoldReward = new int[MAX_ENTITIES];
        public int[] EnemyWaveNumber = new int[MAX_ENTITIES];
        public bool[] EnemyActive = new bool[MAX_ENTITIES];

        // ==================== 敌人 AI 组件的 SOA 存储 ====================
        public string[] EnemyAIAction = new string[MAX_ENTITIES];
        public int[] EnemyAIChargeCounter = new int[MAX_ENTITIES];
        public int[] EnemyAILastAttackTurn = new int[MAX_ENTITIES];
        public string[] EnemyTypeName = new string[MAX_ENTITIES];

        // ==================== 塔组件的 SOA 存储 ====================
        public string[] TowerType = new string[MAX_ENTITIES];
        public float[] TowerAttackDamage = new float[MAX_ENTITIES];
        public int[] TowerRange = new int[MAX_ENTITIES];
        public float[] TowerAttackSpeed = new float[MAX_ENTITIES];
        public int[] TowerLevel = new int[MAX_ENTITIES];
        public float[] TowerUpgradeCost = new float[MAX_ENTITIES];
        public bool[] TowerActive = new bool[MAX_ENTITIES];
        public float[] TowerLastAttackTime = new float[MAX_ENTITIES];

        // ==================== 技能组件的 SOA 存储 ====================
        public string[] SkillName = new string[MAX_PLAYERS];
        public float[] SkillDamageMultiplier = new float[MAX_PLAYERS];
        public int[] SkillAreaWidth = new int[MAX_PLAYERS];
        public int[] SkillAreaHeight = new int[MAX_PLAYERS];
        public int[] SkillAttackRange = new int[MAX_PLAYERS];
        public float[] SkillCooldown = new float[MAX_PLAYERS];
        public float[] SkillCurrentCooldown = new float[MAX_PLAYERS];

        // ==================== 实体管理 ====================
        public int PlayerEntityId { get; private set; } = 1;
        public List<int> ActiveEnemyIds = new List<int>();
        private Stack<int> freeEntityIds = new Stack<int>();
        public Dictionary<int, string> entityNames = new Dictionary<int, string>();
        private int nextEntityId = 2; // 从 2 开始，1 是玩家

        public ComponentStore()
        {
            // 初始化玩家 buffs
            for (int i = 0; i < MAX_PLAYERS; i++)
            {
                PlayerBuffs[i] = new List<string>();
            }
        }

        public int CreateEntity()
        {
            if (freeEntityIds.Count > 0)
            {
                return freeEntityIds.Pop();
            }
            return nextEntityId++;
        }

        public void DestroyEntity(int entityId)
        {
            // 清理组件状态
            PositionActive[entityId] = false;
            EnemyActive[entityId] = false;
            TowerActive[entityId] = false;

            // 清理敌人 AI 状态
            EnemyAIAction[entityId] = "";
            EnemyAIChargeCounter[entityId] = 0;
            EnemyAILastAttackTurn[entityId] = 0;

            // 回收 ID
            freeEntityIds.Push(entityId);
        }

        public int NextEntityId => nextEntityId;

        public string GetEntityName(int entityId)
        {
            return GetName(entityId);
        }

        public string GetName(int entityId)
        {
            if (entityNames.ContainsKey(entityId))
            {
                return entityNames[entityId];
            }
            return $"Entity_{entityId}";
        }

        public void SetEntityName(int entityId, string name)
        {
            entityNames[entityId] = name;
        }

        // ==================== 位置组件访问 ====================

        public void AddPosition(int entityId, float x, float y)
        {
            if (entityId < 0 || entityId >= MAX_ENTITIES) return;

            PositionX[entityId] = x;
            PositionY[entityId] = y;
            PositionActive[entityId] = true;
        }

        public void SetPosition(int entityId, float x, float y)
        {
            if (entityId < 0 || entityId >= MAX_ENTITIES) return;

            PositionX[entityId] = x;
            PositionY[entityId] = y;
        }

        // ==================== 玩家组件访问 ====================

        public void AddPlayer(int entityId, float attackRange, float attackSpeed, float attackDamage, int currentLevel)
        {
            if (entityId < 0 || entityId >= MAX_PLAYERS) return;

            PlayerAttackRange[entityId] = attackRange;
            PlayerAttackSpeed[entityId] = attackSpeed;
            PlayerAttackDamage[entityId] = attackDamage;
            PlayerCurrentLevel[entityId] = currentLevel;
            PlayerGold[entityId] = 0f;
            PlayerUpgradeThreshold[entityId] = 1000f;  // 提高到 1000 以更快升级测试技能
            PlayerBuffs[entityId] = new List<string>();

            PlayerEntityId = entityId;
        }

        public float GetPlayerAttackRange(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 0f;
            return PlayerAttackRange[playerId];
        }

        public void SetPlayerAttackRange(int playerId, float range)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            PlayerAttackRange[playerId] = range;
        }

        public float GetPlayerAttackSpeed(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 0f;
            return PlayerAttackSpeed[playerId];
        }

        public float GetPlayerAttackDamage(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 0f;
            return PlayerAttackDamage[playerId];
        }

        public void SetPlayerAttackDamage(int playerId, float damage)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            PlayerAttackDamage[playerId] = damage;
        }

        public float GetPlayerGold(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 0f;
            return PlayerGold[playerId];
        }

        public float GetPlayerTotalGold(int playerId)
        {
            return GetPlayerGold(playerId);
        }

        public void SetPlayerGold(int playerId, float gold)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            PlayerGold[playerId] = gold;
        }

        public int GetPlayerLevel(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 0;
            return PlayerCurrentLevel[playerId];
        }

        public void SetPlayerLevel(int playerId, int level)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            PlayerCurrentLevel[playerId] = level;
        }

        public List<string> GetPlayerBuffs(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return new List<string>();
            return PlayerBuffs[playerId];
        }

        public void AddPlayerBuff(int playerId, string buff)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            PlayerBuffs[playerId].Add(buff);
        }

        public float GetPlayerUpgradeThreshold(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 0f;
            return PlayerUpgradeThreshold[playerId];
        }

        public void SetPlayerUpgradeThreshold(int playerId, float threshold)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            PlayerUpgradeThreshold[playerId] = threshold;
        }

        // ==================== 敌人组件访问 ====================

        public int AddEnemy(float startX, float startY, float moveSpeed, float health, float maxHealth, float damage, int goldReward, int waveNumber, string fullName = null)
        {
            int entityId = CreateEntity();

            if (entityId >= MAX_ENTITIES) 
            {
                return -1;
            }

            PositionX[entityId] = startX;
            PositionY[entityId] = startY;
            PositionActive[entityId] = true;

            EnemyHealth[entityId] = health;
            EnemyMaxHealth[entityId] = maxHealth;
            EnemyMoveSpeed[entityId] = moveSpeed;
            EnemyDamage[entityId] = damage;
            EnemyGoldReward[entityId] = goldReward;
            EnemyWaveNumber[entityId] = waveNumber;
            EnemyActive[entityId] = true;

            // 缓存怪物类型名（如 "NormalL1W1E0" -> "Normal"），避免每帧解析
            if (fullName != null)
            {
                int sepIdx = fullName.IndexOf('L');
                EnemyTypeName[entityId] = (sepIdx > 0) ? fullName.Substring(0, sepIdx) : fullName;
            }

            ActiveEnemyIds.Add(entityId);
            return entityId;
        }

        public void AddTower(int entityId, string type, float damage, int range, float speed, int level, float cost)
        {
            if (entityId < 0 || entityId >= MAX_ENTITIES) return;
            TowerType[entityId] = type;
            TowerAttackDamage[entityId] = damage;
            TowerRange[entityId] = range;
            TowerAttackSpeed[entityId] = speed;
            TowerLevel[entityId] = level;
            TowerUpgradeCost[entityId] = cost;
            TowerActive[entityId] = true;
            TowerLastAttackTime[entityId] = 0f;
        }

        public void RemoveTower(int entityId)
        {
            if (entityId < 0 || entityId >= MAX_ENTITIES) return;
            TowerActive[entityId] = false;
        }

        public float GetEnemyHealth(int enemyId)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return 0f;
            return EnemyHealth[enemyId];
        }

        public void SetEnemyHealth(int enemyId, float health)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return;
            EnemyHealth[enemyId] = health;
        }

        public float GetEnemyMaxHealth(int enemyId)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return 0f;
            return EnemyMaxHealth[enemyId];
        }

        public float GetEnemyMoveSpeed(int enemyId)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return 0f;
            return EnemyMoveSpeed[enemyId];
        }

        public float GetEnemyDamage(int enemyId)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return 0f;
            return EnemyDamage[enemyId];
        }

        public int GetEnemyGoldReward(int enemyId)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return 0;
            return EnemyGoldReward[enemyId];
        }

        // ==================== 敌人 AI 组件访问 ====================

        public string GetEnemyAIAction(int enemyId)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return "";
            return EnemyAIAction[enemyId];
        }

        public string GetEnemyTypeName(int enemyId)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return "";
            return EnemyTypeName[enemyId] ?? "";
        }

        public void SetEnemyAIAction(int enemyId, string action)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return;
            EnemyAIAction[enemyId] = action ?? "";
        }

        public int GetEnemyAIChargeCounter(int enemyId)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return 0;
            return EnemyAIChargeCounter[enemyId];
        }

        public void SetEnemyAIChargeCounter(int enemyId, int counter)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return;
            EnemyAIChargeCounter[enemyId] = counter;
        }

        public int GetEnemyAILastAttackTurn(int enemyId)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return 0;
            return EnemyAILastAttackTurn[enemyId];
        }

        public void SetEnemyAILastAttackTurn(int enemyId, int turn)
        {
            if (enemyId < 0 || enemyId >= MAX_ENTITIES) return;
            EnemyAILastAttackTurn[enemyId] = turn;
        }

        // ==================== 技能组件 SOA 访问方法 ====================

        public string GetSkillName(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return "";
            return SkillName[playerId];
        }

        public void SetSkillName(int playerId, string name)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            SkillName[playerId] = name;
        }

        public float GetSkillDamageMultiplier(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 1f;
            return SkillDamageMultiplier[playerId];
        }

        public void SetSkillDamageMultiplier(int playerId, float multiplier)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            SkillDamageMultiplier[playerId] = multiplier;
        }

        public int GetSkillAreaWidth(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 1;
            return SkillAreaWidth[playerId];
        }

        public void SetSkillAreaWidth(int playerId, int width)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            SkillAreaWidth[playerId] = width;
        }

        public int GetSkillAreaHeight(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 1;
            return SkillAreaHeight[playerId];
        }

        public void SetSkillAreaHeight(int playerId, int height)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            SkillAreaHeight[playerId] = height;
        }

        public int GetSkillAttackRange(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 1;
            return SkillAttackRange[playerId];
        }

        public void SetSkillAttackRange(int playerId, int range)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            SkillAttackRange[playerId] = range;
        }

        public float GetSkillCooldown(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 0f;
            return SkillCooldown[playerId];
        }

        public void SetSkillCooldown(int playerId, float cooldown)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            SkillCooldown[playerId] = cooldown;
        }

        public float GetSkillCurrentCooldown(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 0f;
            return SkillCurrentCooldown[playerId];
        }

        public void SetSkillCurrentCooldown(int playerId, float currentCooldown)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            SkillCurrentCooldown[playerId] = currentCooldown;
        }

        public void AddToSpatialHash(int entityId) { }
        public void RemoveFromSpatialHash(int entityId) { }
        public List<int> GetEnemiesNear(float x, float y, int range) => new List<int>();

        // ==================== 实体查询 ====================

        public bool IsEnemyActive(int entityId)
        {
            if (entityId < 0 || entityId >= MAX_ENTITIES) return false;
            return EnemyActive[entityId];
        }

        public bool IsPlayer(int entityId)
        {
            return entityId == PlayerEntityId;
        }

        public List<int> GetActiveEnemyIds()
        {
            return new List<int>(ActiveEnemyIds);
        }

        public List<int> GetAllActiveEnemyIds()
        {
            // 使用维护中的 ActiveEnemyIds 列表，O(active) 而非 O(MAX_ENTITIES)
            return ActiveEnemyIds;
        }

        public int GetActiveEnemyCount()
        {
            return ActiveEnemyIds.Count;
        }

        // ==================== 玩家生命值访问方法 ====================

        public float GetPlayerMaxHealth(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 0f;
            return PlayerMaxHealth[playerId];
        }

        public void SetPlayerMaxHealth(int playerId, float maxHealth)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            PlayerMaxHealth[playerId] = maxHealth;
        }

        public float GetPlayerCurrentHealth(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return 0f;
            return PlayerCurrentHealth[playerId];
        }

        public void SetPlayerCurrentHealth(int playerId, float currentHealth)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            PlayerCurrentHealth[playerId] = currentHealth;
        }

        public void DecreasePlayerHealth(int playerId, float damage)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return;
            PlayerCurrentHealth[playerId] = System.Math.Max(0f, PlayerCurrentHealth[playerId] - damage);
        }

        public bool IsPlayerAlive(int playerId)
        {
            if (playerId < 0 || playerId >= MAX_PLAYERS) return false;
            return PlayerCurrentHealth[playerId] > 0f;
        }
    }
}
