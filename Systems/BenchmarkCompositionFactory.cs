using System;
using BattleSystemECS.Config;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    internal readonly struct BenchmarkCompositionRuntime
    {
        public FrameScheduler Scheduler { get; }
        public SystemRegistry Registry { get; }
        public StateMachine StateMachine { get; }
        public string Fingerprint { get; }
        public FrameScenarioKind ScenarioKind { get; }

        public BenchmarkCompositionRuntime(FrameScheduler scheduler,SystemRegistry registry,
            StateMachine stateMachine,string fingerprint,FrameScenarioKind scenarioKind)
        {
            Scheduler=scheduler;
            Registry=registry;
            StateMachine=stateMachine;
            Fingerprint=fingerprint;
            ScenarioKind=scenarioKind;
        }

        public string ExecutionFingerprint(int expectedPopulation)
        {
            if(ScenarioKind==FrameScenarioKind.FixedPopulationBenchmark)
                return Fingerprint+";Population="+expectedPopulation+";WaveStart=Suppressed";
            return Fingerprint+";Population=Dynamic;WaveStart=Enabled";
        }
    }

    internal static class BenchmarkCompositionFactory
    {
        public static BenchmarkCompositionRuntime Create(ComponentStore store,GameConfig config,
            IRenderer logger,int playerId,IBattleEventBus eventBus=null,
            FrameScenarioKind scenarioKind=FrameScenarioKind.Gameplay)
        {
            if(store==null) throw new ArgumentNullException(nameof(store));
            if(config==null) throw new ArgumentNullException(nameof(config));
            if(logger==null) throw new ArgumentNullException(nameof(logger));

            var stateMachine=new StateMachine();
            var registry=new SystemRegistry();
            registry.CreateAll(store,config,logger,playerId,stateMachine,eventBus);
            registry.WireDependencies(store,playerId);

            var scheduler=new FrameScheduler(store,config,eventBus,
                scenarioKind:scenarioKind);
            registry.AssignToGroups(scheduler);
            scheduler.BindStateMachine(stateMachine);

            string wavePolicy=scenarioKind==FrameScenarioKind.FixedPopulationBenchmark?"Suppressed":"Enabled";
            string fingerprint=$"{scheduler.CompositionKind}:{scheduler.FrameGraphTopologyHash}"+
                $";Scenario={scenarioKind};WaveSpawning={wavePolicy}";
            return new BenchmarkCompositionRuntime(scheduler,registry,stateMachine,fingerprint,scenarioKind);
        }
    }
}
