using System;
using Xunit;
using BattleSystemECS.Core.GAS;

namespace BattleSystemECS.Tests.Framework
{
    public class GameplayAbilityTests
    {
        private AbilityInstance Make(float cooldown)
        {
            var def = new GameplayAbilityDef("Test", "desc", 5f, 0f, -1, 10f,
                AbilityActivation.Instant, 0, 0);
            var inst = new AbilityInstance(def);
            inst.CurrentCooldown = cooldown;
            return inst;
        }

        // ─── Bug#37: CanActivate epsilon 边界 ──────────────────────────────────

        [Fact] public void CanActivate_TrueWhenCooldownZero()
            => Assert.True(Make(0f).CanActivate());

        [Fact] public void CanActivate_TrueWhenCooldownBelowEpsilon()
            => Assert.True(Make(0.00005f).CanActivate());

        [Fact] public void CanActivate_FalseWhenCooldownAboveEpsilon()
            => Assert.False(Make(0.001f).CanActivate());

        [Fact] public void CanActivate_TrueWhenCooldownAtOrBelowEpsilon()
            => Assert.True(Make(0.0001f).CanActivate());

        [Fact] public void Activate_SetsCooldownToDefinitionValue()
        {
            var def = new GameplayAbilityDef("Test", "desc", 5f, 0f, -1, 10f,
                AbilityActivation.Instant, 0, 0);
            var inst = new AbilityInstance(def);
            inst.Activate();
            Assert.Equal(5f, inst.CurrentCooldown);
        }

        [Fact] public void AbilityInstance_CooldownMutability()
        {
            var def = new GameplayAbilityDef("Test", "desc", 5f, 0f, -1, 10f,
                AbilityActivation.Instant, 0, 0);
            var inst = new AbilityInstance(def);
            Assert.Equal(0f, inst.CurrentCooldown);
            inst.CurrentCooldown = 3.5f;
            Assert.Equal(3.5f, inst.CurrentCooldown);
        }
    }
}
