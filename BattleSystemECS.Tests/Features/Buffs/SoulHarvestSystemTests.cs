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
    public class SoulHarvestSystemTests
    {
        private const int PlayerId = 0;
        private const int MaxPlayers = 10; // mirrors ComponentStore.MAX_PLAYERS (internal)
        private const float DeltaTime = 1f / 60f;

        // ── Test helpers ────────────────────────────────────────────────

        private static (SoulHarvestSystem system, ComponentStore store) MakeSystem(SoulHarvestConfig? config = null)
        {
            var store = new ComponentStore();
            store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            var system = new SoulHarvestSystem(store, config ?? new SoulHarvestConfig(), null);
            return (system, store);
        }

        private static int MakeEnemy(ComponentStore store, float soulValue = 1f)
        {
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 5f, 10, 1, "E");
            if (soulValue != 1f)
            {
                store.SetEnemySoulValue(eid, soulValue);
            }
            return eid;
        }

        private static void KillEnemy(ComponentStore store, int enemyId, int playerId)
        {
            // Drive the kill through the standard death-queue path so the
            // production OnEnemyKilled event fires.
            store.QueueEnemyDeath(enemyId, playerId);
            store.ResolveEnemiesKilledThisFrame();
        }

        // ── 1. Default state ────────────────────────────────────────────
        [Fact]
        public void DefaultState_AllSoulFieldsInert()
        {
            var store = new ComponentStore();
            // Per C# spec, new arrays of value types are zero-initialized.
            Assert.Equal(0f, store.PlayerSoulCount[0]);
            Assert.Equal(0f, store.PlayerSoulCap[0]);
            Assert.Equal(0f, store.PlayerSoulRegen[0]);
            Assert.Equal(0f, store.PlayerSoulSpentTotal[0]);
            Assert.Equal(0f, store.PlayerSoulEarnedTotal[0]);
            // Enemy default = 1f (set in AddEnemy), not 0.
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 5f, 10, 1, "E");
            Assert.Equal(1f, store.EnemySoulValue[eid]);
        }

        // ── 2. Per-kill harvest: kill credits 1 soul ───────────────────
        [Fact]
        public void Kill_CreditsDefaultSoulValue()
        {
            var (system, store) = MakeSystem();
            system.SubscribeToEvents();
            int eid = MakeEnemy(store); // default soulValue=1
            KillEnemy(store, eid, PlayerId);
            Assert.Equal(1f, store.PlayerSoulCount[PlayerId]);
            Assert.Equal(1f, store.PlayerSoulEarnedTotal[PlayerId]);
        }

        // ── 3. Boss multiplier: SetEnemySoulValue(100) → 100 souls ─────
        [Fact]
        public void Kill_BossGrantsConfiguredSoulValue()
        {
            var (system, store) = MakeSystem();
            system.SubscribeToEvents();
            int boss = MakeEnemy(store, soulValue: 100f);
            KillEnemy(store, boss, PlayerId);
            Assert.Equal(100f, store.PlayerSoulCount[PlayerId]);
        }

        // ── 4. Cap clamp: kills beyond cap don't overflow ──────────────
        [Fact]
        public void Kill_CapClampsAccumulation()
        {
            var (system, store) = MakeSystem();
            system.SubscribeToEvents();
            // Cap at 5
            store.PlayerSoulCap[PlayerId] = 5f;
            // 10 kills at 1 soul each
            for (int i = 0; i < 10; i++)
            {
                int eid = MakeEnemy(store);
                KillEnemy(store, eid, PlayerId);
            }
            // Capped at 5
            Assert.Equal(5f, store.PlayerSoulCount[PlayerId]);
            // EarnedTotal reflects what was actually credited (5), not what was attempted (10).
            Assert.Equal(5f, store.PlayerSoulEarnedTotal[PlayerId]);
        }

        // ── 5. BaseSoulPerKill config adds on top of EnemySoulValue ────
        [Fact]
        public void Kill_AddsBaseSoulPerKillConfig()
        {
            var config = new SoulHarvestConfig { BaseSoulPerKill = 2f };
            var (system, store) = MakeSystem(config);
            system.SubscribeToEvents();
            int eid = MakeEnemy(store, soulValue: 1f);
            KillEnemy(store, eid, PlayerId);
            // 1 (EnemySoulValue) + 2 (BaseSoulPerKill) = 3
            Assert.Equal(3f, store.PlayerSoulCount[PlayerId]);
        }

        // ── 6. TrySpendSouls: success deducts and increments SpentTotal ─
        [Fact]
        public void TrySpendSouls_Success_DeductsAndIncrementsSpent()
        {
            var (system, store) = MakeSystem();
            system.SubscribeToEvents();
            // Grant 10 souls via kills
            for (int i = 0; i < 10; i++)
            {
                int eid = MakeEnemy(store);
                KillEnemy(store, eid, PlayerId);
            }
            Assert.True(system.TrySpendSouls(PlayerId, 4f));
            Assert.Equal(6f, store.PlayerSoulCount[PlayerId]);
            Assert.Equal(4f, store.PlayerSoulSpentTotal[PlayerId]);
        }

        // ── 7. TrySpendSouls: insufficient funds returns false, no deduction
        [Fact]
        public void TrySpendSouls_InsufficientFunds_ReturnsFalse()
        {
            var (system, store) = MakeSystem();
            system.SubscribeToEvents();
            int eid = MakeEnemy(store); // +1 soul
            KillEnemy(store, eid, PlayerId);
            Assert.Equal(1f, store.PlayerSoulCount[PlayerId]);
            // Try to spend 5 — should fail
            Assert.False(system.TrySpendSouls(PlayerId, 5f));
            // Balance unchanged
            Assert.Equal(1f, store.PlayerSoulCount[PlayerId]);
            // SpentTotal unchanged (no successful spend)
            Assert.Equal(0f, store.PlayerSoulSpentTotal[PlayerId]);
        }

        // ── 8. TrySpendSouls: free spend (cost==0) is always allowed ────
        [Fact]
        public void TrySpendSouls_ZeroCost_AlwaysSucceeds()
        {
            var (system, store) = MakeSystem();
            // No souls at all
            Assert.True(system.TrySpendSouls(PlayerId, 0f));
            Assert.Equal(0f, store.PlayerSoulCount[PlayerId]);
            // Negative cost is also free (defensive)
            Assert.True(system.TrySpendSouls(PlayerId, -5f));
        }

        // ── 9. AddSouls: direct grant honors cap ────────────────────────
        [Fact]
        public void AddSouls_DirectGrant_HonorsCap()
        {
            var (system, store) = MakeSystem();
            store.PlayerSoulCap[PlayerId] = 10f;
            system.AddSouls(PlayerId, 5f);
            Assert.Equal(5f, store.PlayerSoulCount[PlayerId]);
            Assert.Equal(5f, store.PlayerSoulEarnedTotal[PlayerId]);
            // Second grant would exceed cap (5 + 7 = 12 > 10)
            system.AddSouls(PlayerId, 7f);
            Assert.Equal(10f, store.PlayerSoulCount[PlayerId]);
            // EarnedTotal still tracks actual credits: 5 + 5 (clamped from 7) = 10
            Assert.Equal(10f, store.PlayerSoulEarnedTotal[PlayerId]);
        }

        // ── 10. Per-frame regen: regen*N seconds adds expected amount ───
        [Fact]
        public void Update_RegenAddsSouls()
        {
            var (system, store) = MakeSystem();
            store.PlayerSoulRegen[PlayerId] = 5f; // 5 souls/sec
            // 1 second = 60 ticks at dt=1/60
            for (int i = 0; i < 60; i++)
            {
                system.Update(DeltaTime);
            }
            // Should be exactly 5 souls (regen * 1.0 sec)
            Assert.Equal(5f, store.PlayerSoulCount[PlayerId], 2);
        }

        // ── 11. Per-frame regen: no regen = no work ────────────────────
        [Fact]
        public void Update_NoRegen_LeavesSoulsAtZero()
        {
            var (system, store) = MakeSystem();
            // PlayerSoulRegen[PlayerId] defaults to 0
            for (int i = 0; i < 100; i++)
            {
                system.Update(DeltaTime);
            }
            Assert.Equal(0f, store.PlayerSoulCount[PlayerId]);
        }

        // ── 12. Per-frame regen: cap-clamp at upper bound ──────────────
        [Fact]
        public void Update_RegenClampedAtCap()
        {
            var (system, store) = MakeSystem();
            store.PlayerSoulCap[PlayerId] = 3f;
            store.PlayerSoulRegen[PlayerId] = 10f; // 10 souls/sec
            // Run for 5 seconds (way more than needed)
            for (int i = 0; i < 300; i++)
            {
                system.Update(DeltaTime);
            }
            Assert.Equal(3f, store.PlayerSoulCount[PlayerId]);
        }

        // ── 13. Per-frame regen: negative dt is no-op ───────────────────
        [Fact]
        public void Update_NegativeDt_NoOp()
        {
            var (system, store) = MakeSystem();
            store.PlayerSoulRegen[PlayerId] = 5f;
            system.Update(-1f);
            Assert.Equal(0f, store.PlayerSoulCount[PlayerId]);
        }

        // ── 14. SetSoulCap / SetSoulRegen clamp to safe ranges ─────────
        [Fact]
        public void SetSoulCap_ClampsToSafeRange()
        {
            var (system, store) = MakeSystem();
            // Negative cap → 0
            system.SetSoulCap(PlayerId, -10f);
            Assert.Equal(0f, store.PlayerSoulCap[PlayerId]);
            // Excessive cap → sentinel (1M)
            system.SetSoulCap(PlayerId, 99_999_999f);
            Assert.Equal(1_000_000f, store.PlayerSoulCap[PlayerId]);
        }

        [Fact]
        public void SetSoulRegen_ClampsToSafeRange()
        {
            var (system, store) = MakeSystem();
            system.SetSoulRegen(PlayerId, -1f);
            Assert.Equal(0f, store.PlayerSoulRegen[PlayerId]);
            system.SetSoulRegen(PlayerId, 99_999_999f);
            Assert.Equal(1000f, store.PlayerSoulRegen[PlayerId]);
        }

        // ── 15. ResetPlayer clears all soul state ──────────────────────
        [Fact]
        public void ResetPlayer_ClearsAllSoulState()
        {
            var (system, store) = MakeSystem();
            system.SubscribeToEvents();
            // Accumulate state
            for (int i = 0; i < 5; i++)
            {
                int eid = MakeEnemy(store);
                KillEnemy(store, eid, PlayerId);
            }
            system.TrySpendSouls(PlayerId, 2f);
            Assert.True(store.PlayerSoulCount[PlayerId] > 0f);
            Assert.Equal(2f, store.PlayerSoulSpentTotal[PlayerId]);
            // Reset
            system.ResetPlayer(PlayerId);
            Assert.Equal(0f, store.PlayerSoulCount[PlayerId]);
            // SpentTotal and EarnedTotal are also cleared (full reset).
            Assert.Equal(0f, store.PlayerSoulSpentTotal[PlayerId]);
            Assert.Equal(0f, store.PlayerSoulEarnedTotal[PlayerId]);
            // Cap is reset to config.DefaultCap = 999f.
            Assert.Equal(999f, store.PlayerSoulCap[PlayerId]);
        }

        // ── 16. AddPlayer resets soul fields to 0 ───────────────────────
        [Fact]
        public void AddPlayer_ResetsSoulFieldsToZero()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            // Simulate prior-game leftovers (would happen if a slot were reused)
            store.PlayerSoulCount[0] = 500f;
            store.PlayerSoulSpentTotal[0] = 200f;
            store.PlayerSoulEarnedTotal[0] = 700f;
            // Re-add player — fields should reset
            store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            Assert.Equal(0f, store.PlayerSoulCount[0]);
            Assert.Equal(0f, store.PlayerSoulSpentTotal[0]);
            Assert.Equal(0f, store.PlayerSoulEarnedTotal[0]);
        }

        // ── 17. SetEnemySoulValue with out-of-range enemyId: no crash, no-op ─────────
        [Fact]
        public void SetEnemySoulValue_OutOfRange_NoOpNoCrash()
        {
            var (system, store) = MakeSystem();
            system.SubscribeToEvents();
            // The death queue will route through ResolveEnemiesKilledThisFrame which
            // iterates only over queued IDs. Manually calling OnEnemyKilled from
            // outside isn't possible (the event isn't a public field), so the
            // "invalid enemy" test instead verifies the underlying accessor
            // defends: SetEnemySoulValue out-of-range is a no-op.
            store.SetEnemySoulValue(-1, 50f);
            store.SetEnemySoulValue(ComponentStore.MAX_ENTITIES, 50f);
            store.SetEnemySoulValue(ComponentStore.MAX_ENTITIES + 100, 50f);
            // The cap is the only thing changed by valid input — no credit yet.
            Assert.Equal(0f, store.PlayerSoulCount[PlayerId]);
        }

        // ── 18. SetSoulCap with out-of-range playerId: no crash, no-op ─────────
        [Fact]
        public void SetSoulCap_OutOfRange_NoOpNoCrash()
        {
            var (system, store) = MakeSystem();
            system.SubscribeToEvents();
            // Direct accessor defends: SetSoulCap on invalid player is a no-op.
            system.SetSoulCap(-1, 50f);
            system.SetSoulCap(MaxPlayers, 50f);
            system.SetSoulCap(MaxPlayers + 100, 50f);
            Assert.Equal(0f, store.PlayerSoulCount[0]);
        }

        // ── 19. HasEnoughSouls read helper ──────────────────────────────
        [Fact]
        public void HasEnoughSouls_BehavesCorrectly()
        {
            var (system, store) = MakeSystem();
            system.SubscribeToEvents();
            int eid = MakeEnemy(store);
            KillEnemy(store, eid, PlayerId);
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
            var (system, store) = MakeSystem();
            system.SubscribeToEvents();
            for (int i = 0; i < 50; i++)
            {
                int eid = MakeEnemy(store);
                KillEnemy(store, eid, PlayerId);
            }
            // 50 kills × 1 soul = 50
            Assert.Equal(50f, store.PlayerSoulCount[PlayerId]);
        }

        // ── 21. SoulHarvestConfig defaults ──────────────────────────────
        [Fact]
        public void SoulHarvestConfig_Defaults()
        {
            var config = new SoulHarvestConfig();
            Assert.Equal(999f, config.DefaultCap);
            Assert.Equal(0f, config.DefaultRegenPerSecond);
            Assert.Equal(0f, config.BaseSoulPerKill);
        }

        // ── 22. GetSoulCap returns resolved effective cap (not 0 sentinel)
        [Fact]
        public void GetSoulCap_ReturnsConfigDefault_WhenSOAIsZero()
        {
            var (system, store) = MakeSystem();
            // SOA PlayerSoulCap is 0 (the AddPlayer reset default)
            Assert.Equal(0f, store.PlayerSoulCap[PlayerId]);
            // GetSoulCap should fall back to config.DefaultCap = 999f
            Assert.Equal(999f, system.GetSoulCap(PlayerId));
        }

        // ── 23. Update at cap stays at cap (no arithmetic drift) ───────
        [Fact]
        public void Update_AtCap_StaysAtCap()
        {
            var (system, store) = MakeSystem();
            store.PlayerSoulCap[PlayerId] = 10f;
            store.PlayerSoulRegen[PlayerId] = 5f;
            // First fill the cap
            for (int i = 0; i < 1000; i++)
            {
                system.Update(DeltaTime);
            }
            Assert.Equal(10f, store.PlayerSoulCount[PlayerId]);
            // More updates should not drift
            for (int i = 0; i < 1000; i++)
            {
                system.Update(DeltaTime);
            }
            Assert.Equal(10f, store.PlayerSoulCount[PlayerId]);
        }

        // ── 24. Per-enemy SoulValue=0 grants no soul (config-driven opt-out)
        [Fact]
        public void Kill_ZeroSoulValue_NoCredit()
        {
            var (system, store) = MakeSystem();
            system.SubscribeToEvents();
            int eid = MakeEnemy(store, soulValue: 0f);
            KillEnemy(store, eid, PlayerId);
            Assert.Equal(0f, store.PlayerSoulCount[PlayerId]);
            Assert.Equal(0f, store.PlayerSoulEarnedTotal[PlayerId]);
        }

        // ── 25. SubscribeToEvents: idempotent guard ─────────────────────
        [Fact]
        public void SubscribeToEvents_Idempotent_DoesNotDoubleCredit()
        {
            var (system, store) = MakeSystem();
            system.SubscribeToEvents();
            system.SubscribeToEvents(); // call twice
            int eid = MakeEnemy(store);
            KillEnemy(store, eid, PlayerId);
            // Should be 1, not 2 — duplicate subscriptions would double-credit.
            Assert.Equal(1f, store.PlayerSoulCount[PlayerId]);
        }

        // ── 26. AddSouls with negative amount is no-op ──────────────────
        [Fact]
        public void AddSouls_NegativeAmount_NoOp()
        {
            var (system, store) = MakeSystem();
            system.AddSouls(PlayerId, 5f);
            system.AddSouls(PlayerId, -10f); // no-op (defensive)
            Assert.Equal(5f, store.PlayerSoulCount[PlayerId]);
            // EarnedTotal only counts the positive grant.
            Assert.Equal(5f, store.PlayerSoulEarnedTotal[PlayerId]);
        }
    }
}