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
    /// Tests for Round 196 Direction 3: Soul Harvest System.
    /// Verifies:
    ///   1. Default state: all soul fields are 0 (zero-overhead)
    ///   2. Per-kill harvest: kill credits EnemySoulValue (default 1f)
    ///   3. Boss multiplier: SetEnemySoulValue(eid, 100) grants 100 souls
    ///   4. Cap clamp: kills beyond cap don't overflow
    ///   5. BaseSoulPerKill config adds on top of EnemySoulValue
    ///   6. TrySpendSouls: success deducts and increments SpentTotal
    ///   7. TrySpendSouls: insufficient funds returns false, no deduction
    ///   8. TrySpendSouls: free spend (cost==0) is always allowed
    ///   9. AddSouls: direct grant honors cap
    ///  10. Per-frame regen: regen*N seconds adds expected amount
    ///  11. Per-frame regen: no regen = no work (sentinel-gated fast path)
    ///  12. Per-frame regen: cap-clamp at upper bound
    ///  13. Per-frame regen: negative dt is no-op
    ///  14. SetSoulCap / SetSoulRegen clamp to safe ranges
    ///  15. ResetPlayer clears all soul state
    ///  16. AddPlayer resets soul fields to 0 (recycled player safety)
    ///  17. Kill with invalid enemyId: no crash, no credit
    ///  18. Kill with invalid playerId: no crash, no credit
    ///  19. HasEnoughSouls read helper
    ///  20. Multiple kills accumulate (no per-kill reset)
    ///  21. SoulHarvestConfig defaults (DefaultCap, DefaultRegenPerSecond, BaseSoulPerKill)
    ///  22. GetSoulCap returns resolved effective cap (not 0 sentinel)
    ///  23. Update at cap stays at cap (no arithmetic drift)
    ///  24. Per-enemy SoulValue=0 grants no soul (config-driven opt-out)
    ///  25. SubscribeToEvents: idempotent — calling twice doesn't double-credit
    ///  26. AddSouls with negative amount is no-op
    /// </summary>
    public class SoulHarvestSystemTests : BattleTestBase
    {
        private const int PlayerId = 0;
        private const int MaxPlayers = 10; // mirrors ComponentStore.MAX_PLAYERS (internal)
        private const float DeltaTime = 1f / 60f;

        // ── Test helpers ────────────────────────────────────────────────

        private SoulHarvestSystem MakeSystem(SoulHarvestConfig? config = null)
        {
            Player();
            return new SoulHarvestSystem(Store, config ?? new SoulHarvestConfig(), null);
        }

        /// <summary>生成指定 soulValue 的敌人并立即走标准死亡队列结算击杀。</summary>
        private void KillEnemy(float soulValue = 1f)
        {
            int eid = Enemy(e => e.Name = "E");
            if (soulValue != 1f)
            {
                Store.SetEnemySoulValue(eid, soulValue);
            }
            // Drive the kill through the standard death-queue path so the
            // production OnEnemyKilled event fires.
            Store.QueueEnemyDeath(eid, PlayerId);
            Store.ResolveEnemiesKilledThisFrame();
        }

        // ── 1. Default state ────────────────────────────────────────────
        [Fact]
        public void DefaultState_AllSoulFieldsInert()
        {
            // Per C# spec, new arrays of value types are zero-initialized.
            Assert.Equal(0f, Store.PlayerSoulCount[0]);
            Assert.Equal(0f, Store.PlayerSoulCap[0]);
            Assert.Equal(0f, Store.PlayerSoulRegen[0]);
            Assert.Equal(0f, Store.PlayerSoulSpentTotal[0]);
            Assert.Equal(0f, Store.PlayerSoulEarnedTotal[0]);
            // Enemy default = 1f (set in AddEnemy), not 0.
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 100f, 5f, 10, 1, "E");
            Assert.Equal(1f, Store.EnemySoulValue[eid]);
        }

        // ── 2. Per-kill harvest: kill credits 1 soul ───────────────────
        [Fact]
        public void Kill_CreditsDefaultSoulValue()
        {
            var system = MakeSystem();
            system.SubscribeToEvents();
            KillEnemy(); // default soulValue=1
            Assert.Equal(1f, Store.PlayerSoulCount[PlayerId]);
            Assert.Equal(1f, Store.PlayerSoulEarnedTotal[PlayerId]);
        }

        // ── 3. Boss multiplier: SetEnemySoulValue(100) → 100 souls ─────
        [Fact]
        public void Kill_BossGrantsConfiguredSoulValue()
        {
            var system = MakeSystem();
            system.SubscribeToEvents();
            KillEnemy(soulValue: 100f);
            Assert.Equal(100f, Store.PlayerSoulCount[PlayerId]);
        }

        // ── 4. Cap clamp: kills beyond cap don't overflow ──────────────
        [Fact]
        public void Kill_CapClampsAccumulation()
        {
            var system = MakeSystem();
            system.SubscribeToEvents();
            // Cap at 5
            Store.PlayerSoulCap[PlayerId] = 5f;
            // 10 kills at 1 soul each
            for (int i = 0; i < 10; i++)
            {
                KillEnemy();
            }
            // Capped at 5
            Assert.Equal(5f, Store.PlayerSoulCount[PlayerId]);
            // EarnedTotal reflects what was actually credited (5), not what was attempted (10).
            Assert.Equal(5f, Store.PlayerSoulEarnedTotal[PlayerId]);
        }

        // ── 5. BaseSoulPerKill config adds on top of EnemySoulValue ────
        [Fact]
        public void Kill_AddsBaseSoulPerKillConfig()
        {
            var config = new SoulHarvestConfig { BaseSoulPerKill = 2f };
            var system = MakeSystem(config);
            system.SubscribeToEvents();
            KillEnemy(soulValue: 1f);
            // 1 (EnemySoulValue) + 2 (BaseSoulPerKill) = 3
            Assert.Equal(3f, Store.PlayerSoulCount[PlayerId]);
        }

        // ── 6. TrySpendSouls: success deducts and increments SpentTotal ─
        [Fact]
        public void TrySpendSouls_Success_DeductsAndIncrementsSpent()
        {
            var system = MakeSystem();
            system.SubscribeToEvents();
            // Grant 10 souls via kills
            for (int i = 0; i < 10; i++)
            {
                KillEnemy();
            }
            Assert.True(system.TrySpendSouls(PlayerId, 4f));
            Assert.Equal(6f, Store.PlayerSoulCount[PlayerId]);
            Assert.Equal(4f, Store.PlayerSoulSpentTotal[PlayerId]);
        }

        // ── 7. TrySpendSouls: insufficient funds returns false, no deduction
        [Fact]
        public void TrySpendSouls_InsufficientFunds_ReturnsFalse()
        {
            var system = MakeSystem();
            system.SubscribeToEvents();
            KillEnemy(); // +1 soul
            Assert.Equal(1f, Store.PlayerSoulCount[PlayerId]);
            // Try to spend 5 — should fail
            Assert.False(system.TrySpendSouls(PlayerId, 5f));
            // Balance unchanged
            Assert.Equal(1f, Store.PlayerSoulCount[PlayerId]);
            // SpentTotal unchanged (no successful spend)
            Assert.Equal(0f, Store.PlayerSoulSpentTotal[PlayerId]);
        }

        // ── 8. TrySpendSouls: free spend (cost==0) is always allowed ────
        [Fact]
        public void TrySpendSouls_ZeroCost_AlwaysSucceeds()
        {
            var system = MakeSystem();
            // No souls at all
            Assert.True(system.TrySpendSouls(PlayerId, 0f));
            Assert.Equal(0f, Store.PlayerSoulCount[PlayerId]);
            // Negative cost is also free (defensive)
            Assert.True(system.TrySpendSouls(PlayerId, -5f));
        }

        // ── 9. AddSouls: direct grant honors cap ────────────────────────
        [Fact]
        public void AddSouls_DirectGrant_HonorsCap()
        {
            var system = MakeSystem();
            Store.PlayerSoulCap[PlayerId] = 10f;
            system.AddSouls(PlayerId, 5f);
            Assert.Equal(5f, Store.PlayerSoulCount[PlayerId]);
            Assert.Equal(5f, Store.PlayerSoulEarnedTotal[PlayerId]);
            // Second grant would exceed cap (5 + 7 = 12 > 10)
            system.AddSouls(PlayerId, 7f);
            Assert.Equal(10f, Store.PlayerSoulCount[PlayerId]);
            // EarnedTotal still tracks actual credits: 5 + 5 (clamped from 7) = 10
            Assert.Equal(10f, Store.PlayerSoulEarnedTotal[PlayerId]);
        }

        // ── 10. Per-frame regen: regen*N seconds adds expected amount ───
        [Fact]
        public void Update_RegenAddsSouls()
        {
            var system = MakeSystem();
            Store.PlayerSoulRegen[PlayerId] = 5f; // 5 souls/sec
            // 1 second = 60 ticks at dt=1/60
            for (int i = 0; i < 60; i++)
            {
                system.Update(DeltaTime);
            }
            // Should be exactly 5 souls (regen * 1.0 sec)
            Assert.Equal(5f, Store.PlayerSoulCount[PlayerId], 2);
        }

        // ── 11. Per-frame regen: no regen = no work ────────────────────
        [Fact]
        public void Update_NoRegen_LeavesSoulsAtZero()
        {
            var system = MakeSystem();
            // PlayerSoulRegen[PlayerId] defaults to 0
            for (int i = 0; i < 100; i++)
            {
                system.Update(DeltaTime);
            }
            Assert.Equal(0f, Store.PlayerSoulCount[PlayerId]);
        }

        // ── 12. Per-frame regen: cap-clamp at upper bound ──────────────
        [Fact]
        public void Update_RegenClampedAtCap()
        {
            var system = MakeSystem();
            Store.PlayerSoulCap[PlayerId] = 3f;
            Store.PlayerSoulRegen[PlayerId] = 10f; // 10 souls/sec
            // Run for 5 seconds (way more than needed)
            for (int i = 0; i < 300; i++)
            {
                system.Update(DeltaTime);
            }
            Assert.Equal(3f, Store.PlayerSoulCount[PlayerId]);
        }

        // ── 13. Per-frame regen: negative dt is no-op ───────────────────
        [Fact]
        public void Update_NegativeDt_NoOp()
        {
            var system = MakeSystem();
            Store.PlayerSoulRegen[PlayerId] = 5f;
            system.Update(-1f);
            Assert.Equal(0f, Store.PlayerSoulCount[PlayerId]);
        }

        // ── 14. SetSoulCap / SetSoulRegen clamp to safe ranges ─────────
        [Fact]
        public void SetSoulCap_ClampsToSafeRange()
        {
            var system = MakeSystem();
            // Negative cap → 0
            system.SetSoulCap(PlayerId, -10f);
            Assert.Equal(0f, Store.PlayerSoulCap[PlayerId]);
            // Excessive cap → sentinel (1M)
            system.SetSoulCap(PlayerId, 99_999_999f);
            Assert.Equal(1_000_000f, Store.PlayerSoulCap[PlayerId]);
        }

        [Fact]
        public void SetSoulRegen_ClampsToSafeRange()
        {
            var system = MakeSystem();
            system.SetSoulRegen(PlayerId, -1f);
            Assert.Equal(0f, Store.PlayerSoulRegen[PlayerId]);
            system.SetSoulRegen(PlayerId, 99_999_999f);
            Assert.Equal(1000f, Store.PlayerSoulRegen[PlayerId]);
        }

        // ── 15. ResetPlayer clears all soul state ──────────────────────
        [Fact]
        public void ResetPlayer_ClearsAllSoulState()
        {
            // 显式注入非默认 DefaultCap/DefaultRegenPerSecond，
            // 期望值全部从注入的 config 推导，不钉生产默认常量。
            var config = new SoulHarvestConfig { DefaultCap = 123f, DefaultRegenPerSecond = 0.5f };
            var system = MakeSystem(config);
            system.SubscribeToEvents();
            // Accumulate state
            for (int i = 0; i < 5; i++)
            {
                KillEnemy();
            }
            system.TrySpendSouls(PlayerId, 2f);
            Assert.True(Store.PlayerSoulCount[PlayerId] > 0f);
            Assert.Equal(2f, Store.PlayerSoulSpentTotal[PlayerId]);
            // Reset
            system.ResetPlayer(PlayerId);
            Assert.Equal(0f, Store.PlayerSoulCount[PlayerId]);
            // SpentTotal and EarnedTotal are also cleared (full reset).
            Assert.Equal(0f, Store.PlayerSoulSpentTotal[PlayerId]);
            Assert.Equal(0f, Store.PlayerSoulEarnedTotal[PlayerId]);
            // Cap / regen are reset to the injected config values.
            Assert.Equal(config.DefaultCap, Store.PlayerSoulCap[PlayerId]);
            Assert.Equal(config.DefaultRegenPerSecond, Store.PlayerSoulRegen[PlayerId]);
        }

        // ── 16. AddPlayer resets soul fields to 0 ───────────────────────
        [Fact]
        public void AddPlayer_ResetsSoulFieldsToZero()
        {
            Store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            // Simulate prior-game leftovers (would happen if a slot were reused)
            Store.PlayerSoulCount[0] = 500f;
            Store.PlayerSoulSpentTotal[0] = 200f;
            Store.PlayerSoulEarnedTotal[0] = 700f;
            // Re-add player — fields should reset
            Store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            Assert.Equal(0f, Store.PlayerSoulCount[0]);
            Assert.Equal(0f, Store.PlayerSoulSpentTotal[0]);
            Assert.Equal(0f, Store.PlayerSoulEarnedTotal[0]);
        }

        // ── 17. SetEnemySoulValue with out-of-range enemyId: no crash, no-op ─────────
        [Fact]
        public void SetEnemySoulValue_OutOfRange_NoOpNoCrash()
        {
            var system = MakeSystem();
            system.SubscribeToEvents();
            // The death queue will route through ResolveEnemiesKilledThisFrame which
            // iterates only over queued IDs. Manually calling OnEnemyKilled from
            // outside isn't possible (the event isn't a public field), so the
            // "invalid enemy" test instead verifies the underlying accessor
            // defends: SetEnemySoulValue out-of-range is a no-op.
            Store.SetEnemySoulValue(-1, 50f);
            Store.SetEnemySoulValue(ComponentStore.MAX_ENTITIES, 50f);
            Store.SetEnemySoulValue(ComponentStore.MAX_ENTITIES + 100, 50f);
            // The cap is the only thing changed by valid input — no credit yet.
            Assert.Equal(0f, Store.PlayerSoulCount[PlayerId]);
        }

        // ── 18. SetSoulCap with out-of-range playerId: no crash, no-op ─────────
        [Fact]
        public void SetSoulCap_OutOfRange_NoOpNoCrash()
        {
            var system = MakeSystem();
            system.SubscribeToEvents();
            // Direct accessor defends: SetSoulCap on invalid player is a no-op.
            system.SetSoulCap(-1, 50f);
            system.SetSoulCap(MaxPlayers, 50f);
            system.SetSoulCap(MaxPlayers + 100, 50f);
            Assert.Equal(0f, Store.PlayerSoulCount[0]);
        }

        // ── 19. HasEnoughSouls read helper ──────────────────────────────
        [Fact]
        public void HasEnoughSouls_BehavesCorrectly()
        {
            var system = MakeSystem();
            system.SubscribeToEvents();
            KillEnemy();
            // 1 soul available
            Assert.True(system.HasEnoughSouls(PlayerId, 1f));
            Assert.True(system.HasEnoughSouls(PlayerId, 0f));
            Assert.False(system.HasEnoughSouls(PlayerId, 2f));
            // Invalid player
            Assert.False(system.HasEnoughSouls(-1, 0f));
            Assert.False(system.HasEnoughSouls(MaxPlayers, 0f));
        }

        // ── 20. Multiple kills accumulate (no per-kill reset) ───────────
        [Fact]
        public void MultipleKills_AccumulateSouls()
        {
            var system = MakeSystem();
            system.SubscribeToEvents();
            for (int i = 0; i < 50; i++)
            {
                KillEnemy();
            }
            // 50 kills × 1 soul = 50
            Assert.Equal(50f, Store.PlayerSoulCount[PlayerId]);
        }

        // ── 21. SoulHarvestConfig defaults ──────────────────────────────
        [Fact]
        public void SoulHarvestConfig_Defaults()
        {
            var config = new SoulHarvestConfig();
            // 只断言相对不变量：默认上限必须可兑现（>0），regen 与击杀基础值不得为负。
            Assert.True(config.DefaultCap > 0f);
            Assert.True(config.DefaultRegenPerSecond >= 0f);
            Assert.True(config.BaseSoulPerKill >= 0f);
        }

        // ── 22. GetSoulCap returns resolved effective cap (not 0 sentinel)
        [Fact]
        public void GetSoulCap_ReturnsConfigDefault_WhenSOAIsZero()
        {
            // 显式注入 DefaultCap：期望值从注入结果推导，不钉生产默认 999f。
            var config = new SoulHarvestConfig { DefaultCap = 42f };
            var system = MakeSystem(config);
            // SOA PlayerSoulCap is 0 (the AddPlayer reset default)
            Assert.Equal(0f, Store.PlayerSoulCap[PlayerId]);
            // GetSoulCap should fall back to the injected config.DefaultCap.
            Assert.Equal(config.DefaultCap, system.GetSoulCap(PlayerId));
        }

        // ── 23. Update at cap stays at cap (no arithmetic drift) ───────
        [Fact]
        public void Update_AtCap_StaysAtCap()
        {
            var system = MakeSystem();
            Store.PlayerSoulCap[PlayerId] = 10f;
            Store.PlayerSoulRegen[PlayerId] = 5f;
            // First fill the cap
            for (int i = 0; i < 1000; i++)
            {
                system.Update(DeltaTime);
            }
            Assert.Equal(10f, Store.PlayerSoulCount[PlayerId]);
            // More updates should not drift
            for (int i = 0; i < 1000; i++)
            {
                system.Update(DeltaTime);
            }
            Assert.Equal(10f, Store.PlayerSoulCount[PlayerId]);
        }

        // ── 24. Per-enemy SoulValue=0 grants no soul (config-driven opt-out)
        [Fact]
        public void Kill_ZeroSoulValue_NoCredit()
        {
            var system = MakeSystem();
            system.SubscribeToEvents();
            KillEnemy(soulValue: 0f);
            Assert.Equal(0f, Store.PlayerSoulCount[PlayerId]);
            Assert.Equal(0f, Store.PlayerSoulEarnedTotal[PlayerId]);
        }

        // ── 25. SubscribeToEvents: idempotent guard ─────────────────────
        [Fact]
        public void SubscribeToEvents_Idempotent_DoesNotDoubleCredit()
        {
            var system = MakeSystem();
            system.SubscribeToEvents();
            system.SubscribeToEvents(); // call twice
            KillEnemy();
            // Should be 1, not 2 — duplicate subscriptions would double-credit.
            Assert.Equal(1f, Store.PlayerSoulCount[PlayerId]);
        }

        // ── 26. AddSouls with negative amount is no-op ──────────────────
        [Fact]
        public void AddSouls_NegativeAmount_NoOp()
        {
            var system = MakeSystem();
            system.AddSouls(PlayerId, 5f);
            system.AddSouls(PlayerId, -10f); // no-op (defensive)
            Assert.Equal(5f, Store.PlayerSoulCount[PlayerId]);
            // EarnedTotal only counts the positive grant.
            Assert.Equal(5f, Store.PlayerSoulEarnedTotal[PlayerId]);
        }
    }
}
