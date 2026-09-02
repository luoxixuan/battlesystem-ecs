#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using BattleSystemECS.Components;
using BattleSystemECS.Core.GAS;

namespace BattleSystemECS.Core
{
    [Flags]
    public enum FramePhaseMask { None=0, Init=1<<0, Build=1<<1, Wave=1<<2, Intermission=1<<3, BranchSelection=1<<4, LevelComplete=1<<5, GameOver=1<<6, Victory=1<<7, Other=1<<8, All=Init|Build|Wave|Intermission|BranchSelection|LevelComplete|GameOver|Victory|Other }
    [Flags]
    public enum FrameTimeDomain { None=0, Real=1<<0, Enemy=1<<1, Combat=1<<2, Effect=1<<3, Build=1<<4, Global=1<<5 }
    public enum FrameExecutionSemantics { SerialPrepare, SerialUpdate, ParallelDisjointWrite, InternalParallelCollectSerialCommit, SerialCommit, PresentationCommit }
    public enum OptionalDependencyPolicy { Disabled, NoOp, Fail }
    public enum FrameGraphCompositionKind { Direct, ProductionRegistry, ManualBenchmark }
    public enum FrameSchedulerExecutionMode { Graph, Legacy }
    public enum FrameScenarioKind { Gameplay, FixedPopulationBenchmark }
    public enum FrameResourceOrigin { ExternalInput, PersistentState, FrameProduced }
    public enum FrameResource
    {
        PhaseState, TimeScaleState, TimeContext, EntityLifecycle, EnemyHealth, EnemyControl,
        EnemyPosition, EnemyMovement, TowerState, TowerCombatCache, PlayerAttributes,
        PlayerResources, ComputedAttributes, WaveState, WeatherState, TerrainState, SpatialIndex,
        AbilityRequests, EffectRequests, DamageRequests, ResourceRequests, ActiveEffects,
        AttributeModifiers, DamageEvents, ResourceEvents, EffectEvents, GameplayEvents, DeathQueue,
        Rewards, ObjectiveState, CorpseState, ComboState, PresentationEvents, ThreatScore,
        AbilitiesCommitted, EffectsCommitted, EarlyDamageCommitted, EarlyResourcesCommitted,
        DamageCommitted, ResourcesCommitted, CascadeDamageCommitted, CascadeResourcesCommitted,
        GameplayEventsCommitted, PostDeathGameplayEventsCommitted, AttributesAggregated,
        PrimaryDeathsResolved, CascadeDeathsResolved, FrameRuntimeState, PlayerSnapshotState, PickupState,
        ProjectileState, EnemyProjectileState, TelegraphState, ReflectRequests, ReflectPrepared,
        HeroState, PlayerAttackPrepared, TowerAttackPrepared, SkillPrepared, AuraPrepared,
        CursePrepared, PullTowerPrepared, HitShieldPrepared, WanderPrepared, TauntPrepared,
        ChronoPrepared, ShrinePrepared, BeaconPrepared, WaveMutatorPrepared, PathModifierPrepared,
        SkillDamageRequests, LegacyDotRequests, TerrainZoneState, RealTimeState, BeamState,
        BeamDamageRequests, PrimaryDeathFacts, CascadeDeathFacts, WaveEvents, DeferredResolverState,
        ElementalReactionPrepared, HealAuraPrepared, ThornsAuraPrepared, HealingZonePrepared,
        RandomEventCallbacks, WaveCallbacks, PersistentStatePublished, EnemyAiPrepared,
        BurrowEmergePrepared, LifeLinkPrepared, LifeLinkBreakPenaltyPrepared,
        SuicideExplosionPrepared, HeroAttackPrepared, BleedPrepared, FrostbitePrepared,
        EnemyAiDeathFacts, BossTrailPrepared, ManaBurnPrepared, DodgePrepared
    }

