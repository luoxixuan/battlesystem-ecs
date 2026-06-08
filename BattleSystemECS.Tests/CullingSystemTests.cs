using System;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
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
    public class CullingSystemTests
    {
        private const int PlayerId = 0;
        private const int MaxPlayers = 10;
        private const float DeltaTime = 1f / 60f;

        // ── Test helpers ────────────────────────────────────────────────

        private static (CullingSystem system, ComponentStore store) MakeSystem(CullingConfig config = null)
        {
            var store = new ComponentStore();
            store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            var system = new CullingSystem(store, PlayerId);
            if (config != null) system.LoadConfig(config);
            return (system, store);
        }

        /// <summary>Spawn a Culling-eligible enemy with the given cull threshold.</summary>
        private static int MakeCullableEnemy(ComponentStore store, float maxHp = 100f, float thresholdPct = 0.10f)
        {
            int eid = store.AddEnemy(0f, 0f, 1f, maxHp, maxHp, 5f, 10, 1, "TestEnemy");
            store.EnemyCullingThresholdPct[eid] = thresholdPct;
            return eid;
        }

        /// <summary>Spawn a Culling-enabled tower with the given damage-pct gate.</summary>
        private static int MakeCullingTower(ComponentStore store, float range = 5f, float damagePct = 0.05f, float x = 0f, float y = 0f)
        {
            int tid = 0;
            store.AddTower(tid, Components.TowerType.Basic, damage: 50f, range: (int)range, speed: 1f, level: 1, cost: 100f);
            store.TowerIsCullingTower[tid] = true;
            store.TowerCullingDamagePct[tid] = damagePct;
            return tid;
        }

        // ── 1. Default state ────────────────────────────────────────────
        [Fact]
        public void DefaultState_AllFieldsZero()
        {
            var store = new ComponentStore();
            Assert.False(store.TowerIsCullingTower[0]);
            Assert.Equal(0f, store.TowerCullingDamagePct[0]);
            Assert.Equal(0f, store.EnemyCullingThresholdPct[0]);
        }

        // ── 2. Opt-out (no per-tower flag) ──────────────────────────────
        [Fact]
        public void TryCull_TowerWithoutFlagIsNoOp()
        {
            var (sys, store) = MakeSystem();
            int eid = MakeCullableEnemy(store, maxHp: 100f, thresholdPct: 0.10f);
            store.EnemyHealth[eid] = 5f; // below threshold
            int tid = 0;
            store.AddTower(tid, Components.TowerType.Basic, damage: 50f, range: 5, speed: 1f, level: 1, cost: 100f);
            // TowerIsCullingTower[default] = false → no cull
            bool result = sys.TryCull(tid, eid, 50f);
            Assert.False(result);
            Assert.Equal(5f, store.EnemyHealth[eid]); // HP unchanged
        }

        // ── 3. Opt-out (no per-enemy threshold + config default 0) ─────
        [Fact]
        public void TryCull_BothThresholdsZeroIsNoOp()
        {
            var (sys, store) = MakeSystem(new CullingConfig { DefaultThresholdPct = 0f, DefaultDamagePct = 0f });
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 5f, 10, 1, "E");
            // EnemyCullingThresholdPct[default] = 0; DefaultThresholdPct = 0
            store.EnemyHealth[eid] = 1f;
            int tid = MakeCullingTower(store, damagePct: 0f);
            // Both opt-out → culling disabled for this pair
            bool result = sys.TryCull(tid, eid, 50f);
            Assert.False(result);
            Assert.Equal(1f, store.EnemyHealth[eid]);
        }

        // ── 4. ExecuteImmune cannot be culled ───────────────────────────
        [Fact]
        public void TryCull_ExecuteImmuneIsNoOp()
        {
            var (sys, store) = MakeSystem();
            int eid = MakeCullableEnemy(store, maxHp: 100f, thresholdPct: 0.10f);
            store.EnemyHealth[eid] = 5f;
            store.EnemyExecuteImmune[eid] = true;
            int tid = MakeCullingTower(store, damagePct: 0.05f);
            bool result = sys.TryCull(tid, eid, 50f);
            Assert.False(result);
            Assert.Equal(5f, store.EnemyHealth[eid]);
        }

        // ── 5. Invulnerable cannot be culled ────────────────────────────
        [Fact]
        public void TryCull_InvulnerableIsNoOp()
        {
            var (sys, store) = MakeSystem();
            int eid = MakeCullableEnemy(store, maxHp: 100f, thresholdPct: 0.10f);
            store.EnemyHealth[eid] = 5f;
            store.EnemyIsInvulnerable[eid] = true;
            int tid = MakeCullingTower(store, damagePct: 0.05f);
            bool result = sys.TryCull(tid, eid, 50f);
            Assert.False(result);
            Assert.Equal(5f, store.EnemyHealth[eid]);
        }

        // ── 6. Above-threshold HP does not fire cull ────────────────────
        [Fact]
        public void TryCull_AboveThresholdIsNoOp()
        {
            var (sys, store) = MakeSystem();
            int eid = MakeCullableEnemy(store, maxHp: 100f, thresholdPct: 0.10f);
            store.EnemyHealth[eid] = 20f; // 20% > 10% threshold
            int tid = MakeCullingTower(store, damagePct: 0.05f);
            bool result = sys.TryCull(tid, eid, 50f);
            Assert.False(result);
            Assert.Equal(20f, store.EnemyHealth[eid]);
        }

        // ── 7. At-threshold + sufficient damage fires cull ───────────────
        [Fact]
        public void TryCull_AtThresholdWithSufficientDamageFiresCull()
        {
            var (sys, store) = MakeSystem();
            int eid = MakeCullableEnemy(store, maxHp: 100f, thresholdPct: 0.10f);
            store.EnemyHealth[eid] = 10f; // exactly at threshold (10% of 100)
            int tid = MakeCullingTower(store, damagePct: 0.05f);
            // hitDamage=50 >= 100 * 0.05 = 5 → cull fires
            bool result = sys.TryCull(tid, eid, 50f);
            Assert.True(result);
            Assert.Equal(0f, store.EnemyHealth[eid]); // HP zeroed
        }

        // ── 8. Damage-gate prevents cull ────────────────────────────────
        [Fact]
        public void TryCull_DamageGateFailsNoCull()
        {
            var (sys, store) = MakeSystem();
            int eid = MakeCullableEnemy(store, maxHp: 100f, thresholdPct: 0.10f);
            store.EnemyHealth[eid] = 5f; // 5% HP
            int tid = MakeCullingTower(store, damagePct: 0.50f); // need 50% of MaxHP
            // hitDamage=10 < 100 * 0.50 = 50 → no cull
            bool result = sys.TryCull(tid, eid, 10f);
            Assert.False(result);
            Assert.Equal(5f, store.EnemyHealth[eid]);
        }

        // ── 9. Cull fires event, sets HP to 0, queues death, increments stacks ──
        [Fact]
        public void TryCull_FiresEventAndIncrementsStacks()
        {
            var (sys, store) = MakeSystem();
            int eid = MakeCullableEnemy(store, maxHp: 100f, thresholdPct: 0.10f);
            store.EnemyHealth[eid] = 5f;
            int tid = MakeCullingTower(store, damagePct: 0.05f);

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
            Assert.True(firedBonus > 0f);
            Assert.Equal(0f, store.EnemyHealth[eid]);
            Assert.Equal(1, store.PlayerCullingStacks[PlayerId]);

            // Resolve death so the enemy is no longer active
            store.ResolveEnemiesKilledThisFrame();
            Assert.False(store.EnemyActive[eid]);
        }

        // ── 10. Bonus gold scales with stacks ───────────────────────────
        [Fact]
        public void TryCull_BonusGoldScalesWithStacks()
        {
            var (sys, store) = MakeSystem(new CullingConfig
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
            int eid1 = MakeCullableEnemy(store, maxHp: 100f, thresholdPct: 0.10f);
            store.EnemyHealth[eid1] = 5f;
            int tid = MakeCullingTower(store, damagePct: 0.05f);
            sys.TryCull(tid, eid1, 50f);
            Assert.Equal(10f, bonus1);

            // Second cull: pre-call stacks=1 → bonus = 10 * (1 + 1*0.05) = 10.5
            int eid2 = MakeCullableEnemy(store, maxHp: 100f, thresholdPct: 0.10f);
            store.EnemyHealth[eid2] = 5f;
            sys.TryCull(tid, eid2, 50f);
            Assert.Equal(10.5f, bonus2);
        }

        // ── 11. Stacks clamped at MaxPlayerStacks ───────────────────────
        [Fact]
        public void TryCull_StacksClampedAtMax()
        {
            var (sys, store) = MakeSystem(new CullingConfig { MaxPlayerStacks = 3 });
            int tid = MakeCullingTower(store, damagePct: 0.05f);
            for (int i = 0; i < 5; i++)
            {
                int eid = MakeCullableEnemy(store, maxHp: 100f, thresholdPct: 0.10f);
                store.EnemyHealth[eid] = 5f;
                sys.TryCull(tid, eid, 50f);
            }
            Assert.Equal(3, store.PlayerCullingStacks[PlayerId]); // capped
        }

        // ── 12. Invalid inputs ─────────────────────────────────────────
        [Fact]
        public void TryCull_InvalidIdsNoOp()
        {
            var (sys, store) = MakeSystem();
            int eid = MakeCullableEnemy(store, maxHp: 100f, thresholdPct: 0.10f);
            store.EnemyHealth[eid] = 5f;
            int tid = MakeCullingTower(store, damagePct: 0.05f);

            Assert.False(sys.TryCull(-1, eid, 50f));
            Assert.False(sys.TryCull(tid, -1, 50f));
            Assert.False(sys.TryCull(ComponentStore.MAX_ENTITIES + 5, eid, 50f));
            Assert.False(sys.TryCull(tid, ComponentStore.MAX_ENTITIES + 5, 50f));
        }

        // ── 13. Dead enemy (HP<=0) no-op ───────────────────────────────
        [Fact]
        public void TryCull_DeadEnemyNoOp()
        {
            var (sys, store) = MakeSystem();
            int eid = MakeCullableEnemy(store, maxHp: 100f, thresholdPct: 0.10f);
            store.EnemyHealth[eid] = 0f; // already dead
            int tid = MakeCullingTower(store, damagePct: 0.05f);
            bool result = sys.TryCull(tid, eid, 50f);
            Assert.False(result);
        }

        // ── 14. Config Enabled=false short-circuits ─────────────────────
        [Fact]
        public void TryCull_ConfigDisabledIsNoOp()
        {
            var (sys, store) = MakeSystem(new CullingConfig { Enabled = false });
            int eid = MakeCullableEnemy(store, maxHp: 100f, thresholdPct: 0.10f);
            store.EnemyHealth[eid] = 5f;
            int tid = MakeCullingTower(store, damagePct: 0.05f);
            bool result = sys.TryCull(tid, eid, 50f);
            Assert.False(result);
            Assert.Equal(5f, store.EnemyHealth[eid]);
        }

        // ── 15. OnWaveStart resets per-player stacks ───────────────────
        [Fact]
        public void OnWaveStart_ResetsStacksToZero()
        {
            var (sys, store) = MakeSystem();
            int tid = MakeCullingTower(store, damagePct: 0.05f);
            for (int i = 0; i < 3; i++)
            {
                int eid = MakeCullableEnemy(store, maxHp: 100f, thresholdPct: 0.10f);
                store.EnemyHealth[eid] = 5f;
                sys.TryCull(tid, eid, 50f);
            }
            Assert.Equal(3, store.PlayerCullingStacks[PlayerId]);

            sys.OnWaveStart();
            Assert.Equal(0, store.PlayerCullingStacks[PlayerId]);
        }

        // ── 16. ComputeBonusGold: BaseBonusGold * (1 + stacks * pct) ────
        [Fact]
        public void ComputeBonusGold_FormulaCorrect()
        {
            var (sys, store) = MakeSystem(new CullingConfig
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
            var (sys, store) = MakeSystem(new CullingConfig { BaseBonusGold = 10f });
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
            var (sys, store) = MakeSystem();
            var newCfg = new CullingConfig { BaseBonusGold = 999f };
            sys.LoadConfig(newCfg);
            Assert.Equal(999f, sys.Config.BaseBonusGold);
        }

        // ── 20. GetPlayerStacks returns correct count ──────────────────
        [Fact]
        public void GetPlayerStacks_ReturnsCorrectCount()
        {
            var (sys, store) = MakeSystem();
            Assert.Equal(0, sys.GetPlayerStacks(PlayerId));
            store.PlayerCullingStacks[PlayerId] = 7;
            Assert.Equal(7, sys.GetPlayerStacks(PlayerId));
            Assert.Equal(0, sys.GetPlayerStacks(-1)); // invalid
            Assert.Equal(0, sys.GetPlayerStacks(MaxPlayers + 5)); // invalid
        }

        // ── 21. Update is a no-op (event-driven) ──────────────────────
        [Fact]
        public void Update_NoOp()
        {
            var (sys, store) = MakeSystem();
            int eid = MakeCullableEnemy(store, maxHp: 100f, thresholdPct: 0.10f);
            store.EnemyHealth[eid] = 5f;
            int tid = MakeCullingTower(store, damagePct: 0.05f);
            // Per-frame Update must not auto-cull; the per-hit hot path is TryCull.
            sys.Update(DeltaTime);
            Assert.Equal(5f, store.EnemyHealth[eid]); // HP unchanged
        }

        // ── 22. Default-fallback: per-enemy 0 → config default kicks in ─
        [Fact]
        public void TryCull_FallsBackToConfigDefaultThreshold()
        {
            var (sys, store) = MakeSystem(new CullingConfig
            {
                DefaultThresholdPct = 0.20f,
                DefaultDamagePct = 0.05f
            });
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 5f, 10, 1, "E");
            // EnemyCullingThresholdPct = 0 (default) → falls back to 0.20
            store.EnemyHealth[eid] = 15f; // 15% < 20% threshold
            int tid = MakeCullingTower(store, damagePct: 0f); // also 0, fall back to config 0.05
            bool result = sys.TryCull(tid, eid, 50f); // 50 >= 100*0.05 = 5
            Assert.True(result);
            Assert.Equal(0f, store.EnemyHealth[eid]);
        }
    }
}
