#nullable enable
using System;
using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Core
{
    /// <summary>
    /// Central registry for all game systems — creates, wires dependencies, and assigns to FrameScheduler groups.
    /// Extracted from GameManager.Initialize() to eliminate the ~300-line "spaghetti" init method.
    ///
    /// 新系统必须在 schema v3 中声明 owner、依赖、策略和 frame binding，
    /// 由生成器发出 typed recipe，再经 ProductionSystemInstaller 按 Construction、
    /// Wiring、Binding 三阶段执行并封存图；CreateAll/WireDependencies/AssignToGroups
    /// 仅是受 session guard 约束的兼容 facade。
    /// </summary>
    public sealed partial class SystemRegistry
    {
        private enum InstallationState
        {
            New,
            Creating,
            Created,
            Wiring,
            Wired,
            Binding,
            Bound,
            Failed
        }

        private InstallationState _installationState;
        internal string? LastRegistrationFailureId { get; private set; }
        internal RegistrationStage? LastRegistrationFailureStage { get; private set; }
        private readonly List<Core.GAS.TriggerDefinition> _runtimeTriggers = new List<Core.GAS.TriggerDefinition>();
        private Core.GAS.GameplayEffectDefinition _runtimeComboEffect;
        // ── Map ──
        public MapSystem? Map { get; private set; }

        // ── Spawning ──
        public WaveSpawningSystem? WaveSpawning { get; private set; }
        public NestSystem? Nest { get; private set; }

        // ── Economy ──
        public GoldSystem? Gold { get; private set; }
        public UpgradeSystem? Upgrade { get; private set; }
        public InterestSystem? Interest { get; private set; }

        // ── Skills & Buffs ──
        public SkillSystem? Skill { get; private set; }
        // ── Buffs & Debuffs ──
        public BuffSystem? Buff { get; private set; }
        // Elemental reactions (element timer decay + shield-break reactions + exposure window).
        // Was never constructed before this wiring — see the CreateAll comment for what that cost.
        public ElementalReactionSystem? ElementalReaction { get; private set; }
        public ComboSystem? Combo { get; private set; }
        public AutoSkillSystem? AutoSkill { get; private set; }
        public ManaSystem? Mana { get; private set; }
        // Round 175 Direction 1 — Mana Shield: mana → damage-absorption shield
        public ManaShieldSystem? ManaShield { get; private set; }
        public GlobalSkillSystem? GlobalSkill { get; private set; }
        public ProductionAbilityPayloadHandler? AbilityPayloads { get; private set; }
        // Round 107 Direction 6 — Target Mark subsystem (stack-based debuff counter)
        public MarkSystem? Mark { get; private set; }
        // Round 200 Direction 5 — Death Mark subsystem (stack-based execute counter + damage bonus)
        public DeathMarkSystem? DeathMark { get; private set; }
        // Round 206 Direction 1 — Culling subsystem (HP-threshold instant execute for high-burst towers).
        //   Per-hit hot path: TowerAttackSystem calls CullingSystem.TryCull(towerId, enemyId, hitDamage)
        //   after a successful hit lands. The system fires OnCullingKilled on threshold/damage-gate
        //   match, increments PlayerCullingStacks, and queues the enemy for death. Per-frame Update is
        //   a no-op (event-driven). OnWaveStart resets per-player stacks. Sentinel-gated: Enabled=false
        //   → TryCull returns false and no event fires.
        public CullingSystem? Culling { get; private set; }
        // Round 109 Direction 5 — Time Rewind snapshot ring (HP / Mana / Shield restore)
        public TimeRewindSnapshotSystem? TimeRewind { get; private set; }

        // ── Towers ──
        public TowerPlacementSystem? TowerPlacement { get; private set; }
        public TowerAttackSystem? TowerAttack { get; private set; }
        public TowerUpgradeSystem? TowerUpgrade { get; private set; }
        public TowerExperienceSystem? TowerExperience { get; private set; }
        public TowerSynergySystem? TowerSynergy { get; private set; }
        // Round 180 Direction 5 — Fortress Aura (clustered-tower damage/speed bonus).
        //   Same-type neighbors within FortressRadius → cached dmg/atk-spd bonuses.
        //   SetTurn runs a single O(N²) pass; consumers (TowerAttackSystem) read cached fields.
        public TowerFortressSystem? TowerFortress { get; private set; }
        public KillCooldownResetSystem? KillCooldownReset { get; private set; }
        // ── Kill-Triggered Player Sustain (HealOnKill / ManaOnKill) ───────────
        public HealOnKillSystem? HealOnKill { get; private set; }
        // Round176 Direction2 — Bloodlust: per-tower kill-stacking attack-speed / damage buff.
        // OnTowerKill handler increments stacks; per-frame Update sheds decayed stacks
        // and re-derives the cached damage / speed mults for the TowerAttack hot path.
        public BloodlustSystem? Bloodlust { get; private set; }
        // Round178 Direction6 — Pre-fight Buff: BuildPhase末「3-选-1」出战 buff.
        // System reads PreFight config Pool, rolls N weighted-random options into
        // per-player option slots on BuildPhase start. OnWaveStart writes the
        // chosen buff's DamageMult/SpeedMult to every active tower's cache.
        // OnWaveComplete clears cache + player selection. Sentinel-gated.
        public PreFightBuffSystem? PreFightBuff { get; private set; }
        // Round174+ Direction3 — Momentum: global per-(wave-time) ramping damage /
        // attack-speed buff shared by all active towers. Per-player timer advances
        // only while a wave is running (latch driven by WaveSpawningSystem
        // OnWaveStart/OnWaveComplete); tier is recomputed each frame and the
        // cached damage / speed bonuses are stamped onto every active tower.
        // Sentinel-gated: Enabled=false / degenerate config → force-clear cache.
        public MomentumSystem? Momentum { get; private set; }
        // Round 207 Direction 2 — Adrenaline: low-HP / critical-HP player-side buff plus
        // one-shot Rush state on tier 1 → 2 entry. Per-frame Update() walks the
        // MAX_PLAYERS slots, derives the tier from the live HP ratio, and stamps the
        // cached attack-speed bonus (additive) + cooldown mult (multiplicative) into
        // the per-player cache arrays. The rush window is detected on tier change and
        // force-fires player towers for RushDurationFrames (read by
        // PlayerTowerAttackSystem). Sentinel-gated: Enabled=false / degenerate
        // thresholds → single O(MAX_PLAYERS) clear-pass.
        public AdrenalineSystem? Adrenaline { get; private set; }
        // Round 178+ Direction 5 — Crest / Tide System. Wave-indexed periodic
        // enemy / player buffs. Reads GameConfig.Crest + ComponentStore and
        // stamps the per-enemy / per-player cache fields on OnWaveStart /
        // OnWaveComplete.
        public CrestSystem? Crest { get; private set; }
        public TowerMorphSystem? TowerMorph { get; private set; }
        public AuraTowerSystem? AuraTower { get; private set; }
        public CurseAuraSystem? Curse { get; private set; }
        public PullTowerSystem? PullTower { get; private set; }
        public TauntSystem? Taunt { get; private set; }
        public BleedSystem? Bleed { get; private set; }
        // Round 170 Direction 6 — Frostbite (non-stacking %-of-maxHP DoT)
        public FrostbiteSystem? Frostbite { get; private set; }
        // Round 122 Direction 2 — Heal Aura System (passive tower-to-tower healing).
        public HealAuraSystem? HealAura { get; private set; }
        // Round 126 Direction 4 — Thorns Aura System (passive tower-centered damage aura on enemies).
        public ThornsAuraSystem? ThornsAura { get; private set; }
        // Round 187 Direction 4 — Rally Buff (player-damage → tower atk-spd buff).
        public RallySystem? Rally { get; private set; }
        // Round 138 — Per-Tower Active Skill System (manual cast, cooldown-tick + public API).
        //   Constructed in the same block as the other tower systems; CombatGroup wires
        //   it after Taunt. Pure state machine — no per-tower field writes when
        //   ActiveSkillId==-1, so cost is O(activeTowers) per frame in the worst case.
        public TowerActiveSkillSystem? TowerActiveSkill { get; private set; }
        // Round 142 方向5 — Aggro / Focus Fire System. Player-driven mark-focus command.
        //   Public API: MarkFocusTower(enemyId, towerId, duration) +
        //   MarkFocusTowerBulk(enemyIds, towerId, duration) + ClearFocus / HasFocus /
        //   GetFocusTowerId read helpers + OnEnemyDestroyed lifecycle hook. Per-frame
        //   Update() is O(1) when no focus is active (sentinel-gated fast path).
        public AggroSystem? Aggro { get; private set; }
        // Round 201 Direction 8 — Echo Clone System. Public API:
        //   ForceSpawnEcho(parentId) for tests / scripted spawns + IsEcho / DestroyEcho
        //   read-write helpers. Per-frame Update() is O(1) when no opt-in parent
        //   is on the field (sentinel-gated). WireEchoClone in scheduler wires it
        //   into CombatGroup last (so the spawn roll sees parent's resolved auras).
        public EchoCloneSystem? EchoClone { get; private set; }
        // Round 145 Direction 3 — Per-Tower Modifier Pool (塔类型专精重随).
        //   Rolls ONE modifier per tower from a weighted pool at placement time.
        //   BuildPhase-only public API (RollAtPlacement / RerollModifier / ClearModifier);
        //   read helpers are pure array reads and may be called from any frame phase.
        //   No per-frame Update() — modifiers are persistent for the tower's lifetime.
        public TowerModifierSystem? TowerModifier { get; private set; }
        // Round 128 Direction 5 — Fire Trail System. Thin wrapper that exposes
        // SpawnTrail(x, y, radius, dps, duration) for callers that want to drop a
        // brief burning patch at a position. No per-frame Update — the actual
        // zone tick and DoT work is delegated to the existing CorpseEffectSystem
        // (effectType 3 = fire DoT). Zero overhead when no caller invokes it.
        public FireTrailSystem? FireTrail { get; private set; }
        public ProjectileSystem? Projectile { get; private set; }
        public ChronoTowerSystem? ChronoTower { get; private set; }
        // Round 106 Direction 2 — Mine / Trap tower (proximity-triggered AoE)
        public MineSystem? Mine { get; private set; }
        // Round 173 Direction 1 — Shrine Tower (persistent pure-buff aura, no attack).
        //   Reads TowerIsShrine / TowerShrineAuraType / TowerShrineRadius / TowerShrinePotency
        //   on every active tower and writes per-frame cache arrays that downstream
        //   systems can consume (GoldSystem / ManaSystem / TowerAttackSystem in v2).
        //   Cost: O(activeShrines × activeTowers) when ≥1 shrine on field, O(1) otherwise.
        public TowerShrineSystem? TowerShrine { get; private set; }
        // Round 177 Direction 2 — Beacon Tower (active command-post broadcast buff, no attack).
        //   Reads TowerIsBeacon / TowerBeaconRadius / TowerBeaconDmgBonus / TowerBeaconAtkSpdBonus
        //   on every active tower and writes per-frame cache arrays that downstream systems
        //   can consume (TowerAttackSystem reads dmg cache, TowerSynergySystem reads atk-spd cache).
        //   Always applies BOTH damage and attack-speed bonuses together. Stacks additively.
        //   Cost: O(activeBeacons × activeTowers) when ≥1 beacon on field, O(1) otherwise.
        public TowerBeaconSystem? TowerBeacon { get; private set; }

        // ── Player ──
        public PlayerTowerAttackSystem? PlayerTowerAttack { get; private set; }
        public HeroSystem? Hero { get; private set; }
        // Round 144 方向4 — Hero Active Skill Set. Per-hero, per-slot cooldown-gated
        //   skill triggers. Soft-coupled: gate + cooldown + log; effect dispatch is
        //   a follow-up (mirrors the Round 138 TowerActiveSkillSystem approach).
        public HeroSkillSystem? HeroSkill { get; private set; }

        // ── Enemies ──
        public EnemyMovementSystem? EnemyMovement { get; private set; }
        public EnemyAISystem? EnemyAI { get; private set; }
        public EnemyAbilitySystem? EnemyAbility { get; private set; }
        public EnemyFissionSystem? EnemyFission { get; private set; }
        public EnemyMorphSystem? EnemyMorph { get; private set; }
        public EnemyBurrowSystem? EnemyBurrow { get; private set; }
        public NecromancerSystem? Necromancer { get; private set; }
        public EnemyLifeLinkSystem? LifeLink { get; private set; }
        public HitShieldSystem? HitShield { get; private set; }
        public TowerSabotageSystem? TowerSabotage { get; private set; }
        public ManaBurnSystem? ManaBurn { get; private set; }
        public PhaseSystem? Phase { get; private set; }
        public FearSystem? Fear { get; private set; }
        public EnemyStrafeSystem? EnemyStrafe { get; private set; }
        public SuicideBombSystem? SuicideBomb { get; private set; }
        public ReflectTowerSystem? ReflectTower { get; private set; }
        public TowerStealthSystem? TowerStealth { get; private set; }
        public PathBlockSystem? PathBlock { get; private set; }

        // ── Desperation / Last Stand ──
        public DesperationSystem? Desperation { get; private set; }

        // ── Shop Reroll (BuildPhase offer pool refresh) ──
        public ShopRerollSystem? ShopReroll { get; private set; }

        // ── Environment ──
        public TerrainSystem? Terrain { get; private set; }
        public PathfindingSystem? Pathfinding { get; private set; }
        public DeployableTrapSystem? DeployableTrap { get; private set; }
        public PathModifierSystem? PathModifier { get; private set; }
        public PullSystem? Pull { get; private set; }
        public WeatherSystem? Weather { get; private set; }
        public DayNightSystem? DayNight { get; private set; }
        public WaveMutatorSystem? WaveMutator { get; private set; }
        public RandomEventSystem? RandomEvent { get; private set; }
        public TelegraphSystem? Telegraph { get; private set; }
        public AdaptiveDifficultySystem? AdaptiveDifficulty { get; private set; }
        public CorpseEffectSystem? CorpseEffect { get; private set; }
        public HealingZoneSystem? HealingZone { get; private set; }
        public ZoneControlSystem? ZoneControl { get; private set; }

        // ── Objective / Branch / Resources ──
        public ObjectiveSystem? Objective { get; private set; }
        public WaveBranchSystem? WaveBranch { get; private set; }
        public ResourceNodeSystem? ResourceNode { get; private set; }
        public WavePreviewSystem? WavePreview { get; private set; }
        // Round 110 Direction 10 — DoomClock countdown + final-score helper.
        public DoomClockSystem? DoomClock { get; private set; }

        // Round 196 Direction 3 — Soul Harvest (kill → soul currency; soul-cost skills).
        //   Per-kill harvesting wires via store.OnEnemyKilled; per-frame regen tick
        //   lives in PostDeathGroup (so it shares the same cadence as Combo / DoomClock).
        //   Public API: TrySpendSouls / AddSouls / SetSoulCap / SetSoulRegen / GetSoulCount
        //   — invoked from skill cast paths and quest/level-up reward paths.
        public SoulHarvestSystem? SoulHarvest { get; private set; }

        // ── Replay / Recording ──
        public ReplaySystem? Replay { get; private set; }

        // ── Hot Zone / Terrain Bonus ──
        public HotZoneSystem? HotZone { get; private set; }
        // Round 200 / Direction 2 — Elemental Terrain Zone (Frozen Lake / Burning Ground /
        // Toxic Swamp / Holy Sanctum). Player-spawned per-element ground effects with stacks,
        // DoT, and slow. Distinct from HotZone (placement bonus) and HazardZone (single-effect DoT).
        public TerrainZoneSystem? TerrainZone { get; private set; }

        // ── Frost Zone (Round 82 Direction 1) ── tower-positioned AoE slow
        public FrostZoneSystem? FrostZone { get; private set; }

        // ── Wander Roam (Round 84 Direction 6) ── off-path enemy movement
        public WanderRoamSystem? WanderRoam { get; private set; }

        // ── Magnetize zones (displacement fields, no damage) ──
        public MagnetizeSystem? Magnetize { get; private set; }
        public SapperSystem? Sapper { get; private set; }

        // ── Wisp aura pets (passive support pets: heal/slow/curse) ──
        public WispSystem? Wisp { get; private set; }

        // ── Boss Path Trail AoE (Round 124 Direction 1) — boss leaves damaging trail
        public BossTrailAoeSystem? BossTrailAoe { get; private set; }
        // ── Tech & Misc ──
        public TechTreeSystem? TechTree { get; private set; }
        public PickupSystem? Pickup { get; private set; }
        // Round 130 — Inventory / Item system (per-player slot-based consumables).
        public InventorySystem? Inventory { get; private set; }
        public AscensionSystem? Ascension { get; private set; }
        public SaveSystem? Save { get; private set; }

        // ── EventBus ──
        public EventBus? EventBus { get; private set; }

        // 系统注册边界分隔线。
        //  Creation — one system per block, in dependency order
        // 系统注册阶段分隔线。

        internal void PrepareInstallation(ComponentStore store, GameConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            store.UseComputedAttributes = true;
            var combo = config.Combo ?? new ComboConfig();
            if (combo.TriggerThreshold < 1)
                throw new Core.GAS.CatalogValidationException("Combo.triggerThreshold must be positive");
            if (combo.ComboDamageBonusPerKill < 0f || combo.ComboMaxMultiplier < 1f)
                throw new Core.GAS.CatalogValidationException("Combo damage bonus/max multiplier is invalid");
            config.CompiledCatalog = Core.GAS.CatalogCompiler.CompileRuntimeExtensions(config.CompiledCatalog,
                new Core.GAS.RuntimeCatalogSpec(combo.ComboDamageBonusPerKill, combo.ComboMaxMultiplier, combo.TriggerThreshold));
            _runtimeComboEffect = config.CompiledCatalog.Effects[config.CompiledCatalog.Effects.Count - 1];
            _runtimeTriggers.Clear();
            _runtimeTriggers.Add(config.CompiledCatalog.Triggers[config.CompiledCatalog.Triggers.Count - 1]);
        }

        public void CreateAll(ComponentStore store, GameConfig config, IRenderer logger, int playerId,
            StateMachine stateMachine, IBattleEventBus? battleEventBus = null)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (stateMachine == null) throw new ArgumentNullException(nameof(stateMachine));
            var plan = SystemRegistrationGraphValidator.GetStableOrder(SystemRegistrationManifest.Entries);
            RequireInstallationState(InstallationState.New, nameof(CreateAll));
            _installationState = InstallationState.Creating;
            LastRegistrationFailureId = null;
            LastRegistrationFailureStage = null;
            try
            {
                try
                {
                    PrepareInstallation(store, config);
                }
                catch
                {
                    LastRegistrationFailureId = "bootstrap";
                    LastRegistrationFailureStage = RegistrationStage.Construction;
                    throw;
                }
                foreach (var entry in plan)
                    if (!entry.IsDisabled)
                    {
                        try
                        {
                            entry.Factory!(this, store, config, logger, playerId, stateMachine, battleEventBus);
                        }
                        catch
                        {
                            LastRegistrationFailureId = entry.Id;
                            LastRegistrationFailureStage = RegistrationStage.Construction;
                            throw;
                        }
                    }
                _installationState = InstallationState.Created;
            }
            catch
            {
                _installationState = InstallationState.Failed;
                throw;
            }
        }

        // 系统接线阶段分隔线。

        public void WireDependencies(ComponentStore store, int playerId)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            var plan = SystemRegistrationGraphValidator.GetStableOrder(
                SystemRegistrationManifest.Entries);
            RequireInstallationState(InstallationState.Created, nameof(WireDependencies));
            _installationState = InstallationState.Wiring;
            LastRegistrationFailureId = null;
            LastRegistrationFailureStage = null;
            try
            {
                foreach (var entry in plan)
                    if (!entry.IsDisabled)
                    {
                        try
                        {
                            entry.Wire!(this, store, playerId);
                        }
                        catch
                        {
                            LastRegistrationFailureId = entry.Id;
                            LastRegistrationFailureStage = RegistrationStage.Wiring;
                            throw;
                        }
                    }
                _installationState = InstallationState.Wired;
            }
            catch
            {
                _installationState = InstallationState.Failed;
                throw;
            }
        }

        private int _thornsAuraPlayerId = 0;

        // 帧组分配阶段分隔线。
        //  Assign to FrameScheduler groups — one block per group
        // ═══════════════════════════════════════════════════════════════════

        internal void FinalizeBindings(FrameScheduler scheduler, bool seal)
        {
            scheduler.Build.ClearBindings();
            scheduler.Build.Register("build.gold.update", () => scheduler.Build.Gold, (st, dt) => Gold!.Update());
            scheduler.Build.Register("build.upgrade.update", () => scheduler.Build.Upgrade, (st, dt) => Upgrade!.Update());
            scheduler.Build.Register("build.skill.update", () => scheduler.Build.Skill, (st, dt) => Skill!.Update(dt, allowCombat: false));
            scheduler.Build.Register("build.auto-skill.update", () => scheduler.Build.AutoSkill, (st, dt) => AutoSkill!.Update(allowCombat: false));
            scheduler.Build.Register("build.interest.update", () => scheduler.Build.Interest, (st, dt) => Interest!.Update());
            scheduler.Build.Register("build.mana.update", () => scheduler.Build.Mana, (st, dt) => Mana!.Update(dt, isBuildPhase: true));
            scheduler.Build.Register("build.mana-shield.update", () => scheduler.Build.ManaShield, (st, dt) => ManaShield!.Update(dt));
            scheduler.Build.Register("build.pre-fight-buff.update", () => scheduler.Build.PreFightBuff, (st, dt) => PreFightBuff!.Update(dt));
            scheduler.Build.Register("build.resource-node.update", () => scheduler.Build.ResourceNode, (st, dt) => ResourceNode!.Update(dt, GameState.BuildPhase));
            scheduler.Build.Register("build.objective.update", () => scheduler.Build.Objective, (st, dt) => Objective!.Update(dt, GameState.BuildPhase));
            scheduler.Build.Register("build.global-skill.update", () => scheduler.Build.GlobalSkill, (st, dt) => GlobalSkill!.Update(dt, isBuildPhase: true));
            scheduler.Build.Register("build.desperation.update", () => scheduler.Build.Desperation, (st, dt) => Desperation!.Update());
            scheduler.Build.Register("build.shop-reroll.update", () => scheduler.Build.ShopReroll, (st, dt) => ShopReroll!.Update());
            scheduler.Build.Register("build.skill.reject-pending", () => scheduler.Build.Skill, (st, dt) => Skill!.RejectPendingSkillDamage());
            scheduler.RegisterBuildFrameBinding(FrameBindingFacts.Get("build.gold.update"));
            if (FrameBindingFacts.TryGet("build.tower-income.update", out var towerIncomeFact))
                scheduler.RegisterBuildFrameBinding(towerIncomeFact);
            scheduler.RegisterBuildFrameBinding(FrameBindingFacts.Get("build.upgrade.update"));
            scheduler.RegisterBuildFrameBinding(FrameBindingFacts.Get("build.skill.update"));
            scheduler.RegisterBuildFrameBinding(FrameBindingFacts.Get("build.auto-skill.update"));
            scheduler.RegisterBuildFrameBinding(FrameBindingFacts.Get("build.tower-relocate.update"));
            scheduler.RegisterBuildFrameBinding(FrameBindingFacts.Get("build.interest.update"));
            scheduler.RegisterBuildFrameBinding(FrameBindingFacts.Get("build.mana.update"));
            scheduler.RegisterBuildFrameBinding(FrameBindingFacts.Get("build.mana-shield.update"));
            scheduler.RegisterBuildFrameBinding(FrameBindingFacts.Get("build.pre-fight-buff.update"));
            scheduler.RegisterBuildFrameBinding(FrameBindingFacts.Get("build.resource-node.update"));
            scheduler.RegisterBuildFrameBinding(FrameBindingFacts.Get("build.objective.update"));
            scheduler.RegisterBuildFrameBinding(FrameBindingFacts.Get("build.global-skill.update"));
            scheduler.RegisterBuildFrameBinding(FrameBindingFacts.Get("build.desperation.update"));
            scheduler.RegisterBuildFrameBinding(FrameBindingFacts.Get("build.shop-reroll.update"));
            scheduler.RegisterBuildFrameBinding(FrameBindingFacts.Get("build.skill.reject-pending"));
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
            scheduler.ConfigureGraphComposition(FrameGraphCompositionKind.ProductionRegistry);
            if (seal) scheduler.SealGraphComposition();
        }

        public void AssignToGroups(FrameScheduler scheduler)
        {
            AssignToGroupsCore(scheduler, true, nameof(AssignToGroups));
        }

        internal void AssignToGroupsForValidation(FrameScheduler scheduler)
        {
            AssignToGroupsCore(scheduler, false, nameof(AssignToGroupsForValidation));
        }

        private void AssignToGroupsCore(FrameScheduler scheduler, bool seal, string operation)
        {
            if (scheduler == null) throw new ArgumentNullException(nameof(scheduler));
            var plan = SystemRegistrationGraphValidator.GetStableOrder(SystemRegistrationManifest.Entries);
            RequireInstallationState(InstallationState.Wired, operation);
            if (scheduler.IsCompositionSealed)
                throw new InvalidOperationException(operation + " cannot mutate a sealed scheduler composition.");
            _installationState = InstallationState.Binding;
            LastRegistrationFailureId = null;
            LastRegistrationFailureStage = null;
            try
            {
                foreach (var entry in plan)
                    if (!entry.IsDisabled)
                    {
                        try
                        {
                            entry.Bind!(this, scheduler);
                            scheduler.CompleteRegistrationBinding(entry);
                        }
                        catch
                        {
                            LastRegistrationFailureId = entry.Id;
                            LastRegistrationFailureStage = RegistrationStage.Binding;
                            throw;
                        }
                    }
                try
                {
                    FinalizeBindings(scheduler, seal);
                }
                catch
                {
                    LastRegistrationFailureId = "graph.seal";
                    LastRegistrationFailureStage = RegistrationStage.Binding;
                    throw;
                }
                _installationState = InstallationState.Bound;
            }
            catch
            {
                _installationState = InstallationState.Failed;
                throw;
            }
        }

        private void RequireInstallationState(InstallationState expected, string operation)
        {
            if (_installationState != expected)
                throw new InvalidOperationException(operation + " requires installation state " + expected +
                    ", but registry is " + _installationState + ".");
        }

    }
}
