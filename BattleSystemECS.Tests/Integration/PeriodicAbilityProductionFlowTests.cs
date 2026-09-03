using System;
using System.IO;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Integration
{
    public sealed class PeriodicAbilityProductionFlowTests : BattleTestBase
    {
        [Fact]
        public void PoisonNovaActivationAppliesPeriodicEffectAndTicksDamage()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Data", "Configs", "skills.json");
            if (!File.Exists(path)) path = Path.Combine(Directory.GetCurrentDirectory(), "Data", "Configs", "skills.json");
            var catalog = CatalogCompiler.Compile(path);
            Assert.True(catalog.TryResolveAlias("Poison Nova", out var poisonId));
            Assert.True(catalog.TryGetAbility(poisonId, out var poison));
            Assert.Single(poison.Effects);
            Assert.True(catalog.TryGetEffect(poison.Effects[0], out var poisonEffect));
            Assert.True(poisonEffect.Periodic.HasValue);
            float expectedTick = poisonEffect.Periodic.Value.Magnitude;
            Assert.True(expectedTick > 0f);

            Store.GameplayPhaseContext = new PhaseContext(PhaseContextKind.Wave);
            int playerId = Player(p =>
            {
                p.X = 0f;
                p.Y = 0f;
                p.AttackDamage = 1f;
            });
            int enemyId = Enemy(e =>
            {
                e.X = 0f;
                e.Y = 0.1f;
                e.Health = 100f;
                e.MaxHealth = 100f;
                e.Damage = 0f;
                e.MoveSpeed = 0f;
            });

            var timers = new float[1];
            var result = GameplayAbilityRuntime.Activate(
                Store,
                catalog,
                timers,
                new AbilityActivationRequest(playerId, 0, 0f, enemyId, ability: poisonId));

            Assert.True(result.Accepted, result.Reason.ToString());
            Assert.True(result.AppliedEffects > 0);
            Assert.True(Store.GetEffectCount(enemyId) > 0);

            float healthBeforeTick = Store.EnemyHealth[enemyId];
            Store.GameplayEffectsRuntime.Tick(1f, ClockId.Combat);
            float damageDealt = healthBeforeTick - Store.EnemyHealth[enemyId];
            Assert.True(damageDealt > 0f, "periodic tick must deal damage after Poison Nova activation");
            Assert.Equal(expectedTick, damageDealt, 3);
        }
    }
}
