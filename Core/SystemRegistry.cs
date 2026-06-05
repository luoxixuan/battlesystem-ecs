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
        public ProjectileSystem? Projectile { get; private set; }
        public ChronoTowerSystem? ChronoTower { get; private set; }
        // Round 106 Direction 2 — Mine / Trap tower (proximity-triggered AoE)
        public MineSystem? Mine { get; private set; }

        // ── Player ──
        public PlayerTowerAttackSystem? PlayerTowerAttack { get; private set; }
        public HeroSystem? Hero { get; private set; }

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

        // ── Tech & Misc ──
        public TechTreeSystem? TechTree { get; private set; }
        public PickupSystem? Pickup { get; private set; }
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

            // ── Tower core systems ──
            TowerPlacement = new TowerPlacementSystem(store, logger, config);
            TowerAttack = new TowerAttackSystem(store, logger, TechTree);
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
            // ── Taunt tower (force-enemy-target-this-tower aura) ──
            Taunt = new TauntSystem(store);

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

            // ── Misc ──
            Pickup = new PickupSystem(store, config, logger);
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

            // ── PlayerTowerAttack wiring ──
            PlayerTowerAttack?.SetLifeLinkSystem(LifeLink);
            PlayerTowerAttack?.SetHitShieldSystem(HitShield);

            // ── Skill wiring ──
            Skill?.InjectDotSystem(Buff);
            Skill?.InjectHealingZoneSystem(HealingZone);
            Skill?.InjectTimeRewindSystem(TimeRewind);

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
        }

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

            // ── Skill / Buff / Bleed ──
            scheduler.SkillBuff.Buff = Buff;
            scheduler.SkillBuff.Skill = Skill;
            scheduler.SkillBuff.Bleed = Bleed;
            scheduler.SkillBuff.HealingZone = HealingZone;
            scheduler.SkillBuff.Wisp = Wisp;
            // Round 107 Direction 6 — Target Mark decay tick (between HealingZone and Skill cd)
            scheduler.SkillBuff.Mark = Mark;

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
