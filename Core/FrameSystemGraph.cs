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
        private static readonly FrameResource[] CombatWrite = { FrameResource.DamageRequests, FrameResource.ResourceRequests, FrameResource.EffectRequests, FrameResource.TowerState, FrameResource.EnemyControl };
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
            var builder = new FrameGraphBuilder(kind).AddAvailableDependency("ComponentStore");
            builder.AddAvailableDependency("FrameScenario:"+scheduler.ScenarioKind);
            if (kind == FrameGraphCompositionKind.ProductionRegistry) builder.RequireReviewedProfiles();
            if (scheduler.HasPathfindingDependency) builder.AddAvailableDependency("PathfindingSystem");
            var r = new Registrar(builder, kind);

            RegisterFramePrelude(r, scheduler);
            RegisterBuild(r, scheduler);
            RegisterNonWave(r, scheduler);
            RegisterWave(r, scheduler);
            return builder.BuildAndSeal();
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
            r.AddBuild(g.Gold,"build.gold.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PlayerResources},new[]{FrameResource.PlayerResources},(x,c)=>x.Update());
            r.AddBuild(g.TowerIncome,"build.tower-income.update",FrameExecutionSemantics.SerialUpdate,EconomyRead,EconomyWrite,(x,c)=>x.Update(c.Delta));
            r.AddBuild(g.Upgrade,"build.upgrade.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PlayerResources,FrameResource.PlayerAttributes},new[]{FrameResource.PlayerAttributes},(x,c)=>x.Update());
            r.AddBuild(g.Skill,"build.skill.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.AbilityRequests,FrameResource.PlayerResources,FrameResource.PlayerAttributes,FrameResource.PlayerSnapshotState,FrameResource.EntityLifecycle},new[]{FrameResource.AbilityRequests,FrameResource.PlayerResources,FrameResource.ResourceRequests,FrameResource.PlayerSnapshotState},(x,c)=>x.Update(c.Delta,false));
            r.AddBuild(g.AutoSkill,"build.auto-skill.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.AbilityRequests,FrameResource.PlayerResources,FrameResource.PlayerAttributes,FrameResource.PlayerSnapshotState,FrameResource.EntityLifecycle},new[]{FrameResource.AbilityRequests,FrameResource.ResourceRequests,FrameResource.PlayerSnapshotState},(x,c)=>x.Update(false));
            r.AddBuild(g.TowerRelocate,"build.tower-relocate.update",FrameExecutionSemantics.SerialUpdate,TowerState,TowerState,(x,c)=>x.Update());
            r.AddBuild(g.Interest,"build.interest.update",FrameExecutionSemantics.SerialUpdate,Empty,Empty,(x,c)=>x.Update());
            r.AddBuild(g.Mana,"build.mana.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PlayerResources},new[]{FrameResource.PlayerResources,FrameResource.ResourceRequests},(x,c)=>x.Update(c.Delta,true));
            r.AddBuild(g.ManaShield,"build.mana-shield.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PlayerResources},new[]{FrameResource.PlayerResources,FrameResource.ResourceRequests},(x,c)=>x.Update(c.Delta));
            r.AddBuild(g.PreFightBuff,"build.pre-fight-buff.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PlayerAttributes},new[]{FrameResource.PlayerAttributes},(x,c)=>x.Update(c.Delta));
            r.AddBuild(g.ResourceNode,"build.resource-node.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PlayerResources,FrameResource.ObjectiveState,FrameResource.TowerState},new[]{FrameResource.PlayerResources,FrameResource.ObjectiveState,FrameResource.ResourceRequests},(x,c)=>x.Update(c.Delta,GameState.BuildPhase));
            r.AddBuild(g.Objective,"build.objective.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.ObjectiveState},new[]{FrameResource.ObjectiveState},(x,c)=>x.Update(c.Delta,GameState.BuildPhase));
            r.AddBuild(g.GlobalSkill,"build.global-skill.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.AbilityRequests,FrameResource.PlayerResources,FrameResource.PlayerAttributes,FrameResource.TowerState},new[]{FrameResource.AbilityRequests,FrameResource.PlayerResources,FrameResource.TowerState,FrameResource.ResourceRequests},(x,c)=>x.Update(c.Delta,true));
            r.AddBuild(g.Desperation,"build.desperation.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PlayerResources},Empty,(x,c)=>x.Update());
            r.AddBuild(g.ShopReroll,"build.shop-reroll.update",FrameExecutionSemantics.SerialUpdate,Empty,Empty,(x,c)=>x.Update());
            r.AddBuild(g.Skill,"build.skill.reject-pending",FrameExecutionSemantics.SerialCommit,new[]{FrameResource.SkillDamageRequests,FrameResource.DamageRequests},new[]{FrameResource.SkillDamageRequests,FrameResource.DamageRequests},(x,c)=>x.RejectPendingSkillDamage());
            r.AddBuildAction("build.effect.tick.real",FrameTimeDomain.Real,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.ActiveEffects},new[]{FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.EffectEvents,FrameResource.DamageRequests,FrameResource.ResourceRequests},c=>s.GraphTickEffects(c,Core.GAS.ClockId.RealTime));
            r.AddBuildAction("build.effect.tick.global",FrameTimeDomain.Global,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.ActiveEffects},new[]{FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.EffectEvents,FrameResource.DamageRequests,FrameResource.ResourceRequests},c=>s.GraphTickEffects(c,Core.GAS.ClockId.Global));
            r.AddBuildAction("build.effect.commit",FrameTimeDomain.Build,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.ActiveEffects,FrameResource.EffectRequests},new[]{FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.EffectEvents,FrameResource.DamageRequests,FrameResource.ResourceRequests,FrameResource.EffectsCommitted},c=>s.GraphTickEffects(c,Core.GAS.ClockId.Build));
            r.AddBuildAction("build.damage.commit",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl,FrameResource.DamageRequests,FrameResource.ResourceRequests},new[]{FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.DamageEvents,FrameResource.DeathQueue,FrameResource.DamageCommitted,FrameResource.ResourceRequests},s.GraphCommitBuildDamage);
            r.AddBuildAction("build.resource.commit",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.PlayerAttributes,FrameResource.EnemyHealth,FrameResource.ResourceRequests},new[]{FrameResource.PlayerResources,FrameResource.EnemyHealth,FrameResource.ResourceEvents,FrameResource.ResourcesCommitted},s.GraphCommitBuildResources);
            r.AddBuildAction("build.gameplay-event.commit",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.DamageEvents,FrameResource.ResourceEvents,FrameResource.EffectEvents},new[]{FrameResource.GameplayEvents,FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.EffectEvents,FrameResource.DamageRequests,FrameResource.ResourceRequests,FrameResource.GameplayEventsCommitted},s.GraphCommitGameplayEvents);
            r.AddBuildAction("build.ability.reject",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.AbilityRequests},new[]{FrameResource.AbilityRequests,FrameResource.AbilitiesCommitted},s.GraphRejectNonWaveAbilities);
            r.AddBuildAction("build.frame.close",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.DeferredResolverState},new[]{FrameResource.DeferredResolverState},s.GraphCloseDeferredResolvers);
        }

        private static void RegisterNonWave(Registrar r,FrameScheduler s)
        {
            r.AddOtherAction("non-wave.damage.reject",FrameExecutionSemantics.SerialCommit,new[]{FrameResource.DamageRequests,FrameResource.ResourceRequests},new[]{FrameResource.DamageRequests,FrameResource.ResourceRequests},s.GraphRejectNonWaveDamage);
            r.AddOtherAction("non-wave.ability.reject",FrameExecutionSemantics.SerialCommit,new[]{FrameResource.AbilityRequests},new[]{FrameResource.AbilityRequests,FrameResource.AbilitiesCommitted},s.GraphRejectNonWaveAbilities);
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
            PreGameGroup g=s.PreGame;
            r.AddWave(g.Weather,"pregame.weather.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.WeatherState,FrameResource.EntityLifecycle,FrameResource.EnemyHealth},new[]{FrameResource.WeatherState,FrameResource.DamageRequests},(x,c)=>x.Update(c.Delta));
            r.AddWave(g.DayNight,"pregame.day-night.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.WeatherState,FrameResource.PlayerResources},new[]{FrameResource.WeatherState},(x,c)=>x.Update(c.Delta));
            r.AddWave(g.AdaptiveDifficulty,"pregame.adaptive-difficulty.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PlayerResources},Empty,(x,c)=>x.Update(c.Delta));
            r.AddWave(g.Construction,"pregame.construction.update",FrameExecutionSemantics.SerialUpdate,TowerState,TowerState,(x,c)=>x.Update(c.Delta));
            r.AddWaveAt(g.Desperation,"pregame.desperation.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PlayerResources},Empty,(x,c)=>x.Update());
            r.AddWave(g.TimeRewind,"pregame.time-rewind.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PlayerSnapshotState,FrameResource.PlayerResources},new[]{FrameResource.PlayerSnapshotState},(x,c)=>x.Update(c.Delta));
            r.AddWaveAt(g.WaveSpawning,"pregame.wave.read-current-wave",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.WaveState},new[]{FrameResource.FrameRuntimeState},(x,c)=>s.GraphCurrentWave=x.GetCurrentWave());
            r.AddWaveAt(g.WaveSpawning,"pregame.wave.read-current-level",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.WaveState},new[]{FrameResource.FrameRuntimeState},(x,c)=>s.GraphCurrentLevel=x.GetCurrentLevel());
            r.AddWave(g.RandomEvent,"pregame.random-event.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PlayerResources,FrameResource.PlayerAttributes,FrameResource.WaveState,FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyPosition,FrameResource.EnemyControl,FrameResource.TimeScaleState,FrameResource.PickupState},new[]{FrameResource.PlayerResources,FrameResource.PlayerAttributes,FrameResource.WaveState,FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyPosition,FrameResource.EnemyControl,FrameResource.TimeScaleState,FrameResource.PickupState,FrameResource.DamageRequests,FrameResource.ResourceRequests,FrameResource.RandomEventCallbacks},(x,c)=>x.Update(c.Delta,s.GraphCurrentWave,s.GraphCurrentLevel));
            r.AddWaveAt(g.RandomEvent,"pregame.random-event.callback-dispatch",FrameTimeDomain.None,FrameExecutionSemantics.PresentationCommit,new[]{FrameResource.RandomEventCallbacks},new[]{FrameResource.PresentationEvents},(x,c)=>x.DispatchPendingCallbacks());
            r.AddWaveAction("early.damage.commit",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.DamageRequests},new[]{FrameResource.EnemyHealth,FrameResource.DamageEvents,FrameResource.DeathQueue,FrameResource.EarlyDamageCommitted},s.GraphCommitEarlyDamage);
            r.AddWaveAction("early.resource.commit",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.ResourceRequests},new[]{FrameResource.PlayerResources,FrameResource.ResourceEvents,FrameResource.EarlyResourcesCommitted},s.GraphCommitEarlyResources);
        }

        private static void RegisterSpawning(Registrar r,FrameScheduler s)
        {
            SpawningGroup g=s.Spawning;
            r.AddWaveAt(g.WaveSpawning,"spawning.wave.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.WaveState,FrameResource.EntityLifecycle},new[]{FrameResource.WaveState,FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.EnemyControl,FrameResource.WaveCallbacks},(x,c)=>s.GraphUpdateWaveSpawning(x));
            r.AddWaveAt(g.WaveSpawning,"spawning.wave.callback-dispatch",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.WaveCallbacks,FrameResource.WaveState,FrameResource.PlayerResources,FrameResource.PlayerAttributes,FrameResource.TowerState,FrameResource.TowerCombatCache,FrameResource.ObjectiveState,FrameResource.CorpseState,FrameResource.ComboState},new[]{FrameResource.PlayerResources,FrameResource.PlayerAttributes,FrameResource.TowerState,FrameResource.TowerCombatCache,FrameResource.ObjectiveState,FrameResource.CorpseState,FrameResource.ComboState,FrameResource.WaveEvents,FrameResource.PresentationEvents},(x,c)=>x.DispatchPendingCallbacks());
            r.AddWaveAt(g.Nest,"spawning.nest.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,Empty,(x,c)=>x.SetTurn(c.Turn));
            r.AddWave(g.Nest,"spawning.nest.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.WaveState},new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement},(x,c)=>x.Update(c.Delta));
        }

        private static void RegisterAI(Registrar r,FrameScheduler s)
        {
            AIGroup g=s.AI;
            r.AddWave(g.ZoneControl,"ai.zone-control.update",FrameExecutionSemantics.SerialUpdate,EnemyState,new[]{FrameResource.EnemyControl},(x,c)=>x.Update(c.Delta));
            r.AddWave(g.Magnetize,"ai.magnetize.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.EnemyMovement},new[]{FrameResource.EnemyMovement},(x,c)=>x.Update(c.Delta));
            r.AddWaveAt(g.EnemyStrafe,"ai.enemy-strafe.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl},new[]{FrameResource.EnemyControl},(x,c)=>x.SetTurn());
            r.AddWaveAt(g.EnemyStrafe,"ai.enemy-strafe.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl,FrameResource.EnemyPosition},new[]{FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement},(x,c)=>x.Update());
            r.AddWave(g.EnemyAI,"ai.enemy.prepare",FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.PlayerResources,FrameResource.TowerState},Empty,(x,c)=>x.SetTurn(c.Turn,c.Delta));
            r.AddWaveAt(g.EnemyAI,"ai.enemy.update",FrameTimeDomain.None,FrameExecutionSemantics.InternalParallelCollectSerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.TowerState,FrameResource.PlayerResources},new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.PlayerResources,FrameResource.DamageRequests,FrameResource.ResourceRequests,FrameResource.ReflectRequests,FrameResource.EnemyAiPrepared,FrameResource.EnemyAiDeathFacts,FrameResource.DeathQueue},(x,c)=>x.Update());
            r.AddWaveAt(g.EnemyAbility,"ai.enemy-ability.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,Empty,(x,c)=>x.SetTurn(c.Turn));
            r.AddWave(g.EnemyAbility,"ai.enemy-ability.cooldowns",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl},new[]{FrameResource.EnemyControl},(x,c)=>x.UpdateCooldowns(c.Delta));
            r.AddWaveAt(g.EnemyAbility,"ai.enemy-ability.execute",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.TowerState,FrameResource.PlayerResources},new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.TowerState,FrameResource.PlayerResources,FrameResource.ResourceRequests,FrameResource.TelegraphState},(x,c)=>x.ExecuteAbilities());
            r.AddWaveAt(g.EnemyAbility,"ai.enemy-ability.cast-timers",FrameTimeDomain.None,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EnemyControl},new[]{FrameResource.EnemyControl},(x,c)=>x.TickCastTimers(c.Delta));
            r.AddWaveAt(g.EnemyAbility,"ai.enemy-ability.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl},new[]{FrameResource.EnemyControl},(x,c)=>x.Update());
            r.AddWaveAt(g.Burrow,"ai.burrow.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,Empty,(x,c)=>x.SetTurn(c.Turn));
            r.AddWaveAt(g.Burrow,"ai.burrow.update",FrameTimeDomain.None,FrameExecutionSemantics.InternalParallelCollectSerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl,FrameResource.EnemyPosition},new[]{FrameResource.EnemyControl,FrameResource.BurrowEmergePrepared},(x,c)=>x.Update(c.Delta));
            r.AddWaveAt(g.Burrow,"ai.burrow.apply",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.PlayerResources,FrameResource.BurrowEmergePrepared},new[]{FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.DamageRequests,FrameResource.ResourceRequests},(x,c)=>x.ApplyBurrowEffects());
            r.AddWaveAt(g.Necromancer,"ai.necromancer.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,Empty,(x,c)=>x.SetTurn(c.Turn,c.Turn));
            r.AddWave(g.Necromancer,"ai.necromancer.update",FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.CorpseState},new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.CorpseState},(x,c)=>x.Update(c.Delta));
            r.AddWaveAt(g.LifeLink,"ai.life-link.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle},Empty,(x,c)=>x.SetTurn(c.Turn));
            r.AddWaveAt(g.LifeLink,"ai.life-link.update",FrameTimeDomain.None,FrameExecutionSemantics.InternalParallelCollectSerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition},new[]{FrameResource.EnemyControl,FrameResource.LifeLinkPrepared},(x,c)=>x.Update());
            r.AddWave(g.LifeLink,"ai.life-link.cooldowns",FrameExecutionSemantics.ParallelDisjointWrite,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl},new[]{FrameResource.EnemyControl},(x,c)=>x.DecrementCooldowns(c.Delta));
            r.AddWave(g.EnemyAffix,"ai.enemy-affix.update",FrameExecutionSemantics.SerialUpdate,EnemyState,new[]{FrameResource.EnemyControl},(x,c)=>x.Update(c.Delta));
            r.AddWaveAt(g.ManaBurn,"ai.mana-burn.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,Empty,(x,c)=>x.SetTurn(c.Turn));
            r.AddWaveAt(g.ManaBurn,"ai.mana-burn.update",FrameTimeDomain.None,FrameExecutionSemantics.InternalParallelCollectSerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl,FrameResource.PlayerResources},new[]{FrameResource.PlayerResources,FrameResource.ManaBurnPrepared},(x,c)=>x.Update());
            r.AddWaveAt(g.Lifesteal,"ai.lifesteal.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,EnemyState,new[]{FrameResource.EnemyControl},(x,c)=>x.SetTurn(c.Turn));
            r.AddWaveAt(g.Lifesteal,"ai.lifesteal.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,EnemyState,new[]{FrameResource.EnemyHealth},(x,c)=>x.Update());
            r.AddWaveAt(g.Phase,"ai.phase.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle},Empty,(x,c)=>x.SetTurn(c.Turn));
            r.AddWave(g.Phase,"ai.phase.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl},new[]{FrameResource.EnemyControl},(x,c)=>x.Update(c.Delta));
            r.AddWaveAt(g.Fear,"ai.fear.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,Empty,(x,c)=>x.SetTurn(c.Turn));
            r.AddWaveAt(g.Fear,"ai.fear.update",FrameTimeDomain.None,FrameExecutionSemantics.ParallelDisjointWrite,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.TowerState},new[]{FrameResource.EnemyControl},(x,c)=>x.Update(c.Delta));
            r.AddWave(g.Sapper,"ai.sapper.prepare",FrameExecutionSemantics.SerialPrepare,Empty,Empty,(x,c)=>x.SetTurn(c.Turn,c.Delta));
            r.AddWave(g.Sapper,"ai.sapper.update",FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.TowerState},new[]{FrameResource.EnemyControl,FrameResource.TowerState},(x,c)=>x.Update(c.Delta));
            r.AddWaveAt(g.Sapper,"ai.sapper.recompute",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.TowerState},new[]{FrameResource.TowerCombatCache},(x,c)=>x.RecomputeTowerSlows());
        }

        private static void RegisterMovement(Registrar r,FrameScheduler s)
        {
            MovementGroup g=s.Movement;
            r.AddWaveAt(g.Wound,"movement.wound.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,EnemyState,new[]{FrameResource.EnemyControl},(x,c)=>x.SetTurn(c.Turn));
            r.AddWaveAt(g.Wound,"movement.wound.update",FrameTimeDomain.None,FrameExecutionSemantics.InternalParallelCollectSerialCommit,EnemyState,new[]{FrameResource.DamageRequests,FrameResource.EnemyControl},(x,c)=>x.Update());
            r.AddWaveAt(g.Pathfinding,"movement.pathfinding.prepare",FrameTimeDomain.None,FrameExecutionSemantics.ParallelDisjointWrite,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.EnemyMovement},new[]{FrameResource.EnemyMovement},(x,c)=>x.SetTurn(c.Turn));
            r.AddWaveAt(g.EnemyMovement,"movement.enemy.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.TowerState},Empty,(x,c)=>x.SetTurn(c.Turn));
            r.AddWaveAt(g.PathBlock,"movement.path-block.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyPosition,FrameResource.TerrainState},new[]{FrameResource.TerrainState},(x,c)=>x.Update());
            r.AddWaveAt(g.EnemyMovement,"movement.enemy.update",FrameTimeDomain.None,FrameExecutionSemantics.InternalParallelCollectSerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.TowerState,FrameResource.PlayerResources,FrameResource.WeatherState,FrameResource.TerrainState},new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.TowerState,FrameResource.PlayerResources,FrameResource.DamageRequests,FrameResource.ResourceRequests,FrameResource.BossTrailPrepared},(x,c)=>x.Update());
            r.AddWaveAt(g.DeployableTrap,"movement.deployable-trap.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.EnemyControl,FrameResource.TowerState},new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl,FrameResource.DamageRequests,FrameResource.TowerState},(x,c)=>x.Update());
            r.AddWaveAt(g.PathModifier,"movement.path-modifier.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.TerrainState},new[]{FrameResource.PathModifierPrepared},(x,c)=>x.SetTurn());
            r.AddWave(g.PathModifier,"movement.path-modifier.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.TerrainState,FrameResource.PathModifierPrepared},new[]{FrameResource.EnemyMovement,FrameResource.TerrainState},(x,c)=>x.Update(c.Delta));
            r.AddWaveAt(g.Pull,"movement.pull.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,Empty,(x,c)=>x.SetTurn(c.Turn));
            r.AddWave(g.Pull,"movement.pull.update",FrameExecutionSemantics.ParallelDisjointWrite,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.PlayerAttributes,FrameResource.TerrainState},new[]{FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.PlayerAttributes,FrameResource.TerrainState},(x,c)=>x.Update(c.Delta));
            r.AddWaveAt(g.EnemyHealer,"movement.enemy-healer.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,EnemyState,new[]{FrameResource.EnemyControl},(x,c)=>x.SetTurn(c.Turn));
            r.AddWave(g.EnemyHealer,"movement.enemy-healer.update",FrameExecutionSemantics.SerialCommit,EnemyState,new[]{FrameResource.ResourceRequests},(x,c)=>x.Update(c.Delta));
            r.AddWaveAt(g.StealGold,"movement.steal-gold.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EnemyPosition,FrameResource.PlayerResources},new[]{FrameResource.ResourceRequests},(x,c)=>x.Update());
            r.AddWaveAt(g.Summon,"movement.summon.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyPosition},new[]{FrameResource.EnemyMovement},(x,c)=>x.SetTurn(c.Turn));
            r.AddWave(g.Summon,"movement.summon.update",FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.EnemyMovement},new[]{FrameResource.DamageRequests,FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyPosition,FrameResource.EnemyMovement},(x,c)=>x.Update(c.Delta));
        }

        private static void RegisterTerrain(Registrar r,FrameScheduler s)
        {
            TerrainGroup g=s.Terrain;
            r.AddWaveAt(g.Terrain,"terrain.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle},new[]{FrameResource.TerrainState},(x,c)=>x.SetTurn());
            r.AddWave(g.Terrain,"terrain.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.TerrainState},new[]{FrameResource.EnemyMovement},(x,c)=>x.Update(c.Delta));
            r.AddWaveAt(g.WaveMutator,"terrain.wave-mutator.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle},new[]{FrameResource.WaveMutatorPrepared},(x,c)=>x.SetTurn(c.Turn));
            r.AddWave(g.WaveMutator,"terrain.wave-mutator.update",FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.WaveState,FrameResource.EnemyHealth,FrameResource.EnemyMovement,FrameResource.WaveMutatorPrepared},new[]{FrameResource.EnemyMovement,FrameResource.ResourceRequests},(x,c)=>x.Update(c.Delta));
            r.AddWave(g.EnemyMorph,"terrain.enemy-morph.update",FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyMovement},new[]{FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyMovement,FrameResource.ResourceRequests},(x,c)=>x.Update(c.Delta));
        }

        private static void RegisterCombatSetup(Registrar r,FrameScheduler s)
        {
            CombatSetupGroup g=s.CombatSetup;
            r.AddWaveAt(g.PlayerTowerAttack,"combat-setup.player-attack.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.PlayerAttributes,FrameResource.ComputedAttributes},new[]{FrameResource.PlayerAttackPrepared},(x,c)=>x.SetTurn(c.Turn));
            r.AddWaveAt(g.Hero,"combat-setup.hero.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,Empty,(x,c)=>x.SetTurn(c.Turn));
            r.AddWaveAt(g.TowerAttack,"combat-setup.tower-attack.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyMovement,FrameResource.TowerState,FrameResource.PlayerAttributes,FrameResource.ComputedAttributes,FrameResource.WaveState,FrameResource.WeatherState},new[]{FrameResource.TowerAttackPrepared,FrameResource.TowerState},(x,c)=>x.SetTurn(c.Turn));
            r.AddWaveAt(g.TowerOvercharge,"combat-setup.overcharge.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,new[]{FrameResource.TowerAttackPrepared},(x,c)=>x.SetTurn(c.Turn));
            r.AddWaveAt(g.Heat,"combat-setup.heat.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,new[]{FrameResource.TowerAttackPrepared},(x,c)=>x.SetTurn(c.Turn));
            r.AddWaveAt(g.TowerSynergy,"combat-setup.synergy.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.TowerAttackPrepared},(x,c)=>x.SetTurn());
            r.AddWaveAt(g.TowerFortress,"combat-setup.fortress.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition},new[]{FrameResource.TowerCombatCache},(x,c)=>x.SetTurn());
            r.AddWaveAt(g.TowerLink,"combat-setup.link.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition},new[]{FrameResource.TowerState},(x,c)=>x.SetTurn());
            r.AddWaveAt(g.Skill,"combat-setup.skill.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle,FrameResource.PlayerAttributes,FrameResource.ComputedAttributes},new[]{FrameResource.SkillPrepared},(x,c)=>x.SetTurn(c.Turn));
            r.AddWaveAt(g.AuraTower,"combat-setup.aura.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.AuraPrepared},(x,c)=>x.SetTurn());
            r.AddWaveAt(g.Curse,"combat-setup.curse.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.CursePrepared},(x,c)=>x.SetTurn());
            r.AddWaveAt(g.PullTower,"combat-setup.pull-tower.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.PullTowerPrepared},(x,c)=>x.SetTurn());
            r.AddWaveAt(g.Mana,"combat-setup.mana.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.PlayerAttributes,FrameResource.PlayerResources},new[]{FrameResource.PlayerResources},(x,c)=>x.SetTurn());
            r.AddWaveAt(g.GlobalSkill,"combat-setup.global-skill.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.PlayerAttributes},new[]{FrameResource.PlayerAttributes},(x,c)=>x.SetTurn(c.Turn));
            r.AddWaveAt(g.HitShield,"combat-setup.hit-shield.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle},new[]{FrameResource.HitShieldPrepared},(x,c)=>x.SetTurn(c.Turn));
            r.AddWaveAt(g.HotZone,"combat-setup.hot-zone.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,Empty,(x,c)=>x.SetTurn(c.Turn));
            r.AddWaveAt(g.FrostZone,"combat-setup.frost-zone.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,Empty,(x,c)=>x.SetTurn(c.Turn));
            r.AddWaveAt(g.FrostZone,"combat-setup.frost-zone.update",FrameTimeDomain.None,FrameExecutionSemantics.ParallelDisjointWrite,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.EnemyControl},new[]{FrameResource.TowerState,FrameResource.EnemyControl},(x,c)=>x.Update());
            r.AddWaveAt(g.TerrainZone,"combat-setup.terrain-zone.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,Empty,(x,c)=>x.SetTurn(c.Turn));
            r.AddWave(g.TerrainZone,"combat-setup.terrain-zone.update",FrameExecutionSemantics.SerialCommit,new[]{FrameResource.TerrainZoneState,FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.EnemyControl},new[]{FrameResource.TerrainZoneState,FrameResource.EnemyControl,FrameResource.DamageRequests},(x,c)=>x.Update(c.Delta));
            r.AddWaveAt(g.WanderRoam,"combat-setup.wander.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.EnemyControl},new[]{FrameResource.WanderPrepared},(x,c)=>x.SetTurn(c.Turn));
            r.AddWaveAt(g.WanderRoam,"combat-setup.wander.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.WanderPrepared},new[]{FrameResource.EnemyMovement},(x,c)=>x.Update());
            r.AddWaveAt(g.Taunt,"combat-setup.taunt.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.TauntPrepared},(x,c)=>x.SetTurn());
        }

        private static void RegisterSpatial(Registrar r,FrameScheduler s)
        {
            r.AddWaveAction("spatial.index.rebuild",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EnemyPosition,FrameResource.EntityLifecycle},new[]{FrameResource.SpatialIndex},s.GraphRebuildSpatialIndex);
            SpatialGroup g=s.Spatial;
            r.AddWaveAt(g.PatrolTower,"spatial.patrol.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,new[]{FrameResource.TowerAttackPrepared},(x,c)=>x.SetTurn(c.Turn));
            r.AddWave(g.PatrolTower,"spatial.patrol.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition},new[]{FrameResource.TowerState,FrameResource.EnemyPosition},(x,c)=>x.Update(c.Delta));
            r.AddWaveAt(g.ChronoTower,"spatial.chrono.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.ChronoPrepared},(x,c)=>x.SetTurn());
            r.AddWaveAt(g.ChronoTower,"spatial.chrono.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.EnemyControl,FrameResource.ChronoPrepared},new[]{FrameResource.EnemyControl},(x,c)=>x.Update());
            r.AddWaveAt(g.Fog,"spatial.fog.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,Empty,(x,c)=>x.SetTurn());
            r.AddWaveAt(g.Fog,"spatial.fog.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.SpatialIndex},new[]{FrameResource.TowerCombatCache},(x,c)=>x.Update());
            r.AddWaveAt(g.PointDefense,"spatial.point-defense.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,Empty,Empty,(x,c)=>x.SetTurn(c.Turn));
            r.AddWave(g.PointDefense,"spatial.point-defense.update",FrameExecutionSemantics.SerialCommit,new[]{FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.EnemyProjectileState},new[]{FrameResource.EnemyProjectileState},(x,c)=>x.Update(c.Delta));
            r.AddWave(g.Telegraph,"spatial.telegraph.update",FrameExecutionSemantics.SerialCommit,new[]{FrameResource.TelegraphState,FrameResource.PlayerResources},new[]{FrameResource.TelegraphState,FrameResource.PlayerResources,FrameResource.GameplayEvents},(x,c)=>x.Update(c.Delta));
            r.AddWaveAt(g.Mine,"spatial.mine.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.TowerState},(x,c)=>x.SetTurn(c.Turn));
            r.AddWave(g.Mine,"spatial.mine.update",FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.EnemyHealth},new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.DamageRequests,FrameResource.DeathQueue},(x,c)=>x.Update(c.Delta));
        }

        private static void RegisterCombat(Registrar r,FrameScheduler s)
        {
            CombatGroup g=s.Combat;
            r.AddWaveAt(g.PlayerTowerAttack,"combat.player-attack.update",FrameTimeDomain.None,FrameExecutionSemantics.InternalParallelCollectSerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.PlayerAttributes,FrameResource.ComputedAttributes,FrameResource.PlayerAttackPrepared},new[]{FrameResource.EnemyControl,FrameResource.PlayerAttributes,FrameResource.DamageRequests,FrameResource.DeathQueue,FrameResource.GameplayEvents,FrameResource.PresentationEvents},(x,c)=>x.Update());
            r.AddWave(g.TowerOvercharge,"combat.overcharge.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.TowerState},(x,c)=>x.Update(c.Delta));
            r.AddWave(g.Heat,"combat.heat.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.TowerState},(x,c)=>x.Update(c.Delta));
            r.AddWave(g.Energy,"combat.energy.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition},new[]{FrameResource.TowerState},(x,c)=>x.Update(c.Delta));
            r.AddWaveAt(g.Demolish,"combat.demolish.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.EnemyHealth,FrameResource.SpatialIndex},new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyControl,FrameResource.DamageRequests,FrameResource.DeathQueue},(x,c)=>x.Update());
            r.AddWave(g.HitShield,"combat.hit-shield.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl,FrameResource.HitShieldPrepared},new[]{FrameResource.EnemyControl},(x,c)=>x.Update(c.Delta));
            r.AddWave(g.TowerSabotage,"combat.sabotage.update",FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.EnemyControl,FrameResource.TowerState},new[]{FrameResource.EnemyControl,FrameResource.TowerState},(x,c)=>x.Update(c.Delta));
            r.AddWave(g.TowerStealth,"combat.stealth.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.TowerState},(x,c)=>x.Update(c.Delta));
            r.AddWaveAt(g.TowerSynergy,"combat.synergy.resolve-buff-shares",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition},new[]{FrameResource.TowerState},(x,c)=>x.ResolveBuffShares());
            r.AddWave(g.TowerAttack,"combat.tower-attack.update",FrameExecutionSemantics.InternalParallelCollectSerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.TowerState,FrameResource.TowerCombatCache,FrameResource.PlayerAttributes,FrameResource.PlayerResources,FrameResource.ComputedAttributes,FrameResource.SpatialIndex,FrameResource.WeatherState,FrameResource.ProjectileState,FrameResource.TowerAttackPrepared},new[]{FrameResource.TowerState,FrameResource.EnemyControl,FrameResource.DamageRequests,FrameResource.ResourceRequests,FrameResource.DeathQueue,FrameResource.ProjectileState,FrameResource.GameplayEvents,FrameResource.PresentationEvents,FrameResource.DodgePrepared},(x,c)=>x.Update(c.Delta));
            r.AddWaveAt(g.TowerSynergy,"combat.synergy.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.TowerState,FrameResource.TowerCombatCache},(x,c)=>x.Update());
            r.AddWaveAt(g.TowerLink,"combat.link.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.EnemyControl},new[]{FrameResource.TowerState,FrameResource.EnemyControl},(x,c)=>x.Update());
            r.AddWaveAt(g.AuraTower,"combat.aura.resolve",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.AuraPrepared},new[]{FrameResource.TowerState},(x,c)=>x.ResolveAuraBuffs());
            r.AddWaveAt(g.TowerShrine,"combat.shrine.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.ShrinePrepared},(x,c)=>x.SetTurn());
            r.AddWaveAt(g.TowerShrine,"combat.shrine.resolve",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.ShrinePrepared},new[]{FrameResource.TowerCombatCache},(x,c)=>x.ResolveShrineBuffs());
            r.AddWaveAt(g.TowerBeacon,"combat.beacon.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.BeaconPrepared},(x,c)=>x.SetTurn());
            r.AddWaveAt(g.TowerBeacon,"combat.beacon.resolve",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.BeaconPrepared},new[]{FrameResource.TowerCombatCache},(x,c)=>x.ResolveBeaconBuffs());
            r.AddWaveAt(g.Curse,"combat.curse.resolve",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.EnemyControl,FrameResource.CursePrepared},new[]{FrameResource.EnemyControl},(x,c)=>x.ResolveCurseDebuffs());
            r.AddWave(g.PullTower,"combat.pull-tower.update",FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.EnemyControl,FrameResource.PullTowerPrepared},new[]{FrameResource.TowerState,FrameResource.EnemyControl},(x,c)=>x.Update(c.Delta));
            r.AddWave(g.TowerSilence,"combat.silence.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.TowerState},(x,c)=>x.Update(c.Delta));
            r.AddWave(g.Dispel,"combat.dispel.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.TowerState},(x,c)=>x.Update(c.Delta));
            r.AddWave(g.Projectile,"combat.projectile.update",FrameExecutionSemantics.SerialCommit,new[]{FrameResource.ProjectileState,FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.EnemyControl,FrameResource.PlayerResources},new[]{FrameResource.ProjectileState,FrameResource.DamageRequests,FrameResource.PlayerResources},(x,c)=>x.Update(c.Delta));
            r.AddWave(g.EnemyProjectile,"combat.enemy-projectile.update",FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EnemyProjectileState,FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.PlayerResources},new[]{FrameResource.EnemyProjectileState,FrameResource.PlayerResources},(x,c)=>x.Update(c.Delta));
            r.AddWave(g.Pickup,"combat.pickup.update",FrameExecutionSemantics.SerialCommit,new[]{FrameResource.PickupState,FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.PlayerAttributes,FrameResource.PlayerResources},new[]{FrameResource.PickupState,FrameResource.PlayerAttributes,FrameResource.PlayerResources,FrameResource.ResourceRequests},(x,c)=>x.Update(c.Delta));
            r.AddWave(g.Mana,"combat.mana.update",FrameExecutionSemantics.SerialCommit,new[]{FrameResource.PlayerAttributes,FrameResource.PlayerResources},new[]{FrameResource.PlayerResources,FrameResource.ResourceRequests},(x,c)=>x.Update(c.Delta,false));
            r.AddWave(g.ManaShield,"combat.mana-shield.update",FrameExecutionSemantics.SerialCommit,new[]{FrameResource.PlayerAttributes,FrameResource.PlayerResources},new[]{FrameResource.PlayerResources,FrameResource.ResourceRequests},(x,c)=>x.Update(c.Delta));
            r.AddWave(g.GlobalSkill,"combat.global-skill.update",FrameExecutionSemantics.SerialCommit,new[]{FrameResource.PhaseState,FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.TowerState,FrameResource.PlayerAttributes,FrameResource.PlayerResources},new[]{FrameResource.PlayerAttributes,FrameResource.TowerState,FrameResource.DamageRequests,FrameResource.ResourceRequests,FrameResource.TimeScaleState},(x,c)=>x.Update(c.Delta,false));
            r.AddWave(g.BeamTower,"combat.beam.update",FrameExecutionSemantics.InternalParallelCollectSerialCommit,new[]{FrameResource.BeamState,FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.EnemyControl,FrameResource.SpatialIndex},new[]{FrameResource.BeamDamageRequests},(x,c)=>x.Update(c.Delta));
            r.AddWave(g.Hero,"combat.hero.update",FrameExecutionSemantics.InternalParallelCollectSerialCommit,new[]{FrameResource.HeroState,FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.SpatialIndex},new[]{FrameResource.HeroState,FrameResource.DamageRequests,FrameResource.HeroAttackPrepared},(x,c)=>x.Update(c.Delta));
            r.AddWaveAt(g.SuicideBomb,"combat.suicide-bomb.update",FrameTimeDomain.None,FrameExecutionSemantics.InternalParallelCollectSerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyPosition,FrameResource.TowerState,FrameResource.ReflectRequests},new[]{FrameResource.DamageRequests,FrameResource.ResourceRequests,FrameResource.DeathQueue,FrameResource.ReflectRequests,FrameResource.SuicideExplosionPrepared},(x,c)=>x.Update());
            r.AddWaveAt(g.ReflectTower,"combat.reflect.resolve",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.ReflectRequests,FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition},new[]{FrameResource.ReflectPrepared},(x,c)=>x.ResolveReflect());
            r.AddWaveAt(g.ReflectTower,"combat.reflect.apply",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.ReflectPrepared,FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyHealth},new[]{FrameResource.DamageRequests},(x,c)=>x.ApplyReflectDamage());
            r.AddWave(g.TowerMorph,"combat.tower-morph.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.TowerState},(x,c)=>x.Update(c.Delta));
            r.AddWaveAt(g.Taunt,"combat.taunt.resolve",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyPosition,FrameResource.TowerState,FrameResource.EnemyControl,FrameResource.TauntPrepared},new[]{FrameResource.EnemyControl},(x,c)=>x.ResolveTauntAssignments());
            r.AddWave(g.TowerActiveSkill,"combat.tower-active-skill.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PhaseState,FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.TowerState},(x,c)=>x.Update(c.Delta));
            r.AddWave(g.Aggro,"combat.aggro.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl},new[]{FrameResource.EnemyControl},(x,c)=>x.Update(c.Delta));
            r.AddWave(g.HeroSkill,"combat.hero-skill.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PhaseState,FrameResource.HeroState},new[]{FrameResource.HeroState},(x,c)=>x.Update(c.Delta));
            r.AddWave(g.EchoClone,"combat.echo-clone.update",FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.RealTimeState},new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},(x,c)=>x.Update(c.Delta));
            r.AddWaveAt(g.Bloodlust,"combat.bloodlust.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState},new[]{FrameResource.TowerState},(x,c)=>x.Update(c.Turn));
            r.AddWave(g.Momentum,"combat.momentum.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.PlayerAttributes,FrameResource.TowerState},new[]{FrameResource.PlayerAttributes,FrameResource.TowerState},(x,c)=>x.Update(c.Delta));
            r.AddWave(g.Adrenaline,"combat.adrenaline.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PlayerAttributes},new[]{FrameResource.PlayerAttributes},(x,c)=>x.Update(c.Delta));
            r.AddWave(g.Crest,"combat.crest.update",FrameExecutionSemantics.SerialUpdate,Empty,Empty,(x,c)=>x.Update(c.Delta));
            r.AddWave(g.Culling,"combat.culling.update",FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.EnemyPosition,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.PlayerAttributes},new[]{FrameResource.DamageRequests,FrameResource.PlayerAttributes,FrameResource.DeathQueue},(x,c)=>x.Update(c.Delta));
        }

        private static void RegisterSkillBuff(Registrar r,FrameScheduler s)
        {
            SkillBuffGroup g=s.SkillBuff;
            r.AddWaveAction("effect.commit",FrameTimeDomain.Effect,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.ActiveEffects,FrameResource.EffectRequests},new[]{FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.EffectEvents,FrameResource.DamageRequests,FrameResource.ResourceRequests,FrameResource.EffectsCommitted},s.GraphTickConfiguredEffect);
            r.AddWaveAction("effect.tick.combat",FrameTimeDomain.Combat,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.ActiveEffects},new[]{FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.EffectEvents,FrameResource.DamageRequests,FrameResource.ResourceRequests},c=>s.GraphTickSupplementalEffect(c,Core.GAS.ClockId.Combat));
            r.AddWaveAction("effect.tick.enemy",FrameTimeDomain.Enemy,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.ActiveEffects},new[]{FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.EffectEvents,FrameResource.DamageRequests,FrameResource.ResourceRequests},c=>s.GraphTickSupplementalEffect(c,Core.GAS.ClockId.Enemy));
            r.AddWaveAction("effect.tick.real",FrameTimeDomain.Real,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.ActiveEffects},new[]{FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.EffectEvents,FrameResource.DamageRequests,FrameResource.ResourceRequests},c=>s.GraphTickSupplementalEffect(c,Core.GAS.ClockId.RealTime));
            r.AddWaveAction("effect.tick.global",FrameTimeDomain.Global,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.ActiveEffects},new[]{FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.EffectEvents,FrameResource.DamageRequests,FrameResource.ResourceRequests},c=>s.GraphTickSupplementalEffect(c,Core.GAS.ClockId.Global));
            r.AddWave(g.Buff,"skill-buff.buff.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.EntityLifecycle,FrameResource.ActiveEffects,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.PlayerResources},new[]{FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.DamageRequests,FrameResource.LegacyDotRequests},(x,c)=>x.Update(c.Delta));
            r.AddWaveAt(g.Skill,"skill-buff.skill.resolve-damage",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.SkillDamageRequests,FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl},new[]{FrameResource.DamageRequests},(x,c)=>x.ResolveSkillDamage());
            r.AddWaveAt(g.Buff,"skill-buff.buff.resolve-dot",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.LegacyDotRequests,FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl},new[]{FrameResource.DamageRequests},(x,c)=>x.ResolveDotDamage());
            r.AddWave(g.ElementalReaction,"skill-buff.elemental.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.DamageEvents,FrameResource.EntityLifecycle,FrameResource.EnemyControl,FrameResource.ElementalReactionPrepared},new[]{FrameResource.EnemyControl,FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.ElementalReactionPrepared},(x,c)=>x.Update(c.Delta));
            r.AddWaveAt(g.ElementalReaction,"skill-buff.elemental.resolve",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.ElementalReactionPrepared},new[]{FrameResource.DamageRequests},(x,c)=>x.ResolveReactionDamage());
            r.AddWave(g.Bleed,"skill-buff.bleed.update",FrameExecutionSemantics.ParallelDisjointWrite,EnemyState,new[]{FrameResource.EnemyControl,FrameResource.BleedPrepared},(x,c)=>x.Update(c.Delta));
            r.AddWaveAt(g.Bleed,"skill-buff.bleed.resolve",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.BleedPrepared},new[]{FrameResource.DamageRequests},(x,c)=>x.ResolveBleedDamage());
            r.AddWave(g.Frostbite,"skill-buff.frostbite.update",FrameExecutionSemantics.ParallelDisjointWrite,EnemyState,new[]{FrameResource.EnemyControl,FrameResource.FrostbitePrepared},(x,c)=>x.Update(c.Delta));
            r.AddWaveAt(g.Frostbite,"skill-buff.frostbite.resolve",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.FrostbitePrepared},new[]{FrameResource.DamageRequests},(x,c)=>x.ResolveFrostbiteDamage());
            r.AddWave(g.HealingZone,"skill-buff.healing-zone.update",FrameExecutionSemantics.SerialCommit,new[]{FrameResource.CorpseState,FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyPosition,FrameResource.PlayerResources,FrameResource.HealingZonePrepared},new[]{FrameResource.CorpseState,FrameResource.EnemyHealth,FrameResource.PlayerResources,FrameResource.ResourceRequests,FrameResource.HealingZonePrepared},(x,c)=>x.Update(c.Delta));
            r.AddWave(g.Mark,"skill-buff.mark.update",FrameExecutionSemantics.SerialUpdate,EnemyState,new[]{FrameResource.EnemyControl},(x,c)=>x.Update(c.Delta));
            r.AddWave(g.DeathMark,"skill-buff.death-mark.update",FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl},new[]{FrameResource.DamageRequests,FrameResource.EnemyControl},(x,c)=>x.Update(c.Delta));
            r.AddWaveAt(g.HealAura,"skill-buff.heal-aura.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,TowerState,new[]{FrameResource.HealAuraPrepared},(x,c)=>x.SetTurn());
            r.AddWave(g.HealAura,"skill-buff.heal-aura.update",FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.TowerState,FrameResource.HealAuraPrepared},new[]{FrameResource.TowerState},(x,c)=>x.Update(c.Delta));
            r.AddWaveAt(g.ThornsAura,"skill-buff.thorns-aura.prepare",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,TowerState,new[]{FrameResource.ThornsAuraPrepared},(x,c)=>x.SetTurn());
            r.AddWave(g.ThornsAura,"skill-buff.thorns-aura.update",FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.TowerState,FrameResource.ThornsAuraPrepared},new[]{FrameResource.TowerState,FrameResource.DamageRequests},(x,c)=>x.Update(c.Delta,g.ThornsAuraPlayerId));
            r.AddWave(g.Skill,"ability.commit",FrameExecutionSemantics.InternalParallelCollectSerialCommit,new[]{FrameResource.AbilityRequests,FrameResource.PlayerResources,FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyPosition,FrameResource.EnemyControl,FrameResource.EnemyMovement,FrameResource.TowerState,FrameResource.CorpseState,FrameResource.PlayerAttributes,FrameResource.PlayerSnapshotState,FrameResource.SkillPrepared},new[]{FrameResource.AbilityRequests,FrameResource.EffectRequests,FrameResource.DamageRequests,FrameResource.SkillDamageRequests,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.PlayerResources,FrameResource.ResourceRequests,FrameResource.TimeScaleState,FrameResource.EntityLifecycle,FrameResource.CorpseState,FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.PlayerSnapshotState,FrameResource.AbilitiesCommitted},(x,c)=>x.Update(c.Delta));
            r.AddWave(g.Wisp,"skill-buff.wisp.update",FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.PlayerResources},new[]{FrameResource.EnemyControl,FrameResource.EnemyMovement,FrameResource.PlayerResources,FrameResource.ResourceRequests},(x,c)=>x.Update(c.Delta));
            r.AddWave(g.Rally,"skill-buff.rally.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PlayerAttributes,FrameResource.TowerState},new[]{FrameResource.PlayerAttributes,FrameResource.TowerCombatCache},(x,c)=>x.Update(c.Delta));
        }

        private static void RegisterPrimaryCommit(Registrar r,FrameScheduler s)
        {
            r.AddWaveAction("damage.commit",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl,FrameResource.DamageRequests},new[]{FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.DamageEvents,FrameResource.DeathQueue,FrameResource.DamageCommitted},s.GraphCommitGameplayDamage);
            r.AddWaveAction("resource.commit",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.PlayerAttributes,FrameResource.EnemyHealth,FrameResource.ResourceRequests},new[]{FrameResource.PlayerResources,FrameResource.EnemyHealth,FrameResource.ResourceEvents,FrameResource.ResourcesCommitted},s.GraphCommitGameplayResources);
            r.AddWaveAction("gameplay-event.commit",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.DamageEvents,FrameResource.ResourceEvents,FrameResource.EffectEvents},new[]{FrameResource.GameplayEvents,FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.EffectEvents,FrameResource.DamageRequests,FrameResource.ResourceRequests,FrameResource.GameplayEventsCommitted},s.GraphCommitGameplayEvents);
            r.AddWaveAction("primary-death.resolve",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.DeathQueue},new[]{FrameResource.PrimaryDeathFacts},s.GraphPrepareDeaths);
            r.AddWaveAction("primary-death.callback-dispatch",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,
                new[]{FrameResource.PrimaryDeathFacts,FrameResource.DamageEvents,FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.TowerState,FrameResource.TowerCombatCache,FrameResource.PlayerAttributes,FrameResource.PlayerResources,FrameResource.ComboState,FrameResource.ObjectiveState,FrameResource.CorpseState,FrameResource.PickupState,FrameResource.ActiveEffects},
                new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.PlayerResources,FrameResource.Rewards,FrameResource.PresentationEvents,FrameResource.DamageEvents,FrameResource.GameplayEvents,FrameResource.ComboState,FrameResource.PlayerAttributes,FrameResource.ObjectiveState,FrameResource.CorpseState,FrameResource.PickupState,FrameResource.TowerState,FrameResource.TowerCombatCache,FrameResource.AbilityRequests,FrameResource.DamageRequests,FrameResource.ResourceRequests,FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.PrimaryDeathsResolved},s.GraphDispatchDeathCallbacks);
        }

        private static void RegisterPostDeath(Registrar r,FrameScheduler s)
        {
            PostDeathGroup g=s.PostDeath;
            r.AddWaveAt(g.EnemyFission,"post-death.fission.update",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.PrimaryDeathsResolved,FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyMovement,FrameResource.EnemyControl},new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.EnemyControl},(x,c)=>x.Update());
            r.AddWaveAt(g.LifeLink,"post-death.life-link.resolve",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.PrimaryDeathsResolved,FrameResource.EnemyControl},new[]{FrameResource.DamageRequests},(x,c)=>x.ResolveBreakPenalties());
            r.AddWave(g.Objective,"post-death.objective.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PrimaryDeathsResolved,FrameResource.ObjectiveState},new[]{FrameResource.ObjectiveState},(x,c)=>x.Update(c.Delta,GameState.WavePhase));
            r.AddWave(g.ResourceNode,"post-death.resource-node.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.ObjectiveState,FrameResource.PlayerResources},new[]{FrameResource.PlayerResources,FrameResource.ObjectiveState},(x,c)=>x.Update(c.Delta,GameState.WavePhase));
            r.AddWave(g.TowerIncome,"post-death.tower-income.update",FrameExecutionSemantics.SerialUpdate,EconomyRead,EconomyWrite,(x,c)=>x.Update(c.Delta));
            r.AddWave(g.CorpseEffect,"post-death.corpse.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PrimaryDeathsResolved,FrameResource.CorpseState,FrameResource.EnemyPosition,FrameResource.EnemyControl,FrameResource.TowerState},new[]{FrameResource.CorpseState,FrameResource.EffectRequests,FrameResource.DamageRequests,FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.EnemyControl,FrameResource.EnemyMovement,FrameResource.TowerState},(x,c)=>x.Update(c.Delta));
            r.AddWave(g.Combo,"post-death.combo.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PrimaryDeathsResolved,FrameResource.ComboState},new[]{FrameResource.ComboState,FrameResource.PlayerAttributes},(x,c)=>x.Update(c.Delta));
            r.AddWave(g.DoomClock,"post-death.doom-clock.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.ObjectiveState,FrameResource.WaveState},new[]{FrameResource.ObjectiveState,FrameResource.WaveState},(x,c)=>x.Update(c.Delta,GameState.WavePhase));
            r.AddWave(g.SoulHarvest,"post-death.soul-harvest.update",FrameExecutionSemantics.SerialUpdate,new[]{FrameResource.PrimaryDeathsResolved,FrameResource.PlayerResources},new[]{FrameResource.PlayerResources},(x,c)=>x.Update(c.Delta));
        }

        private static void RegisterCascadeCommit(Registrar r,FrameScheduler s)
        {
            r.AddWaveAction("cascade.damage.commit",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.EnemyControl,FrameResource.DamageRequests},new[]{FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.DamageEvents,FrameResource.DeathQueue,FrameResource.CascadeDamageCommitted},s.GraphCommitGameplayDamage);
            r.AddWaveAction("cascade.resource.commit",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.PlayerAttributes,FrameResource.EnemyHealth,FrameResource.ResourceRequests},new[]{FrameResource.PlayerResources,FrameResource.EnemyHealth,FrameResource.ResourceEvents,FrameResource.CascadeResourcesCommitted},s.GraphCommitGameplayResources);
            r.AddWaveAction("cascade-death.resolve",FrameTimeDomain.None,FrameExecutionSemantics.SerialPrepare,new[]{FrameResource.DeathQueue},new[]{FrameResource.CascadeDeathFacts},s.GraphPrepareDeaths);
            r.AddWaveAction("cascade-death.callback-dispatch",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,
                new[]{FrameResource.CascadeDeathFacts,FrameResource.DamageEvents,FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.EnemyPosition,FrameResource.EnemyMovement,FrameResource.TowerState,FrameResource.TowerCombatCache,FrameResource.PlayerAttributes,FrameResource.PlayerResources,FrameResource.ComboState,FrameResource.ObjectiveState,FrameResource.CorpseState,FrameResource.PickupState,FrameResource.ActiveEffects},
                new[]{FrameResource.EntityLifecycle,FrameResource.EnemyHealth,FrameResource.EnemyControl,FrameResource.PlayerResources,FrameResource.Rewards,FrameResource.PresentationEvents,FrameResource.DamageEvents,FrameResource.GameplayEvents,FrameResource.ComboState,FrameResource.PlayerAttributes,FrameResource.ObjectiveState,FrameResource.CorpseState,FrameResource.PickupState,FrameResource.TowerState,FrameResource.TowerCombatCache,FrameResource.AbilityRequests,FrameResource.DamageRequests,FrameResource.ResourceRequests,FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.CascadeDeathsResolved},s.GraphDispatchDeathCallbacks);
            r.AddWaveAction("post-death.gameplay-event.commit",FrameTimeDomain.None,FrameExecutionSemantics.SerialCommit,new[]{FrameResource.EntityLifecycle,FrameResource.DamageEvents,FrameResource.ResourceEvents,FrameResource.EffectEvents,FrameResource.CascadeDeathsResolved},new[]{FrameResource.GameplayEvents,FrameResource.ActiveEffects,FrameResource.AttributeModifiers,FrameResource.EffectEvents,FrameResource.DamageRequests,FrameResource.ResourceRequests,FrameResource.PostDeathGameplayEventsCommitted},s.GraphCommitPostDeathGameplayEvents);
        }

        private sealed class Registrar
        {
            private readonly FrameGraphBuilder _builder; private readonly bool _strict;
            private FrameNodeId? _lastAll,_lastBuild,_lastWave,_lastOther;
            private FrameTimeDomain _waveDomain=FrameTimeDomain.Enemy;
            private static readonly FramePhaseMask OtherPhases=FramePhaseMask.All&~(FramePhaseMask.Build|FramePhaseMask.Wave);
            private readonly FrameGraphCompositionKind _compositionKind;
            public Registrar(FrameGraphBuilder builder,FrameGraphCompositionKind compositionKind){_builder=builder;_compositionKind=compositionKind;_strict=compositionKind==FrameGraphCompositionKind.ProductionRegistry;}
            public void BeginAll(){_lastAll=null;}
            public void StartBranches(){if(!_lastAll.HasValue)throw new InvalidOperationException("Frame prelude is empty.");_lastBuild=_lastAll;_lastWave=_lastAll;_lastOther=_lastAll;}
            public void UseWaveTimeDomain(FrameTimeDomain domain){_waveDomain=domain;}
            public void AddAll(string id,FrameTimeDomain domain,FrameExecutionSemantics semantics,FrameResource[] reads,FrameResource[] writes,Action<NodeExecutionContext> action,params OptionalFrameDependency[] optional)=>AddAction(ref _lastAll,id,FramePhaseMask.All,domain,semantics,reads,writes,action,optional);
            public void AddBuildAction(string id,FrameTimeDomain domain,FrameExecutionSemantics semantics,FrameResource[] reads,FrameResource[] writes,Action<NodeExecutionContext> action)=>AddAction(ref _lastBuild,id,FramePhaseMask.Build,domain,semantics,reads,writes,action);
            public void AddWaveAction(string id,FrameTimeDomain domain,FrameExecutionSemantics semantics,FrameResource[] reads,FrameResource[] writes,Action<NodeExecutionContext> action)=>AddAction(ref _lastWave,id,FramePhaseMask.Wave,domain,semantics,reads,writes,action);
            public void AddOtherAction(string id,FrameExecutionSemantics semantics,FrameResource[] reads,FrameResource[] writes,Action<NodeExecutionContext> action)=>AddAction(ref _lastOther,id,OtherPhases,FrameTimeDomain.None,semantics,reads,writes,action);
            public void AddBuild<T>(T? system,string id,FrameExecutionSemantics semantics,FrameResource[] reads,FrameResource[] writes,Action<T,NodeExecutionContext> action)where T:class=>AddSlot(ref _lastBuild,system,id,FramePhaseMask.Build,FrameTimeDomain.Build,semantics,reads,writes,action);
            public void AddWave<T>(T? system,string id,FrameExecutionSemantics semantics,FrameResource[] reads,FrameResource[] writes,Action<T,NodeExecutionContext> action)where T:class=>AddSlot(ref _lastWave,system,id,FramePhaseMask.Wave,_waveDomain,semantics,reads,writes,action);
            public void AddWaveAt<T>(T? system,string id,FrameTimeDomain domain,FrameExecutionSemantics semantics,FrameResource[] reads,FrameResource[] writes,Action<T,NodeExecutionContext> action)where T:class=>AddSlot(ref _lastWave,system,id,FramePhaseMask.Wave,domain,semantics,reads,writes,action);
            private void AddSlot<T>(ref FrameNodeId? last,T? system,string id,FramePhaseMask phase,FrameTimeDomain domain,FrameExecutionSemantics semantics,FrameResource[] reads,FrameResource[] writes,Action<T,NodeExecutionContext> action)where T:class
            {var owner=typeof(T).FullName??typeof(T).Name;var metadata=Metadata(id,phase,domain,semantics,reads,writes,last,bindingId:owner+"."+FrameAdapterBindingCatalog.Require(id),owner:owner,requiresSystemBinding:true,requiredDependencies:new[]{"ComponentStore",owner});if(system==null){string? reason=ReviewedDisabledReason(id);if(_strict&&reason==null)throw new FrameGraphValidationException($"Required production system call '{id}' is not configured.");_builder.DeclareDisabled(metadata,reason??"direct composition slot missing; policy=Disabled");return;}_builder.AddAvailableDependency(owner);_builder.Add(new FrameNodeAdapter(metadata,new DelegateSystem(c=>action(system,c))));last=metadata.Id;}
            private void AddAction(ref FrameNodeId? last,string id,FramePhaseMask phase,FrameTimeDomain domain,FrameExecutionSemantics semantics,FrameResource[] reads,FrameResource[] writes,Action<NodeExecutionContext> action,params OptionalFrameDependency[] optional)
            {const string owner="BattleSystemECS.Core.FrameScheduler";var metadata=Metadata(id,phase,domain,semantics,reads,writes,last,optional,owner+"."+FrameAdapterBindingCatalog.Require(id),owner,false);_builder.Add(new FrameNodeAdapter(metadata,new DelegateSystem(action)));last=metadata.Id;}
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
