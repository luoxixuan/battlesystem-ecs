#nullable enable
using System;
using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Systems;
using BattleSystemECS.Config;

namespace BattleSystemECS.Core
{
    /// <summary>
    /// 统一帧调度器 — 所有帧调度路径（GameManager / Benchmark / Tests）必须走这里。
    /// 
    /// 帧顺序（两阶段模式）：
    ///   Phase 1 (并行可介入): AI、Abilities、Movement
    ///   Phase 2 (串行结算):    RebuildSpatialGrid、Attack、SkillDamage、DOT、Death Resolve
    /// </summary>
    public class FrameScheduler
    {
        private readonly ComponentStore store;
        private readonly GameConfig gameConfig;

        /// <summary>
        /// Current game phase — controls which systems run per frame.
        /// BuildPhase: only tower placement/upgrade UI, no combat.
        /// WavePhase: full combat systems.
        /// </summary>
        public GameState Phase { get; set; } = GameState.WavePhase;

        // Systems — nullable，调用方按需注入
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

        // Kill notification: fires for each enemy killed during ResolveEnemiesKilledThisFrame
        // Used by ComboSystem to increment combo counters.
        public event Action<int, int> OnEnemyKilled;

        public FrameScheduler(ComponentStore store, GameConfig gameConfig)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.gameConfig = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
        }

