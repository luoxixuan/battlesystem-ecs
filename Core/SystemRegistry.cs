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
    /// Adding a new system:
    ///   1. Add a public property below
    ///   2. Create it in CreateAll()
    ///   3. Wire its SetXxx() dependencies in WireDependencies()
    ///   4. Assign it to the correct scheduler group in AssignToGroups()
    /// </summary>
    public class SystemRegistry
    {
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
        public ComboSystem? Combo { get; private set; }
        public AutoSkillSystem? AutoSkill { get; private set; }
        public ManaSystem? Mana { get; private set; }
        public GlobalSkillSystem? GlobalSkill { get; private set; }
        // Round 107 Direction 6 — Target Mark subsystem (stack-based debuff counter)
        public MarkSystem? Mark { get; private set; }
        // Round 109 Direction 5 — Time Rewind snapshot ring (HP / Mana / Shield restore)
        public TimeRewindSnapshotSystem? TimeRewind { get; private set; }

        // ── Towers ──
        public TowerPlacementSystem? TowerPlacement { get; private set; }
        public TowerAttackSystem? TowerAttack { get; private set; }
        public TowerUpgradeSystem? TowerUpgrade { get; private set; }
        public TowerExperienceSystem? TowerExperience { get; private set; }
        public TowerSynergySystem? TowerSynergy { get; private set; }
        public KillCooldownResetSystem? KillCooldownReset { get; private set; }
        // ── Kill-Triggered Player Sustain (HealOnKill / ManaOnKill) ───────────
        public HealOnKillSystem? HealOnKill { get; private set; }
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

        // ── Replay / Recording ──
        public ReplaySystem? Replay { get; private set; }

        // ── Hot Zone / Terrain Bonus ──
        public HotZoneSystem? HotZone { get; private set; }

        // ── Frost Zone (Round 82 Direction 1) ── tower-positioned AoE slow
        public FrostZoneSystem? FrostZone { get; private set; }

        // ── Wander Roam (Round 84 Direction 6) ── off-path enemy movement
        public WanderRoamSystem? WanderRoam { get; private set; }

        // ── Magnetize zones (displacement fields, no damage) ──
        public MagnetizeSystem? Magnetize { get; private set; }

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
        public IEventBus? EventBus { get; private set; }

        // ═══════════════════════════════════════════════════════════════════
        //  Creation — one system per block, in dependency order
        // ═══════════════════════════════════════════════════════════════════

        public void CreateAll(ComponentStore store, GameConfig config, IRenderer logger, int playerId, StateMachine stateMachine)
        {
            // ── EventBus (needed early by several systems) ──
            var eventBus = new EventBus();

            // ── Map ──
            Map = new MapSystem(logger, store);

            // ── Tech Tree (needed by most systems) ──
            var techConfig = TechTreeSystem.LoadConfig(logger);
            TechTree = new TechTreeSystem(store, logger, playerId, techConfig, config);

            // ── Pathfinding & Movement ──
            Pathfinding = new PathfindingSystem(store);
            EnemyMovement = new EnemyMovementSystem(store, playerId, config.MapWidth, config);
            EnemyMovement.SetPathfindingSystem(Pathfinding);
            // Round 124 — Direction 1: Boss Path Trail AoE. Create the system and inject it
            // into EnemyMovementSystem so the parallel pass can queue trail events. The actual
            // drain runs in EnemyMovementSystem.Update()'s serial pass.
            BossTrailAoe = new BossTrailAoeSystem(store, playerId);
            EnemyMovement.SetBossTrailSystem(BossTrailAoe);

            // ── Tower core systems ──
            TowerPlacement = new TowerPlacementSystem(store, logger, config);
            TowerAttack = new TowerAttackSystem(store, logger, TechTree);
            // Round 143 Direction 1 — inject the effectiveness matrix for tower-vs-enemy damage
            TowerAttack.SetGameConfig(config);
            TowerUpgrade = new TowerUpgradeSystem(store, logger, config);
            TowerExperience = new TowerExperienceSystem(store, config);
            TowerSynergy = new TowerSynergySystem(store, logger);
            TowerSynergy.LoadSynergyConfig();
            // Kill-triggered cooldown reset (ARPG/Roguelike mechanic)
            KillCooldownReset = new KillCooldownResetSystem(store, config, playerId);
            // Kill-triggered player sustain (heal / mana on tower kill)
            HealOnKill = new HealOnKillSystem(store);
            TowerMorph = new TowerMorphSystem(store);

            // ── Player attack ──
            PlayerTowerAttack = new PlayerTowerAttackSystem(store, logger, playerId, config, TechTree);
            Hero = new HeroSystem(store, playerId);
            // Round 144 方向4 — Hero Active Skill Set. Constructed alongside Hero
            //   and Skill (it needs config to resolve SkillName → SkillDef id).
            //   Initialize() loads slot bindings from Data/Configs/hero_skills.json
            //   and is idempotent (safe to call again on hot-reload).
            HeroSkill = new HeroSkillSystem(store, playerId, config: config);
            HeroSkill.SetConfig(config);
            HeroSkill.Initialize();

            // ── Spawning ──
            WaveSpawning = new WaveSpawningSystem(store, logger, config);
            Nest = new NestSystem(store, config, logger, playerId);
            Nest.Initialize();

            // ── Economy ──
            Gold = new GoldSystem(store, logger, TechTree);
            Upgrade = new UpgradeSystem(store, logger, playerId, config);
            Interest = new InterestSystem(store, logger, config, playerId);

            // ── Skills & Buffs & Mana ──
            Skill = new SkillSystem(store, logger, playerId, config, TechTree);
            Skill.InitializePlayerSkills();
            Buff = new BuffSystem(store, playerId);
            Combo = new ComboSystem(store, config.Combo);
            Mana = new ManaSystem(store, logger, config, playerId, TechTree);
            Mana.Initialize();
            AutoSkill = new AutoSkillSystem(store, logger, playerId, Skill, config.AutoSkill);
            GlobalSkill = new GlobalSkillSystem(store, config, logger, playerId, TechTree);

            // ── Mana ↔ Skill wiring ──
            Skill.InjectManaSystem(Mana);

            // ── Enemy AI & Abilities ──
            EnemyAbility = new EnemyAbilitySystem(store, logger, playerId, config, eventBus);
            EnemyAI = new EnemyAISystem(store, logger, playerId, config, EnemyAbility, TechTree, eventBus, ReflectTower);
            // Round 119 Dir 3 — wire WaveSpawningSystem into EnemyAISystem so phase-triggered
            // minion summons can be drained into SpawnMinionNearPosition() at end of Update.
            EnemyAI.SetWaveSpawningSystem(WaveSpawning);

            // ── Hit Shield ──
            var hitShield = new HitShieldSystem(store, logger);
            HitShield = hitShield;

            // ── Tower Sabotage ──
            TowerSabotage = new TowerSabotageSystem(store);

            // ── Mana Burn ──
            ManaBurn = new ManaBurnSystem(store, playerId);

            // ── Phase ──
            Phase = new PhaseSystem(store, playerId);

            // ── Fear ──
            Fear = new FearSystem(store, playerId);

            // ── Enemy Strafe/Dodge ──
            EnemyStrafe = new EnemyStrafeSystem(store, logger);

            // ── Suicide Bomb ──
            SuicideBomb = new SuicideBombSystem(store, playerId, ReflectTower, TowerStealth);

            // ── Reflect Tower ──
            ReflectTower = new ReflectTowerSystem(store, playerId);

            // ── Tower Stealth ──
            TowerStealth = new TowerStealthSystem(store, playerId);

            // ── Desperation / Last Stand ──
            Desperation = new DesperationSystem(store);

            // ── Shop Reroll (BuildPhase offer pool) ──
            ShopReroll = new ShopRerollSystem(store, logger, config, playerId);

            // ── Burrow, Necromancer, LifeLink ──
            EnemyBurrow = new EnemyBurrowSystem(store, playerId);
            Necromancer = new NecromancerSystem(store, config, logger);
            LifeLink = new EnemyLifeLinkSystem(store, config, logger);

            // ── Fission, Morph ──
            EnemyFission = new EnemyFissionSystem(store, config, logger);
            EnemyMorph = new EnemyMorphSystem(store, config, logger);

            // ── Environment ──
            Terrain = new TerrainSystem(store, playerId, config);
            Terrain.SetBuffSystem(Buff);
            WaveMutator = new WaveMutatorSystem(store, playerId, logger);
            WaveMutator.LoadMutators(config.WaveMutatorDefs);
            Weather = new WeatherSystem(store, config);
            DayNight = new DayNightSystem(store, config);
            DayNight.Initialize(playerId);
            EnemyMovement.SetWeatherSystem(Weather);
            TowerAttack.SetWeatherSystem(Weather);
            EnemyMovement.SetDayNightSystem(DayNight);
            TowerAttack.SetDayNightSystem(DayNight);

            // ── Enemy Strafe ──
            TowerAttack.SetEnemyStrafeSystem(EnemyStrafe);

            // ── Telegraph ──
            Telegraph = new TelegraphSystem(store, logger, config, eventBus);
            EnemyAbility.SetTelegraphSystem(Telegraph);

            // ── Aura / Curse / Pull / Bleed ──
            AuraTower = new AuraTowerSystem(store);
            Curse = new CurseAuraSystem(store);
            PullTower = new PullTowerSystem(store);
            Bleed = new BleedSystem(store, playerId);
            // Round 170 Direction 6 — Frostbite (non-stacking %-of-maxHP DoT)
            Frostbite = new FrostbiteSystem(store, playerId);
            // ── Taunt tower (force-enemy-target-this-tower aura) ──
            Taunt = new TauntSystem(store);

            // Round 122 Direction 2 — Heal Aura System. Created alongside the other
            // aura-flavor systems (Taunt, Curse, AuraTower) since it shares the same
            // tower-only effect semantics: opt-in via tower-config fields, zero-overhead
            // when no heal-aura tower is on the field (radius==0 fast path).
            HealAura = new HealAuraSystem(store);
            // Round 126 Direction 4 — Thorns Aura System. Mirrors the HealAura wiring:
            // opt-in via tower-config fields, zero-overhead when no thorns tower is on
            // the field (IsThornsTower==false fast path). Runs in SkillBuffGroup like
            // HealAura, but deals damage to enemies instead of healing friendly towers.
            ThornsAura = new ThornsAuraSystem(store);

            // Round 138 — Per-Tower Active Skill System. Pure state machine that ticks
            //   per-tower cooldowns and exposes TriggerTowerActive(towerId) for the
            //   player/HUD. No effect dispatch yet (SkillSystem refactor is a future
            //   round) — this round establishes the gate + cooldown contract.
            TowerActiveSkill = new TowerActiveSkillSystem(store, config);

            // Round 142 方向5 — Aggro / Focus Fire System. Player-driven mark-focus
            //   command. Constructed after the tower-side systems (no per-tower
            //   dependency; operates on enemy-side state) and before FireTrail
            //   (which is also a passive system). Wired into CombatGroup.Update()
            //   last in the combat phase, after TowerActiveSkill.
            Aggro = new AggroSystem(store);

            // Round 145 Direction 3 — Per-Tower Modifier Pool. Constructed early so
            //   TowerPlacementSystem can call RollAtPlacement() right after AddTower.
            //   BuildPhase-only system — no per-frame work; the modifier is rolled
            //   once and consumed lazily by combat systems.
            TowerModifier = new TowerModifierSystem(store, config);

            // Round 128 Direction 5 — Fire Trail System. Passive wrapper, no
            // dependencies on Buff/Skill systems. Constructed early so it can be
            // injected into TowerAttackSystem via WireDependencies below.
            FireTrail = new FireTrailSystem(store);

            // ── Projectile ──
            Projectile = new ProjectileSystem(store, logger);

            // ── Objective / Branch / Resource ──
            Objective = new ObjectiveSystem(store, playerId);
            WaveBranch = new WaveBranchSystem(store, logger, config, stateMachine);
            ResourceNode = new ResourceNodeSystem(store, logger, playerId);
            WavePreview = new WavePreviewSystem(store, config, playerId);
            // Round 110 Direction 10 — DoomClock countdown + final score helper.
            // Created here so it shares the same ComponentStore as the other
            // objective / post-death systems. The countdown itself is ticked
            // by PostDeathGroup.DoomClock.Update(...) each WavePhase frame.
            DoomClock = new DoomClockSystem(store, playerId);

            // ── Replay / Recording (per-frame telemetry, opt-in via GameConfig.Replay.Enabled) ──
            Replay = new ReplaySystem(store, config, playerId);

            // ── Hot Zone ──
            HotZone = new HotZoneSystem(store, config, playerId);

            // ── Frost Zone (Round 82 Direction 1) — instantiates per registry
            FrostZone = new FrostZoneSystem(store);

            // ── Wander Roam (Round 84 Direction 6) — instantiates per registry
            WanderRoam = new WanderRoamSystem(store);

            // ── Magnetize (displacement fields) ──
            Magnetize = new MagnetizeSystem(store, logger);

            // ── Wisp aura pets (Heal / Slow / Curse) ──
            Wisp = new WispSystem(store, logger);

            // ── Adaptive Difficulty ──
            AdaptiveDifficulty = new AdaptiveDifficultySystem(store, config);
            WaveSpawning.SetAdaptiveDifficulty(AdaptiveDifficulty);
            // Round 120 Dir 3 — wire WaveSpawning back to AdaptiveDifficulty so OnWaveComplete
            // can write the rubber-band spawn multiplier for the next wave.
            AdaptiveDifficulty.SetWaveSpawningSystem(WaveSpawning);

            // ── Misc ──
            Pickup = new PickupSystem(store, config, logger);
            Inventory = new InventorySystem(store, config, logger);
            Ascension = new AscensionSystem(store, logger, config);
            Ascension.SelectModifier("tough_enemies");
            WaveSpawning.SetAscensionSystem(Ascension);
            Save = new SaveSystem(store, playerId);

            // ── Corpse effects ──
            CorpseEffect = new CorpseEffectSystem(store, config, Buff, logger);
            CorpseEffect.LoadCorpseEffects();

            // ── Healing zones ──
            HealingZone = new HealingZoneSystem(store, logger);

            // ── Zone control (CC zones: Slow/Stun/Freeze/Root) ──
            ZoneControl = new ZoneControlSystem(store, logger);

            // ── Path modifier ──
            PathModifier = new PathModifierSystem(store);
            Pull = new PullSystem(store, playerId);

            // ── Path block system (dynamic path blocking) ──
            PathBlock = new PathBlockSystem(store);

            // ── Random events ──
            RandomEvent = new RandomEventSystem(store, config);

            // ── Chrono tower ──
            ChronoTower = new ChronoTowerSystem(store);

            // ── Round 106 Direction 2 — Mine / Trap tower ──
            Mine = new MineSystem(store, logger, config, playerId);

            // ── Round 173 Direction 1 — Shrine Tower (persistent pure-buff aura) ──
            // Created alongside Mine because both are "non-attack" tower-flavor systems
            // with their own TowerType enum value. Sentinel-gated fast path when no
            // shrine is on the field.
            TowerShrine = new TowerShrineSystem(store);

            // ── Round 177 Direction 2 — Beacon Tower (active command-post broadcast buff) ──
            //   Created alongside Shrine (both are non-attack "support" tower-flavor systems
            //   with their own TowerType enum value and per-frame additive cache arrays).
            //   Sentinel-gated fast path when no beacon is on the field. Always broadcasts
            //   BOTH damage and attack-speed bonuses together to every friendly tower in range.
            TowerBeacon = new TowerBeaconSystem(store);

            // ── Round 107 Direction 6 — Target Mark subsystem ──
            Mark = new MarkSystem(store, playerId);

            // ── Round 109 Direction 5 — Time Rewind snapshot ring ──
            TimeRewind = new TimeRewindSnapshotSystem(store);

            // ── Store EventBus ──
            EventBus = eventBus;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Dependency wiring — SetXxx() injections & event subscriptions
        // ═══════════════════════════════════════════════════════════════════

        public void WireDependencies(ComponentStore store, int playerId)
        {
            // ── TowerAttack dependency wiring ──
            TowerAttack?.SetBuffSystem(Buff);
            TowerAttack?.SetBleedSystem(Bleed);
            TowerAttack?.SetTowerExperienceSystem(TowerExperience);
            TowerAttack?.SetProjectileSystem(Projectile);
            TowerAttack?.SetLifeLinkSystem(LifeLink);
            TowerAttack?.SetHitShieldSystem(HitShield);
            TowerAttack?.SetTowerStealthSystem(TowerStealth);
            TowerAttack?.SetDesperationSystem(Desperation);
            // Round 128 Direction 5 — Fire Trail System. Inject after Desperation
            // (last-situation system) to keep the wiring list ordered by
            // injection-time-of-arrival. Optional dependency; null-safe at call
            // site.
            TowerAttack?.SetFireTrailSystem(FireTrail);

            // Round 145 Direction 3 — Per-Tower Modifier Pool: inject into TowerPlacementSystem
            //   so PlaceTower() can roll the modifier at placement time. Optional dependency;
            //   null-safe (the placement path branches on null before calling).
            TowerPlacement?.SetTowerModifierSystem(TowerModifier);

            // ── PlayerTowerAttack wiring ──
            PlayerTowerAttack?.SetLifeLinkSystem(LifeLink);
            PlayerTowerAttack?.SetHitShieldSystem(HitShield);

            // ── Skill wiring ──
            Skill?.InjectDotSystem(Buff);
            Skill?.InjectHealingZoneSystem(HealingZone);
            Skill?.InjectTimeRewindSystem(TimeRewind);
            // Round 133 Direction 5 — wire NecromancerSystem into SkillSystem so the
            // MassResurrect (AreaShapeType.MassResurrect = 18) ability can delegate to
            // NecromancerSystem.MassResurrect for the actual AOE corpse revive. Optional
            // dependency: null-safe at the call site (ExecuteAbility case 18 logs a
            // warning and returns 0 if not injected).
            Skill?.InjectNecromancerSystem(Necromancer);

            // ── Mark wiring: subscribe to OnEnemyKilled to free the per-entity
            //    threshold-fired latch on enemy destroy (avoids ID-reuse leakage). ──
            store.OnEnemyKilled += (enemyId, pid) => Mark?.OnEnemyDestroyed(enemyId);

            // ── OnEnemyKilled → Combo + Necromancer ──
            store.OnEnemyKilled += (enemyId, pid) => Combo?.HandleComboIncrement(pid);
            store.OnEnemyKilled += (enemyId, pid) => Necromancer?.OnEnemyKilled(enemyId, pid);

            // ── OnTowerKill → TowerExperience ──
            store.OnTowerKill += (enemyId, pid, towerId) => TowerExperience?.HandleEnemyKilled(enemyId, pid, towerId);

            // ── OnTowerKill + OnEnemyKilled → KillCooldownReset (cooldown reset on kill) ──
            KillCooldownReset?.SubscribeToEvents();

            // ── OnTowerKill → HealOnKill (player heal / mana restore on tower kill) ──
            HealOnKill?.SubscribeToEvents();

            // ── CorpseEffect subscribes to OnEnemyKilled ──
            CorpseEffect?.SubscribeToOnEnemyKilled();

            // ── OnWaveComplete hooks ──
            if (WaveSpawning != null)
            {
                WaveSpawning.OnWaveComplete += () => TechTree?.OnWaveComplete();
                WaveSpawning.OnWaveComplete += () => Interest?.OnWaveComplete();
                WaveSpawning.OnWaveComplete += () => Save?.SaveCheckpoint();
                WaveSpawning.OnWaveComplete += () =>
                {
                    if (WaveBranch != null && WaveSpawning != null)
                        WaveBranch.CheckAndActivateBranch(
                            WaveSpawning.GetCurrentWave() - 1,
                            WaveSpawning.GetCurrentLevel()
                        );
                };
                // Breather-wave reward hook: GoldSystem applies heal + CDR + gold x2 when a Breather wave ends.
                Gold?.SubscribeToBreatherWave(WaveSpawning);
                // Decaying-Wave-Bounty hook: GoldSystem resets PlayerWaveKillCount when each new wave starts.
                Gold?.SubscribeToWaveStart(WaveSpawning);
            }

            // ── OnWaveStart hooks ──
            if (WaveSpawning != null)
            {
                WaveSpawning.OnWaveStart += () =>
                {
                    int wave = WaveSpawning.GetCurrentWave();
                    PlayerTowerAttack?.SetWaveNumber(wave);
                    TowerAttack?.SetWaveNumber(wave);
                    Skill?.SetWaveNumber(wave);
                    Combo?.ResetCombo(playerId);
                    WaveMutator?.OnWaveStart(wave);
                };
                // WavePreview handles its own wave-start recompute inside HandleWaveStart
                // (queries GetCurrentLevel/GetCurrentWave directly from the spawner).
                WavePreview?.Subscribe(WaveSpawning);
            }
            // Round 126 Direction 4 — stashed so AssignToGroups can plumb playerId into
            // the SkillBuffGroup. ThornsAuraSystem.Update needs the killing-player id
            // to attribute QueueEnemyDeath calls (matches BleedSystem / TowerAttackSystem
            // behavior). SystemRegistry itself has no SkillBuff field — the property
            // lives on FrameScheduler.
            _thornsAuraPlayerId = playerId;
        }

        private int _thornsAuraPlayerId = 0;

        // ═══════════════════════════════════════════════════════════════════
        //  Assign to FrameScheduler groups — one block per group
        // ═══════════════════════════════════════════════════════════════════

        public void AssignToGroups(FrameScheduler scheduler)
        {
            // ── BuildPhase ──
            scheduler.Build.Gold = Gold;
            scheduler.Build.TowerIncome = null;
            scheduler.Build.Upgrade = Upgrade;
            scheduler.Build.Skill = Skill;
            scheduler.Build.AutoSkill = AutoSkill;
            scheduler.Build.TowerRelocate = null;
            scheduler.Build.Interest = Interest;
            scheduler.Build.Mana = Mana;
            scheduler.Build.Objective = Objective;
            scheduler.Build.ResourceNode = ResourceNode;
            scheduler.Build.GlobalSkill = GlobalSkill;
            scheduler.Build.Desperation = Desperation;
            scheduler.Build.ShopReroll = ShopReroll;

            // ── PreGame ──
            scheduler.PreGame.WaveSpawning = WaveSpawning;
            scheduler.PreGame.Weather = Weather;
            scheduler.PreGame.DayNight = DayNight;
            scheduler.PreGame.AdaptiveDifficulty = AdaptiveDifficulty;
            scheduler.PreGame.Construction = null;
            scheduler.PreGame.RandomEvent = RandomEvent;
            scheduler.PreGame.Desperation = Desperation;
            scheduler.PreGame.TimeRewind = TimeRewind;

            // ── Spawning ──
            scheduler.Spawning.WaveSpawning = WaveSpawning;
            scheduler.Spawning.Nest = Nest;

            // ── AI ──
            scheduler.AI.EnemyAI = EnemyAI;
            scheduler.AI.EnemyAbility = EnemyAbility;
            scheduler.AI.Burrow = EnemyBurrow;
            scheduler.AI.Necromancer = Necromancer;
            scheduler.AI.LifeLink = LifeLink;
            scheduler.AI.EnemyAffix = null;
            scheduler.AI.ManaBurn = ManaBurn;
            scheduler.AI.Phase = Phase;
            scheduler.AI.Fear = Fear;
            scheduler.AI.ZoneControl = ZoneControl;
            scheduler.AI.EnemyStrafe = EnemyStrafe;
            scheduler.AI.ReflectTower = ReflectTower;
            scheduler.AI.Magnetize = Magnetize;

            // ── Movement ──
            scheduler.Movement.Wound = null;
            scheduler.Movement.Pathfinding = Pathfinding;
            scheduler.Movement.EnemyMovement = EnemyMovement;
            scheduler.Movement.PathModifier = PathModifier;
            scheduler.Movement.Pull = Pull;
            scheduler.Movement.EnemyHealer = null;
            scheduler.Movement.StealGold = null;
            scheduler.Movement.Summon = null;
            scheduler.Movement.PathBlock = PathBlock;

            // ── Terrain + Mutators + Morph ──
            scheduler.Terrain.Terrain = Terrain;
            scheduler.Terrain.WaveMutator = WaveMutator;
            scheduler.Terrain.EnemyMorph = EnemyMorph;

            // ── Combat Setup ──
            scheduler.CombatSetup.PlayerTowerAttack = PlayerTowerAttack;
            scheduler.CombatSetup.Hero = Hero;
            scheduler.CombatSetup.TowerAttack = TowerAttack;
            scheduler.CombatSetup.TowerOvercharge = null;
            scheduler.CombatSetup.TowerSynergy = TowerSynergy;
            scheduler.CombatSetup.TowerLink = null;
            scheduler.CombatSetup.Skill = Skill;
            scheduler.CombatSetup.AuraTower = AuraTower;
            scheduler.CombatSetup.Curse = Curse;
            scheduler.CombatSetup.PullTower = PullTower;
            scheduler.CombatSetup.Mana = Mana;
            scheduler.CombatSetup.GlobalSkill = GlobalSkill;
            scheduler.CombatSetup.HitShield = HitShield;
            scheduler.CombatSetup.HotZone = HotZone;
            scheduler.CombatSetup.FrostZone = FrostZone;
            scheduler.CombatSetup.WanderRoam = WanderRoam;
            scheduler.CombatSetup.Taunt = Taunt;

            // ── Spatial ──
            scheduler.Spatial.PatrolTower = null;
            scheduler.Spatial.ChronoTower = ChronoTower;
            scheduler.Spatial.Fog = null;
            scheduler.Spatial.PointDefense = null;
            scheduler.Spatial.Telegraph = Telegraph;
            scheduler.Spatial.Mine = Mine;

            // ── Combat ──
            scheduler.Combat.PlayerTowerAttack = PlayerTowerAttack;
            scheduler.Combat.TowerOvercharge = null;
            scheduler.Combat.Heat = null; // HeatSystem — heat accumulation + overheat state
            scheduler.Combat.Demolish = null;
            scheduler.Combat.HitShield = HitShield;
            scheduler.Combat.TowerSabotage = TowerSabotage;
            scheduler.Combat.Hero = Hero;
            scheduler.Combat.SuicideBomb = SuicideBomb;
            scheduler.Combat.ReflectTower = ReflectTower;
            scheduler.Combat.TowerAttack = TowerAttack;
            scheduler.Combat.TowerMorph = TowerMorph;
            scheduler.Combat.TowerStealth = TowerStealth;
            scheduler.Combat.TowerSynergy = TowerSynergy;
            scheduler.Combat.TowerLink = null;
            scheduler.Combat.AuraTower = AuraTower;
            scheduler.Combat.Curse = Curse;
            scheduler.Combat.PullTower = PullTower;
            scheduler.Combat.TowerSilence = null;
            scheduler.Combat.Dispel = null;
            scheduler.Combat.Projectile = Projectile;
            scheduler.Combat.EnemyProjectile = null;
            scheduler.Combat.Pickup = Pickup;
            scheduler.Combat.Mana = Mana;
            scheduler.Combat.GlobalSkill = GlobalSkill;
            scheduler.Combat.Taunt = Taunt;
            // Round 138 — Per-tower active skill (cooldown tick).
            scheduler.Combat.TowerActiveSkill = TowerActiveSkill;
            // Round 142 方向5 — Aggro / Focus Fire (per-frame duration tick). Wired
            //   last in the combat phase, after TowerActiveSkill. O(1) when no
            //   focus is active; O(n_enemies) when at least one enemy has an
            //   active focus assignment.
            scheduler.Combat.Aggro = Aggro;
            // Round 144 方向4 — Hero Active Skill Set per-frame cooldown tick. Wired
            //   last in the combat phase, after Aggro. O(1) when no skill is
            //   configured (sentinel _anySkillConfigured in the system).
            scheduler.Combat.HeroSkill = HeroSkill;
            // Round 173 Direction 1 — Shrine Tower: resolve persistent aura buffs.
            //   Runs after AuraTower.ResolveAuraBuffs (both are serial aura-phase
            //   passes) and before the projectile/buff downstream consumers, so
            //   any v2 wiring that consumes the cached bonuses sees fresh values.
            scheduler.Combat.TowerShrine = TowerShrine;
            // Round 177 Direction 2 — Beacon Tower: resolve active broadcast buffs.
            //   Runs immediately after Shrine.ResolveShrineBuffs (both are serial
            //   aura-phase passes) and before the projectile/buff downstream consumers.
            //   Both beacon damage and atk-spd cache arrays are written to per-tower
            //   additive slots and consumed by TowerAttackSystem in v2.
            scheduler.Combat.TowerBeacon = TowerBeacon;

            // ── Skill / Buff / Bleed ──
            scheduler.SkillBuff.Buff = Buff;
            scheduler.SkillBuff.Skill = Skill;
            scheduler.SkillBuff.Bleed = Bleed;
            scheduler.SkillBuff.Frostbite = Frostbite;
            scheduler.SkillBuff.HealingZone = HealingZone;
            scheduler.SkillBuff.Wisp = Wisp;
            // Round 107 Direction 6 — Target Mark decay tick (between HealingZone and Skill cd)
            scheduler.SkillBuff.Mark = Mark;
            // Round 122 Direction 2 — Heal Aura System wiring (passive tower-to-tower healing)
            scheduler.SkillBuff.HealAura = HealAura;
            // Round 126 Direction 4 — Thorns Aura System wiring (passive tower-centered damage on enemies).
            //   The system reference is wired in AssignToGroups; the playerId is set below in
            //   WireDependencies where the parameter is in scope. (Moved from AssignToGroups
            //   because that method does not take a playerId parameter.)
            scheduler.SkillBuff.ThornsAura = ThornsAura;
            // Round 126 Direction 4 — playerId stashed in WireDependencies (which is
            // the only place the id is in scope) is now propagated to the SkillBuffGroup
            // so ThornsAuraSystem.Update can attribute QueueEnemyDeath to the killing player.
            scheduler.SkillBuff.ThornsAuraPlayerId = _thornsAuraPlayerId;

            // ── Post-death ──
            scheduler.PostDeath.EnemyFission = EnemyFission;
            scheduler.PostDeath.LifeLink = LifeLink;
            scheduler.PostDeath.Objective = Objective;
            scheduler.PostDeath.ResourceNode = ResourceNode;
            scheduler.PostDeath.TowerIncome = null;
            scheduler.PostDeath.CorpseEffect = CorpseEffect;
            scheduler.PostDeath.WaveBranch = WaveBranch;
            scheduler.PostDeath.Combo = Combo;
            // Round 110 Direction 10 — wire DoomClock into PostDeath so the
            // countdown ticks alongside objective bookkeeping each WavePhase.
            // Zero overhead when not active (Update() short-circuits on
            // DoomClockActive[playerId] == false).
            scheduler.PostDeath.DoomClock = DoomClock;
        }
    }
}
