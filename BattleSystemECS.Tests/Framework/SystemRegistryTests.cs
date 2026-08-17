using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Tests.Infrastructure;

namespace BattleSystemECS.Tests.Framework
{
    /// <summary>
    /// 装配自检：防止 144 系统 / 11 组接线被改断。
    ///
    /// 与单系统测试不同，本类走生产 <see cref="SystemRegistry"/> 的完整
    /// CreateAll → WireDependencies → AssignToGroups 链路，使用
    /// <see cref="GameConfigLoader.LoadConfig"/> 读出的真实配置。
    /// 任何一步接线断裂（属性未赋值、关键 group 槽位悬空）都会在此立刻失败。
    /// </summary>
    public class SystemRegistryTests : BattleTestBase
    {
        [Fact]
        public void Assembly_CreateWireAssign_AllCriticalSystemsAndGroupsPopulated()
        {
            // ── 用真实配置装配：与 GameManager 启动路径同源。──
            // Renderer 实现 IRenderer（BattleTestBase 的 MockRenderer）；
            // StateMachine 为无参构造，初始状态 Init。
            GameConfig config = GameConfigLoader.LoadConfig(Renderer);
            var stateMachine = new StateMachine();
            int playerId = Player();

            var registry = new SystemRegistry();
            registry.CreateAll(Store, config, Renderer, playerId, stateMachine);
            registry.WireDependencies(Store, playerId);
            var scheduler = new FrameScheduler(Store, config);
            registry.AssignToGroups(scheduler);

            // ── 关键系统非空：这些是跨 group 的骨干系统，接线断裂最先体现在这里。──
            Assert.NotNull(registry.WaveSpawning);
            Assert.NotNull(registry.TowerAttack);
            Assert.NotNull(registry.EnemyAI);
            Assert.NotNull(registry.Skill);
            Assert.NotNull(registry.Buff);
            Assert.NotNull(registry.TechTree);
            Assert.NotNull(registry.Pathfinding);
            Assert.NotNull(registry.EnemyMovement);
            Assert.NotNull(registry.EventBus);

            // ── 11 个 group 各选一个 AssignToGroups 实际赋值的代表性槽位。──
            // 若某 group 整个被漏配或字段改名，这里的 Assert.NotNull 会点名到 group。
            Assert.NotNull(scheduler.Build.Gold);
            Assert.NotNull(scheduler.PreGame.WaveSpawning);
            Assert.NotNull(scheduler.Spawning.WaveSpawning);
            Assert.NotNull(scheduler.AI.EnemyAI);
            Assert.NotNull(scheduler.Movement.Pathfinding);
            Assert.NotNull(scheduler.Terrain.Terrain);
            Assert.NotNull(scheduler.CombatSetup.TowerAttack);
            Assert.NotNull(scheduler.Spatial.ChronoTower);
            Assert.NotNull(scheduler.Combat.TowerAttack);
            Assert.NotNull(scheduler.SkillBuff.Buff);
            Assert.NotNull(scheduler.PostDeath.Combo);
        }

        [Fact]
        public void Assembly_BuildPhaseTick_KeepsStoreConsistent()
        {
            // ── 完整装配后跑 3 帧 BuildPhase：BuildPhase 只允许 BuildGroup 运行。──
            int playerId = Player();
            GameConfig config = GameConfigLoader.LoadConfig(Renderer);
            var stateMachine = new StateMachine();
            Assert.True(stateMachine.TransitionTo(GameState.BuildPhase));

            var registry = new SystemRegistry();
            registry.CreateAll(Store, config, Renderer, playerId, stateMachine);
            registry.WireDependencies(Store, playerId);
            var scheduler = new FrameScheduler(Store, config);
            registry.AssignToGroups(scheduler);
            scheduler.Phase = GameState.BuildPhase;

            int enemiesBefore = Store.GetActiveEnemyCount();
            int towersBefore = Store.ActiveTowerIds.Count;

            for (int turn = 0; turn < 3; turn++)
            {
                scheduler.TickGameTurn(1f, turn);
            }

            // ── BuildPhase 不应产生敌人 / 塔，玩家槽位也必须仍然有效。──
            Assert.Equal(enemiesBefore, Store.GetActiveEnemyCount());
            Assert.Equal(towersBefore, Store.ActiveTowerIds.Count);
            Assert.True(Store.IsPlayerAlive(playerId), "BuildPhase 3 帧后玩家必须仍然存活");
            Assert.True(Store.GetPlayerCurrentHealth(playerId) > 0f, "BuildPhase 不应扣除玩家生命");
        }
    }
}