        /// <summary>
        /// 执行一帧完整调度。
        /// Systems are gated by current Phase:
        ///   BuildPhase — tower placement/upgrade only (no WaveSpawning/EnemyAI/Combat)
        ///   WavePhase  — full combat pipeline
        /// </summary>
        /// <param name="deltaTime">时间步长（通常 1f）</param>
        /// <param name="turn">当前回合编号（从 1 开始）</param>
        public void Tick(float deltaTime, int turn)
        {
            // ── Phase 0: 帧初始化 ──────────────────────────────────────────
            store.BeginFrame();
            store.SetTurnCCFlags();

            // ── Time Dilation: apply per-player time scale (bullet time / fast-forward) ──
            //衰减剩余持续时间
            if (store.GlobalTimeScaleDuration[0] > 0f)
            {
                store.GlobalTimeScaleDuration[0] -= 1f;
                if (store.GlobalTimeScaleDuration[0] <= 0f)
                {
                    store.GlobalTimeScaleDuration[0] = 0f;
                    store.GlobalTimeScale[0] = 1f; // 恢复到正常速度
                }
            }
            // 应用时间缩放到 deltaTime
            float effectiveDelta = deltaTime * store.GlobalTimeScale[0];

            if (Phase == GameState.BuildPhase)
            {
// ── BuildPhase: tower placement/upgrade UI only ───────────
                Gold?.Update();
                TowerIncome?.Update(deltaTime); // income tower gold production (build phase runs every frame)
                Upgrade?.Update();
                Skill?.Update(deltaTime); // skill cooldown ticking
                AutoSkill?.Update();      // auto-cast ready skills
                Interest?.Update();       // bank/interest system
                Mana?.Update(deltaTime, isBuildPhase: true); // mana regen (build phase = higher regen)
                Objective?.Update(deltaTime, Phase); // escort NPC movement, objective timers
                ResourceNode?.Update(deltaTime, Phase); // resource node production
                return;
            }

            // ── WavePhase / Intermission: full combat pipeline ──────────────

// ── Phase 0.5: Weather update (before combat) ───────────────────
            Weather?.Update(effectiveDelta);

            // ── Phase 0.55: Day/Night cycle update ────────────────────────────
            DayNight?.Update(effectiveDelta);

            // ── Phase 0.6: Adaptive Difficulty update ────────────────────────
            AdaptiveDifficulty?.Update(effectiveDelta);

            // ── Phase 0.65: Random Mid-Wave Events ────────────────────────────
            int waveNum = WaveSpawning?.GetCurrentWave() ?? 1;
            int lvlNum = WaveSpawning?.GetCurrentLevel() ?? 1;
            RandomEvent?.Update(effectiveDelta, waveNum, lvlNum);

            // ── Phase 1: 生成 ─────────────────────────────────────────────
            WaveSpawning?.Update();

            // ── Phase 1.5: Nest / Spawner Structures ────────────────────
            Nest?.SetTurn(turn);
            Nest?.Update(effectiveDelta);

            // ── Phase 2: AI + Abilities ───────────────────────────────────
            EnemyAI?.SetTurn(turn, effectiveDelta);
            EnemyAI?.Update();

            EnemyAbility?.SetTurn(turn);
            EnemyAbility?.UpdateCooldowns(effectiveDelta);
            EnemyAbility?.ExecuteAbilities();
            EnemyAbility?.Update();

            // ── Phase 2.5: Enemy Affixes (per-enemy affix effects) ──────────
            EnemyAffix?.Update(effectiveDelta);

            // ── Phase 2.55: Enemy Burrow — underground enemy state transitions ──
            Burrow?.SetTurn(turn);
            Burrow?.Update();
            Burrow?.ApplyBurrowEffects();

            // ── Phase 2.6: Necromancer — resurrect corpses as reanimated minions ──
            Necromancer?.SetTurn(turn, turn);  // second param = sim elapsed (turn = proxy for sim seconds)
            Necromancer?.Update(deltaTime);

            // ── Phase 3: Movement ──────────────────────────────────────────
            Wound?.SetTurn(turn);
            Wound?.Update();
            Pathfinding?.SetTurn(turn);
            EnemyMovement?.SetTurn(turn);
            EnemyMovement?.Update();

            // ── Phase 3.05: Path Modifiers — reroute enemies inside influence zones ──
            PathModifier?.SetTurn();
            PathModifier?.Update(effectiveDelta);

            // ── Phase 3.5: Terrain Effects (after movement, before combat) ──
            Terrain?.SetTurn();
            Terrain?.Update(effectiveDelta);

            // ── Phase 3.6: Wave Mutators (global wave modifiers) ─────────────
            WaveMutator?.SetTurn(turn);
            WaveMutator?.Update(effectiveDelta);

            // ── Phase 3.7: Enemy Morph — transform mid-wave enemies before combat ──
            EnemyMorph?.Update(effectiveDelta);

            // ── Phase 3.75: Enemy Healer — heal-over-time for healer units ──────
            EnemyHealer?.SetTurn(turn);
            EnemyHealer?.Update(effectiveDelta);

            // ── Phase 3.8: Enemy Steal Gold — process thieves that reached the base ──
            StealGold?.Update();

            // ── Phase 3.85: Player Summons — update summoned unit movement and attacks ──
            Summon?.SetTurn(turn);
            Summon?.Update(effectiveDelta);

            // ── Phase 4: Combat — SetTurn ─────────────────────────────────
            PlayerTowerAttack?.SetTurn(turn);
            TowerAttack?.SetTurn(turn);
            TowerOvercharge?.SetTurn(turn);
            TowerSynergy?.SetTurn();
            TowerLink?.SetTurn();
            Skill?.SetTurn(turn);
            AuraTower?.SetTurn();
            Curse?.SetTurn();
            PullTower?.SetTurn();
            Mana?.SetTurn(); // cache tech tree mana bonuses

            // ── Phase 5: Spatial Rebuild ──────────────────────────────────
            store.RebuildSpatialGrid();

            // ── Phase 5.5: Point Defense — intercept enemy projectiles before they hit ──
            PointDefense?.SetTurn(turn);
            PointDefense?.Update(effectiveDelta);

            // ── Phase 5.6: Telegraph System — update warning zones countdown ──
            Telegraph?.Update(effectiveDelta);

            // ── Phase 6: Combat — Update ──────────────────────────────────
            PlayerTowerAttack?.Update();
            TowerOvercharge?.Update(effectiveDelta);
            // TowerDemolish: process any towers marked for sacrifice this frame
            // Runs before TowerAttack so demolish damage is resolved before regular attacks
            Demolish?.Update();
            TowerAttack?.Update(effectiveDelta);
            TowerSynergy?.Update();
            TowerLink?.Update();
            AuraTower?.ResolveAuraBuffs();
            Curse?.ResolveCurseDebuffs();
            PullTower?.Update(effectiveDelta);
            TowerSilence?.Update(effectiveDelta);
            Dispel?.Update(effectiveDelta);
            Projectile?.Update(effectiveDelta);
            // Update enemy projectiles (moves them toward player base)
            EnemyProjectile?.Update(effectiveDelta);
            Pickup?.Update(effectiveDelta);
            Mana?.Update(effectiveDelta, isBuildPhase: false); // mana regen (wave phase = normal regen)

            // ── Phase 7: Skill / Buff Damage ──────────────────────────────
            Buff?.Update(effectiveDelta);
            Skill?.ResolveSkillDamage();
            Buff?.ResolveDotDamage();
            Bleed?.Update(effectiveDelta);
            Bleed?.ResolveBleedDamage();
            Skill?.Update(effectiveDelta); // skill cooldown ticking (WavePhase only path)

            // ── Phase 9: Death Resolve ─────────────────────────────────────
            // Collect kill events before resolving so ComboSystem can subscribe
            var killEvents = new List<(int enemyId, int playerId)>();
            // Snapshot the death queue (readIdx is set by ResolveEnemiesKilledThisFrame internally)
            // We need to collect kills AFTER ResolveEnemiesKilledThisFrame processes them.
            // The safest approach: resolve first, then fire event. But we need the IDs.
            // Alternative: hook into ComponentStore.ResolveEnemiesKilledThisFrame callback.
            //
            // For now, fire a generic "kills resolved" signal after resolve completes.
            // Subscribers can read store.TotalKills delta or the combo counters directly.
            store.ResolveEnemiesKilledThisFrame();
            Combo?.Update(deltaTime); // decay already called above — safe to call again (idempotent)

            // ── Phase 9.5: Enemy Fission — spawn children after death resolve ─────
            EnemyFission?.Update();

// ── Phase 9.6: Objective System — update objective state ─────────────
            Objective?.Update(effectiveDelta, Phase);
            ResourceNode?.Update(effectiveDelta, Phase); // resource node production
            TowerIncome?.Update(effectiveDelta);         // income tower gold production

            // ── Phase 9.65: Corpse Effect System — tick ground effect durations and apply ──
            CorpseEffect?.Update(effectiveDelta);

            // ── Phase 9.7: Wave Branch — pause combat while player selects branch ──
            if (WaveBranch?.IsBranchActive == true)
            {
                // Combat paused — branch UI is showing. Skip remaining combat systems this frame.
                return;
            }
        }

        /// <summary>
        /// 游戏主循环使用的完整每回合调度（含游戏状态维护）。
        /// 与 GameManager.Run() 行为完全对齐。
        /// </summary>
        public void TickGameTurn(float deltaTime, int turn)
        {
            Tick(deltaTime, turn);

            // ── Post-tick 游戏逻辑（GameManager 中每帧执行的非战斗逻辑）──
            // Gold/Upgrade/Skill cooldown already handled inside Tick based on phase.
            // Additional game-level systems that run regardless of phase:
            // (TechTree is read-only here, Gold/Upgrade already called above)
        }
    }
}
