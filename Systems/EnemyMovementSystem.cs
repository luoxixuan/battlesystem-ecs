using System;
using BattleSystemECS.Components;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    public class EnemyMovementSystem
    {
        private EntityManager entityManager;
        private IRenderer renderer;

        public EnemyMovementSystem(EntityManager entityManager, IRenderer renderer)
        {
            this.entityManager = entityManager;
            this.renderer = renderer;
        }

        public void Update()
        {
            var entities = entityManager.GetAllEntities();
            int enemiesMoved = 0;

            foreach (var entity in entities)
            {
                if (entity.Id == 1) continue;

                if (!entityManager.HasComponent<PositionComponent>(entity))
                    continue;

                if (!entityManager.HasComponent<EnemyComponent>(entity))
                    continue;

                var enemyPos = entityManager.GetComponent<PositionComponent>(entity);
                var enemyHealth = entityManager.GetComponent<EnemyComponent>(entity);

                // Skip dead enemies
                if (enemyHealth.Health <= 0f)
                    continue;

                // Enemy moves downward
                enemyPos.Y -= enemyHealth.MoveSpeed;
                entityManager.SetComponent(entity, enemyPos);

                enemiesMoved++;
            }

            if (enemiesMoved > 0)
            {
                renderer.Log("[MOVE] " + enemiesMoved + " enemies moved downward");
            }
        }
    }
}
