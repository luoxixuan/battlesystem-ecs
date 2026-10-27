using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    /// <summary>
    /// 帧末容量丢弃通知：Resolver 请求溢出等丢弃在发生的那一帧末报告增量，无新增时不报告。
    /// </summary>
    public sealed class CapacityDropReportTests
    {
        [Fact]
        public void DamageRequestOverflowIsReportedOnceAtFrameEnd()
        {
            using var store = new ComponentStore();
            store.AddPlayer(0, 10f, 1f, 1f, 1);
            int enemy = store.AddEnemy(0f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            var scheduler = new FrameScheduler(store, new Config.GameConfig());
            scheduler.SealGraphComposition();
            var reports = new List<CapacityDropReport>();
            scheduler.CapacityDropped += reports.Add;

            scheduler.Tick(0f, 0);
            Assert.Empty(reports);

            // 延迟队列填满后再多提交 2 个请求：2 次 RequestQueueOverflow 拒绝
            store.DamageResolver.EnableDeferred(true);
            var source = store.GetEntityHandle(0);
            var target = store.GetEntityHandle(enemy);
            int overflow = 2;
            for (int i = 0; i < DamageResolver.MaxPendingRequests + overflow; i++)
                store.DamageResolver.TryApply(new DamageRequest(source, target, 1f, DamageType.True,
                    ElementType.None, DamageFlags.None, DamageAmountStage.Raw, DamageCommitBoundary.GameplayResolve,
                    10L + i, ownerPlayerId: 0));

            scheduler.Tick(0f, 1);
            var report = Assert.Single(reports);
            Assert.Equal(overflow, report.DamageDropped);
            Assert.Equal(0, report.ResourceDropped);

            scheduler.Tick(0f, 2);
            Assert.Single(reports);
        }
    }
}
