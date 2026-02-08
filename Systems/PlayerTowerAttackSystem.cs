using System;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    public class PlayerTowerAttackSystem
    {
        private EntityManager entityManager;
        private IRenderer renderer;
        private int playerId;
        private GameConfig gameConfig;

        public PlayerTowerAttackSystem(EntityManager entityManager, IRenderer renderer, int playerId, GameConfig gameConfig)
        {
            this.entityManager = entityManager;
            this.renderer = renderer;
            this.playerId = playerId;
            this.gameConfig = gameConfig;
        }

        public void Update()
        {
            if (!entityManager.HasComponent<PlayerComponent>(new Entity(playerId)))
                return;

            if (!entityManager.HasComponent<PositionComponent>(new Entity(playerId)))
                return;

            var player = entityManager.GetComponent<PlayerComponent>(new Entity(playerId));
            var playerPos = entityManager.GetComponent<PositionComponent>(new Entity(playerId));

            var upgrade = entityManager.GetComponent<UpgradeComponent>(new Entity(playerId));
            var gold = entityManager.GetComponent<GoldComponent>(new Entity(playerId));

            // Calculate player stats with buff effects
            float attackDamage = player.AttackDamage;
            float attackRange = player.AttackRange;

            if (entityManager.HasComponent<UpgradeComponent>(new Entity(playerId)))
            {
                foreach (string buff in upgrade.Buffs)
                {
                    if (buff == "Attack+10%")
                    {
                        attackDamage *= 1.1f;
                        renderer.Log("[BUFF] Attack+10% applied: " + attackDamage + " damage");
                    }
                    else if (buff == "Crit Rate+5%")
                    {
                        if (new Random().NextDouble() < 0.05)
                        {
                            attackDamage *= 2f;
                            renderer.Log("[BUFF] CRITICAL! Damage doubled: " + attackDamage);
                        }
                    }
                }
            }

            // Find and attack enemies in range
            var enemies = entityManager.GetAllEntities();
            int enemiesAttacked = 0;

            foreach (var enemy in enemies)
            {
                if (enemy.Id == playerId) continue;

                if (!entityManager.HasComponent<PositionComponent>(enemy))
                    continue;

                if (!entityManager.HasComponent<EnemyComponent>(enemy))
                    continue;

                var enemyPos = entityManager.GetComponent<PositionComponent>(enemy);
                var enemyHealth = entityManager.GetComponent<EnemyComponent>(enemy);

                // Skip dead enemies
                if (enemyHealth.Health <= 0f)
                    continue;

                // Check if in attack range
                float distance = Math.Abs(enemyPos.X - playerPos.X);
                if (distance <= attackRange && enemyPos.Y > playerPos.Y)
                {
                    // Attack enemy
                    enemyHealth.Health = Math.Max(0f, enemyHealth.Health - attackDamage);
                    entityManager.SetComponent(enemy, enemyHealth);

                    renderer.Log("[ATTACK] Player (Level " + player.CurrentLevel + ") attacks enemy " + enemy.Id + ", damage: " + attackDamage + ", position: x=" + enemyPos.X + ", y=" + enemyPos.Y);

                    if (enemyHealth.Health <= 0f)
                    {
                        if (entityManager.HasComponent<GoldComponent>(new Entity(playerId)))
                        {
                            var goldComp = entityManager.GetComponent<GoldComponent>(new Entity(playerId));
                            goldComp.Amount += enemyHealth.GoldReward;
                            entityManager.SetComponent(new Entity(playerId), goldComp);

                            var monsterName = entityManager.GetName(enemy);
                            renderer.Log("[GOLD] Killed " + monsterName + ", gained " + enemyHealth.GoldReward + " gold");
                            renderer.Log("[GOLD] Total gold: " + goldComp.Amount);
                        }

                        enemiesAttacked++;
                    }
                }
            }

            if (enemiesAttacked > 0)
            {
                renderer.Log("[COMBAT] Attacked " + enemiesAttacked + " enemies this turn");
            }
        }
    }
}
