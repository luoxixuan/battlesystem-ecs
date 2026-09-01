using System;
using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Systems;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class SkillBuildBoundaryTests : BattleTestBase
    {
        [Fact]
        public void BuildPhase_PublicCastIsRejectedAndCannotReachFirstWave()
        {
            // Bug 回归：BuildPhase 的公开 CastSkill 伤害请求必须在同帧边界消费。
            var events = new RecordingEventBus();
            Player(p => { p.Health = 1000f; p.X = 0f; p.Y = 0f; });
            Config.Levels.Clear();
            ConfigureDamageSkill();
            Config.ManaShield.Enabled = false;
            int enemyId = Enemy(e => { e.X = 2f; e.Y = 0f; e.Health = 100f; e.MaxHealth = 100f; e.GoldReward = 7; });
            var registry = CreateRegistry(events);
            registry.Skill!.SetTurn(0);
            var scheduler = new FrameScheduler(Store, Config, events);
            registry.AssignToGroups(scheduler);
            scheduler.Phase = GameState.BuildPhase;
            // Bug 回归：composition seal 后不得靠清空 Group 槽绕过生产节点；显式冻结资源输入。
            Store.PlayerMaxMana[0] = 100f;
            Store.PlayerMana[0] = 100f;
            Store.PlayerManaRegen[0] = 0f;
            float manaBefore = Store.PlayerMana[0];
            int resolverEventsBefore = Store.DamageResolver.Events.Count;
            registry.Skill.CastSkill(Config.Skills[0].Name);
            Assert.Equal(0, registry.Skill.PendingSkillDamageCount);
            Assert.Equal(1, registry.Skill.RejectedAbilityCount);
            Assert.Equal(SkillDamageRejectReason.PhaseNotAllowed, registry.Skill.LastRejectReason);
            Assert.Equal(manaBefore, Store.PlayerMana[0]);
            scheduler.Tick(1f, 0);

            Assert.Equal(0, registry.Skill.PendingSkillDamageCount);
            Assert.Equal(0, registry.Skill.RejectedSkillDamageCount);
            Assert.Equal(0, registry.Skill.ConsumedSkillDamageCount);
            Assert.Equal(SkillDamageRejectReason.PhaseNotAllowed, registry.Skill.LastRejectReason);
            Assert.Equal(manaBefore, Store.PlayerMana[0]);
            Assert.Equal(0f, Store.GetAbility(0, 0).CurrentCooldown);
            Assert.Equal(100f, Store.EnemyHealth[enemyId]);
            Assert.Equal(0, Store.TotalKills);
            Assert.Equal(0f, Store.GetPlayerGold(0));
            Assert.Equal(0, Store.DamageResolver.PendingRequestCount);
            Assert.Equal(resolverEventsBefore, Store.DamageResolver.Events.Count);
            Assert.Empty(events.Events);
            Assert.True(Renderer.HasLogContaining("ABILITY_REJECTED"));
            Assert.False(Renderer.HasLogContaining("[DEATH]"));
            Assert.False(Renderer.HasLogContaining("[DAMAGE]"));

            scheduler.Phase = GameState.WavePhase;
            registry.Skill.CastSkill(Config.Skills[0].Name);
            Assert.Equal(1, registry.Skill.PendingSkillDamageCount);
            scheduler.Phase = GameState.BuildPhase;
            scheduler.Tick(1f, 1);
            Assert.Equal(0, registry.Skill.PendingSkillDamageCount);
            Assert.Equal(1, registry.Skill.RejectedSkillDamageCount);
            Assert.Equal(SkillDamageRejectReason.PhaseNotAllowed, registry.Skill.LastRejectReason);

            scheduler.Phase = GameState.WavePhase;
            scheduler.Tick(1f, 1);
            Assert.Equal(100f, Store.EnemyHealth[enemyId]);
            Assert.Equal(0, Store.TotalKills);
            Assert.Equal(0, Store.DamageResolver.PendingRequestCount);
            registry.Skill.RejectPendingSkillDamage();
            Assert.Equal(SkillDamageRejectReason.PhaseNotAllowed, registry.Skill.LastRejectReason);
        }

        [Fact]
        public void WavePhase_PublicCastIsConsumedByFramePath()
        {
            // Bug 回归：WavePhase 的合法技能请求仍由真实 SkillBuff 帧路径消费。
            var events = new RecordingEventBus();
            Player(p => { p.Health = 1000f; p.X = 0f; p.Y = 0f; });
            Config.Levels.Clear();
            ConfigureDamageSkill();
            int enemyId = Enemy(e => { e.X = 2f; e.Y = 0f; e.Health = 100f; e.MaxHealth = 100f; });
            var registry = CreateRegistry(events);
            registry.Skill!.SetTurn(0);
            Store.PlayerMana[0] = 100f;
            var scheduler = new FrameScheduler(Store, Config, events);
            registry.AssignToGroups(scheduler);
            scheduler.Phase = GameState.WavePhase;
            registry.Skill.CastSkill(Config.Skills[0].Name);
            Assert.Equal(1, registry.Skill.PendingSkillDamageCount);
            int resolverEventsBefore = Store.DamageResolver.Events.Count;
            scheduler.Tick(1f, 0);

            Assert.Equal(0, registry.Skill.PendingSkillDamageCount);
            Assert.Equal(1, registry.Skill.ConsumedSkillDamageCount);
            Assert.True(Store.EnemyHealth[enemyId] < 100f);
            Assert.Equal(0, Store.DamageResolver.PendingRequestCount);
            Assert.Equal(resolverEventsBefore + 2, Store.DamageResolver.Events.Count);
            Assert.Equal(GameplayEventType.HitConfirmed, Store.DamageResolver.Events.Get(resolverEventsBefore).Type);
            Assert.Equal(GameplayEventType.DamageApplied, Store.DamageResolver.Events.Get(resolverEventsBefore + 1).Type);
            Assert.Equal(Store.DamageResolver.Events.Get(resolverEventsBefore).Sequence, Store.DamageResolver.Events.Get(resolverEventsBefore + 1).Sequence);
            Assert.Empty(events.Events);
        }

        [Fact]
        public void UnboundCombatEntrypointsFailSafeWithoutSideEffects()
        {
            // Bug 回归：未绑定 Scheduler 的公开能力入口默认拒绝战斗效果。
            int playerId = Player(p => { p.Health = 100f; });
            ConfigureDamageSkill();
            var skill = new SkillSystem(Store, Renderer, playerId, Config);
            skill.InitializePlayerSkills();
            Store.PlayerMana[playerId] = 100f;
            var before = Store.GetAbility(playerId, 0);
            skill.CastSkill(Config.Skills[0].Name);
            Assert.Equal(0, skill.PendingSkillDamageCount);
            Assert.Equal(1, skill.RejectedAbilityCount);
            Assert.Equal(100f, Store.PlayerMana[playerId]);
            Assert.Equal(before.CurrentCooldown, Store.GetAbility(playerId, 0).CurrentCooldown);

            skill.AutoCastBestSkill();
            Assert.Equal(2, skill.RejectedAbilityCount);
            Assert.Equal(100f, Store.PlayerMana[playerId]);

            Config.GlobalSkills.Add(new GlobalSkillDef { Name = "Meteor", SkillType = (int)GlobalSkillType.MeteorStrike, ManaCost = 10f, Cooldown = 5f, DamagePct = 100f, MaxDamage = 1000f });
            var global = new GlobalSkillSystem(Store, Config, Renderer, playerId);
            global.SetTurn(0);
            float healthBefore = Store.PlayerCurrentHealth[playerId];
            Assert.False(global.TryActivateGlobalSkill(0));
            Assert.Equal(100f, Store.PlayerMana[playerId]);
            Assert.Equal(0f, Store.PlayerGlobalSkillCooldown[playerId * 8]);
            Assert.Equal(healthBefore, Store.PlayerCurrentHealth[playerId]);
        }

        [Fact]
        public void UnboundPublicAoeEntrypointsRejectWithoutControlSideEffects()
        {
            // Bug 回归：公开 CC 入口未绑定 phase 时不得直接 ApplyEnemy*。
            int playerId = Player(p => { p.X = 0f; p.Y = 0f; });
            int enemyId = Enemy(e => { e.X = 1f; e.Y = 0f; });
            var skill = new SkillSystem(Store, Renderer, playerId, Config);
            skill.SetTurn(0);
            float knockbackBefore = Store.EnemyKnockbackForceLeft[enemyId];
            Assert.Equal(0, skill.CastAoeStun(0f, 0f, 10, 2f, "stun"));
            Assert.Equal(0, skill.CastAoeRoot(0f, 0f, 10, 2f, "root"));
            Assert.Equal(0, skill.CastAoeKnockback(0f, 0f, 10, 2f, "knockback"));
            Assert.Equal(0, Store.EnemyStunDurationLeft[enemyId]);
            Assert.Equal(0, Store.EnemyRootDurationLeft[enemyId]);
            Assert.Equal(knockbackBefore, Store.EnemyKnockbackForceLeft[enemyId]);
            Assert.Equal(3, skill.RejectedAbilityCount);
            Assert.Equal(SkillDamageRejectReason.PhaseNotAllowed, skill.LastRejectReason);
        }

        [Fact]
        public void DirectResolveInBuildContextRejectsPendingDamage()
        {
            // Bug 回归：ResolveSkillDamage 不能绕过提交边界在 Build 直接落地伤害。
            int playerId = Player(p => { p.X = 0f; p.Y = 0f; });
            ConfigureDamageSkill();
            Config.GlobalSkills.Add(new GlobalSkillDef { Name = "Meteor", SkillType = (int)GlobalSkillType.MeteorStrike, ManaCost = 0f, Cooldown = 5f, DamagePct = 100f, MaxDamage = 1000f });
            int enemyId = Enemy(e => { e.X = 2f; e.Y = 0f; e.Health = 100f; e.MaxHealth = 100f; });
            var skill = new SkillSystem(Store, Renderer, playerId, Config);
            skill.InitializePlayerSkills();
            skill.SetTurn(0);
            skill.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            skill.CastSkill(Config.Skills[0].Name);
            Assert.Equal(1, skill.PendingSkillDamageCount);
            skill.SetPhaseContext(new PhaseContext(PhaseContextKind.Build));
            skill.ResolveSkillDamage();
            Assert.Equal(0, skill.PendingSkillDamageCount);
            Assert.Equal(1, skill.RejectedSkillDamageCount);
            Assert.Equal(SkillDamageRejectReason.UnsupportedCommitBoundary, skill.LastRejectReason);
            Assert.Equal(100f, Store.EnemyHealth[enemyId]);
        }

        [Fact]
        public void BuildPhase_AutoSkillUsesResourceAllowlist()
        {
            // Bug 回归：AutoSkill Build 请求与 SkillSystem 共用资源 allowlist。
            int playerId = Player(p => { p.Health = 100f; });
            Config.Levels.Clear();
            Config.AutoSkill.Enabled = true;
            Config.AutoSkill.MaxSkillsPerPhase = 1;
            Config.Skills[0].AreaShape = "heal";
            Config.Skills[0].HealPercent = 0.5f;
            Config.Skills[0].ManaCost = 0f;
            for (int i = 1; i < Config.Skills.Count; i++)
                Config.Skills[i].Cooldown = 100f;
            var registry = CreateRegistry(new RecordingEventBus());
            registry.Skill!.SetTurn(0);
            Enemy(e => { e.X = 1f; e.Y = 0f; e.Health = 100f; e.MaxHealth = 100f; });
            Store.PlayerCurrentHealth[playerId] = 40f;
            var scheduler = new FrameScheduler(Store, Config);
            registry.AssignToGroups(scheduler);
            scheduler.Phase = GameState.BuildPhase;
            scheduler.Build.Mana = null;
            scheduler.Build.ManaShield = null;
            scheduler.Tick(1f, 0);
            Assert.Equal(90f, Store.PlayerCurrentHealth[playerId]);
            Assert.Equal(0, registry.Skill.RejectedAbilityCount);
            Assert.Equal(0, registry.Skill.PendingSkillDamageCount);
            Assert.Equal(Config.Skills[0].Cooldown, Store.GetAbility(playerId, 0).CurrentCooldown);
            Assert.Equal(Config.Skills.Count - 1, registry.AutoSkill!.RejectedCandidateCount);
            Assert.Equal(1, registry.AutoSkill.SuccessfulCastCount);
            Assert.Equal(SkillDamageRejectReason.PhaseNotAllowed, registry.AutoSkill.LastRejectReason);
        }

        [Theory]
        [InlineData("heal")]
        [InlineData("shield")]
        [InlineData("chainheal")]
        [InlineData("timerwind")]
        public void BuildPhase_PlayerResourceAllowlistIsExplicit(string areaShape)
        {
            // Bug 回归：Build 资源白名单必须逐项可用，不能被战斗入口门禁误拒绝。
            int playerId = Player(p => { p.Health = 100f; });
            Config.Skills[0].AreaShape = areaShape;
            Config.Skills[0].Cooldown = 5f;
            Config.Skills[0].ManaCost = 0f;
            var registry = CreateRegistry(new RecordingEventBus());
            var scheduler = new FrameScheduler(Store, Config);
            registry.AssignToGroups(scheduler);
            scheduler.Phase = GameState.BuildPhase;
            Assert.True(registry.Skill!.CastSkill(Config.Skills[0].Name));
            Assert.Equal(0, registry.Skill.RejectedAbilityCount);
            Assert.Equal(5f, Store.GetAbility(playerId, 0).CurrentCooldown);
        }

        [Theory]
        [InlineData((int)GlobalSkillType.EmergencyHeal)]
        [InlineData((int)GlobalSkillType.GoldBurst)]
        public void BuildPhase_GlobalResourceAllowlistIsExplicit(int skillType)
        {
            // Bug 回归：GlobalSkill 的 Build 资源白名单必须逐项提交且只记成功。
            int playerId = Player(p => { p.Health = 100f; });
            Config.GlobalSkills.Add(new GlobalSkillDef { Name = "resource", SkillType = skillType, ManaCost = 0f, Cooldown = 5f, HealPct = 0.5f, GoldAmount = 25f });
            var registry = CreateRegistry(new RecordingEventBus());
            var scheduler = new FrameScheduler(Store, Config);
            registry.AssignToGroups(scheduler);
            scheduler.Phase = GameState.BuildPhase;
            registry.GlobalSkill!.SetTurn(0);
            Assert.True(registry.GlobalSkill.TryActivateGlobalSkill(0));
            Assert.Equal(0, registry.GlobalSkill.RejectedActivationCount);
            Assert.Equal(0, registry.GlobalSkill.RejectedCandidateCount);
            Assert.Equal(0, registry.GlobalSkill.RejectedInputCount);
            Assert.Equal(1, registry.GlobalSkill.SuccessfulActivationCount);
            Assert.Equal(5f, Store.PlayerGlobalSkillCooldown[playerId * 8]);
        }

        [Fact]
        public void BuildPhase_AutoSkillCombatFailureHasNoSuccessAccounting()
        {
            // Bug 回归：AutoSkill 的 Build 战斗候选拒绝不得记入成功计数或日志。
            int playerId = Player();
            Config.Levels.Clear();
            Config.AutoSkill.Enabled = true;
            Config.AutoSkill.MaxSkillsPerPhase = Config.Skills.Count;
            foreach (SkillConfig skill in Config.Skills)
            {
                skill.AutoCast = false;
                skill.AreaShape = "circle";
            }
            var registry = CreateRegistry(new RecordingEventBus());
            var scheduler = new FrameScheduler(Store, Config);
            registry.AssignToGroups(scheduler);
            scheduler.Phase = GameState.BuildPhase;
            scheduler.Build.Mana = null;
            scheduler.Build.ManaShield = null;
            scheduler.Tick(1f, 0);
            Assert.Equal(Config.Skills.Count, registry.AutoSkill!.RejectedCandidateCount);
            Assert.Equal(0, registry.AutoSkill.SuccessfulCastCount);
            Assert.Equal(SkillDamageRejectReason.PhaseNotAllowed, registry.AutoSkill.LastRejectReason);
            Assert.DoesNotContain(Renderer.Logs, log => log.Contains("[AUTOSKILL]", StringComparison.Ordinal));
            Assert.Equal(0f, Store.GetAbility(playerId, 0).CurrentCooldown);
        }

        [Fact]
        public void BuildPhase_GlobalSkillSkipsCombatSlotForResourceCandidate()
        {
            // Bug 回归：GlobalSkill slot 0 战斗技能被拒绝时，后续资源技能不得饿死。
            int playerId = Player(p => { p.Health = 100f; });
            Config.Levels.Clear();
            Config.GlobalSkills.Add(new GlobalSkillDef { Name = "Meteor", SkillType = (int)GlobalSkillType.MeteorStrike, ManaCost = 0f, Cooldown = 9f, DamagePct = 100f, MaxDamage = 1000f });
            Config.GlobalSkills.Add(new GlobalSkillDef { Name = "Emergency", SkillType = (int)GlobalSkillType.EmergencyHeal, ManaCost = 0f, Cooldown = 4f, HealPct = 0.5f });
            Store.PlayerCurrentHealth[playerId] = 40f;
            var registry = CreateRegistry(new RecordingEventBus());
            var scheduler = new FrameScheduler(Store, Config);
            registry.AssignToGroups(scheduler);
            scheduler.Phase = GameState.BuildPhase;
            registry.GlobalSkill!.SetTurn(0);
            Store.PlayerGlobalSkillPressed[playerId] = true;
            scheduler.Tick(1f, 0);
            Assert.Equal(60f, Store.PlayerCurrentHealth[playerId]);
            Assert.Equal(0f, Store.PlayerGlobalSkillCooldown[playerId * 8]);
            Assert.Equal(4f, Store.PlayerGlobalSkillCooldown[playerId * 8 + 1]);
            Assert.Equal(1, registry.GlobalSkill.RejectedActivationCount);
            Assert.Equal(1, registry.GlobalSkill.RejectedCandidateCount);
            Assert.Equal(0, registry.GlobalSkill.RejectedInputCount);
            Assert.Equal(1, registry.GlobalSkill.SuccessfulActivationCount);
            Assert.Equal(SkillDamageRejectReason.PhaseNotAllowed, registry.GlobalSkill.LastRejectReason);

            Store.PlayerGlobalSkillCooldown[playerId * 8 + 1] = 0f;
            Store.PlayerGlobalSkillPressed[playerId] = true;
            scheduler.Phase = GameState.WavePhase;
            int enemyId = Enemy(e => { e.Health = 10f; e.MaxHealth = 10f; e.GoldReward = 1; });
            scheduler.Tick(1f, 1);
            Assert.False(Store.EnemyActive[enemyId]);
            Assert.Equal(9f, Store.PlayerGlobalSkillCooldown[playerId * 8]);
        }

        [Fact]
        public void PhaseContextMatrix_AllNonWaveContextsRejectCombat()
        {
            // Bug 回归：只有 WavePhase 允许 combat，过渡/终态不得由布尔推断放行。
            int playerId = Player();
            ConfigureDamageSkill();
            var registry = CreateRegistry(new RecordingEventBus());
            registry.Skill!.SetTurn(0);
            Enemy(e => { e.X = 1f; e.Y = 0f; e.Health = 100f; e.MaxHealth = 100f; });
            var scheduler = new FrameScheduler(Store, Config);
            registry.AssignToGroups(scheduler);
            GameState[] rejected =
            {
                GameState.Init, GameState.BuildPhase, GameState.Intermission,
                GameState.BranchSelection, GameState.LevelComplete,
                GameState.GameOver, GameState.Victory
            };
            foreach (GameState phase in rejected)
            {
                scheduler.Phase = phase;
                bool cast = registry.Skill.CastSkill(Config.Skills[0].Name);
                Assert.False(cast);
                Assert.Equal(0, registry.Skill.PendingSkillDamageCount);
            }
            scheduler.Phase = GameState.WavePhase;
            var ready = Store.GetAbility(playerId, 0);
            ready.CurrentCooldown = 0f;
            Store.SetAbility(playerId, 0, ready);
            Assert.True(registry.Skill.CastSkill(Config.Skills[0].Name));
            Assert.Equal(rejected.Length, registry.Skill.RejectedAbilityCount);
        }

        [Fact]
        public void StateMachineBindingSynchronizesEveryProductionState()
        {
            // Bug 回归：生产状态机的每个状态都必须同步到所有能力系统。
            var scheduler = new FrameScheduler(Store, Config);
            var stateMachine = new StateMachine();
            Player();
            var registry = new SystemRegistry();
            registry.CreateAll(Store, Config, Renderer, 0, stateMachine, new RecordingEventBus());
            registry.WireDependencies(Store, 0);
            registry.AssignToGroups(scheduler);
            scheduler.BindStateMachine(stateMachine);
            Assert.Equal(GameState.Init, scheduler.Phase);
            AssertSystemsUse(PhaseContextKind.Init);
            Assert.True(stateMachine.TransitionTo(GameState.BuildPhase));
            Assert.Equal(GameState.BuildPhase, scheduler.Phase);
            AssertSystemsUse(PhaseContextKind.Build);
            Assert.True(stateMachine.TransitionTo(GameState.WavePhase));
            Assert.Equal(GameState.WavePhase, scheduler.Phase);
            AssertSystemsUse(PhaseContextKind.Wave);
            Assert.True(stateMachine.TransitionTo(GameState.Intermission));
            Assert.Equal(GameState.Intermission, scheduler.Phase);
            AssertSystemsUse(PhaseContextKind.Intermission);
            Assert.True(stateMachine.TransitionTo(GameState.BranchSelection));
            Assert.Equal(GameState.BranchSelection, scheduler.Phase);
            AssertSystemsUse(PhaseContextKind.BranchSelection);
            Assert.True(stateMachine.TransitionTo(GameState.WavePhase));
            Assert.True(stateMachine.TransitionTo(GameState.LevelComplete));
            Assert.Equal(GameState.LevelComplete, scheduler.Phase);
            AssertSystemsUse(PhaseContextKind.LevelComplete);
            Assert.True(stateMachine.TransitionTo(GameState.BuildPhase));
            Assert.True(stateMachine.TransitionTo(GameState.Victory));
            Assert.Equal(GameState.Victory, scheduler.Phase);
            AssertSystemsUse(PhaseContextKind.Victory);
            Assert.True(stateMachine.TransitionTo(GameState.GameOver));
            Assert.Equal(GameState.GameOver, scheduler.Phase);
            AssertSystemsUse(PhaseContextKind.GameOver);

            void AssertSystemsUse(PhaseContextKind expected)
            {
                Assert.Equal(expected, registry.Skill!.CurrentPhaseContext);
                Assert.Equal(expected, registry.GlobalSkill!.CurrentPhaseContext);
                Assert.Equal(expected, registry.HeroSkill!.CurrentPhaseContext);
                Assert.Equal(expected, registry.TowerActiveSkill!.CurrentPhaseContext);
            }
        }

        [Theory]
        [InlineData(GameState.Intermission)]
        [InlineData(GameState.BranchSelection)]
        [InlineData(GameState.LevelComplete)]
        [InlineData(GameState.GameOver)]
        [InlineData(GameState.Victory)]
        [InlineData(GameState.Init)]
        [InlineData(GameState.BuildPhase)]
        public void NonWaveTickRejectsWaveQueueBeforeNextWave(GameState nonWavePhase)
        {
            // Bug 回归：Wave 已入队请求不得跨 Intermission 到下一 Wave。
            var events = new RecordingEventBus();
            int playerId = Player(p => { p.X = 0f; p.Y = 0f; });
            ConfigureDamageSkill();
            int enemyId = Enemy(e => { e.X = 1f; e.Y = 0f; e.Health = 100f; e.MaxHealth = 100f; e.GoldReward = 7; });
            var registry = CreateRegistry(events);
            registry.Skill!.SetTurn(0);
            var scheduler = new FrameScheduler(Store, Config, events);
            registry.AssignToGroups(scheduler);
            scheduler.Phase = GameState.WavePhase;
            Assert.True(registry.Skill.CastSkill(Config.Skills[0].Name));
            Assert.Equal(1, registry.Skill.PendingSkillDamageCount);
            registry.GlobalSkill!.SetTurn(0);
            Store.PlayerGlobalSkillPressed[playerId] = true;
            Store.DamageResolver.EnableDeferred(true);
            Store.ResourceResolver.EnableDeferred(true);
            Assert.True(Store.DamageResolver.TryApply(new DamageRequest(
                Store.GetEntityHandle(playerId), Store.GetEntityHandle(enemyId), 3f,
                DamageType.True, ElementType.None, DamageFlags.None, DamageAmountStage.Raw,
                DamageCommitBoundary.GameplayResolve, 901L, ownerPlayerId: playerId)).Deferred);
            float manaBefore = Store.PlayerMana[playerId];
            Assert.True(Store.ResourceResolver.TryApply(new ResourceRequest(
                Store.GetEntityHandle(playerId), Store.GetEntityHandle(playerId),
                new AttributeKey(7), 2f, 902L, ownerPlayerId: playerId)).Deferred);
            Assert.Equal(1, Store.DamageResolver.PendingRequestCount);
            Assert.Equal(1, Store.ResourceResolver.PendingRequestCount);
            int damageFactCount = CountDamageFacts(enemyId);

            scheduler.Phase = nonWavePhase;
            Assert.Equal(0, registry.Skill.PendingSkillDamageCount);
            Assert.Equal(1, registry.Skill.RejectedSkillDamageCount);
            Assert.Equal(0, registry.Skill.ConsumedSkillDamageCount);
            Assert.False(Store.PlayerGlobalSkillPressed[playerId]);
            Assert.Equal(1, registry.GlobalSkill.RejectedActivationCount);
            Assert.Equal(0, registry.GlobalSkill.RejectedCandidateCount);
            Assert.Equal(1, registry.GlobalSkill.RejectedInputCount);
            Assert.Equal(0, Store.DamageResolver.PendingRequestCount);
            Assert.Equal(0, Store.ResourceResolver.PendingRequestCount);
            Assert.Equal(manaBefore, Store.PlayerMana[playerId]);
            Assert.Equal(100f, Store.EnemyHealth[enemyId]);
            Assert.Equal(0, Store.TotalKills);
            Assert.Equal(0f, Store.GetPlayerGold(playerId));
            Assert.Equal(damageFactCount, CountDamageFacts(enemyId));
            Assert.Empty(events.Events);

            scheduler.Phase = GameState.WavePhase;
            scheduler.Tick(1f, 1);
            Assert.Equal(100f, Store.EnemyHealth[enemyId]);
            Assert.Equal(0, Store.TotalKills);
            Assert.Equal(damageFactCount, CountDamageFacts(enemyId));
        }

        [Fact]
        public void UnboundResourceAndPassiveEntrypointsFailSafe()
        {
            // Bug 回归：未绑定不等价于 Build allowlist。
            int caster = Player(p => { p.Health = 100f; });
            int ally = Player(p => { p.EntityId = 1; p.Health = 100f; });
            Store.PlayerCurrentHealth[ally] = 20f;
            var skill = new SkillSystem(Store, Renderer, caster, Config);
            Assert.Equal(0, skill.CastChainHealPublic(50f, 0f, 0f, 100, "chain-heal", 0f, 0f));
            Assert.Equal(20f, Store.PlayerCurrentHealth[ally]);
            Assert.Equal(1, skill.RejectedAbilityCount);

            Config.Skills[0].AutoCast = true;
            skill.InitializePlayerSkills();
            skill.SetTurn(0);
            skill.Update(1f);
            Assert.Equal(2, skill.RejectedAbilityCount);
            Assert.Equal(0, skill.PendingSkillDamageCount);

            Config.GlobalSkills.Add(new GlobalSkillDef { Name = "Emergency", SkillType = (int)GlobalSkillType.EmergencyHeal, ManaCost = 0f, Cooldown = 5f, HealPct = 0.5f });
            Config.GlobalSkills.Add(new GlobalSkillDef { Name = "Gold", SkillType = (int)GlobalSkillType.GoldBurst, ManaCost = 0f, Cooldown = 5f, GoldAmount = 25f });
            var global = new GlobalSkillSystem(Store, Config, Renderer, caster);
            global.SetTurn(0);
            float hpBefore = Store.PlayerCurrentHealth[caster];
            float goldBefore = Store.GetPlayerGold(caster);
            Assert.False(global.TryActivateGlobalSkill(0));
            Assert.False(global.TryActivateGlobalSkill(1));
            Assert.Equal(hpBefore, Store.PlayerCurrentHealth[caster]);
            Assert.Equal(goldBefore, Store.GetPlayerGold(caster));
            Assert.Equal(0f, Store.PlayerGlobalSkillCooldown[caster * 8]);
            Assert.Equal(0f, Store.PlayerGlobalSkillCooldown[caster * 8 + 1]);
            Assert.Equal(2, global.RejectedActivationCount);
            Assert.Equal(2, global.RejectedCandidateCount);
            Assert.Equal(0, global.RejectedInputCount);
        }

        [Fact]
        public void TowerActiveRequiresWaveContextWithoutCooldownSideEffects()
        {
            // Bug 回归：塔主动技能在未绑定或 Build 上下文拒绝时不得启动冷却。
            int towerId = RawTower(0, 0);
            Store.SetTowerActiveSkill(towerId, 0, 6f);
            var system = new TowerActiveSkillSystem(Store, Config);
            Assert.False(system.TriggerTowerActive(towerId));
            Assert.Equal(0f, Store.GetTowerActiveCooldown(towerId));
            system.SetPhaseContext(new PhaseContext(PhaseContextKind.Build));
            Assert.False(system.TriggerTowerActive(towerId));
            Assert.Equal(0f, Store.GetTowerActiveCooldown(towerId));
            system.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            Assert.False(system.TriggerTowerActive(towerId));
            Assert.Equal(0f, Store.GetTowerActiveCooldown(towerId));
        }

        [Fact]
        public void UnboundAutoSkillResourceCandidateIsRejectedWithoutSuccess()
        {
            // Bug 回归：未绑定 AutoSkill 即使命中资源白名单也必须无副作用拒绝。
            int playerId = Player(p => { p.Health = 100f; });
            Config.AutoSkill.Enabled = true;
            Config.AutoSkill.MaxSkillsPerPhase = 1;
            Config.Skills[0].AreaShape = "heal";
            Config.Skills[0].HealPercent = 0.5f;
            var skill = new SkillSystem(Store, Renderer, playerId, Config);
            skill.InitializePlayerSkills();
            for (int slot = 1; slot < Store.AbilityCount[playerId]; slot++)
            {
                var blocked = Store.GetAbility(playerId, slot);
                blocked.CurrentCooldown = 100f;
                Store.SetAbility(playerId, slot, blocked);
            }
            var auto = new AutoSkillSystem(Store, Renderer, playerId, skill, Config.AutoSkill);
            Store.PlayerCurrentHealth[playerId] = 40f;
            auto.Update(allowCombat: false);
            Assert.Equal(40f, Store.PlayerCurrentHealth[playerId]);
            Assert.Equal(0, auto.SuccessfulCastCount);
            Assert.Equal(1, auto.RejectedCandidateCount);
            Assert.Equal(SkillDamageRejectReason.PhaseNotAllowed, auto.LastRejectReason);
            Assert.Equal(0f, Store.GetAbility(playerId, 0).CurrentCooldown);
        }

        [Fact]
        public void ProductionBenchmarkCompositionRegistersSkillPhaseContext()
        {
            // Bug 回归：完整局压测必须通过生产组合入口注册 SkillSystem。
            int playerId = Player();
            var runtime = BenchmarkCompositionFactory.Create(Store, Config, Renderer, playerId);
            var skill = Assert.IsType<SkillSystem>(runtime.Registry.Skill);

            Assert.Same(skill, runtime.Scheduler.Build.Skill);
            Assert.Same(skill, runtime.Scheduler.CombatSetup.Skill);
            Assert.Same(skill, runtime.Scheduler.SkillBuff.Skill);
            Assert.Equal(GameState.Init,runtime.StateMachine.CurrentState);
            Assert.Equal(PhaseContextKind.Init,skill.CurrentPhaseContext);
            Assert.True(runtime.StateMachine.TransitionTo(GameState.BuildPhase));
            Assert.Equal(PhaseContextKind.Build, skill.CurrentPhaseContext);
            Assert.True(runtime.StateMachine.TransitionTo(GameState.WavePhase));
            Assert.Equal(PhaseContextKind.Wave, skill.CurrentPhaseContext);
        }

        [Fact]
        public void PhaseContextWritersAreAssemblyInternal()
        {
            // Bug 回归：生产调用方不能绕过 FrameScheduler 单独伪造能力系统阶段。
            Type[] systems =
            {
                typeof(SkillSystem), typeof(GlobalSkillSystem),
                typeof(HeroSkillSystem), typeof(TowerActiveSkillSystem)
            };

            foreach (Type type in systems)
            {
                var publicWriter = type.GetMethod("SetPhaseContext", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                var internalWriter = type.GetMethod("SetPhaseContext", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.Null(publicWriter);
                Assert.NotNull(internalWriter);
                Assert.True(internalWriter!.IsAssembly);
            }
        }

        [Fact]
        public void GameManagerInitializationBindsStateMachineToAbilitySystems()
        {
            // Bug 回归：必须经过 GameManager.Initialize 的真实组合绑定状态机。
            var manager = new GameManager();
            manager.Initialize();

            Assert.Equal(GameState.Init, manager.StateMachineDiagnostics.CurrentState);
            Assert.Equal(GameState.Init, manager.SchedulerDiagnostics.Phase);
            AssertManagerSystemsUse(PhaseContextKind.Init);
            Assert.True(manager.StateMachineDiagnostics.TransitionTo(GameState.BuildPhase));
            Assert.Equal(GameState.BuildPhase, manager.SchedulerDiagnostics.Phase);
            AssertManagerSystemsUse(PhaseContextKind.Build);
            Assert.True(manager.StateMachineDiagnostics.TransitionTo(GameState.WavePhase));
            Assert.Equal(GameState.WavePhase, manager.SchedulerDiagnostics.Phase);
            AssertManagerSystemsUse(PhaseContextKind.Wave);

            void AssertManagerSystemsUse(PhaseContextKind expected)
            {
                Assert.Equal(expected, manager.RegistryDiagnostics.Skill!.CurrentPhaseContext);
                Assert.Equal(expected, manager.RegistryDiagnostics.GlobalSkill!.CurrentPhaseContext);
                Assert.Equal(expected, manager.RegistryDiagnostics.HeroSkill!.CurrentPhaseContext);
                Assert.Equal(expected, manager.RegistryDiagnostics.TowerActiveSkill!.CurrentPhaseContext);
            }
        }

        private void ConfigureDamageSkill()
        {
            Config.Skills[0].AutoCast = false;
            Config.Skills[0].DamageMultiplier = 1f;
            Config.Skills[0].AreaShape = "circle";
            Config.Skills[0].AreaRadius = 10;
            Config.Skills[0].Cooldown = 0f;
            Config.Skills[0].ManaCost = 10f;
        }

        private int CountDamageFacts(int targetId)
        {
            int count = 0;
            for (int i = 0; i < Store.DamageResolver.Events.Count; i++)
            {
                GameplayEventType type = Store.DamageResolver.Events.Get(i).Type;
                if ((type == GameplayEventType.HitConfirmed || type == GameplayEventType.DamageApplied) &&
                    Store.DamageResolver.Events.Get(i).Target.Index == targetId)
                    count++;
            }
            return count;
        }

        private SystemRegistry CreateRegistry(IBattleEventBus events)
        {
            var registry = new SystemRegistry();
            registry.CreateAll(Store, Config, Renderer, 0, new StateMachine(), events);
            registry.WireDependencies(Store, 0);
            registry.Skill!.InitializePlayerSkills();
            return registry;
        }

        private sealed class RecordingEventBus : IBattleEventBus
        {
            public List<string> Events { get; } = new List<string>();
            public void OnEntityCreated(int entityId, float x, float y, string entityType) => Events.Add("created");
            public void OnTowerCreated(int entityId, float x, float y, TowerType towerType) { }
            public void OnEntityDestroyed(int entityId) => Events.Add("destroyed");
            public void OnPositionChanged(int entityId, float x, float y) { }
            public void OnPositionsChanged(List<(int entityId, float x, float y)> changes) { }
            public void OnDamageDealt(int targetId, float amount, string damageType, bool isCritical) => Events.Add("damage");
            public void OnEntityKilled(int entityId, int killerId) => Events.Add("killed");
            public void OnProjectileFired(float fromX, float fromY, float toX, float toY, float speed) { }
            public void OnWaveStarted(int waveNumber) => Events.Add("wave");
            public void OnGameOver(bool victory) => Events.Add("gameover");
        }
    }
}
