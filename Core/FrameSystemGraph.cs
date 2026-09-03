#nullable enable
using System;
using System.Collections.Generic;

namespace BattleSystemECS.Core
{
    internal static class FrameSystemGraph
    {
        private static readonly FrameResource[] FrameState = { FrameResource.EntityLifecycle, FrameResource.PhaseState };
        private static readonly FrameResource[] EnemyState = { FrameResource.EntityLifecycle, FrameResource.EnemyHealth, FrameResource.EnemyControl, FrameResource.EnemyPosition, FrameResource.EnemyMovement };
        private static readonly FrameResource[] TowerState = { FrameResource.EntityLifecycle, FrameResource.TowerState, FrameResource.TowerCombatCache };
        private static readonly FrameResource[] CombatRead = { FrameResource.EntityLifecycle, FrameResource.EnemyHealth, FrameResource.EnemyControl, FrameResource.EnemyPosition, FrameResource.TowerState, FrameResource.TowerCombatCache, FrameResource.PlayerAttributes, FrameResource.ComputedAttributes, FrameResource.SpatialIndex };
        private static readonly FrameResource[] EconomyRead = { FrameResource.EntityLifecycle, FrameResource.PlayerResources, FrameResource.TowerState, FrameResource.WaveState };
        private static readonly FrameResource[] EconomyWrite = { FrameResource.PlayerResources, FrameResource.TowerState, FrameResource.ObjectiveState };
        private static readonly FrameResource[] Empty = Array.Empty<FrameResource>();
        private static readonly FrameResource[] PersistentFrameState =
        {
            FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,
            FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.TowerState,
            FrameResource.TowerCombatCache,FrameResource.PlayerAttributes,FrameResource.PlayerResources,
            FrameResource.ComputedAttributes,FrameResource.WaveState,FrameResource.WeatherState,
            FrameResource.TerrainState,FrameResource.ActiveEffects,FrameResource.AttributeModifiers,
            FrameResource.Rewards,FrameResource.ObjectiveState,FrameResource.CorpseState,
            FrameResource.ComboState,FrameResource.ThreatScore,FrameResource.PlayerSnapshotState,
            FrameResource.PickupState,FrameResource.ProjectileState,FrameResource.EnemyProjectileState,
            FrameResource.TelegraphState,FrameResource.ReflectRequests,FrameResource.HeroState,
            FrameResource.TerrainZoneState,FrameResource.RealTimeState,FrameResource.BeamState,
            FrameResource.DeathQueue,FrameResource.FrameRuntimeState,FrameResource.SkillDamageRequests,
            FrameResource.LegacyDotRequests,FrameResource.BeamDamageRequests,
            FrameResource.ElementalReactionPrepared,FrameResource.HealingZonePrepared
        };

        public static FrameGraph Build(FrameScheduler scheduler, FrameGraphCompositionKind kind)
        {
            if (kind != FrameGraphCompositionKind.ProductionRegistry)
            {
                scheduler.PreGame.RegisterBoundFrameAdapters(scheduler);
                scheduler.Spawning.RegisterBoundFrameAdapters(scheduler);
                scheduler.AI.RegisterFrameBindings(scheduler);
                scheduler.Movement.RegisterFrameBindings(scheduler);
                scheduler.Terrain.RegisterFrameBindings(scheduler);
                scheduler.CombatSetup.RegisterFrameBindings(scheduler);
                scheduler.Spatial.RegisterFrameBindings(scheduler);
                scheduler.Combat.RegisterFrameBindings(scheduler);
                scheduler.SkillBuff.RegisterFrameBindings(scheduler);
                scheduler.PostDeath.RegisterFrameBindings(scheduler);
            }
            var builder = new FrameGraphBuilder(kind).AddAvailableDependency("ComponentStore");
            builder.AddAvailableDependency("FrameScenario:"+scheduler.ScenarioKind);
            if (kind == FrameGraphCompositionKind.ProductionRegistry)
            {
                builder.RequireReviewedProfiles();
                scheduler.AddCompletedRegistrationDependencies(builder);
            }
            if (scheduler.HasPathfindingDependency) builder.AddAvailableDependency("PathfindingSystem");
            var r = new Registrar(builder, scheduler, kind);

            RegisterFramePrelude(r, scheduler);
            RegisterBuild(r, scheduler);
            RegisterNonWave(r, scheduler);
            RegisterWave(r, scheduler);
            FrameGraph graph = builder.BuildAndSeal();
            if (kind == FrameGraphCompositionKind.ProductionRegistry)
                FrameRegistrationContractCatalog.ValidateProductionGraph(graph, scheduler);
            return graph;
        }

