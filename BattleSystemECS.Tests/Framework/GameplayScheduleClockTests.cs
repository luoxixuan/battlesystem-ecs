using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class GameplayScheduleClockTests
    {
        [Fact]
        public void CombatAndEnemyClocksExpireByVirtualTimeNotFrameCount()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 1f, 1f, 1f, 1);
            int source = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            int combatTarget = store.AddEnemy(1, 0, 1f, 100f, 100f, 1f, 1, 1);
            int enemyTarget = store.AddEnemy(2, 0, 1f, 100f, 100f, 1f, 1, 1);
            var combat = Periodic(40, ClockId.Combat, period: 1f, duration: 10f, magnitude: 1f);
            var enemy = Periodic(41, ClockId.Enemy, period: 1f, duration: 10f, magnitude: 1f);
            Assert.True(store.GameplayEffectsRuntime.TryApply(combat.Id, combat,
                store.GetEntityHandle(source), store.GetEntityHandle(combatTarget), out _, ownerPlayerId: 0));
            Assert.True(store.GameplayEffectsRuntime.TryApply(enemy.Id, enemy,
                store.GetEntityHandle(source), store.GetEntityHandle(enemyTarget), out _, ownerPlayerId: 0));

            const int frames = 4;
            for (int i = 0; i < frames; i++)
            {
                store.GameplayEffectsRuntime.Tick(1f, ClockId.Combat);
                store.GameplayEffectsRuntime.Tick(0.25f, ClockId.Enemy);
            }

            Assert.Equal(4d, store.GameplayEffectsRuntime.VirtualNow(ClockId.Combat), 6);
            Assert.Equal(1d, store.GameplayEffectsRuntime.VirtualNow(ClockId.Enemy), 6);
            Assert.Equal(96f, store.EnemyHealth[combatTarget], 3);
            Assert.Equal(99f, store.EnemyHealth[enemyTarget], 3);
            Assert.True(store.TryGetActiveEffectAt(combatTarget, 0, out var combatActive, out _, out _));
            Assert.True(store.TryGetActiveEffectAt(enemyTarget, 0, out var enemyActive, out _, out _));
            Assert.Equal(4, combatActive.TicksProcessed);
            Assert.Equal(1, enemyActive.TicksProcessed);
        }

        [Fact]
        public void RebuildSchedulePreservesDueTimesFromActiveEffects()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, 1f, 1f, 1f, 1);
            int source = store.AddEnemy(0, 0, 1f, 10f, 10f, 1f, 1, 1);
            int target = store.AddEnemy(1, 0, 1f, 20f, 20f, 1f, 1, 1);
            var def = new GameplayEffectDefinition(new EffectId(42), EffectType.Duration,
                Array.Empty<ModifierDefinition>(), 2f, 0f, ClockId.Combat, StackingBehavior.None, 1,
                RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent,
                default(TagId), Array.Empty<ExecutionId>());
            Assert.True(store.GameplayEffectsRuntime.TryApply(def.Id, def,
                store.GetEntityHandle(source), store.GetEntityHandle(target), out _, ownerPlayerId: 0));
            store.GameplayEffectsRuntime.Tick(0.5f, ClockId.Combat);
            Assert.True(store.TryGetActiveEffectAt(target, 0, out var before, out _, out _));
            Assert.Equal(1.5f, before.RemainingTime, 3);
            store.GameplayEffectsRuntime.RebuildSchedule(ClockId.Combat);
            store.GameplayEffectsRuntime.Tick(1.5f, ClockId.Combat);
            Assert.Equal(0, store.GetEffectCount(target));
        }

        private static GameplayEffectDefinition Periodic(int id, ClockId clock, float period, float duration, float magnitude)
        {
            var spec = new PeriodicSpec(period, FirstTickPolicy.NextInterval, CatchUpPolicy.CatchUpAll,
                default(ExecutionId), DamageType.True, ElementType.None, magnitude);
            return new GameplayEffectDefinition(new EffectId(id), EffectType.Periodic,
                Array.Empty<ModifierDefinition>(), duration, clock, StackingBehavior.None, 1,
                RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.Damage, default(TagId),
                spec, Array.Empty<ExecutionId>());
        }
    }
}
