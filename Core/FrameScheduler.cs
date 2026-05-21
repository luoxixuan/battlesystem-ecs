#nullable enable
using System;
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

        // Systems — nullable，调用方按需注入
        public WaveSpawningSystem? WaveSpawning { get; set; }
        public EnemyAISystem? EnemyAI { get; set; }
        public EnemyAbilitySystem? EnemyAbility { get; set; }
        public EnemyMovementSystem? EnemyMovement { get; set; }
        public PlayerTowerAttackSystem? PlayerTowerAttack { get; set; }
        public TowerAttackSystem? TowerAttack { get; set; }
        public SkillSystem? Skill { get; set; }
        public BuffSystem? Buff { get; set; }
        public TechTreeSystem? TechTree { get; set; }
        public GoldSystem? Gold { get; set; }
        public UpgradeSystem? Upgrade { get; set; }

        public FrameScheduler(ComponentStore store, GameConfig gameConfig)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.gameConfig = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
        }

        /// <summary>
        /// 执行一帧完整调度。
        /// </summary>
        /// <param name="deltaTime">时间步长（通常 1f）</param>
        /// <param name="turn">当前回合编号（从 1 开始）</param>
        public void Tick(float deltaTime, int turn)
        {
            // ── Phase 0: 帧初始化 ──────────────────────────────────────────
            store.BeginFrame();
            store.SetTurnCCFlags();

            // ── Phase 1: 生成 ─────────────────────────────────────────────
            WaveSpawning?.Update();

            // ── Phase 2: AI + Abilities ───────────────────────────────────
            EnemyAI?.SetTurn(turn);
            EnemyAI?.Update();

            EnemyAbility?.SetTurn(turn);
            EnemyAbility?.UpdateCooldowns(deltaTime);
            EnemyAbility?.ExecuteAbilities();
            EnemyAbility?.Update();

            // ── Phase 3: Movement ──────────────────────────────────────────
            EnemyMovement?.SetTurn(turn);
            EnemyMovement?.Update();

            // ── Phase 4: Combat — SetTurn ─────────────────────────────────
            PlayerTowerAttack?.SetTurn(turn);
            TowerAttack?.SetTurn(turn);
            Skill?.SetTurn(turn);

            // ── Phase 5: Spatial Rebuild ──────────────────────────────────
            store.RebuildSpatialGrid();

            // ── Phase 6: Combat — Update ──────────────────────────────────
            PlayerTowerAttack?.Update();
            TowerAttack?.Update(deltaTime);

            // ── Phase 7: Skill / Buff Damage ──────────────────────────────
            Buff?.Update(deltaTime);
            Skill?.ResolveSkillDamage();
            Buff?.ResolveDotDamage();

            // ── Phase 8: Death Resolve ─────────────────────────────────────
            store.ResolveEnemiesKilledThisFrame();
        }

        /// <summary>
        /// 游戏主循环使用的完整每回合调度（含游戏状态维护）。
        /// 与 GameManager.Run() 行为完全对齐。
        /// </summary>
        public void TickGameTurn(float deltaTime, int turn)
        {
            Tick(deltaTime, turn);

            // ── Post-tick 游戏逻辑（GameManager 中每帧执行的非战斗逻辑）──
            Gold?.Update();
            Upgrade?.Update();
            Skill?.Update(deltaTime); // 冷却更新
        }
    }
}
