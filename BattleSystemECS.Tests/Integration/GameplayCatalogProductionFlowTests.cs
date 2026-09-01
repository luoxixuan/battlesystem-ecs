using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Integration
{
    public sealed class GameplayCatalogProductionFlowTests : BattleTestBase
    {
        [Fact]
        public void CompiledComboEffectChangesPlayerAttackAfterProductionTick()
        {
            int playerId = Player(p =>
            {
                p.X = 0f;
                p.Y = 0f;
                p.AttackDamage = 100f;
                p.AttackRange = 10f;
                p.AttackSpeed = 1f;
            });
            int enemyId = Enemy(e =>
            {
                e.X = 0f;
                e.Y = 0.1f;
                e.Health = 1000f;
                e.MaxHealth = 1000f;
                e.Damage = 0f;
                e.MoveSpeed = 0f;
            });

            var config = GameConfigLoader.LoadStrictCatalog(Renderer);
            config.Combo.TriggerThreshold = 1;
            config.Combo.ComboDamageBonusPerKill = 0.5f;
            config.Combo.ComboMaxMultiplier = 2f;
            var stateMachine = new StateMachine();
            var registry = new SystemRegistry();
            registry.CreateAll(Store, config, Renderer, playerId, stateMachine);
            registry.WireDependencies(Store, playerId);
            var scheduler = new FrameScheduler(Store, config);
            registry.AssignToGroups(scheduler);
            scheduler.Phase = GameState.WavePhase;

            Assert.NotNull(config.CompiledCatalog);
            Assert.NotEmpty(config.CompiledCatalog!.Effects);
            Assert.NotEmpty(config.CompiledCatalog.Triggers);
            var comboEffect = config.CompiledCatalog.Effects[config.CompiledCatalog.Effects.Count - 1];
            var comboTrigger = config.CompiledCatalog.Triggers[config.CompiledCatalog.Triggers.Count - 1];
            Assert.Equal(new AttributeKey(0), comboEffect.Modifiers[0].Attribute);
            Assert.Equal(AttributeModifierOp.Multiply, comboEffect.Modifiers[0].Operation);
            Assert.Equal(1.5f, comboEffect.Modifiers[0].Magnitude, 3);
            Assert.Equal(TriggerScope.PerSource, comboTrigger.Scope);
            Assert.Equal(1, comboTrigger.Threshold);
            Assert.Equal(comboEffect.Id, comboTrigger.Effect);

            float firstBefore = Store.EnemyHealth[enemyId];
            scheduler.Tick(0.016f, 0);
            Assert.True(Store.UseComputedAttributes);
            float firstDamage = firstBefore - Store.EnemyHealth[enemyId];
            Assert.True(firstDamage > 0f);
            bool comboApplied = false;
            for (int slot = 0; slot < Store.GetEffectCount(playerId); slot++)
            {
                if (!Store.TryGetActiveEffectAt(playerId, slot, out var active, out var appliedDefinition, out _)) continue;
                if (appliedDefinition.Id != comboEffect.Id) continue;
                comboApplied = true;
                Assert.Equal(Store.GetEntityHandle(playerId), active.Source);
                Assert.Equal(Store.GetEntityHandle(playerId), active.Target);
            }
            Assert.True(comboApplied);

            float projection = Store.GetPlayerAttackDamageProjection(playerId);
            Assert.Equal(150f, projection, 3);
            // The production target may carry post-hit i-frames; clear that
            // target-owned lifecycle state so this test isolates combo damage.
            Store.EnemyInvulnFramesLeft[enemyId] = 0;
            Store.EnemyBlinkIFramesLeft[enemyId] = 0f;
            float secondBefore = Store.EnemyHealth[enemyId];
            scheduler.Tick(0.016f, 1);
            float secondDamage = secondBefore - Store.EnemyHealth[enemyId];
            Assert.True(secondDamage > firstDamage,
                $"compiled player modifier must increase the next production attack (first={firstDamage}, second={secondDamage})");
            Assert.InRange(secondDamage, projection * 0.9f, projection * 1.2f);
        }
    }
}