        private static void RegisterFramePrelude(Registrar r, FrameScheduler scheduler)
        {
            r.BeginAll();
            r.AddAll("frame.input.publish",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,
                PersistentFrameState,new[]{FrameResource.PersistentStatePublished},scheduler.GraphPublishPersistentFrameState);
            r.AddAll("frame.begin", FrameTimeDomain.Real, FrameExecutionSemantics.SerialCommit,
                new[] { FrameResource.EntityLifecycle,FrameResource.FrameRuntimeState, FrameResource.DeathQueue, FrameResource.ComputedAttributes, FrameResource.PlayerAttributes, FrameResource.PlayerResources },
                new[] { FrameResource.FrameRuntimeState, FrameResource.DeferredResolverState, FrameResource.DeathQueue, FrameResource.ComputedAttributes, FrameResource.PlayerAttributes, FrameResource.PlayerResources,FrameResource.EnemyControl,FrameResource.TowerState,FrameResource.TowerCombatCache, FrameResource.DamageRequests, FrameResource.ResourceRequests, FrameResource.DamageEvents, FrameResource.ResourceEvents, FrameResource.EffectEvents, FrameResource.GameplayEvents }, scheduler.GraphBeginFrame);
            r.AddAll("frame.invulnerability.update", FrameTimeDomain.Real, FrameExecutionSemantics.SerialUpdate,
                new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl}, new[] { FrameResource.EnemyControl }, scheduler.GraphDecrementInvulnerability);
            r.AddAll("frame.phaser.update", FrameTimeDomain.Real, FrameExecutionSemantics.SerialUpdate,
                new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl}, new[] { FrameResource.EnemyControl }, scheduler.GraphTickPhaser);
            r.AddAll("frame.blinker.update", FrameTimeDomain.Real, FrameExecutionSemantics.SerialUpdate,
                new[] { FrameResource.EntityLifecycle,FrameResource.EnemyPosition, FrameResource.EnemyMovement, FrameResource.EnemyControl }, new[] { FrameResource.EnemyPosition, FrameResource.EnemyControl }, scheduler.GraphTickBlinker,
                new OptionalFrameDependency("PathfindingSystem", OptionalDependencyPolicy.NoOp));
            r.AddAll("frame.time.freeze", FrameTimeDomain.Global, FrameExecutionSemantics.SerialCommit,
                new[] { FrameResource.TimeScaleState, FrameResource.PlayerResources, FrameResource.PhaseState }, new[] { FrameResource.TimeScaleState, FrameResource.TimeContext }, scheduler.GraphFreezeTime);
            r.AddAll("attribute.aggregate", FrameTimeDomain.None, FrameExecutionSemantics.SerialCommit,
                new[] { FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.PlayerAttributes, FrameResource.AttributeModifiers }, new[] { FrameResource.ComputedAttributes, FrameResource.AttributesAggregated }, scheduler.GraphAggregateAttributes);
            r.StartBranches();
        }

        private static void RegisterBuild(Registrar r, FrameScheduler s)
        {
            BuildGroup g=s.Build;
            r.AddBuild(g,"build.gold.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PlayerResources},new[]{FrameResource.PlayerResources});
            r.AddBuild(g,"build.tower-income.update",FrameExecutionSemantics.SerialUpdate,EconomyRead,EconomyWrite);
            r.AddBuild(g,"build.upgrade.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PlayerResources,FrameResource.PlayerAttributes},new[]{FrameResource.PlayerAttributes});
            r.AddBuild(g,"build.skill.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PlayerResources,FrameResource.PlayerAttributes,FrameResource.PlayerSnapshotState,FrameResource.EntityLifecycle},new[]{FrameResource.PlayerResources,FrameResource.ResourceRequests,FrameResource.PlayerSnapshotState});
            r.AddBuild(g,"build.auto-skill.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PlayerResources,FrameResource.PlayerAttributes,FrameResource.PlayerSnapshotState,FrameResource.EntityLifecycle},new[]{FrameResource.ResourceRequests,FrameResource.PlayerSnapshotState});
            r.AddBuild(g,"build.tower-relocate.update",FrameExecutionSemantics.SerialUpdate,TowerState,TowerState);
            r.AddBuild(g,"build.interest.update",FrameExecutionSemantics.SerialUpdate,Empty,Empty);
            r.AddBuild(g,"build.mana.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PlayerResources},new[]{FrameResource.PlayerResources,FrameResource.ResourceRequests});
            r.AddBuild(g,"build.mana-shield.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PlayerResources},new[]{FrameResource.PlayerResources,FrameResource.ResourceRequests});
            r.AddBuild(g,"build.pre-fight-buff.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PlayerAttributes},new[]{FrameResource.PlayerAttributes});
            r.AddBuild(g,"build.resource-node.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PlayerResources,FrameResource.ObjectiveState,FrameResource.TowerState},new[]{FrameResource.PlayerResources,FrameResource.ObjectiveState,FrameResource.ResourceRequests});
            r.AddBuild(g,"build.objective.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.ObjectiveState},new[]{FrameResource.ObjectiveState});
            r.AddBuild(g,"build.global-skill.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PlayerResources,FrameResource.PlayerAttributes,FrameResource.TowerState},new[]{FrameResource.PlayerResources,FrameResource.TowerState,FrameResource.ResourceRequests});
            r.AddBuild(g,"build.desperation.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PlayerResources},Empty);
            r.AddBuild(g,"build.shop-reroll.update",FrameExecutionSemantics.SerialUpdate,Empty,Empty);
            r.AddBuild(g,"build.skill.reject-pending",FrameExecutionSemantics.SerialCommit,new[]{FrameResource.SkillDamageRequests,FrameResource.DamageRequests},new[]{FrameResource.SkillDamageRequests,FrameResource.DamageRequests});
            r.AddBuildAction("build.effect.tick.real",FrameTimeDomain.Real,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.ActiveEffects},new[]{FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.EffectEvents,FrameResource.DamageRequests,FrameResource.ResourceRequests},s.GraphTickEffectReal);
            r.AddBuildAction("build.effect.tick.global",FrameTimeDomain.Global,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.ActiveEffects},new[]{FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.EffectEvents,FrameResource.DamageRequests,FrameResource.ResourceRequests},s.GraphTickEffectGlobal);
            r.AddBuildAction("build.effect.tick",FrameTimeDomain.Build,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.ActiveEffects},new[]{FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.EffectEvents,FrameResource.DamageRequests,FrameResource.ResourceRequests,FrameResource.EffectsCommitted},s.GraphTickEffectBuild);
            r.AddBuildAction("build.damage.commit",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl,FrameResource.DamageRequests,FrameResource.ResourceRequests},new[]{FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.DamageEvents,FrameResource.DeathQueue,FrameResource.DamageCommitted,FrameResource.ResourceRequests},s.GraphCommitBuildDamage);
            r.AddBuildAction("build.resource.commit",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.PlayerAttributes,FrameResource.EnemyHealth,FrameResource.ResourceRequests},new[]{FrameResource.PlayerResources,FrameResource.EnemyHealth,FrameResource.ResourceEvents,FrameResource.ResourcesCommitted},s.GraphCommitBuildResources);
            r.AddBuildAction("build.gameplay-event.commit",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.DamageEvents,FrameResource.ResourceEvents,FrameResource.EffectEvents},new[]{FrameResource.GameplayEvents,FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.EffectEvents,FrameResource.DamageRequests,FrameResource.ResourceRequests,FrameResource.GameplayEventsCommitted},s.GraphCommitGameplayEvents);
            r.AddBuildAction("build.ability.reject",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,Empty,new[]{FrameResource.AbilitiesCommitted},s.GraphRejectNonWaveAbilities);
            r.AddBuildAction("build.frame.close",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.DeferredResolverState},new[]{FrameResource.DeferredResolverState},s.GraphCloseDeferredResolvers);
        }

        private static void RegisterNonWave(Registrar r,FrameScheduler s)
        {
            r.AddOtherAction("non-wave.damage.reject",FrameExecutionSemantics.SerialCommit,new[]{FrameResource.DamageRequests,FrameResource.ResourceRequests},new[]{FrameResource.DamageRequests,FrameResource.ResourceRequests},s.GraphRejectNonWaveDamage);
            r.AddOtherAction("non-wave.ability.reject",FrameExecutionSemantics.SerialCommit,Empty,new[]{FrameResource.AbilitiesCommitted},s.GraphRejectNonWaveAbilities);
            r.AddOtherAction("non-wave.frame.close",FrameExecutionSemantics.SerialCommit,new[]{FrameResource.DeferredResolverState},new[]{FrameResource.DeferredResolverState},s.GraphCloseDeferredResolvers);
        }

        private static void RegisterWave(Registrar r,FrameScheduler s)
        {
            RegisterPreGame(r,s); RegisterSpawning(r,s); RegisterAI(r,s); RegisterMovement(r,s);
            r.AddWaveAction("movement.presentation.commit",FrameTimeDomain.None,FrameExecutionSemantics.PresentationCommit,new[]{FrameResource.EnemyPosition},new[]{FrameResource.PresentationEvents},s.GraphEmitPositions);
            RegisterTerrain(r,s); RegisterCombatSetup(r,s); RegisterSpatial(r,s);
            r.UseWaveTimeDomain(FrameTimeDomain.Combat);
            RegisterCombat(r,s); RegisterSkillBuff(r,s);
            RegisterPrimaryCommit(r,s); RegisterPostDeath(r,s); RegisterCascadeCommit(r,s);
            r.AddWaveAction("threat.aggregate",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.ThreatScore,FrameResource.DamageEvents},new[]{FrameResource.ThreatScore},s.GraphAggregateThreat);
            r.AddWaveAction("wave.frame.close",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.DeferredResolverState},new[]{FrameResource.DeferredResolverState},s.GraphCloseDeferredResolvers);
        }

        private static void RegisterPreGame(Registrar r,FrameScheduler s)
        {
            r.AddWaveBinding(s,"pregame.weather.update",FrameTimeDomain.Enemy,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.WeatherState,FrameResource.EntityLifecycle,FrameResource.EnemyHealth},new[]{FrameResource.WeatherState,FrameResource.DamageRequests});
            r.AddWaveBinding(s,"pregame.day-night.update",FrameTimeDomain.Enemy,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.WeatherState,FrameResource.PlayerResources},new[]{FrameResource.WeatherState});
            r.AddWaveBinding(s,"pregame.adaptive-difficulty.update",FrameTimeDomain.Enemy,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PlayerResources},Empty);
            r.AddWaveBinding(s,"pregame.construction.update",FrameTimeDomain.Enemy,FrameExecutionSemantics.SerialUpdate,TowerState,TowerState);
            r.AddWaveBinding(s,"pregame.desperation.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PlayerResources},Empty);
            r.AddWaveBinding(s,"pregame.time-rewind.update",FrameTimeDomain.Enemy,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PlayerSnapshotState,FrameResource.PlayerResources},new[]{FrameResource.PlayerSnapshotState});
            r.AddWaveBinding(s,"pregame.wave.read-current-wave",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.WaveState},new[]{FrameResource.FrameRuntimeState});
            r.AddWaveBinding(s,"pregame.wave.read-current-level",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.WaveState},new[]{FrameResource.FrameRuntimeState});
            r.AddWaveBinding(s,"pregame.random-event.update",FrameTimeDomain.Enemy,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PlayerResources,FrameResource.PlayerAttributes,FrameResource.WaveState,FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyPosition,FrameResource.EnemyControl,FrameResource.TimeScaleState,FrameResource.PickupState},new[]{FrameResource.PlayerResources,FrameResource.PlayerAttributes,FrameResource.WaveState,FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyPosition,FrameResource.EnemyControl,FrameResource.TimeScaleState,FrameResource.PickupState,FrameResource.DamageRequests,FrameResource.ResourceRequests,FrameResource.RandomEventCallbacks});
            r.AddWaveBinding(s,"pregame.random-event.callback-dispatch",FrameTimeDomain.None,FrameExecutionSemantics.PresentationCommit,new[]{FrameResource.RandomEventCallbacks},new[]{FrameResource.PresentationEvents});
            r.AddWaveAction("early.damage.commit",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.DamageRequests},new[]{FrameResource.EnemyHealth,FrameResource.DamageEvents,FrameResource.DeathQueue,FrameResource.EarlyDamageCommitted},s.GraphCommitEarlyDamage);
            r.AddWaveAction("early.resource.commit",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.ResourceRequests},new[]{FrameResource.PlayerResources,FrameResource.ResourceEvents,FrameResource.EarlyResourcesCommitted},s.GraphCommitEarlyResources);
        }

        private static void RegisterSpawning(Registrar r,FrameScheduler s)
        {
            r.AddWaveBinding(s,"spawning.wave.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.WaveState,FrameResource.EntityLifecycle},new[]{FrameResource.WaveState,FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.EnemyControl,FrameResource.WaveCallbacks});
            r.AddWaveBinding(s,"spawning.wave.callback-dispatch",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.WaveCallbacks,FrameResource.WaveState,FrameResource.PlayerResources,FrameResource.PlayerAttributes,FrameResource.TowerState,FrameResource.TowerCombatCache,FrameResource.ObjectiveState,FrameResource.CorpseState,FrameResource.ComboState},new[]{FrameResource.PlayerResources,FrameResource.PlayerAttributes,FrameResource.TowerState,FrameResource.TowerCombatCache,FrameResource.ObjectiveState,FrameResource.CorpseState,FrameResource.ComboState,FrameResource.WaveEvents,FrameResource.PresentationEvents});
            r.AddWaveBinding(s,"spawning.nest.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,Empty);
            r.AddWaveBinding(s,"spawning.nest.update",FrameTimeDomain.Enemy,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.WaveState},new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement});
        }

        private static void RegisterAI(Registrar r,FrameScheduler s)
        {
            r.AddWaveBinding(s,"ai.zone-control.update",FrameTimeDomain.Enemy,FrameExecutionSemantics.SerialUpdate,EnemyState,new[]{FrameResource.EnemyControl});
            r.AddWaveBinding(s,"ai.magnetize.update",FrameTimeDomain.Enemy,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.EnemyMovement},new[]{FrameResource.EnemyMovement});
            r.AddWaveBinding(s,"ai.enemy-strafe.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl},new[]{FrameResource.EnemyControl});
            r.AddWaveBinding(s,"ai.enemy-strafe.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl,FrameResource.EnemyPosition},new[]{FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement});
            r.AddWaveBinding(s,"ai.enemy.prepare",FrameTimeDomain.Enemy,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.PlayerResources,FrameResource.TowerState},Empty);
            r.AddWaveBinding(s,"ai.enemy.update",FrameTimeDomain.None,FrameExecutionSemantics.InternalParallelCollectSerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.TowerState,FrameResource.PlayerResources},new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.PlayerResources,FrameResource.DamageRequests,FrameResource.ResourceRequests,FrameResource.ReflectRequests,FrameResource.EnemyAiPrepared,FrameResource.EnemyAiDeathFacts,FrameResource.DeathQueue});
            r.AddWaveBinding(s,"ai.enemy-ability.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,Empty);
            r.AddWaveBinding(s,"ai.enemy-ability.cooldowns",FrameTimeDomain.Enemy,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl},new[]{FrameResource.EnemyControl});
            r.AddWaveBinding(s,"ai.enemy-ability.execute",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.TowerState,FrameResource.PlayerResources},new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.TowerState,FrameResource.PlayerResources,FrameResource.ResourceRequests,FrameResource.TelegraphState});
            r.AddWaveBinding(s,"ai.enemy-ability.cast-timers",FrameTimeDomain.None,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EnemyControl},new[]{FrameResource.EnemyControl});
            r.AddWaveBinding(s,"ai.enemy-ability.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl},new[]{FrameResource.EnemyControl});
            r.AddWaveBinding(s,"ai.burrow.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,Empty);
            r.AddWaveBinding(s,"ai.burrow.update",FrameTimeDomain.None,FrameExecutionSemantics.InternalParallelCollectSerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl,FrameResource.EnemyPosition},new[]{FrameResource.EnemyControl,FrameResource.BurrowEmergePrepared});
            r.AddWaveBinding(s,"ai.burrow.apply",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.PlayerResources,FrameResource.BurrowEmergePrepared},new[]{FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.DamageRequests,FrameResource.ResourceRequests});
            r.AddWaveBinding(s,"ai.necromancer.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,Empty);
            r.AddWaveBinding(s,"ai.necromancer.update",FrameTimeDomain.Enemy,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.CorpseState},new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.CorpseState});
            r.AddWaveBinding(s,"ai.life-link.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle},Empty);
            r.AddWaveBinding(s,"ai.life-link.update",FrameTimeDomain.None,FrameExecutionSemantics.InternalParallelCollectSerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition},new[]{FrameResource.EnemyControl,FrameResource.LifeLinkPrepared});
            r.AddWaveBinding(s,"ai.life-link.cooldowns",FrameTimeDomain.Enemy,FrameExecutionSemantics.ParallelDisjointWrite,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl},new[]{FrameResource.EnemyControl});
            r.AddWaveBinding(s,"ai.enemy-affix.update",FrameTimeDomain.Enemy,FrameExecutionSemantics.SerialUpdate,EnemyState,new[]{FrameResource.EnemyControl});
            r.AddWaveBinding(s,"ai.mana-burn.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,Empty);
            r.AddWaveBinding(s,"ai.mana-burn.update",FrameTimeDomain.None,FrameExecutionSemantics.InternalParallelCollectSerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl,FrameResource.PlayerResources},new[]{FrameResource.PlayerResources,FrameResource.ManaBurnPrepared});
            r.AddWaveBinding(s,"ai.lifesteal.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,EnemyState,new[]{FrameResource.EnemyControl});
            r.AddWaveBinding(s,"ai.lifesteal.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,EnemyState,new[]{FrameResource.EnemyHealth});
            r.AddWaveBinding(s,"ai.phase.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle},Empty);
            r.AddWaveBinding(s,"ai.phase.update",FrameTimeDomain.Enemy,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl},new[]{FrameResource.EnemyControl});
            r.AddWaveBinding(s,"ai.fear.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,Empty);
            r.AddWaveBinding(s,"ai.fear.update",FrameTimeDomain.None,FrameExecutionSemantics.ParallelDisjointWrite,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.TowerState},new[]{FrameResource.EnemyControl});
            r.AddWaveBinding(s,"ai.sapper.prepare",FrameTimeDomain.Enemy,FrameExecutionSemantics.SerialPrepare,Empty,Empty);
            r.AddWaveBinding(s,"ai.sapper.update",FrameTimeDomain.Enemy,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.TowerState},new[]{FrameResource.EnemyControl,FrameResource.TowerState});
            r.AddWaveBinding(s,"ai.sapper.recompute",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.TowerState},new[]{FrameResource.TowerCombatCache});
        }

        private static void RegisterMovement(Registrar r,FrameScheduler s)
        {
            r.AddWaveBinding(s,"movement.wound.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,EnemyState,new[]{FrameResource.EnemyControl});
            r.AddWaveBinding(s,"movement.wound.update",FrameTimeDomain.None,FrameExecutionSemantics.InternalParallelCollectSerialCommit,EnemyState,new[]{FrameResource.DamageRequests,FrameResource.EnemyControl});
            r.AddWaveBinding(s,"movement.pathfinding.prepare",FrameTimeDomain.None,FrameExecutionSemantics.ParallelDisjointWrite,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.EnemyMovement},new[]{FrameResource.EnemyMovement});
            r.AddWaveBinding(s,"movement.enemy.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.TowerState},Empty);
            r.AddWaveBinding(s,"movement.path-block.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyPosition,FrameResource.TerrainState},new[]{FrameResource.TerrainState});
            r.AddWaveBinding(s,"movement.enemy.update",FrameTimeDomain.None,FrameExecutionSemantics.InternalParallelCollectSerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.TowerState,FrameResource.PlayerResources,FrameResource.WeatherState,FrameResource.TerrainState},new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.TowerState,FrameResource.PlayerResources,FrameResource.DamageRequests,FrameResource.ResourceRequests,FrameResource.BossTrailPrepared});
            r.AddWaveBinding(s,"movement.deployable-trap.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.EnemyControl,FrameResource.TowerState},new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl,FrameResource.DamageRequests,FrameResource.TowerState});
            r.AddWaveBinding(s,"movement.path-modifier.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.TerrainState},new[]{FrameResource.PathModifierPrepared});
            r.AddWaveBinding(s,"movement.path-modifier.update",FrameTimeDomain.Enemy,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.TerrainState,FrameResource.PathModifierPrepared},new[]{FrameResource.EnemyMovement,FrameResource.TerrainState});
            r.AddWaveBinding(s,"movement.pull.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,Empty);
            r.AddWaveBinding(s,"movement.pull.update",FrameTimeDomain.Enemy,FrameExecutionSemantics.ParallelDisjointWrite,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.PlayerAttributes,FrameResource.TerrainState},new[]{FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.PlayerAttributes,FrameResource.TerrainState});
            r.AddWaveBinding(s,"movement.enemy-healer.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,EnemyState,new[]{FrameResource.EnemyControl});
            r.AddWaveBinding(s,"movement.enemy-healer.update",FrameTimeDomain.Enemy,FrameExecutionSemantics.SerialCommit,EnemyState,new[]{FrameResource.ResourceRequests});
            r.AddWaveBinding(s,"movement.steal-gold.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EnemyPosition,FrameResource.PlayerResources},new[]{FrameResource.ResourceRequests});
            r.AddWaveBinding(s,"movement.summon.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyPosition},new[]{FrameResource.EnemyMovement});
            r.AddWaveBinding(s,"movement.summon.update",FrameTimeDomain.Enemy,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.EnemyMovement},new[]{FrameResource.DamageRequests,FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyPosition,FrameResource.EnemyMovement});
        }

        private static void RegisterTerrain(Registrar r,FrameScheduler s)
        {
            r.AddWaveBinding(s,"terrain.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle},new[]{FrameResource.TerrainState});
            r.AddWaveBinding(s,"terrain.update",FrameTimeDomain.Enemy,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.TerrainState},new[]{FrameResource.EnemyMovement});
            r.AddWaveBinding(s,"terrain.wave-mutator.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle},new[]{FrameResource.WaveMutatorPrepared});
            r.AddWaveBinding(s,"terrain.wave-mutator.update",FrameTimeDomain.Enemy,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.WaveState,FrameResource.EnemyHealth,FrameResource.EnemyMovement,FrameResource.WaveMutatorPrepared},new[]{FrameResource.EnemyMovement,FrameResource.ResourceRequests});
            r.AddWaveBinding(s,"terrain.enemy-morph.update",FrameTimeDomain.Enemy,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyMovement},new[]{FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyMovement,FrameResource.ResourceRequests});
        }

        private static void RegisterCombatSetup(Registrar r,FrameScheduler s)
        {
            r.AddWaveBinding(s,"combat-setup.player-attack.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.PlayerAttributes,FrameResource.ComputedAttributes},new[]{FrameResource.PlayerAttackPrepared});
            r.AddWaveBinding(s,"combat-setup.hero.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,Empty);
            r.AddWaveBinding(s,"combat-setup.tower-attack.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyMovement,FrameResource.TowerState,FrameResource.PlayerAttributes,FrameResource.ComputedAttributes,FrameResource.WaveState,FrameResource.WeatherState},new[]{FrameResource.TowerAttackPrepared,FrameResource.TowerState});
            r.AddWaveBinding(s,"combat-setup.overcharge.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,new[]{FrameResource.TowerAttackPrepared});
            r.AddWaveBinding(s,"combat-setup.heat.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,new[]{FrameResource.TowerAttackPrepared});
            r.AddWaveBinding(s,"combat-setup.synergy.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.TowerAttackPrepared});
            r.AddWaveBinding(s,"combat-setup.fortress.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition},new[]{FrameResource.TowerCombatCache});
            r.AddWaveBinding(s,"combat-setup.link.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition},new[]{FrameResource.TowerState});
            r.AddWaveBinding(s,"combat-setup.skill.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle,FrameResource.PlayerAttributes,FrameResource.ComputedAttributes},new[]{FrameResource.SkillPrepared});
            r.AddWaveBinding(s,"combat-setup.aura.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.AuraPrepared});
            r.AddWaveBinding(s,"combat-setup.curse.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.CursePrepared});
            r.AddWaveBinding(s,"combat-setup.pull-tower.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.PullTowerPrepared});
            r.AddWaveBinding(s,"combat-setup.mana.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.PlayerAttributes,FrameResource.PlayerResources},new[]{FrameResource.PlayerResources});
            r.AddWaveBinding(s,"combat-setup.global-skill.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.PlayerAttributes},new[]{FrameResource.PlayerAttributes});
            r.AddWaveBinding(s,"combat-setup.hit-shield.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle},new[]{FrameResource.HitShieldPrepared});
            r.AddWaveBinding(s,"combat-setup.hot-zone.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,Empty);
            r.AddWaveBinding(s,"combat-setup.frost-zone.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,Empty);
            r.AddWaveBinding(s,"combat-setup.frost-zone.update",FrameTimeDomain.None,FrameExecutionSemantics.ParallelDisjointWrite,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.EnemyControl},new[]{FrameResource.TowerState,FrameResource.EnemyControl});
            r.AddWaveBinding(s,"combat-setup.terrain-zone.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,Empty);
            r.AddWaveBinding(s,"combat-setup.terrain-zone.update",FrameTimeDomain.Enemy,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.TerrainZoneState,FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.EnemyControl},new[]{FrameResource.TerrainZoneState,FrameResource.EnemyControl,FrameResource.DamageRequests});
            r.AddWaveBinding(s,"combat-setup.wander.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.EnemyControl},new[]{FrameResource.WanderPrepared});
            r.AddWaveBinding(s,"combat-setup.wander.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.WanderPrepared},new[]{FrameResource.EnemyMovement});
            r.AddWaveBinding(s,"combat-setup.taunt.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.TauntPrepared});
        }

        private static void RegisterSpatial(Registrar r,FrameScheduler s)
        {
            r.AddWaveAction("spatial.index.rebuild",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EnemyPosition,FrameResource.EntityLifecycle},new[]{FrameResource.SpatialIndex},s.GraphRebuildSpatialIndex);
            r.AddWaveBinding(s,"spatial.patrol.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,new[]{FrameResource.TowerAttackPrepared});
            r.AddWaveBinding(s,"spatial.patrol.update",FrameTimeDomain.Enemy,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition},new[]{FrameResource.TowerState,FrameResource.EnemyPosition});
            r.AddWaveBinding(s,"spatial.chrono.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.ChronoPrepared});
            r.AddWaveBinding(s,"spatial.chrono.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.EnemyControl,FrameResource.ChronoPrepared},new[]{FrameResource.EnemyControl});
            r.AddWaveBinding(s,"spatial.fog.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,Empty);
            r.AddWaveBinding(s,"spatial.fog.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.SpatialIndex},new[]{FrameResource.TowerCombatCache});
            r.AddWaveBinding(s,"spatial.point-defense.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,Empty);
            r.AddWaveBinding(s,"spatial.point-defense.update",FrameTimeDomain.Enemy,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.EnemyProjectileState},new[]{FrameResource.EnemyProjectileState});
            r.AddWaveBinding(s,"spatial.telegraph.update",FrameTimeDomain.Enemy,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.TelegraphState,FrameResource.PlayerResources},new[]{FrameResource.TelegraphState,FrameResource.PlayerResources,FrameResource.GameplayEvents});
            r.AddWaveBinding(s,"spatial.mine.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.TowerState});
            r.AddWaveBinding(s,"spatial.mine.update",FrameTimeDomain.Enemy,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.EnemyHealth},new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.DamageRequests,FrameResource.DeathQueue});
        }

        private static void RegisterCombat(Registrar r,FrameScheduler s)
        {
            CombatGroup g=s.Combat;
            r.AddWaveBinding(s,"combat.player-attack.update",FrameTimeDomain.None,FrameExecutionSemantics.InternalParallelCollectSerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.PlayerAttributes,FrameResource.PlayerResources,FrameResource.ComputedAttributes,FrameResource.PlayerAttackPrepared},new[]{FrameResource.EnemyControl,FrameResource.PlayerAttributes,FrameResource.PlayerResources,FrameResource.DamageRequests,FrameResource.ResourceRequests,FrameResource.DeathQueue,FrameResource.GameplayEvents,FrameResource.PresentationEvents});
            r.AddWaveBinding(s,"combat.overcharge.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.TowerState});
            r.AddWaveBinding(s,"combat.heat.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.TowerState});
            r.AddWaveBinding(s,"combat.energy.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition},new[]{FrameResource.TowerState});
            r.AddWaveBinding(s,"combat.demolish.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.EnemyHealth,FrameResource.SpatialIndex},new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyControl,FrameResource.DamageRequests,FrameResource.DeathQueue});
            r.AddWaveBinding(s,"combat.hit-shield.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl,FrameResource.HitShieldPrepared},new[]{FrameResource.EnemyControl});
            r.AddWaveBinding(s,"combat.sabotage.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.EnemyControl,FrameResource.TowerState},new[]{FrameResource.EnemyControl,FrameResource.TowerState});
            r.AddWaveBinding(s,"combat.stealth.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.TowerState});
            r.AddWaveBinding(s,"combat.synergy.resolve-buff-shares",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition},new[]{FrameResource.TowerState});
            r.AddWaveBinding(s,"combat.tower-attack.update",FrameTimeDomain.Combat,FrameExecutionSemantics.InternalParallelCollectSerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.TowerState,FrameResource.TowerCombatCache,FrameResource.PlayerAttributes,FrameResource.PlayerResources,FrameResource.ComputedAttributes,FrameResource.SpatialIndex,FrameResource.WeatherState,FrameResource.ProjectileState,FrameResource.TowerAttackPrepared},new[]{FrameResource.TowerState,FrameResource.EnemyControl,FrameResource.PlayerResources,FrameResource.DamageRequests,FrameResource.ResourceRequests,FrameResource.DeathQueue,FrameResource.ProjectileState,FrameResource.GameplayEvents,FrameResource.PresentationEvents,FrameResource.DodgePrepared});
            r.AddWaveBinding(s,"combat.synergy.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.TowerState,FrameResource.TowerCombatCache});
            r.AddWaveBinding(s,"combat.link.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.EnemyControl},new[]{FrameResource.TowerState,FrameResource.EnemyControl});
            r.AddWaveBinding(s,"combat.aura.resolve",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.AuraPrepared},new[]{FrameResource.TowerState});
            r.AddWaveBinding(s,"combat.shrine.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.ShrinePrepared});
            r.AddWaveBinding(s,"combat.shrine.resolve",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.ShrinePrepared},new[]{FrameResource.TowerCombatCache});
            r.AddWaveBinding(s,"combat.beacon.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.BeaconPrepared});
            r.AddWaveBinding(s,"combat.beacon.resolve",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.BeaconPrepared},new[]{FrameResource.TowerCombatCache});
            r.AddWaveBinding(s,"combat.curse.resolve",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.EnemyControl,FrameResource.CursePrepared},new[]{FrameResource.EnemyControl});
            r.AddWaveBinding(s,"combat.pull-tower.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.EnemyControl,FrameResource.PullTowerPrepared},new[]{FrameResource.TowerState,FrameResource.EnemyControl});
            r.AddWaveBinding(s,"combat.silence.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.TowerState});
            r.AddWaveBinding(s,"combat.dispel.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.TowerState});
            r.AddWaveBinding(s,"combat.projectile.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.ProjectileState,FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.EnemyControl,FrameResource.PlayerResources},new[]{FrameResource.ProjectileState,FrameResource.DamageRequests,FrameResource.PlayerResources});
            r.AddWaveBinding(s,"combat.enemy-projectile.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EnemyProjectileState,FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.PlayerResources},new[]{FrameResource.EnemyProjectileState,FrameResource.PlayerResources});
            r.AddWaveBinding(s,"combat.pickup.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.PickupState,FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.PlayerAttributes,FrameResource.PlayerResources},new[]{FrameResource.PickupState,FrameResource.PlayerAttributes,FrameResource.PlayerResources,FrameResource.ResourceRequests});
            r.AddWaveBinding(s,"combat.mana.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.PlayerAttributes,FrameResource.PlayerResources},new[]{FrameResource.PlayerResources,FrameResource.ResourceRequests});
            r.AddWaveBinding(s,"combat.mana-shield.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.PlayerAttributes,FrameResource.PlayerResources},new[]{FrameResource.PlayerResources,FrameResource.ResourceRequests});
            r.AddWaveBinding(s,"combat.global-skill.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.PhaseState,FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.TowerState,FrameResource.PlayerAttributes,FrameResource.PlayerResources},new[]{FrameResource.PlayerAttributes,FrameResource.TowerState,FrameResource.DamageRequests,FrameResource.ResourceRequests,FrameResource.TimeScaleState});
            r.AddWaveBinding(s,"combat.beam.update",FrameTimeDomain.Combat,FrameExecutionSemantics.InternalParallelCollectSerialCommit,new[]{FrameResource.BeamState,FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.EnemyControl,FrameResource.SpatialIndex},new[]{FrameResource.BeamDamageRequests});
            r.AddWaveBinding(s,"combat.hero.update",FrameTimeDomain.Combat,FrameExecutionSemantics.InternalParallelCollectSerialCommit,new[]{FrameResource.HeroState,FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.SpatialIndex},new[]{FrameResource.HeroState,FrameResource.DamageRequests,FrameResource.HeroAttackPrepared});
            r.AddWaveBinding(s,"combat.suicide-bomb.update",FrameTimeDomain.None,FrameExecutionSemantics.InternalParallelCollectSerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyPosition,FrameResource.TowerState,FrameResource.ReflectRequests},new[]{FrameResource.DamageRequests,FrameResource.ResourceRequests,FrameResource.DeathQueue,FrameResource.ReflectRequests,FrameResource.SuicideExplosionPrepared});
            r.AddWaveBinding(s,"combat.reflect.resolve",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.ReflectRequests,FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition},new[]{FrameResource.ReflectPrepared});
            r.AddWaveBinding(s,"combat.reflect.apply",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.ReflectPrepared,FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyHealth},new[]{FrameResource.DamageRequests});
            r.AddWaveBinding(s,"combat.tower-morph.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.TowerState});
            r.AddWaveBinding(s,"combat.taunt.resolve",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.TowerState,FrameResource.EnemyControl,FrameResource.TauntPrepared},new[]{FrameResource.EnemyControl});
            r.AddWaveBinding(s,"combat.tower-active-skill.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PhaseState,FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.TowerState});
            r.AddWaveBinding(s,"combat.aggro.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl},new[]{FrameResource.EnemyControl});
            r.AddWaveBinding(s,"combat.hero-skill.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialUpdate,
                new[]{FrameResource.PhaseState,FrameResource.HeroState,FrameResource.EntityLifecycle,FrameResource.EnemyHealth},
                new[]{FrameResource.HeroState,FrameResource.DamageRequests,FrameResource.ResourceRequests,FrameResource.GameplayEvents});
            r.AddWaveBinding(s,"combat.echo-clone.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.RealTimeState},new[]{FrameResource.EntityLifecycle,FrameResource.TowerState});
            r.AddWaveBinding(s,"combat.bloodlust.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.TowerState});
            r.AddWaveBinding(s,"combat.momentum.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.PlayerAttributes,FrameResource.TowerState},new[]{FrameResource.PlayerAttributes,FrameResource.TowerState});
            r.AddWaveBinding(s,"combat.adrenaline.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PlayerAttributes},new[]{FrameResource.PlayerAttributes});
            r.AddWaveBinding(s,"combat.crest.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialUpdate,Empty,Empty);
            r.AddWaveBinding(s,"combat.culling.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.PlayerAttributes},new[]{FrameResource.DamageRequests,FrameResource.PlayerAttributes,FrameResource.DeathQueue});
        }

        private static void RegisterSkillBuff(Registrar r,FrameScheduler s)
        {
            SkillBuffGroup g=s.SkillBuff;
            r.AddWaveAction("effect.tick",FrameTimeDomain.Effect,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.ActiveEffects},new[]{FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.EffectEvents,FrameResource.DamageRequests,FrameResource.ResourceRequests,FrameResource.EffectsCommitted},s.GraphTickConfiguredEffect);
            r.AddWaveAction("effect.tick.combat",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.ActiveEffects},new[]{FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.EffectEvents,FrameResource.DamageRequests,FrameResource.ResourceRequests},s.GraphTickEffectCombat);
            r.AddWaveAction("effect.tick.enemy",FrameTimeDomain.Enemy,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.ActiveEffects},new[]{FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.EffectEvents,FrameResource.DamageRequests,FrameResource.ResourceRequests},s.GraphTickEffectEnemy);
            r.AddWaveAction("effect.tick.real",FrameTimeDomain.Real,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.ActiveEffects},new[]{FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.EffectEvents,FrameResource.DamageRequests,FrameResource.ResourceRequests},s.GraphTickEffectReal);
            r.AddWaveAction("effect.tick.global",FrameTimeDomain.Global,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.ActiveEffects},new[]{FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.EffectEvents,FrameResource.DamageRequests,FrameResource.ResourceRequests},s.GraphTickEffectGlobal);
            r.AddWaveBinding(s,"skill-buff.buff.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.ActiveEffects,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.PlayerResources},new[]{FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.DamageRequests,FrameResource.LegacyDotRequests});
            r.AddWaveBinding(s,"skill-buff.skill.resolve-damage",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.SkillDamageRequests,FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl},new[]{FrameResource.DamageRequests});
            r.AddWaveBinding(s,"skill-buff.buff.resolve-dot",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.LegacyDotRequests,FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl},new[]{FrameResource.DamageRequests});
            r.AddWaveBinding(s,"skill-buff.elemental.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.DamageEvents,FrameResource.EntityLifecycle,FrameResource.EnemyControl,FrameResource.ElementalReactionPrepared},new[]{FrameResource.EnemyControl,FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.ElementalReactionPrepared});
            r.AddWaveBinding(s,"skill-buff.elemental.resolve",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.ElementalReactionPrepared},new[]{FrameResource.DamageRequests});
            r.AddWaveBinding(s,"skill-buff.bleed.update",FrameTimeDomain.Combat,FrameExecutionSemantics.ParallelDisjointWrite,EnemyState,new[]{FrameResource.EnemyControl,FrameResource.BleedPrepared});
            r.AddWaveBinding(s,"skill-buff.bleed.resolve",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.BleedPrepared},new[]{FrameResource.DamageRequests});
            r.AddWaveBinding(s,"skill-buff.frostbite.update",FrameTimeDomain.Combat,FrameExecutionSemantics.ParallelDisjointWrite,EnemyState,new[]{FrameResource.EnemyControl,FrameResource.FrostbitePrepared});
            r.AddWaveBinding(s,"skill-buff.frostbite.resolve",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.FrostbitePrepared},new[]{FrameResource.DamageRequests});
            r.AddWaveBinding(s,"skill-buff.healing-zone.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.CorpseState,FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyPosition,FrameResource.PlayerResources,FrameResource.HealingZonePrepared},new[]{FrameResource.CorpseState,FrameResource.EnemyHealth,FrameResource.PlayerResources,FrameResource.ResourceRequests,FrameResource.HealingZonePrepared});
            r.AddWaveBinding(s,"skill-buff.mark.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialUpdate,EnemyState,new[]{FrameResource.EnemyControl});
            r.AddWaveBinding(s,"skill-buff.death-mark.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl},new[]{FrameResource.DamageRequests,FrameResource.EnemyControl});
            r.AddWaveBinding(s,"skill-buff.heal-aura.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,TowerState,new[]{FrameResource.HealAuraPrepared});
            r.AddWaveBinding(s,"skill-buff.heal-aura.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.HealAuraPrepared},new[]{FrameResource.TowerState});
            r.AddWaveBinding(s,"skill-buff.thorns-aura.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,TowerState,new[]{FrameResource.ThornsAuraPrepared});
            r.AddWaveBinding(s,"skill-buff.thorns-aura.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.TowerState,FrameResource.ThornsAuraPrepared},new[]{FrameResource.TowerState,FrameResource.DamageRequests});
            r.AddWaveBinding(s,"skill-buff.skill.update",FrameTimeDomain.Combat,FrameExecutionSemantics.InternalParallelCollectSerialCommit,new[]{FrameResource.PlayerResources,FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyPosition,FrameResource.EnemyControl,FrameResource.EnemyMovement,FrameResource.TowerState,FrameResource.CorpseState,FrameResource.PlayerAttributes,FrameResource.PlayerSnapshotState,FrameResource.SkillPrepared},new[]{FrameResource.DamageRequests,FrameResource.SkillDamageRequests,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.PlayerResources,FrameResource.ResourceRequests,FrameResource.TimeScaleState,FrameResource.EntityLifecycle,FrameResource.CorpseState,FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.PlayerSnapshotState,FrameResource.AbilitiesCommitted});
            r.AddWaveBinding(s,"skill-buff.wisp.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.PlayerResources},new[]{FrameResource.EnemyControl,FrameResource.EnemyMovement,FrameResource.PlayerResources,FrameResource.ResourceRequests});
            r.AddWaveBinding(s,"skill-buff.rally.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PlayerAttributes,FrameResource.TowerState},new[]{FrameResource.PlayerAttributes,FrameResource.TowerCombatCache});
        }

        private static void RegisterPrimaryCommit(Registrar r,FrameScheduler s)
        {
            r.AddWaveAction("damage.commit",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl,FrameResource.DamageRequests},new[]{FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.DamageEvents,FrameResource.DeathQueue,FrameResource.DamageCommitted},s.GraphCommitGameplayDamage);
            r.AddWaveAction("resource.commit",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.PlayerAttributes,FrameResource.EnemyHealth,FrameResource.ResourceRequests},new[]{FrameResource.PlayerResources,FrameResource.EnemyHealth,FrameResource.ResourceEvents,FrameResource.ResourcesCommitted},s.GraphCommitGameplayResources);
            r.AddWaveAction("gameplay-event.commit",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.DamageEvents,FrameResource.ResourceEvents,FrameResource.EffectEvents},new[]{FrameResource.GameplayEvents,FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.EffectEvents,FrameResource.DamageRequests,FrameResource.ResourceRequests,FrameResource.GameplayEventsCommitted},s.GraphCommitGameplayEvents);
            r.AddWaveAction("primary-death.resolve",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.DeathQueue},new[]{FrameResource.PrimaryDeathFacts},s.GraphPrepareDeaths);
            r.AddWaveAction("primary-death.callback-dispatch",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,
                new[]{FrameResource.PrimaryDeathFacts,FrameResource.DamageEvents,FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.TowerState,FrameResource.TowerCombatCache,FrameResource.PlayerAttributes,FrameResource.PlayerResources,FrameResource.ComboState,FrameResource.ObjectiveState,FrameResource.CorpseState,FrameResource.PickupState,FrameResource.ActiveEffects},
                new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.PlayerResources,FrameResource.Rewards,FrameResource.PresentationEvents,FrameResource.DamageEvents,FrameResource.GameplayEvents,FrameResource.ComboState,FrameResource.PlayerAttributes,FrameResource.ObjectiveState,FrameResource.CorpseState,FrameResource.PickupState,FrameResource.TowerState,FrameResource.TowerCombatCache,FrameResource.DamageRequests,FrameResource.ResourceRequests,FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.PrimaryDeathsResolved},s.GraphDispatchDeathCallbacks);
        }

        private static void RegisterPostDeath(Registrar r,FrameScheduler s)
        {
            PostDeathGroup g=s.PostDeath;
            r.AddWaveBinding(s,"post-death.fission.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.PrimaryDeathsResolved,FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyMovement,FrameResource.EnemyControl},new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.EnemyControl});
            r.AddWaveBinding(s,"post-death.life-link.resolve",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.PrimaryDeathsResolved,FrameResource.EnemyControl},new[]{FrameResource.DamageRequests});
            r.AddWaveBinding(s,"post-death.objective.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PrimaryDeathsResolved,FrameResource.ObjectiveState},new[]{FrameResource.ObjectiveState});
            r.AddWaveBinding(s,"post-death.resource-node.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.ObjectiveState,FrameResource.PlayerResources},new[]{FrameResource.PlayerResources,FrameResource.ObjectiveState});
            r.AddWaveBinding(s,"post-death.tower-income.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialUpdate,EconomyRead,EconomyWrite);
            r.AddWaveBinding(s,"post-death.corpse.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PrimaryDeathsResolved,FrameResource.CorpseState,FrameResource.EnemyPosition,FrameResource.EnemyControl,FrameResource.TowerState},new[]{FrameResource.CorpseState,FrameResource.DamageRequests,FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.EnemyControl,FrameResource.EnemyMovement,FrameResource.TowerState});
            r.AddWaveBinding(s,"post-death.combo.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PrimaryDeathsResolved,FrameResource.ComboState},new[]{FrameResource.ComboState,FrameResource.PlayerAttributes});
            r.AddWaveBinding(s,"post-death.doom-clock.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.ObjectiveState,FrameResource.WaveState},new[]{FrameResource.ObjectiveState,FrameResource.WaveState});
            r.AddWaveBinding(s,"post-death.soul-harvest.update",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PrimaryDeathsResolved,FrameResource.PlayerResources},new[]{FrameResource.PlayerResources});
        }

        private static void RegisterCascadeCommit(Registrar r,FrameScheduler s)
        {
            r.AddWaveAction("cascade.damage.commit",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl,FrameResource.DamageRequests},new[]{FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.DamageEvents,FrameResource.DeathQueue,FrameResource.CascadeDamageCommitted},s.GraphCommitGameplayDamage);
            r.AddWaveAction("cascade.resource.commit",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.PlayerAttributes,FrameResource.EnemyHealth,FrameResource.ResourceRequests},new[]{FrameResource.PlayerResources,FrameResource.EnemyHealth,FrameResource.ResourceEvents,FrameResource.CascadeResourcesCommitted},s.GraphCommitGameplayResources);
            r.AddWaveAction("cascade-death.resolve",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.DeathQueue},new[]{FrameResource.CascadeDeathFacts},s.GraphPrepareDeaths);
            r.AddWaveAction("cascade-death.callback-dispatch",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,
                new[]{FrameResource.CascadeDeathFacts,FrameResource.DamageEvents,FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.TowerState,FrameResource.TowerCombatCache,FrameResource.PlayerAttributes,FrameResource.PlayerResources,FrameResource.ComboState,FrameResource.ObjectiveState,FrameResource.CorpseState,FrameResource.PickupState,FrameResource.ActiveEffects},
                new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.PlayerResources,FrameResource.Rewards,FrameResource.PresentationEvents,FrameResource.DamageEvents,FrameResource.GameplayEvents,FrameResource.ComboState,FrameResource.PlayerAttributes,FrameResource.ObjectiveState,FrameResource.CorpseState,FrameResource.PickupState,FrameResource.TowerState,FrameResource.TowerCombatCache,FrameResource.DamageRequests,FrameResource.ResourceRequests,FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.CascadeDeathsResolved},s.GraphDispatchDeathCallbacks);
            r.AddWaveAction("post-death.gameplay-event.commit",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.DamageEvents,FrameResource.ResourceEvents,FrameResource.EffectEvents,FrameResource.CascadeDeathsResolved},new[]{FrameResource.GameplayEvents,FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.EffectEvents,FrameResource.DamageRequests,FrameResource.ResourceRequests,FrameResource.PostDeathGameplayEventsCommitted},s.GraphCommitPostDeathGameplayEvents);
        }

        private sealed class Registrar
        {
            private readonly FrameGraphBuilder _builder; private readonly bool _strict;
            private FrameNodeId? _lastAll,_lastBuild,_lastWave,_lastOther;
            private FrameTimeDomain _waveDomain=FrameTimeDomain.Enemy;
            private static readonly FramePhaseMask OtherPhases=FramePhaseMask.All&~(FramePhaseMask.Build|FramePhaseMask.Wave);
            private readonly FrameGraphCompositionKind _compositionKind;
            private readonly FrameScheduler _scheduler;
            public Registrar(FrameGraphBuilder builder,FrameScheduler scheduler,FrameGraphCompositionKind compositionKind){_builder=builder;_scheduler=scheduler;_compositionKind=compositionKind;_strict=compositionKind==FrameGraphCompositionKind.ProductionRegistry;}
            public void BeginAll(){_lastAll=null;}
            public void StartBranches(){if(!_lastAll.HasValue)throw new InvalidOperationException("Frame prelude is empty.");_lastBuild=_lastAll;_lastWave=_lastAll;_lastOther=_lastAll;}
            public void UseWaveTimeDomain(FrameTimeDomain domain){_waveDomain=domain;}
            public void AddAll(string id,FrameTimeDomain domain,FrameExecutionSemantics semantics,FrameResource[] reads,FrameResource[] writes,Action<NodeExecutionContext> action,params OptionalFrameDependency[] optional)=>AddAction(ref _lastAll,id,FramePhaseMask.All,domain,semantics,reads,writes,action,optional);
            public void AddBuildAction(string id,FrameTimeDomain domain,FrameExecutionSemantics semantics,FrameResource[] reads,FrameResource[] writes,Action<NodeExecutionContext> action)=>AddAction(ref _lastBuild,id,FramePhaseMask.Build,domain,semantics,reads,writes,action);
            public void AddWaveAction(string id,FrameTimeDomain domain,FrameExecutionSemantics semantics,FrameResource[] reads,FrameResource[] writes,Action<NodeExecutionContext> action)=>AddAction(ref _lastWave,id,FramePhaseMask.Wave,domain,semantics,reads,writes,action);
            public void AddOtherAction(string id,FrameExecutionSemantics semantics,FrameResource[] reads,FrameResource[] writes,Action<NodeExecutionContext> action)=>AddAction(ref _lastOther,id,OtherPhases,FrameTimeDomain.None,semantics,reads,writes,action);
            public void AddBuild<T>(T? system,string id,FrameExecutionSemantics semantics,FrameResource[] reads,FrameResource[] writes,Action<T,NodeExecutionContext> action)where T:class=>AddSlot(ref _lastBuild,system,id,FramePhaseMask.Build,FrameTimeDomain.Build,semantics,reads,writes,action);
            public void AddBuild(BuildGroup group,string id,FrameExecutionSemantics semantics,FrameResource[] reads,FrameResource[] writes)
            {
                Action<ComponentStore,float>? action;
                if (!group.TryGetBinding(id, out action))
                {
                    string? disabledReason = ReviewedDisabledReason(id);
                    var metadata = disabledReason == null
                        ? RegistrationMetadata(id, FramePhaseMask.Build, FrameTimeDomain.Build, semantics, reads, writes, _lastBuild)
                        : DisabledMetadata(id, FramePhaseMask.Build, FrameTimeDomain.Build, semantics, reads, writes, _lastBuild);
                    if (!_strict && group.HasSlot(id))
                    {
                        _builder.Add(new FrameNodeAdapter(metadata, new DelegateSystem(_ => { })));
                        _lastBuild = metadata.Id;
                        return;
                    }
                    if (_strict && id != "build.tower-income.update" && id != "build.tower-relocate.update")
                        throw new FrameGraphValidationException($"Required production build binding '{id}' is not configured.");
                    _builder.DeclareDisabled(metadata, disabledReason ?? "content build binding missing; policy=Disabled");
                    return;
                }
                FrameBindingRegistration fact = BindingFact(id, FramePhaseMask.Build, semantics);
                _scheduler.RegisterOrValidateFrameBindingFact(fact);
                string bindingOwner = FactOwner(fact);
                if (!_strict) _builder.AddAvailableDependency(bindingOwner);
                var bindingMetadata = Metadata(id, fact.Phase, FrameTimeDomain.Build, fact.ExecutionPolicy, reads, writes, _lastBuild,
                    bindingId: bindingOwner + "." + FrameAdapterBindingCatalog.Require(id), owner: bindingOwner, requiresSystemBinding: true,
                    requiredDependencies: fact.RequiredTokens);
                _builder.Add(new FrameNodeAdapter(bindingMetadata, new DelegateSystem(c => action!(c.Store, c.Delta))));
                _lastBuild = bindingMetadata.Id;
            }
            public void AddWave<T>(T? system,string id,FrameExecutionSemantics semantics,FrameResource[] reads,FrameResource[] writes,Action<T,NodeExecutionContext> action)where T:class=>AddSlot(ref _lastWave,system,id,FramePhaseMask.Wave,_waveDomain,semantics,reads,writes,action);
            public void AddWaveAt<T>(T? system,string id,FrameTimeDomain domain,FrameExecutionSemantics semantics,FrameResource[] reads,FrameResource[] writes,Action<T,NodeExecutionContext> action)where T:class=>AddSlot(ref _lastWave,system,id,FramePhaseMask.Wave,domain,semantics,reads,writes,action);
            public void AddWaveBinding(FrameScheduler scheduler,string id,FrameTimeDomain domain,FrameExecutionSemantics semantics,FrameResource[] reads,FrameResource[] writes)
            {
                Action<NodeExecutionContext>? action = null;
                scheduler.TryGetFrameBinding(id, out action);
                if (action == null)
                {
                    string? reason=ReviewedDisabledReason(id);
                    if (_strict && reason == null) throw new FrameGraphValidationException($"Required production frame binding '{id}' is not configured.");
                    var missingMetadata=reason==null
                        ? RegistrationMetadata(id,FramePhaseMask.Wave,domain,semantics,reads,writes,_lastWave)
                        : DisabledMetadata(id,FramePhaseMask.Wave,domain,semantics,reads,writes,_lastWave);
                    _builder.DeclareDisabled(missingMetadata,reason??"frame binding missing; policy=Disabled");
                    return;
                }
                string? directDisabledReason=ReviewedDisabledReason(id);
                FrameNodeMetadata metadata;
                if(!_strict&&directDisabledReason!=null)
                    metadata=DisabledMetadata(id,FramePhaseMask.Wave,domain,semantics,reads,writes,_lastWave);
                else
                {
                    FrameBindingRegistration fact = BindingFact(id, FramePhaseMask.Wave, semantics);
                    _scheduler.RegisterOrValidateFrameBindingFact(fact);
                    var owner=FactOwner(fact);
                    metadata=Metadata(id,fact.Phase,domain,fact.ExecutionPolicy,reads,writes,_lastWave,
                        bindingId:owner+"."+FrameAdapterBindingCatalog.Require(id),owner:owner,requiresSystemBinding:true,
                        requiredDependencies:fact.RequiredTokens);
                    if(!_strict)_builder.AddAvailableDependency(owner);
                }
                _builder.Add(new FrameNodeAdapter(metadata,new DelegateSystem(action)));
                _lastWave=metadata.Id;
            }
             private void AddSlot<T>(ref FrameNodeId? last,T? system,string id,FramePhaseMask phase,FrameTimeDomain domain,FrameExecutionSemantics semantics,FrameResource[] reads,FrameResource[] writes,Action<T,NodeExecutionContext> action)where T:class
             {if(system==null){string? reason=ReviewedDisabledReason(id);if(_strict&&reason==null)throw new FrameGraphValidationException($"Required production system call '{id}' is not configured.");var missing=reason==null?RegistrationMetadata(id,phase,domain,semantics,reads,writes,last):DisabledMetadata(id,phase,domain,semantics,reads,writes,last);_builder.DeclareDisabled(missing,reason??"direct composition slot missing; policy=Disabled");return;}string? directReason=ReviewedDisabledReason(id);FrameNodeMetadata metadata;if(!_strict&&directReason!=null)metadata=DisabledMetadata(id,phase,domain,semantics,reads,writes,last);else{FrameBindingRegistration fact=BindingFact(id,phase,semantics);string owner=FactOwner(fact);metadata=Metadata(id,phase,domain,semantics,reads,writes,last,bindingId:owner+"."+FrameAdapterBindingCatalog.Require(id),owner:owner,requiresSystemBinding:true,requiredDependencies:fact.RequiredTokens);if(!_strict)_builder.AddAvailableDependency(owner);}_builder.Add(new FrameNodeAdapter(metadata,new DelegateSystem(c=>action(system,c))));last=metadata.Id;}
            private FrameNodeMetadata RegistrationMetadata(string id,FramePhaseMask phase,FrameTimeDomain domain,FrameExecutionSemantics semantics,FrameResource[] reads,FrameResource[] writes,FrameNodeId? after)
              {FrameBindingRegistration fact=BindingFact(id,phase,semantics);string owner=FactOwner(fact);return Metadata(id,phase,domain,semantics,reads,writes,after,bindingId:owner+"."+FrameAdapterBindingCatalog.Require(id),owner:owner,requiresSystemBinding:true,requiredDependencies:fact.RequiredTokens);}
             private static FrameBindingRegistration BindingFact(string id,FramePhaseMask phase,FrameExecutionSemantics semantics)
             {FrameBindingRegistration fact=FrameBindingFacts.Get(id);if(fact.Phase!=phase)throw new FrameGraphValidationException($"Frame binding phase mismatch for '{id}': fact={fact.Phase}, graph={phase}.");if(fact.ExecutionPolicy!=semantics)throw new FrameGraphValidationException($"Frame binding execution policy mismatch for '{id}': fact={fact.ExecutionPolicy}, graph={semantics}.");return fact;}
             private static string FactOwner(FrameBindingRegistration fact)
             {for(int i=0;i<fact.RequiredTokens.Length;i++){string token=fact.RequiredTokens[i];if(token.StartsWith("registration.",StringComparison.Ordinal))return token;}throw new FrameGraphValidationException("Frame binding fact has no owner token: "+fact.NodeId);}
            private FrameNodeMetadata DisabledMetadata(string id,FramePhaseMask phase,FrameTimeDomain domain,FrameExecutionSemantics semantics,FrameResource[] reads,FrameResource[] writes,FrameNodeId? after)
            {const string owner=FrameRegistrationContractCatalog.DisabledOwnerToken;return Metadata(id,phase,domain,semantics,reads,writes,after,bindingId:owner+"."+FrameAdapterBindingCatalog.Require(id),owner:owner,requiresSystemBinding:false,requiredDependencies:new[]{"ComponentStore"});}
            private void AddAction(ref FrameNodeId? last,string id,FramePhaseMask phase,FrameTimeDomain domain,FrameExecutionSemantics semantics,FrameResource[] reads,FrameResource[] writes,Action<NodeExecutionContext> action,params OptionalFrameDependency[] optional)
            {const string owner="BattleSystemECS.Core.FrameScheduler";bool bound=_strict;string[] required=bound?new[]{"ComponentStore",owner}:new[]{"ComponentStore"};if(bound)_scheduler.RegisterFrameNodeContract(id,"FrameScheduler",phase,semantics,required);var metadata=Metadata(id,phase,domain,semantics,reads,writes,last,optional,owner+"."+FrameAdapterBindingCatalog.Require(id),owner,bound,required);_builder.Add(new FrameNodeAdapter(metadata,new DelegateSystem(action)));last=metadata.Id;}
            private FrameNodeMetadata Metadata(string id,FramePhaseMask phase,FrameTimeDomain domain,FrameExecutionSemantics semantics,FrameResource[] reads,FrameResource[] writes,FrameNodeId? after,OptionalFrameDependency[]? optional=null,string? bindingId=null,string? owner=null,bool requiresSystemBinding=false,string[]? requiredDependencies=null)
            {
                string stableBinding=bindingId??throw new FrameGraphValidationException($"Frame node '{id}' has no binding semantic id.");
                FrameNodeId[] afterNodes=after.HasValue?new[]{after.Value}:Array.Empty<FrameNodeId>();
                string[] required=requiredDependencies??new[]{"ComponentStore"};
                OptionalFrameDependency[] optionalNodes=optional??Array.Empty<OptionalFrameDependency>();
                bool reviewed=FrameAccessReviewCatalog.TryCreate(id,stableBinding,phase,domain,semantics,
                    reads,writes,Array.Empty<FrameNodeId>(),afterNodes,required,optionalNodes,
                    _compositionKind,out FrameAccessReviewRecord? review);
                FrameAccessReviewId reviewId=review?.Id??default;
                return new FrameNodeMetadata(id,phase,domain,semantics,reads,writes,
                    after:afterNodes,
                    requiredDependencies:required,
                    optionalDependencies:optionalNodes,
                    bindingId:stableBinding,
                    owner:owner??throw new FrameGraphValidationException($"Frame node '{id}' has no binding owner."),
                    evidence:reviewed?FrameAccessReviewCatalog.EvidenceFor(id):FrameAccessEvidence.Unreviewed,
                    reviewId:reviewId,review:review,requiresSystemBinding:requiresSystemBinding);
            }

            private static string? ReviewedDisabledReason(string id) => id switch
            {
                "ai.enemy-affix.update" => "Registry explicitly disables the inactive enemy-affix update slot; source reviewed.",
                "ai.lifesteal.prepare" or "ai.lifesteal.update" => "Registry has no LifestealSystem instance; paired prepare/update slots are reviewed disabled.",
                "post-death.life-link.resolve" => "Life-link break damage is disabled because its destroyed-source policy is undefined; current behavior remains no-op.",
                "build.tower-income.update" or "post-death.tower-income.update" => "TowerIncomeSystem is not configured in the current Registry composition; reviewed disabled.",
                "build.tower-relocate.update" => "TowerRelocateSystem is not configured in the current Registry composition; reviewed disabled.",
                "pregame.construction.update" => "TowerConstructionSystem is not configured in the current Registry composition; reviewed disabled.",
                "movement.wound.prepare" or "movement.wound.update" => "EnemyWoundSystem is intentionally absent from Registry composition; reviewed disabled pair.",
                "movement.enemy-healer.prepare" or "movement.enemy-healer.update" => "EnemyHealerSystem is intentionally absent from Registry composition; reviewed disabled pair.",
                "movement.steal-gold.update" => "StealGoldSystem is intentionally absent from Registry composition; reviewed disabled.",
                "movement.summon.prepare" or "movement.summon.update" => "SummonSystem is intentionally absent from Registry composition; reviewed disabled pair.",
                "combat-setup.overcharge.prepare" or "combat.overcharge.update" => "TowerOverchargeSystem is intentionally absent from Registry composition; reviewed disabled pair.",
                "combat-setup.heat.prepare" or "combat.heat.update" => "HeatSystem is intentionally absent from Registry composition; reviewed disabled pair.",
                "combat-setup.link.prepare" or "combat.link.update" => "TowerLinkSystem is intentionally absent from Registry composition; reviewed disabled pair.",
                "spatial.patrol.prepare" or "spatial.patrol.update" => "PatrolTowerSystem is intentionally absent from Registry composition; reviewed disabled pair.",
                "spatial.fog.prepare" or "spatial.fog.update" => "FogSystem is intentionally absent from Registry composition; reviewed disabled pair.",
                "spatial.point-defense.prepare" or "spatial.point-defense.update" => "PointDefenseSystem is intentionally absent from Registry composition; reviewed disabled pair.",
                "combat.energy.update" => "TowerEnergySystem is intentionally absent from Registry composition; reviewed disabled.",
                "combat.beam.update" => "BeamTowerSystem is intentionally absent from Registry composition; reviewed disabled.",
                "combat.demolish.update" => "TowerDemolishSystem is intentionally absent from Registry composition; reviewed disabled.",
                "combat.silence.update" => "TowerSilenceSystem is intentionally absent from Registry composition; reviewed disabled.",
                "combat.dispel.update" => "TowerDispelSystem is intentionally absent from Registry composition; reviewed disabled.",
                "combat.enemy-projectile.update" => "EnemyProjectileSystem is intentionally absent from Registry composition; reviewed disabled.",
                _ => null
            };

        }
    }
}
