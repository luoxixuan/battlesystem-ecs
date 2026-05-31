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
        public BuffSystem? Buff { get; private set; }
        public ComboSystem? Combo { get; private set; }
        public AutoSkillSystem? AutoSkill { get; private set; }
        public ManaSystem? Mana { get; private set; }
        public GlobalSkillSystem? GlobalSkill { get; private set; }

        // ── Towers ──
        public TowerPlacementSystem? TowerPlacement { get; private set; }
        public TowerAttackSystem? TowerAttack { get; private set; }
        public TowerUpgradeSystem? TowerUpgrade { get; private set; }
        public TowerExperienceSystem? TowerExperience { get; private set; }
        public TowerSynergySystem? TowerSynergy { get; private set; }
        public AuraTowerSystem? AuraTower { get; private set; }
        public CurseAuraSystem? Curse { get; private set; }
        public PullTowerSystem? PullTower { get; private set; }
        public BleedSystem? Bleed { get; private set; }
        public ProjectileSystem? Projectile { get; private set; }
        public ChronoTowerSystem? ChronoTower { get; private set; }

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
        public SuicideBombSystem? SuicideBomb { get; private set; }

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

        // ── Hot Zone / Terrain Bonus ──
        public HotZoneSystem? HotZone { get; private set; }

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
            EnemyMovement = new EnemyMovementSystem(store, playerId, config.MapWidth);
            EnemyMovement.SetPathfindingSystem(Pathfinding);

            // ── Tower core systems ──
            TowerPlacement = new TowerPlacementSystem(store, logger, config);
            TowerAttack = new TowerAttackSystem(store, logger, TechTree);
            TowerUpgrade = new TowerUpgradeSystem(store, logger, config);
            TowerExperience = new TowerExperienceSystem(store, config);
            TowerSynergy = new TowerSynergySystem(store, logger);
            TowerSynergy.LoadSynergyConfig();

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
            EnemyAI = new EnemyAISystem(store, logger, playerId, config, EnemyAbility, TechTree, eventBus);

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

            // ── Suicide Bomb ──
            SuicideBomb = new SuicideBombSystem(store, playerId);

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

            // ── Telegraph ──
            Telegraph = new TelegraphSystem(store, logger, config, eventBus);
            EnemyAbility.SetTelegraphSystem(Telegraph);

            // ── Aura / Curse / Pull / Bleed ──
            AuraTower = new AuraTowerSystem(store);
            Curse = new CurseAuraSystem(store);
            PullTower = new PullTowerSystem(store);
            Bleed = new BleedSystem(store, playerId);

            // ── Projectile ──
            Projectile = new ProjectileSystem(store, logger);

            // ── Objective / Branch / Resource ──
            Objective = new ObjectiveSystem(store, playerId);
            WaveBranch = new WaveBranchSystem(store, logger, config, stateMachine);
            ResourceNode = new ResourceNodeSystem(store, logger, playerId);

            // ── Hot Zone ──
            HotZone = new HotZoneSystem(store, config, playerId);

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

            // ── Random events ──
            RandomEvent = new RandomEventSystem(store, config);

            // ── Chrono tower ──
            ChronoTower = new ChronoTowerSystem(store);

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

            // ── PlayerTowerAttack wiring ──
            PlayerTowerAttack?.SetLifeLinkSystem(LifeLink);
            PlayerTowerAttack?.SetHitShieldSystem(HitShield);

            // ── Skill wiring ──
            Skill?.InjectDotSystem(Buff);

            // ── OnEnemyKilled → Combo + Necromancer ──
            store.OnEnemyKilled += (enemyId, pid) => Combo?.HandleComboIncrement(pid);
            store.OnEnemyKilled += (enemyId, pid) => Necromancer?.OnEnemyKilled(enemyId, pid);

            // ── OnTowerKill → TowerExperience ──
            store.OnTowerKill += (enemyId, pid, towerId) => TowerExperience?.HandleEnemyKilled(enemyId, pid, towerId);

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

            // ── PreGame ──
            scheduler.PreGame.WaveSpawning = WaveSpawning;
            scheduler.PreGame.Weather = Weather;
            scheduler.PreGame.DayNight = DayNight;
            scheduler.PreGame.AdaptiveDifficulty = AdaptiveDifficulty;
            scheduler.PreGame.Construction = null;
            scheduler.PreGame.RandomEvent = RandomEvent;

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

            // ── Movement ──
            scheduler.Movement.Wound = null;
            scheduler.Movement.Pathfinding = Pathfinding;
            scheduler.Movement.EnemyMovement = EnemyMovement;
            scheduler.Movement.PathModifier = PathModifier;
            scheduler.Movement.Pull = Pull;
            scheduler.Movement.EnemyHealer = null;
            scheduler.Movement.StealGold = null;
            scheduler.Movement.Summon = null;

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

            // ── Spatial ──
            scheduler.Spatial.PatrolTower = null;
            scheduler.Spatial.ChronoTower = ChronoTower;
            scheduler.Spatial.Fog = null;
            scheduler.Spatial.PointDefense = null;
            scheduler.Spatial.Telegraph = Telegraph;

            // ── Combat ──
            scheduler.Combat.PlayerTowerAttack = PlayerTowerAttack;
            scheduler.Combat.TowerOvercharge = null;
            scheduler.Combat.Heat = null; // HeatSystem — heat accumulation + overheat state
            scheduler.Combat.Demolish = null;
            scheduler.Combat.HitShield = HitShield;
            scheduler.Combat.TowerSabotage = TowerSabotage;
            scheduler.Combat.Hero = Hero;
            scheduler.Combat.SuicideBomb = SuicideBomb;
            scheduler.Combat.TowerAttack = TowerAttack;
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

            // ── Skill / Buff / Bleed ──
            scheduler.SkillBuff.Buff = Buff;
            scheduler.SkillBuff.Skill = Skill;
            scheduler.SkillBuff.Bleed = Bleed;
            scheduler.SkillBuff.HealingZone = HealingZone;

            // ── Post-death ──
            scheduler.PostDeath.EnemyFission = EnemyFission;
            scheduler.PostDeath.LifeLink = LifeLink;
            scheduler.PostDeath.Objective = Objective;
            scheduler.PostDeath.ResourceNode = ResourceNode;
            scheduler.PostDeath.TowerIncome = null;
            scheduler.PostDeath.CorpseEffect = CorpseEffect;
            scheduler.PostDeath.WaveBranch = WaveBranch;
            scheduler.PostDeath.Combo = Combo;
        }
    }
}