    public readonly struct FrameNodeId : IEquatable<FrameNodeId>, IComparable<FrameNodeId>
    {
        public string Value { get; }
        public FrameNodeId(string value) { if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("NodeId cannot be empty.", nameof(value)); Value=value; }
        public int CompareTo(FrameNodeId other) => string.Compare(Value, other.Value, StringComparison.Ordinal);
        public bool Equals(FrameNodeId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object? obj) => obj is FrameNodeId other && Equals(other);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value ?? string.Empty);
        public override string ToString() => Value ?? string.Empty;
        public static implicit operator FrameNodeId(string value) => new FrameNodeId(value);
    }

    public readonly struct TimeContext
    {
        public float RawDelta { get; } public float RealDelta { get; } public float EnemyDelta { get; }
        public float CombatDelta { get; } public float EffectDelta { get; } public float BuildDelta { get; }
        public float GlobalDelta { get; } public int Turn { get; } public int Frame { get; } public PhaseContext Phase { get; } public ClockId EffectClock { get; }
        public TimeContext(float rawDelta,float realDelta,float enemyDelta,float combatDelta,float effectDelta,float buildDelta,float globalDelta,int turn,int frame,PhaseContext phase,ClockId effectClock=ClockId.Combat)
        { RawDelta=rawDelta;RealDelta=realDelta;EnemyDelta=enemyDelta;CombatDelta=combatDelta;EffectDelta=effectDelta;BuildDelta=buildDelta;GlobalDelta=globalDelta;Turn=turn;Frame=frame;Phase=phase;EffectClock=effectClock; }
        public float DeltaFor(FrameTimeDomain domain) => domain switch { FrameTimeDomain.None=>0f,FrameTimeDomain.Real=>RealDelta,FrameTimeDomain.Enemy=>EnemyDelta,FrameTimeDomain.Combat=>CombatDelta,FrameTimeDomain.Effect=>EffectDelta,FrameTimeDomain.Build=>BuildDelta,FrameTimeDomain.Global=>GlobalDelta,_=>throw new ArgumentOutOfRangeException(nameof(domain),domain,"A node must declare exactly one time domain.") };
        public float EffectDeltaFor(ClockId clock) => clock switch { ClockId.Build=>BuildDelta,ClockId.Enemy=>EnemyDelta,ClockId.Combat=>CombatDelta,ClockId.RealTime=>RealDelta,ClockId.Global=>GlobalDelta,_=>throw new ArgumentOutOfRangeException(nameof(clock)) };
    }

    public sealed class FrameExecutionContext
    {
        private TimeContext _time; private bool _timeFrozen;
        public ComponentStore Store { get; } public TimeContext Time=>_time; public int Turn=>_time.Turn; public int Frame=>_time.Frame; public bool IsTimeFrozen=>_timeFrozen;
        internal FrameExecutionContext(ComponentStore store,float rawDelta,int turn,int frame,PhaseContext phase) { Store=store;Reset(rawDelta,turn,frame,phase); }
        internal void Reset(float rawDelta,int turn,int frame,PhaseContext phase) { _time=new TimeContext(rawDelta,rawDelta,rawDelta,rawDelta,rawDelta,rawDelta,rawDelta,turn,frame,phase);_timeFrozen=false; }
        internal void FreezeTime(TimeContext time) { if(_timeFrozen)throw new InvalidOperationException("TimeContext may only be frozen once per frame.");_time=time;_timeFrozen=true; }
    }

    public readonly struct NodeExecutionContext : BattleSystemECS.Engine.IFrameContext
    {
        private readonly FrameExecutionContext _frame; private readonly FrameTimeDomain _domain;
        internal NodeExecutionContext(FrameExecutionContext frame,FrameTimeDomain domain){_frame=frame;_domain=domain;}
        public float Delta=>_frame.Time.DeltaFor(_domain); public float DeltaTime=>Delta; public int Turn=>_frame.Turn; public int Frame=>_frame.Frame; public PhaseContext Phase=>_frame.Time.Phase; internal ComponentStore Store=>_frame.Store; internal ClockId EffectClock=>_frame.Time.EffectClock;
        internal TimeContext Time=>_frame.Time; internal void FreezeTime(TimeContext time)=>_frame.FreezeTime(time);
    }
    public interface ISystem { void Execute(NodeExecutionContext context); }
    public sealed class DelegateSystem:ISystem { private readonly Action<NodeExecutionContext> _execute; public DelegateSystem(Action<NodeExecutionContext> execute){_execute=execute??throw new ArgumentNullException(nameof(execute));} public void Execute(NodeExecutionContext context)=>_execute(context); }

    public readonly struct OptionalFrameDependency
    {
        public string Name { get; } public OptionalDependencyPolicy MissingPolicy { get; }
        public OptionalFrameDependency(string name,OptionalDependencyPolicy missingPolicy){Name=name??throw new ArgumentNullException(nameof(name));MissingPolicy=missingPolicy;}
    }
    public sealed class FrameNodeMetadata
    {
        public FrameNodeId Id{get;} public IReadOnlyList<FrameResource> Reads=>AccessProfile.Reads; public IReadOnlyList<FrameResource> Writes=>AccessProfile.Writes; public FrameAccessProfile AccessProfile{get;}
        public IReadOnlyList<FrameNodeId> Before{get;} public IReadOnlyList<FrameNodeId> After{get;} public IReadOnlyList<string> RequiredDependencies{get;}
        public IReadOnlyList<OptionalFrameDependency> OptionalDependencies{get;} public FramePhaseMask ActivePhases{get;} public FrameTimeDomain TimeDomain{get;} public FrameExecutionSemantics ExecutionSemantics{get;}
        public FrameNodeMetadata(FrameNodeId id,FramePhaseMask activePhases,FrameTimeDomain timeDomain,FrameExecutionSemantics executionSemantics,IReadOnlyList<FrameResource>? reads=null,IReadOnlyList<FrameResource>? writes=null,IReadOnlyList<FrameNodeId>? before=null,IReadOnlyList<FrameNodeId>? after=null,IReadOnlyList<string>? requiredDependencies=null,IReadOnlyList<OptionalFrameDependency>? optionalDependencies=null,FrameBindingId bindingId=default,FrameAccessOwner owner=default,FrameAccessEvidence evidence=FrameAccessEvidence.Unreviewed,FrameAccessReviewId reviewId=default,FrameAccessReviewRecord? review=null,bool requiresSystemBinding=false)
        {Id=id;ActivePhases=activePhases;TimeDomain=timeDomain;ExecutionSemantics=executionSemantics;var readCopy=Copy(reads);var writeCopy=Copy(writes);AccessProfile=new FrameAccessProfile(bindingId,owner,evidence,reviewId,review,readCopy,writeCopy,requiresSystemBinding);Before=Copy(before);After=Copy(after);RequiredDependencies=Copy(requiredDependencies);OptionalDependencies=Copy(optionalDependencies);}
        private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T>? source){if(source==null||source.Count==0)return Array.Empty<T>();var copy=new T[source.Count];for(int i=0;i<source.Count;i++)copy[i]=source[i];return Array.AsReadOnly(copy);}
    }

    /// <summary>绑定事实产生的运行时帧节点声明，不从 manifest 派生。</summary>
    internal sealed class FrameNodeRuntimeDeclaration : IEquatable<FrameNodeRuntimeDeclaration>
    {
        internal string NodeId { get; }
        internal string RegistrationId { get; }
        internal FramePhaseMask Phase { get; }
        internal FrameExecutionSemantics ExecutionPolicy { get; }
        internal string[] RequiredTokens { get; }

        internal FrameNodeRuntimeDeclaration(string nodeId, string registrationId, FramePhaseMask phase,
            FrameExecutionSemantics executionPolicy, IReadOnlyList<string> requiredTokens)
        {
            NodeId = nodeId;
            RegistrationId = registrationId;
            Phase = phase;
            ExecutionPolicy = executionPolicy;
            RequiredTokens = new string[requiredTokens.Count];
            for (int i = 0; i < requiredTokens.Count; i++) RequiredTokens[i] = requiredTokens[i];
        }

        public bool Equals(FrameNodeRuntimeDeclaration? other)
        {
            if (other == null || !string.Equals(NodeId, other.NodeId, StringComparison.Ordinal) ||
                !string.Equals(RegistrationId, other.RegistrationId, StringComparison.Ordinal) ||
                Phase != other.Phase || ExecutionPolicy != other.ExecutionPolicy ||
                RequiredTokens.Length != other.RequiredTokens.Length) return false;
            for (int i = 0; i < RequiredTokens.Length; i++)
                if (!string.Equals(RequiredTokens[i], other.RequiredTokens[i], StringComparison.Ordinal)) return false;
            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as FrameNodeRuntimeDeclaration);
        public override int GetHashCode() => NodeId.GetHashCode(StringComparison.Ordinal);
    }
    public sealed class FrameNodeAdapter : BattleSystemECS.Engine.IFrameNode { public FrameNodeMetadata Metadata{get;} public ISystem System{get;} public string Id=>Metadata.Id.Value; public FrameNodeAdapter(FrameNodeMetadata metadata,ISystem system){Metadata=metadata??throw new ArgumentNullException(nameof(metadata));System=system??throw new ArgumentNullException(nameof(system));} public void Execute(BattleSystemECS.Engine.IFrameContext context){if(!(context is NodeExecutionContext typed))throw new ArgumentException("Frame node requires the engine frame context.",nameof(context));System.Execute(typed);} }
    public sealed class FrameCompositionDiagnostic { public FrameNodeMetadata Metadata{get;} public FrameNodeId NodeId=>Metadata.Id; public OptionalDependencyPolicy Policy{get;} public string Reason{get;} public FrameBindingId BindingId=>Metadata.AccessProfile.BindingId; public FrameAccessOwner Owner=>Metadata.AccessProfile.Owner; public FrameAccessEvidence Evidence=>Metadata.AccessProfile.Evidence; public FrameAccessReviewId ReviewId=>Metadata.AccessProfile.ReviewId; public FrameAccessReviewRecord? Review=>Metadata.AccessProfile.Review; public FrameCompositionDiagnostic(FrameNodeMetadata metadata,OptionalDependencyPolicy policy,string reason){Metadata=metadata??throw new ArgumentNullException(nameof(metadata));Policy=policy;Reason=reason;} }
    public sealed class FrameGraphValidationException:InvalidOperationException { public FrameGraphValidationException(string message):base(message){} }

    public sealed class FrameGraph : BattleSystemECS.Engine.IFrameExecutionPlan
    {
        private readonly FrameNodeAdapter[] _nodes; private readonly IReadOnlyList<FrameNodeAdapter> _readOnlyNodes; private readonly IReadOnlyList<BattleSystemECS.Engine.IFrameNode> _engineNodes; private readonly IReadOnlyList<FrameCompositionDiagnostic> _diagnostics; private readonly IReadOnlyList<string> _availableDependencies;
        internal FrameGraph(FrameNodeAdapter[] nodes,FrameCompositionDiagnostic[] diagnostics,string[] availableDependencies,string topologyHash,string reviewRoot,FrameGraphCompositionKind compositionKind){_nodes=nodes;_readOnlyNodes=Array.AsReadOnly(nodes);var engineNodes=new BattleSystemECS.Engine.IFrameNode[nodes.Length];for(int i=0;i<nodes.Length;i++)engineNodes[i]=nodes[i];_engineNodes=Array.AsReadOnly(engineNodes);_diagnostics=Array.AsReadOnly(diagnostics);_availableDependencies=Array.AsReadOnly(availableDependencies);TopologyHash=topologyHash;ReviewRoot=reviewRoot;CompositionKind=compositionKind;}
        public IReadOnlyList<FrameNodeAdapter> Nodes=>_readOnlyNodes; public IReadOnlyList<FrameCompositionDiagnostic> Diagnostics=>_diagnostics; public IReadOnlyList<string> AvailableDependencies=>_availableDependencies; public string TopologyHash{get;} public string ReviewRoot{get;} public FrameGraphCompositionKind CompositionKind{get;}
        IReadOnlyList<BattleSystemECS.Engine.IFrameNode> BattleSystemECS.Engine.IFrameExecutionPlan.Nodes=>_engineNodes;
        public void Execute(FrameExecutionContext context){FramePhaseMask current=FrameGraphValidator.ToMask(context.Time.Phase.Kind);for(int i=0;i<_nodes.Length;i++){FrameNodeAdapter node=_nodes[i];if((node.Metadata.ActivePhases&current)==0)continue;node.System.Execute(new NodeExecutionContext(context,node.Metadata.TimeDomain));}}
    }
    public sealed class FrameGraphBuilder
    {
        private readonly List<FrameNodeAdapter> _nodes=new List<FrameNodeAdapter>(); private readonly List<FrameCompositionDiagnostic> _diagnostics=new List<FrameCompositionDiagnostic>(); private readonly HashSet<string> _availableDependencies=new HashSet<string>(StringComparer.Ordinal); private readonly FrameGraphCompositionKind _compositionKind; private bool _requireReviewedProfiles;
        public FrameGraphBuilder(FrameGraphCompositionKind compositionKind=FrameGraphCompositionKind.Direct){_compositionKind=compositionKind;}
        public FrameGraphBuilder Add(FrameNodeAdapter node){_nodes.Add(node??throw new ArgumentNullException(nameof(node)));return this;} public FrameGraphBuilder AddAvailableDependency(string name){_availableDependencies.Add(name??throw new ArgumentNullException(nameof(name)));return this;}
        public FrameGraphBuilder DeclareDisabled(FrameNodeMetadata metadata,string reason){_diagnostics.Add(new FrameCompositionDiagnostic(metadata,OptionalDependencyPolicy.Disabled,reason));return this;}
        public FrameGraphBuilder RequireReviewedProfiles(){_requireReviewedProfiles=true;return this;}
        public FrameGraph BuildAndSeal()=>FrameGraphValidator.ValidateAndBuild(_nodes,_diagnostics,_availableDependencies,_requireReviewedProfiles,_compositionKind);
    }

    public static class FrameGraphValidator
    {
        private static readonly FramePhaseMask[] AtomicPhases={FramePhaseMask.Init,FramePhaseMask.Build,FramePhaseMask.Wave,FramePhaseMask.Intermission,FramePhaseMask.BranchSelection,FramePhaseMask.LevelComplete,FramePhaseMask.GameOver,FramePhaseMask.Victory,FramePhaseMask.Other};
        internal static FrameGraph ValidateAndBuild(IReadOnlyList<FrameNodeAdapter> input,IReadOnlyList<FrameCompositionDiagnostic> diagnostics,HashSet<string> availableDependencies,bool requireReviewedProfiles,FrameGraphCompositionKind compositionKind)
        {
            var byId=new Dictionary<FrameNodeId,int>();var declaredIds=new HashSet<FrameNodeId>();
            for(int i=0;i<diagnostics.Count;i++)
            {
                FrameCompositionDiagnostic diagnostic=diagnostics[i];
                if(!declaredIds.Add(diagnostic.NodeId))
                    throw new FrameGraphValidationException($"Duplicate frame node '{diagnostic.NodeId}'.");
                ValidateMetadata(diagnostic.Metadata,false);
                ValidateDependencyDeclarations(diagnostic.Metadata);
                FrameAccessReviewRecord? review=diagnostic.Review;
                if(requireReviewedProfiles&&((diagnostic.Evidence!=FrameAccessEvidence.SourceReviewed&&diagnostic.Evidence!=FrameAccessEvidence.DisabledUnsafe)||
                    review==null||!review.IsApproved||string.IsNullOrWhiteSpace(diagnostic.ReviewId.Value)||
                    string.IsNullOrWhiteSpace(diagnostic.BindingId.Value)||string.IsNullOrWhiteSpace(diagnostic.Owner.Value)))
                    throw new FrameGraphValidationException($"Disabled production node '{diagnostic.NodeId}' requires approved reviewed evidence id, binding, and owner.");
            }
            for(int i=0;i<input.Count;i++){FrameNodeMetadata m=input[i].Metadata;if(!declaredIds.Add(m.Id)||byId.ContainsKey(m.Id))throw new FrameGraphValidationException($"Duplicate frame node '{m.Id}'.");byId.Add(m.Id,i);ValidateMetadata(m,requireReviewedProfiles);ValidateDependencyDeclarations(m);}
            string reviewRoot=requireReviewedProfiles
                ?FrameAccessReviewCatalog.ValidateApprovedSnapshot(input,diagnostics,availableDependencies,compositionKind)
                :string.Empty;
            var enabled=new bool[input.Count];for(int i=0;i<enabled.Length;i++)enabled[i]=true;
            for(int i=0;i<input.Count;i++)
            {
                FrameNodeMetadata m=input[i].Metadata;
                for(int d=0;d<m.RequiredDependencies.Count;d++)
                {
                    string dep=m.RequiredDependencies[d];
                    if(!availableDependencies.Contains(dep))throw new FrameGraphValidationException($"Node '{m.Id}' requires missing dependency '{dep}'.");
                }
                for(int d=0;d<m.OptionalDependencies.Count;d++)
                {
                    OptionalFrameDependency dep=m.OptionalDependencies[d];
                    if(availableDependencies.Contains(dep.Name))continue;
                    if(dep.MissingPolicy==OptionalDependencyPolicy.Fail)throw new FrameGraphValidationException($"Node '{m.Id}' optional dependency '{dep.Name}' uses Fail policy and is missing.");
                    if(dep.MissingPolicy==OptionalDependencyPolicy.Disabled)enabled[i]=false;
                }
            }
            var nodes=new List<FrameNodeAdapter>();var enabledIndex=new Dictionary<FrameNodeId,int>();for(int i=0;i<input.Count;i++){if(!enabled[i])continue;enabledIndex.Add(input[i].Metadata.Id,nodes.Count);nodes.Add(input[i]);}
            int count=nodes.Count;var edges=new List<int>[count];var indegree=new int[count];for(int i=0;i<count;i++)edges[i]=new List<int>();
            void AddEdge(FrameNodeId from,FrameNodeId to,FrameNodeId owner){if(!enabledIndex.TryGetValue(from,out int fi))throw new FrameGraphValidationException($"Node '{owner}' references missing or disabled ordering dependency '{from}'.");if(!enabledIndex.TryGetValue(to,out int ti))throw new FrameGraphValidationException($"Node '{owner}' references missing or disabled ordering dependency '{to}'.");if(fi==ti)throw new FrameGraphValidationException($"Node '{owner}' cannot depend on itself.");if(!edges[fi].Contains(ti)){edges[fi].Add(ti);indegree[ti]++;}}
            for(int i=0;i<count;i++){FrameNodeMetadata m=nodes[i].Metadata;for(int d=0;d<m.After.Count;d++)AddEdge(m.After[d],m.Id,m.Id);for(int d=0;d<m.Before.Count;d++)AddEdge(m.Id,m.Before[d],m.Id);}
            var sorted=new FrameNodeAdapter[count];var emitted=new bool[count];
            for(int output=0;output<count;output++){int next=-1;FrameNodeId nextId=default(FrameNodeId);for(int i=0;i<count;i++){if(emitted[i]||indegree[i]!=0)continue;FrameNodeId id=nodes[i].Metadata.Id;if(next<0||id.CompareTo(nextId)<0){next=i;nextId=id;}}if(next<0){var remaining=new List<string>();for(int i=0;i<count;i++)if(!emitted[i])remaining.Add(nodes[i].Metadata.Id.ToString());remaining.Sort(StringComparer.Ordinal);throw new FrameGraphValidationException("Frame graph contains a cycle involving: "+string.Join(", ",remaining)+".");}emitted[next]=true;sorted[output]=nodes[next];for(int e=0;e<edges[next].Count;e++)indegree[edges[next][e]]--;}
            var sortedIndex=new Dictionary<FrameNodeId,int>();for(int i=0;i<sorted.Length;i++)sortedIndex.Add(sorted[i].Metadata.Id,i);bool[,] reachable=BuildReachability(sorted,sortedIndex);ValidateResources(sorted,reachable);
            var dependencyArray=new string[availableDependencies.Count];availableDependencies.CopyTo(dependencyArray);Array.Sort(dependencyArray,StringComparer.Ordinal);
            var diagnosticArray=new FrameCompositionDiagnostic[diagnostics.Count];for(int i=0;i<diagnostics.Count;i++)diagnosticArray[i]=diagnostics[i];Array.Sort(diagnosticArray,(a,b)=>a.NodeId.CompareTo(b.NodeId));return new FrameGraph(sorted,diagnosticArray,dependencyArray,ComputeTopologyHash(sorted,diagnosticArray,availableDependencies,compositionKind),reviewRoot,compositionKind);
        }
        private static bool[,] BuildReachability(FrameNodeAdapter[] sorted,Dictionary<FrameNodeId,int> index){int count=sorted.Length;var reachable=new bool[count,count];for(int i=0;i<count;i++){FrameNodeMetadata m=sorted[i].Metadata;for(int d=0;d<m.After.Count;d++)reachable[index[m.After[d]],i]=true;for(int d=0;d<m.Before.Count;d++)reachable[i,index[m.Before[d]]]=true;}for(int k=0;k<count;k++)for(int i=0;i<count;i++)if(reachable[i,k])for(int j=0;j<count;j++)if(reachable[k,j])reachable[i,j]=true;return reachable;}
        private static void ValidateResources(FrameNodeAdapter[] nodes,bool[,] reachable)
        {
            for(int p=0;p<AtomicPhases.Length;p++){FramePhaseMask phase=AtomicPhases[p];foreach(FrameResource resource in Enum.GetValues(typeof(FrameResource))){var writers=new List<int>();var readers=new List<int>();for(int i=0;i<nodes.Length;i++){if((nodes[i].Metadata.ActivePhases&phase)==0)continue;if(Contains(nodes[i].Metadata.Writes,resource))writers.Add(i);if(Contains(nodes[i].Metadata.Reads,resource))readers.Add(i);}if(IsSingleWriter(resource)&&writers.Count>1)throw new FrameGraphValidationException($"Single-writer resource '{resource}' has {writers.Count} writers in phase '{phase}': {WriterNames(nodes,writers)}.");for(int a=0;a<writers.Count;a++)for(int b=a+1;b<writers.Count;b++){int left=writers[a],right=writers[b];if(!reachable[left,right]&&!reachable[right,left])throw new FrameGraphValidationException($"Resource '{resource}' has unordered shared writers '{nodes[left].Metadata.Id}' and '{nodes[right].Metadata.Id}' in phase '{phase}'.");}for(int r=0;r<readers.Count;r++){int reader=readers[r],reachableWriters=0;for(int w=0;w<writers.Count;w++)if(reachable[writers[w],reader])reachableWriters++;FrameResourceOrigin origin=ResourceOrigin(resource);if(reachableWriters==0&&origin==FrameResourceOrigin.PersistentState&&!HasPersistentPublication(nodes,reachable,reader,resource))throw new FrameGraphValidationException($"Node '{nodes[reader].Metadata.Id}' reads persistent resource '{resource}' without a reachable publication boundary in phase '{phase}'.");if(reachableWriters==0&&origin==FrameResourceOrigin.FrameProduced)throw new FrameGraphValidationException($"Node '{nodes[reader].Metadata.Id}' reads resource '{resource}' without a reachable writer in phase '{phase}'.");}}}
        }
        private static bool HasPersistentPublication(FrameNodeAdapter[] nodes,bool[,] reachable,int reader,FrameResource resource)
        {
            for(int i=0;i<nodes.Length;i++)
            {
                bool publisher=Contains(nodes[i].Metadata.Writes,FrameResource.PersistentStatePublished)&&Contains(nodes[i].Metadata.Reads,resource);
                if(publisher&&(i==reader||reachable[i,reader]))return true;
            }
            return false;
        }
        private static bool Contains(IReadOnlyList<FrameResource> values,FrameResource value){for(int i=0;i<values.Count;i++)if(values[i]==value)return true;return false;}
        private static string WriterNames(FrameNodeAdapter[] nodes,List<int> writers){var names=new string[writers.Count];for(int i=0;i<writers.Count;i++)names[i]=nodes[writers[i]].Metadata.Id.ToString();return string.Join(", ",names);}
        internal static FrameResourceOrigin ResourceOrigin(FrameResource r)=>r switch
        {
            FrameResource.PhaseState or FrameResource.TimeScaleState or FrameResource.AbilityRequests or
                FrameResource.EffectRequests => FrameResourceOrigin.ExternalInput,
            FrameResource.EntityLifecycle or FrameResource.EnemyHealth or FrameResource.EnemyControl or
                FrameResource.EnemyPosition or FrameResource.EnemyMovement or FrameResource.TowerState or
                FrameResource.TowerCombatCache or FrameResource.PlayerAttributes or FrameResource.PlayerResources or
                FrameResource.ComputedAttributes or FrameResource.WaveState or FrameResource.WeatherState or
                FrameResource.TerrainState or FrameResource.ActiveEffects or FrameResource.AttributeModifiers or
                FrameResource.Rewards or FrameResource.ObjectiveState or FrameResource.CorpseState or
                FrameResource.ComboState or FrameResource.ThreatScore or FrameResource.PlayerSnapshotState or
                FrameResource.PickupState or FrameResource.ProjectileState or FrameResource.EnemyProjectileState or
                FrameResource.TelegraphState or FrameResource.ReflectRequests or FrameResource.HeroState or
                FrameResource.TerrainZoneState or FrameResource.RealTimeState or FrameResource.BeamState or
                FrameResource.DeathQueue or FrameResource.FrameRuntimeState or FrameResource.SkillDamageRequests or FrameResource.LegacyDotRequests or
                FrameResource.BeamDamageRequests or FrameResource.ElementalReactionPrepared or
                FrameResource.HealingZonePrepared =>
                FrameResourceOrigin.PersistentState,
            _ => FrameResourceOrigin.FrameProduced
        };
        private static bool IsSingleWriter(FrameResource r)=>r switch{FrameResource.TimeContext or FrameResource.PersistentStatePublished or FrameResource.AbilitiesCommitted or FrameResource.EffectsCommitted or FrameResource.EarlyDamageCommitted or FrameResource.EarlyResourcesCommitted or FrameResource.DamageCommitted or FrameResource.ResourcesCommitted or FrameResource.CascadeDamageCommitted or FrameResource.CascadeResourcesCommitted or FrameResource.GameplayEventsCommitted or FrameResource.PostDeathGameplayEventsCommitted or FrameResource.AttributesAggregated or FrameResource.PrimaryDeathFacts or FrameResource.CascadeDeathFacts or FrameResource.PrimaryDeathsResolved or FrameResource.CascadeDeathsResolved=>true,_=>false};
        private static void ValidateMetadata(FrameNodeMetadata m,bool requireReviewedProfile)
        {
            if(string.IsNullOrWhiteSpace(m.Id.Value))throw new FrameGraphValidationException("Frame node id cannot be empty.");
            if(requireReviewedProfile&&(m.AccessProfile.Evidence!=FrameAccessEvidence.SourceReviewed||m.AccessProfile.Review==null||!m.AccessProfile.Review.IsApproved||string.IsNullOrWhiteSpace(m.AccessProfile.ReviewId.Value)||string.IsNullOrWhiteSpace(m.AccessProfile.BindingId.Value)||string.IsNullOrWhiteSpace(m.AccessProfile.Owner.Value)))throw new FrameGraphValidationException($"Production node '{m.Id}' requires an approved source-reviewed access profile with stable review id, binding, and owner.");
            if(m.ActivePhases==FramePhaseMask.None||(m.ActivePhases&~FramePhaseMask.All)!=0)throw new FrameGraphValidationException($"Node '{m.Id}' has invalid phase mask '{m.ActivePhases}'.");int dv=(int)m.TimeDomain;int known=(int)(FrameTimeDomain.Real|FrameTimeDomain.Enemy|FrameTimeDomain.Combat|FrameTimeDomain.Effect|FrameTimeDomain.Build|FrameTimeDomain.Global);if((dv&~known)!=0||(dv!=0&&(dv&(dv-1))!=0))throw new FrameGraphValidationException($"Node '{m.Id}' must declare exactly one valid time domain, got '{m.TimeDomain}'.");if(!Enum.IsDefined(typeof(FrameExecutionSemantics),m.ExecutionSemantics))throw new FrameGraphValidationException($"Node '{m.Id}' has invalid execution semantics '{m.ExecutionSemantics}'.");for(int i=0;i<AtomicPhases.Length;i++){if((m.ActivePhases&AtomicPhases[i])==0)continue;if((m.TimeDomain==FrameTimeDomain.Enemy||m.TimeDomain==FrameTimeDomain.Combat)&&AtomicPhases[i]!=FramePhaseMask.Wave)throw new FrameGraphValidationException($"Node '{m.Id}' cannot use {m.TimeDomain} time in phase '{AtomicPhases[i]}'.");if(m.TimeDomain==FrameTimeDomain.Build&&AtomicPhases[i]!=FramePhaseMask.Build)throw new FrameGraphValidationException($"Node '{m.Id}' cannot use Build time in phase '{AtomicPhases[i]}'.");if(m.TimeDomain==FrameTimeDomain.Effect&&AtomicPhases[i]!=FramePhaseMask.Build&&AtomicPhases[i]!=FramePhaseMask.Wave)throw new FrameGraphValidationException($"Node '{m.Id}' cannot use Effect time in phase '{AtomicPhases[i]}'.");}ValidateResourcesDefined(m.Id,m.Reads);ValidateResourcesDefined(m.Id,m.Writes);
        }
        private static void ValidateResourcesDefined(FrameNodeId id,IReadOnlyList<FrameResource> resources){for(int i=0;i<resources.Count;i++)if(!Enum.IsDefined(typeof(FrameResource),resources[i]))throw new FrameGraphValidationException($"Node '{id}' declares invalid frame resource '{resources[i]}'.");}
        private static void ValidateDependencyDeclarations(FrameNodeMetadata metadata)
        {
            for(int i=0;i<metadata.RequiredDependencies.Count;i++)
                if(string.IsNullOrWhiteSpace(metadata.RequiredDependencies[i]))
                    throw new FrameGraphValidationException($"Node '{metadata.Id}' declares an empty required dependency.");
            for(int i=0;i<metadata.OptionalDependencies.Count;i++)
            {
                OptionalFrameDependency dependency=metadata.OptionalDependencies[i];
                if(string.IsNullOrWhiteSpace(dependency.Name))
                    throw new FrameGraphValidationException($"Node '{metadata.Id}' declares an empty optional dependency.");
                if(!Enum.IsDefined(typeof(OptionalDependencyPolicy),dependency.MissingPolicy))
                    throw new FrameGraphValidationException($"Node '{metadata.Id}' has invalid optional dependency policy '{dependency.MissingPolicy}'.");
            }
        }
        private static string ComputeTopologyHash(FrameNodeAdapter[] nodes,FrameCompositionDiagnostic[] diagnostics,HashSet<string> availableDependencies,FrameGraphCompositionKind compositionKind)
        {
            var text=new StringBuilder();
            text.Append("composition|").Append((int)compositionKind).AppendLine();
            var dependencies=new string[availableDependencies.Count];
            availableDependencies.CopyTo(dependencies);
            Array.Sort(dependencies,StringComparer.Ordinal);
            text.Append("available");
            for(int i=0;i<dependencies.Length;i++)text.Append('|').Append(dependencies[i]);
            text.AppendLine();
            for(int i=0;i<nodes.Length;i++)
            {
                FrameNodeMetadata m=nodes[i].Metadata;
                text.Append(m.Id).Append('|').Append((int)m.ActivePhases).Append('|').Append((int)m.TimeDomain)
                    .Append('|').Append((int)m.ExecutionSemantics).Append('|').Append(m.AccessProfile.BindingId)
                    .Append('|').Append(m.AccessProfile.Owner).Append('|').Append((int)m.AccessProfile.Evidence)
                    .Append('|').Append(m.AccessProfile.ReviewId).Append('|').Append(m.AccessProfile.RequiresSystemBinding);
                AppendReview(text,m.AccessProfile.Review);
                Append(text,m.Reads);Append(text,m.Writes);Append(text,m.After);Append(text,m.Before);Append(text,m.RequiredDependencies);
                text.Append('|');
                for(int d=0;d<m.OptionalDependencies.Count;d++)
                {
                    if(d>0)text.Append(',');
                    text.Append(m.OptionalDependencies[d].Name).Append(':').Append((int)m.OptionalDependencies[d].MissingPolicy);
                }
                text.AppendLine();
            }
            for(int i=0;i<diagnostics.Length;i++)
            {
                text.Append("disabled|").Append(diagnostics[i].NodeId).Append('|').Append(diagnostics[i].Policy)
                    .Append('|').Append(diagnostics[i].BindingId).Append('|').Append(diagnostics[i].Owner)
                    .Append('|').Append((int)diagnostics[i].Evidence).Append('|').Append(diagnostics[i].ReviewId);
                AppendReview(text,diagnostics[i].Review);
                FrameNodeMetadata metadata=diagnostics[i].Metadata;
                text.Append('|').Append((int)metadata.ActivePhases).Append('|').Append((int)metadata.TimeDomain)
                    .Append('|').Append((int)metadata.ExecutionSemantics).Append('|').Append(metadata.AccessProfile.RequiresSystemBinding);
                Append(text,metadata.Reads);Append(text,metadata.Writes);Append(text,metadata.After);Append(text,metadata.Before);
                Append(text,metadata.RequiredDependencies);text.Append('|');
                for(int d=0;d<metadata.OptionalDependencies.Count;d++)
                {
                    if(d>0)text.Append(',');
                    text.Append(metadata.OptionalDependencies[d].Name).Append(':').Append((int)metadata.OptionalDependencies[d].MissingPolicy);
                }
                text.Append('|').Append(diagnostics[i].Reason).AppendLine();
            }
            using var sha=SHA256.Create();
            byte[] hash=sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()));
            var result=new StringBuilder(hash.Length*2);
            for(int i=0;i<hash.Length;i++)result.Append(hash[i].ToString("x2"));
            return result.ToString();
        }
        private static void AppendReview(StringBuilder text,FrameAccessReviewRecord? review)
        {
            text.Append('|');
            if(review==null)return;
            text.Append(review.ArtifactSha256).Append(':').Append(review.MetadataFingerprint)
                .Append(':').Append((int)review.Disposition).Append(':').Append(review.TransitiveCallees);
        }
        private static void Append<T>(StringBuilder text,IReadOnlyList<T> values){text.Append('|');for(int i=0;i<values.Count;i++){if(i>0)text.Append(',');text.Append(values[i]);}}
        internal static FramePhaseMask ToMask(PhaseContextKind phase)=>phase switch{PhaseContextKind.Init=>FramePhaseMask.Init,PhaseContextKind.Build=>FramePhaseMask.Build,PhaseContextKind.Wave=>FramePhaseMask.Wave,PhaseContextKind.Intermission=>FramePhaseMask.Intermission,PhaseContextKind.BranchSelection=>FramePhaseMask.BranchSelection,PhaseContextKind.LevelComplete=>FramePhaseMask.LevelComplete,PhaseContextKind.GameOver=>FramePhaseMask.GameOver,PhaseContextKind.Victory=>FramePhaseMask.Victory,_=>FramePhaseMask.Other};
    }
}
