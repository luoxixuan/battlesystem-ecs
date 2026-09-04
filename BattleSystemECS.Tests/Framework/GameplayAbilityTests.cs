using System;
using Xunit;
using BattleSystemECS.Tests.Infrastructure;
using BattleSystemECS.Core.GAS;

namespace BattleSystemECS.Tests.Framework
{
    public class GameplayAbilityTests : BattleTestBase
    {
        private AbilityInstance Make(float cooldown)
        {
            var def = new GameplayAbilityDef("Test", "desc", 5f, 0f, -1, 10f,
                AbilityActivation.Instant, 0, 0);
            var inst = new AbilityInstance(def);
            inst.CurrentCooldown = cooldown;
            Assert.Equal(cooldown, inst.State.Cooldown);
            Assert.Equal(1, inst.State.MaxCharges);
            return inst;
        }

        // ─── Bug#37 回归：CanActivate epsilon 边界 ───────────────────────────────
        // 四个原本几乎相同的 [Fact] 合并为单个 [Theory]。
        [Theory(DisplayName = "CanActivate 冷却 epsilon 边界")]
        [InlineData(0f, true)]
        [InlineData(0.00005f, true)]
        [InlineData(0.0001f, true)]
        [InlineData(0.001f, false)]
        public void CanActivate_RespectsEpsilonBoundary(float cooldown, bool expected)
        {
            Assert.Equal(expected, Make(cooldown).CanActivate());
        }

        [Fact]
        public void Activate_SetsCooldownToDefinitionValue()
        {
            var def = new GameplayAbilityDef("Test", "desc", 5f, 0f, -1, 10f,
                AbilityActivation.Instant, 0, 0);
            var inst = new AbilityInstance(def);
            inst.Activate();
            // 期望值直接取注入的 def.Cooldown，不重复钉住 5f 字面量。
            Assert.Equal(def.Cooldown, inst.CurrentCooldown);
            Assert.Equal(AbilityPhase.None, inst.State.Phase);
        }

        [Fact]
        public void AbilityInstance_CooldownMutability()
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
