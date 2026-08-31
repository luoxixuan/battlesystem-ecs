using BattleSystemECS.Tests.Infrastructure;
using System;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
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
    ///  15. SkillSystem 真实冷却路径读取 PlayerAdrenalineCooldownMult
    /// </summary>
    public class AdrenalineSystemTests : BattleTestBase
    {
        private const int DeltaTime = 1; // 1 second per tick to make the test math simple

        // ── Test helpers ────────────────────────────────────────────────

        private AdrenalineSystem MakeSystem(AdrenalineConfig? config = null)
        {
            Player();
            if (config != null) Config.Adrenaline = config;
            return new AdrenalineSystem(Store, Config);
        }

        /// <summary>Set player 0's HP ratio. maxHp is fixed at 1000 for the test math.</summary>
        private void SetPlayerHpRatio(int playerId, float ratio)
        {
            Store.PlayerMaxHealth[playerId] = 1000f;
            Store.PlayerCurrentHealth[playerId] = 1000f * Math.Max(0f, Math.Min(1f, ratio));
        }

        // ── 1. Default state ────────────────────────────────────────────
        [Fact]
        public void DefaultState_AllFieldsAreNoBonusDefaults()
        {
            Assert.Equal(0, Store.PlayerAdrenalineTier[0]);
            Assert.Equal(0, Store.PlayerAdrenalineRushActiveFrames[0]);
            Assert.Equal(0f, Store.PlayerAdrenalineAttackSpeedMult[0]);
            Assert.Equal(1f, Store.PlayerAdrenalineCooldownMult[0]);
        }

        // ── 2. Disabled config → no-bonus fast path ─────────────────────
        [Fact]
        public void DisabledConfig_ForceClearCache()
        {
            var system = MakeSystem(new AdrenalineConfig { Enabled = false });
            // Pollute cache with non-default values to confirm ForceClearAllPlayers wipes them
            Store.PlayerAdrenalineTier[0] = 2;
            Store.PlayerAdrenalineRushActiveFrames[0] = 60;
            Store.PlayerAdrenalineAttackSpeedMult[0] = 0.5f;
            Store.PlayerAdrenalineCooldownMult[0] = 0.5f;

            system.Update(DeltaTime);

            Assert.Equal(0, Store.PlayerAdrenalineTier[0]);
            Assert.Equal(0, Store.PlayerAdrenalineRushActiveFrames[0]);
            Assert.Equal(0f, Store.PlayerAdrenalineAttackSpeedMult[0]);
            Assert.Equal(1f, Store.PlayerAdrenalineCooldownMult[0]);
        }

        // ── 3. Tier 0 (HP > LowHpThreshold) ─────────────────────────────
        [Fact]
        public void Tier0_HpAboveLowThreshold_NoBuff()
        {
            var system = MakeSystem(); // default config
            SetPlayerHpRatio(0, 0.50f); // 50% HP, well above 30% low threshold

            system.Update(DeltaTime);

            Assert.Equal(0, Store.PlayerAdrenalineTier[0]);
            Assert.Equal(0, Store.PlayerAdrenalineRushActiveFrames[0]);
            Assert.Equal(0f, Store.PlayerAdrenalineAttackSpeedMult[0]);
            Assert.Equal(1f, Store.PlayerAdrenalineCooldownMult[0]);
        }

        // ── 4. Tier 1 (HP in (CriticalHp, LowHp]) ──────────────────────
        [Fact]
        public void Tier1_HpInLowRange_LowBonusesNoRush()
        {
            var system = MakeSystem();
            SetPlayerHpRatio(0, 0.20f); // 20% HP — between 10% crit and 30% low

            system.Update(DeltaTime);

            Assert.Equal(1, Store.PlayerAdrenalineTier[0]);
            Assert.Equal(0, Store.PlayerAdrenalineRushActiveFrames[0]);
            Assert.Equal(0.25f, Store.PlayerAdrenalineAttackSpeedMult[0]); // default LowTier
            Assert.Equal(0.80f, Store.PlayerAdrenalineCooldownMult[0]);   // default LowTier
        }

        // ── 5. Tier 2 (HP <= CriticalHp) — RUSH fires on first entry ──
        [Fact]
        public void Tier2_HpAtCritical_TriggersRushWithCritBonuses()
        {
            var system = MakeSystem();
            SetPlayerHpRatio(0, 0.05f); // 5% HP — well below 10% crit

            system.Update(DeltaTime);

            Assert.Equal(2, Store.PlayerAdrenalineTier[0]);
            // After Update, Adrenaline has decremented the rush counter (RushDurationFrames=60
            // gets +1 to compensate, then decrements to 60). So we assert 60 here, NOT 61.
            Assert.Equal(60, Store.PlayerAdrenalineRushActiveFrames[0]);
            Assert.Equal(0.50f, Store.PlayerAdrenalineAttackSpeedMult[0]); // default CriticalTier
            Assert.Equal(0.50f, Store.PlayerAdrenalineCooldownMult[0]);   // default CriticalTier
        }

        // ── 6. Rush transition: tier 2 → 2 does NOT re-fire ────────────
        [Fact]
        public void Rush_DoesNotRefireOnConsecutiveCriticalFrames()
        {
            var system = MakeSystem();
            SetPlayerHpRatio(0, 0.05f);

            system.Update(DeltaTime);
            int rushAfterFirst = Store.PlayerAdrenalineRushActiveFrames[0];
            Assert.Equal(60, rushAfterFirst);

            // Second frame: still tier 2, but prevTier was already 2 → no new rush.
            // However the frame counter does decrement.
            system.Update(DeltaTime);
            Assert.Equal(2, Store.PlayerAdrenalineTier[0]);
            Assert.Equal(rushAfterFirst - 1, Store.PlayerAdrenalineRushActiveFrames[0]);
        }

        // ── 7. Rush frame countdown: clamped at 0 ──────────────────────
        [Fact]
        public void RushFrameCountdown_DecrementsAndClampsAtZero()
        {
            var system = MakeSystem(new AdrenalineConfig { RushDurationFrames = 3 });
            SetPlayerHpRatio(0, 0.05f);

            system.Update(DeltaTime);
            // First tick: trigger (write 3+1=4) then decrement to 3
            Assert.Equal(3, Store.PlayerAdrenalineRushActiveFrames[0]);

            system.Update(DeltaTime);
            Assert.Equal(2, Store.PlayerAdrenalineRushActiveFrames[0]);

            system.Update(DeltaTime);
            Assert.Equal(1, Store.PlayerAdrenalineRushActiveFrames[0]);

            system.Update(DeltaTime);
            Assert.Equal(0, Store.PlayerAdrenalineRushActiveFrames[0]);

            // Subsequent updates keep it clamped at 0
            system.Update(DeltaTime);
            Assert.Equal(0, Store.PlayerAdrenalineRushActiveFrames[0]);
        }

        // ── 8. Rush heals back to safe then re-crosses → fresh Rush ────
        [Fact]
        public void Rush_RefiresOnReEntryIntoTier2()
        {
            var system = MakeSystem();
            // Step 1: drop to critical, fire rush
            SetPlayerHpRatio(0, 0.05f);
            system.Update(DeltaTime);
            Assert.Equal(60, Store.PlayerAdrenalineRushActiveFrames[0]);

            // Step 2: heal back to 50% — rush countdown continues
            SetPlayerHpRatio(0, 0.50f);
            for (int i = 0; i < 70; i++) system.Update(DeltaTime);
            Assert.Equal(0, Store.PlayerAdrenalineRushActiveFrames[0]);

            // Step 3: drop back to critical → fresh rush
            SetPlayerHpRatio(0, 0.05f);
            system.Update(DeltaTime);
            Assert.Equal(60, Store.PlayerAdrenalineRushActiveFrames[0]);
        }

        // ── 9. CriticalHpThreshold = 0 → tier 2 disabled, no Rush ──────
        [Fact]
        public void CriticalThresholdZero_NoTier2NoRush()
        {
            var system = MakeSystem(new AdrenalineConfig { CriticalHpThreshold = 0f });
            SetPlayerHpRatio(0, 0.01f);

            system.Update(DeltaTime);

            // 1% HP — below LowHpThreshold (0.30) but CriticalHpThreshold is 0 → degenerate
            // config path → cache is cleared. Tier stays 0, no rush.
            Assert.Equal(0, Store.PlayerAdrenalineTier[0]);
            Assert.Equal(0, Store.PlayerAdrenalineRushActiveFrames[0]);
            Assert.Equal(0f, Store.PlayerAdrenalineAttackSpeedMult[0]);
            Assert.Equal(1f, Store.PlayerAdrenalineCooldownMult[0]);
        }

        // ── 10. LowHpThreshold <= CriticalHpThreshold → degenerate → no bonus ──
        [Fact]
        public void DegenerateThresholds_ForceClear()
        {
            var system = MakeSystem(new AdrenalineConfig
            {
                LowHpThreshold = 0.10f,
                CriticalHpThreshold = 0.30f, // critical > low → degenerate
            });
            SetPlayerHpRatio(0, 0.05f);
            Store.PlayerAdrenalineRushActiveFrames[0] = 99; // pollute to verify clear

            system.Update(DeltaTime);

            Assert.Equal(0, Store.PlayerAdrenalineTier[0]);
            Assert.Equal(0, Store.PlayerAdrenalineRushActiveFrames[0]);
        }

        // ── 11. Negative cooldown mult → clamped to 1f (no divide-by-zero) ──
        [Fact]
        public void NegativeCooldownMult_ClampedToOne()
        {
            var system = MakeSystem(new AdrenalineConfig
            {
                CriticalHpThreshold = 0.10f,
                CriticalTierCooldownMult = -1.0f, // degenerate
            });
            SetPlayerHpRatio(0, 0.05f);

            system.Update(DeltaTime);

            // Tier 2 should fire and the mult should be clamped to 1f (not -1.0f)
            Assert.Equal(2, Store.PlayerAdrenalineTier[0]);
            Assert.Equal(1f, Store.PlayerAdrenalineCooldownMult[0]);
        }

        // ── 12. Dead player (HP=0) skipped ─────────────────────────────
        [Fact]
        public void DeadPlayer_Skipped()
        {
            var system = MakeSystem();
            // Player 0 dies (HP=0); we expect Update to skip and NOT fire rush
            Store.PlayerMaxHealth[0] = 1000f;
            Store.PlayerCurrentHealth[0] = 0f;

            system.Update(DeltaTime);

            Assert.Equal(0, Store.PlayerAdrenalineTier[0]);
            Assert.Equal(0, Store.PlayerAdrenalineRushActiveFrames[0]);
        }

        // ── 13. Multi-player: tier derived per-player independently ────
        [Fact]
        public void MultiPlayer_TierDerivedIndependently()
        {
            // Bootstrap a second player slot manually (MAX_PLAYERS > 1 supported in store)
            // Player 0
            Store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            // Player 1
            Store.AddPlayer(1, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            var system = new AdrenalineSystem(Store, Config);

            Store.PlayerMaxHealth[0] = 1000f; Store.PlayerCurrentHealth[0] = 800f; // 80% — tier 0
            Store.PlayerMaxHealth[1] = 1000f; Store.PlayerCurrentHealth[1] = 50f;  // 5% — tier 2

            system.Update(DeltaTime);

            Assert.Equal(0, Store.PlayerAdrenalineTier[0]);
            Assert.Equal(0, Store.PlayerAdrenalineRushActiveFrames[0]);
            Assert.Equal(2, Store.PlayerAdrenalineTier[1]);
            Assert.Equal(60, Store.PlayerAdrenalineRushActiveFrames[1]);
        }

        // ── 14. AdrenalineConfig defaults ───────────────────────────────
        [Fact]
        public void ConfigDefaults_AreSane()
        {
            var cfg = new AdrenalineConfig();
            Assert.True(cfg.Enabled);
            // 只断言相对不变量：阈值有序且都在 (0,1]，加成非负，冷却倍率在 (0,1]。
            Assert.InRange(cfg.LowHpThreshold, 0f, 1f);
            Assert.InRange(cfg.CriticalHpThreshold, 0f, cfg.LowHpThreshold);
            Assert.True(cfg.CriticalHpThreshold > 0f);
            Assert.True(cfg.LowTierAttackSpeedBonus >= 0f);
            Assert.True(cfg.CriticalTierAttackSpeedBonus >= 0f);
            Assert.True(cfg.LowTierCooldownMult > 0f && cfg.LowTierCooldownMult <= 1f);
            Assert.True(cfg.CriticalTierCooldownMult > 0f && cfg.CriticalTierCooldownMult <= 1f);
            Assert.True(cfg.RushDurationFrames > 0);
        }

        // ── 15. SkillSystem 真实冷却路径读取 Adrenaline cooldown mult ──
        [Theory(DisplayName = "SkillSystem 冷却推进读取 PlayerAdrenalineCooldownMult")]
        [InlineData(1.0f, 9.0f)]   // 无加成：10s 冷却过 1s → 剩余 9s
        [InlineData(0.5f, 8.0f)]   // 0.5 倍率：同 1s 内衰减翻倍 → 剩余 8s
        [InlineData(0.0f, 9.0f)]   // 退化值 0：生产回退到 1f，不允许除零/负衰减
        public void SkillCooldownPath_ReadsAdrenalineCooldownMult(float adrMult, float expectedRemaining)
        {
            Player();
            // 显式注入一个 Instant 技能并置于冷却中，避免 Passive 自动施放干扰观测。
            var def = new GameplayAbilityDef(
                name: "CooldownProbe",
                desc: "cooldown probe",
                cooldown: 10f,
                cost: 0f,
                dmgAttr: -1,
                fixedDmg: 0f,
                act: AbilityActivation.Instant,
                areaShape: AreaShapeType.Single,
                areaRadius: 0);
            Store.AddAbility(0, def);
            var slot = Store.GetAbility(0, 0);
            slot.CurrentCooldown = 10f;
            Store.SetAbility(0, 0, slot);
            Store.PlayerAdrenalineCooldownMult[0] = adrMult;
            Store.PlayerCooldownReduction[0] = 0f; // 排除 CDR 干扰，只观察 Adrenaline 路径

            var skill = new SkillSystem(Store, Renderer, 0, Config);
            skill.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            skill.Update(1.0f);

            Assert.Equal(expectedRemaining, Store.GetAbility(0, 0).CurrentCooldown, 5);
        }
    }
}
