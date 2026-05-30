#nullable enable
using System;
using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Systems;
using BattleSystemECS.Config;

namespace BattleSystemECS.Core
{
    /// <summary>
    /// Unified frame scheduler — all frame paths (GameManager / Benchmark / Tests) go through here.
    /// 
    /// Frame order (two-phase pattern):
    ///   Phase 1 (parallel-safe):     AI, Abilities, Movement
    ///   Phase 2 (serial settlement): RebuildSpatialGrid, Attack, SkillDamage, DOT, Death Resolve
    /// </summary>
    public class FrameScheduler
    {
        private readonly ComponentStore store;
        private readonly GameConfig gameConfig;

        public GameState Phase { get; set; } = GameState.WavePhase;

        // Systems — nullable, injected by caller
        public WaveSpawningSystem? WaveSpawning { get; set; }
        public EnemyAISystem? EnemyAI { get; set; }
        public EnemyAbilitySystem? EnemyAbility { get; set; }
        public EnemyMovementSystem? EnemyMovement { get; set; }
        public PlayerTowerAttackSystem? PlayerTowerAttack { get; set; }
        public TowerAttackSystem? TowerAttack { get; set; }
        public TowerSynergySystem? TowerSynergy { get; set; }
        public SkillSystem? Skill { get; set; }
        public BuffSystem? Buff { get; set; }
        public TechTreeSystem? TechTree { get; set; }
        public GoldSystem? Gold { get; set; }
        public UpgradeSystem? Upgrade { get; set; }
        public ComboSystem? Combo { get; set; }
        public AutoSkillSystem? AutoSkill { get; set; }
        public WeatherSystem? Weather { get; set; }
        public DayNightSystem? DayNight { get; set; }
        public AuraTowerSystem? AuraTower { get; set; }
        public TowerIncomeSystem? TowerIncome { get; set; }
        public PathfindingSystem? Pathfinding { get; set; }
        public ProjectileSystem? Projectile { get; set; }
        public TerrainSystem? Terrain { get; set; }
        public WaveMutatorSystem? WaveMutator { get; set; }
        public InterestSystem? Interest { get; set; }
        public EnemyAffixSystem? EnemyAffix { get; set; }
        public ManaSystem? Mana { get; set; }
        public PickupSystem? Pickup { get; set; }
        public EnemyProjectileSystem? EnemyProjectile { get; set; }
        public PointDefenseSystem? PointDefense { get; set; }
        public TowerOverchargeSystem? TowerOvercharge { get; set; }
        public TowerDemolishSystem? Demolish { get; set; }
        public EnemyFissionSystem? EnemyFission { get; set; }
        public EnemyMorphSystem? EnemyMorph { get; set; }
        public EnemyStealGoldSystem? StealGold { get; set; }
        public ObjectiveSystem? Objective { get; set; }
        public WaveBranchSystem? WaveBranch { get; set; }
        public ResourceNodeSystem? ResourceNode { get; set; }
        public TelegraphSystem? Telegraph { get; set; }
        public TowerSilenceSystem? TowerSilence { get; set; }
        public TowerDispelSystem? Dispel { get; set; }
        public CurseAuraSystem? Curse { get; set; }
        public TowerLinkSystem? TowerLink { get; set; }
        public PullTowerSystem? PullTower { get; set; }
        public BleedSystem? Bleed { get; set; }
        public EnemyWoundSystem? Wound { get; set; }
        public AdaptiveDifficultySystem? AdaptiveDifficulty { get; set; }
        public CorpseEffectSystem? CorpseEffect { get; set; }
        public NestSystem? Nest { get; set; }
        public EnemyHealerSystem? EnemyHealer { get; set; }
        public PlayerSummonSystem? Summon { get; set; }
        public PathModifierSystem? PathModifier { get; set; }
        public EnemyBurrowSystem? Burrow { get; set; }
        public NecromancerSystem? Necromancer { get; set; }
        public RandomEventSystem? RandomEvent { get; set; }
        public ChronoTowerSystem? ChronoTower { get; set; }
        public TowerRelocateSystem? TowerRelocate { get; set; }
        public TowerConstructionSystem? Construction { get; set; }
        public GlobalSkillSystem? GlobalSkill { get; set; }
        public EnemyLifeLinkSystem? LifeLink { get; set; }
        public FogOfWarSystem? Fog { get; set; }
        public PatrolTowerSystem? PatrolTower { get; set; }

        public event Action<int, int> OnEnemyKilled;

