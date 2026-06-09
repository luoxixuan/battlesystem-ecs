using System;
using BattleSystemECS.Config;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Adrenaline System (Round 207 Direction 2) — low-HP / critical-HP player-side
    /// attack-speed / cooldown-reduction buff plus a one-shot Rush state.
    ///
    /// Lifecycle per frame:
    /// 1. Update(float deltaTime) runs once per CombatGroup tick. For each player it
    ///    derives the current tier from the live HP ratio:
    ///      tier 0 — HP > LowHpThreshold (normal, no buff)
    ///      tier 1 — HP in (CriticalHpThreshold, LowHpThreshold]
    ///      tier 2 — HP <= CriticalHpThreshold (critical + one-shot Rush)
    ///    and stamps the cached attack-speed bonus (additive) and cooldown multiplier
    ///    (multiplicative) into PlayerAdrenalineAttackSpeedMult / CooldownMult.
    /// 2. Rush transition: when the tier transitions from < 2 to exactly 2 (HP
    ///    crosses CriticalHpThreshold downward), PlayerAdrenalineRushActiveFrames is
    ///    set to RushDurationFrames. The transition is detected on tier change, NOT
    ///    on the HP crossing per se, so the rush window does NOT re-trigger every
    ///    frame the player stays critical. While > 0, PlayerTowerAttackSystem
    ///    force-fires every active tower once per frame (Nano Boost-style burst).
    /// 3. Decrement: each tick, PlayerAdrenalineRushActiveFrames is decremented by 1
    ///    (clamped at 0). When it reaches 0 the rush window closes and the per-frame
    ///    force-fire stops.
    ///
    /// The system does NOT directly modify PlayerTowerAttackSystem — it just writes
    /// the cache arrays. PlayerTowerAttackSystem reads PlayerAdrenalineRushActiveFrames
    /// and PlayerAdrenalineAttackSpeedMult to apply the bonus. SkillSystem reads
    /// PlayerAdrenalineCooldownMult to scale skill cooldowns.
    ///
    /// Sentinel-gated: Enabled=false OR LowHpThreshold <= CriticalHpThreshold →
    /// Update is a no-op and all four cache fields are forced to the no-bonus
    /// defaults (tier 0, 0 rush frames, 0f atk-spd bonus, 1f cooldown mult).
    /// </summary>
    public class AdrenalineSystem
    {
        private readonly ComponentStore store;
        private readonly GameConfig gameConfig;

        // Cached previous tier per player — used to detect tier-1 → 2 transition
        // for the one-shot Rush. Stored as a local field because the cache array
        // is shared with the rest of the game (HUD / debug overlays). Default 0
        // matches a fresh-player init. NOT reset on OnWaveStart so the rush
        // doesn't re-fire on every wave start. The re-fire semantic is
        // "any time the player is NOT currently in tier 2, the next entry to
        // tier 2 fires a fresh Rush" — i.e. healing up to tier 0/1 and then
        // re-crossing downward DOES re-fire. This is intentional and gives
        // the player multiple adrenaline bursts if they survive a critical
        // scrape repeatedly. If a future designer wants "one Rush per session",
        // they should add a session-level gate around this branch.
        private readonly int[] _prevTier = new int[ComponentStore.MAX_PLAYERS];

        public AdrenalineSystem(ComponentStore store, GameConfig gameConfig)
        {
            this.store = store;
            this.gameConfig = gameConfig;
        }

        /// <summary>
        /// Per-frame tick. Derives the player's current tier from the live HP
        /// ratio, stamps the cache arrays, and ticks down any active Rush window.
        ///
        /// Sentinel fast path: when the config is null / disabled / or the threshold
        /// math is degenerate (LowHpThreshold &lt;= 0 OR CriticalHpThreshold &lt;= 0
        /// OR LowHpThreshold &lt;= CriticalHpThreshold), the per-player tier is
        /// forced to 0, the rush frame count is forced to 0, and the cache fields
        /// are forced to the no-bonus defaults (0f atk-spd, 1f cooldown mult).
        /// </summary>
        public void Update(float deltaTime)
        {
            var cfg = gameConfig.Adrenaline;
            // Disabled / degenerate config → force all per-player cache fields
            // to the no-bonus defaults. O(MAX_PLAYERS) loop.
            if (cfg == null || !cfg.Enabled
                || cfg.LowHpThreshold <= 0f
                || cfg.CriticalHpThreshold <= 0f
                || cfg.LowHpThreshold <= cfg.CriticalHpThreshold)
            {
                ForceClearAllPlayers();
                return;
            }

            int playerCount = ComponentStore.MAX_PLAYERS;
            int rushDuration = cfg.RushDurationFrames > 0 ? cfg.RushDurationFrames : 0;
            float lowThreshold = cfg.LowHpThreshold;
            float critThreshold = cfg.CriticalHpThreshold;
            float lowAtkSpd = cfg.LowTierAttackSpeedBonus;
            float critAtkSpd = cfg.CriticalTierAttackSpeedBonus;
            float lowCd = cfg.LowTierCooldownMult;
            float critCd = cfg.CriticalTierCooldownMult;
            // Sentinel 1f for the cooldown mult so a degenerate config doesn't
            // silently double skill cooldowns. (Tier 0 path uses 1f explicitly.)
            if (lowCd <= 0f) lowCd = 1f;
            if (critCd <= 0f) critCd = 1f;
            // Sentinel-gate the atk-spd bonuses to non-negative so we never
            // accidentally reduce tower attack speed when the designer sets a
            // negative value.
            if (lowAtkSpd < 0f) lowAtkSpd = 0f;
            if (critAtkSpd < 0f) critAtkSpd = 0f;

            for (int p = 0; p < playerCount; p++)
            {
                // Bug 2 + 3 fix: dead / uninitialized player slots need
                // their cache fields force-cleared so a respawn into critical
                // HP triggers a fresh Rush. The previous "skip via continue"
                // approach left _prevTier[p] stale (Bug 2: missed Rush on
                // respawn-into-critical) and the per-player cache fields
                // stale (Bug 3: potential staleness if any consumer reads
                // them without a liveness check). We now write the no-bonus
                // defaults and continue — the cost is the same (one extra
                // write per dead slot) and the semantics are clean.
                if (store.PlayerCurrentHealth[p] <= 0f)
                {
                    store.PlayerAdrenalineTier[p] = 0;
                    store.PlayerAdrenalineRushActiveFrames[p] = 0;
                    store.PlayerAdrenalineAttackSpeedMult[p] = 0f;
                    store.PlayerAdrenalineCooldownMult[p] = 1f;
                    _prevTier[p] = 0;
                    continue;
                }
                float maxHp = store.PlayerMaxHealth[p];
                if (maxHp <= 0f) continue;
                float ratio = store.PlayerCurrentHealth[p] / maxHp;
                int tier;
                float atkSpdBonus;
                float cdMult;
                if (ratio <= critThreshold)
                {
                    tier = 2;
                    atkSpdBonus = critAtkSpd;
                    cdMult = critCd;
                }
                else if (ratio <= lowThreshold)
                {
                    tier = 1;
                    atkSpdBonus = lowAtkSpd;
                    cdMult = lowCd;
                }
                else
                {
                    tier = 0;
                    atkSpdBonus = 0f;
                    cdMult = 1f;
                }

                // Detect tier transition into tier 2 (one-shot Rush).
                // We use _prevTier so a player who heals back to > CriticalHp
                // and re-crosses downward gets a second Rush — the design
                // intent is "one shot per critical-HP entry", not "one shot
                // per game session". Crit-to-crit same tier → no re-fire.
                if (tier == 2 && _prevTier[p] < 2 && rushDuration > 0)
                {
                    // Bug 1 fix: set Rush=Duration+1 so the first frame the
                    // PlayerTowerAttackSystem sees is the freshly-set value
                    // (not Duration-1 after the post-set decrement). Without
                    // this fix PlayerTowerAttackSystem only ever sees 59..0
                    // for a 60-frame rush, giving the player 1 frame less
                    // of 2x damage than the documented RushDurationFrames
                    // spec. Decrement to Duration (60) on the same frame.
                    store.PlayerAdrenalineRushActiveFrames[p] = rushDuration + 1;
                }
                _prevTier[p] = tier;

                store.PlayerAdrenalineTier[p] = tier;
                store.PlayerAdrenalineAttackSpeedMult[p] = atkSpdBonus;
                store.PlayerAdrenalineCooldownMult[p] = cdMult;

                // Decrement the rush window (clamped at 0). Decrement happens
                // AFTER the transition check so a frame that triggers a fresh
                // rush has the rush visible to PlayerTowerAttackSystem for
                // the full RushDurationFrames count (with the +1 fix above,
                // the first frame the player sees is the full RushDurationFrames
                // value, then it decrements to Duration-1 on the next frame, etc).
                int rush = store.PlayerAdrenalineRushActiveFrames[p];
                if (rush > 0)
                {
                    rush--;
                    if (rush < 0) rush = 0;
                    store.PlayerAdrenalineRushActiveFrames[p] = rush;
                }
            }
        }

        /// <summary>
        /// Force-clear all per-player Adrenaline cache fields to the no-bonus
        /// defaults. Used by the disabled-config fast path so a re-enabled
        /// config starts from a deterministic baseline (tier 0, no rush, 0f
        /// atk-spd, 1f cooldown mult). Also clears _prevTier so the next
        /// transition into tier 2 fires a fresh Rush.
        /// </summary>
        private void ForceClearAllPlayers()
        {
            int playerCount = ComponentStore.MAX_PLAYERS;
            for (int p = 0; p < playerCount; p++)
            {
                store.PlayerAdrenalineTier[p] = 0;
                store.PlayerAdrenalineRushActiveFrames[p] = 0;
                store.PlayerAdrenalineAttackSpeedMult[p] = 0f;
                store.PlayerAdrenalineCooldownMult[p] = 1f;
                _prevTier[p] = 0;
            }
        }
    }
}
