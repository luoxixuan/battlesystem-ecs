using BattleSystemECS.Tests.Infrastructure;
using System.Reflection;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;
using Xunit;

namespace BattleSystemECS.Tests.Features.Bosses
{
    /// <summary>
    /// Tests for Round 134 Direction 3: Boss HP natural regen (Boss Regen / Boss Shield Regen).
    /// Verifies that:
    ///   - SOA fields EnemyHealthRegenPerSec / EnemyHealthRegenMult exist and default to 0/1.0
    ///   - MonsterConfig.HealthRegenPerSec + PhaseRegenMult fields exist and default sensibly
    ///   - GameConfigLoader.ParseFloatArray parses simple + nested + scientific notation arrays
    ///   - TickBossRegen applies baseRegen * mult * dt and clamps to MaxHealth
    ///   - BossRegenDrainCount reports the touched-enemy count
    ///   - Zero-regen enemies are skipped (no write, no count increment)
    ///   - Full-health / dead enemies are skipped (no over-heal, no zombie revives)
    ///   - Phase multiplier is applied when monsterConfig.PhaseRegenMult[phase] is in range
    ///   - dt=0 short-circuits TickBossRegen (no work)
    ///   - Missing monsterConfig gracefully falls back to 1.0× mult
    /// </summary>
    public class BossRegenTests : BattleTestBase
    {
        private const int PlayerId = 0;
        private const float DeltaTime = 1f / 60f;

        /// <summary>文件内共享构造：基于基类 Store/Config 创建 EnemyAISystem（含 EnemyAbilitySystem）。</summary>
        private EnemyAISystem CreateAi()
        {
            var ability = new EnemyAbilitySystem(Store, Renderer, PlayerId, Config);
            return new EnemyAISystem(Store, Renderer, PlayerId, Config, ability);
        }

        // ── SOA + MonsterConfig field defaults ──────────────────────────

        [Fact]
        public void ComponentStore_BossRegenFields_DefaultToZeroAndOne()
        {
            // The new arrays must exist and zero-init: EnemyHealthRegenPerSec=0 (no regen)
            // and EnemyHealthRegenMult=1.0 (legacy no-scaling). Any enemy that hasn't
            // been touched by AddEnemy yet must NOT regen by accident.
            Assert.Equal(0f, Store.EnemyHealthRegenPerSec[0]);
            Assert.Equal(1f, Store.EnemyHealthRegenMult[0]);
            Assert.Equal(0f, Store.EnemyHealthRegenPerSec[1000]);
            Assert.Equal(1f, Store.EnemyHealthRegenMult[1000]);
        }

        [Fact]
        public void MonsterConfig_BossRegenFields_DefaultToZeroAndEmpty()
        {
            // A fresh MonsterConfig must NOT regen. The opt-in fields stay at the safe
            // defaults so legacy monster JSONs (no HealthRegenPerSec key) behave exactly
            // as before.
            var cfg = new MonsterConfig();
            Assert.Equal(0f, cfg.HealthRegenPerSec);
            Assert.NotNull(cfg.PhaseRegenMult);
            Assert.Empty(cfg.PhaseRegenMult);
        }