        public FrameScheduler(ComponentStore store, GameConfig gameConfig)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.gameConfig = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
        }

        /// <summary>
        /// Execute one full frame of systems, gated by current Phase.
        /// BuildPhase: UI-only systems. WavePhase/Intermission: full combat pipeline.
        /// </summary>
        public void Tick(float deltaTime, int turn)
        {
            store.BeginFrame();
            store.SetTurnCCFlags();

            UpdateTimeScale(ref deltaTime);

            if (Phase == GameState.BuildPhase)
            {
                RunBuildPhase(deltaTime);
                return;
            }

            RunWavePhase(deltaTime, turn);
        }

        /// <summary>
        /// Full game turn with post-tick game logic, matching GameManager.Run() behavior.
        /// </summary>
        public void TickGameTurn(float deltaTime, int turn)
        {
            Tick(deltaTime, turn);
        }

        // ─── Private phase methods ───────────────────────────────────────────

        private void UpdateTimeScale(ref float deltaTime)
        {
            if (store.GlobalTimeScaleDuration[0] > 0f)
            {
                store.GlobalTimeScaleDuration[0] -= 1f;
                if (store.GlobalTimeScaleDuration[0] <= 0f)
                {
                    store.GlobalTimeScaleDuration[0] = 0f;
                    store.GlobalTimeScale[0] = 1f;
                }
            }
            deltaTime = deltaTime * store.GlobalTimeScale[0];
        }

        private void RunBuildPhase(float deltaTime)
        {
            Gold?.Update();
            TowerIncome?.Update(deltaTime);
            Upgrade?.Update();
            Skill?.Update(deltaTime);
            AutoSkill?.Update();
            TowerRelocate?.Update();
            Interest?.Update();
            Mana?.Update(deltaTime, isBuildPhase: true);
            Objective?.Update(deltaTime, Phase);
            ResourceNode?.Update(deltaTime, Phase);
            GlobalSkill?.Update(deltaTime, isBuildPhase: true);
        }

        private void RunWavePhase(float deltaTime, int turn)
        {
            RunPreGameSystems(deltaTime, turn);
            RunSpawningPhase(turn, deltaTime);
            RunAIPhase(turn, deltaTime);
            RunEnemyAffixPhase(deltaTime);
            RunMovementPhase(turn, deltaTime);
            RunTerrainPhase(turn, deltaTime);
            RunMorphPhase(deltaTime, turn);
            RunPreCombatSetup(turn);
            RebuildSpatial(deltaTime, turn);
            RunCombatPhase(deltaTime);
            RunSkillBuffDamagePhase(deltaTime);
            RunDeathResolvePhase(deltaTime);
            RunPostDeathPhase(deltaTime);
        }

        // ── Pre-game: weather, day/night, difficulty, construction, events ──
        private void RunPreGameSystems(float deltaTime, int turn)
        {
            Weather?.Update(deltaTime);
            DayNight?.Update(deltaTime);
            AdaptiveDifficulty?.Update(deltaTime);
            Construction?.Update(deltaTime);

            int waveNum = WaveSpawning?.GetCurrentWave() ?? 1;
            int levelNum = WaveSpawning?.GetCurrentLevel() ?? 1;
            RandomEvent?.Update(deltaTime, waveNum, levelNum);
        }

        // ── Spawning: wave, nest, summoner structures ──
        private void RunSpawningPhase(int turn, float deltaTime)
        {
            WaveSpawning?.Update();
            Nest?.SetTurn(turn);
            Nest?.Update(deltaTime);
        }

        // ── AI phase: behaviour trees, abilities, burrow, necromancer, life link ──
        private void RunAIPhase(int turn, float deltaTime)
        {
            EnemyAI?.SetTurn(turn, deltaTime);
            EnemyAI?.Update();

            EnemyAbility?.SetTurn(turn);
            EnemyAbility?.UpdateCooldowns(deltaTime);
            EnemyAbility?.ExecuteAbilities();
            EnemyAbility?.Update();

            Burrow?.SetTurn(turn);
            Burrow?.Update();
            Burrow?.ApplyBurrowEffects();

            Necromancer?.SetTurn(turn, turn);
            Necromancer?.Update(deltaTime);

            LifeLink?.SetTurn(turn);
            LifeLink?.Update();
            LifeLink?.DecrementCooldowns(deltaTime);
        }

