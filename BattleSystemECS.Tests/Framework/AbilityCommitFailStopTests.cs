using System;
using System.Collections.Generic;
using System.Reflection;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Content.Contracts;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Systems;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    /// <summary>
    /// P2 / F12：预校验通过后的运行时竞争改为拒绝事实，不再 throw prevalidated/during commit。
    /// </summary>
    public sealed class AbilityCommitFailStopTests : BattleTestBase
    {
        [Fact]
        public void PlayerDamagePublishFailure_CancelsWithoutThrow_HealthUnchanged()
        {
            int player = Player(p => { p.Health = 80f; });
            Store.GameplayPhaseContext = new PhaseContext(PhaseContextKind.Wave);
            Store.PlayerMaxHealth[player] = 80f;
            int enemy = Enemy(e => { e.X = 0f; e.Y = 0f; e.Damage = 10f; });
            var catalog = DamageToPlayerCatalog();
            var handler = new EnemyAbilitySystem(Store, Renderer, player, new GameConfig { CompiledCatalog = catalog });
            Store.DeferAbilityAndEffectCommit = true;
            var request = new AbilityActivationRequest(enemy, 0, 0f, player, new AbilityId(0),
                magnitudeOverride: 5f, ownerPlayerId: player);
            Assert.True(GameplayAbilityRuntime.Activate(Store, catalog, new float[1], request, handler).Accepted);
            FillEventQueue(Store.ResourceResolver.Events);
            float health = Store.PlayerCurrentHealth[player];

            AbilityActivationResult committed = default;
            Exception? thrown = Record.Exception(() =>
                committed = GameplayAbilityRuntime.CommitQueuedAbilities(Store));

            Assert.Null(thrown);
            Assert.False(committed.Accepted);
            Assert.Equal(AbilityActivationRejectReason.QueueOverflow, committed.Reason);
            Assert.Equal(health, Store.PlayerCurrentHealth[player]);
            Assert.DoesNotContain("prevalidated", thrown?.Message ?? string.Empty, StringComparison.Ordinal);
        }

        [Fact]
        public void PlayerDamageTargetInvalidBeforeCommit_CancelsAsNoTarget()
        {
            int player = Player(p => { p.Health = 80f; });
            Store.GameplayPhaseContext = new PhaseContext(PhaseContextKind.Wave);
            Store.PlayerMaxHealth[player] = 80f;
            int enemy = Enemy(e => { e.X = 0f; e.Y = 0f; e.Damage = 10f; });
            var catalog = DamageToPlayerCatalog();
            var handler = new EnemyAbilitySystem(Store, Renderer, player, new GameConfig { CompiledCatalog = catalog });
            Store.DeferAbilityAndEffectCommit = true;
            var request = new AbilityActivationRequest(enemy, 0, 0f, player, new AbilityId(0),
                magnitudeOverride: 5f, ownerPlayerId: player);
            Assert.True(GameplayAbilityRuntime.Activate(Store, catalog, new float[1], request, handler).Accepted);
            Store.PlayerCurrentHealth[player] = 0f;

            AbilityActivationResult committed = default;
            Exception? thrown = Record.Exception(() =>
                committed = GameplayAbilityRuntime.CommitQueuedAbilities(Store));

            Assert.Null(thrown);
            Assert.False(committed.Accepted);
            Assert.Equal(AbilityActivationRejectReason.NoTarget, committed.Reason);
            Assert.Equal(0f, Store.PlayerCurrentHealth[player]);
        }

        [Fact]
        public void TelegraphQueueFullAfterEnqueue_SkipsWithoutThrow()
        {
            var ability = new EnemyAbilityDef
            {
                Id = "warn", Name = "warn", AbilityType = "aoe_damage", Cooldown = 5f,
                DamageMultiplier = 1f, AoeRadius = 4, TelegraphDuration = 2f, TelegraphColor = 0
            };
            var config = EnemyConfig(ability);
            int player = Player(p => { p.X = 0f; p.Y = 0f; p.Health = 100f; });
            int enemy = Enemy(e => { e.X = 1f; e.Y = 0f; e.Damage = 8f; });
            var system = new EnemyAbilitySystem(Store, Renderer, player, config);
            var telegraph = new TelegraphSystem(Store, Renderer, config, new EventBus());
            system.SetTelegraphSystem(telegraph);
            system.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            Store.DeferAbilityAndEffectCommit = true;
            system.EnqueueAbility(enemy, ability.Id);
            system.ExecuteAbilities();
            Assert.Equal(1, Store.AbilityRequests.Count);
            var source = Store.GetEntityHandle(enemy);
            var target = Store.GetEntityHandle(player);
            for (int i = 0; i < TelegraphSystem.MAX_TELEGRAPH_ZONES; i++)
                Assert.True(telegraph.TryQueueTelegraphZone(source, target, 0f, 0f, 1f, 1f, 1f,
                    default, player, TelegraphShape.Circle));
            Assert.False(telegraph.CanQueueTelegraphZone(1f));
            int zones = CountActiveTelegraphs(telegraph);

            Exception? thrown = Record.Exception(() => GameplayAbilityRuntime.CommitQueuedAbilities(Store));

            Assert.Null(thrown);
            Assert.Equal(zones, CountActiveTelegraphs(telegraph));
        }

        [Fact]
        public void SummonCreateEntityFailureAfterEnqueue_SkipsWithoutThrow()
        {
            var ability = new EnemyAbilityDef
            {
                Id = "summon", Name = "summon", AbilityType = "summon_minion", Cooldown = 5f,
                MinionHealthMult = 0.5f, MinionDamageMult = 0.25f
            };
            var config = EnemyConfig(ability);
            int player = Player();
            int summoner = Enemy(e => { e.Health = 100f; e.MaxHealth = 100f; e.Damage = 20f; });
            var system = new EnemyAbilitySystem(Store, Renderer, player, config);
            system.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            Store.DeferAbilityAndEffectCommit = true;
            system.EnqueueAbility(summoner, ability.Id);
            system.ExecuteAbilities();
            Assert.Equal(1, Store.AbilityRequests.Count);
            int activeBefore = Store.ActiveEnemyIds.Count;
            ExhaustEntityPool();

            Exception? thrown = Record.Exception(() => GameplayAbilityRuntime.CommitQueuedAbilities(Store));

            Assert.Null(thrown);
            Assert.Equal(activeBefore, Store.ActiveEnemyIds.Count);
            Assert.Equal(-1, Store.CreateEntity());
        }

        [Fact]
        public void DispelRemoveFailureAfterEnqueue_SkipsSlotWithoutThrow()
        {
            var ability = new EnemyAbilityDef
            {
                Id = "purge", Name = "purge", AbilityType = "dispel_tower", DispelRadius = 5f, Cooldown = 5f
            };
            var config = EnemyConfig(ability);
            int player = Player(p => { p.X = 50f; p.Y = 50f; });
            int source = Enemy(e => { e.X = 0f; e.Y = 0f; e.MoveSpeed = 0f; });
            int tower = RawTower(1, 0);
            var system = new EnemyAbilitySystem(Store, Renderer, player, config);
            system.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            var removable = new GameplayEffectDefinition(new EffectId(950), EffectType.Duration,
                Array.Empty<ModifierDefinition>(), 20f, 0f, ClockId.Enemy, StackingBehavior.None, 1,
                RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.Status,
                CatalogRegistries.SkillTag, Array.Empty<ExecutionId>(),
                grantedTags: new[] { CatalogRegistries.DispellableTag });
            Assert.True(Store.GameplayEffectsRuntime.TryApply(removable.Id, removable,
                Store.GetEntityHandle(source), Store.GetEntityHandle(tower), out var handle));
            Store.DeferAbilityAndEffectCommit = true;
            system.EnqueueAbility(source, ability.Id);
            system.ExecuteAbilities();
            Assert.Equal(1, Store.AbilityRequests.Count);
            Assert.True(Store.GameplayEffectsRuntime.Remove(Store.GetEntityHandle(tower), handle));
            Assert.Equal(0, Store.GetEffectCount(tower));

            Exception? thrown = Record.Exception(() => GameplayAbilityRuntime.CommitQueuedAbilities(Store));

            Assert.Null(thrown);
            Assert.Equal(0, Store.GetEffectCount(tower));
        }

        [Fact]
        public void MassResurrectCapacityExhausted_KeepsSuccessfulMinionsWithoutThrow()
        {
            Config.MonsterTypes.Add(new MonsterConfig
            {
                Name = "TestSkeleton", Type = "TestSkeleton", Health = 100f, Damage = 5f, MoveSpeed = 1f,
                GoldReward = 1
            });
            int player = Player(p => { p.X = 0f; p.Y = 0f; });
            Store.NecromancerQueueCorpse(-1, 1f, 0f, "TestSkeleton", 1f, 0f);
            Store.NecromancerQueueCorpse(-1, 2f, 0f, "TestSkeleton", 1f, 0f);
            var sys = new NecromancerSystem(Store, Config, Renderer);
            sys.SetTurn(0, 0f);
            Assert.True(sys.CanMassResurrect(0f, 0f, 4f));
            LeaveEntitySlots(1);
            int activeBefore = Store.ActiveEnemyIds.Count;

            int revived = 0;
            Exception? thrown = Record.Exception(() =>
                revived = sys.MassResurrect(player, 0f, 0f, 4f, 0.3f));

            Assert.Null(thrown);
            Assert.Equal(1, revived);
            Assert.Equal(activeBefore + 1, Store.ActiveEnemyIds.Count);
            int reanimated = 0;
            for (int i = 0; i < ComponentStore.MAX_CORPSE_QUEUE; i++)
                if (Store.CorpseActive[i] && Store.CorpseReanimated[i]) reanimated++;
            Assert.Equal(1, reanimated);
            int leftover = 0;
            for (int i = 0; i < ComponentStore.MAX_CORPSE_QUEUE; i++)
                if (Store.CorpseActive[i] && !Store.CorpseReanimated[i] && Store.CorpseOwnerId[i] < 0) leftover++;
            Assert.Equal(1, leftover);
        }

        [Fact]
        public void TimeRewindResourceWriteFailure_RejectsRestoreWithoutThrow()
        {
            int player = Player(p => { p.Health = 80f; });
            Store.PlayerMaxHealth[player] = 100f;
            Store.PlayerCurrentHealth[player] = 80f;
            Store.PlayerMana[player] = 70f;
            Store.PlayerShield[player] = 60f;
            var sys = new TimeRewindSnapshotSystem(Store);
            sys.AppendSnapshot(player);
            Store.PlayerCurrentHealth[player] = 10f;
            Store.PlayerMana[player] = 20f;
            Store.PlayerShield[player] = 30f;
            Store.ResourceResolver.EnableDeferred(true);
            var handle = Store.GetEntityHandle(player);
            for (int i = 0; i < ResourceResolver.MaxPendingRequests - 2; i++)
                Assert.True(Store.ResourceResolver.TryApply(new ResourceRequest(handle, handle,
                    new AttributeKey(4), 1f, i + 1, ownerPlayerId: player)).Accepted);

            float restored = 0f;
            Exception? thrown = Record.Exception(() => restored = sys.RestoreFromSnapshot(player, 0.25f));

            Assert.Null(thrown);
            Assert.Equal(-1f, restored);
            Assert.Equal(10f, Store.PlayerCurrentHealth[player]);
            Assert.Equal(20f, Store.PlayerMana[player]);
            Assert.Equal(30f, Store.PlayerShield[player]);
        }

        [Fact]
        public void ProductionHandlerMissingSnapshotAfterEnqueue_CancelsRestoreWithoutThrow()
        {
            int player = Player(p => { p.Health = 80f; });
            Store.GameplayPhaseContext = new PhaseContext(PhaseContextKind.Wave);
            Store.PlayerCurrentHealth[player] = 10f;
            Store.PlayerMana[player] = 20f;
            Store.PlayerShield[player] = 30f;
            var restore = new StubRestore { Samples = 1, RestoreResult = 1f };
            var handler = new ProductionAbilityPayloadHandler(Store, new StubResurrect(), restore);
            var catalog = RestoreCatalog();
            Store.DeferAbilityAndEffectCommit = true;
            var request = new AbilityActivationRequest(player, 0, 0f, player, new AbilityId(0), ownerPlayerId: player);
            Assert.True(GameplayAbilityRuntime.Activate(Store, catalog, new float[1], request, handler).Accepted);
            restore.Samples = 0;
            restore.RestoreResult = -1f;

            AbilityActivationResult committed = default;
            Exception? thrown = Record.Exception(() =>
                committed = GameplayAbilityRuntime.CommitQueuedAbilities(Store));

            Assert.Null(thrown);
            Assert.False(committed.Accepted);
            Assert.Equal(AbilityActivationRejectReason.UnsupportedDefinition, committed.Reason);
            Assert.Equal(10f, Store.PlayerCurrentHealth[player]);
            Assert.Equal(20f, Store.PlayerMana[player]);
            Assert.Equal(30f, Store.PlayerShield[player]);
            Assert.Contains(Enumerable.Range(0, Store.DamageResolver.Events.Count),
                i => Store.DamageResolver.Events.Get(i).Type == GameplayEventType.AbilityCancelled);
        }

        [Fact]
        public void OldPrevalidatedCommitStringsAreGoneFromProductionSources()
        {
            string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            foreach (var relative in new[] { "Core", "Systems" })
            {
                foreach (var file in System.IO.Directory.GetFiles(System.IO.Path.Combine(root, relative), "*.cs",
                             System.IO.SearchOption.AllDirectories))
                {
                    string text = System.IO.File.ReadAllText(file);
                    bool hasPrevalidated = text.IndexOf("prevalidated", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool hasDuringCommit = text.IndexOf("during commit", StringComparison.OrdinalIgnoreCase) >= 0;
                    Assert.False(hasPrevalidated && hasDuringCommit, file);
                }
            }
        }

        private static GameplayCatalog DamageToPlayerCatalog()
        {
            var targeting = new TargetingDefinition(new TargetingId(0), TargetingShape.Single, 10, 1, 1, 1);
            var execution = new ExecutionDefinition(new ExecutionId(0), EffectPayloadKind.Damage, 5f, new TagId(0),
                operation: ExecutionOperation.ApplyDamage);
            var ability = new AbilityDefinition(new AbilityId(0), "hit", targeting, ClockId.Combat, 1f,
                GameplayPhaseMask.Wave, Array.Empty<EffectId>(), Array.Empty<ModifierDefinition>(),
                CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer, executions: new[] { execution.Id });
            return new GameplayCatalog(new[] { ability }, new[] { targeting }, Array.Empty<GameplayEffectDefinition>(),
                new[] { execution }, Array.Empty<TriggerDefinition>(), Array.Empty<ModifierDefinition>(),
                new Dictionary<string, AbilityId> { ["hit"] = ability.Id });
        }

        private static GameplayCatalog RestoreCatalog()
        {
            var targeting = new TargetingDefinition(new TargetingId(0), TargetingShape.TimeRewind, 10, 1, 1, 1);
            var execution = new ExecutionDefinition(new ExecutionId(0), EffectPayloadKind.Resource, 3f, new TagId(0),
                operation: ExecutionOperation.RestoreSnapshot);
            var ability = new AbilityDefinition(new AbilityId(0), "rewind", targeting, ClockId.Combat, 1f,
                GameplayPhaseMask.Wave, Array.Empty<EffectId>(), Array.Empty<ModifierDefinition>(),
                CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer, executions: new[] { execution.Id });
            return new GameplayCatalog(new[] { ability }, new[] { targeting }, Array.Empty<GameplayEffectDefinition>(),
                new[] { execution }, Array.Empty<TriggerDefinition>(), Array.Empty<ModifierDefinition>(),
                new Dictionary<string, AbilityId> { ["rewind"] = ability.Id });
        }

        private static GameConfig EnemyConfig(EnemyAbilityDef ability)
        {
            var config = new GameConfig
            {
                StrictCatalogReferences = true,
                EnemyAbilities = new List<EnemyAbilityDef> { ability }
            };
            config.CompiledCatalog = CatalogCompiler.CompileEnemyExtensions(CatalogCompiler.CreateEmpty(),
                config.EnemyAbilities);
            return config;
        }

        private static void FillEventQueue(GameplayEventQueue queue)
        {
            var filler = new GameplayEvent(GameplayEventType.HitConfirmed, default, default, 1L);
            while (queue.TryPublish(filler, true)) { }
            Assert.False(queue.CanPublish(1, true));
        }

        private void ExhaustEntityPool()
        {
            typeof(ComponentStore).GetField("nextEntityId", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Store, ComponentStore.MAX_ENTITIES);
        }

        private void LeaveEntitySlots(int slots)
        {
            typeof(ComponentStore).GetField("nextEntityId", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Store, ComponentStore.MAX_ENTITIES - slots);
        }

        private static int CountActiveTelegraphs(TelegraphSystem telegraph)
        {
            FieldInfo field = typeof(TelegraphSystem).GetField("_activeZoneIds",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            return ((System.Collections.IList)field.GetValue(telegraph)!).Count;
        }

        private sealed class StubRestore : ISnapshotRestorePort
        {
            public int Samples;
            public float RestoreResult = 1f;
            public int GetSampleCount(int playerId) => Samples;
            public float RestoreFromSnapshot(int playerId, float secondsBack) =>
                RestoreFromSnapshot(playerId, playerId, secondsBack);
            public float RestoreFromSnapshot(int sourceEntityId, int playerId, float secondsBack) => RestoreResult;
        }

        private sealed class StubResurrect : IResurrectionPort
        {
            public void SetTurn(int turn, float simTime) { }
            public bool CanMassResurrect(float centerX, float centerY, float radius) => false;
            public int MassResurrect(int playerId, float centerX, float centerY, float radius, float hpFraction) => 0;
        }
    }
}
