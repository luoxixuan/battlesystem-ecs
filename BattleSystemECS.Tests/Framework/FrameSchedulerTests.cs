using BattleSystemECS.Tests.Infrastructure;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    /// <summary>
    /// Invariants for FrameScheduler and the game tick lifecycle.
    /// 复用 BattleTestBase 的 Store / Renderer / Config 与实体工厂，
    /// 避免手工构造组件存储、渲染器与游戏配置。
    /// </summary>
    public class FrameSchedulerTests : BattleTestBase
    {
        [Fact]
        public void TickGameTurn_SpawnsEnemies()
        {
            Player(p => { p.X = 5f; p.Y = 0f; p.Health = 200f; });

            // 测试显式注入波次总数：生产 Update 每帧固定批量生成 5 个，
            // 注入 5 正好一批，期望值完全由注入数据推导。
            var waveConfig = Config.Levels[0].Waves[0];
            waveConfig.EnemyCount = 5;
            int expectedSpawned = waveConfig.GetTotalEnemyCount();

            var waveSpawning = new WaveSpawningSystem(Store, Renderer, Config);
            var scheduler = new FrameScheduler(Store, Config);
            scheduler.Spawning.WaveSpawning = waveSpawning;

            scheduler.TickGameTurn(1f, 0);

            Assert.Equal(expectedSpawned, Store.GetActiveEnemyCount());
            Assert.Equal(expectedSpawned, waveSpawning.GetTotalEnemiesSpawned());
        }

        [Fact]
        public void Tick_ResolvesKilledEnemies()
        {
            Player(p => { p.X = 5f; p.Y = 0f; p.Health = 200f; p.Gold = 9999f; });

            // 预生成静止敌人（World.Enemy 工厂自动注册进活跃列表）。
            // 期望值从显式注入的敌人数 / 生命值推导。
            const int injectedEnemyCount = 5;
            const float injectedEnemyHealth = 20f;
            int[] enemyIds = new int[injectedEnemyCount];
            for (int i = 0; i < injectedEnemyCount; i++)
            {
                float x = 4f + i * 0.5f;
                enemyIds[i] = Enemy(e =>
                {
                    e.X = x;
                    e.Y = 1f;
                    e.Health = injectedEnemyHealth;
                    e.MaxHealth = injectedEnemyHealth;
                });
            }

            int aliveBefore = Store.GetActiveEnemyCount();
            Assert.Equal(injectedEnemyCount, aliveBefore);

            // 放置高伤塔（Tower 工厂走完整 PlaceTower 路径并自动注册活跃塔列表）。
            int towerId = Tower(5, 1, TowerType.Basic, t =>
            {
                t.Damage = 1000f;
                t.Range = 10;
                t.Speed = 1f;
                t.Cost = 50f;
            });
            Assert.Contains(towerId, Store.ActiveTowerIds);

            var scheduler = new FrameScheduler(Store, Config);
            var towerAttack = new TowerAttackSystem(Store, Renderer);
            scheduler.Combat.TowerAttack = towerAttack;
            scheduler.CombatSetup.TowerAttack = towerAttack;

            // 等价于 GameManager 每帧开头的网格重建。
            RebuildGrid();

            scheduler.Tick(1f, 0);

            // 一帧一次单目标攻击：离塔最近的敌人（x=5，与塔同格）被 1000 伤害击杀，
            // 帧末 ResolveEnemiesKilledThisFrame 统一销毁并收窄活跃列表。
            Assert.Equal(aliveBefore - 1, Store.GetActiveEnemyCount());
            Assert.Equal(1, Store.TotalKills);

            // 被杀者（第三个敌人，x=5）结算后血量为 0；未中弹的敌人保持注入血量。
            Assert.Equal(0f, Store.EnemyHealth[enemyIds[2]]);
            Assert.Equal(injectedEnemyHealth, Store.EnemyHealth[enemyIds[0]], 3);
        }

        [Fact]
        // 真实帧入口验证攻击、暴击与护甲投影，方法名保持静态规则可解析。
        public void Tick_CombatProjectionEntry()
        {
            Player();
            Store.PositionX[0] = 5f; Store.PositionY[0] = 0f;
            Store.PlayerAttackDamage[0] = 10f; Store.PlayerAttackRange[0] = 10f; Store.PlayerAttackSpeed[0] = 1f;
            Store.PlayerMaxHealth[0] = 100f; Store.PlayerCurrentHealth[0] = 100f;
            int enemyId = Enemy();
            Store.PositionX[enemyId] = 5f; Store.PositionY[enemyId] = 1f; Store.EnemyMoveSpeed[enemyId] = 0f;
            Store.EnemyPathId[enemyId] = -1;
            Store.EnemyHealth[enemyId] = 100f; Store.EnemyMaxHealth[enemyId] = 100f; Store.EnemyArmor[enemyId] = 0f;
            Store.PlayerPreFightCritBonus[0] = 0f;
            Store.PlayerIsWaveActive[0] = true;
            Store.PlayerArmor[0] = 0.2f;
            Store.UseComputedAttributes = true;
            Store.AddAttributeModifier(0, new BattleSystemECS.Core.GAS.ModifierDefinition(new BattleSystemECS.Core.GAS.AttributeKey(0), BattleSystemECS.Core.GAS.AttributeModifierOp.Multiply, 2f));
            Store.AddAttributeModifier(0, new BattleSystemECS.Core.GAS.ModifierDefinition(new BattleSystemECS.Core.GAS.AttributeKey(5), BattleSystemECS.Core.GAS.AttributeModifierOp.Add, 1f));
            Store.AddAttributeModifier(0, new BattleSystemECS.Core.GAS.ModifierDefinition(new BattleSystemECS.Core.GAS.AttributeKey(10), BattleSystemECS.Core.GAS.AttributeModifierOp.Add, 0.1f));
            var events = new EventBus();
            int criticalHits = 0;
            events.EnemyCrit.Subscribe(_ => criticalHits++);
            var attack = new PlayerTowerAttackSystem(Store, Renderer, 0, Config, null, events);
            var scheduler = new FrameScheduler(Store, Config);
            scheduler.CombatSetup.PlayerTowerAttack = attack;
            scheduler.Combat.PlayerTowerAttack = attack;
            RebuildGrid();

            scheduler.Tick(1f, 0);

            Assert.Equal(1, attack.GetCachedEnemyCount());
            Assert.True(Store.EnemyActive[enemyId]);
            scheduler.Tick(1f, 1);
            Assert.True(Store.EnemyHealth[enemyId] < 100f);
            Assert.True(criticalHits > 0);
            Assert.Equal(0.3f, Store.GetPlayerArmorProjection(0), 3);
            float computedDamage = 100f - Store.EnemyHealth[enemyId];
            Store.EnemyHealth[enemyId] = 100f;
            Store.UseComputedAttributes = false;
            scheduler.Tick(1f, 2);
            Assert.Equal(10f, 100f - Store.EnemyHealth[enemyId], 3);
            Assert.True(computedDamage > 10f);
            Store.DecreasePlayerHealth(0, 10f);
            Assert.Equal(92f, Store.PlayerCurrentHealth[0], 3);
        }
    }
}
