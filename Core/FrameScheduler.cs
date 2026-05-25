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
        public AuraTowerSystem? AuraTower { get; set; }
        public PathfindingSystem? Pathfinding { get; set; }
        public ProjectileSystem? Projectile { get; set; }
        public TerrainSystem? Terrain { get; set; }
        public WaveMutatorSystem? WaveMutator { get; set; }

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

            if (Phase == GameState.BuildPhase)
            {
                // ── BuildPhase: tower placement/upgrade UI only ────────────
                Gold?.Update();
                Upgrade?.Update();
                Skill?.Update(deltaTime); // skill cooldown ticking
                AutoSkill?.Update();      // auto-cast ready skills
                return;
            }

            // ── WavePhase / Intermission: full combat pipeline ──────────────

            // ── Phase 0.5: Weather update (before combat) ───────────────────
            Weather?.Update(deltaTime);

            // ── Phase 1: 生成 ─────────────────────────────────────────────
            WaveSpawning?.Update();

            // ── Phase 2: AI + Abilities ───────────────────────────────────
            EnemyAI?.SetTurn(turn, deltaTime);
            EnemyAI?.Update();

            EnemyAbility?.SetTurn(turn);
            EnemyAbility?.UpdateCooldowns(deltaTime);
            EnemyAbility?.ExecuteAbilities();
            EnemyAbility?.Update();

            // ── Phase 3: Movement ──────────────────────────────────────────
            Pathfinding?.SetTurn(turn);
            EnemyMovement?.SetTurn(turn);
            EnemyMovement?.Update();

            // ── Phase 3.5: Terrain Effects (after movement, before combat) ──
            Terrain?.SetTurn();
            Terrain?.Update(deltaTime);

            // ── Phase 3.6: Wave Mutators (global wave modifiers) ─────────────
            WaveMutator?.SetTurn(turn);
            WaveMutator?.Update(deltaTime);

            // ── Phase 4: Combat — SetTurn ─────────────────────────────────
            PlayerTowerAttack?.SetTurn(turn);
            TowerAttack?.SetTurn(turn);
            TowerSynergy?.SetTurn();
            Skill?.SetTurn(turn);
            AuraTower?.SetTurn();

            // ── Phase 5: Spatial Rebuild ──────────────────────────────────
            store.RebuildSpatialGrid();

            // ── Phase 6: Combat — Update ──────────────────────────────────
            PlayerTowerAttack?.Update();
            TowerAttack?.Update(deltaTime);
            TowerSynergy?.Update();
            AuraTower?.ResolveAuraBuffs();
            Projectile?.Update(deltaTime);

            // ── Phase 7: Skill / Buff Damage ──────────────────────────────
            Buff?.Update(deltaTime);
            Skill?.ResolveSkillDamage();
            Buff?.ResolveDotDamage();
            Skill?.Update(deltaTime); // skill cooldown ticking (WavePhase only path)

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
