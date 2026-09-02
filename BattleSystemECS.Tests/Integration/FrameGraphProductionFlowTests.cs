using System;
using System.Collections.Generic;
using System.Linq;
using BattleSystemECS.Config;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Integration
{
    public sealed class FrameGraphProductionFlowTests : BattleTestBase
    {
        [Fact]
        public void BindingFactRegistersIndependentRuntimeDeclarationAndRejectsDrift()
        {
            var scheduler = new FrameScheduler(new ComponentStore(), GameConfigLoader.LoadConfigStrict(new MockRenderer()));
            scheduler.RegisterFrameBinding("pregame.weather.update", _ => { });

            Assert.True(scheduler.TryGetFrameNodeContract("pregame.weather.update", out var declaration));
            Assert.NotNull(declaration);
            Assert.Equal("Weather", declaration!.RegistrationId);
            Assert.Equal(FramePhaseMask.Wave, declaration.Phase);
            Assert.Throws<FrameGraphValidationException>(() => scheduler.RegisterFrameNodeContract(
                declaration.NodeId, declaration.RegistrationId, declaration.Phase,
                FrameExecutionSemantics.SerialCommit, declaration.RequiredTokens));
        }

        [Fact]
        public void UnknownStringFrameBindingIsRejectedBeforeMutation()
        {
            var scheduler = new FrameScheduler(new ComponentStore(), GameConfigLoader.LoadConfigStrict(new MockRenderer()));

            var error = Assert.Throws<FrameGraphValidationException>(() =>
                scheduler.RegisterFrameBinding("binding.unknown", _ => { }));

            Assert.Contains("Unknown frame binding id: binding.unknown", error.Message, StringComparison.Ordinal);
            Assert.False(scheduler.TryGetFrameBinding("binding.unknown", out _));
            Assert.False(scheduler.TryGetFrameNodeContract("binding.unknown", out _));
        }

        [Fact]
        public void UnknownBuildFrameBindingIsRejectedBeforeMutation()
        {
            var scheduler = new FrameScheduler(new ComponentStore(), GameConfigLoader.LoadConfigStrict(new MockRenderer()));

            var error = Assert.Throws<FrameGraphValidationException>(() =>
                scheduler.RegisterBuildFrameBinding("binding.unknown"));

            Assert.Contains("Unknown frame binding id: binding.unknown", error.Message, StringComparison.Ordinal);
            Assert.False(scheduler.TryGetFrameNodeContract("binding.unknown", out _));
        }

        [Fact]
        public void ReflectRequestRunsThroughProductionDeathCallbacksRewardAndEventsOnce()
        {
            // Bug 回归：原始反伤必须经生产 graph 的提交与拆分死亡回调精确结算一次。
            var events=new RecordingBattleEventBus();
            int playerId=Player();
            int towerId=Tower(0,0);
            int enemyId=Enemy(e=>{e.Health=5f;e.MaxHealth=5f;e.GoldReward=7;});
            Store.TowerReflectRatio[towerId]=1f;
            var registry=new SystemRegistry();
            registry.CreateAll(Store,Config,Renderer,playerId,new StateMachine(),events);
            registry.WireDependencies(Store,playerId);
            var scheduler=new FrameScheduler(Store,Config,events);
            registry.AssignToGroups(scheduler);
            scheduler.Phase=GameState.WavePhase;
            float comboBefore=Store.PlayerComboCount[playerId];
            registry.ReflectTower!.QueueReflect(towerId,enemyId,10f);

            scheduler.Tick(0.016f,0);

            Assert.False(Store.EnemyActive[enemyId]);
            Assert.Equal(1,Store.TotalKills);
            Assert.Equal(7f,Store.GetPlayerGold(playerId));
            Assert.Equal(comboBefore+1f,Store.PlayerComboCount[playerId]);
            Assert.Equal(new[]{"killed","destroyed"},events.KillEvents);
            Assert.Equal(0,Store.DamageResolver.LegacyApplyCount);
            string[] ordered={"combat.reflect.resolve","combat.reflect.apply","damage.commit",
                "primary-death.resolve","primary-death.callback-dispatch"};
            int[] indices=ordered.Select(id=>scheduler.FrameGraphPlan.ToList()
                .FindIndex(node=>node.Metadata.Id.Value==id)).ToArray();
            Assert.All(indices,index=>Assert.True(index>=0));
            Assert.Equal(indices.OrderBy(index=>index),indices);
        }

        [Fact]
        public void SetLevelFirstProductionTickDispatchesWaveStartAndPresentationOnce()
        {
            // Bug 回归：SetLevel 与首次 Update 不得重复提交首波 callback。
            var events=new RecordingBattleEventBus();
            int playerId=Player();
            Store.PlayerWaveKillCount[playerId]=9;
            var registry=new SystemRegistry();
            registry.CreateAll(Store,Config,Renderer,playerId,new StateMachine(),events);
            registry.WireDependencies(Store,playerId);
            int subscriberCalls=0;
            registry.WaveSpawning!.OnWaveStart+=()=>subscriberCalls++;
            var scheduler=new FrameScheduler(Store,Config,events);
            registry.AssignToGroups(scheduler);
            scheduler.Phase=GameState.WavePhase;
            registry.WaveSpawning.SetLevel(1);

            scheduler.Tick(0.016f,0);

            Assert.Equal(1,subscriberCalls);
            Assert.Equal(new[]{1},events.WaveStarts);
            Assert.Equal(0,Store.PlayerWaveKillCount[playerId]);
        }

        [Fact]
        public void ShortRandomEventsDispatchApplyThenExpireOnceForEachPlayer()
        {
            // Bug 回归：短 duration 事件同帧 apply→expire 必须有序提交，不得覆盖或抛异常。
            Config.RandomEvents=new RandomEventConfig
            {
                GlobalEventChance=0f,
                Events=new List<RandomEventDef>
                {
                    new RandomEventDef{Id="short",Name="short",EventType=RandomEventConfig.Merchant,
                        Weight=1f,Duration=0.001f,Cooldown=30f}
                }
            };
            int player0=Player(p=>p.Health=100f);
            int player1=1;
            Store.AddPlayer(player1,10f,1f,10f,1,20);
            Store.AddPosition(player1,1f,0f);
            Store.PlayerMaxHealth[player1]=100f;
            Store.PlayerCurrentHealth[player1]=100f;
            var registry=new SystemRegistry();
            registry.CreateAll(Store,Config,Renderer,player0,new StateMachine());
            registry.WireDependencies(Store,player0);
            var callbacks=new List<string>();
            registry.RandomEvent!.OnEventTriggered+=(id,name)=>callbacks.Add($"{id}:apply:{name}");
            registry.RandomEvent.OnEventEnded+=(id,name)=>callbacks.Add($"{id}:expire:{name}");
            var scheduler=new FrameScheduler(Store,Config);
            registry.AssignToGroups(scheduler);
            scheduler.Phase=GameState.WavePhase;
            registry.RandomEvent.ForceEvent(player0,RandomEventConfig.Merchant,0.001f);
            registry.RandomEvent.ForceEvent(player1,RandomEventConfig.Merchant,0.001f);

            scheduler.Tick(0.016f,0);

            Assert.Equal(new[]{
                $"{player0}:apply:short",$"{player0}:expire:short",
                $"{player1}:apply:short",$"{player1}:expire:short"},callbacks);
            Assert.Equal(RandomEventConfig.None,Store.RandomEventActiveType[player0]);
            Assert.Equal(RandomEventConfig.None,Store.RandomEventActiveType[player1]);
            string[] nodeIds=scheduler.FrameGraphPlan.Select(n=>n.Metadata.Id.Value).ToArray();
            Assert.True(Array.IndexOf(nodeIds,"pregame.random-event.update")<
                Array.IndexOf(nodeIds,"pregame.random-event.callback-dispatch"));
        }

        [Fact]
        public void FixedPopulationProductionScenarioKeepsTenThousandAndSuppressesWaveStart()
        {
            // Bug 回归：mode4 必须执行 sealed production graph，但 500 帧不得生成额外波次敌人或 WaveStart。
            var events=new RecordingBattleEventBus();
            int playerId=Player(p=>p.Health=1000000f);
            var runtime=Systems.BenchmarkCompositionFactory.Create(Store,Config,Renderer,playerId,events,
                FrameScenarioKind.FixedPopulationBenchmark);
            int callbacks=0;
            runtime.Registry.WaveSpawning!.OnWaveStart+=()=>callbacks++;
            for(int i=0;i<10000;i++)
            {
                int enemyId=Store.AddEnemy(i%100,1000f+i/100,0f,float.MaxValue,float.MaxValue,0f,0,1);
                Assert.True(enemyId>=0);
            }
            runtime.StateMachine.TransitionTo(GameState.BuildPhase);
            runtime.StateMachine.TransitionTo(GameState.WavePhase);

            for(int frame=0;frame<500;frame++)
            {
                runtime.Scheduler.Tick(0.016f,frame);
                Assert.Equal(10000,Store.GetActiveEnemyCount());
                Assert.Equal(0,Store.DamageResolver.LegacyApplyCount);
            }

            Assert.True(runtime.Scheduler.IsCompositionSealed);
            Assert.Equal(FrameScenarioKind.FixedPopulationBenchmark,runtime.Scheduler.ScenarioKind);
            Assert.Equal(0,runtime.Registry.WaveSpawning.GetTotalEnemiesSpawned());
            Assert.Equal(0,callbacks);
            Assert.Empty(events.WaveStarts);
        }

        [Fact]
        public void BulletTimeDoesNotScaleTurnBasedAiTimers()
        {
            // Bug 回归：bullet-time 只缩放 EnemyDelta，不得改变按 Tick 计数的 burrow/fear/channel 持续回合数。
            Config.EnemyAbilities=new List<EnemyAbilityDef>{new EnemyAbilityDef
            {Id="turn-cast",Name="turn-cast",AbilityType="self_heal",CastTime=2f,HealAmount=0f}};
            int playerId=Player(p=>p.Health=10000f);
            var runtime=Systems.BenchmarkCompositionFactory.Create(Store,Config,Renderer,playerId,
                scenarioKind:FrameScenarioKind.FixedPopulationBenchmark);
            int enemyId=Enemy(e=>{e.Health=100f;e.MaxHealth=100f;});
            runtime.Registry.EnemyBurrow!.TriggerBurrow(enemyId,2f,0f,0f,0f);
            Store.EnemyIsFeared[enemyId]=true;
            Store.EnemyFearDurationLeft[enemyId]=2f;
            runtime.Registry.EnemyAbility!.EnqueueAbility(enemyId,"turn-cast");
            Store.ActivateBulletTime(playerId,2f,0.1f);
            runtime.StateMachine.TransitionTo(GameState.BuildPhase);
            runtime.StateMachine.TransitionTo(GameState.WavePhase);

            runtime.Scheduler.Tick(0.016f,1);

            Assert.Equal(0.0016f,runtime.Scheduler.LastTimeContext.EnemyDelta,5);
            Assert.Equal(1f,Store.EnemyBurrowTimer[enemyId]);
            Assert.Equal(1f,Store.EnemyFearDurationLeft[enemyId]);
            Assert.Equal(1f,Store.EnemyChannelTimer[enemyId]);
            FrameNodeAdapter burrow=Assert.Single(runtime.Scheduler.FrameGraphPlan,
                n=>n.Metadata.Id.Value=="ai.burrow.update");
            FrameNodeAdapter fear=Assert.Single(runtime.Scheduler.FrameGraphPlan,
                n=>n.Metadata.Id.Value=="ai.fear.update");
            FrameNodeAdapter channel=Assert.Single(runtime.Scheduler.FrameGraphPlan,
                n=>n.Metadata.Id.Value=="ai.enemy-ability.cast-timers");
            Assert.Equal(FrameTimeDomain.None,burrow.Metadata.TimeDomain);
            Assert.Equal(FrameTimeDomain.None,fear.Metadata.TimeDomain);
            Assert.Equal(FrameTimeDomain.None,channel.Metadata.TimeDomain);
        }

        private sealed class RecordingBattleEventBus : IBattleEventBus
        {
            public List<string> KillEvents { get; }=new List<string>();
            public List<int> WaveStarts { get; }=new List<int>();
            public void OnEntityCreated(int entityId,float x,float y,string entityType) { }
            public void OnTowerCreated(int entityId,float x,float y,TowerType towerType) { }
            public void OnEntityDestroyed(int entityId)=>KillEvents.Add("destroyed");
            public void OnPositionChanged(int entityId,float x,float y) { }
            public void OnPositionsChanged(List<(int entityId,float x,float y)> changes) { }
            public void OnDamageDealt(int targetId,float amount,string damageType,bool isCritical) { }
            public void OnEntityKilled(int entityId,int killerId)=>KillEvents.Add("killed");
            public void OnProjectileFired(float fromX,float fromY,float toX,float toY,float speed) { }
            public void OnWaveStarted(int waveNumber)=>WaveStarts.Add(waveNumber);
            public void OnGameOver(bool victory) { }
        }
    }
}
