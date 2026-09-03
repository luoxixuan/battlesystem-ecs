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
            Assert.Equal(CatalogRegistries.DamageOutputMultiplier, comboEffect.Modifiers[0].Attribute);
            Assert.Equal(AttributeModifierOp.Add, comboEffect.Modifiers[0].Operation);
            Assert.Equal(config.Combo.ComboDamageBonusPerKill, comboEffect.Modifiers[0].Magnitude, 3);
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

            float attackBase = Store.GetPlayerAttackDamage(playerId);
            float expectedProjection = attackBase * (1f + config.Combo.ComboDamageBonusPerKill);
            float projection = Store.GetPlayerAttackDamageProjection(playerId);
            Assert.Equal(expectedProjection, projection, 3);
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

        [Fact]
        public void CompiledComboEffectChangesTowerAttackAfterProductionTick()
        {
            int playerId = Player(p =>
            {
                p.X = 0f;
                p.Y = 0f;
                p.AttackDamage = 1f;
                p.AttackRange = 1f;
                p.AttackSpeed = 0.01f;
            });
            int enemyId = Enemy(e =>
            {
                e.X = 5f;
                e.Y = 1f;
                e.Health = 5000f;
                e.MaxHealth = 5000f;
                e.Damage = 0f;
                e.MoveSpeed = 0f;
            });
            float towerBaseDamage = 40f;
            float bonus = 0.25f;
            int towerId = RawTower(5, 1, BattleSystemECS.Components.TowerType.Basic,
                damage: towerBaseDamage, range: 3, speed: 1f);
            Store.TowerCritChance[towerId] = 0f;
            Store.TowerDamageVariance[towerId] = 0f;

            var config = GameConfigLoader.LoadStrictCatalog(Renderer);
            config.Combo.TriggerThreshold = 1;
            config.Combo.ComboDamageBonusPerKill = bonus;
            config.Combo.ComboMaxMultiplier = 3f;
            var stateMachine = new StateMachine();
            var registry = new SystemRegistry();
            registry.CreateAll(Store, config, Renderer, playerId, stateMachine);
            registry.WireDependencies(Store, playerId);
            var scheduler = new FrameScheduler(Store, config);
            registry.AssignToGroups(scheduler);
            scheduler.Phase = GameState.WavePhase;
            Store.RebuildSpatialGrid();

            var comboEffect = config.CompiledCatalog!.Effects[config.CompiledCatalog.Effects.Count - 1];
            float firstBefore = Store.EnemyHealth[enemyId];
            scheduler.Tick(1f, 0);
            float firstDamage = firstBefore - Store.EnemyHealth[enemyId];
            Assert.True(firstDamage > 0f, "tower must land at least one hit in the first production tick");

            bool comboOnTower = false;
            for (int slot = 0; slot < Store.GetEffectCount(towerId); slot++)
            {
                if (!Store.TryGetActiveEffectAt(towerId, slot, out _, out var appliedDefinition, out _)) continue;
                if (appliedDefinition.Id != comboEffect.Id) continue;
                comboOnTower = true;
            }
            Assert.True(comboOnTower, "HitConfirmed combo must attach to the attacking tower source");

            float baseDamage = Store.TowerAttackDamage[towerId];
            float expected = baseDamage * (1f + bonus);
            float boosted = Store.GetTowerAttackDamage(towerId);
            Assert.Equal(expected, boosted, 3);
            Assert.True(boosted > baseDamage);

            Store.EnemyInvulnFramesLeft[enemyId] = 0;
            Store.EnemyBlinkIFramesLeft[enemyId] = 0f;
            Store.TowerLastAttackTime[towerId] = Store.TowerAttackSpeed[towerId] > 0f
                ? 1f / Store.TowerAttackSpeed[towerId]
                : 1f;
            float secondBefore = Store.EnemyHealth[enemyId];
            scheduler.Tick(1f, 1);
            float secondDamage = secondBefore - Store.EnemyHealth[enemyId];
            Assert.True(secondDamage >= boosted * 0.9f,
                $"boosted tower projection must drive subsequent damage (boosted={boosted}, second={secondDamage})");
        }
    }
}
