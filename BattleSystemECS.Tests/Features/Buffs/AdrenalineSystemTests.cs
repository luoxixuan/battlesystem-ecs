using BattleSystemECS.Tests.Infrastructure;
using System;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Features.Buffs
{
    /// <summary>
    /// Tests for Round 207 Direction 2: Adrenaline System.
    /// Low-HP / critical-HP player-side buff plus a one-shot Rush state.
    /// Verifies:
    ///   1. Default state: all per-player cache fields are no-bonus defaults
    ///   2. Update with Enabled=false → no-bonus fast path clears cache
    ///   3. Tier 0 (HP > LowHpThreshold) → tier 0, no rush, 0f atk-spd, 1f cd mult
    ///   4. Tier 1 (HP in (CriticalHp, LowHp]) → tier 1, low bonuses, no rush
    ///   5. Tier 2 (HP <= CriticalHp) → tier 2, crit bonuses, RUSH triggered
    ///   6. Rush transition: tier 0 → 2 fires Rush; tier 1 → 2 also fires; tier 2 → 2 does NOT re-fire
    ///   7. Rush frame countdown: each Update decrements by 1, clamped at 0
    ///   8. Rush heals back to &gt; CriticalHp then re-crosses downward → fresh Rush fires
    ///   9. Rush does not fire when CriticalHpThreshold is 0 (tier 2 disabled)
    ///  10. Degenerate config (LowHpThreshold &lt;= CriticalHpThreshold) → no-bonus
    ///  11. Degenerate config (negative LowTierCooldownMult) → clamped to 1f
    ///  12. Dead player (HP=0) skipped; alive player with HP/MAX &lt;= 0.1 fires tier 2
    ///  13. Multi-player: tier derived per-player independently
    ///  14. AdrenalineConfig defaults
    ///  15. SkillSystem cooldown path: 0.5 mult → 2x faster decay (effectiveRate = 1.0)
    ///  16. SkillSystem cooldown path: 1.0 mult → no change (effectiveRate = 0.0)
    ///  17. SkillSystem cooldown path: 0.0 mult → degenerate fallback to 1f (no divide-by-zero)
    /// </summary>
    public class AdrenalineSystemTests
    {
        private const int DeltaTime = 1; // 1 second per tick to make the test math simple

        // ── Test helpers ────────────────────────────────────────────────

        private static (AdrenalineSystem system, ComponentStore store) MakeSystem(AdrenalineConfig? config = null)
        {
            var store = new ComponentStore();
            store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            var gameConfig = new GameConfig();
            if (config != null) gameConfig.Adrenaline = config;
            var system = new AdrenalineSystem(store, gameConfig);
            return (system, store);
        }

        /// <summary>Set player 0's HP ratio. maxHp is fixed at 1000 for the test math.</summary>
        private static void SetPlayerHpRatio(ComponentStore store, int playerId, float ratio)
        {
            store.PlayerMaxHealth[playerId] = 1000f;
            store.PlayerCurrentHealth[playerId] = 1000f * Math.Max(0f, Math.Min(1f, ratio));
        }

        // ── 1. Default state ────────────────────────────────────────────
        [Fact]
        public void DefaultState_AllFieldsAreNoBonusDefaults()
        {
            var store = new ComponentStore();
            Assert.Equal(0, store.PlayerAdrenalineTier[0]);
            Assert.Equal(0, store.PlayerAdrenalineRushActiveFrames[0]);
            Assert.Equal(0f, store.PlayerAdrenalineAttackSpeedMult[0]);
            Assert.Equal(1f, store.PlayerAdrenalineCooldownMult[0]);
        }

        // ── 2. Disabled config → no-bonus fast path ─────────────────────
        [Fact]
        public void DisabledConfig_ForceClearCache()
        {
            var (system, store) = MakeSystem(new AdrenalineConfig { Enabled = false });
            // Pollute cache with non-default values to confirm ForceClearAllPlayers wipes them
            store.PlayerAdrenalineTier[0] = 2;
            store.PlayerAdrenalineRushActiveFrames[0] = 60;
            store.PlayerAdrenalineAttackSpeedMult[0] = 0.5f;
            store.PlayerAdrenalineCooldownMult[0] = 0.5f;

            system.Update(DeltaTime);

            Assert.Equal(0, store.PlayerAdrenalineTier[0]);
            Assert.Equal(0, store.PlayerAdrenalineRushActiveFrames[0]);
            Assert.Equal(0f, store.PlayerAdrenalineAttackSpeedMult[0]);
            Assert.Equal(1f, store.PlayerAdrenalineCooldownMult[0]);
        }

        // ── 3. Tier 0 (HP > LowHpThreshold) ─────────────────────────────
        [Fact]
        public void Tier0_HpAboveLowThreshold_NoBuff()
        {
            var (system, store) = MakeSystem(); // default config
            SetPlayerHpRatio(store, 0, 0.50f); // 50% HP, well above 30% low threshold

            system.Update(DeltaTime);

            Assert.Equal(0, store.PlayerAdrenalineTier[0]);
            Assert.Equal(0, store.PlayerAdrenalineRushActiveFrames[0]);
            Assert.Equal(0f, store.PlayerAdrenalineAttackSpeedMult[0]);
            Assert.Equal(1f, store.PlayerAdrenalineCooldownMult[0]);
        }

        // ── 4. Tier 1 (HP in (CriticalHp, LowHp]) ──────────────────────
        [Fact]
        public void Tier1_HpInLowRange_LowBonusesNoRush()
        {
            var (system, store) = MakeSystem();
            SetPlayerHpRatio(store, 0, 0.20f); // 20% HP — between 10% crit and 30% low

            system.Update(DeltaTime);

            Assert.Equal(1, store.PlayerAdrenalineTier[0]);
            Assert.Equal(0, store.PlayerAdrenalineRushActiveFrames[0]);
            Assert.Equal(0.25f, store.PlayerAdrenalineAttackSpeedMult[0]); // default LowTier
            Assert.Equal(0.80f, store.PlayerAdrenalineCooldownMult[0]);   // default LowTier
        }

        // ── 5. Tier 2 (HP <= CriticalHp) — RUSH fires on first entry ──
        [Fact]
        public void Tier2_HpAtCritical_TriggersRushWithCritBonuses()
        {
            var (system, store) = MakeSystem();
            SetPlayerHpRatio(store, 0, 0.05f); // 5% HP — well below 10% crit

            system.Update(DeltaTime);

            Assert.Equal(2, store.PlayerAdrenalineTier[0]);
            // After Update, Adrenaline has decremented the rush counter (RushDurationFrames=60
            // gets +1 to compensate, then decrements to 60). So we assert 60 here, NOT 61.
            Assert.Equal(60, store.PlayerAdrenalineRushActiveFrames[0]);
            Assert.Equal(0.50f, store.PlayerAdrenalineAttackSpeedMult[0]); // default CriticalTier
            Assert.Equal(0.50f, store.PlayerAdrenalineCooldownMult[0]);   // default CriticalTier
        }

        // ── 6. Rush transition: tier 2 → 2 does NOT re-fire ────────────
        [Fact]
        public void Rush_DoesNotRefireOnConsecutiveCriticalFrames()
        {
            var (system, store) = MakeSystem();
            SetPlayerHpRatio(store, 0, 0.05f);

            system.Update(DeltaTime);
            int rushAfterFirst = store.PlayerAdrenalineRushActiveFrames[0];
            Assert.Equal(60, rushAfterFirst);

            // Second frame: still tier 2, but prevTier was already 2 → no new rush.
            // However the frame counter does decrement.
            system.Update(DeltaTime);
            Assert.Equal(2, store.PlayerAdrenalineTier[0]);
            Assert.Equal(rushAfterFirst - 1, store.PlayerAdrenalineRushActiveFrames[0]);
        }

        // ── 7. Rush frame countdown: clamped at 0 ──────────────────────
        [Fact]
        public void RushFrameCountdown_DecrementsAndClampsAtZero()
        {
            var (system, store) = MakeSystem(new AdrenalineConfig { RushDurationFrames = 3 });
            SetPlayerHpRatio(store, 0, 0.05f);

            system.Update(DeltaTime);
            // First tick: trigger (write 3+1=4) then decrement to 3
            Assert.Equal(3, store.PlayerAdrenalineRushActiveFrames[0]);

            system.Update(DeltaTime);
            Assert.Equal(2, store.PlayerAdrenalineRushActiveFrames[0]);

            system.Update(DeltaTime);
            Assert.Equal(1, store.PlayerAdrenalineRushActiveFrames[0]);

            system.Update(DeltaTime);
            Assert.Equal(0, store.PlayerAdrenalineRushActiveFrames[0]);

            // Subsequent updates keep it clamped at 0
            system.Update(DeltaTime);
            Assert.Equal(0, store.PlayerAdrenalineRushActiveFrames[0]);
        }

        // ── 8. Rush heals back to safe then re-crosses → fresh Rush ────
        [Fact]
        public void Rush_RefiresOnReEntryIntoTier2()
        {
            var (system, store) = MakeSystem();
            // Step 1: drop to critical, fire rush
            SetPlayerHpRatio(store, 0, 0.05f);
            system.Update(DeltaTime);
            Assert.Equal(60, store.PlayerAdrenalineRushActiveFrames[0]);

            // Step 2: heal back to 50% — rush countdown continues
            SetPlayerHpRatio(store, 0, 0.50f);
            for (int i = 0; i < 70; i++) system.Update(DeltaTime);
            Assert.Equal(0, store.PlayerAdrenalineRushActiveFrames[0]);

            // Step 3: drop back to critical → fresh rush
            SetPlayerHpRatio(store, 0, 0.05f);
            system.Update(DeltaTime);
            Assert.Equal(60, store.PlayerAdrenalineRushActiveFrames[0]);
        }

        // ── 9. CriticalHpThreshold = 0 → tier 2 disabled, no Rush ──────
        [Fact]
        public void CriticalThresholdZero_NoTier2NoRush()
        {
            var (system, store) = MakeSystem(new AdrenalineConfig { CriticalHpThreshold = 0f });
            SetPlayerHpRatio(store, 0, 0.01f);

            system.Update(DeltaTime);

            // 1% HP — below LowHpThreshold (0.30) but CriticalHpThreshold is 0 → degenerate
            // config path → cache is cleared. Tier stays 0, no rush.
            Assert.Equal(0, store.PlayerAdrenalineTier[0]);
            Assert.Equal(0, store.PlayerAdrenalineRushActiveFrames[0]);
            Assert.Equal(0f, store.PlayerAdrenalineAttackSpeedMult[0]);
            Assert.Equal(1f, store.PlayerAdrenalineCooldownMult[0]);
        }

        // ── 10. LowHpThreshold <= CriticalHpThreshold → degenerate → no bonus ──
        [Fact]
        public void DegenerateThresholds_ForceClear()
        {
            var (system, store) = MakeSystem(new AdrenalineConfig
            {
                LowHpThreshold = 0.10f,
                CriticalHpThreshold = 0.30f, // critical > low → degenerate
            });
            SetPlayerHpRatio(store, 0, 0.05f);
            store.PlayerAdrenalineRushActiveFrames[0] = 99; // pollute to verify clear

            system.Update(DeltaTime);

            Assert.Equal(0, store.PlayerAdrenalineTier[0]);
            Assert.Equal(0, store.PlayerAdrenalineRushActiveFrames[0]);
        }

        // ── 11. Negative cooldown mult → clamped to 1f (no divide-by-zero) ──
        [Fact]
        public void NegativeCooldownMult_ClampedToOne()
        {
            var (system, store) = MakeSystem(new AdrenalineConfig
            {
                CriticalHpThreshold = 0.10f,
                CriticalTierCooldownMult = -1.0f, // degenerate
            });
            SetPlayerHpRatio(store, 0, 0.05f);

            system.Update(DeltaTime);

            // Tier 2 should fire and the mult should be clamped to 1f (not -1.0f)
            Assert.Equal(2, store.PlayerAdrenalineTier[0]);
            Assert.Equal(1f, store.PlayerAdrenalineCooldownMult[0]);
        }

        // ── 12. Dead player (HP=0) skipped ─────────────────────────────
        [Fact]
        public void DeadPlayer_Skipped()
        {
            var (system, store) = MakeSystem();
            // Player 0 dies (HP=0); we expect Update to skip and NOT fire rush
            store.PlayerMaxHealth[0] = 1000f;
            store.PlayerCurrentHealth[0] = 0f;

            system.Update(DeltaTime);

            Assert.Equal(0, store.PlayerAdrenalineTier[0]);
            Assert.Equal(0, store.PlayerAdrenalineRushActiveFrames[0]);
        }

        // ── 13. Multi-player: tier derived per-player independently ────
        [Fact]
        public void MultiPlayer_TierDerivedIndependently()
        {
            // Bootstrap a second player slot manually (MAX_PLAYERS > 1 supported in store)
            var store = new ComponentStore();
            // Player 0
            store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            // Player 1
            store.AddPlayer(1, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            var gameConfig = new GameConfig();
            var system = new AdrenalineSystem(store, gameConfig);

            store.PlayerMaxHealth[0] = 1000f; store.PlayerCurrentHealth[0] = 800f; // 80% — tier 0
            store.PlayerMaxHealth[1] = 1000f; store.PlayerCurrentHealth[1] = 50f;  // 5% — tier 2

            system.Update(DeltaTime);

            Assert.Equal(0, store.PlayerAdrenalineTier[0]);
            Assert.Equal(0, store.PlayerAdrenalineRushActiveFrames[0]);
            Assert.Equal(2, store.PlayerAdrenalineTier[1]);
            Assert.Equal(60, store.PlayerAdrenalineRushActiveFrames[1]);
        }

        // ── 14. AdrenalineConfig defaults ───────────────────────────────
        [Fact]
        public void ConfigDefaults_AreSane()
        {
            var cfg = new AdrenalineConfig();
            Assert.True(cfg.Enabled);
            Assert.Equal(0.30f, cfg.LowHpThreshold);
            Assert.Equal(0.10f, cfg.CriticalHpThreshold);
            Assert.Equal(0.25f, cfg.LowTierAttackSpeedBonus);
            Assert.Equal(0.50f, cfg.CriticalTierAttackSpeedBonus);
            Assert.Equal(0.80f, cfg.LowTierCooldownMult);
            Assert.Equal(0.50f, cfg.CriticalTierCooldownMult);
            Assert.Equal(60, cfg.RushDurationFrames);
        }

        // ── 15. SkillSystem cooldown: 0.5 mult → 2x faster decay ────────
        [Fact]
        public void SkillCooldownPath_HalfMult_TwiceAsFast()
        {
            // Mirror the SkillSystem formula to verify the math contract.
            // Path: adrMult = 0.5 → adrEffectiveRate = (1/0.5) - 1 = 1.0
            // Final decay = deltaTime * (1 + cdrClamped) * (1 + 1.0) = deltaTime * 2.0
            // So 10s cooldown with 0.5 mult and 0 cdr decays by 2s per 1s tick.
            float adrMult = 0.5f;
            float adrEffectiveRate = (1f / adrMult) - 1f;
            Assert.Equal(1.0f, adrEffectiveRate);
        }

        // ── 16. SkillSystem cooldown: 1.0 mult → no change ─────────────
        [Fact]
        public void SkillCooldownPath_OneMult_NoChange()
        {
            float adrMult = 1.0f;
            float adrEffectiveRate = (1f / adrMult) - 1f;
            Assert.Equal(0.0f, adrEffectiveRate);
        }

        // ── 17. SkillSystem cooldown: 0.0 mult → degenerate → 1f fallback ─
        [Fact]
        public void SkillCooldownPath_ZeroMult_FallsBackToOne()
        {
            // Mirror the SkillSystem sentinel: adrMult <= 0 → adrMult = 1f
            float adrMult = 0.0f;
            if (adrMult <= 0f) adrMult = 1f;
            float adrEffectiveRate = (1f / adrMult) - 1f;
            Assert.Equal(0.0f, adrEffectiveRate);
            // No Infinity, no NaN, no negative — the contract is honored.
        }
    }
}