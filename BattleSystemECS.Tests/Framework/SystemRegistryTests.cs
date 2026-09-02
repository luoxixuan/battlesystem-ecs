using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Tests.Infrastructure;
using BattleSystemECS.Core.GAS;
using System.Reflection;

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

        [Fact]
        public void Assembly_ComboUsesConfigAndPlayerAttackProjection()
        {
            int playerId = Player(p => p.AttackDamage = 100f);
            GameConfig config = GameConfigLoader.LoadConfig(Renderer);
            config.Combo.ComboDamageBonusPerKill = 0.2f;
            config.Combo.ComboMaxMultiplier = 2f;
            config.Combo.TriggerThreshold = 3;
            var registry = new SystemRegistry();
            registry.CreateAll(Store, config, Renderer, playerId, new StateMachine());

            var effect = (GameplayEffectDefinition)typeof(SystemRegistry)
                .GetField("_runtimeComboEffect", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry)!;
            var triggers = (System.Collections.Generic.List<TriggerDefinition>)typeof(SystemRegistry)
                .GetField("_runtimeTriggers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry)!;
            Assert.Equal(new AttributeKey(0), effect.Modifiers[0].Attribute);
            Assert.Equal(AttributeModifierOp.Multiply, effect.Modifiers[0].Operation);
            Assert.Equal(1.2f, effect.Modifiers[0].Magnitude);
            Assert.Equal(3, triggers[0].Threshold);
            registry.WireDependencies(Store, playerId);
            var scheduler = new FrameScheduler(Store, config);
            registry.AssignToGroups(scheduler);
            scheduler.Phase = GameState.BuildPhase;
            scheduler.Tick(1f, 0);
            Assert.True(Store.UseComputedAttributes);
        }

        // ─── Bug 回归：构造顺序不得让消费者捕获 null ─────────────────────────
        // CreateAll 是一条线性方法，依赖靠"先构造、后当参数传入"表达，而框架
        // 不做任何校验。ReflectTower / TowerStealth 曾被排在 SuicideBomb 之后
        // 构造，于是 SuicideBomb 把 null 存进 readonly 字段且没有补注 setter，
        // 反伤与潜行判定对自爆兵路径永久失效（编译通过、旧测试全绿）。
        // 这里用反射读私有字段，因为生产没有暴露这两个依赖的只读访问器。

        [Fact]
        public void Assembly_SuicideBomb_ReceivesNonNullReflectAndStealth()
        {
            GameConfig config = GameConfigLoader.LoadConfig(Renderer);
            var stateMachine = new StateMachine();
            int playerId = Player();

            var registry = new SystemRegistry();
            registry.CreateAll(Store, config, Renderer, playerId, stateMachine);
            registry.WireDependencies(Store, playerId);

            Assert.NotNull(registry.ReflectTower);
            Assert.NotNull(registry.TowerStealth);
            Assert.NotNull(registry.SuicideBomb);

            Assert.NotNull(ReadPrivateField(registry.SuicideBomb!, "_reflectTowerSystem"));
            Assert.NotNull(ReadPrivateField(registry.SuicideBomb!, "_towerStealthSystem"));
        }

        [Fact]
        public void Assembly_EnemyAI_DoesNotRetainDeadReflectTowerEdge()
        {
            GameConfig config = GameConfigLoader.LoadConfig(Renderer);
            var stateMachine = new StateMachine();
            int playerId = Player();

            var registry = new SystemRegistry();
            registry.CreateAll(Store, config, Renderer, playerId, stateMachine);
            registry.WireDependencies(Store, playerId);

            Assert.NotNull(registry.EnemyAI);
            Assert.NotNull(registry.ReflectTower);
            Assert.Null(registry.EnemyAI!.GetType().GetField("_reflectTowerSystem",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance));
        }

        /// <summary>读取私有实例字段（生产未提供只读访问器；补测缝后可移除）。</summary>
        private static object? ReadPrivateField(object target, string fieldName)
        {
            var field = target.GetType().GetField(fieldName,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?? throw new System.InvalidOperationException(
                    $"{target.GetType().Name}.{fieldName} 字段不存在");
            return field.GetValue(target);
        }
    }
}
