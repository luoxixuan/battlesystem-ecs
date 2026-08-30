using System;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class EntityHandleTests
    {
        [Fact]
        public void EntityHandle_IsRejectedAfterSlotRecycle()
        {
            var store = new ComponentStore();
            int first = store.AddEnemy(0, 0, 1, 10, 10, 1, 1, 1);
            EntityHandle oldHandle = store.GetEntityHandle(first);
            store.DestroyEntity(first);
            int second = store.AddEnemy(0, 0, 1, 10, 10, 1, 1, 1);
            Assert.Equal(first, second);
            Assert.False(store.TryResolve(oldHandle, out _, out var reason));
            Assert.Equal(HandleResolveFailure.StaleGeneration, reason);
        }

    }
}
