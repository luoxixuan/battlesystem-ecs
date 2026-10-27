using System;
using System.Runtime.CompilerServices;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    /// <summary>
    /// 塔失效通知是 store 实例事件：订阅系统不会被静态事件永久根引用（旧实现每个 TowerSynergySystem
    /// 连同其 ~250 MB 的 store 泄漏），且一个 store 的塔销毁不会通知其他 store 的订阅者。
    /// </summary>
    public sealed class TowerInvalidationEventScopeTests
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static (WeakReference system, WeakReference store) CreateDroppedSynergySystem()
        {
            var store = new ComponentStore();
            var system = new TowerSynergySystem(store, new MockRenderer());
            return (new WeakReference(system), new WeakReference(store));
        }

        [Fact]
        public void DroppedSynergySystemAndStoreAreCollectable()
        {
            var (system, store) = CreateDroppedSynergySystem();
            for (int i = 0; i < 3 && (system.IsAlive || store.IsAlive); i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            Assert.False(system.IsAlive);
            Assert.False(store.IsAlive);
        }

        [Fact]
        public void TowerInvalidationOnlyNotifiesOwningStore()
        {
            using var a = new TestWorld();
            using var b = new TestWorld();
            int notifiedA = 0, notifiedB = 0;
            a.Store.OnTowerEntityInvalidated += _ => notifiedA++;
            b.Store.OnTowerEntityInvalidated += _ => notifiedB++;

            int tower = a.RawTower(2, 2);
            int beforeA = notifiedA;
            a.Store.DestroyEntity(tower);

            Assert.True(notifiedA > beforeA);
            Assert.Equal(0, notifiedB);
        }
    }
}