        // ── Enemy affixes ──
        private void RunEnemyAffixPhase(float deltaTime)
        {
            EnemyAffix?.Update(deltaTime);
        }

        // ── Movement: pathfinding, wound, modifiers, healer, summons, steal ──
        private void RunMovementPhase(int turn, float deltaTime)
        {
            Wound?.SetTurn(turn);
            Wound?.Update();
            Pathfinding?.SetTurn(turn);
            EnemyMovement?.SetTurn(turn);
            EnemyMovement?.Update();

            PathModifier?.SetTurn();
            PathModifier?.Update(deltaTime);

            EnemyHealer?.SetTurn(turn);
            EnemyHealer?.Update(deltaTime);

            StealGold?.Update();

            Summon?.SetTurn(turn);
            Summon?.Update(deltaTime);
        }

        // ── Terrain, wave mutators ──
        private void RunTerrainPhase(int turn, float deltaTime)
        {
            Terrain?.SetTurn();
            Terrain?.Update(deltaTime);
            WaveMutator?.SetTurn(turn);
            WaveMutator?.Update(deltaTime);
        }

        // ── Morph ──
        private void RunMorphPhase(float deltaTime, int turn)
        {
            EnemyMorph?.Update(deltaTime);
        }

        // ── Pre-combat setup: SetTurn on all combat systems ──
        private void RunPreCombatSetup(int turn)
        {
            PlayerTowerAttack?.SetTurn(turn);
            TowerAttack?.SetTurn(turn);
            TowerOvercharge?.SetTurn(turn);
            TowerSynergy?.SetTurn();
            TowerLink?.SetTurn();
            Skill?.SetTurn(turn);
            AuraTower?.SetTurn();
            Curse?.SetTurn();
            PullTower?.SetTurn();
            Mana?.SetTurn();
            GlobalSkill?.SetTurn(turn);
        }

        // ── Spatial rebuild + post-rebuild systems (patrol, chrono, fog, telegraph) ──
        private void RebuildSpatial(float deltaTime, int turn)
        {
            store.RebuildSpatialGrid();

            PatrolTower?.SetTurn(turn);
            PatrolTower?.Update(deltaTime);

            ChronoTower?.SetTurn();
            ChronoTower?.Update();

            Fog?.SetTurn();
            Fog?.Update();

            PointDefense?.SetTurn(turn);
            PointDefense?.Update(deltaTime);

            Telegraph?.Update(deltaTime);
        }

        // ── Combat: attacks, synergy, auras, curses, projectiles, mana, skills ──
        private void RunCombatPhase(float deltaTime)
        {
            PlayerTowerAttack?.Update();
            TowerOvercharge?.Update(deltaTime);
            Demolish?.Update();
            TowerAttack?.Update(deltaTime);
            TowerSynergy?.Update();
            TowerLink?.Update();
            AuraTower?.ResolveAuraBuffs();
            Curse?.ResolveCurseDebuffs();
            PullTower?.Update(deltaTime);
            TowerSilence?.Update(deltaTime);
            Dispel?.Update(deltaTime);
            Projectile?.Update(deltaTime);
            EnemyProjectile?.Update(deltaTime);
            Pickup?.Update(deltaTime);
            Mana?.Update(deltaTime, isBuildPhase: false);
            GlobalSkill?.Update(deltaTime, isBuildPhase: false);
        }

        // ── Skill / Buff / Bleed damage ──
        private void RunSkillBuffDamagePhase(float deltaTime)
        {
            Buff?.Update(deltaTime);
            Skill?.ResolveSkillDamage();
            Buff?.ResolveDotDamage();
            Bleed?.Update(deltaTime);
            Bleed?.ResolveBleedDamage();
            Skill?.Update(deltaTime);
        }

        // ── Death resolve + combo decay ──
        private void RunDeathResolvePhase(float deltaTime)
        {
            store.ResolveEnemiesKilledThisFrame();
            Combo?.Update(deltaTime);
        }

        // ── Post-death: fission, life link penalties, objective, resources, income, corpse ──
        private void RunPostDeathPhase(float deltaTime)
        {
            EnemyFission?.Update();
            LifeLink?.ResolveBreakPenalties();
            Objective?.Update(deltaTime, Phase);
            ResourceNode?.Update(deltaTime, Phase);
            TowerIncome?.Update(deltaTime);
            CorpseEffect?.Update(deltaTime);

            // Wave branch: pause combat if branch selection is active
            if (WaveBranch?.IsBranchActive == true)
                return;
        }
    }
}
