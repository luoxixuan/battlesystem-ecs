using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class FrameGraphTests : BattleTestBase
    {
        [Fact]
        public void StableNodeIdTopologyAndHashIgnoreRegistrationOrder()
        {
            // Bug 回归：相同节点集合不得因注册顺序改变拓扑或 hash。
            FrameNodeAdapter root=Node("root");
            FrameNodeAdapter alpha=Node("alpha",after:new[]{(FrameNodeId)"root"});
            FrameNodeAdapter beta=Node("beta",after:new[]{(FrameNodeId)"root"});
            FrameNodeAdapter tail=Node("tail",after:new[]{(FrameNodeId)"alpha",(FrameNodeId)"beta"});
            FrameGraph first=Build(root,beta,tail,alpha);
            FrameGraph second=Build(tail,alpha,root,beta);
            string[] expected={"root","alpha","beta","tail"};
            Assert.Equal(expected,first.Nodes.Select(n=>n.Metadata.Id.Value));
            Assert.Equal(expected,second.Nodes.Select(n=>n.Metadata.Id.Value));
            Assert.Equal(first.TopologyHash,second.TopologyHash);
        }

        [Fact]
        public void TopologyHashIncludesBindingRequiredAndOptionalSemantics()
        {
            // Bug 回归：任何影响 composition 的稳定身份或依赖 presence 都必须改变快照。
            FrameGraph baseline=BuildMetadataVariant("binding.one","review.one","RequiredA",OptionalDependencyPolicy.NoOp,true,FrameGraphCompositionKind.Direct);
            FrameGraph bindingChanged=BuildMetadataVariant("binding.two","review.one","RequiredA",OptionalDependencyPolicy.NoOp,true,FrameGraphCompositionKind.Direct);
            FrameGraph reviewChanged=BuildMetadataVariant("binding.one","review.two","RequiredA",OptionalDependencyPolicy.NoOp,true,FrameGraphCompositionKind.Direct);
            FrameGraph requiredChanged=BuildMetadataVariant("binding.one","review.one","RequiredB",OptionalDependencyPolicy.NoOp,true,FrameGraphCompositionKind.Direct);
            FrameGraph optionalChanged=BuildMetadataVariant("binding.one","review.one","RequiredA",OptionalDependencyPolicy.Fail,true,FrameGraphCompositionKind.Direct);
            FrameGraph disabledChanged=BuildMetadataVariant("binding.one","review.one","RequiredA",OptionalDependencyPolicy.Disabled,true,FrameGraphCompositionKind.Direct);
            FrameGraph dependencyMissing=BuildMetadataVariant("binding.one","review.one","RequiredA",OptionalDependencyPolicy.NoOp,false,FrameGraphCompositionKind.Direct);
            FrameGraph compositionChanged=BuildMetadataVariant("binding.one","review.one","RequiredA",OptionalDependencyPolicy.NoOp,true,FrameGraphCompositionKind.ManualBenchmark);
            Assert.NotEqual(baseline.TopologyHash,bindingChanged.TopologyHash);
            Assert.NotEqual(baseline.TopologyHash,reviewChanged.TopologyHash);
            Assert.NotEqual(baseline.TopologyHash,requiredChanged.TopologyHash);
            Assert.NotEqual(baseline.TopologyHash,optionalChanged.TopologyHash);
            Assert.NotEqual(baseline.TopologyHash,disabledChanged.TopologyHash);
            Assert.NotEqual(baseline.TopologyHash,dependencyMissing.TopologyHash);
            Assert.NotEqual(baseline.TopologyHash,compositionChanged.TopologyHash);
            Assert.Equal(FrameGraphCompositionKind.ManualBenchmark,compositionChanged.CompositionKind);
        }

        [Fact]
        public void ProductionBuilderRejectsUnreviewedAccessProfile()
        {
            // Bug 回归：仅设置 SourceReviewed 枚举不能伪造一次可追踪的源码审阅。
            var metadata=new FrameNodeMetadata("unreviewed",FramePhaseMask.Wave,FrameTimeDomain.None,
                FrameExecutionSemantics.SerialUpdate,bindingId:"binding",owner:"owner",
                evidence:FrameAccessEvidence.SourceReviewed);
            var builder=new FrameGraphBuilder(FrameGraphCompositionKind.ProductionRegistry).RequireReviewedProfiles()
                .Add(new FrameNodeAdapter(metadata,new DelegateSystem(c=>{})));
            Assert.Contains("source-reviewed",Assert.Throws<FrameGraphValidationException>(()=>builder.BuildAndSeal()).Message,StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void OptionalDependencyPresenceChangesProductionTopologyHash()
        {
            // Bug 回归：Pathfinding 存在与缺失不能生成相同的 composition hash。
            var missingStore=new ComponentStore();
            var missing=new FrameScheduler(missingStore,Config);
            missing.SealGraphComposition();
            var presentStore=new ComponentStore();
            var present=new FrameScheduler(presentStore,Config);
            present.SetPathfindingSystem(new Systems.PathfindingSystem(presentStore));
            present.SealGraphComposition();
            Assert.NotEqual(missing.FrameGraphTopologyHash,present.FrameGraphTopologyHash);
        }

        [Fact]
        public void DuplicateNodeFailsWithStableId()
        {
            // Bug 回归：重复 NodeId 必须在 composition 阶段给出稳定诊断。
            var error=Assert.Throws<FrameGraphValidationException>(()=>Build(Node("same"),Node("same")));
            Assert.Contains("same",error.Message,StringComparison.Ordinal);
        }

        [Fact]
        public void MissingRequiredDependencyFailsWithDiagnostic()
        {
            // Bug 回归：required dependency 缺失不能拖到首帧才失败。
            var builder=new FrameGraphBuilder().Add(Node("consumer",required:new[]{"MissingRuntime"}));
            var error=Assert.Throws<FrameGraphValidationException>(()=>builder.BuildAndSeal());
            Assert.Contains("MissingRuntime",error.Message,StringComparison.Ordinal);
        }

        [Fact]
        public void DefaultNodeIdAndEmptyDependencyNamesFailWithDiagnostics()
        {
            // Bug 回归：默认 NodeId 与空依赖名都必须被 validator 拒绝。
            var missingId=new FrameNodeAdapter(new FrameNodeMetadata(default,FramePhaseMask.Wave,FrameTimeDomain.None,
                FrameExecutionSemantics.SerialUpdate),new DelegateSystem(c=>{}));
            Assert.Contains("id cannot be empty",Assert.Throws<FrameGraphValidationException>(()=>Build(missingId)).Message,StringComparison.OrdinalIgnoreCase);
            Assert.Contains("empty required dependency",Assert.Throws<FrameGraphValidationException>(()=>
                new FrameGraphBuilder().Add(Node("required",required:new[]{" "})).BuildAndSeal()).Message,StringComparison.OrdinalIgnoreCase);
            Assert.Contains("empty optional dependency",Assert.Throws<FrameGraphValidationException>(()=>
                new FrameGraphBuilder().Add(Node("optional",optional:default(OptionalFrameDependency))).BuildAndSeal()).Message,StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ReaderWithoutReachableWriterFails()
        {
            // Bug 回归：reader 不能依赖拓扑上不可达的 writer。
            FrameNodeAdapter reader=Node("reader",reads:new[]{FrameResource.GameplayEvents});
            var error=Assert.Throws<FrameGraphValidationException>(()=>Build(reader));
            Assert.Contains("without a reachable writer",error.Message,StringComparison.OrdinalIgnoreCase);
            Assert.Contains("GameplayEvents",error.Message,StringComparison.Ordinal);
        }

        [Fact]
        public void PersistentSoaReaderRequiresPublishedWriterButExternalInputDoesNot()
        {
            // Bug 回归：普通 SOA 状态不能再被 broad external-seed allowlist 绕过 writer 校验。
            var missingWriter=Assert.Throws<FrameGraphValidationException>(()=>
                Build(Node("health-reader",reads:new[]{FrameResource.EnemyHealth})));
            Assert.Contains("EnemyHealth",missingWriter.Message,StringComparison.Ordinal);
            Assert.Equal(FrameResourceOrigin.PersistentState,FrameGraphValidator.ResourceOrigin(FrameResource.EnemyHealth));

            // Bug 回归：持久状态必须经过真实发布令牌，不得伪装成 Health writer。
            FrameNodeAdapter publisher=Node("publish",reads:new[]{FrameResource.EnemyHealth},
                writes:new[]{FrameResource.PersistentStatePublished});
            FrameNodeAdapter publishedReader=Node("published-reader",reads:new[]{FrameResource.EnemyHealth},
                after:new[]{(FrameNodeId)"publish"});
            FrameGraph published=Build(publishedReader,publisher);
            Assert.Equal(new[]{"publish","published-reader"},published.Nodes.Select(n=>n.Metadata.Id.Value));

            // Bug 回归：真正帧外注入的阶段状态允许由首个节点读取。
            FrameGraph external=Build(Node("phase-reader",reads:new[]{FrameResource.PhaseState}));
            Assert.Single(external.Nodes);
            Assert.Equal(FrameResourceOrigin.ExternalInput,FrameGraphValidator.ResourceOrigin(FrameResource.PhaseState));
        }

        [Fact]
        public void ProductionPersistentPublicationAdvancesFrameTokenWithoutMutatingSoaState()
        {
            // Bug 回归：publish 是可观察的帧发布屏障，不得伪写 EnemyHealth。
            int playerId=Player();
            int enemyId=Enemy(e=>e.Health=37f);
            var registry=new SystemRegistry();
            registry.CreateAll(Store,Config,Renderer,playerId,new StateMachine());
            registry.WireDependencies(Store,playerId);
            var scheduler=new FrameScheduler(Store,Config);
            registry.AssignToGroups(scheduler);
            scheduler.Phase=GameState.BuildPhase;

            scheduler.Tick(0.016f,0);
            Assert.Equal(0,scheduler.LastPublishedPersistentFrame);
            Assert.Equal(37f,Store.EnemyHealth[enemyId]);
            scheduler.Tick(0.016f,1);
            Assert.Equal(1,scheduler.LastPublishedPersistentFrame);
            Assert.Equal(37f,Store.EnemyHealth[enemyId]);
        }

        [Fact]
        public void WriterAfterReaderDoesNotSatisfyRead()
        {
            // Bug 回归：排在 reader 之后的 writer 不能满足本次读取。
            FrameNodeAdapter reader=Node("reader",reads:new[]{FrameResource.GameplayEvents},after:new[]{(FrameNodeId)"root"});
            FrameNodeAdapter writer=Node("writer",writes:new[]{FrameResource.GameplayEvents},after:new[]{(FrameNodeId)"reader"});
            var error=Assert.Throws<FrameGraphValidationException>(()=>Build(Node("root"),writer,reader));
            Assert.Contains("reader",error.Message,StringComparison.Ordinal);
            Assert.Contains("GameplayEvents",error.Message,StringComparison.Ordinal);
        }

        [Fact]
        public void UnorderedSharedWritersFailUsingProductionResourceVocabulary()
        {
            // Bug 回归：共享资源的多个 writer 必须具有明确可达顺序。
            FrameNodeAdapter left=Node("left",writes:new[]{FrameResource.EnemyHealth});
            FrameNodeAdapter right=Node("right",writes:new[]{FrameResource.EnemyHealth});
            var error=Assert.Throws<FrameGraphValidationException>(()=>Build(right,left));
            Assert.Contains("unordered shared writers",error.Message,StringComparison.OrdinalIgnoreCase);
            Assert.Contains("EnemyHealth",error.Message,StringComparison.Ordinal);
        }

        [Fact]
        public void SingleWriterCommitResourceRejectsEvenOrderedSecondWriter()
        {
            // Bug 回归：single-writer commit 资源即使有顺序也只能有一个 owner。
            FrameNodeAdapter first=Node("first",writes:new[]{FrameResource.DamageCommitted});
            FrameNodeAdapter second=Node("second",writes:new[]{FrameResource.DamageCommitted},after:new[]{(FrameNodeId)"first"});
            var error=Assert.Throws<FrameGraphValidationException>(()=>Build(first,second));
            Assert.Contains("Single-writer",error.Message,StringComparison.OrdinalIgnoreCase);
            Assert.Contains("DamageCommitted",error.Message,StringComparison.Ordinal);
        }

        [Fact]
        public void CycleFailsWithParticipatingNodes()
        {
            // Bug 回归：cycle 诊断必须列出参与节点，便于定位生产接线。
            var error=Assert.Throws<FrameGraphValidationException>(()=>Build(
                Node("a",after:new[]{(FrameNodeId)"b"}),Node("b",after:new[]{(FrameNodeId)"a"})));
            Assert.Contains("cycle",error.Message,StringComparison.OrdinalIgnoreCase);
            Assert.Contains("a",error.Message,StringComparison.Ordinal);
            Assert.Contains("b",error.Message,StringComparison.Ordinal);
        }

        [Fact]
        public void BeforeAndAfterEdgesBothParticipateInStableTopology()
        {
            // Bug 回归：Before 与 After 必须共同参与稳定拓扑排序。
            FrameNodeAdapter alpha=Node("alpha",before:new[]{(FrameNodeId)"middle"});
            FrameNodeAdapter middle=Node("middle",after:new[]{(FrameNodeId)"root"});
            FrameGraph graph=Build(middle,alpha,Node("root"));
            Assert.Equal(new[]{"alpha","root","middle"},graph.Nodes.Select(n=>n.Metadata.Id.Value));
        }

        [Fact]
        public void UnknownResourceFailsAtValidationBoundary()
        {
            // Bug 回归：未知资源位不得进入 production graph。
            var error=Assert.Throws<FrameGraphValidationException>(()=>Build(Node("invalid-resource",writes:new[]{(FrameResource)999})));
            Assert.Contains("invalid frame resource",error.Message,StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(FramePhaseMask.None,FrameTimeDomain.Real)]
        [InlineData((FramePhaseMask)1024,FrameTimeDomain.Real)]
        [InlineData(FramePhaseMask.Build|FramePhaseMask.Wave,FrameTimeDomain.Enemy)]
        [InlineData(FramePhaseMask.Build|FramePhaseMask.Wave,FrameTimeDomain.Combat)]
        [InlineData(FramePhaseMask.Build|FramePhaseMask.Wave,FrameTimeDomain.Build)]
        [InlineData(FramePhaseMask.Wave,FrameTimeDomain.Real|FrameTimeDomain.Enemy)]
        [InlineData(FramePhaseMask.Wave,(FrameTimeDomain)128)]
        public void IllegalPhaseTimeCombinationFails(FramePhaseMask phases,FrameTimeDomain domain)
        {
            // Bug 回归：非法 phase/time mask 组合必须在 Seal 前失败。
            var error=Assert.Throws<FrameGraphValidationException>(()=>Build(Node("illegal",phases,domain)));
            Assert.Contains("illegal",error.Message,StringComparison.Ordinal);
        }

        [Fact]
        public void OptionalDependencyPoliciesAreObservable()
        {
            // Bug 回归：optional dependency 的 NoOp、Disabled、Fail 策略必须可观察。
            int noOpRuns=0;
            var builder=new FrameGraphBuilder()
                .Add(Node("no-op",optional:new OptionalFrameDependency("MissingA",OptionalDependencyPolicy.NoOp),execute:c=>noOpRuns++))
                .Add(Node("disabled",optional:new OptionalFrameDependency("MissingB",OptionalDependencyPolicy.Disabled)));
            FrameGraph graph=builder.BuildAndSeal();
            graph.Execute(new FrameExecutionContext(Store,1f,1,1,new PhaseContext(PhaseContextKind.Wave)));
            Assert.Equal(1,noOpRuns);
            Assert.DoesNotContain(graph.Nodes,n=>n.Metadata.Id.Value=="disabled");
            var fail=new FrameGraphBuilder().Add(Node("fail",optional:new OptionalFrameDependency("MissingC",OptionalDependencyPolicy.Fail)));
            Assert.Contains("MissingC",Assert.Throws<FrameGraphValidationException>(()=>fail.BuildAndSeal()).Message,StringComparison.Ordinal);
        }

        [Fact]
        public void NodeReceivesOnlyItsDeclaredDelta()
        {
            // Bug 回归：节点只能取得 metadata 声明的时间域 delta。
            float enemy=0f,combat=0f;
            FrameGraph graph=Build(
                Node("enemy",FramePhaseMask.Wave,FrameTimeDomain.Enemy,execute:c=>enemy=c.Delta),
                Node("combat",FramePhaseMask.Wave,FrameTimeDomain.Combat,execute:c=>combat=c.Delta));
            var frame=new FrameExecutionContext(Store,1f,1,1,new PhaseContext(PhaseContextKind.Wave));
            frame.FreezeTime(new TimeContext(1f,2f,3f,4f,5f,6f,7f,1,1,new PhaseContext(PhaseContextKind.Wave)));
            graph.Execute(frame);
            Assert.Equal(3f,enemy);
            Assert.Equal(4f,combat);
        }

        [Theory]
        [InlineData(FramePhaseMask.Init,PhaseContextKind.Init,FrameTimeDomain.Real,2f)]
        [InlineData(FramePhaseMask.Build,PhaseContextKind.Build,FrameTimeDomain.Build,6f)]
        [InlineData(FramePhaseMask.Wave,PhaseContextKind.Wave,FrameTimeDomain.Enemy,3f)]
        [InlineData(FramePhaseMask.Wave,PhaseContextKind.Wave,FrameTimeDomain.Combat,4f)]
        [InlineData(FramePhaseMask.Wave,PhaseContextKind.Wave,FrameTimeDomain.Effect,5f)]
        [InlineData(FramePhaseMask.Intermission,PhaseContextKind.Intermission,FrameTimeDomain.Global,7f)]
        [InlineData(FramePhaseMask.BranchSelection,PhaseContextKind.BranchSelection,FrameTimeDomain.Real,2f)]
        [InlineData(FramePhaseMask.LevelComplete,PhaseContextKind.LevelComplete,FrameTimeDomain.Real,2f)]
        [InlineData(FramePhaseMask.GameOver,PhaseContextKind.GameOver,FrameTimeDomain.Real,2f)]
        [InlineData(FramePhaseMask.Victory,PhaseContextKind.Victory,FrameTimeDomain.Real,2f)]
        [InlineData(FramePhaseMask.Other,PhaseContextKind.Unbound,FrameTimeDomain.None,0f)]
        public void EveryPhaseAndTimeDomainUsesDeclaredContext(FramePhaseMask mask,PhaseContextKind phase,FrameTimeDomain domain,float expected)
        {
            // Bug 回归：每个 phase/time domain 都必须消费统一 TimeContext。
            float observed=-1f;
            FrameGraph graph=Build(Node("probe",mask,domain,execute:c=>observed=c.Delta));
            var frame=new FrameExecutionContext(Store,1f,3,4,new PhaseContext(phase));
            frame.FreezeTime(new TimeContext(1f,2f,3f,4f,5f,6f,7f,3,4,new PhaseContext(phase)));
            graph.Execute(frame);
            Assert.Equal(expected,observed);
        }

        [Fact]
        public void TickBeforeCompositionSealFailsWithoutLazyBuild()
        {
            // Bug 回归：首个 Tick 不得隐式构图或分配 composition。
            var scheduler=new FrameScheduler(Store,Config);
            var error=Assert.Throws<InvalidOperationException>(()=>scheduler.Tick(1f,1));
            Assert.Contains("sealed",error.Message,StringComparison.OrdinalIgnoreCase);
            Assert.False(scheduler.IsCompositionSealed);
            scheduler.SealGraphComposition();
            scheduler.Tick(1f,2);
            Assert.Equal(0,scheduler.LastTimeContext.Frame);
        }

        [Fact]
        public void SchedulerModesAreFixedAndAdvanceTheSameTimeIdentity()
        {
            // Bug 回归：启动时选择的 graph/legacy 模式不能破坏 frame identity 连续性。
            using var graphWorld=new TestWorld();
            using var legacyWorld=new TestWorld();
            graphWorld.Player();
            legacyWorld.Player();
            graphWorld.Store.PlayerBulletTimeTurnsLeft[0]=2f;
            legacyWorld.Store.PlayerBulletTimeTurnsLeft[0]=2f;
            graphWorld.Store.PlayerBulletTimeScale[0]=0.25f;
            legacyWorld.Store.PlayerBulletTimeScale[0]=0.25f;
            var graph=new FrameScheduler(graphWorld.Store,graphWorld.Config,null,FrameSchedulerExecutionMode.Graph);
            var legacy=new FrameScheduler(legacyWorld.Store,legacyWorld.Config,null,FrameSchedulerExecutionMode.Legacy);
            graph.SealGraphComposition();
            legacy.SealGraphComposition();

            graph.Tick(1f,7);
            legacy.Tick(1f,7);
            AssertTimeIdentity(graph.LastTimeContext,legacy.LastTimeContext);
            graph.Tick(0.5f,8);
            legacy.Tick(0.5f,8);
            AssertTimeIdentity(graph.LastTimeContext,legacy.LastTimeContext);
            Assert.Equal(FrameSchedulerExecutionMode.Graph,graph.ExecutionMode);
            Assert.Equal(FrameSchedulerExecutionMode.Legacy,legacy.ExecutionMode);
            Assert.Null(typeof(FrameScheduler).GetProperty("UseLegacyExecutionForValidation",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic));
        }

        [Fact]
        public void EnemyEffectClockGraphAndLegacyTickEveryClockOnceWithSameDelta()
        {
            // Bug 回归：非 Combat effect clock 在 graph/legacy 中必须只推进一次。
            float graphHealth=RunEffectClockScenario(FrameSchedulerExecutionMode.Graph);
            float legacyHealth=RunEffectClockScenario(FrameSchedulerExecutionMode.Legacy);
            Assert.Equal(legacyHealth,graphHealth);
            Assert.Equal(95f,graphHealth,3);
        }

        [Fact]
        public void GameManagerExposesStartupOnlyRollbackSelectionWithGraphDefault()
        {
            // Bug 回归：回滚模式只能在启动 composition 时选择且默认使用 graph。
            var defaultManager=new GameManager();
            var legacyManager=new GameManager(FrameSchedulerExecutionMode.Legacy);
            Assert.Equal(FrameSchedulerExecutionMode.Graph,defaultManager.ConfiguredExecutionMode);
            Assert.Equal(FrameSchedulerExecutionMode.Legacy,legacyManager.ConfiguredExecutionMode);
            Assert.Throws<ArgumentOutOfRangeException>(()=>new GameManager((FrameSchedulerExecutionMode)99));
            Assert.Throws<ArgumentOutOfRangeException>(()=>new FrameScheduler(Store,Config,null,(FrameSchedulerExecutionMode)99));
        }

        [Fact]
        public void LegacyCompatibilityGroupsCannotOwnMutableTimeOrPhaseState()
        {
            // Bug 回归：legacy facade 不得重新持有可变时间或阶段真相源。
            string[] removedSkillBuffProperties={"GameplayEnemyDeltaTime","GameplayRealTimeDeltaTime","GameplayGlobalDeltaTime","GameplayClock"};
            Assert.All(removedSkillBuffProperties,name=>Assert.Null(typeof(SkillBuffGroup).GetProperty(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)));
            Assert.Null(typeof(PostDeathGroup).GetProperty("Phase",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic));
            MethodInfo? skillLegacy=typeof(SkillBuffGroup).GetMethod("ExecuteLegacy",BindingFlags.Instance|BindingFlags.NonPublic);
            MethodInfo? postDeathLegacy=typeof(PostDeathGroup).GetMethod("ExecuteLegacy",BindingFlags.Instance|BindingFlags.NonPublic);
            Assert.NotNull(skillLegacy);
            Assert.NotNull(postDeathLegacy);
            Assert.False(skillLegacy!.IsPublic);
            Assert.False(postDeathLegacy!.IsPublic);
            Assert.Null(typeof(FrameScheduler).GetProperty("GameplayClock",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic));
            PropertyInfo? effectClock=typeof(FrameScheduler).GetProperty("EffectClock",BindingFlags.Instance|BindingFlags.Public);
            Assert.NotNull(effectClock);
            Assert.False(effectClock!.CanWrite);
        }

        [Fact]
        public void LegacyGroupFacadesRemainCallableWithoutOwningSchedulerState()
        {
            // Bug 回归：旧 ISystemGroup facade 必须可调用，不能以异常代替兼容合同。
            int playerId=Player();
            int enemyId=Enemy(e=>{e.Health=20f;e.MaxHealth=20f;});
            var effect=new Core.GAS.GameplayEffectDefinition(new Core.GAS.EffectId(8190),
                Core.GAS.EffectType.Periodic,Array.Empty<Core.GAS.ModifierDefinition>(),2f,1f,
                Core.GAS.ClockId.Combat,Core.GAS.StackingBehavior.None,1,Core.GAS.RefreshPolicy.None,
                Core.GAS.SourceDeathPolicy.Persist,Core.GAS.EffectPayloadKind.Damage,default(Core.GAS.TagId),
                Array.Empty<Core.GAS.ExecutionId>(),periodicMagnitude:2f);
            Assert.True(Store.GameplayEffectsRuntime.TryApply(effect.Id,effect,Store.GetEntityHandle(playerId),
                Store.GetEntityHandle(enemyId),out _,ownerPlayerId:playerId));
            ((ISystemGroup)new SkillBuffGroup()).Execute(Store,1f,0);
            Assert.Equal(18f,Store.EnemyHealth[enemyId],3);

            Store.PlayerSoulCount[playerId]=0f;
            Store.PlayerSoulCap[playerId]=10f;
            Store.PlayerSoulRegen[playerId]=4f;
            var postDeath=new PostDeathGroup
            {
                SoulHarvest=new Systems.SoulHarvestSystem(Store,new Config.SoulHarvestConfig())
            };
            ((ISystemGroup)postDeath).Execute(Store,0.5f,0);
            Assert.Equal(2f,Store.PlayerSoulCount[playerId]);
        }

        [Fact]
        public void ProductionCompositionContainsSystemNodesRealResourcesAndParallelSemantics()
        {
            // Bug 回归：生产 Registry composition 必须包含完整真实节点、profile 与并行语义。
            Player();
            var registry=new SystemRegistry();
            registry.CreateAll(Store,Config,Renderer,0,new StateMachine());
            registry.WireDependencies(Store,0);
            var scheduler=new FrameScheduler(Store,Config);
            registry.AssignToGroups(scheduler);
            Assert.True(scheduler.IsCompositionSealed);
            Assert.Equal(FrameGraphCompositionKind.ProductionRegistry,scheduler.CompositionKind);
            // Bug 回归：删除或绕过任一真实生产节点时，composition 快照必须失败。
            Assert.Equal(FrameAccessReviewCatalog.ReviewedNodeCount-scheduler.FrameGraphDiagnostics.Count,
                scheduler.FrameGraphPlan.Count);
            Assert.Equal("85f5d36d45eac52ec1271149e8da6fa9d6f6a56655b4d27b078153842320e8ff",scheduler.FrameGraphTopologyHash);
            Assert.Contains(scheduler.FrameGraphPlan,n=>n.Metadata.Id.Value=="frame.input.publish");
            FrameNodeAdapter publisher=Assert.Single(scheduler.FrameGraphPlan,n=>n.Metadata.Id.Value=="frame.input.publish");
            Assert.Contains(FrameResource.EnemyHealth,publisher.Metadata.Reads);
            Assert.Equal(new[]{FrameResource.PersistentStatePublished},publisher.Metadata.Writes);
            Assert.DoesNotContain(FrameResource.EnemyHealth,publisher.Metadata.Writes);
            Assert.Equal("BattleSystemECS.Core.FrameScheduler.GraphPublishPersistentFrameState/frame.input.publish",
                publisher.Metadata.AccessProfile.BindingId.Value);
            Assert.Contains(scheduler.FrameGraphPlan,n=>n.Metadata.Id.Value=="pregame.random-event.callback-dispatch");
            Assert.Contains(scheduler.FrameGraphPlan,n=>n.Metadata.Id.Value=="spawning.wave.callback-dispatch");
            Assert.DoesNotContain(scheduler.FrameGraphPlan,n=>n.Metadata.Id.Value.EndsWith("Group",StringComparison.Ordinal));
            AssertParallelNode(scheduler,"ability.commit",FrameResource.AbilitiesCommitted);
            AssertCommitNode(scheduler,"effect.commit");
            AssertCommitNode(scheduler,"attribute.aggregate");
            AssertCommitNode(scheduler,"damage.commit");
            AssertCommitNode(scheduler,"resource.commit");
            AssertCommitNode(scheduler,"gameplay-event.commit");
            AssertPrepareNode(scheduler,"primary-death.resolve");
            AssertCommitNode(scheduler,"primary-death.callback-dispatch");
            AssertCommitNode(scheduler,"cascade.damage.commit");
            AssertCommitNode(scheduler,"cascade.resource.commit");
            AssertPrepareNode(scheduler,"cascade-death.resolve");
            AssertCommitNode(scheduler,"cascade-death.callback-dispatch");
            AssertCommitNode(scheduler,"post-death.gameplay-event.commit");
            Assert.Contains(scheduler.FrameGraphPlan,n=>n.Metadata.ExecutionSemantics==FrameExecutionSemantics.InternalParallelCollectSerialCommit);
            AssertParallelNode(scheduler,"ai.enemy.update",FrameResource.DamageRequests);
            AssertParallelNode(scheduler,"movement.enemy.update",FrameResource.EnemyPosition);
            AssertDisjointParallelNode(scheduler,"movement.pathfinding.prepare",FrameResource.EnemyMovement);
            AssertParallelNode(scheduler,"combat.tower-attack.update",FrameResource.DamageRequests);
            AssertDisjointParallelNode(scheduler,"skill-buff.bleed.update",FrameResource.BleedPrepared);
            AssertDisjointParallelNode(scheduler,"skill-buff.frostbite.update",FrameResource.FrostbitePrepared);
            AssertSerialCommitNode(scheduler,"skill-buff.bleed.resolve");
            AssertSerialCommitNode(scheduler,"skill-buff.frostbite.resolve");
            AssertSerialCommitNode(scheduler,"combat.synergy.resolve-buff-shares");
            AssertSerialCommitNode(scheduler,"skill-buff.heal-aura.update");
            AssertSerialCommitNode(scheduler,"skill-buff.thorns-aura.update");
            Assert.All(scheduler.FrameGraphPlan,n=>
            {
                Assert.NotEmpty(n.Metadata.RequiredDependencies);
                Assert.Equal(FrameAccessEvidence.SourceReviewed,n.Metadata.AccessProfile.Evidence);
                Assert.False(string.IsNullOrWhiteSpace(n.Metadata.AccessProfile.ReviewId.Value));
                Assert.False(string.IsNullOrWhiteSpace(n.Metadata.AccessProfile.BindingId.Value));
                Assert.False(string.IsNullOrWhiteSpace(n.Metadata.AccessProfile.Owner.Value));
                Assert.DoesNotContain("WorldState",n.Metadata.Id.Value,StringComparison.Ordinal);
                Assert.DoesNotContain(n.Metadata.Reads,r=>!Enum.IsDefined(typeof(FrameResource),r));
                Assert.DoesNotContain(n.Metadata.Writes,r=>!Enum.IsDefined(typeof(FrameResource),r));
            });
            Assert.DoesNotContain(Enum.GetNames(typeof(FrameResource)),name=>name.Contains("Unknown",StringComparison.Ordinal)||name.Contains("WorldState",StringComparison.Ordinal));
            Assert.Equal(scheduler.FrameGraphPlan.Count,
                scheduler.FrameGraphPlan.Select(n=>n.Metadata.AccessProfile.BindingId.Value).Distinct(StringComparer.Ordinal).Count());
            Assert.All(scheduler.FrameGraphPlan.Where(n=>n.Metadata.AccessProfile.Owner.Value!="BattleSystemECS.Core.FrameScheduler"),
                n=>
                {
                    Assert.True(n.Metadata.AccessProfile.RequiresSystemBinding,$"{n.Metadata.Id} must fail when its Registry binding is removed.");
                    Assert.Contains(n.Metadata.AccessProfile.Owner.Value,n.Metadata.RequiredDependencies);
                });
            Assert.Equal("BattleSystemECS.Systems.SkillSystem.Update(delta)/ability.commit",
                Assert.Single(scheduler.FrameGraphPlan,n=>n.Metadata.Id.Value=="ability.commit").Metadata.AccessProfile.BindingId.Value);
            Assert.Equal("BattleSystemECS.Systems.PathfindingSystem.SetTurn(turn)/movement.pathfinding.prepare",
                Assert.Single(scheduler.FrameGraphPlan,n=>n.Metadata.Id.Value=="movement.pathfinding.prepare").Metadata.AccessProfile.BindingId.Value);
            Assert.Equal("BattleSystemECS.Core.FrameScheduler.GraphBeginFrame/frame.begin",
                Assert.Single(scheduler.FrameGraphPlan,n=>n.Metadata.Id.Value=="frame.begin").Metadata.AccessProfile.BindingId.Value);
            Assert.Equal(FrameAccessReviewCatalog.ReviewedNodeCount,
                scheduler.FrameGraphPlan.Count+scheduler.FrameGraphDiagnostics.Count);
            Assert.All(scheduler.FrameGraphDiagnostics,d=>
            {
                Assert.False(string.IsNullOrWhiteSpace(d.Reason));
                if(d.NodeId.Value=="combat.beam.update"||d.NodeId.Value=="post-death.life-link.resolve")
                    Assert.Equal(FrameAccessEvidence.DisabledUnsafe,d.Evidence);
                else
                    Assert.Equal(FrameAccessEvidence.SourceReviewed,d.Evidence);
                Assert.False(string.IsNullOrWhiteSpace(d.ReviewId.Value));
                Assert.False(string.IsNullOrWhiteSpace(d.BindingId.Value));
                Assert.False(string.IsNullOrWhiteSpace(d.Owner.Value));
            });
            var reviewScopes=scheduler.FrameGraphPlan.Select(n=>n.Metadata.AccessProfile.Review!)
                .Concat(scheduler.FrameGraphDiagnostics.Select(d=>d.Review!))
                .GroupBy(review=>review.ArtifactId,StringComparer.Ordinal)
                .ToDictionary(group=>group.Key,group=>group.Count(),StringComparer.Ordinal);
            Assert.Equal(FrameAccessReviewCatalog.ExpectedArtifactNodeCount("early-groups.md"),reviewScopes["early-groups.md"]);
            Assert.Equal(FrameAccessReviewCatalog.ExpectedArtifactNodeCount("combat-groups.md"),reviewScopes["combat-groups.md"]);
            Assert.Equal(FrameAccessReviewCatalog.ExpectedArtifactNodeCount("commit-postdeath.md"),reviewScopes["commit-postdeath.md"]);
            Assert.Equal(FrameAccessReviewCatalog.ExpectedArtifactNodeCount("supplemental-production-nodes.md"),reviewScopes["supplemental-production-nodes.md"]);
            Assert.Equal(FrameAccessReviewCatalog.ReviewedNodeCount,reviewScopes.Values.Sum());
            Assert.Equal(207,FrameAccessReviewCatalog.ReportedUnionNodeCount);
            Assert.Equal(FrameAccessReviewCatalog.ReviewedNodeCount,
                FrameAccessReviewCatalog.ReportedUnionNodeCount+FrameAccessReviewCatalog.SupplementalNodeIds.Count);
            var allProfiles=scheduler.FrameGraphPlan.Select(n=>(NodeId:n.Metadata.Id.Value,Review:n.Metadata.AccessProfile.Review!))
                .Concat(scheduler.FrameGraphDiagnostics.Select(d=>(NodeId:d.NodeId.Value,Review:d.Review!)))
                .ToDictionary(entry=>entry.NodeId,entry=>entry.Review,StringComparer.Ordinal);
            Assert.All(FrameAccessReviewCatalog.SupplementalNodeIds,nodeId=>
            {
                FrameAccessReviewRecord review=allProfiles[nodeId];
                Assert.Equal("supplemental-production-nodes.md",review.ArtifactId);
                Assert.Equal(FrameAccessReviewCatalog.SupplementalArtifactSha256,review.ArtifactSha256);
                Assert.False(string.IsNullOrWhiteSpace(review.TransitiveCallees));
                Assert.False(string.IsNullOrWhiteSpace(review.MetadataFingerprint));
                Assert.True(review.IsApproved);
            });
            AssertProfileResources(scheduler,"frame.begin",
                new[]{FrameResource.EntityLifecycle,FrameResource.FrameRuntimeState,FrameResource.DeathQueue,FrameResource.ComputedAttributes,FrameResource.PlayerAttributes,FrameResource.PlayerResources},
                new[]{FrameResource.FrameRuntimeState,FrameResource.DeferredResolverState,FrameResource.DeathQueue,FrameResource.ComputedAttributes,FrameResource.PlayerAttributes,FrameResource.PlayerResources,FrameResource.EnemyControl,FrameResource.TowerState,FrameResource.TowerCombatCache,FrameResource.DamageRequests,FrameResource.ResourceRequests,FrameResource.DamageEvents,FrameResource.ResourceEvents,FrameResource.EffectEvents,FrameResource.GameplayEvents});
            AssertProfileResources(scheduler,"pregame.time-rewind.update",
                new[]{FrameResource.PlayerSnapshotState,FrameResource.PlayerResources},new[]{FrameResource.PlayerSnapshotState});
            AssertProfileResources(scheduler,"combat.pickup.update",
                new[]{FrameResource.PickupState,FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.PlayerAttributes,FrameResource.PlayerResources},
                new[]{FrameResource.PickupState,FrameResource.PlayerAttributes,FrameResource.PlayerResources,FrameResource.ResourceRequests});
            AssertProfileResources(scheduler,"movement.enemy.update",
                new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.TowerState,FrameResource.PlayerResources,FrameResource.WeatherState,FrameResource.TerrainState},
                new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.TowerState,FrameResource.PlayerResources,FrameResource.DamageRequests,FrameResource.ResourceRequests,FrameResource.BossTrailPrepared});
            Assert.Contains(scheduler.FrameGraphDiagnostics,d=>d.NodeId.Value=="combat.energy.update"&&d.Policy==OptionalDependencyPolicy.Disabled);
            Assert.Contains(scheduler.FrameGraphDiagnostics,d=>d.NodeId.Value=="ai.lifesteal.update"&&d.Policy==OptionalDependencyPolicy.Disabled);
            Assert.Same(registry.Sapper,scheduler.AI.Sapper);
        }

        [Fact]
        public void DisabledUnsafeBeamAndLifeLinkCannotBeEnabledInProduction()
        {
            // Bug 回归：未修复的 Beam race 与 LifeLink destroyed-source 合同不得被标成安全并启用。
            var enableActions=new (Action<FrameScheduler,SystemRegistry> enable,string nodeId)[]
            {
                ((scheduler,_)=>scheduler.Combat.BeamTower=new Systems.BeamTowerSystem(scheduler.Store),"combat.beam.update"),
                ((scheduler,registry)=>scheduler.PostDeath.LifeLink=registry.LifeLink,"post-death.life-link.resolve")
            };
            foreach(var item in enableActions)
            {
                using var world=new TestWorld();
                int playerId=world.Player();
                var registry=new SystemRegistry();
                registry.CreateAll(world.Store,world.Config,world.Renderer,playerId,new StateMachine());
                registry.WireDependencies(world.Store,playerId);
                var scheduler=new FrameScheduler(world.Store,world.Config);
                registry.AssignToGroupsForValidation(scheduler);
                item.enable(scheduler,registry);
                FrameGraphValidationException error=Assert.Throws<FrameGraphValidationException>(
                    ()=>scheduler.SealGraphComposition());
                Assert.Contains(item.nodeId,error.Message,StringComparison.Ordinal);
            }
        }

        [Fact]
        public void ProductionTickDispatchesRandomAndWaveCallbacksOnceInCommittedOrder()
        {
            // Bug 回归：同步 delegate 副作用必须由显式 callback 节点按稳定顺序提交。
            Config.RandomEvents=new BattleSystemECS.Config.RandomEventConfig
            {
                GlobalEventChance=1f,
                Events=new List<BattleSystemECS.Config.RandomEventDef>
                {
                    new BattleSystemECS.Config.RandomEventDef
                    {
                        Id="dispatch",Name="dispatch",EventType=BattleSystemECS.Config.RandomEventConfig.Merchant,
                        Weight=1f,Duration=10f,Cooldown=30f
                    }
                }
            };
            int playerId=Player(p=>p.Health=100f);
            var eventBus=new CallbackOrderEventBus();
            var registry=new SystemRegistry();
            registry.CreateAll(Store,Config,Renderer,playerId,new StateMachine(),eventBus);
            registry.WireDependencies(Store,playerId);
            registry.RandomEvent!.OnEventTriggered+=(id,name)=>eventBus.Order.Add("random");
            registry.WaveSpawning!.OnWaveStart+=()=>eventBus.Order.Add("wave-subscriber");
            var scheduler=new FrameScheduler(Store,Config,eventBus);
            registry.AssignToGroups(scheduler);
            scheduler.Phase=GameState.WavePhase;

            scheduler.Tick(0.016f,0);
            scheduler.Tick(0.016f,1);

            Assert.Equal(new[]{"wave-subscriber","wave-presentation","random"},eventBus.Order);
            string[] nodeIds=scheduler.FrameGraphPlan.Select(n=>n.Metadata.Id.Value).ToArray();
            Assert.True(Array.IndexOf(nodeIds,"pregame.random-event.update")<
                Array.IndexOf(nodeIds,"pregame.random-event.callback-dispatch"));
            Assert.True(Array.IndexOf(nodeIds,"spawning.wave.update")<
                Array.IndexOf(nodeIds,"spawning.wave.callback-dispatch"));
        }

        [Fact]
        public void RemovingActiveRegistryBindingsAcrossGroupsFailsAtSeal()
        {
            // Bug 回归：删除任一 active Registry binding 必须在 Seal 时失败。
            var removals=new (Action<FrameScheduler> remove,string expectedNode)[]
            {
                (s=>s.Build.Gold=null,"build.gold.update"),
                (s=>s.AI.EnemyAI=null,"ai.enemy.prepare"),
                (s=>s.Movement.EnemyMovement=null,"movement.enemy.prepare"),
                (s=>s.Combat.TowerAttack=null,"combat.tower-attack.update"),
                (s=>s.PostDeath.Combo=null,"post-death.combo.update")
            };
            foreach(var removal in removals)
            {
                using var world=new TestWorld();
                int playerId=world.Player();
                var registry=new SystemRegistry();
                registry.CreateAll(world.Store,world.Config,world.Renderer,playerId,new StateMachine());
                registry.WireDependencies(world.Store,playerId);
                var scheduler=new FrameScheduler(world.Store,world.Config);
                registry.AssignToGroupsForValidation(scheduler);
                removal.remove(scheduler);
                var error=Assert.Throws<FrameGraphValidationException>(()=>scheduler.SealGraphComposition());
                Assert.Contains(removal.expectedNode,error.Message,StringComparison.Ordinal);
            }
        }

        [Fact]
        public void ProductionReviewSnapshotRejectsChangedBindingAndStaleArtifactEvidence()
        {
            // Bug 回归：NodeId 不变时，binding 漂移或伪造 artifact SHA 仍必须在生产验证中失败。
            Player();
            var registry=new SystemRegistry();
            registry.CreateAll(Store,Config,Renderer,0,new StateMachine());
            registry.WireDependencies(Store,0);
            var scheduler=new FrameScheduler(Store,Config);
            registry.AssignToGroups(scheduler);
            FrameNodeAdapter original=Assert.Single(scheduler.FrameGraphPlan,
                node=>node.Metadata.Id.Value=="ability.commit");
            FrameNodeMetadata metadata=original.Metadata;
            FrameBindingId changedBinding=metadata.AccessProfile.BindingId.Value+"/changed";
            Assert.True(FrameAccessReviewCatalog.TryCreate(metadata.Id.Value,changedBinding,metadata.ActivePhases,
                metadata.TimeDomain,metadata.ExecutionSemantics,metadata.Reads,metadata.Writes,
                metadata.Before,metadata.After,metadata.RequiredDependencies,metadata.OptionalDependencies,
                scheduler.CompositionKind,out FrameAccessReviewRecord? changedReview));
            FrameNodeAdapter[] changedNodes=scheduler.FrameGraphPlan.ToArray();
            int index=Array.FindIndex(changedNodes,node=>node.Metadata.Id.Equals(metadata.Id));
            changedNodes[index]=new FrameNodeAdapter(CloneMetadata(metadata,changedBinding,changedReview!),original.System);
            FrameGraphValidationException bindingError=Assert.Throws<FrameGraphValidationException>(()=>
                FrameAccessReviewCatalog.ValidateApprovedSnapshot(changedNodes,scheduler.FrameGraphDiagnostics,
                    scheduler.FrameGraphAvailableDependencies.ToHashSet(StringComparer.Ordinal),scheduler.CompositionKind));
            Assert.Contains("snapshot is stale",bindingError.Message,StringComparison.Ordinal);

            FrameAccessReviewRecord current=metadata.AccessProfile.Review!;
            var staleReview=new FrameAccessReviewRecord(current.Id,current.ArtifactId,"BAD-SHA",current.EvidenceLocator,
                current.TransitiveCallees,current.MetadataFingerprint,current.ParallelModel,current.Disposition,true);
            FrameNodeAdapter[] staleNodes=scheduler.FrameGraphPlan.ToArray();
            staleNodes[index]=new FrameNodeAdapter(CloneMetadata(metadata,metadata.AccessProfile.BindingId,staleReview),original.System);
            FrameGraphValidationException evidenceError=Assert.Throws<FrameGraphValidationException>(()=>
                FrameAccessReviewCatalog.ValidateApprovedSnapshot(staleNodes,scheduler.FrameGraphDiagnostics,
                    scheduler.FrameGraphAvailableDependencies.ToHashSet(StringComparer.Ordinal),scheduler.CompositionKind));
            Assert.Contains("source-review evidence",evidenceError.Message,StringComparison.Ordinal);
        }

        [Fact]
        public void ProductionReviewSnapshotRejectsOrderingAndDependencyOnlyChanges()
        {
            // Bug 回归：NodeId/binding 不变时，仅改排序边或依赖策略也必须使 review 失效。
            Player();
            var registry=new SystemRegistry();
            registry.CreateAll(Store,Config,Renderer,0,new StateMachine());
            registry.WireDependencies(Store,0);
            var scheduler=new FrameScheduler(Store,Config);
            registry.AssignToGroups(scheduler);
            FrameNodeAdapter original=Assert.Single(scheduler.FrameGraphPlan,
                node=>node.Metadata.Id.Value=="ability.commit");
            FrameNodeMetadata source=original.Metadata;
            var variants=new[]
            {
                new FrameNodeMetadata(source.Id,source.ActivePhases,source.TimeDomain,source.ExecutionSemantics,
                    source.Reads,source.Writes,new[]{new FrameNodeId("resource.commit")},source.After,
                    source.RequiredDependencies,source.OptionalDependencies,source.AccessProfile.BindingId,
                    source.AccessProfile.Owner,source.AccessProfile.Evidence,source.AccessProfile.ReviewId,
                    source.AccessProfile.Review,source.AccessProfile.RequiresSystemBinding),
                new FrameNodeMetadata(source.Id,source.ActivePhases,source.TimeDomain,source.ExecutionSemantics,
                    source.Reads,source.Writes,source.Before,new[]{new FrameNodeId("frame.begin")},
                    source.RequiredDependencies,source.OptionalDependencies,source.AccessProfile.BindingId,
                    source.AccessProfile.Owner,source.AccessProfile.Evidence,source.AccessProfile.ReviewId,
                    source.AccessProfile.Review,source.AccessProfile.RequiresSystemBinding),
                new FrameNodeMetadata(source.Id,source.ActivePhases,source.TimeDomain,source.ExecutionSemantics,
                    source.Reads,source.Writes,source.Before,source.After,new[]{"ComponentStore","changed-required"},
                    source.OptionalDependencies,source.AccessProfile.BindingId,source.AccessProfile.Owner,
                    source.AccessProfile.Evidence,source.AccessProfile.ReviewId,source.AccessProfile.Review,
                    source.AccessProfile.RequiresSystemBinding),
                new FrameNodeMetadata(source.Id,source.ActivePhases,source.TimeDomain,source.ExecutionSemantics,
                    source.Reads,source.Writes,source.Before,source.After,source.RequiredDependencies,
                    new[]{new OptionalFrameDependency("changed-optional",OptionalDependencyPolicy.NoOp)},
                    source.AccessProfile.BindingId,source.AccessProfile.Owner,source.AccessProfile.Evidence,
                    source.AccessProfile.ReviewId,source.AccessProfile.Review,source.AccessProfile.RequiresSystemBinding)
            };
            foreach(FrameNodeMetadata changed in variants)
            {
                FrameNodeAdapter[] nodes=scheduler.FrameGraphPlan.ToArray();
                int index=Array.FindIndex(nodes,node=>node.Metadata.Id.Equals(source.Id));
                nodes[index]=new FrameNodeAdapter(changed,original.System);
                FrameGraphValidationException error=Assert.Throws<FrameGraphValidationException>(()=>
                    FrameAccessReviewCatalog.ValidateApprovedSnapshot(nodes,scheduler.FrameGraphDiagnostics,
                        scheduler.FrameGraphAvailableDependencies.ToHashSet(StringComparer.Ordinal),scheduler.CompositionKind));
                Assert.Contains("source-review evidence",error.Message,StringComparison.Ordinal);
            }
        }

        [Fact]
        public void DisabledMetadataStillRejectsInvalidBaseContracts()
        {
            // Bug 回归：disabled 只跳过执行与 writer 分析，不能绕过 phase/time/resource/依赖格式校验。
            var invalid=new[]
            {
                new FrameNodeMetadata("disabled.phase",FramePhaseMask.None,FrameTimeDomain.None,
                    FrameExecutionSemantics.SerialUpdate),
                new FrameNodeMetadata("disabled.time",FramePhaseMask.Wave,FrameTimeDomain.Enemy|FrameTimeDomain.Combat,
                    FrameExecutionSemantics.SerialUpdate),
                new FrameNodeMetadata("disabled.resource",FramePhaseMask.Wave,FrameTimeDomain.None,
                    FrameExecutionSemantics.SerialUpdate,reads:new[]{(FrameResource)int.MaxValue}),
                new FrameNodeMetadata("disabled.semantics",FramePhaseMask.Wave,FrameTimeDomain.None,
                    (FrameExecutionSemantics)int.MaxValue),
                new FrameNodeMetadata("disabled.required",FramePhaseMask.Wave,FrameTimeDomain.None,
                    FrameExecutionSemantics.SerialUpdate,requiredDependencies:new[]{""}),
                new FrameNodeMetadata("disabled.optional",FramePhaseMask.Wave,FrameTimeDomain.None,
                    FrameExecutionSemantics.SerialUpdate,optionalDependencies:new[]{
                        new OptionalFrameDependency("dependency",(OptionalDependencyPolicy)int.MaxValue)})
            };
            foreach(FrameNodeMetadata metadata in invalid)
            {
                var builder=new FrameGraphBuilder();
                builder.DeclareDisabled(metadata,"reviewed disabled test");
                Assert.Throws<FrameGraphValidationException>(()=>builder.BuildAndSeal());
            }
        }

        [Fact]
        public void BenchmarkFactoryUsesSealedProductionRegistryComposition()
        {
            // Bug 回归：mode4/5 factory 必须走完整 Registry 装配并封印生产 graph。
            int playerId=Player();
            var runtime=Systems.BenchmarkCompositionFactory.Create(Store,Config,Renderer,playerId);
            Assert.True(runtime.Scheduler.IsCompositionSealed);
            Assert.Equal(FrameGraphCompositionKind.ProductionRegistry,runtime.Scheduler.CompositionKind);
            Assert.Equal(GameState.Init,runtime.StateMachine.CurrentState);
            Assert.Equal($"ProductionRegistry:{runtime.Scheduler.FrameGraphTopologyHash};Scenario=Gameplay;WaveSpawning=Enabled",runtime.Fingerprint);
            Assert.Equal(FrameAccessReviewCatalog.ApprovedFingerprintRootGameplay,
                runtime.Scheduler.FrameGraphReviewRoot);
            Assert.Contains(runtime.Scheduler.FrameGraphPlan,n=>n.Metadata.Id.Value=="effect.commit");
            Assert.NotNull(runtime.Registry.WaveSpawning);
            Assert.NotNull(runtime.Registry.Skill);
            Assert.Contains(runtime.Scheduler.FrameGraphDiagnostics,d=>
                d.NodeId.Value=="combat.beam.update"&&d.Evidence==FrameAccessEvidence.DisabledUnsafe);
            Assert.Contains(runtime.Scheduler.FrameGraphDiagnostics,d=>
                d.NodeId.Value=="post-death.life-link.resolve"&&d.Evidence==FrameAccessEvidence.DisabledUnsafe);
        }

        [Fact]
        public void BenchmarkModesExposeExecutableCompositionEvidenceBoundary()
        {
            // Bug 回归：small harness 必须走与生产相同的 scenario definition 和 dispatcher。
            var mode2=new Systems.BenchmarkSystem(new ComponentStore()).RunCompositionHarness(2);
            var mode4=new Systems.BenchmarkSystem(new ComponentStore()).RunCompositionHarness(4);
            var mode5=new Systems.BenchmarkSystem(new ComponentStore()).RunCompositionHarness(5);
            Assert.Equal(Systems.BenchmarkCompositionContract.ManualMergedLoop,mode2.Composition);
            Assert.Equal(1,mode2.FramesExecuted);
            Assert.Equal(1,mode2.BeginFrameCalls);
            Assert.Equal(1,mode2.ManualMergedCalls);
            Assert.Equal(0,mode2.GraphTickCalls);
            Assert.False(mode2.GraphSealed);
            Assert.Equal("manual-merged-loop:v1",mode2.CompositionFingerprint);
            Assert.Equal(Systems.BenchmarkCompositionContract.ProductionRegistryGraph,mode4.Composition);
            Assert.Equal(1,mode4.FramesExecuted);
            Assert.Equal(1,mode4.BeginFrameCalls);
            Assert.Equal(0,mode4.ManualMergedCalls);
            Assert.Equal(1,mode4.GraphTickCalls);
            Assert.True(mode4.GraphSealed);
            Assert.Equal(GameState.WavePhase,mode4.FinalState);
            Assert.Equal(1,mode4.StateEntryCount(GameState.BuildPhase));
            Assert.Equal(1,mode4.StateEntryCount(GameState.WavePhase));
            Assert.Contains(";Scenario=FixedPopulationBenchmark;WaveSpawning=Suppressed;Population=64;WaveStart=Suppressed",
                mode4.CompositionFingerprint,StringComparison.Ordinal);
            Assert.StartsWith("ProductionRegistry:1c78440d8676d79ec71ca65f49aa45bcdfab28662f6a5a183f8418e8d5f8e96b",
                mode4.CompositionFingerprint,StringComparison.Ordinal);
            Assert.Equal(Systems.BenchmarkCompositionContract.ProductionRegistryGraph,mode5.Composition);
            Assert.Equal(4,mode5.FramesExecuted);
            Assert.Equal(4,mode5.BeginFrameCalls);
            Assert.Equal(0,mode5.ManualMergedCalls);
            Assert.Equal(4,mode5.GraphTickCalls);
            Assert.True(mode5.GraphSealed);
            Assert.Equal(GameState.GameOver,mode5.FinalState);
            Assert.Equal(2,mode5.StateEntryCount(GameState.BuildPhase));
            Assert.Equal(2,mode5.StateEntryCount(GameState.WavePhase));
            Assert.Equal(1,mode5.StateEntryCount(GameState.Intermission));
            Assert.Equal(1,mode5.StateEntryCount(GameState.LevelComplete));
            Assert.Equal(1,mode5.StateEntryCount(GameState.GameOver));
            Assert.Contains(";Scenario=Gameplay;WaveSpawning=Enabled;Population=Dynamic;WaveStart=Enabled",
                mode5.CompositionFingerprint,StringComparison.Ordinal);
            Assert.NotEqual(mode4.CompositionFingerprint,mode5.CompositionFingerprint);
            Assert.Equal(Systems.BenchmarkRunnerKind.ProductionGraphFixed,Systems.BenchmarkSystem.GetScenarioDefinition(4).Runner);
            Assert.Equal(Systems.BenchmarkRunnerKind.GraphFullGame,Systems.BenchmarkSystem.GetScenarioDefinition(5).Runner);
            Assert.Equal(FrameScenarioKind.FixedPopulationBenchmark,Systems.BenchmarkSystem.GetScenarioDefinition(4).ScenarioKind);
            Assert.Equal(FrameScenarioKind.Gameplay,Systems.BenchmarkSystem.GetScenarioDefinition(5).ScenarioKind);
            Assert.Throws<ArgumentOutOfRangeException>(()=>Systems.BenchmarkSystem.GetScenarioDefinition(1));
        }

        [Fact]
        public void BenchmarkScenarioChangesSealedTopologyReviewRootAndMarker()
        {
            // Bug 回归：相同 Registry 改变场景策略时不得复用 sealed identity。
            using var gameplayWorld=new TestWorld();
            using var fixedWorld=new TestWorld();
            int gameplayPlayer=gameplayWorld.Player();
            int fixedPlayer=fixedWorld.Player();
            var gameplay=Systems.BenchmarkCompositionFactory.Create(gameplayWorld.Store,gameplayWorld.Config,
                gameplayWorld.Renderer,gameplayPlayer,scenarioKind:FrameScenarioKind.Gameplay);
            var fixedPopulation=Systems.BenchmarkCompositionFactory.Create(fixedWorld.Store,fixedWorld.Config,
                fixedWorld.Renderer,fixedPlayer,scenarioKind:FrameScenarioKind.FixedPopulationBenchmark);

            Assert.NotEqual(gameplay.Scheduler.FrameGraphTopologyHash,fixedPopulation.Scheduler.FrameGraphTopologyHash);
            Assert.Equal("85f5d36d45eac52ec1271149e8da6fa9d6f6a56655b4d27b078153842320e8ff",
                gameplay.Scheduler.FrameGraphTopologyHash);
            Assert.Equal("1c78440d8676d79ec71ca65f49aa45bcdfab28662f6a5a183f8418e8d5f8e96b",
                fixedPopulation.Scheduler.FrameGraphTopologyHash);
            Assert.NotEqual(gameplay.Scheduler.FrameGraphReviewRoot,fixedPopulation.Scheduler.FrameGraphReviewRoot);
            Assert.Equal(FrameAccessReviewCatalog.ApprovedFingerprintRootGameplay,gameplay.Scheduler.FrameGraphReviewRoot);
            Assert.Equal(FrameAccessReviewCatalog.ApprovedFingerprintRootFixedPopulation,
                fixedPopulation.Scheduler.FrameGraphReviewRoot);
            Assert.Contains("Scenario=Gameplay;WaveSpawning=Enabled",gameplay.Fingerprint,StringComparison.Ordinal);
            Assert.Contains("Scenario=FixedPopulationBenchmark;WaveSpawning=Suppressed",
                fixedPopulation.Fingerprint,StringComparison.Ordinal);
        }

        [Fact]
        public void SealedDirectGraphHasZeroSchedulerAllocationAtSteadyState()
        {
            // Bug 回归：封存后的 steady-state Tick 不得产生线程内堆分配。
            Player();
            var scheduler=new FrameScheduler(Store,Config);
            scheduler.Phase=GameState.Intermission;
            scheduler.SealGraphComposition();
            for(int i=0;i<8;i++)scheduler.Tick(0.016f,i);
            long before=GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<128;i++)scheduler.Tick(0.016f,i+8);
            long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
            Assert.Equal(0,allocated);
        }

        private static void AssertTimeIdentity(TimeContext expected,TimeContext actual)
        {
            Assert.Equal(expected.RawDelta,actual.RawDelta);
            Assert.Equal(expected.RealDelta,actual.RealDelta);
            Assert.Equal(expected.EnemyDelta,actual.EnemyDelta);
            Assert.Equal(expected.CombatDelta,actual.CombatDelta);
            Assert.Equal(expected.EffectDelta,actual.EffectDelta);
            Assert.Equal(expected.Turn,actual.Turn);
            Assert.Equal(expected.Frame,actual.Frame);
            Assert.Equal(expected.Phase.Kind,actual.Phase.Kind);
            Assert.Equal(expected.EffectClock,actual.EffectClock);
        }

        private static float RunEffectClockScenario(FrameSchedulerExecutionMode mode)
        {
            using var world=new TestWorld();
            int playerId=world.Player();
            int enemyId=world.Enemy(e=>{e.Health=100f;e.MaxHealth=100f;});
            var enemySpec=new Core.GAS.PeriodicSpec(0.25f,new Core.GAS.ExecutionId(8101),Core.GAS.EffectPayloadKind.Damage,
                Core.GAS.MagnitudeSource.Constant,Core.GAS.FirstTickPolicy.NextInterval,Core.GAS.CatchUpPolicy.CatchUpAll,magnitude:1f);
            var combatSpec=new Core.GAS.PeriodicSpec(0.25f,new Core.GAS.ExecutionId(8102),Core.GAS.EffectPayloadKind.Damage,
                Core.GAS.MagnitudeSource.Constant,Core.GAS.FirstTickPolicy.NextInterval,Core.GAS.CatchUpPolicy.CatchUpAll,magnitude:1f);
            var enemyEffect=new Core.GAS.GameplayEffectDefinition(new Core.GAS.EffectId(8101),Core.GAS.EffectType.Periodic,
                Array.Empty<Core.GAS.ModifierDefinition>(),2f,Core.GAS.ClockId.Enemy,Core.GAS.StackingBehavior.None,1,
                Core.GAS.RefreshPolicy.None,Core.GAS.SourceDeathPolicy.Persist,Core.GAS.EffectPayloadKind.Damage,
                default(Core.GAS.TagId),enemySpec,Array.Empty<Core.GAS.ExecutionId>());
            var combatEffect=new Core.GAS.GameplayEffectDefinition(new Core.GAS.EffectId(8102),Core.GAS.EffectType.Periodic,
                Array.Empty<Core.GAS.ModifierDefinition>(),2f,Core.GAS.ClockId.Combat,Core.GAS.StackingBehavior.None,1,
                Core.GAS.RefreshPolicy.None,Core.GAS.SourceDeathPolicy.Persist,Core.GAS.EffectPayloadKind.Damage,
                default(Core.GAS.TagId),combatSpec,Array.Empty<Core.GAS.ExecutionId>());
            Assert.True(world.Store.GameplayEffectsRuntime.TryApply(enemyEffect.Id,enemyEffect,
                world.Store.GetEntityHandle(playerId),world.Store.GetEntityHandle(enemyId),out _,ownerPlayerId:playerId));
            Assert.True(world.Store.GameplayEffectsRuntime.TryApply(combatEffect.Id,combatEffect,
                world.Store.GetEntityHandle(playerId),world.Store.GetEntityHandle(enemyId),out _,ownerPlayerId:playerId));
            world.Store.PlayerBulletTimeTurnsLeft[playerId]=1f;
            world.Store.PlayerBulletTimeScale[playerId]=0.25f;
            var scheduler=new FrameScheduler(world.Store,world.Config,null,mode,Core.GAS.ClockId.Enemy);
            scheduler.SealGraphComposition();
            scheduler.Tick(1f,0);
            Assert.Equal(Core.GAS.ClockId.Enemy,scheduler.EffectClock);
            Assert.Equal(Core.GAS.ClockId.Enemy,scheduler.LastTimeContext.EffectClock);
            Assert.Equal(0.25f,scheduler.LastTimeContext.EffectDelta);
            return world.Store.EnemyHealth[enemyId];
        }

        private static void AssertCommitNode(FrameScheduler scheduler,string id)
        {
            FrameNodeAdapter node=Assert.Single(scheduler.FrameGraphPlan,n=>n.Metadata.Id.Value==id);
            Assert.Equal(FrameExecutionSemantics.SerialCommit,node.Metadata.ExecutionSemantics);
        }

        private static void AssertPrepareNode(FrameScheduler scheduler,string id)
        {
            FrameNodeAdapter node=Assert.Single(scheduler.FrameGraphPlan,n=>n.Metadata.Id.Value==id);
            Assert.Equal(FrameExecutionSemantics.SerialPrepare,node.Metadata.ExecutionSemantics);
        }

        private static void AssertParallelNode(FrameScheduler scheduler,string id,FrameResource expectedWrite)
        {
            FrameNodeAdapter node=Assert.Single(scheduler.FrameGraphPlan,n=>n.Metadata.Id.Value==id);
            Assert.Equal(FrameExecutionSemantics.InternalParallelCollectSerialCommit,node.Metadata.ExecutionSemantics);
            Assert.Contains(expectedWrite,node.Metadata.Writes);
        }

        private static void AssertDisjointParallelNode(FrameScheduler scheduler,string id,FrameResource expectedWrite)
        {
            FrameNodeAdapter node=Assert.Single(scheduler.FrameGraphPlan,n=>n.Metadata.Id.Value==id);
            Assert.Equal(FrameExecutionSemantics.ParallelDisjointWrite,node.Metadata.ExecutionSemantics);
            Assert.Contains(expectedWrite,node.Metadata.Writes);
        }

        private static void AssertSerialCommitNode(FrameScheduler scheduler,string id)
        {
            FrameNodeAdapter node=Assert.Single(scheduler.FrameGraphPlan,n=>n.Metadata.Id.Value==id);
            Assert.Equal(FrameExecutionSemantics.SerialCommit,node.Metadata.ExecutionSemantics);
        }

        private static void AssertProfileResources(FrameScheduler scheduler,string id,FrameResource[] reads,FrameResource[] writes)
        {
            FrameNodeAdapter node=Assert.Single(scheduler.FrameGraphPlan,n=>n.Metadata.Id.Value==id);
            Assert.Equal(reads,node.Metadata.Reads);
            Assert.Equal(writes,node.Metadata.Writes);
        }

        private static FrameNodeMetadata CloneMetadata(FrameNodeMetadata source,FrameBindingId bindingId,
            FrameAccessReviewRecord review)
        {
            return new FrameNodeMetadata(source.Id,source.ActivePhases,source.TimeDomain,source.ExecutionSemantics,
                source.Reads,source.Writes,source.Before,source.After,source.RequiredDependencies,source.OptionalDependencies,
                bindingId,source.AccessProfile.Owner,source.AccessProfile.Evidence,review.Id,review,
                source.AccessProfile.RequiresSystemBinding);
        }

        private static FrameGraph Build(params FrameNodeAdapter[] nodes)
        {
            var builder=new FrameGraphBuilder();
            for(int i=0;i<nodes.Length;i++)builder.Add(nodes[i]);
            return builder.BuildAndSeal();
        }

        private static FrameGraph BuildMetadataVariant(string binding,string review,string required,
            OptionalDependencyPolicy policy,bool optionalAvailable,FrameGraphCompositionKind compositionKind)
        {
            var metadata=new FrameNodeMetadata("profile",FramePhaseMask.Wave,FrameTimeDomain.None,
                FrameExecutionSemantics.SerialUpdate,requiredDependencies:new[]{required},
                optionalDependencies:new[]{new OptionalFrameDependency("Optional",policy)},bindingId:binding,
                owner:"owner",evidence:FrameAccessEvidence.SourceReviewed,reviewId:review,requiresSystemBinding:true);
            var builder=new FrameGraphBuilder(compositionKind).AddAvailableDependency(required);
            if(optionalAvailable)builder.AddAvailableDependency("Optional");
            return builder.Add(new FrameNodeAdapter(metadata,new DelegateSystem(c=>{}))).BuildAndSeal();
        }

        private static FrameNodeAdapter Node(string id,FramePhaseMask phases=FramePhaseMask.Wave,
            FrameTimeDomain domain=FrameTimeDomain.None,IReadOnlyList<FrameResource>? reads=null,
            IReadOnlyList<FrameResource>? writes=null,IReadOnlyList<FrameNodeId>? before=null,IReadOnlyList<FrameNodeId>? after=null,
            IReadOnlyList<string>? required=null,OptionalFrameDependency? optional=null,
            Action<NodeExecutionContext>? execute=null)
        {
            IReadOnlyList<OptionalFrameDependency> optionalList=optional.HasValue?new[]{optional.Value}:Array.Empty<OptionalFrameDependency>();
            var metadata=new FrameNodeMetadata(id,phases,domain,FrameExecutionSemantics.SerialUpdate,
                reads,writes,before:before,after:after,requiredDependencies:required,optionalDependencies:optionalList);
            return new FrameNodeAdapter(metadata,new DelegateSystem(execute??(c=>{})));
        }

        private sealed class CallbackOrderEventBus : IBattleEventBus
        {
            public List<string> Order { get; }=new List<string>();
            public void OnEntityCreated(int entityId,float x,float y,string entityType) { }
            public void OnTowerCreated(int entityId,float x,float y,TowerType towerType) { }
            public void OnEntityDestroyed(int entityId) { }
            public void OnPositionChanged(int entityId,float x,float y) { }
            public void OnPositionsChanged(List<(int entityId,float x,float y)> changes) { }
            public void OnDamageDealt(int targetId,float amount,string damageType,bool isCritical) { }
            public void OnEntityKilled(int entityId,int killerId) { }
            public void OnProjectileFired(float fromX,float fromY,float toX,float toY,float speed) { }
            public void OnWaveStarted(int waveNumber)=>Order.Add("wave-presentation");
            public void OnGameOver(bool victory) { }
        }
    }
}
