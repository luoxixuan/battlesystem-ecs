using System;
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
            Console.WriteLine("     Roguelike Tower Defense - ECS");
            Console.WriteLine("========================================");
            Console.WriteLine();
            Console.WriteLine("Press any key to start...");
            Console.ReadKey();

            var entityManager = new EntityManager();
            var renderer = new ConsoleLogger();

            var playerEntity = entityManager.CreateEntity();
            entityManager.SetName(playerEntity, "Player");
            entityManager.AddComponent(playerEntity, new PositionComponent(5f, 0f));
            entityManager.AddComponent(playerEntity, new PlayerComponent { AttackRange = 3f, AttackSpeed = 1f, AttackDamage = 10f, CurrentLevel = 1 });
            entityManager.AddComponent(playerEntity, new GoldComponent { Amount = 0f });
            entityManager.AddComponent(playerEntity, new UpgradeComponent { NextUpgradeThreshold = 100f });

            var enemyEntity = entityManager.CreateEntity();
            entityManager.SetName(enemyEntity, "Enemy");
            entityManager.AddComponent(enemyEntity, new PositionComponent(5f, 10f));
            entityManager.AddComponent(enemyEntity, new EnemyComponent { MoveSpeed = 1f, Health = 20f, MaxHealth = 20f, Damage = 5f, GoldReward = 10, WaveNumber = 1 });

            Console.WriteLine();
            renderer.Log("Game Start!");
            Console.WriteLine();
        }
    }
}
