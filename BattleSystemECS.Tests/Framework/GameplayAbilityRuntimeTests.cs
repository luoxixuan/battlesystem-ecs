using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class GameplayAbilityRuntimeTests
    {
        [Fact]
        public void TryActivateDoesNotMutateUntilAbilityCommit()
        {
            var store = new ComponentStore();
            int player = 0;
            store.AddPlayer(player, 10f, 1f, 1f, 1);
            var def = new GameplayAbilityDef("runtime", "", 3f, 0f, -1, 1f, AbilityActivation.Instant, AreaShapeType.Single, 1);
            Assert.True(store.TryAddAbility(player, def));

            Assert.True(GameplayAbilityRuntime.TryActivate(store, player, 0, out var pending));
            Assert.Equal(0f, pending.CurrentCooldown);
            Assert.Equal(0f, store.GetAbility(player, 0).CurrentCooldown);
            Assert.True(GameplayAbilityRuntime.AbilityCommit(store, player, 0));
            Assert.Equal(def.Cooldown, store.GetAbility(player, 0).CurrentCooldown);
        }

        [Fact]
        public void AbilityCommitRejectsCooldownSlot()
        {
            var store = new ComponentStore();
            int player = 0;
            store.AddPlayer(player, 10f, 1f, 1f, 1);
            var def = new GameplayAbilityDef("runtime", "", 3f, 0f, -1, 1f, AbilityActivation.Instant, AreaShapeType.Single, 1);
            Assert.True(store.TryAddAbility(player, def));
            Assert.True(GameplayAbilityRuntime.AbilityCommit(store, player, 0));
            Assert.False(GameplayAbilityRuntime.AbilityCommit(store, player, 0));
            Assert.Equal(def.Cooldown, store.GetAbility(player, 0).CurrentCooldown);
        }
    }
}
