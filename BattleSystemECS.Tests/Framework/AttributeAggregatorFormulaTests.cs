using System;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Systems;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    /// <summary>P3 Lumio 属性公式：Percent 加项、Override 最终覆盖、Multiply 守卫。</summary>
    public sealed class AttributeAggregatorFormulaTests : BattleTestBase
    {
        private static readonly AttributeKey Damage = CatalogRegistries.DamageOutputMultiplier;
        private static readonly AttributeKey Attack = CatalogRegistries.AttackDamage;

        private static ModifierDefinition Mod(AttributeKey key, AttributeModifierOp op, float value, int priority = 0)
            => new ModifierDefinition(key, op, value, priority);

        [Fact]
        public void TwoPercentBonusesSumToOnePointSixtyNotProduct()
        {
            var a = new AttributeAggregator();
            a.SetBase(1, Damage, 1f);
            a.AddModifier(1, Mod(Damage, AttributeModifierOp.Percent, 0.30f));
            a.AddModifier(1, Mod(Damage, AttributeModifierOp.Percent, 0.30f));
            float computed = a.GetComputed(1, Damage);
            Assert.Equal(1.60f, computed, 5);
            Assert.NotEqual(1.69f, computed, 5);

            a.SetBase(1, Damage, 10f);
            Assert.Equal(16f, a.GetComputed(1, Damage), 5);
            Assert.NotEqual(10.60f, a.GetComputed(1, Damage), 5);
        }

        [Fact]
        public void TwoNegativePercentBonusesClampFactorToPercentFloorZero()
        {
            var a = new AttributeAggregator();
            a.SetBase(1, Damage, 10f);
            a.AddModifier(1, Mod(Damage, AttributeModifierOp.Percent, -0.60f));
            a.AddModifier(1, Mod(Damage, AttributeModifierOp.Percent, -0.60f));
            Assert.Equal(0f, a.GetComputed(1, Damage), 5);
            Assert.NotEqual(-2f, a.GetComputed(1, Damage), 5);
        }

        [Fact]
        public void Schema_PercentFloorDefaultsToZero()
        {
            Assert.Equal(0f, AttributeSchema.Default.Get(Attack).PercentFloor);
            Assert.Equal(0f, AttributeSchema.Default.Get(Damage).PercentFloor);
        }

        [Fact]
        public void CustomPercentFloorIsAppliedBeforeClamp()
        {
            var schema = new AttributeSchema(new[]
            {
                new AttributeDefinition(Damage, "DamageOutputMultiplier", AttributeDomain.Combat, 1f,
                    AttributeUnit.Scalar, 0f, float.PositiveInfinity, true, 0.1f)
            });
            var a = new AttributeAggregator(schema);
            a.SetBase(1, Damage, 10f);
            a.AddModifier(1, Mod(Damage, AttributeModifierOp.Percent, -0.60f));
            a.AddModifier(1, Mod(Damage, AttributeModifierOp.Percent, -0.60f));
            Assert.Equal(1f, a.GetComputed(1, Damage), 5);
        }

        [Fact]
        public void OverrideIgnoresAddAndPercentEvenAtNineNineNine()
        {
            var a = new AttributeAggregator();
            a.SetBase(1, Damage, 10f);
            a.AddModifier(1, Mod(Damage, AttributeModifierOp.Add, 5f, 10));
            a.AddModifier(1, Mod(Damage, AttributeModifierOp.Percent, 0.30f, 10));
            a.AddModifier(1, Mod(Damage, AttributeModifierOp.Override, 999f, 0));
            Assert.Equal(999f, a.GetComputed(1, Damage), 5);
        }

        [Fact]
        public void SamePriorityOverridePrefersLargerSequence()
        {
            var a = new AttributeAggregator();
            a.SetBase(1, Damage, 10f);
            a.AddModifier(1, Mod(Damage, AttributeModifierOp.Override, 2f, 3));
            a.AddModifier(1, Mod(Damage, AttributeModifierOp.Override, 7f, 3));
            Assert.Equal(7f, a.GetComputed(1, Damage), 5);

            a.AddModifier(1, Mod(Damage, AttributeModifierOp.Override, 1f, 3));
            Assert.Equal(1f, a.GetComputed(1, Damage), 5);
        }

        [Fact]
        public void BuffAlliesCompilesToPercentMatchingLegacyMultiplyWhenMaxStacksIsOne()
        {
            const float injectedBonus = 0.30f;
            var catalog = CatalogCompiler.CompileEnemyExtensions(CatalogCompiler.CreateEmpty(),
                new[]
                {
                    new EnemyAbilityDef
                    {
                        Id = "war-cry", Name = "war-cry", AbilityType = "buff_allies",
                        DamageMultiplier = injectedBonus, BuffDuration = 3, AoeRadius = 5, Cooldown = 5f
                    }
                });
            Assert.True(catalog.TryResolveAlias("war-cry", out var abilityId));
            Assert.True(catalog.TryGetAbility(abilityId, out var ability));
            Assert.True(catalog.TryGetEffect(ability.Effects[0], out var effect));
            Assert.Equal(1, effect.MaxStacks);
            var modifier = Assert.Single(effect.Modifiers);
            Assert.Equal(Attack, modifier.Attribute);
            Assert.Equal(AttributeModifierOp.Percent, modifier.Operation);
            Assert.Equal(injectedBonus, modifier.Magnitude, 5);

            const float baseline = 10f;
            var a = new AttributeAggregator();
            a.SetBase(1, Attack, baseline);
            a.AddModifier(1, modifier);
            float expectedLegacy = baseline * (1f + injectedBonus);
            Assert.Equal(expectedLegacy, a.GetComputed(1, Attack), 5);
        }

        [Fact]
        public void SkillSystemAttackBoostAdapterMapsMultiplyToPercent()
        {
            var attackBoost = new GameplayEffectDef("Attack+10%", EffectType.Instant,
                AttributeSetDefinitions.ATTACK_DAMAGE, AttributeModifierOp.Multiply, 1.1f);
            var source = Store.GetEntityHandle(Player());
            var application = LegacyEffectAdapter.CreateApplication(attackBoost, source, source);
            var mapped = Assert.Single(application.Definition.Modifiers);
            Assert.Equal(Attack, mapped.Attribute);
            Assert.Equal(AttributeModifierOp.Percent, mapped.Operation);
            Assert.Equal(0.1f, mapped.Magnitude, 5);
            Assert.Equal(SnapshotPolicy.CaptureOnApply, mapped.Snapshot);

            const float baseline = 10f;
            var a = new AttributeAggregator();
            a.SetBase(1, Attack, baseline);
            a.AddModifier(1, mapped);
            Assert.Equal(baseline * 1.1f, a.GetComputed(1, Attack), 5);
        }

        [Fact]
        public void InitializePlayerSkills_StoresMappedPercentAttackBoost()
        {
            int pid = Player(p => p.AttackDamage = 10f);
            var sys = new SkillSystem(Store, Renderer, pid, Config);
            sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            sys.InitializePlayerSkills();

            bool found = false;
            for (int slot = 0; slot < Store.GetEffectCount(pid); slot++)
            {
                Assert.True(Store.TryGetActiveEffectAt(pid, slot, out _, out var definition, out var snapshot));
                if (!string.Equals(snapshot.Name, "Attack+10%", StringComparison.Ordinal)) continue;
                var mapped = Assert.Single(definition.Modifiers);
                Assert.Equal(AttributeModifierOp.Percent, mapped.Operation);
                Assert.Equal(0.1f, mapped.Magnitude, 5);
                found = true;
            }
            Assert.True(found);
        }

        [Fact]
        public void AddModifier_RejectsResidualMultiplyAtRuntime()
        {
            var a = new AttributeAggregator();
            a.SetBase(1, Damage, 1f);
            var error = Assert.Throws<InvalidOperationException>(
                () => a.AddModifier(1, Mod(Damage, AttributeModifierOp.Multiply, 1.1f)));
            Assert.Contains("Multiply", error.Message, StringComparison.Ordinal);
            Assert.Equal(1f, a.GetComputed(1, Damage), 5);

            using (var store = new ComponentStore())
            {
                store.AddPlayer(0, 10f, 1f, 1f, 1);
                Assert.Throws<InvalidOperationException>(() =>
                    store.AddAttributeModifier(0, new ModifierDefinition(Attack, AttributeModifierOp.Multiply, 2f)));
            }
        }

        [Fact]
        public void StackedPercentUsesStackCountMultiplierNotTwoHandles()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 10f, 1f, 1f, 1);
            int target = store.AddEnemy(0, 0, 1f, 100f, 100f, 1f, 1, 1);
            const float baseline = 1f;
            const float percent = 0.30f;
            store.AttributeAggregator.SetBase(target, Damage, baseline);
            var definition = new GameplayEffectDefinition(new EffectId(960), EffectType.Duration,
                new[] { new ModifierDefinition(Damage, AttributeModifierOp.Percent, percent) },
                8f, 0f, ClockId.Combat, StackingBehavior.MaxStacks, 4, RefreshPolicy.None,
                SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent, default(TagId),
                Array.Empty<ExecutionId>());
            Assert.True(store.GameplayEffectsRuntime.TryApply(definition.Id, definition,
                store.GetEntityHandle(0), store.GetEntityHandle(target), out _, ownerPlayerId: 0));
            Assert.Equal(definition.Modifiers.Count, store.GameplayEffectsRuntime.ModifierHandleCount);
            Assert.True(store.GameplayEffectsRuntime.TryApply(definition.Id, definition,
                store.GetEntityHandle(0), store.GetEntityHandle(target), out _, ownerPlayerId: 0));
            Assert.True(store.TryGetActiveEffectAt(target, 0, out var active, out _, out _));
            Assert.Equal(2, active.StackCount);
            Assert.Equal(definition.Modifiers.Count, store.GameplayEffectsRuntime.ModifierHandleCount);
            Assert.Equal(baseline * (1f + percent * 2f), store.AttributeAggregator.GetComputed(target, Damage, baseline), 5);
        }
    }
}
