using System;
using System.Threading;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;

namespace BattleSystemECS
{
    /// <summary>
    /// 主程序入口 - ECS 架构，逻辑核心与渲染层完全分离
    /// </summary>
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("========================================");
            Console.WriteLine("     战斗系统 Demo - ECS 架构");
            Console.WriteLine("     逻辑核心与渲染层完全分离");
            Console.WriteLine("========================================");
            Console.WriteLine();
            Console.WriteLine("请选择渲染方式：");
            Console.WriteLine("1. 控制台日志（默认）");
            Console.WriteLine("2. 文件日志（保存到 battle_log.txt）");
            Console.WriteLine();
            Console.Write("请输入选择 (1-2): ");

            string choice = Console.ReadLine() ?? "1";  // 默认选择 1
            Console.WriteLine();

            // 创建渲染器（渲染层）
            IRenderer renderer;
            switch (choice.Trim())
            {
                case "1":
                    renderer = new ConsoleLogger();
                    ConsoleLogger.EnableLog = true;
                    Console.WriteLine("[INFO] 已选择：控制台日志渲染");
                    break;
                case "2":
                    renderer = new FileLogger("battle_log.txt");
                    ((FileLogger)renderer).ClearLog();
                    Console.WriteLine("[INFO] 已选择：文件日志渲染");
                    Console.WriteLine("[INFO] 日志将保存到: battle_log.txt");
                    break;
                default:
                    renderer = new ConsoleLogger();
                    ConsoleLogger.EnableLog = true;
                    Console.WriteLine("[INFO] 无效选择，使用默认：控制台日志渲染");
                    break;
            }

            Console.WriteLine();
            Console.WriteLine("========================================");

            // 创建实体管理器
            EntityManager entityManager = new EntityManager();

            // 创建战斗系统（逻辑核心）- 注入渲染器
            CombatSystem combatSystem = new CombatSystem(entityManager, renderer);

            // 创建玩家
            int playerId = CreatePlayer(entityManager);

            // 创建敌人
            int enemyId = CreateEnemy(entityManager);

            // 开始战斗
            combatSystem.StartBattle(playerId, enemyId);

            Console.WriteLine();
            renderer.Log("战斗开始！");

            // 战斗循环
            Console.WriteLine();

            while (combatSystem.IsBattleInProgress)
            {
                // 每回合等待 1 秒
                Thread.Sleep(1000);

                // 处理战斗
                combatSystem.ProcessBattle();
            }

            Console.WriteLine();
            renderer.Log("战斗结束！");
            Console.WriteLine();
            renderer.Log("程序即将退出...");
        }

        private static int CreatePlayer(EntityManager entityManager)
        {
            int playerId = entityManager.CreateEntity();

            // 添加组件
            entityManager.AddComponent(playerId, new NameComponent("玩家"));
            entityManager.AddComponent(playerId, new HealthComponent(100f, 100f));
            entityManager.AddComponent(playerId, new AttackPowerComponent(50f));
            entityManager.AddComponent(playerId, new DefensePowerComponent(20f));
            entityManager.AddComponent(playerId, new PlayerTagComponent());
            entityManager.AddComponent(playerId, new BattleStateComponent()
            {
                CurrentState = BattleStateComponent.State.Fighting
            });

            // 添加技能组件（火球术）
            entityManager.AddComponent(playerId, new SkillComponent("火球术", 60f, 10f, 20, 100));

            // 添加 Buff（增加攻击力）
            entityManager.AddComponent(playerId, new BuffComponent("狂暴", 5f, 0f, 0f, 0f));

            return playerId;
        }

        private static int CreateEnemy(EntityManager entityManager)
        {
            int enemyId = entityManager.CreateEntity();

            // 添加组件
            entityManager.AddComponent(enemyId, new NameComponent("敌人"));
            entityManager.AddComponent(enemyId, new HealthComponent(100f, 100f));
            entityManager.AddComponent(enemyId, new AttackPowerComponent(40f));
            entityManager.AddComponent(enemyId, new DefensePowerComponent(15f));
            entityManager.AddComponent(enemyId, new EnemyTagComponent());
            entityManager.AddComponent(enemyId, new BattleStateComponent()
            {
                CurrentState = BattleStateComponent.State.Fighting
            });

            // 添加技能组件（暗影箭）
            entityManager.AddComponent(enemyId, new SkillComponent("暗影箭", 50f, 8f, 15, 100));

            // 添加 Debuff（减少防御力）
            entityManager.AddComponent(enemyId, new DebuffComponent("虚弱", 0f, 5f, 0f, 0f));

            return enemyId;
        }
    }
}