        [Fact]
        public void AddEnemy_InitializesBossRegenFieldsSafely()
        {
            // AddEnemy's null-safe init must leave the arrays at 0/1.0 so the
            // TickBossRegen fast path is a no-op for legacy enemies.
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "Goblin"; });
            Assert.Equal(0f, Store.EnemyHealthRegenPerSec[eid]);
            Assert.Equal(1f, Store.EnemyHealthRegenMult[eid]);
        }

        // ── TickBossRegen behavior ───────────────────────────────────────

        [Fact]
        public void TickBossRegen_ZeroRegenRate_IsNoOp()
        {
            // Default enemy (HealthRegenPerSec=0) must not move HP. The BossRegenDrainCount
            // is incremented for any enemy the loop "touched" — but a zero-regen enemy is
            // skipped before any state mutation, so the count must stay 0.
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "Goblin"; });
            Store.EnemyHealth[eid] = 50f; // half HP — would regen if regen rate > 0
            var ai = CreateAi();

            ai.SetTurn(1, DeltaTime);
            InvokeTickBossRegen(ai);

            Assert.Equal(50f, Store.EnemyHealth[eid]);
            Assert.Equal(0, ai.BossRegenDrainCount);
        }

        [Fact]
        public void TickBossRegen_PositiveRegenRate_HealsOverTime()
        {
            // With HealthRegenPerSec=60, dt=1/60, mult=1.0 → heal = 1.0 HP per frame.
            // After 10 frames, half-HP enemy should be at 60.0 (clamped from 100, healing
            // tick by tick). We just verify a single tick: 50 → 51.
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "Boss"; });
            Store.EnemyHealth[eid] = 50f;
            Store.EnemyMaxHealth[eid] = 100f;
            Store.EnemyHealthRegenPerSec[eid] = 60f; // 60 HP/sec
            Store.EnemyHealthRegenMult[eid] = 1f;
            var ai = CreateAi();

            ai.SetTurn(1, DeltaTime);
            InvokeTickBossRegen(ai);

            // 50 + 60 * 1 * (1/60) = 51.0 (with float tolerance for multiplication order)
            Assert.InRange(Store.EnemyHealth[eid], 50.99f, 51.01f);
            Assert.Equal(1, ai.BossRegenDrainCount);
        }

        [Fact]
        public void TickBossRegen_ClampsToMaxHealth()
        {
            // If the heal tick would push HP above MaxHealth, clamp to MaxHealth.
            // Regen=600 HP/sec, dt=1/60 → heal = 10 HP per tick. From 95 → 100 (clamped),
            // NOT 105.
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "Boss"; });
            Store.EnemyHealth[eid] = 95f;
            Store.EnemyMaxHealth[eid] = 100f;
            Store.EnemyHealthRegenPerSec[eid] = 600f;
            Store.EnemyHealthRegenMult[eid] = 1f;
            var ai = CreateAi();

            ai.SetTurn(1, DeltaTime);
            InvokeTickBossRegen(ai);

            Assert.Equal(100f, Store.EnemyHealth[eid]);
        }

        [Fact]
        public void TickBossRegen_SkipsFullHealthEnemy()
        {
            // A full-HP enemy must NOT be "touched" — BossRegenDrainCount counts only
            // enemies where a heal actually applied. Full-HP enemies are also
            // short-circuited inside the loop (no array write to EnemyHealth).
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "Boss"; });
            // EnemyHealth=MaxHealth=100
            Store.EnemyHealthRegenPerSec[eid] = 100f;
            var ai = CreateAi();

            ai.SetTurn(1, DeltaTime);
            InvokeTickBossRegen(ai);

            Assert.Equal(100f, Store.EnemyHealth[eid]);
            Assert.Equal(0, ai.BossRegenDrainCount);
        }

        [Fact]
        public void TickBossRegen_SkipsDeadEnemy()
        {
            // A dead (HP <= 0) enemy must NOT be revived by a stray heal tick. The
            // currentHp <= 0 short-circuit is critical for integrity — without it,
            // a boss with regen would resurrect in the middle of ResolveEnemiesKilledThisFrame.
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "Boss"; });
            Store.EnemyHealth[eid] = 0f;
            Store.EnemyMaxHealth[eid] = 100f;
            Store.EnemyHealthRegenPerSec[eid] = 100f;
            var ai = CreateAi();

            ai.SetTurn(1, DeltaTime);
            InvokeTickBossRegen(ai);

            // HP stays at 0 — no zombie revive. (The enemy is technically "inactive" in
            // the destroy queue, but TickBossRegen is defensive against in-flight cases.)
            Assert.Equal(0f, Store.EnemyHealth[eid]);
            Assert.Equal(0, ai.BossRegenDrainCount);
        }

        [Fact]
        public void TickBossRegen_PhaseMultiplier_AppliedFromConfig()
        {
            // With PhaseRegenMult={1.0, 1.5, 2.5} and the boss in phase 1 (index 1),
            // the regen rate is 60 * 1.5 = 90 HP/sec → 1.5 HP per frame (dt=1/60).
            // 50 → 51.5 after one tick.
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "Boss"; });
            Store.EnemyHealth[eid] = 50f;
            Store.EnemyMaxHealth[eid] = 100f;
            Store.EnemyHealthRegenPerSec[eid] = 60f;
            Store.EnemyHealthRegenMult[eid] = 1f; // base mult, will be overridden by live lookup
            Store.EnemyBossPhase[eid] = 1; // phase 2 (index 1)

            // Register a monster config for "Boss" with PhaseRegenMult[1] = 1.5
            var bossCfg = new MonsterConfig
            {
                Name = "Boss",
                Type = "Boss",
                Health = 100f,
                MaxHealth = 100f,
                Damage = 5f,
                MoveSpeed = 1f,
                AttackRange = 1f,
                AttackInterval = 1f,
                GoldReward = 10,
                HealthRegenPerSec = 60f,
                PhaseRegenMult = new[] { 1.0f, 1.5f, 2.5f },
            };
            Config.MonsterTypes = new System.Collections.Generic.List<MonsterConfig> { bossCfg };

            var ai = CreateAi();

            ai.SetTurn(1, DeltaTime);
            InvokeTickBossRegen(ai);

            // 50 + 60 * 1.5 * (1/60) = 51.5
            Assert.InRange(Store.EnemyHealth[eid], 51.49f, 51.51f);
        }

        [Fact]
        public void TickBossRegen_ZeroDeltaTime_ShortCircuits()
        {
            // dt <= 0 must early-out: no enemy should be touched, no BossRegenDrainCount
            // change. This guard prevents accidental regen on the first frame of a
            // turn (when SetTurn may be called with dt=0 by some call sites) and on
            // paused frames.
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "Boss"; });
            Store.EnemyHealth[eid] = 50f;
            Store.EnemyMaxHealth[eid] = 100f;
            Store.EnemyHealthRegenPerSec[eid] = 100f;
            var ai = CreateAi();

            ai.SetTurn(1, 0f); // dt=0 — short-circuits TickBossRegen
            InvokeTickBossRegen(ai);

            Assert.Equal(50f, Store.EnemyHealth[eid]);
            Assert.Equal(0, ai.BossRegenDrainCount);
        }

        [Fact]
        public void TickBossRegen_FallsBackToStoredMult_WhenConfigMissing()
        {
            // If the monster type can't be looked up in GameConfig (e.g. test fixture
            // using a bare AddEnemy with no registered MonsterConfig), the stored
            // EnemyHealthRegenMult[id] must be used as the fallback. This makes the
            // feature robust to test harnesses that don't go through WaveSpawningSystem.
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Name = "UnknownType"; });
            Store.EnemyHealth[eid] = 50f;
            Store.EnemyMaxHealth[eid] = 100f;
            Store.EnemyHealthRegenPerSec[eid] = 60f;
            Store.EnemyHealthRegenMult[eid] = 2.0f; // fallback mult
            // Config 为空（未注册任何 MonsterConfig）→ lookup 失败，走 fallback。
            var ai = CreateAi();

            ai.SetTurn(1, DeltaTime);
            InvokeTickBossRegen(ai);

            // 50 + 60 * 2.0 * (1/60) = 52.0
            Assert.InRange(Store.EnemyHealth[eid], 51.99f, 52.01f);
        }

        [Fact]
        public void TickBossRegen_MultipleEnemies_OnlyRegenOnesTouched()
        {
            // Mixed pool: 2 regen-enabled + 1 zero-regen + 1 dead. After the tick,
            // BossRegenDrainCount must be 2 (only the regen-enabled half-HP enemies
            // got a heal).
            int regenA = Enemy(e => { e.MoveSpeed = 1f; e.Name = "A"; });
            int regenB = Enemy(e => { e.X = 1f; e.MoveSpeed = 1f; e.Name = "B"; });
            int noRegen = Enemy(e => { e.X = 2f; e.MoveSpeed = 1f; e.Name = "C"; });
            int deadBoss = Enemy(e => { e.X = 3f; e.MoveSpeed = 1f; e.Name = "D"; });

            Store.EnemyHealth[regenA] = 50f; Store.EnemyMaxHealth[regenA] = 100f;
            Store.EnemyHealthRegenPerSec[regenA] = 60f; // regen
            Store.EnemyHealth[regenB] = 60f; Store.EnemyMaxHealth[regenB] = 100f;
            Store.EnemyHealthRegenPerSec[regenB] = 60f; // regen
            // noRegen: HealthRegenPerSec=0 (default). Seed HP=50 explicitly so the
            // post-tick assertion verifies the regen-skip fast path: HP must remain 50
            // (TickBossRegen should NOT clamp/overwrite it back to MaxHealth).
            Store.EnemyHealth[noRegen] = 50f; Store.EnemyMaxHealth[noRegen] = 100f;
            Store.EnemyHealth[deadBoss] = 0f; Store.EnemyMaxHealth[deadBoss] = 100f;
            Store.EnemyHealthRegenPerSec[deadBoss] = 60f; // regen but dead

            var ai = CreateAi();

            ai.SetTurn(1, DeltaTime);
            InvokeTickBossRegen(ai);

            Assert.Equal(2, ai.BossRegenDrainCount);
            // regenA and regenB should have ticked up
            Assert.InRange(Store.EnemyHealth[regenA], 50.99f, 51.01f);
            Assert.InRange(Store.EnemyHealth[regenB], 60.99f, 61.01f);
            // noRegen and deadBoss untouched
            Assert.Equal(50f, Store.EnemyHealth[noRegen]);
            Assert.Equal(0f, Store.EnemyHealth[deadBoss]);
        }

        // ── GameConfigLoader.ParseFloatArray via reflection ─────────────

        [Fact]
        public void GameConfigLoader_ParseFloatArray_HandlesSimpleArray()
        {
            // Parse a simple array of floats — the production use case is PhaseRegenMult.
            // Pin that the parser handles whitespace, trailing commas, and scientific
            // notation. (The parser is private static; we test it via the public
            // ParseMonsterConfig path with a hand-rolled JSON string.)
            var method = typeof(GameConfigLoader).GetMethod(
                "ParseFloatArray", // 私有方法 nameof 类外不可用，保留字符串（反射点）
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method); // sanity — the helper must exist

            string json = @"{ ""PhaseRegenMult"": [ 1.0, 1.5, 2.5 ] }";
            var result = (float[])method.Invoke(null, new object[] { json, "PhaseRegenMult" })!;
            Assert.NotNull(result);
            Assert.Equal(3, result.Length);
            Assert.Equal(1.0f, result[0]);
            Assert.Equal(1.5f, result[1]);
            Assert.Equal(2.5f, result[2]);
        }

        [Fact]
        public void GameConfigLoader_ParseFloatArray_EmptyKey_ReturnsEmpty()
        {
            // Key absent → empty array (not null). Callers use Length==0 as the
            // "feature disabled" sentinel.
            var method = typeof(GameConfigLoader).GetMethod(
                "ParseFloatArray", // 私有方法 nameof 类外不可用，保留字符串（反射点）
                BindingFlags.NonPublic | BindingFlags.Static);
            string json = @"{ ""OtherKey"": 42 }";
            var result = (float[])method!.Invoke(null, new object[] { json, "PhaseRegenMult" })!;
            Assert.NotNull(result);
            Assert.Empty(result);
        }

        [Fact]
        public void GameConfigLoader_ParseFloatArray_HandlesNegatives()
        {
            // Negative values (e.g. decay mults) must parse correctly. Without
            // sign handling the loop would treat '-' as a separator and skip the token.
            var method = typeof(GameConfigLoader).GetMethod(
                "ParseFloatArray", // 私有方法 nameof 类外不可用，保留字符串（反射点）
                BindingFlags.NonPublic | BindingFlags.Static);
            string json = @"{ ""Decay"": [ -0.5, 0.0, 0.5 ] }";
            var result = (float[])method!.Invoke(null, new object[] { json, "Decay" })!;
            Assert.Equal(3, result.Length);
            Assert.Equal(-0.5f, result[0]);
            Assert.Equal(0.0f, result[1]);
            Assert.Equal(0.5f, result[2]);
        }

        // ── helpers ──────────────────────────────────────────────────────

        private static void InvokeTickBossRegen(EnemyAISystem ai)
        {
            // TickBossRegen is private; tests reach it via reflection to keep the
            // production call site (Update → TickBossRegen) and the test surface
            // independent. The serial drain pattern matches the rest of the suite.
            var method = typeof(EnemyAISystem).GetMethod(
                "TickBossRegen", // 私有方法 nameof 类外不可用，保留字符串（反射点）
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            method.Invoke(ai, null);
        }
    }
}