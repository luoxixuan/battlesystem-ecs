using System;
using System.Collections.Generic;
using BattleSystemECS.Components;

namespace BattleSystemECS.Core.GAS
{
    internal enum GameplayScheduleKind : byte { EffectExpire, PeriodicTick, AbilityExpire }

    internal struct GameplayScheduleEntry
    {
        public ClockId Clock;
        public GameplayScheduleKind Kind;
        public double DueVirtual;
        public EffectHandle Effect;
        public EntityHandle Target;
        public int EntityId;
        public int AbilitySlot;
    }

    /// <summary>
    /// 排期本：Timed Ability 与诊断 Rebuild 用。effect.tick 热路径不每帧 Sync/Clear（避免 O(E²)+闭包分配）。
    /// CollectDue 仅测试/Rebuild；生产 Tick 对效果走 float RemainingTime / TickAccumulator。
    /// </summary>
    internal sealed class GameplayScheduleBook
    {
        internal const int ClockCount = 5;
        private readonly double[] _virtualNow = new double[ClockCount];
        private readonly List<GameplayScheduleEntry>[] _entries;

        public GameplayScheduleBook()
        {
            _entries = new List<GameplayScheduleEntry>[ClockCount];
            for (int i = 0; i < ClockCount; i++)
                _entries[i] = new List<GameplayScheduleEntry>(64);
        }

        public double VirtualNow(ClockId clock)
        {
            int i = Index(clock);
            return i < 0 ? 0d : _virtualNow[i];
        }

        /// <summary>推进该 clock 的虚拟时间。dt 必须是缩放后的 clock 增量，禁止用帧计数。</summary>
        public void Advance(ClockId clock, float dt)
        {
            int i = Index(clock);
            if (i < 0 || dt == 0f || float.IsNaN(dt)) return;
            _virtualNow[i] += dt;
        }

        public void ClearEffect(EffectHandle handle)
        {
            if (!handle.IsValid) return;
            for (int c = 0; c < ClockCount; c++)
            {
                var list = _entries[c];
                for (int n = list.Count - 1; n >= 0; n--)
                    if (list[n].Effect.Equals(handle)) list.RemoveAt(n);
            }
        }

        public void ClearAbility(int entityId, int slot)
        {
            for (int c = 0; c < ClockCount; c++)
            {
                var list = _entries[c];
                for (int n = list.Count - 1; n >= 0; n--)
                {
                    var e = list[n];
                    if (e.Kind == GameplayScheduleKind.AbilityExpire && e.EntityId == entityId && e.AbilitySlot == slot)
                        list.RemoveAt(n);
                }
            }
        }

        public void UpsertEffectExpire(ClockId clock, EffectHandle handle, EntityHandle target, double due)
        {
            Upsert(clock, GameplayScheduleKind.EffectExpire, handle, target, due, -1, -1);
        }

        public void UpsertPeriodic(ClockId clock, EffectHandle handle, EntityHandle target, double due)
        {
            Upsert(clock, GameplayScheduleKind.PeriodicTick, handle, target, due, -1, -1);
        }

        public void UpsertAbility(ClockId clock, int entityId, int slot, double due)
        {
            int i = Index(clock);
            if (i < 0) return;
            var list = _entries[i];
            for (int n = 0; n < list.Count; n++)
            {
                var e = list[n];
                if (e.Kind != GameplayScheduleKind.AbilityExpire || e.EntityId != entityId || e.AbilitySlot != slot) continue;
                e.DueVirtual = due;
                e.Clock = clock;
                list[n] = e;
                return;
            }
            list.Add(new GameplayScheduleEntry
            {
                Clock = clock,
                Kind = GameplayScheduleKind.AbilityExpire,
                DueVirtual = due,
                EntityId = entityId,
                AbilitySlot = slot
            });
        }

        public int CollectDue(ClockId clock, GameplayScheduleEntry[] buffer)
        {
            int i = Index(clock);
            if (i < 0 || buffer == null || buffer.Length == 0) return 0;
            double now = _virtualNow[i];
            var list = _entries[i];
            int written = 0;
            for (int n = 0; n < list.Count && written < buffer.Length; n++)
            {
                if (list[n].DueVirtual <= now)
                    buffer[written++] = list[n];
            }
            return written;
        }

        internal int DebugCount(ClockId clock)
        {
            int i = Index(clock);
            return i < 0 ? 0 : _entries[i].Count;
        }

        /// <summary>从 ActiveEffect 全量重建该 clock 的效果条目；虚拟时钟本身不重置。生产 Tick 不调用。</summary>
        public void RebuildEffects(ComponentStore store, IReadOnlyList<int> runtimeEntityIds, ClockId clock)
        {
            int i = Index(clock);
            if (i < 0 || store == null) return;
            var list = _entries[i];
            for (int n = list.Count - 1; n >= 0; n--)
                if (list[n].Kind != GameplayScheduleKind.AbilityExpire)
                    list.RemoveAt(n);
            if (runtimeEntityIds == null) return;
            for (int e = 0; e < runtimeEntityIds.Count; e++)
            {
                int entityId = runtimeEntityIds[e];
                int count = store.GetEffectCount(entityId);
                for (int slot = 0; slot < count; slot++)
                {
                    if (!store.TryGetActiveEffectAt(entityId, slot, out var active, out var definition, out _)) continue;
                    if (!active.RuntimeOwned || active.Clock != clock) continue;
                    if (definition.DurationPolicy != DurationPolicy.Infinite)
                        list.Add(new GameplayScheduleEntry
                        {
                            Clock = clock,
                            Kind = GameplayScheduleKind.EffectExpire,
                            DueVirtual = active.ExpireAtVirtual,
                            Effect = active.Handle,
                            Target = active.Target
                        });
                    if (definition.Type == EffectType.Periodic && active.TicksRemaining > 0)
                        list.Add(new GameplayScheduleEntry
                        {
                            Clock = clock,
                            Kind = GameplayScheduleKind.PeriodicTick,
                            DueVirtual = active.NextTickAtVirtual,
                            Effect = active.Handle,
                            Target = active.Target
                        });
                }
            }
        }

        private void Upsert(ClockId clock, GameplayScheduleKind kind, EffectHandle handle, EntityHandle target, double due,
            int entityId, int slot)
        {
            int i = Index(clock);
            if (i < 0 || !handle.IsValid) return;
            var list = _entries[i];
            for (int n = 0; n < list.Count; n++)
            {
                var e = list[n];
                if (e.Kind != kind || !e.Effect.Equals(handle)) continue;
                e.DueVirtual = due;
                e.Target = target;
                e.Clock = clock;
                list[n] = e;
                return;
            }
            list.Add(new GameplayScheduleEntry
            {
                Clock = clock,
                Kind = kind,
                DueVirtual = due,
                Effect = handle,
                Target = target,
                EntityId = entityId,
                AbilitySlot = slot
            });
        }

        internal static int Index(ClockId clock)
        {
            int v = (int)clock;
            return (uint)v < ClockCount ? v : -1;
        }
    }
}
