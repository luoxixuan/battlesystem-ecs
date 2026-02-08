using Unity.Entities;
using BattleSystemECS.Components;
using BattleSystemECS.Systems;
using BattleSystemECS.Core;

namespace BattleSystemECS
{
    /// <summary>
    /// 游戏管理器 - 协调所有系统
    /// </summary>
    public class GameManager
    {
        private EntityManager entityManager;
        private IRenderer renderer;

        // 系统引用
        private MapSystem mapSystem;
        private EnemyMovementSystem enemyMovementSystem;
        private PlayerTowerAttackSystem playerTowerAttackSystem;
        private WaveSpawningSystem waveSpawningSystem;
        private GoldRewardSystem goldRewardSystem;
        private UpgradeSystem upgradeSystem;

        // 游戏状态
        public int PlayerLevel { get; private set; }
        public float PlayerGold { get; private set; }
        public int CurrentWave { get; private set; }
        public int EnemiesKilled { get; private set; }

        public GameManager(EntityManager entityManager, IRenderer renderer)
        {
            this.entityManager = entityManager;
            this.renderer = renderer;

            // 初始化所有系统
            this.mapSystem = new MapSystem(renderer);
            this.enemyMovementSystem = new EnemyMovementSystem();
            this.playerTowerAttackSystem = new PlayerTowerAttackSystem(renderer);
            this.waveSpawningSystem = new WaveSpawningSystem(renderer);
            this.goldRewardSystem = new GoldRewardSystem(renderer);
            this.upgradeSystem = new UpgradeSystem(renderer);

            // 初始化游戏状态
            this.PlayerLevel = 1;
            this.PlayerGold = 0f;
            this.CurrentWave = 1;
            this.EnemiesKilled = 0;
        }

        public void Initialize()
        {
            renderer.Log("========================================");
            renderer.Log("     肉鸽塔防游戏 - 初始化");
            renderer.Log("========================================");
            renderer.Log();
            renderer.Log("[INFO] 游戏规格：");
            renderer.Log("[INFO]   地图：10x50 格子（宽度x高度）");
            renderer.Log("[INFO]   玩家：防御塔，自动攻击");
            renderer.Log("[INFO]   敌人：波次生成，纵向移动");
            renderer.Log("[INFO]   升级：金币自动升级");
            renderer.Log("[INFO]   奖励：随机技能/Buff");
            renderer.Log();
        }

        public void Update(float deltaTime)
        {
            // 设置时间增量
            Time.DeltaTime = deltaTime;
            Time.TotalTime += deltaTime;

            // 更新所有系统
            mapSystem.Update();
            enemyMovementSystem.Update();
            playerTowerAttackSystem.Update();
            waveSpawningSystem.Update();
            goldRewardSystem.Update();
            upgradeSystem.Update();
        }

        public void GetPlayerStats()
        {
            renderer.Log("========================================");
            renderer.Log("[INFO] 玩家状态：");
            renderer.Log($"[INFO]   等级：{PlayerLevel}");
            renderer.Log($"[INFO]   金币：{PlayerGold:F1}");
            renderer.Log($"[INFO]   当前波次：{CurrentWave}");
            renderer.Log($"[INFO]   击杀数：{EnemiesKilled}");
            renderer.Log("========================================");
        }
    }
}
