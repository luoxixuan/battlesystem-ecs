using BattleSystemECS.Tests.Infrastructure;
using System;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Mechanisms.Combat
{
    /// <summary>
    /// Tests for Round 206 Direction 1: Culling System.
    /// HP-threshold instant execute for high-burst single-target towers.
    /// Verifies:
    ///   1. Default state: per-tower / per-enemy culling flags are 0 (zero-overhead, opt-out sentinel)
    ///   2. TryCull: opt-out (no per-tower flag) is no-op
    ///   3. TryCull: opt-out (no per-enemy threshold + config default = 0) is no-op
    ///   4. TryCull: ExecuteImmune enemies cannot be culled
    ///   5. TryCull: Invulnerable enemies cannot be culled
    ///   6. TryCull: above-threshold HP does not fire cull
    ///   7. TryCull: at-or-below threshold + sufficient damage DOES fire cull
    ///   8. TryCull: damage-gate (hitDamage < maxHp * damagePct) prevents cull
    ///   9. TryCull: at threshold fires event, sets HP to 0, queues death, increments player stacks
    ///   10. TryCull: cull event fires OnCullingKilled with correct (enemy, tower, player, bonusGold) signature
    ///   11. TryCull: stacks clamped at MaxPlayerStacks
    ///   12. TryCull: invalid enemyId / towerId no-op
    ///   13. TryCull: dead enemy (HP<=0) no-op
    ///   14. TryCull: config Enabled=false short-circuits
    ///   15. OnWaveStart resets per-player stacks to 0
    ///   16. ComputeBonusGold: BaseBonusGold * (1 + stacks * pct)
    ///   17. ComputeBonusGold: stacks=0 returns BaseBonusGold
    ///   18. CullingConfig defaults
    ///   19. LoadConfig override replaces config
    ///   20. GetPlayerStacks returns correct count
    /// </summary>
    public class CullingSystemTests : BattleTestBase
    {
        private const int PlayerId = 0;
        private const int MaxPlayers = 10;
        private const float DeltaTime = 1f / 60f;

        // ── Test helpers ────────────────────────────────────────────────

        private CullingSystem MakeSystem(CullingConfig? config = null)
        {
            Store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            var system = new CullingSystem(Store, PlayerId);
            if (config != null) system.LoadConfig(config);
            return system;
        }

        /// <summary>Spawn a Culling-eligible enemy with the given cull threshold.</summary>
        private int MakeCullableEnemy(float maxHp = 100f, float thresholdPct = 0.10f)
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, maxHp, maxHp, 5f, 10, 1, "TestEnemy");
            Store.EnemyCullingThresholdPct[eid] = thresholdPct;
            return eid;
        }

        /// <summary>Spawn a Culling-enabled tower with the given damage-pct gate.</summary>
        private int MakeCullingTower(float range = 5f, float damagePct = 0.05f, float x = 0f, float y = 0f)
        {
            int tid = RawTower((int)x, (int)y, Components.TowerType.Basic, damage: 50f, range: (int)range, speed: 1f, level: 1, cost: 100f);
            Store.TowerIsCullingTower[tid] = true;
            Store.TowerCullingDamagePct[tid] = damagePct;
            return tid;
        }

        // ── 1. Default state ────────────────────────────────────────────
        [Fact]
        public void DefaultState_AllFieldsZero()
        {
            Assert.False(Store.TowerIsCullingTower[0]);
            Assert.Equal(0f, Store.TowerCullingDamagePct[0]);
            Assert.Equal(0f, Store.EnemyCullingThresholdPct[0]);
        }

        // ── 2-6/8/13/14: 所有“不触发 cull”的门合并为理论驱动 ──────────
        // 各分支只有前置条件不同，断言同构：TryCull 返回 false 且敌人 HP 不变。
        public enum CullBlockGate
        {
            TowerNotFlagged,      // 塔没有 Culling 旗标
            BothThresholdsZero,   // per-enemy 阈值与 config 默认阈值都是 0
            ExecuteImmune,        // 处决免疫敌人
            Invulnerable,         // 无敌敌人
            AboveThreshold,       // HP 比例高于阈值
            DamageGateBelow,      // 单发伤害低于伤害门
            DeadEnemy,            // 已死亡敌人
            ConfigDisabled,       // 配置主开关关闭
        }

        [Theory]
        [InlineData(CullBlockGate.TowerNotFlagged, 5f)]
        [InlineData(CullBlockGate.BothThresholdsZero, 1f)]
        [InlineData(CullBlockGate.ExecuteImmune, 5f)]
        [InlineData(CullBlockGate.Invulnerable, 5f)]
        [InlineData(CullBlockGate.AboveThreshold, 20f)]
        [InlineData(CullBlockGate.DamageGateBelow, 5f)]
        [InlineData(CullBlockGate.DeadEnemy, 0f)]
        [InlineData(CullBlockGate.ConfigDisabled, 5f)]
        public void TryCull_BlockedByGate_ReturnsFalseAndKeepsHp(CullBlockGate gate, float expectedHp)
        {
            CullingSystem sys;
            int eid;
            int tid;
            float hitDamage;

            switch (gate)
            {
                case CullBlockGate.TowerNotFlagged:
                    sys = MakeSystem();
                    eid = MakeCullableEnemy(maxHp: 100f, thresholdPct: 0.10f);
                    Store.EnemyHealth[eid] = expectedHp;
                    tid = RawTower(0, 0, Components.TowerType.Basic, damage: 50f, range: 5, speed: 1f, level: 1, cost: 100f);
                    hitDamage = 50f; // 塔无旗标 → no cull
                    break;
                case CullBlockGate.BothThresholdsZero:
                    sys = MakeSystem(new CullingConfig { DefaultThresholdPct = 0f, DefaultDamagePct = 0f });
                    eid = Store.AddEnemy(0f, 0f, 1f, 100f, 100f, 5f, 10, 1, "E");
                    Store.EnemyHealth[eid] = expectedHp;
                    tid = MakeCullingTower(damagePct: 0f); // per-enemy 与 config 阈值都为 0
                    hitDamage = 50f;
                    break;
                case CullBlockGate.ExecuteImmune:
                    sys = MakeSystem();
                    eid = MakeCullableEnemy(maxHp: 100f, thresholdPct: 0.10f);
                    Store.EnemyHealth[eid] = expectedHp;
                    Store.EnemyExecuteImmune[eid] = true;
                    tid = MakeCullingTower(damagePct: 0.05f);
                    hitDamage = 50f;
                    break;
                case CullBlockGate.Invulnerable:
                    sys = MakeSystem();
                    eid = MakeCullableEnemy(maxHp: 100f, thresholdPct: 0.10f);
                    Store.EnemyHealth[eid] = expectedHp;
                    Store.EnemyIsInvulnerable[eid] = true;
                    tid = MakeCullingTower(damagePct: 0.05f);
                    hitDamage = 50f;
                    break;
                case CullBlockGate.AboveThreshold:
                    sys = MakeSystem();
                    eid = MakeCullableEnemy(maxHp: 100f, thresholdPct: 0.10f);
                    Store.EnemyHealth[eid] = expectedHp; // 20% > 10% 阈值
                    tid = MakeCullingTower(damagePct: 0.05f);
                    hitDamage = 50f;
                    break;
                case CullBlockGate.DamageGateBelow:
                    sys = MakeSystem();
                    eid = MakeCullableEnemy(maxHp: 100f, thresholdPct: 0.10f);
                    Store.EnemyHealth[eid] = expectedHp;
                    tid = MakeCullingTower(damagePct: 0.50f); // 需要 50% MaxHP 的单发伤害
                    hitDamage = 10f;                                  // 10 < 50 → 不触发
                    break;
                case CullBlockGate.DeadEnemy:
                    sys = MakeSystem();
                    eid = MakeCullableEnemy(maxHp: 100f, thresholdPct: 0.10f);
                    Store.EnemyHealth[eid] = 0f; // 已死亡
                    tid = MakeCullingTower(damagePct: 0.05f);
                    hitDamage = 50f;
                    break;
                default: // ConfigDisabled
                    sys = MakeSystem(new CullingConfig { Enabled = false });
                    eid = MakeCullableEnemy(maxHp: 100f, thresholdPct: 0.10f);
                    Store.EnemyHealth[eid] = expectedHp;
                    tid = MakeCullingTower(damagePct: 0.05f);
                    hitDamage = 50f;
                    break;
            }

            bool result = sys.TryCull(tid, eid, hitDamage);

            Assert.False(result);
            Assert.Equal(expectedHp, Store.EnemyHealth[eid]); // HP 保持不变
        }

        // ── 7. At-threshold + sufficient damage fires cull ───────────────
        [Fact]
        public void TryCull_AtThresholdWithSufficientDamageFiresCull()
        {
            var sys = MakeSystem();
            int eid = MakeCullableEnemy(maxHp: 100f, thresholdPct: 0.10f);
            Store.EnemyHealth[eid] = 10f; // exactly at threshold (10% of 100)
            int tid = MakeCullingTower(damagePct: 0.05f);
            // hitDamage=50 >= 100 * 0.05 = 5 → cull fires
            bool result = sys.TryCull(tid, eid, 50f);
            Assert.True(result);
            Assert.Equal(0f, Store.EnemyHealth[eid]); // HP zeroed
        }

        // ── 9. Cull fires event, sets HP to 0, queues death, increments stacks ──
        [Fact]
        public void TryCull_FiresEventAndIncrementsStacks()
        {
            var sys = MakeSystem();
            int eid = MakeCullableEnemy(maxHp: 100f, thresholdPct: 0.10f);
            Store.EnemyHealth[eid] = 5f;
            int tid = MakeCullingTower(damagePct: 0.05f);

            int firedEnemy = -1, firedTower = -1, firedPlayer = -1;
            float firedBonus = -1f;
            sys.OnCullingKilled += (en, tw, pl, bg) =>
            {
                firedEnemy = en;
                firedTower = tw;
                firedPlayer = pl;
                firedBonus = bg;
            };

            bool result = sys.TryCull(tid, eid, 50f);
            Assert.True(result);
            Assert.Equal(eid, firedEnemy);
            Assert.Equal(tid, firedTower);
            Assert.Equal(PlayerId, firedPlayer);
            // 首次 cull 的堆栈为 0：奖金精确等于 config.BaseBonusGold（从读取的配置推导）。
            float expectedBonus = sys.Config.BaseBonusGold * (1f + 0f * sys.Config.PlayerStackBonusGoldPct);
            Assert.Equal(expectedBonus, firedBonus);
            Assert.Equal(0f, Store.EnemyHealth[eid]);
            Assert.Equal(1, Store.PlayerCullingStacks[PlayerId]);

            // Resolve death so the enemy is no longer active
            Store.ResolveEnemiesKilledThisFrame();
            Assert.False(Store.EnemyActive[eid]);
        }

        // ── 10. Bonus gold scales with stacks ───────────────────────────
        [Fact]
        public void TryCull_BonusGoldScalesWithStacks()
        {
            var sys = MakeSystem(new CullingConfig
            {
                BaseBonusGold = 10f,
                PlayerStackBonusGoldPct = 0.05f,
                DefaultThresholdPct = 0.10f,
                DefaultDamagePct = 0.05f
            });

            int eventIndex = 0;
            float bonus1 = -1f;
            float bonus2 = -1f;
            sys.OnCullingKilled += (en, tw, pl, bg) =>
            {
                if (eventIndex == 0) bonus1 = bg;
                else if (eventIndex == 1) bonus2 = bg;
                eventIndex++;
            };

            // First cull: pre-call stacks=0 → bonus = 10 * (1 + 0*0.05) = 10.0
            // (per contract: stacks apply to "subsequent" culls, so the first cull pays base only)
            int eid1 = MakeCullableEnemy(maxHp: 100f, thresholdPct: 0.10f);
            Store.EnemyHealth[eid1] = 5f;
            int tid = MakeCullingTower(damagePct: 0.05f);
            sys.TryCull(tid, eid1, 50f);
            Assert.Equal(10f, bonus1);

            // Second cull: pre-call stacks=1 → bonus = 10 * (1 + 1*0.05) = 10.5
            int eid2 = MakeCullableEnemy(maxHp: 100f, thresholdPct: 0.10f);
            Store.EnemyHealth[eid2] = 5f;
            sys.TryCull(tid, eid2, 50f);
            Assert.Equal(10.5f, bonus2);
        }

        // ── 11. Stacks clamped at MaxPlayerStacks ───────────────────────
        [Fact]
        public void TryCull_StacksClampedAtMax()
        {
            var sys = MakeSystem(new CullingConfig { MaxPlayerStacks = 3 });
            int tid = MakeCullingTower(damagePct: 0.05f);
            for (int i = 0; i < 5; i++)
            {
                int eid = MakeCullableEnemy(maxHp: 100f, thresholdPct: 0.10f);
                Store.EnemyHealth[eid] = 5f;
                sys.TryCull(tid, eid, 50f);
            }
            Assert.Equal(3, Store.PlayerCullingStacks[PlayerId]); // capped
        }

        // ── 12. Invalid inputs ─────────────────────────────────────────
        [Fact]
        public void TryCull_InvalidIdsNoOp()
        {
            var sys = MakeSystem();
            int eid = MakeCullableEnemy(maxHp: 100f, thresholdPct: 0.10f);
            Store.EnemyHealth[eid] = 5f;
            int tid = MakeCullingTower(damagePct: 0.05f);

            Assert.False(sys.TryCull(-1, eid, 50f));
            Assert.False(sys.TryCull(tid, -1, 50f));
            Assert.False(sys.TryCull(ComponentStore.MAX_ENTITIES + 5, eid, 50f));
            Assert.False(sys.TryCull(tid, ComponentStore.MAX_ENTITIES + 5, 50f));
        }

        // ── 15. OnWaveStart resets per-player stacks ───────────────────
        [Fact]
        public void OnWaveStart_ResetsStacksToZero()
        {
            var sys = MakeSystem();
            int tid = MakeCullingTower(damagePct: 0.05f);
            for (int i = 0; i < 3; i++)
            {
                int eid = MakeCullableEnemy(maxHp: 100f, thresholdPct: 0.10f);
                Store.EnemyHealth[eid] = 5f;
                sys.TryCull(tid, eid, 50f);
            }
            Assert.Equal(3, Store.PlayerCullingStacks[PlayerId]);

            sys.OnWaveStart();
            Assert.Equal(0, Store.PlayerCullingStacks[PlayerId]);
        }

        // ── 16. ComputeBonusGold: BaseBonusGold * (1 + stacks * pct) ────
        [Fact]
        public void ComputeBonusGold_FormulaCorrect()
        {
            var sys = MakeSystem(new CullingConfig
            {
                BaseBonusGold = 20f,
                PlayerStackBonusGoldPct = 0.10f
            });
            Assert.Equal(20f, sys.ComputeBonusGold(0));    // 20 * 1.0
            Assert.Equal(22f, sys.ComputeBonusGold(1));    // 20 * 1.1
            Assert.Equal(40f, sys.ComputeBonusGold(10));   // 20 * 2.0
        }

        // ── 17. ComputeBonusGold: negative stacks → 0 ─────────────────
        [Fact]
        public void ComputeBonusGold_NegativeStacksClampedToZero()
        {
            var sys = MakeSystem(new CullingConfig { BaseBonusGold = 10f });
            Assert.Equal(10f, sys.ComputeBonusGold(-5)); // clamps to 0
        }

        // ── 18. CullingConfig defaults ─────────────────────────────────
        [Fact]
        public void CullingConfig_Defaults()
        {
            var cfg = new CullingConfig();
            Assert.True(cfg.Enabled);
            Assert.Equal(0.10f, cfg.DefaultThresholdPct);
            Assert.Equal(0.05f, cfg.DefaultDamagePct);
            Assert.Equal(10f, cfg.BaseBonusGold);
            Assert.Equal(0.05f, cfg.PlayerStackBonusGoldPct);
            Assert.Equal(50, cfg.MaxPlayerStacks);
        }

        // ── 19. LoadConfig override replaces config ────────────────────
        [Fact]
        public void LoadConfig_ReplacesConfig()
        {
            var sys = MakeSystem();
            var newCfg = new CullingConfig { BaseBonusGold = 999f };
            sys.LoadConfig(newCfg);
            Assert.Equal(999f, sys.Config.BaseBonusGold);
        }

        // ── 20. GetPlayerStacks returns correct count ──────────────────
        [Fact]
        public void GetPlayerStacks_ReturnsCorrectCount()
        {
            var sys = MakeSystem();
            Assert.Equal(0, sys.GetPlayerStacks(PlayerId));
            Store.PlayerCullingStacks[PlayerId] = 7;
            Assert.Equal(7, sys.GetPlayerStacks(PlayerId));
            Assert.Equal(0, sys.GetPlayerStacks(-1)); // invalid
            Assert.Equal(0, sys.GetPlayerStacks(MaxPlayers + 5)); // invalid
        }

        // ── 21. Update is a no-op (event-driven) ──────────────────────
        [Fact]
        public void Update_NoOp()
        {
            var sys = MakeSystem();
            int eid = MakeCullableEnemy(maxHp: 100f, thresholdPct: 0.10f);
            Store.EnemyHealth[eid] = 5f;
            int tid = MakeCullingTower(damagePct: 0.05f);
            // Per-frame Update must not auto-cull; the per-hit hot path is TryCull.
            sys.Update(DeltaTime);
            Assert.Equal(5f, Store.EnemyHealth[eid]); // HP unchanged
        }

        // ── 22. Default-fallback: per-enemy 0 → config default kicks in ─
        [Fact]
        public void TryCull_FallsBackToConfigDefaultThreshold()
        {
            var sys = MakeSystem(new CullingConfig
            {
                DefaultThresholdPct = 0.20f,
                DefaultDamagePct = 0.05f
            });
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 100f, 5f, 10, 1, "E");
            // EnemyCullingThresholdPct = 0 (default) → falls back to 0.20
            Store.EnemyHealth[eid] = 15f; // 15% < 20% threshold
            int tid = MakeCullingTower(damagePct: 0f); // also 0, fall back to config 0.05
            bool result = sys.TryCull(tid, eid, 50f); // 50 >= 100*0.05 = 5
            Assert.True(result);
            Assert.Equal(0f, Store.EnemyHealth[eid]);
        }
    }
}