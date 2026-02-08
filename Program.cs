using System;
using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Systems;
using BattleSystemECS.Core;

namespace BattleSystemECS
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("========================================");
            Console.WriteLine("     肉鸽塔防游戏 - ECS 架构（纯 C#）");
            Console.WriteLine("========================================");
            Console.WriteLine();
            Console.WriteLine("游戏说明：");
            Console.WriteLine("- 地图大小：10 格子 (宽度) x 50 格子 (高度)");
            Console.WriteLine("- 玩家：最下方中间位置 (x=5, y=0)");
            Console.WriteLine("- 玩家类型：防御塔（玩家本人），自动攻击");
            Console.WriteLine("- 敌人：按波次生成，从上往下走");
            Console.WriteLine("- 战斗方式：自动攻击，波次系统");
            Console.WriteLine("- 金币：击杀敌人获得金币");
            Console.WriteLine("- 升级：金币达到阈值自动升级");
            Console.WriteLine("- 奖励：升级后随机获得 Buff");
            Console.WriteLine();

            var entityManager = new EntityManager();
            var renderer = new ConsoleLogger();

            int playerId = CreatePlayer(entityManager);

            for (int i = 0; i < 5; i++)
            {
                CreateEnemy(entityManager, 1, i);
            }

            Console.WriteLine();
            renderer.Log("========================================");
            renderer.Log("[MAP] 初始地图");
            renderer.Log("========================================");
            RenderInitialMap(renderer);
            renderer.Log("========================================");

            Console.WriteLine();
            renderer.Log("游戏开始！");

            bool gameRunning = true;
            int turn = 0;
            int maxTurns = 10; // 自动运行 10 回合后退出

            while (gameRunning && turn < maxTurns)
            {
                turn++;

                System.Threading.Thread.Sleep(1000);

                renderer.Log($"[INFO] --- 第 {turn} 回合 ---");
                ProcessPlayerTurn(entityManager, renderer, playerId);

                if (CheckEnemiesAtBottom(entityManager, renderer))
                {
                    gameRunning = false;
                }
            }

            Console.WriteLine();
            renderer.Log($"游戏结束！运行了 {turn} 回合");
            Console.WriteLine();
        }

        private static int CreatePlayer(EntityManager entityManager)
        {
            var playerEntity = entityManager.CreateEntity();
            entityManager.SetName(playerEntity, "玩家");

            entityManager.AddComponent(playerEntity, new PositionComponent(5f, 0f));

            entityManager.AddComponent(playerEntity, new PlayerComponent
            {
                AttackRange = 3f,
                AttackSpeed = 1f,
                AttackDamage = 10f,
                CurrentLevel = 1
            });

            entityManager.AddComponent(playerEntity, new GoldComponent { Amount = 0f });

            entityManager.AddComponent(playerEntity, new UpgradeComponent());

            Console.WriteLine($"[INFO] 玩家已创建！位置：x=5, y=0");
            Console.WriteLine($"[INFO] 攻击范围：3 格子，攻击速度：1次/秒");
            Console.WriteLine($"[INFO] 初始金币：0，初始等级：1");
            Console.WriteLine($"[INFO] 第一级升级需要：100 金币");

            return playerEntity.Id;
        }

        private static int CreateEnemy(EntityManager entityManager, int waveNumber, int enemyIndex)
        {
            var enemyEntity = entityManager.CreateEntity();
            string enemyName = $"敌人W{waveNumber}E{enemyIndex}";
            entityManager.SetName(enemyEntity, enemyName);

            Random random = new Random();
            float startX = (float)random.Next(0, 10);
            entityManager.AddComponent(enemyEntity, new PositionComponent(startX, 49f)); // 从顶部开始

            entityManager.AddComponent(enemyEntity, new EnemyComponent
            {
                MoveSpeed = 1f, // 每回合向下移动 1 格子
                Health = 20f,
                MaxHealth = 20f,
                Damage = 5f,
                GoldReward = 10,
                WaveNumber = waveNumber
            });

            Console.WriteLine($"[INFO] {enemyName} 已创建！位置：x={startX:F0}, y=49");
            return enemyEntity.Id;
        }

        private static void ProcessPlayerTurn(EntityManager entityManager, IRenderer renderer, int playerId)
        {
            var player = entityManager.GetComponent<PlayerComponent>(new Entity(playerId));
            var playerPos = entityManager.GetComponent<PositionComponent>(new Entity(playerId));
            var gold = entityManager.GetComponent<GoldComponent>(new Entity(playerId));
            var upgrade = entityManager.GetComponent<UpgradeComponent>(new Entity(playerId));

            // 移动敌人
            MoveEnemies(entityManager);

            // 玩家攻击
            var enemies = entityManager.GetAllEntities();

            foreach (var enemy in enemies)
            {
                if (enemy.Id == playerId) continue;

                var enemyPos = entityManager.GetComponent<PositionComponent>(enemy);
                var enemyHealth = entityManager.GetComponent<EnemyComponent>(enemy);

                // 跳过已死亡的敌人
                if (enemyHealth.Health <= 0f)
                    continue;

                // 检查是否在攻击范围内
                float distance = Math.Abs(enemyPos.X - playerPos.X);
                if (distance <= player.AttackRange && enemyPos.Y > playerPos.Y)
                {
                    float damage = player.AttackDamage;
                    enemyHealth.Health = Math.Max(0f, enemyHealth.Health - damage);
                    entityManager.SetComponent(enemy, enemyHealth);

                    renderer.Log($"[ATTACK] 玩家攻击敌人 {enemy.Id}，造成 {damage:F1} 点伤害，敌人位置：x={enemyPos.X:F0}, y={enemyPos.Y:F0}");

                    if (enemyHealth.Health <= 0f)
                    {
                        gold.Amount += enemyHealth.GoldReward;
                        entityManager.SetComponent(new Entity(playerId), gold);

                        renderer.Log($"[GOLD] 击杀敌人 {enemy.Id}，获得 {enemyHealth.GoldReward} 金币");
                        renderer.Log($"[GOLD] 当前总金币：{gold.Amount:F1}");

                        if (gold.Amount >= upgrade.NextUpgradeThreshold)
                        {
                            ProcessUpgrade(entityManager, renderer, playerId, upgrade);
                            break;
                        }
                    }
                }
            }
        }

        private static void MoveEnemies(EntityManager entityManager)
        {
            var enemies = entityManager.GetAllEntities();

            foreach (var enemy in enemies)
            {
                // 跳过玩家
                if (enemy.Id == 1) continue;

                var enemyPos = entityManager.GetComponent<PositionComponent>(enemy);
                var enemyHealth = entityManager.GetComponent<EnemyComponent>(enemy);

                // 跳过已死亡的敌人
                if (enemyHealth.Health <= 0f)
                    continue;

                // 敌人向下移动
                enemyPos.Y -= enemyHealth.MoveSpeed;
                entityManager.SetComponent(enemy, enemyPos);
            }
        }

        private static void ProcessUpgrade(EntityManager entityManager, IRenderer renderer, int playerId, UpgradeComponent upgrade)
        {
            var player = entityManager.GetComponent<PlayerComponent>(new Entity(playerId));

            player.CurrentLevel++;
            player.AttackDamage += 5f;
            player.AttackRange += 1f;
            upgrade.NextUpgradeThreshold *= 1.5f;

            entityManager.SetComponent(new Entity(playerId), player);
            entityManager.SetComponent(new Entity(playerId), upgrade);

            renderer.Log($"[UPGRADE] 玩家升级到等级 {player.CurrentLevel}！");
            renderer.Log($"[UPGRADE] 攻击力提升到 {player.AttackDamage:F1}");
            renderer.Log($"[UPGRADE] 攻击范围提升到 {player.AttackRange:F1} 格子");
            renderer.Log($"[UPGRADE] 下一级需要 {upgrade.NextUpgradeThreshold:F1} 金币");

            RandomlyGainBuff(upgrade);
        }

        private static void RandomlyGainBuff(UpgradeComponent upgrade)
        {
            string[] buffs = { "攻击力+10%", "防御力+10%", "攻击速度+20%", "暴击率+5%", "生命值+20%" };
            int randomIndex = new Random().Next(buffs.Length);
            string newBuff = buffs[randomIndex];

            if (!upgrade.Buffs.Contains(newBuff))
            {
                upgrade.Buffs.Add(newBuff);
                Console.WriteLine($"[BUFF] 获得新 Buff：{newBuff}！");
            }
        }

        private static bool CheckEnemiesAtBottom(EntityManager entityManager, IRenderer renderer)
        {
            var enemies = entityManager.GetAllEntities();

            foreach (var enemy in enemies)
            {
                if (enemy.Id == 1) continue; // 跳过玩家

                var pos = entityManager.GetComponent<PositionComponent>(enemy);
                var enemyHealth = entityManager.GetComponent<EnemyComponent>(enemy);

                // 跳过已死亡的敌人
                if (enemyHealth.Health <= 0f)
                    continue;

                if (pos.Y <= 0f)
                {
                    renderer.Log($"[INFO] 敌人 {enemy.Id} 已到达底部（y={pos.Y:F0}），游戏结束！");
                    return true;
                }
            }

            return false;
        }

        private static void RenderInitialMap(IRenderer renderer)
        {
            renderer.Log("[MAP] 10x50 地图");
            renderer.Log("[MAP] P = 玩家，E = 敌人，· = 空地");

            for (int y = 49; y >= 0; y--)
            {
                string row = "";
                for (int x = 0; x < 10; x++)
                {
                    if (y == 0 && x == 5)
                        row += "P ";
                    else if (y == 49)
                        row += "E ";
                    else
                        row += "· ";
                }
                Console.WriteLine("[MAP] " + row);
            }
        }
    }
}
