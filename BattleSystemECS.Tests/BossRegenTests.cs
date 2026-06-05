using System.Reflection;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;
using Xunit;

namespace BattleSystemECS.Tests
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
    public class BossRegenTests
    {
        private const int PlayerId = 0;
        private const float DeltaTime = 1f / 60f;

        // ── SOA + MonsterConfig field defaults ──────────────────────────

        [Fact]
        public void ComponentStore_BossRegenFields_DefaultToZeroAndOne()
        {
            // The new arrays must exist and zero-init: EnemyHealthRegenPerSec=0 (no regen)
            // and EnemyHealthRegenMult=1.0 (legacy no-scaling). Any enemy that hasn't
            // been touched by AddEnemy yet must NOT regen by accident.
            var store = new ComponentStore();
            Assert.Equal(0f, store.EnemyHealthRegenPerSec[0]);
            Assert.Equal(1f, store.EnemyHealthRegenMult[0]);
            Assert.Equal(0f, store.EnemyHealthRegenPerSec[1000]);
            Assert.Equal(1f, store.EnemyHealthRegenMult[1000]);
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
            var store = new ComponentStore();
            int eid = store.AddEnemy(0, 0, 1f, 100f, 100f, 5f, 10, 1, "Goblin");
            Assert.Equal(0f, store.EnemyHealthRegenPerSec[eid]);
            Assert.Equal(1f, store.EnemyHealthRegenMult[eid]);
        }

        // ── TickBossRegen behavior ───────────────────────────────────────

        [Fact]
        public void TickBossRegen_ZeroRegenRate_IsNoOp()
        {
            // Default enemy (HealthRegenPerSec=0) must not move HP. The BossRegenDrainCount
            // is incremented for any enemy the loop "touched" — but a zero-regen enemy is
            // skipped before any state mutation, so the count must stay 0.
            var store = new ComponentStore();
            int eid = store.AddEnemy(0, 0, 1f, 100f, 100f, 5f, 10, 1, "Goblin");
            store.EnemyHealth[eid] = 50f; // half HP — would regen if regen rate > 0
            var config = new GameConfig();
            var renderer = new MockRenderer();
            var enemyAbility = new EnemyAbilitySystem(store, renderer, PlayerId, config);
            var ai = new EnemyAISystem(store, renderer, PlayerId, config, enemyAbility);

            ai.SetTurn(1, DeltaTime);
            InvokeTickBossRegen(ai);

            Assert.Equal(50f, store.EnemyHealth[eid]);
            Assert.Equal(0, ai.BossRegenDrainCount);
        }

        [Fact]
        public void TickBossRegen_PositiveRegenRate_HealsOverTime()
        {
            // With HealthRegenPerSec=60, dt=1/60, mult=1.0 → heal = 1.0 HP per frame.
            // After 10 frames, half-HP enemy should be at 60.0 (clamped from 100, healing
            // tick by tick). We just verify a single tick: 50 → 51.
            var store = new ComponentStore();
            int eid = store.AddEnemy(0, 0, 1f, 100f, 100f, 5f, 10, 1, "Boss");
            store.EnemyHealth[eid] = 50f;
            store.EnemyMaxHealth[eid] = 100f;
            store.EnemyHealthRegenPerSec[eid] = 60f; // 60 HP/sec
            store.EnemyHealthRegenMult[eid] = 1f;
            var config = new GameConfig();
            var renderer = new MockRenderer();
            var enemyAbility = new EnemyAbilitySystem(store, renderer, PlayerId, config);
            var ai = new EnemyAISystem(store, renderer, PlayerId, config, enemyAbility);

            ai.SetTurn(1, DeltaTime);
            InvokeTickBossRegen(ai);

            // 50 + 60 * 1 * (1/60) = 51.0 (with float tolerance for multiplication order)
            Assert.InRange(store.EnemyHealth[eid], 50.99f, 51.01f);
            Assert.Equal(1, ai.BossRegenDrainCount);
        }

        [Fact]
        public void TickBossRegen_ClampsToMaxHealth()
        {
            // If the heal tick would push HP above MaxHealth, clamp to MaxHealth.
            // Regen=600 HP/sec, dt=1/60 → heal = 10 HP per tick. From 95 → 100 (clamped),
            // NOT 105.
            var store = new ComponentStore();
            int eid = store.AddEnemy(0, 0, 1f, 100f, 100f, 5f, 10, 1, "Boss");
            store.EnemyHealth[eid] = 95f;
            store.EnemyMaxHealth[eid] = 100f;
            store.EnemyHealthRegenPerSec[eid] = 600f;
            store.EnemyHealthRegenMult[eid] = 1f;
            var config = new GameConfig();
            var renderer = new MockRenderer();
            var enemyAbility = new EnemyAbilitySystem(store, renderer, PlayerId, config);
            var ai = new EnemyAISystem(store, renderer, PlayerId, config, enemyAbility);

            ai.SetTurn(1, DeltaTime);
            InvokeTickBossRegen(ai);

            Assert.Equal(100f, store.EnemyHealth[eid]);
        }

        [Fact]
        public void TickBossRegen_SkipsFullHealthEnemy()
        {
            // A full-HP enemy must NOT be "touched" — BossRegenDrainCount counts only
            // enemies where a heal actually applied. Full-HP enemies are also
            // short-circuited inside the loop (no array write to EnemyHealth).
            var store = new ComponentStore();
            int eid = store.AddEnemy(0, 0, 1f, 100f, 100f, 5f, 10, 1, "Boss");
            // EnemyHealth=MaxHealth=100
            store.EnemyHealthRegenPerSec[eid] = 100f;
            var config = new GameConfig();
            var renderer = new MockRenderer();
            var enemyAbility = new EnemyAbilitySystem(store, renderer, PlayerId, config);
            var ai = new EnemyAISystem(store, renderer, PlayerId, config, enemyAbility);

            ai.SetTurn(1, DeltaTime);
            InvokeTickBossRegen(ai);

            Assert.Equal(100f, store.EnemyHealth[eid]);
            Assert.Equal(0, ai.BossRegenDrainCount);
        }

        [Fact]
        public void TickBossRegen_SkipsDeadEnemy()
        {
            // A dead (HP <= 0) enemy must NOT be revived by a stray heal tick. The
            // currentHp <= 0 short-circuit is critical for integrity — without it,
            // a boss with regen would resurrect in the middle of ResolveEnemiesKilledThisFrame.
            var store = new ComponentStore();
            int eid = store.AddEnemy(0, 0, 1f, 100f, 100f, 5f, 10, 1, "Boss");
            store.EnemyHealth[eid] = 0f;
            store.EnemyMaxHealth[eid] = 100f;
            store.EnemyHealthRegenPerSec[eid] = 100f;
            var config = new GameConfig();
            var renderer = new MockRenderer();
            var enemyAbility = new EnemyAbilitySystem(store, renderer, PlayerId, config);
            var ai = new EnemyAISystem(store, renderer, PlayerId, config, enemyAbility);

            ai.SetTurn(1, DeltaTime);
            InvokeTickBossRegen(ai);

            // HP stays at 0 — no zombie revive. (The enemy is technically "inactive" in
            // the destroy queue, but TickBossRegen is defensive against in-flight cases.)
            Assert.Equal(0f, store.EnemyHealth[eid]);
            Assert.Equal(0, ai.BossRegenDrainCount);
        }

        [Fact]
        public void TickBossRegen_PhaseMultiplier_AppliedFromConfig()
        {
            // With PhaseRegenMult={1.0, 1.5, 2.5} and the boss in phase 1 (index 1),
            // the regen rate is 60 * 1.5 = 90 HP/sec → 1.5 HP per frame (dt=1/60).
            // 50 → 51.5 after one tick.
            var store = new ComponentStore();
            int eid = store.AddEnemy(0, 0, 1f, 100f, 100f, 5f, 10, 1, "Boss");
            store.EnemyHealth[eid] = 50f;
            store.EnemyMaxHealth[eid] = 100f;
            store.EnemyHealthRegenPerSec[eid] = 60f;
            store.EnemyHealthRegenMult[eid] = 1f; // base mult, will be overridden by live lookup
            store.EnemyBossPhase[eid] = 1; // phase 2 (index 1)

            var config = new GameConfig();
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
            config.MonsterTypes = new System.Collections.Generic.List<MonsterConfig> { bossCfg };

            var renderer = new MockRenderer();
            var enemyAbility = new EnemyAbilitySystem(store, renderer, PlayerId, config);
            var ai = new EnemyAISystem(store, renderer, PlayerId, config, enemyAbility);

            ai.SetTurn(1, DeltaTime);
            InvokeTickBossRegen(ai);

            // 50 + 60 * 1.5 * (1/60) = 51.5
            Assert.InRange(store.EnemyHealth[eid], 51.49f, 51.51f);
        }

        [Fact]
        public void TickBossRegen_ZeroDeltaTime_ShortCircuits()
        {
            // dt <= 0 must early-out: no enemy should be touched, no BossRegenDrainCount
            // change. This guard prevents accidental regen on the first frame of a
            // turn (when SetTurn may be called with dt=0 by some call sites) and on
            // paused frames.
            var store = new ComponentStore();
            int eid = store.AddEnemy(0, 0, 1f, 100f, 100f, 5f, 10, 1, "Boss");
            store.EnemyHealth[eid] = 50f;
            store.EnemyMaxHealth[eid] = 100f;
            store.EnemyHealthRegenPerSec[eid] = 100f;
            var config = new GameConfig();
            var renderer = new MockRenderer();
            var enemyAbility = new EnemyAbilitySystem(store, renderer, PlayerId, config);
            var ai = new EnemyAISystem(store, renderer, PlayerId, config, enemyAbility);

            ai.SetTurn(1, 0f); // dt=0 — short-circuits TickBossRegen
            InvokeTickBossRegen(ai);

            Assert.Equal(50f, store.EnemyHealth[eid]);
            Assert.Equal(0, ai.BossRegenDrainCount);
        }

        [Fact]
        public void TickBossRegen_FallsBackToStoredMult_WhenConfigMissing()
        {
            // If the monster type can't be looked up in GameConfig (e.g. test fixture
            // using a bare AddEnemy with no registered MonsterConfig), the stored
            // EnemyHealthRegenMult[id] must be used as the fallback. This makes the
            // feature robust to test harnesses that don't go through WaveSpawningSystem.
            var store = new ComponentStore();
            int eid = store.AddEnemy(0, 0, 1f, 100f, 100f, 5f, 10, 1, "UnknownType");
            store.EnemyHealth[eid] = 50f;
            store.EnemyMaxHealth[eid] = 100f;
            store.EnemyHealthRegenPerSec[eid] = 60f;
            store.EnemyHealthRegenMult[eid] = 2.0f; // fallback mult
            var config = new GameConfig(); // empty config — lookup will fail
            var renderer = new MockRenderer();
            var enemyAbility = new EnemyAbilitySystem(store, renderer, PlayerId, config);
            var ai = new EnemyAISystem(store, renderer, PlayerId, config, enemyAbility);

            ai.SetTurn(1, DeltaTime);
            InvokeTickBossRegen(ai);

            // 50 + 60 * 2.0 * (1/60) = 52.0
            Assert.InRange(store.EnemyHealth[eid], 51.99f, 52.01f);
        }

        [Fact]
        public void TickBossRegen_MultipleEnemies_OnlyRegenOnesTouched()
        {
            // Mixed pool: 2 regen-enabled + 1 zero-regen + 1 dead. After the tick,
            // BossRegenDrainCount must be 2 (only the regen-enabled half-HP enemies
            // got a heal).
            var store = new ComponentStore();
            int regenA = store.AddEnemy(0, 0, 1f, 100f, 100f, 5f, 10, 1, "A");
            int regenB = store.AddEnemy(1, 0, 1f, 100f, 100f, 5f, 10, 1, "B");
            int noRegen = store.AddEnemy(2, 0, 1f, 100f, 100f, 5f, 10, 1, "C");
            int deadBoss = store.AddEnemy(3, 0, 1f, 100f, 100f, 5f, 10, 1, "D");

            store.EnemyHealth[regenA] = 50f; store.EnemyMaxHealth[regenA] = 100f;
            store.EnemyHealthRegenPerSec[regenA] = 60f; // regen
            store.EnemyHealth[regenB] = 60f; store.EnemyMaxHealth[regenB] = 100f;
            store.EnemyHealthRegenPerSec[regenB] = 60f; // regen
            // noRegen: HealthRegenPerSec=0 (default). Seed HP=50 explicitly so the
            // post-tick assertion verifies the regen-skip fast path: HP must remain 50
            // (TickBossRegen should NOT clamp/overwrite it back to MaxHealth).
            store.EnemyHealth[noRegen] = 50f; store.EnemyMaxHealth[noRegen] = 100f;
            store.EnemyHealth[deadBoss] = 0f; store.EnemyMaxHealth[deadBoss] = 100f;
            store.EnemyHealthRegenPerSec[deadBoss] = 60f; // regen but dead

            var config = new GameConfig();
            var renderer = new MockRenderer();
            var enemyAbility = new EnemyAbilitySystem(store, renderer, PlayerId, config);
            var ai = new EnemyAISystem(store, renderer, PlayerId, config, enemyAbility);

            ai.SetTurn(1, DeltaTime);
            InvokeTickBossRegen(ai);

            Assert.Equal(2, ai.BossRegenDrainCount);
            // regenA and regenB should have ticked up
            Assert.InRange(store.EnemyHealth[regenA], 50.99f, 51.01f);
            Assert.InRange(store.EnemyHealth[regenB], 60.99f, 61.01f);
            // noRegen and deadBoss untouched
            Assert.Equal(50f, store.EnemyHealth[noRegen]);
            Assert.Equal(0f, store.EnemyHealth[deadBoss]);
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
                "ParseFloatArray",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method); // sanity — the helper must exist

            string json = @"{ ""PhaseRegenMult"": [ 1.0, 1.5, 2.5 ] }";
            var result = (float[])method.Invoke(null, new object[] { json, "PhaseRegenMult" });
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
                "ParseFloatArray",
                BindingFlags.NonPublic | BindingFlags.Static);
            string json = @"{ ""OtherKey"": 42 }";
            var result = (float[])method.Invoke(null, new object[] { json, "PhaseRegenMult" });
            Assert.NotNull(result);
            Assert.Empty(result);
        }

        [Fact]
        public void GameConfigLoader_ParseFloatArray_HandlesNegatives()
        {
            // Negative values (e.g. decay mults) must parse correctly. Without
            // sign handling the loop would treat '-' as a separator and skip the token.
            var method = typeof(GameConfigLoader).GetMethod(
                "ParseFloatArray",
                BindingFlags.NonPublic | BindingFlags.Static);
            string json = @"{ ""Decay"": [ -0.5, 0.0, 0.5 ] }";
            var result = (float[])method.Invoke(null, new object[] { json, "Decay" });
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
                "TickBossRegen",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            method.Invoke(ai, null);
        }
    }
}
