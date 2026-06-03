using System;
using System.IO;
using BattleSystemECS.Config;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Wave Skip Reward System — Roguelike / Arknights "commissary" style decision branch.
    ///
    /// Design:
    /// - During BuildPhase, the player can spend gold (default 50g) to "skip" the upcoming wave
    ///   in exchange for a permanent additive damage bonus that stacks for the rest of the level.
    /// - Cap: MaxSkipsPerLevel (default 3) — prevents trivializing difficulty by spamming skips.
    /// - State is per-player (ComponentStore.Player* SOA fields) and persists across waves.
    /// - The actual wave-index advance is a UI/GameManager concern; this system is a pure
    ///   data layer that owns the gold deduction + damage bonus accumulation. The BuildPhase
    ///   UI is expected to call TryPurchaseSkip() and, on success, advance the wave index via
    ///   WaveSpawning (e.g. by triggering the next wave early or skipping spawns).
    ///
    /// Storage (SOA, all in ComponentStore.Player fields):
    /// - PlayerWaveSkipsUsed: number of skips purchased this level
    /// - PlayerSkipBonusDamagePct: cumulative additive damage bonus (e.g. 0.30 = +30% dmg)
    ///
    /// Damage application: GetPlayerAttackDamage() in ComponentStore multiplies base damage
    /// by (1 + PlayerSkipBonusDamagePct) so all damage sources (player/tower-on-player) inherit
    /// the bonus automatically without per-call wiring.
    ///
    /// Hot-path impact: zero (BuildPhase only, no per-frame work).
    /// </summary>
    public class WaveSkipSystem
    {
        private readonly ComponentStore store;
        private readonly IRenderer renderer;
        private readonly WaveSkipConfig cfgCached;
        private readonly int playerId;

        public WaveSkipSystem(ComponentStore store, IRenderer renderer, GameConfig gameConfig, int playerId)
        {
            this.store = store;
            this.renderer = renderer;
            this.playerId = playerId;
            // Cached at construction; falls back to a safe default if config is null.
            // WaveSkipConfig is always a non-null default in GameConfig.WaveSkip so this
            // is purely defensive in case the caller hands us a partially-initialized
            // GameConfig (e.g. in a unit test that omits WaveSkip).
            this.cfgCached = gameConfig.WaveSkip ?? new WaveSkipConfig { Enabled = false };
        }

        /// <summary>
        /// Cheap accessor — avoids re-allocating a default WaveSkipConfig on every call.
        /// </summary>
        private WaveSkipConfig Config => cfgCached ?? new WaveSkipConfig { Enabled = false };

        /// <summary>
        /// Per-BuildPhase tick — currently a no-op (the system is event-driven
        /// via OnEnterBuildPhase and explicit TryPurchaseSkip() calls).
        /// </summary>
        public void Update()
        {
            // No per-frame work needed.
        }

        /// <summary>
        /// Hook for the state machine at the start of a new BuildPhase. Currently a no-op
        /// (player skip count and bonus persist across the level, only resetting on AddPlayer).
        /// </summary>
        public void OnEnterBuildPhase()
        {
            // Intentionally empty: skip count + bonus are level-scoped (reset by AddPlayer),
            // not phase-scoped — buying a skip in wave 3 still benefits wave 7.
        }

        /// <summary>
        /// Attempt to purchase a wave-skip reward. Spends gold (Config.SkipCost), increments
        /// the skip counter, and adds the additive damage bonus. Returns true on success.
        /// Fails (returns false) when: system disabled, cap reached, or insufficient gold.
        /// </summary>
        /// <param name="damageBonusGranted">How much damage bonus was applied (0 if no purchase).</param>
        /// <param name="goldSpent">How much gold was deducted (0 if no purchase).</param>
        public bool TryPurchaseSkip(out float damageBonusGranted, out float goldSpent)
        {
            damageBonusGranted = 0f;
            goldSpent = 0f;
            if (!Config.Enabled) return false;
            if (playerId < 0 || playerId >= ComponentStore.MAX_PLAYERS) return false;

            int used = store.GetPlayerWaveSkipsUsed(playerId);
            if (used >= Config.MaxSkipsPerLevel)
            {
                renderer?.Log($"[WAVESKIP] Cap reached ({Config.MaxSkipsPerLevel} skips this level).");
                return false;
            }

            float cost = Config.SkipCost;
            float currentGold = store.GetPlayerGold(playerId);
            if (currentGold < cost)
            {
                renderer?.Log($"[WAVESKIP] Not enough gold to skip (need {cost:F1}, have {currentGold:F1}).");
                return false;
            }

            // Atomic apply: deduct gold, increment skip count, add bonus.
            store.SetPlayerGold(playerId, currentGold - cost);
            store.SetPlayerWaveSkipsUsed(playerId, used + 1);
            float newBonus = store.GetPlayerSkipBonusDamagePct(playerId) + Config.SkipDamageBonusPct;
            store.SetPlayerSkipBonusDamagePct(playerId, newBonus);

            damageBonusGranted = Config.SkipDamageBonusPct;
            goldSpent = cost;
            renderer?.Log($"[WAVESKIP] Skip #{used + 1}/{Config.MaxSkipsPerLevel} purchased for {cost:F1}g (+{Config.SkipDamageBonusPct:P0} dmg, total +{newBonus:P0}).");
            return true;
        }

        /// <summary>Number of skips remaining for this player (read-only convenience).</summary>
        public int GetRemainingSkips()
        {
            int used = store.GetPlayerWaveSkipsUsed(playerId);
            int rem = Config.MaxSkipsPerLevel - used;
            return rem < 0 ? 0 : rem;
        }

        /// <summary>Current cumulative additive damage bonus from skip purchases (0 = none).</summary>
        public float GetCumulativeDamageBonusPct()
        {
            return store.GetPlayerSkipBonusDamagePct(playerId);
        }

        /// <summary>Cost of the next skip purchase (0 if disabled or cap reached).</summary>
        public float GetSkipCost()
        {
            if (!Config.Enabled) return 0f;
            if (store.GetPlayerWaveSkipsUsed(playerId) >= Config.MaxSkipsPerLevel) return 0f;
            return Config.SkipCost;
        }
    }

    /// <summary>
    /// Wave Skip Reward configuration — controls the cost, bonus magnitude, and per-level cap
    /// for BuildPhase "skip wave → gain bonus" purchases. Inspired by Arknights commissary
    /// and Brotato-style rush rewards.
    /// </summary>
    public class WaveSkipConfig
    {
        /// <summary>Master switch. Default: true</summary>
        public bool Enabled { get; set; } = true;
        /// <summary>Gold cost per skip purchase. Default: 50</summary>
        public float SkipCost { get; set; } = 50f;
        /// <summary>Additive damage bonus granted per skip (0.10 = +10% dmg). Default: 0.10</summary>
        public float SkipDamageBonusPct { get; set; } = 0.10f;
        /// <summary>Hard cap on skips per level. Default: 3</summary>
        public int MaxSkipsPerLevel { get; set; } = 3;
    }
}
