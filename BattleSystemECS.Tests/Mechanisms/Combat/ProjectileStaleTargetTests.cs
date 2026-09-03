using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Systems;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Mechanisms.Combat
{
    public sealed class ProjectileStaleTargetTests : BattleTestBase
    {
        [Fact]
        public void ProjectileRejectsTargetWhoseSlotWasRecycled()
        {
            var (player, tower, target, projectile) = CreateScenario();
            EntityHandle oldTarget = Store.GetEntityHandle(target);
            projectile.Fire(tower, target, 25f, player, 2f);

            Store.DestroyEntity(target);
            int replacement = Store.AddEnemy(2f, 0f, 0f, 100f, 100f, 0f, 0, 0);
            EntityHandle replacementHandle = Store.GetEntityHandle(replacement);
            Assert.Equal(oldTarget.Index, replacement);
            Assert.NotEqual(oldTarget.Generation, replacementHandle.Generation);

            projectile.Update(2f);

            Assert.Equal(0, projectile.ActiveProjectileCount);
            Assert.Equal(1, projectile.TotalStaleTargetRejections);
            Assert.Equal(100f, Store.EnemyHealth[replacement]);
            Assert.DoesNotContain(ReadFacts(), fact =>
                fact.Target.Equals(replacementHandle) && fact.Type == GameplayEventType.HitConfirmed);
        }

        [Fact]
        public void ProjectileStillHitsTargetWithMatchingGeneration()
        {
            var (player, tower, target, projectile) = CreateScenario();
            EntityHandle targetHandle = Store.GetEntityHandle(target);
            projectile.Fire(tower, target, 25f, player, 1f,
                isHoming: false, leadAimFactor: 1f);

            projectile.Update(2f);

            Assert.Equal(75f, Store.EnemyHealth[target]);
            Assert.Equal(0, projectile.TotalStaleTargetRejections);
            Assert.Contains(ReadFacts(), fact =>
                fact.Target.Equals(targetHandle) && fact.Type == GameplayEventType.HitConfirmed);
        }

        [Fact]
        public void ProjectileWithoutEntityTargetUsesInvalidGenerationSentinel()
        {
            var (player, tower, _, projectile) = CreateScenario();

            projectile.Fire(tower, -1, 25f, player, 1f);
            projectile.Update(1f);

            Assert.Equal(0, projectile.ActiveProjectileCount);
            Assert.Equal(0, projectile.TotalStaleTargetRejections);
            Assert.Empty(ReadFacts());
        }

        [Fact]
        public void ArcProjectileRejectsTargetWhoseSlotWasRecycled()
        {
            var (player, tower, target, projectile) = CreateScenario();
            EntityHandle oldTarget = Store.GetEntityHandle(target);
            projectile.FireWithArc(tower, target, 25f, player, 2f,
                isHoming: true, arcType: 1, arcPeakHeight: 0f, gravityScale: 0f);

            Store.DestroyEntity(target);
            int replacement = Store.AddEnemy(2f, 0f, 0f, 100f, 100f, 0f, 0, 0);
            EntityHandle replacementHandle = Store.GetEntityHandle(replacement);
            Assert.Equal(oldTarget.Index, replacement);
            Assert.NotEqual(oldTarget.Generation, replacementHandle.Generation);

            projectile.Update(2f);

            Assert.Equal(0, projectile.ActiveProjectileCount);
            Assert.Equal(1, projectile.TotalStaleTargetRejections);
            Assert.Equal(100f, Store.EnemyHealth[replacement]);
            Assert.DoesNotContain(ReadFacts(), fact =>
                fact.Target.Equals(replacementHandle) && fact.Type == GameplayEventType.HitConfirmed);
        }

        [Fact]
        public void FragmentPointProjectileKeepsItsTargetGeneration()
        {
            var (player, tower, target, projectile) = CreateScenario();
            Store.PositionX[target] = 0f;
            Store.PositionY[target] = 0f;
            int fragmentTarget = Enemy(spec =>
            {
                spec.X = 2f;
                spec.Y = 0f;
                spec.Health = 100f;
                spec.MaxHealth = 100f;
                spec.MoveSpeed = 0f;
            });
            EntityHandle oldFragmentTarget = Store.GetEntityHandle(fragmentTarget);
            projectile.Fire(tower, target, 1f, player, 1f,
                fragmentCount: 1, fragmentRange: 5f, fragmentDmgMult: 1f);
            projectile.Update(0f);
            Assert.Equal(1, projectile.ActiveProjectileCount);

            Store.DestroyEntity(fragmentTarget);
            int replacement = Store.AddEnemy(2f, 0f, 0f, 100f, 100f, 0f, 0, 0);
            EntityHandle replacementHandle = Store.GetEntityHandle(replacement);
            Assert.Equal(oldFragmentTarget.Index, replacement);
            Assert.NotEqual(oldFragmentTarget.Generation, replacementHandle.Generation);

            projectile.Update(10f);

            Assert.Equal(0, projectile.ActiveProjectileCount);
            Assert.Equal(1, projectile.TotalStaleTargetRejections);
            Assert.Equal(100f, Store.EnemyHealth[replacement]);
            Assert.DoesNotContain(ReadFacts(), fact =>
                fact.Target.Equals(replacementHandle) && fact.Type == GameplayEventType.HitConfirmed);
        }

        private (int Player, int Tower, int Target, ProjectileSystem Projectile) CreateScenario()
        {
            GameConfig config = GameConfigLoader.LoadConfigStrict(Renderer);
            int player = Player(spec =>
            {
                spec.AttackDamage = 0f;
                spec.AttackRange = 0f;
                spec.AttackSpeed = 1000f;
            });
            int tower = RawTower(0, 0, damage: 0f, range: 0, speed: 1000f);
            int target = Enemy(spec =>
            {
                spec.X = 2f;
                spec.Y = 0f;
                spec.Health = 100f;
                spec.MaxHealth = 100f;
                spec.MoveSpeed = 0f;
            });

            var registry = new SystemRegistry();
            var scheduler = new FrameScheduler(Store, config);
            new ProductionSystemInstaller().Install(registry, Store, config, Renderer, player,
                new StateMachine(), scheduler);
            Assert.True(scheduler.IsCompositionSealed);
            Assert.NotNull(registry.Projectile);
            return (player, tower, target, registry.Projectile!);
        }

        private GameplayEvent[] ReadFacts()
        {
            var facts = new GameplayEvent[Store.DamageResolver.Events.Count];
            for (int i = 0; i < facts.Length; i++)
                facts[i] = Store.DamageResolver.Events.Get(i);
            return facts;
        }
    }
}
