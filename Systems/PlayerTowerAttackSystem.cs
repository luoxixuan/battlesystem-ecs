using System;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    public class PlayerTowerAttackSystem
    {
        private EntityManager em;
        private Entity playerEntity;
        private PlayerComponent player;
        private PositionComponent playerPos;
        private GoldComponent gold;
        private UpgradeComponent upgrade;
        private GameConfig gameConfig;
        private IRenderer renderer;

        public PlayerTowerAttackSystem(EntityManager entityManager, IRenderer renderer, int playerId, GameConfig gameConfig)
        {
            this.em = entityManager;
            this.renderer = renderer;
            this.playerEntity = new Entity(playerId);
            this.gameConfig = gameConfig;

            // 在构造函数中初始化缓存组件（每游戏初始化一次）
            RefreshCache();
        }

        private void RefreshCache()
        {
            if (em.HasComponent<PlayerComponent>(playerEntity))
                this.player = em.GetComponent<PlayerComponent>(playerEntity);

            if (em.HasComponent<PositionComponent>(playerEntity))
                this.playerPos = em.GetComponent<PositionComponent>(playerEntity);

            if (em.HasComponent<GoldComponent>(playerEntity))
                this.gold = em.GetComponent<GoldComponent>(playerEntity);

            if (em.HasComponent<UpgradeComponent>(playerEntity))
                this.upgrade = em.GetComponent<UpgradeComponent>(playerEntity);
        }

        public void Update()
        {
            // 更新缓存组件（每帧）
            RefreshCache();

            // 检查是否可以继续执行
            if (!em.HasComponent<PlayerComponent>(playerEntity))
                return;

            if (!em.HasComponent<PositionComponent>(playerEntity))
                return;

            // Calculate player stats with buff effects
            float attackDamage = player.AttackDamage;
            float attackRange = player.AttackRange;

            if (em.HasComponent<UpgradeComponent>(playerEntity))
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
            var enemies = em.GetAllEntities();
            int enemiesAttacked = 0;

            foreach (var enemy in enemies)
            {
                if (enemy.Id == playerEntity.Id) continue;

                if (!em.HasComponent<PositionComponent>(enemy))
                    continue;

                if (!em.HasComponent<EnemyComponent>(enemy))
                    continue;

                var enemyPos = em.GetComponent<PositionComponent>(enemy);
                var enemyHealth = em.GetComponent<EnemyComponent>(enemy);

                // Skip dead enemies
                if (enemyHealth.Health <= 0f)
                    continue;

                // Check if in attack range
                float distance = Math.Abs(enemyPos.X - playerPos.X);
                if (distance <= attackRange && enemyPos.Y > playerPos.Y)
                {
                    // Attack enemy
                    enemyHealth.Health = Math.Max(0f, enemyHealth.Health - attackDamage);
                    em.SetComponent(enemy, enemyHealth);

                    renderer.Log("[ATTACK] Player (Level " + player.CurrentLevel + ") attacks enemy " + enemy.Id + ", damage: " + attackDamage + ", position: x=" + enemyPos.X + ", y=" + enemyPos.Y);

                    if (enemyHealth.Health <= 0f)
                    {
                        if (em.HasComponent<GoldComponent>(playerEntity))
                        {
                            var goldComp = em.GetComponent<GoldComponent>(playerEntity);
                            goldComp.Amount += enemyHealth.GoldReward;
                            em.SetComponent(playerEntity, goldComp);

                            var monsterName = em.GetName(enemy);
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
