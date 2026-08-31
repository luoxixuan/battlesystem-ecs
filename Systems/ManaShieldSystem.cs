using System;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Mana Shield System (Round 175 Direction 1) — mana-fueled damage absorption.
    ///
    /// Lifecycle per frame (per player):
    ///   1. Recompute cap = PlayerMaxMana * ManaShieldConfig.MaxShieldPercent
    ///   2. If Enabled = false → force absorb ratio to 0 (silent no-op fast path
    ///      so the damage hot-path stays cheap on legacy saves / disabled config)
    ///   3. If current mana is above TriggerThresholdPercent * PlayerMaxMana:
    ///        - Convert excess mana into shield up to cap, at ConversionRatio
    ///   4. Else (mana below threshold):
    ///        - Decay shield by DecayPerSecond * deltaTime
    ///   5. Clamp shield to [0, cap]
    ///
    /// The system does NOT itself intercept damage — that's done inside
    /// ComponentStore.DecreasePlayerHealth so the path is shared with the
    /// existing PlayerShield / armor / floor / reincarnation stack. This keeps
    /// the system pure-side-effect-free for tests.
    /// </summary>
    public class ManaShieldSystem
    {
        private ComponentStore store;
        private GameConfig gameConfig;
        private readonly int playerId;

        public ManaShieldSystem(ComponentStore store, GameConfig gameConfig, int playerId)
        {
            this.store = store;
            this.gameConfig = gameConfig;
            this.playerId = playerId;
        }

        /// <summary>
        /// One-time setup called from SystemRegistry after the player entity exists.
        /// Resets the mana-shield pool to 0 (so a recycled player slot doesn't
        /// inherit a leftover shield from a prior game) and primes the absorb
        /// ratio based on the master Enabled flag. Idempotent: safe to call
        /// twice in a single game (the second call is a fast re-assert of the
        /// ratio).
        /// </summary>
        public void Initialize()
        {
            if (!IsValid(playerId)) return;
            store.PlayerManaShield[playerId] = 0f;
            store.PlayerManaShieldCap[playerId] = 0f;
            store.PlayerManaShieldAbsorbRatio[playerId] = gameConfig.ManaShield.Enabled ? 1f : 0f;
            store.PlayerManaShieldTriggered[playerId] = false;
        }

        /// <summary>
        /// Per-frame tick. Sentinel-gated: when Enabled = false OR ConversionRatio <= 0,
        /// the work is a single forced-write of absorb-ratio to 0 (so the damage
        /// hot-path doesn't pay the branch cost every time) plus the cap recompute.
        /// No mana is consumed and no shield is grown in the disabled path.
        /// </summary>
        public void Update(float deltaTime)
        {
            if (!IsValid(playerId)) return;
            var cfg = gameConfig.ManaShield;

            float maxMana = store.PlayerMaxMana[playerId];
            if (maxMana <= 0f) return; // Player has no mana pool at all → inert

            // Recompute cap from current max-mana. Cheap single-write; needed
            // so tech-tree / buff changes to max-mana immediately reflect.
            store.PlayerManaShieldCap[playerId] = maxMana * cfg.MaxShieldPercent;

            if (!cfg.Enabled || cfg.ConversionRatio <= 0f)
            {
                // Force absorb ratio to 0 so DecreasePlayerHealth takes the
                // cheap no-op path. Don't drain the existing shield — that
                // would feel bad if a player temporarily disables the system.
                store.PlayerManaShieldAbsorbRatio[playerId] = 0f;
                return;
            }

            // Re-enable absorb ratio (in case it was forced to 0 by a prior
            // disabled frame).
            store.PlayerManaShieldAbsorbRatio[playerId] = 1f;

            float currentMana = store.PlayerManaShield[playerId];
            float cap = store.PlayerManaShieldCap[playerId];
            float threshold = maxMana * cfg.TriggerThresholdPercent;
            float mana = store.PlayerMana[playerId];

            if (mana > threshold && currentMana < cap)
            {
                // Excess mana above the trigger threshold, converted to shield
                // at the configured rate. We only convert as much as the
                // remaining headroom in the shield pool allows — anything
                // beyond is left in the mana pool (ManaSystem will keep
                // regen-capping it at maxMana).
                float headroom = cap - currentMana;
                // Conversion is bounded by *available* excess mana too — if
                // the player is sitting at exactly the threshold we have
                // 0 excess. If they're at maxMana, excess = maxMana - threshold.
                float excess = mana - threshold;
                // Frame-budget: convert at most `excess` mana, at most
                // `headroom` shield points' worth. Shield points gained =
                // min(excess / ratio, headroom). We then *consume* the
                // matching amount of mana to keep the energy ledger honest.
                float shieldGain = Math.Min(excess / cfg.ConversionRatio, headroom);
                if (shieldGain > 0f)
                {
                    float manaCost = shieldGain * cfg.ConversionRatio;
                    // Reduce player mana by the converted amount (clamped to 0)
                    // and raise the shield pool. Both writes go through the
                    // store's setters for safety.
                    float newMana = Math.Max(0f, mana - manaCost);
                    store.ApplyPlayerResourceAuthority(playerId, playerId, new Core.GAS.AttributeKey(7), -manaCost);
                    store.PlayerManaShield[playerId] = currentMana + shieldGain;
                }
            }
            else if (cfg.DecayPerSecond > 0f)
            {
                // Mana is below the trigger threshold — leak the shield at
                // the configured rate. This is the "mana drought" feel: shield
                // is a finite resource that disappears when you stop fueling it.
                float decay = cfg.DecayPerSecond * deltaTime;
                float pool = store.PlayerManaShield[playerId];
                if (pool > 0f)
                {
                    float newPool = Math.Max(0f, pool - decay);
                    store.PlayerManaShield[playerId] = newPool;
                }
            }

            // Final clamp — belt-and-suspenders after the gain+decay branches
            // to guard against float drift on long runs.
            float finalPool = store.PlayerManaShield[playerId];
            float finalCap = store.PlayerManaShieldCap[playerId];
            if (finalPool > finalCap) store.PlayerManaShield[playerId] = finalCap;
            if (finalPool < 0f) store.PlayerManaShield[playerId] = 0f;
        }

        /// <summary>
        /// Public read-only helper used by tests and UI. Returns the current
        /// mana-shield pool (0 if disabled / no mana).
        /// </summary>
        public float GetCurrentShield()
        {
            if (!IsValid(playerId)) return 0f;
            return store.PlayerManaShield[playerId];
        }

        public float GetShieldCap()
        {
            if (!IsValid(playerId)) return 0f;
            return store.PlayerManaShieldCap[playerId];
        }

        // Inlined bounds check (mirrors the public IsValidEntity pattern from
        // ComponentStore so the system doesn't depend on a private helper).
        private static bool IsValid(int id) => (uint)id < ComponentStore.MAX_PLAYERS;
    }
}
