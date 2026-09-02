using BattleSystemECS.Tests.Infrastructure;
using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Features.Buffs
{
    /// <summary>
    /// Tests for Round 178+ Direction 5 — Tide / Crest System. Wave-indexed
    /// periodic buffs ("Crest of Fury" / "Tide of Healing" / "Crest of
    /// Bounty" / etc.) that apply multiplicative / additive bonuses to
    /// enemies or players during the matching wave.
    ///
    /// WaveSpawningSystem defaults currentWave=1 on construction, so we
    /// drive OnWaveStart with a crest roster that includes wave 1 in its
    /// TriggerWaves to exercise the positive path.
    ///
    /// Invariants verified:
    ///  1. Default state: per-enemy / per-player crest cache fields at fast-path
    ///     defaults (1f for mults, 0f for regen, empty id).
    ///  2. AddEnemy / AddPlayer reset crest fields to defaults.
    ///  3. CrestConfig defaults: Enabled=true, Crests=empty (sentinel fast path).
    ///  4. CrestDef defaults: 1f for mults, 0f for regen, empty TriggerWaves.
    ///  5. OnWaveStart with disabled config: no-op (defaults preserved).
    ///  6. OnWaveStart with empty roster: no-op.
    ///  7. OnWaveStart with non-matching wave: no-op (defaults preserved).
    ///  8. OnWaveStart with matching "enemy" scope: stamps EnemyCrestDamageMult /
    ///     EnemyCrestRegenPerSec, leaves player fields at defaults.
    ///  9. OnWaveStart with matching "player" scope: stamps PlayerCrestDamageMult /
    ///     PlayerCrestGoldMult, leaves enemy fields at defaults.
    /// 10. OnWaveStart with "both" scope: stamps both enemy and player fields.
    /// 11. Multiple crests triggering same wave: damage mults compose
    ///     multiplicatively, regen stacks additively.
    /// 12. OnWaveComplete: force-reset all per-enemy / per-player caches.
    /// 13. Update() is a no-op (event-driven system).
    /// 14. SubscribeToWaveEvents is idempotent.
    /// </summary>
    public class CrestSystemTests : BattleTestBase
    {
        private const int PlayerId = 0;

        private (CrestSystem sys, WaveSpawningSystem wave) MakeSystem(
            bool enabled = true,
            CrestDef[]? crests = null)
        {
            Config.Crest = new CrestConfig
            {
                Enabled = enabled,
                Crests = crests ?? Array.Empty<CrestDef>()
            };
            var wave = new WaveSpawningSystem(Store, Renderer, Config);
            var sys = new CrestSystem(Store, Config);
            wave.OnWaveStart += () => sys.HandleWaveStart(wave.GetCurrentWave());
            wave.OnWaveComplete += sys.HandleWaveComplete;
            return (sys, wave);
        }

        /// <summary>
        /// Fire the WaveSpawningSystem OnWaveStart event from a test. The
        /// event is declared with `event` so external code can't call it
        /// directly — we use GetInvocationList() to invoke the registered
        /// handlers.
        /// </summary>
        private static void FireOnWaveStart(WaveSpawningSystem wave)
        {
            var d = GetEventDelegate(wave, "OnWaveStart") as Action;
            d?.Invoke();
        }

        private static void FireOnWaveComplete(WaveSpawningSystem wave)
        {
            var d = GetEventDelegate(wave, "OnWaveComplete") as Action;
            d?.Invoke();
        }

        private static Delegate? GetEventDelegate(object source, string eventName)
        {
            var field = source.GetType().GetField(eventName,
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public);
            return field?.GetValue(source) as Delegate;
        }

        // ─── Default state ──────────────────────────────────────────────

        [Fact]
        public void DefaultState_NewComponentStore_EnemyCrestFieldsAtFastPath()
        {
            // 1f damage mult = no buff (fast path)
            Assert.Equal(1f, Store.EnemyCrestDamageMult[0]);
            Assert.Equal(1f, Store.EnemyCrestDamageMult[100]);
            // 0f regen = no regen (fast path)
            Assert.Equal(0f, Store.EnemyCrestRegenPerSec[0]);
            Assert.Equal(0f, Store.EnemyCrestRegenPerSec[100]);
        }

        [Fact]
        public void DefaultState_NewComponentStore_PlayerCrestFieldsAtFastPath()
        {
            Assert.Equal(1f, Store.PlayerCrestDamageMult[PlayerId]);
            Assert.Equal(1f, Store.PlayerCrestGoldMult[PlayerId]);
            Assert.True(string.IsNullOrEmpty(Store.PlayerCrestActiveId[PlayerId]));
        }

        [Fact]
        public void CrestConfig_DefaultEnabled_True_DefaultCrests_Empty()
        {
            var cfg = new CrestConfig();
            Assert.True(cfg.Enabled, "CrestConfig should be Enabled by default");
            Assert.NotNull(cfg.Crests);
            Assert.Empty(cfg.Crests);
        }

        [Fact]
        public void CrestDef_DefaultsAreFastPathSafe()
        {
            var def = new CrestDef();
            Assert.Equal("", def.Id);
            Assert.Equal("", def.Name);
            Assert.NotNull(def.TriggerWaves);
            Assert.Empty(def.TriggerWaves);
            Assert.Equal("both", def.TargetScope);
            Assert.Equal(1f, def.EnemyDamageMult);
            Assert.Equal(0f, def.EnemyRegenPerSec);
            Assert.Equal(1f, def.PlayerGoldMult);
            Assert.Equal(1f, def.PlayerDamageMult);
        }

        [Fact]
        public void AddEnemy_ResetsCrestCacheToDefaults()
        {
            // Pre-corrupt
            Store.EnemyCrestDamageMult[0] = 5f;
            Store.EnemyCrestRegenPerSec[0] = 99f;
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 100f, 10f, 5, 1);
            Assert.Equal(1f, Store.EnemyCrestDamageMult[eid]);
            Assert.Equal(0f, Store.EnemyCrestRegenPerSec[eid]);
        }

        [Fact]
        public void AddPlayer_ResetsCrestCacheToDefaults()
        {
            Store.PlayerCrestDamageMult[PlayerId] = 5f;
            Store.PlayerCrestGoldMult[PlayerId] = 5f;
            Store.PlayerCrestActiveId[PlayerId] = "garbage";
            Store.AddPlayer(PlayerId, 5f, 1f, 100f, 1);
            Assert.Equal(1f, Store.PlayerCrestDamageMult[PlayerId]);
            Assert.Equal(1f, Store.PlayerCrestGoldMult[PlayerId]);
            Assert.True(string.IsNullOrEmpty(Store.PlayerCrestActiveId[PlayerId]));
        }

        // ─── Disabled config / empty roster — OnWaveStart no-op ─────────

        [Fact]
        public void OnWaveStart_DisabledConfig_NoOp()
        {
            var (sys, wave) = MakeSystem(enabled: false, crests: new[]
            {
                new CrestDef { Id = "CrestOfFury", TriggerWaves = new[] { 1 }, TargetScope = "enemy", EnemyDamageMult = 1.5f }
            });
            int eid = Enemy();
            Player();
            FireOnWaveStart(wave);
            Assert.Equal(1f, Store.EnemyCrestDamageMult[eid]);
            Assert.Equal(0f, Store.EnemyCrestRegenPerSec[eid]);
        }

        [Fact]
        public void OnWaveStart_EmptyRoster_NoOp()
        {
            var (sys, wave) = MakeSystem(enabled: true, crests: Array.Empty<CrestDef>());
            int eid = Enemy();
            FireOnWaveStart(wave);
            Assert.Equal(1f, Store.EnemyCrestDamageMult[eid]);
            Assert.Equal(0f, Store.EnemyCrestRegenPerSec[eid]);
        }

        // ─── Trigger match + scope routing (currentWave defaults to 1) ──

        [Fact]
        public void OnWaveStart_EnemyScope_StampsEnemyLeavesPlayer()
        {
            var (sys, wave) = MakeSystem(crests: new[]
            {
                new CrestDef { Id = "CrestOfFury", TriggerWaves = new[] { 1 }, TargetScope = "enemy", EnemyDamageMult = 1.20f, EnemyRegenPerSec = 3f }
            });
            int eid = Enemy();
            Player();
            FireOnWaveStart(wave);
            // Enemy fields stamped
            Assert.Equal(1.20f, Store.EnemyCrestDamageMult[eid]);
            Assert.Equal(3f, Store.EnemyCrestRegenPerSec[eid]);
            // Player fields untouched (1f fast path)
            Assert.Equal(1f, Store.PlayerCrestDamageMult[PlayerId]);
            Assert.Equal(1f, Store.PlayerCrestGoldMult[PlayerId]);
            // PlayerCrestActiveId reflects the first matching crest id
            Assert.Equal("CrestOfFury", Store.PlayerCrestActiveId[PlayerId]);
        }

        [Fact]
        public void OnWaveStart_PlayerScope_StampsPlayerLeavesEnemy()
        {
            var (sys, wave) = MakeSystem(crests: new[]
            {
                new CrestDef { Id = "CrestOfBounty", TriggerWaves = new[] { 1 }, TargetScope = "player", PlayerDamageMult = 1.15f, PlayerGoldMult = 1.50f }
            });
            int eid = Enemy();
            Player();
            FireOnWaveStart(wave);
            // Enemy fields untouched (1f/0f fast path)
            Assert.Equal(1f, Store.EnemyCrestDamageMult[eid]);
            Assert.Equal(0f, Store.EnemyCrestRegenPerSec[eid]);
            // Player fields stamped
            Assert.Equal(1.15f, Store.PlayerCrestDamageMult[PlayerId]);
            Assert.Equal(1.50f, Store.PlayerCrestGoldMult[PlayerId]);
            Assert.Equal("CrestOfBounty", Store.PlayerCrestActiveId[PlayerId]);
        }

        [Fact]
        public void OnWaveStart_BothScope_StampsBothEnemyAndPlayer()
        {
            var (sys, wave) = MakeSystem(crests: new[]
            {
                new CrestDef { Id = "CrestOfFortitude", TriggerWaves = new[] { 1 }, TargetScope = "both", EnemyDamageMult = 1.10f, EnemyRegenPerSec = 2f, PlayerDamageMult = 1.10f, PlayerGoldMult = 1.20f }
            });
            int eid = Enemy();
            Player();
            FireOnWaveStart(wave);
            // Enemy fields stamped
            Assert.Equal(1.10f, Store.EnemyCrestDamageMult[eid]);
            Assert.Equal(2f, Store.EnemyCrestRegenPerSec[eid]);
            // Player fields stamped
            Assert.Equal(1.10f, Store.PlayerCrestDamageMult[PlayerId]);
            Assert.Equal(1.20f, Store.PlayerCrestGoldMult[PlayerId]);
        }

        // ─── Non-matching wave index → no-op ───────────────────────────

        [Fact]
        public void OnWaveStart_NonMatchingWave_NoOp()
        {
            // currentWave defaults to 1, so crests with TriggerWaves that
            // don't include 1 should not fire.
            var (sys, wave) = MakeSystem(crests: new[]
            {
                new CrestDef { Id = "Fury", TriggerWaves = new[] { 4, 7, 10 }, TargetScope = "enemy", EnemyDamageMult = 1.5f, EnemyRegenPerSec = 5f },
                new CrestDef { Id = "Bounty", TriggerWaves = new[] { 5 }, TargetScope = "player", PlayerGoldMult = 1.5f }
            });
            int eid = Enemy();
            Player();
            FireOnWaveStart(wave);
            // No crest matched wave 1 → defaults preserved
            Assert.Equal(1f, Store.EnemyCrestDamageMult[eid]);
            Assert.Equal(0f, Store.EnemyCrestRegenPerSec[eid]);
            Assert.Equal(1f, Store.PlayerCrestDamageMult[PlayerId]);
            Assert.Equal(1f, Store.PlayerCrestGoldMult[PlayerId]);
            Assert.True(string.IsNullOrEmpty(Store.PlayerCrestActiveId[PlayerId]));
        }

        // ─── Multi-crest composition (multiplicative / additive) ───────

        [Fact]
        public void OnWaveStart_MultipleCrestsSameWave_ComposeMultiplicatively()
        {
            // Two enemy-scope crests both fire on wave 1.
            // 1.20 * 1.50 = 1.80 → 1.80f damage mult.
            // 3.0 + 2.0 = 5.0 additive regen.
            var (sys, wave) = MakeSystem(crests: new[]
            {
                new CrestDef { Id = "Fury", TriggerWaves = new[] { 1 }, TargetScope = "enemy", EnemyDamageMult = 1.20f, EnemyRegenPerSec = 3f },
                new CrestDef { Id = "TideOfHealing", TriggerWaves = new[] { 1 }, TargetScope = "enemy", EnemyDamageMult = 1.50f, EnemyRegenPerSec = 2f }
            });
            int eid = Enemy();
            FireOnWaveStart(wave);
            Assert.Equal(1.80f, Store.EnemyCrestDamageMult[eid], 4);
            Assert.Equal(5f, Store.EnemyCrestRegenPerSec[eid], 4);
            // First matching crest id is the one cached
            Assert.Equal("Fury", Store.PlayerCrestActiveId[PlayerId]);
        }

        // ─── Per-frame Update is a no-op ───────────────────────────────

        [Fact]
        public void Update_IsNoOp()
        {
            var (sys, wave) = MakeSystem(crests: new[]
            {
                new CrestDef { Id = "Fury", TriggerWaves = new[] { 1 }, TargetScope = "enemy", EnemyDamageMult = 1.5f }
            });
            int eid = Enemy();
            // Per-frame tick — must be a no-op.
            sys.Update(0.016f);
            sys.Update(1.0f);
            sys.Update(0f);
            Assert.Equal(1f, Store.EnemyCrestDamageMult[eid]);
            Assert.Equal(0f, Store.EnemyCrestRegenPerSec[eid]);
        }

        // ─── OnWaveComplete reset path ────────────────────────────────

        [Fact]
        public void OnWaveComplete_ResetsEnemyCachesToDefaults()
        {
            var (sys, wave) = MakeSystem(crests: Array.Empty<CrestDef>());
            int eid = Enemy();
            // Pre-corrupt
            Store.EnemyCrestDamageMult[eid] = 1.75f;
            Store.EnemyCrestRegenPerSec[eid] = 12f;
            FireOnWaveComplete(wave);
            Assert.Equal(1f, Store.EnemyCrestDamageMult[eid]);
            Assert.Equal(0f, Store.EnemyCrestRegenPerSec[eid]);
        }

        [Fact]
        public void OnWaveComplete_ResetsPlayerCachesToDefaults()
        {
            var (sys, wave) = MakeSystem(crests: Array.Empty<CrestDef>());
            Player();
            Store.PlayerCrestDamageMult[PlayerId] = 2f;
            Store.PlayerCrestGoldMult[PlayerId] = 3f;
            Store.PlayerCrestActiveId[PlayerId] = "stale";
            FireOnWaveComplete(wave);
            Assert.Equal(1f, Store.PlayerCrestDamageMult[PlayerId]);
            Assert.Equal(1f, Store.PlayerCrestGoldMult[PlayerId]);
            Assert.True(string.IsNullOrEmpty(Store.PlayerCrestActiveId[PlayerId]));
        }

        [Fact]
        public void OnWaveComplete_RunsEvenWhenSystemDisabled()
        {
            // Cleanup is unconditional: disabled-then-enabled sessions
            // shouldn't see stale data.
            var (sys, wave) = MakeSystem(enabled: false);
            int eid = Enemy();
            Player();
            Store.EnemyCrestDamageMult[eid] = 9f;
            Store.PlayerCrestDamageMult[PlayerId] = 9f;
            FireOnWaveComplete(wave);
            Assert.Equal(1f, Store.EnemyCrestDamageMult[eid]);
            Assert.Equal(1f, Store.PlayerCrestDamageMult[PlayerId]);
        }

        // ─── SubscribeToWaveEvents idempotency ─────────────────────────

        [Fact]
        public void TestCompositionSubscribesEachHandlerExactlyOnce()
        {
            var (sys, wave) = MakeSystem(crests: Array.Empty<CrestDef>());
            var startHandlers = GetEventDelegate(wave, "OnWaveStart");
            var completeHandlers = GetEventDelegate(wave, "OnWaveComplete");
            Assert.Single(startHandlers!.GetInvocationList());
            Assert.Single(completeHandlers!.GetInvocationList());
            int eid = Enemy();
            Store.EnemyCrestDamageMult[eid] = 5f;
            FireOnWaveComplete(wave);
            Assert.Equal(1f, Store.EnemyCrestDamageMult[eid]);
        }

        [Fact]
        public void HandlersRequireNoConcreteSpawnerReference()
        {
            Config.Crest = new CrestConfig
            {
                Enabled = true,
                Crests = Array.Empty<CrestDef>()
            };
            var sys = new CrestSystem(Store, Config);
            int eid = Enemy();
            Store.EnemyCrestDamageMult[eid] = 5f;
            sys.HandleWaveComplete();
            Assert.Equal(1f, Store.EnemyCrestDamageMult[eid]);
        }

        // ─── EnemyActive / active-list filtering ──────────────────────

        [Fact]
        public void OnWaveComplete_OnlyResetsActiveEnemies()
        {
            var (sys, wave) = MakeSystem(crests: Array.Empty<CrestDef>());
            int e0 = Enemy();
            int e1 = Enemy(e => e.X = 1f);
            Store.EnemyActive[e1] = false;
            Store.EnemyCrestDamageMult[e0] = 5f;
            Store.EnemyCrestDamageMult[e1] = 7f;
            FireOnWaveComplete(wave);
            // Active enemy reset
            Assert.Equal(1f, Store.EnemyCrestDamageMult[e0]);
            // Inactive enemy NOT reset (the system skips EnemyActive=false)
            Assert.Equal(7f, Store.EnemyCrestDamageMult[e1]);
        }

        // ─── JSON deserialization smoke test ──────────────────────────

        [Fact]
        public void CrestConfig_JsonDeserialization_ProducesValidConfig()
        {
            const string json = @"{
                ""Enabled"": true,
                ""Crests"": [
                    {
                        ""Id"": ""CrestOfFury"",
                        ""Name"": ""Crest of Fury"",
                        ""TriggerWaves"": [1, 4, 7, 10],
                        ""TargetScope"": ""enemy"",
                        ""EnemyDamageMult"": 1.20,
                        ""EnemyRegenPerSec"": 0.0,
                        ""PlayerGoldMult"": 1.0,
                        ""PlayerDamageMult"": 1.0
                    },
                    {
                        ""Id"": ""CrestOfBounty"",
                        ""Name"": ""Crest of Bounty"",
                        ""TriggerWaves"": [2, 5, 8, 11],
                        ""TargetScope"": ""player"",
                        ""PlayerGoldMult"": 1.50
                    }
                ]
            }";
            var opts = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            var cfg = System.Text.Json.JsonSerializer.Deserialize<CrestConfig>(json, opts);
            Assert.NotNull(cfg);
            Assert.True(cfg.Enabled);
            Assert.Equal(2, cfg.Crests.Length);
            Assert.Equal("CrestOfFury", cfg.Crests[0].Id);
            Assert.Equal("enemy", cfg.Crests[0].TargetScope);
            Assert.Equal(1.20f, cfg.Crests[0].EnemyDamageMult);
            Assert.Equal(new[] { 1, 4, 7, 10 }, cfg.Crests[0].TriggerWaves);
            // CrestOfBounty: only PlayerGoldMult set; others fall back to defaults
            Assert.Equal("CrestOfBounty", cfg.Crests[1].Id);
            Assert.Equal(1.50f, cfg.Crests[1].PlayerGoldMult);
            Assert.Equal(1f, cfg.Crests[1].PlayerDamageMult); // default
            Assert.Equal(1f, cfg.Crests[1].EnemyDamageMult); // default
            Assert.Equal(0f, cfg.Crests[1].EnemyRegenPerSec); // default
        }
    }
}
