using System;
using BattleSystemECS.Core.GAS;

namespace BattleSystemECS.Core
{
    /// <summary>
    /// 单帧容量丢弃报告：相对上一帧新增的 Resolver 请求溢出拒绝、事件队列溢出与触发器循环防护中止。
    /// 只在至少一项 > 0 时由 <see cref="FrameScheduler.CapacityDropped"/> 发出。
    /// </summary>
    public readonly struct CapacityDropReport
    {
        public readonly int Frame;
        public readonly long DamageDropped;
        public readonly long ResourceDropped;
        public readonly long DamageEventOverflows;
        public readonly long ResourceEventOverflows;
        public readonly long TriggerAborts;

        public CapacityDropReport(int frame, long damageDropped, long resourceDropped,
            long damageEventOverflows, long resourceEventOverflows, long triggerAborts)
        {
            Frame = frame;
            DamageDropped = damageDropped;
            ResourceDropped = resourceDropped;
            DamageEventOverflows = damageEventOverflows;
            ResourceEventOverflows = resourceEventOverflows;
            TriggerAborts = triggerAborts;
        }

        public override string ToString() =>
            $"damageDropped={DamageDropped} resourceDropped={ResourceDropped} damageEventOverflow={DamageEventOverflows} " +
            $"resourceEventOverflow={ResourceEventOverflows} triggerAborts={TriggerAborts}";
    }

    /// <summary>
    /// 每帧末读取 store 上的累计容量计数并与上一帧比较（几次字段读取，无分配）。
    /// 计数器在 store 生命周期内单调递增，首帧以构造时的值为基线。
    /// </summary>
    internal sealed class CapacityDropWatch
    {
        private readonly ComponentStore _store;
        private long _damageDropped, _resourceDropped, _damageEventOverflows, _resourceEventOverflows, _triggerAborts;

        public CapacityDropWatch(ComponentStore store)
        {
            _store = store;
            Read(out _damageDropped, out _resourceDropped, out _damageEventOverflows, out _resourceEventOverflows, out _triggerAborts);
        }

        /// <summary>有新增丢弃时返回 true 并给出本帧增量。</summary>
        public bool TryCollect(int frame, out CapacityDropReport report)
        {
            Read(out long damage, out long resource, out long damageEvents, out long resourceEvents, out long aborts);
            long dDamage = damage - _damageDropped, dResource = resource - _resourceDropped;
            long dDamageEvents = damageEvents - _damageEventOverflows, dResourceEvents = resourceEvents - _resourceEventOverflows;
            long dAborts = aborts - _triggerAborts;
            _damageDropped = damage; _resourceDropped = resource;
            _damageEventOverflows = damageEvents; _resourceEventOverflows = resourceEvents; _triggerAborts = aborts;
            if (dDamage <= 0 && dResource <= 0 && dDamageEvents <= 0 && dResourceEvents <= 0 && dAborts <= 0)
            {
                report = default;
                return false;
            }
            report = new CapacityDropReport(frame, Math.Max(0, dDamage), Math.Max(0, dResource),
                Math.Max(0, dDamageEvents), Math.Max(0, dResourceEvents), Math.Max(0, dAborts));
            return true;
        }

        private void Read(out long damage, out long resource, out long damageEvents, out long resourceEvents, out long aborts)
        {
            damage = _store.DamageResolver.GetRejectionCount(DamageRejectionReason.RequestQueueOverflow);
            resource = _store.ResourceResolver.GetRejectionCount(ResourceRejectionReason.RequestQueueOverflow);
            damageEvents = _store.DamageResolver.EventOverflowCount;
            resourceEvents = _store.ResourceResolver.EventOverflowCount;
            aborts = _store.GameplayTriggersRuntime.LoopAborts;
        }
    }
}
