using System;
using System.Collections.Generic;
using System.Linq;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    /// <summary>使用真实 FrameScheduler.Tick 入口的战斗 golden 场景。</summary>
    public sealed class CombatGoldenReplayTests : BattleTestBase
    {
        [Fact]
        public void WaveFrame_ReplaysOnFreshStores_WithExpectedKillRewardAndEvents()
        {
            // Bug 回归：重放必须使用独立初态，并保留真实减伤、死亡与奖励事实。
            ReplaySnapshot first = RunWaveFrameOnFreshWorld();
            ReplaySnapshot second = RunWaveFrameOnFreshWorld();
            Assert.NotSame(first.SourceStore, second.SourceStore);
            Assert.Equal(first, second);
            Assert.Equal(0, first.ActiveEnemyCount);
            Assert.Empty(first.ActiveEnemyIds);
            Assert.Equal(1, first.Kills);
            Assert.Equal(7f, first.Gold);
            Assert.Equal(0f, first.EnemyHealth);
            Assert.Equal(first.ExpectedMitigatedDamage, Assert.Single(first.DamageAmounts), 3);
            Assert.True(first.ExpectedMitigatedDamage > first.InitialEnemyHealth);
            Assert.Equal(new[] { "damage", "killed", "destroyed" }, first.Events);
            Assert.Equal(new[]
            {
                "HitConfirmed", "DamageApplied", "DeathQueued", "KillConfirmed"
            }, first.DamageFactTypes);
            Assert.Equal(new[] { "ResourceChanged" }, first.ResourceFactTypes);
            Assert.Single(first.DamageFacts.Select(fact => fact.Sequence).Distinct());
            Assert.Equal(first.DamageFacts[0].Sequence, first.ResourceFacts[0].Sequence);
            Assert.True(first.DamageFacts[0].Sequence > 0L);
            Assert.Equal(first.DamageFacts, second.DamageFacts);
            Assert.Equal(first.ResourceFacts, second.ResourceFacts);
            Assert.Equal(0, first.PendingDamageRequests);
            Assert.Equal(0, first.PendingResourceRequests);
        }

        [Fact]
        public void TowerFrame_MitigationLayersEachReduceNonLethalDamage()
        {
            // Bug 回归：护甲、类型抗性和通用抗性必须分别贡献，不能被致死裁剪掩盖。
            const float injectedDamage = 100f;
            const float injectedArmor = 0.25f;
            const float injectedMagicResistance = 0.5f;
            const float injectedGenericResistance = 0.2f;
            const float conversionRatio = 0.5f;

            float control = RunNonLethalTowerFrame(0f, 0f, 0f, conversionRatio);
            float withArmor = RunNonLethalTowerFrame(injectedArmor, 0f, 0f, conversionRatio);
            float withTyped = RunNonLethalTowerFrame(injectedArmor, injectedMagicResistance, 0f, conversionRatio);
            float withGeneric = RunNonLethalTowerFrame(injectedArmor, injectedMagicResistance,
                injectedGenericResistance, conversionRatio);

            float expectedControl = injectedDamage;
            float expectedWithArmor = injectedDamage * (1f - conversionRatio) * (1f - injectedArmor)
                + injectedDamage * conversionRatio;
            float expectedWithTyped = injectedDamage * (1f - conversionRatio) * (1f - injectedArmor)
                + injectedDamage * conversionRatio * (1f - injectedMagicResistance);
            float expectedWithGeneric = expectedWithTyped * (1f - injectedGenericResistance);

            Assert.Equal(expectedControl, control, 3);
            Assert.Equal(expectedWithArmor, withArmor, 3);
            Assert.Equal(expectedWithTyped, withTyped, 3);
            Assert.Equal(expectedWithGeneric, withGeneric, 3);
            Assert.True(control > withArmor);
            Assert.True(withArmor > withTyped);
            Assert.True(withTyped > withGeneric);
        }

        [Fact]
        public void SameFrameFourteenHits_EmitsFourteenDamageEventsForSameTarget()
        {
            var events = new RecordingEventBus();
            int playerId = Player(p => { p.Health = 1000f; });
            int enemyId = Enemy(e => { e.X = 0f; e.Y = 0f; e.Health = 1000f; e.MaxHealth = 1000f; });
            for (int i = 0; i < 14; i++)
            {
                int towerId = RawTower(0, 0, TowerType.Basic, damage: 1f, range: 2, speed: 1f);
                Store.TowerCritChance[towerId] = 0f;
                Store.TowerDamageVariance[towerId] = 0f;
            }
            var scheduler = new FrameScheduler(Store, Config, events);
            var attack = new Systems.TowerAttackSystem(Store, Renderer, null, 10, new EventBus(), events);
            scheduler.Combat.TowerAttack = attack;
            scheduler.CombatSetup.TowerAttack = attack;
            RebuildGrid();
            scheduler.Tick(1f, 0);
            scheduler.Tick(0f, 1);
            Assert.Equal(14, events.DamageTargets.Count);
            Assert.All(events.DamageTargets, id => Assert.Equal(enemyId, id));
            Assert.Equal(14, events.Events.Count);
            Assert.All(events.Events, kind => Assert.Equal("damage", kind));
            Assert.Equal(986f, Store.EnemyHealth[enemyId]);
            Assert.Equal(0, Store.TotalKills);
            Assert.True(Store.IsPlayerAlive(playerId));
        }

        [Fact]
        public void BuildPhase_GlobalSkillIsRejected_WithNoHpOrDeathWork()
        {
            // Bug 回归：建造阶段的攻击性全局技能必须消费输入但不得提交伤害。
            var events = new RecordingEventBus();
            (SystemRegistry registry, FrameScheduler scheduler, int enemyId) = MakeRegistryScenario(events);
            scheduler.Phase = GameState.BuildPhase;
            registry.GlobalSkill!.SetTurn(0);
            Store.PlayerGlobalSkillPressed[0] = true;
            scheduler.Tick(1f, 0);
            Assert.Equal(10f, Store.EnemyHealth[enemyId]);
            Assert.True(Store.EnemyActive[enemyId]);
            Assert.Equal(0, Store.TotalKills);
            Assert.Empty(events.KillEvents);
            Assert.True(Renderer.HasLogContaining("PhaseNotAllowed"));
            Assert.False(Store.PlayerGlobalSkillPressed[0]);
            Assert.Equal(0, Store.DamageResolver.PendingRequestCount);
            Assert.Equal(0, Store.ResourceResolver.PendingRequestCount);
            Assert.DoesNotContain(Enumerable.Range(0, Store.DamageResolver.Events.Count),
                i => Store.DamageResolver.Events.Get(i).Type == Core.GAS.GameplayEventType.DeathQueued);
        }

        [Fact]
        public void BuildPhase_RegistrySkillAndAutoSkillAreRejectedWithoutDamageWork()
        {
            var events = new RecordingEventBus();
            Player(p => { p.Health = 1000f; p.X = 0f; p.Y = 0f; });
            Config.Levels.Clear();
            Config.Skills[0].AutoCast = true;
            int enemyId = Enemy(e => { e.X = 0f; e.Y = 0f; e.Health = 100f; e.MaxHealth = 100f; });
            var registry = new SystemRegistry();
            registry.CreateAll(Store, Config, Renderer, 0, new StateMachine(), events);
            registry.WireDependencies(Store, 0);
            registry.Skill!.InitializePlayerSkills();
            var scheduler = new FrameScheduler(Store, Config, events);
            registry.AssignToGroups(scheduler);
            scheduler.Phase = GameState.BuildPhase;
            scheduler.Tick(1f, 0);
            scheduler.Tick(1f, 1);
            scheduler.Tick(1f, 2);

            // Skill 与 AutoSkill 在 BuildGroup 中真实接线，但 BuildPhase 只允许资源/准备操作。
            Assert.Equal(100f, Store.EnemyHealth[enemyId]);
            Assert.Equal(0, Store.TotalKills);
            Assert.NotNull(registry.Skill);
            Assert.NotNull(registry.AutoSkill);
            Assert.True(Renderer.HasLogContaining("PhaseNotAllowed skill="));
            Assert.True(Renderer.HasLogContaining("PhaseNotAllowed source=AutoSkill"));
            Assert.Equal(2, Renderer.Logs.FindAll(x => x.Contains("PhaseNotAllowed", StringComparison.Ordinal)).Count);
            Assert.Equal(0, Store.DamageResolver.PendingRequestCount);
            Assert.Equal(0, Store.ResourceResolver.PendingRequestCount);
            Assert.DoesNotContain(Enumerable.Range(0, Store.DamageResolver.Events.Count),
                i => Store.DamageResolver.Events.Get(i).Type == Core.GAS.GameplayEventType.DeathQueued);
        }

        [Fact]
        public void HolyLegacyMaskBlocksBeforeHolyResistance()
        {
            // Bug 回归：兼容掩码 64 优先于独立 Holy 抗性分支。
            int playerId = Player(p => p.Health = 1000f);
            int enemyId = Enemy(e =>
            {
                e.Health = 1000f; e.MaxHealth = 1000f; e.HolyResist = 0.5f;
            });
            EntityHandle source = Store.GetEntityHandle(playerId);
            EntityHandle target = Store.GetEntityHandle(enemyId);
            Store.EnemyDamageImmunityMask[enemyId] = (int)DamageType.Holy;

            DamageApplyResult blocked = Store.DamageResolver.TryApply(
                new DamageRequest(source, target, 100f, DamageType.Holy, 1L, ownerPlayerId: playerId));

            Assert.False(blocked.Accepted);
            Assert.Equal(DamageRejectionReason.Invulnerable, blocked.Reason);
            Assert.Equal(1000f, Store.EnemyHealth[enemyId]);

            Store.EnemyDamageImmunityMask[enemyId] = 0;
            DamageApplyResult resisted = Store.DamageResolver.TryApply(
                new DamageRequest(source, target, 100f, DamageType.Holy, 2L, ownerPlayerId: playerId));

            Assert.True(resisted.Accepted);
            Assert.Equal(50f, resisted.Applied);
            Assert.Equal(950f, Store.EnemyHealth[enemyId]);
        }

        [Fact]
        public void SharedStackKeyReusesEffectAndRefreshesAtMaximumStacks()
        {
            // Bug 回归：跨定义共享 stack-key 必须复用实例且满层刷新不突破上限。
            using var store = new ComponentStore();
            int sourceId = store.AddEnemy(0f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            int targetId = store.AddEnemy(1f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            var key = new TagId(700);
            var first = new GameplayEffectDefinition(new EffectId(701), EffectType.Duration,
                Array.Empty<ModifierDefinition>(), 4f, 0f, ClockId.Combat,
                StackingBehavior.MaxStacksRefresh, 2, RefreshPolicy.StacksAndDuration,
                SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent, default(TagId),
                Array.Empty<ExecutionId>(), stackKey: key);
            var second = new GameplayEffectDefinition(new EffectId(702), EffectType.Duration,
                Array.Empty<ModifierDefinition>(), 6f, 0f, ClockId.Combat,
                StackingBehavior.MaxStacksRefresh, 2, RefreshPolicy.StacksAndDuration,
                SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent, default(TagId),
                Array.Empty<ExecutionId>(), stackKey: key);
            EntityHandle source = store.GetEntityHandle(sourceId);
            EntityHandle target = store.GetEntityHandle(targetId);

            Assert.True(store.GameplayEffectsRuntime.TryApply(first.Id, first, source, target, out var handle));
            store.GameplayEffectsRuntime.Tick(0.5f, ClockId.Combat);
            Assert.True(store.GameplayEffectsRuntime.TryApply(second.Id, second, source, target, out _));
            store.GameplayEffectsRuntime.Tick(0.5f, ClockId.Combat);
            Assert.True(store.GameplayEffectsRuntime.TryApply(second.Id, second, source, target, out _));

            Assert.Equal(1, store.GetEffectCount(targetId));
            Assert.True(store.GameplayEffects.TryGet(handle, out var active, out _, out _));
            Assert.Equal(2, active.StackCount);
            Assert.Equal(6f, active.RemainingTime, 3);
        }

        [Fact]
        public void EffectRefreshPoliciesPreserveCurrentCompatibilityBranches()
        {
            using var store = new ComponentStore();
            int sourceId = store.AddEnemy(0f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            int durationTargetId = store.AddEnemy(1f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            int maxDurationTargetId = store.AddEnemy(2f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            int stackDurationTargetId = store.AddEnemy(3f, 0f, 1f, 10f, 10f, 1f, 1, 1);
            EntityHandle source = store.GetEntityHandle(sourceId);
            var durationRefresh = new GameplayEffectDefinition(new EffectId(710), EffectType.Duration,
                Array.Empty<ModifierDefinition>(), 4f, 0f, ClockId.Combat,
                StackingBehavior.DurationRefresh, 1, RefreshPolicy.Duration,
                SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent, default(TagId),
                Array.Empty<ExecutionId>());
            var maxStacksDuration = new GameplayEffectDefinition(new EffectId(711), EffectType.Duration,
                Array.Empty<ModifierDefinition>(), 4f, 0f, ClockId.Combat,
                StackingBehavior.MaxStacks, 2, RefreshPolicy.Duration,
                SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent, default(TagId),
                Array.Empty<ExecutionId>());
            var maxStacksAndDuration = new GameplayEffectDefinition(new EffectId(712), EffectType.Duration,
                Array.Empty<ModifierDefinition>(), 4f, 0f, ClockId.Combat,
                StackingBehavior.MaxStacks, 2, RefreshPolicy.StacksAndDuration,
                SourceDeathPolicy.Persist, EffectPayloadKind.GameplayEvent, default(TagId),
                Array.Empty<ExecutionId>());
            EntityHandle durationTarget = store.GetEntityHandle(durationTargetId);
            EntityHandle maxDurationTarget = store.GetEntityHandle(maxDurationTargetId);
            EntityHandle stackDurationTarget = store.GetEntityHandle(stackDurationTargetId);

            Assert.True(store.GameplayEffectsRuntime.TryApply(durationRefresh.Id, durationRefresh,
                source, durationTarget, out var durationHandle));
            Assert.True(store.GameplayEffectsRuntime.TryApply(maxStacksDuration.Id, maxStacksDuration,
                source, maxDurationTarget, out var maxDurationHandle));
            Assert.True(store.GameplayEffectsRuntime.TryApply(maxStacksAndDuration.Id, maxStacksAndDuration,
                source, stackDurationTarget, out var stackDurationHandle));
            store.GameplayEffectsRuntime.Tick(1f, ClockId.Combat);
            Assert.True(store.GameplayEffectsRuntime.TryApply(durationRefresh.Id, durationRefresh,
                source, durationTarget, out _));
            Assert.True(store.GameplayEffectsRuntime.TryApply(maxStacksDuration.Id, maxStacksDuration,
                source, maxDurationTarget, out _));
            Assert.True(store.GameplayEffectsRuntime.TryApply(maxStacksAndDuration.Id, maxStacksAndDuration,
                source, stackDurationTarget, out _));

            Assert.True(store.GameplayEffects.TryGet(durationHandle, out var durationActive, out _, out _));
            Assert.True(store.GameplayEffects.TryGet(maxDurationHandle, out var maxDurationActive, out _, out _));
            Assert.True(store.GameplayEffects.TryGet(stackDurationHandle, out var stackDurationActive, out _, out _));
            Assert.Equal(1, durationActive.StackCount);
            Assert.Equal(4f, durationActive.RemainingTime, 3);
            Assert.Equal(2, maxDurationActive.StackCount);
            Assert.Equal(3f, maxDurationActive.RemainingTime, 3);
            Assert.Equal(2, stackDurationActive.StackCount);
            Assert.Equal(4f, stackDurationActive.RemainingTime, 3);
        }

        [Fact]
        public void PeriodicExplicitMagnitudeIsCapturedAtApplication()
        {
            // Bug 回归：周期效果必须使用申请时快照，不能在 tick 时重新读取来源值。
            using var store = new ComponentStore();
            store.AddPlayer(0, 1f, 1f, 1f, 1);
            int targetId = store.AddEnemy(1f, 0f, 1f, 20f, 20f, 1f, 1, 1);
            int defaultTargetId = store.AddEnemy(2f, 0f, 1f, 20f, 20f, 1f, 1, 1);
            var spec = new PeriodicSpec(1f, new ExecutionId(703), EffectPayloadKind.Damage,
                MagnitudeSource.Constant, FirstTickPolicy.NextInterval, CatchUpPolicy.CatchUpAll,
                magnitude: 2f);
            var definition = new GameplayEffectDefinition(new EffectId(703), EffectType.Periodic,
                Array.Empty<ModifierDefinition>(), 2f, ClockId.Combat, StackingBehavior.None, 1,
                RefreshPolicy.None, SourceDeathPolicy.Persist, EffectPayloadKind.Damage,
                default(TagId), spec, Array.Empty<ExecutionId>());

            Assert.True(store.GameplayEffectsRuntime.TryApply(definition.Id, definition,
                store.GetEntityHandle(0), store.GetEntityHandle(targetId), out var handle,
                snapshot: 7f, ownerPlayerId: 0));
            Assert.True(store.GameplayEffectsRuntime.TryApply(definition.Id, definition,
                store.GetEntityHandle(0), store.GetEntityHandle(defaultTargetId), out var defaultHandle,
                ownerPlayerId: 0));
            Assert.True(store.GameplayEffects.TryGet(handle, out var active, out _, out _));
            Assert.True(store.GameplayEffects.TryGet(defaultHandle, out var defaultActive, out _, out _));
            Assert.Equal(7f, active.CapturedMagnitude);
            Assert.Equal(2f, defaultActive.CapturedMagnitude);

            store.GameplayEffectsRuntime.Tick(1f, ClockId.Combat);

            Assert.Equal(13f, store.EnemyHealth[targetId], 3);
            Assert.Equal(18f, store.EnemyHealth[defaultTargetId], 3);
        }

        [Fact]
        public void MaxHealthIncreaseDoesNotHealPlayerOrEnemy()
        {
            using var store = new ComponentStore();
            store.AddPlayer(0, 1f, 1f, 1f, 1);
            store.PlayerMaxHealth[0] = 100f;
            store.PlayerCurrentHealth[0] = 40f;
            int enemyId = store.AddEnemy(0f, 0f, 1f, 40f, 100f, 1f, 1, 1);
            EntityHandle player = store.GetEntityHandle(0);
            EntityHandle enemy = store.GetEntityHandle(enemyId);

            Assert.True(store.ResourceResolver.TryApply(new ResourceRequest(player, player,
                new AttributeKey(2), 25f, ResourceOperation.Set, 0, 1L, ownerPlayerId: 0)).Accepted);
            Assert.True(store.ResourceResolver.TryApply(new ResourceRequest(player, enemy,
                new AttributeKey(2), 25f, ResourceOperation.Set, 0, 2L, ownerPlayerId: 0)).Accepted);
            Assert.Equal(25f, store.PlayerCurrentHealth[0]);
            Assert.Equal(25f, store.EnemyHealth[enemyId]);

            Assert.True(store.ResourceResolver.TryApply(new ResourceRequest(player, player,
                new AttributeKey(2), 80f, ResourceOperation.Set, 0, 3L, ownerPlayerId: 0)).Accepted);
            Assert.True(store.ResourceResolver.TryApply(new ResourceRequest(player, enemy,
                new AttributeKey(2), 80f, ResourceOperation.Set, 0, 4L, ownerPlayerId: 0)).Accepted);
            Assert.Equal(25f, store.PlayerCurrentHealth[0]);
            Assert.Equal(25f, store.EnemyHealth[enemyId]);
        }

        [Fact]
        public void WavePhase_GlobalSkillCommitsMeteorDeathAndReward()
        {
            var events = new RecordingEventBus();
            (SystemRegistry registry, FrameScheduler scheduler, int enemyId) = MakeRegistryScenario(events);
            scheduler.Phase = GameState.WavePhase;
            registry.GlobalSkill!.SetTurn(0);
            Store.PlayerGlobalSkillPressed[0] = true;
            scheduler.Tick(1f, 0);
            Assert.False(Store.EnemyActive[enemyId]);
            Assert.Equal(1, Store.TotalKills);
            Assert.Equal(7f, Store.GetPlayerGold(0));
            Assert.Equal(new[] { "killed", "destroyed" }, events.KillEvents);
        }

        [Fact]
        public void BuildPhase_EmergencyHealRemainsAllowed_AndChangesPlayerHealth()
        {
            Config.Levels.Clear();
            Config.GlobalSkills.Add(new GlobalSkillDef { Name = "Emergency Heal", SkillType = (int)GlobalSkillType.EmergencyHeal, ManaCost = 0f, Cooldown = 0f, HealPct = 0.5f });
            Player(p => { p.Health = 100f; });
            Store.PlayerCurrentHealth[0] = 40f;
            var registry = new SystemRegistry();
            registry.CreateAll(Store, Config, Renderer, 0, new StateMachine(), new RecordingEventBus());
            registry.WireDependencies(Store, 0);
            var scheduler = new FrameScheduler(Store, Config, new RecordingEventBus());
            registry.AssignToGroups(scheduler);
            scheduler.Phase = GameState.BuildPhase;
            registry.GlobalSkill!.SetTurn(0);
            Store.PlayerGlobalSkillPressed[0] = true;
            scheduler.Tick(1f, 0);
            Assert.Equal(60f, Store.PlayerCurrentHealth[0]);
            Assert.True(Renderer.HasLogContaining("Emergency Heal"));
            Assert.Equal(0, Store.DamageResolver.PendingRequestCount);
            Assert.Equal(0, Store.ResourceResolver.PendingRequestCount);
            Assert.Contains(Enumerable.Range(0, Store.ResourceResolver.Events.Count),
                i => Store.ResourceResolver.Events.Get(i).Type == Core.GAS.GameplayEventType.ResourceChanged);
        }

        [Fact]
        public void BulletTime_PoisonUsesCombatClock_AtFullTickRate()
        {
            Player(p => { p.Health = 1000f; });
            int enemyId = Enemy(e => { e.Health = 100f; e.MaxHealth = 100f; });
            var buff = new Systems.BuffSystem(Store, 0, Renderer);
            var poison = Core.GAS.GameplayEffectDef.Periodic("Poison", Core.GAS.AttributeSetDefinitions.ENEMY_HEALTH, 10f, 2f, 1f);
            buff.ApplyDot(enemyId, poison);
            var scheduler = new FrameScheduler(Store, Config);
            scheduler.SkillBuff.Buff = buff;
            Store.PlayerBulletTimeTurnsLeft[0] = 2f;
            Store.PlayerBulletTimeScale[0] = 0.25f;
            scheduler.Tick(1f, 0);
            scheduler.Tick(1f, 1);
            Assert.Equal(80f, Store.EnemyHealth[enemyId]);
        }

        [Fact]
        public void BulletTime_TowerAttackUsesCombatClock_AndHitsNormally()
        {
            Player(p => { p.Health = 1000f; });
            int enemyId = Enemy(e => { e.X = 0f; e.Y = 0f; e.Health = 300f; e.MaxHealth = 300f; });
            int towerId = RawTower(0, 0, TowerType.Basic, damage: 100f, range: 2, speed: 1f);
            Store.TowerCritChance[towerId] = 0f;
            Store.TowerDamageVariance[towerId] = 0f;
            var scheduler = new FrameScheduler(Store, Config);
            var attack = new Systems.TowerAttackSystem(Store, Renderer);
            scheduler.Combat.TowerAttack = attack;
            scheduler.CombatSetup.TowerAttack = attack;
            Store.PlayerBulletTimeTurnsLeft[0] = 2f;
            Store.PlayerBulletTimeScale[0] = 0.25f;
            RebuildGrid();
            scheduler.Tick(1f, 0);
            scheduler.Tick(0f, 1);
            Assert.Equal(200f, Store.EnemyHealth[enemyId]);
        }

        [Fact]
        public void BulletTime_WeatherUsesEnemyClock_ScalingDamageByQuarter()
        {
            Player(p => { p.Health = 1000f; });
            Config.Levels.Clear();
            Config.Weather.Types["Sandstorm"] = new WeatherTypeConfig { EnemyDotPct = 0.005f, MinIntensity = 1f, MaxIntensity = 1f, DefaultDuration = 10f };
            int enemyId = Enemy(e => { e.Health = 100f; e.MaxHealth = 100f; });
            var weather = new Systems.WeatherSystem(Store, Config);
            weather.ForceWeather(0, WeatherConfig.Sandstorm, 1f, 10f);
            var scheduler = new FrameScheduler(Store, Config);
            scheduler.PreGame.Weather = weather;
            Store.PlayerBulletTimeTurnsLeft[0] = 2f;
            Store.PlayerBulletTimeScale[0] = 0.25f;
            scheduler.Tick(1f, 0);
            Assert.Equal(99.875f, Store.EnemyHealth[enemyId], 3);
        }

        [Fact]
        public void BulletTime_WoundTransitionIsDtFree_AndDoesNotShareWeatherClock()
        {
            Player(p => { p.Health = 1000f; });
            Config.Levels.Clear();
            int enemyId = Enemy(e => { e.Health = 10f; e.MaxHealth = 100f; e.MoveSpeed = 2f; });
            Store.EnemyWoundThreshold[enemyId] = 0.5f;
            Store.EnemyWoundSlowRatio[enemyId] = 0.5f;
            var wound = new Systems.EnemyWoundSystem(Store, 0);
            var scheduler = new FrameScheduler(Store, Config);
            scheduler.Movement.Wound = wound;
            Store.PlayerBulletTimeTurnsLeft[0] = 2f;
            Store.PlayerBulletTimeScale[0] = 0.25f;
            scheduler.Tick(1f, 0);
            Assert.True(Store.EnemyIsWounded[enemyId]);
            Assert.Equal(1f, Store.EnemyMoveSpeed[enemyId]);
        }

        private ReplaySnapshot RunWaveFrameOnFreshWorld()
        {
            using var world = new TestWorld();
            var events = new RecordingEventBus();
            const float injectedTowerDamage = 100f;
            const float injectedArmor = 0.25f;
            const float injectedMagicResistance = 0.5f;
            const float injectedGenericResistance = 0.2f;
            const float injectedConversionRatio = 0.5f;
            const float injectedEnemyHealth = 45f;
            const int injectedGoldReward = 7;
            float expectedMitigatedDamage = injectedTowerDamage * (1f - injectedConversionRatio) * (1f - injectedArmor)
                + injectedTowerDamage * injectedConversionRatio * (1f - injectedMagicResistance);
            expectedMitigatedDamage *= 1f - injectedGenericResistance;
            int playerId = world.Player(p => { p.Health = 100f; p.Gold = 0f; });
            int enemyId = world.Enemy(e =>
            {
                e.X = 5f; e.Y = 1f; e.Health = injectedEnemyHealth; e.MaxHealth = injectedEnemyHealth;
                e.Armor = injectedArmor; e.MagicResist = injectedMagicResistance;
                e.GoldReward = injectedGoldReward;
            });
            world.Store.EnemyDamageResistance[enemyId] = injectedGenericResistance;
            int towerId = world.Tower(5, 1, TowerType.Basic,
                t => { t.Damage = injectedTowerDamage; t.Range = 2; t.Speed = 1f; });
            world.Store.TowerCritChance[towerId] = 0f;
            world.Store.TowerDamageVariance[towerId] = 0f;
            world.Store.TowerDamageType[towerId] = DamageType.Physical;
            world.Store.TowerDamageConversionRatio[towerId] = injectedConversionRatio;
            world.Store.TowerConvertedDamageType[towerId] = DamageType.Magic;
            var scheduler = new FrameScheduler(world.Store, world.Config, events);
            var attack = new Systems.TowerAttackSystem(world.Store, world.Renderer, null, 10, new EventBus(), events);
            scheduler.Combat.TowerAttack = attack;
            scheduler.CombatSetup.TowerAttack = attack;
            scheduler.Phase = GameState.WavePhase;
            world.Store.RebuildSpatialGrid();
            scheduler.Tick(1f, 0);
            var active = new List<int>(world.Store.ActiveEnemyIds);
            var damageFacts = Enumerable.Range(0, world.Store.DamageResolver.Events.Count)
                .Select(i => ResolverFact.From(world.Store.DamageResolver.Events.Get(i))).ToList();
            var resourceFacts = Enumerable.Range(0, world.Store.ResourceResolver.Events.Count)
                .Select(i => ResolverFact.From(world.Store.ResourceResolver.Events.Get(i))).ToList();
            return new ReplaySnapshot(world.Store, world.Store.GetActiveEnemyCount(), active, world.Store.TotalKills,
                world.Store.GetPlayerGold(playerId), world.Store.EnemyHealth[enemyId], events.Events,
                damageFacts, resourceFacts, world.Store.DamageResolver.PendingRequestCount,
                world.Store.ResourceResolver.PendingRequestCount, injectedEnemyHealth,
                expectedMitigatedDamage, events.DamageAmounts);
        }

        private static float RunNonLethalTowerFrame(float armor, float magicResistance,
            float genericResistance, float conversionRatio)
        {
            using var world = new TestWorld();
            var events = new RecordingEventBus();
            world.Player(p => p.Health = 100f);
            int enemyId = world.Enemy(e =>
            {
                e.X = 5f; e.Y = 1f; e.Health = 1000f; e.MaxHealth = 1000f;
                e.Armor = armor; e.MagicResist = magicResistance;
            });
            world.Store.EnemyDamageResistance[enemyId] = genericResistance;
            int towerId = world.Tower(5, 1, TowerType.Basic,
                t => { t.Damage = 100f; t.Range = 2; t.Speed = 1f; });
            world.Store.TowerCritChance[towerId] = 0f;
            world.Store.TowerDamageVariance[towerId] = 0f;
            world.Store.TowerDamageType[towerId] = DamageType.Physical;
            world.Store.TowerDamageConversionRatio[towerId] = conversionRatio;
            world.Store.TowerConvertedDamageType[towerId] = DamageType.Magic;
            var scheduler = new FrameScheduler(world.Store, world.Config, events);
            var attack = new Systems.TowerAttackSystem(world.Store, world.Renderer, null, 10,
                new EventBus(), events);
            scheduler.Combat.TowerAttack = attack;
            scheduler.CombatSetup.TowerAttack = attack;
            scheduler.Phase = GameState.WavePhase;
            world.Store.RebuildSpatialGrid();
            scheduler.Tick(1f, 0);
            Assert.True(world.Store.EnemyActive[enemyId]);
            return Assert.Single(events.DamageAmounts);
        }

        private (SystemRegistry registry, FrameScheduler scheduler, int enemyId) MakeRegistryScenario(IBattleEventBus events)
        {
            Player(p => { p.Health = 1000f; p.Gold = 0f; });
            Config.Levels.Clear();
            Config.GlobalSkills.Add(new GlobalSkillDef { Name = "Meteor Strike", SkillType = (int)GlobalSkillType.MeteorStrike, ManaCost = 0f, Cooldown = 0f, DamagePct = 100f, MaxDamage = 10000f });
            int enemyId = Enemy(e => { e.X = 20f; e.Y = 20f; e.Health = 10f; e.MaxHealth = 10f; e.GoldReward = 7; });
            var registry = new SystemRegistry();
            registry.CreateAll(Store, Config, Renderer, 0, new StateMachine(), events);
            registry.WireDependencies(Store, 0);
            var scheduler = new FrameScheduler(Store, Config, events);
            registry.AssignToGroups(scheduler);
            return (registry, scheduler, enemyId);
        }

        private sealed class RecordingEventBus : IBattleEventBus
        {
            public List<string> Events { get; } = new List<string>();
            public List<int> DamageTargets { get; } = new List<int>();
            public List<float> DamageAmounts { get; } = new List<float>();
            public List<string> KillEvents { get; } = new List<string>();
            public void OnEntityCreated(int entityId, float x, float y, string entityType) => Events.Add("created");
            public void OnTowerCreated(int entityId, float x, float y, TowerType towerType) { }
            public void OnEntityDestroyed(int entityId) { Events.Add("destroyed"); KillEvents.Add("destroyed"); }
            public void OnPositionChanged(int entityId, float x, float y) { }
            public void OnPositionsChanged(List<(int entityId, float x, float y)> changes) { }
            public void OnDamageDealt(int targetId, float amount, string damageType, bool isCritical) { Events.Add("damage"); DamageTargets.Add(targetId); DamageAmounts.Add(amount); }
            public void OnEntityKilled(int entityId, int killerId) { Events.Add("killed"); KillEvents.Add("killed"); }
            public void OnProjectileFired(float fromX, float fromY, float toX, float toY, float speed) { }
            public void OnWaveStarted(int waveNumber) { }
            public void OnGameOver(bool victory) { }
        }

        private sealed class ReplaySnapshot : IEquatable<ReplaySnapshot>
        {
            public ComponentStore SourceStore { get; }
            public int ActiveEnemyCount { get; }
            public List<int> ActiveEnemyIds { get; }
            public int Kills { get; }
            public float Gold { get; }
            public float EnemyHealth { get; }
            public List<string> Events { get; }
            public List<ResolverFact> DamageFacts { get; }
            public List<ResolverFact> ResourceFacts { get; }
            public List<string> DamageFactTypes => DamageFacts.Select(fact => fact.Type).ToList();
            public List<string> ResourceFactTypes => ResourceFacts.Select(fact => fact.Type).ToList();
            public int PendingDamageRequests { get; }
            public int PendingResourceRequests { get; }
            public float InitialEnemyHealth { get; }
            public float ExpectedMitigatedDamage { get; }
            public List<float> DamageAmounts { get; }
            public ReplaySnapshot(ComponentStore sourceStore, int count, List<int> ids, int kills, float gold,
                float hp, List<string> events, List<ResolverFact> damageFacts, List<ResolverFact> resourceFacts,
                int pendingDamageRequests, int pendingResourceRequests, float initialEnemyHealth,
                float expectedMitigatedDamage, List<float> damageAmounts)
            {
                SourceStore = sourceStore; ActiveEnemyCount = count; ActiveEnemyIds = new List<int>(ids); Kills = kills; Gold = gold;
                EnemyHealth = hp; Events = new List<string>(events); DamageFacts = new List<ResolverFact>(damageFacts);
                ResourceFacts = new List<ResolverFact>(resourceFacts); PendingDamageRequests = pendingDamageRequests;
                PendingResourceRequests = pendingResourceRequests;
                InitialEnemyHealth = initialEnemyHealth; ExpectedMitigatedDamage = expectedMitigatedDamage;
                DamageAmounts = new List<float>(damageAmounts);
            }
            public bool Equals(ReplaySnapshot? other) => other != null && ActiveEnemyCount == other.ActiveEnemyCount && Kills == other.Kills && Gold == other.Gold && EnemyHealth == other.EnemyHealth && InitialEnemyHealth == other.InitialEnemyHealth && ExpectedMitigatedDamage == other.ExpectedMitigatedDamage && PendingDamageRequests == other.PendingDamageRequests && PendingResourceRequests == other.PendingResourceRequests && ActiveEnemyIds.SequenceEqual(other.ActiveEnemyIds) && Events.SequenceEqual(other.Events) && DamageAmounts.SequenceEqual(other.DamageAmounts) && DamageFacts.SequenceEqual(other.DamageFacts) && ResourceFacts.SequenceEqual(other.ResourceFacts);
            public override bool Equals(object? obj) => Equals(obj as ReplaySnapshot);
            public override int GetHashCode() => HashCode.Combine(ActiveEnemyCount, Kills, Gold, EnemyHealth);
        }

        private sealed class ResolverFact : IEquatable<ResolverFact>
        {
            public string Type { get; }
            public long Sequence { get; }
            public long ParentSequence { get; }
            public int SourceIndex { get; }
            public int SourceGeneration { get; }
            public int TargetIndex { get; }
            public int TargetGeneration { get; }
            public int OwnerPlayerId { get; }

            private ResolverFact(Core.GAS.GameplayEvent fact)
            {
                Type = fact.Type.ToString(); Sequence = fact.Sequence; ParentSequence = fact.ParentSequence;
                SourceIndex = fact.Source.Index; SourceGeneration = fact.Source.Generation;
                TargetIndex = fact.Target.Index; TargetGeneration = fact.Target.Generation;
                OwnerPlayerId = fact.OwnerPlayerId;
            }

            public static ResolverFact From(Core.GAS.GameplayEvent fact) => new ResolverFact(fact);
            public bool Equals(ResolverFact? other) => other != null && Type == other.Type &&
                Sequence == other.Sequence && ParentSequence == other.ParentSequence &&
                SourceIndex == other.SourceIndex && SourceGeneration == other.SourceGeneration &&
                TargetIndex == other.TargetIndex && TargetGeneration == other.TargetGeneration &&
                OwnerPlayerId == other.OwnerPlayerId;
            public override bool Equals(object? obj) => Equals(obj as ResolverFact);
            public override int GetHashCode() => HashCode.Combine(Type, Sequence, ParentSequence, SourceIndex,
                SourceGeneration, TargetIndex, TargetGeneration, OwnerPlayerId);
        }
    }
}
