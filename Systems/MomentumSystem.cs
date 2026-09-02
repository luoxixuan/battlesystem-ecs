using System;
using BattleSystemECS.Config;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Momentum System (Round174+ Direction3) — global per-(wave-time) ramping
    /// damage / attack-speed buff shared by all of a player's active towers.
    ///
    /// Lifecycle per frame:
    ///1. Update(float deltaTime) runs once per WavePhase tick (when the wave is
    ///   running). For each active player it increments PlayerMomentumTimer by
    ///   deltaTime, then computes the new tier as
    ///     tier = min(floor(timer / TierDuration), MaxTiers)
    ///   and writes the cached damage / speed bonuses into every active tower.
    ///   When the wave is NOT running (BuildPhase or pre-wave), the timer does
    ///   NOT advance (latch _waveRunning == false) and the cached tower bonuses
    ///   are forced to 0f so the TowerAttack hot path takes the no-bonus branch.
    ///2. The cached damage / speed bonuses are derived once per frame from
    ///   the highest tier across all active players (so in a multi-player
    ///   game, the strongest player carries the team's towers — this matches
    ///   the "shared momentum" design intent since towers do not track
    ///   per-player ownership in ComponentStore and the global ramping is
    ///   meant to apply to the player's force as a whole). Sentinel fast
    ///   path: when no player has tier > 0 the cache is forced to 0f.
    ///3. The system subscribes to WaveSpawningSystem.OnWaveStart /
    ///   OnWaveComplete (one-shot, idempotent) to drive the _waveRunning
    ///   latch and to optionally reset the per-player timer at wave start
    ///   (when MomentumConfig.ResetOnWave == true).
    ///
    /// The system does NOT directly modify TowerAttackSystem — it just writes
    /// the cached arrays. Sentinel-gated: when MomentumConfig.Enabled ==
    /// false OR TierDuration <= 0 OR MaxTiers <= 0, the per-frame Update is a
    /// no-op and all tower cache fields are forced to 0f. Wave-start reset
    /// is also a no-op in that case.
    ///
    /// Note: towers in the active list are stamped uniformly with the
    /// max-active-player-tier bonus. This is the conservative path because
    /// the SOA does not track per-tower player ownership. Single-player
    /// games (the dominant use case) see the right behavior; multi-player
    /// sees a shared "momentum ramp" where any player's progress lifts the
    /// team's towers.
    /// </summary>
    public class MomentumSystem
    {
        private readonly ComponentStore store;
        private readonly GameConfig gameConfig;

        // Wave running latch — true between OnWaveStart and OnWaveComplete.
        // Cleared on OnWaveComplete; set on OnWaveStart. Update() only
        // accumulates the timer when this is true so the BuildPhase (and
        // inter-wave idle) do not bleed momentum.
        private bool _waveRunning;
        // Idempotency guard against WireDependencies re-init / test reset
        // paths stacking duplicate handlers.

        public MomentumSystem(ComponentStore store, GameConfig gameConfig)
        {
            this.store = store;
            this.gameConfig = gameConfig;
        }

        /// <summary>
        /// Subscribe to WaveSpawningSystem OnWaveStart / OnWaveComplete. Called
        /// once by SystemRegistry.WireDependencies(). Idempotent: re-call is
        /// a no-op so the WireDependencies reset-test path doesn't stack
        /// duplicate handlers.
        /// </summary>
        /// <summary>
        /// Public setter for the wave-running latch. Useful for tests that
        /// don't want to spin up a full WaveSpawningSystem. Production code
        /// 通过 <see cref="SubscribeToWaveEvents"/> 驱动该流程。
        /// </summary>
        public void SetWaveRunning(bool running) => _waveRunning = running;

        // ── Event handlers ────────────────────────────────────────────
        public void HandleWaveStart()
        {
            _waveRunning = true;
            var cfg = gameConfig.Momentum;
            if (cfg == null || !cfg.Enabled) return;
            // On wave start, optionally reset the per-player timer so each
            // wave starts at tier 0. We iterate all MAX_PLAYERS slots — the
            // default 0f timer is also the right post-reset value, so this
            // is a no-op for players who never built momentum anyway.
            if (cfg.ResetOnWave)
            {
                int playerCount = ComponentStore.MAX_PLAYERS;
                for (int p = 0; p < playerCount; p++)
                {
                    store.PlayerMomentumTimer[p] = 0f;
                    store.PlayerMomentumCurrentTier[p] = 0;
                }
            }
        }

        public void HandleWaveComplete()
        {
            _waveRunning = false;
            // On wave end, the timer stops accumulating. We do NOT clear
            // the per-tower bonus cache here — the next Update tick (with
            // _waveRunning == false) will force-clear it via the
            // wave-not-running fast path, so the cleanup is owned by the
            // Update gate rather than the event handler.
        }

        /// <summary>
        /// Per-frame tick. Only accumulates the per-player timer when a wave
        /// is in progress. Computes the highest tier across active players
        /// and stamps the cached damage / speed bonuses onto every active
        /// tower.
        ///
        /// Sentinel fast path: when the config is null / disabled / or the
        /// tier math is degenerate (TierDuration <= 0 OR MaxTiers <= 0), the
        /// per-player timer is left untouched and the per-tower cache is
        /// forced to 0f in a single pass. When _waveRunning is false, we
        /// also stamp the cache to 0f so any stale tier value from the
        /// previous wave is cleared.
        /// </summary>
        public void Update(float deltaTime)
        {
            var cfg = gameConfig.Momentum;
            // Disabled / degenerate config → force all tower caches to 0f.
            // We walk active towers only (O(active), not O(MAX_ENTITIES)) so
            // the fast path is bounded by the deployed tower count.
            if (cfg == null || !cfg.Enabled || cfg.TierDuration <= 0f || cfg.MaxTiers <= 0)
            {
                ForceClearAllTowers();
                return;
            }
            // Wave not running → also force clear. The bonus should not
            // leak into the BuildPhase. Sentinel: even if _waveRunning is
            // false we still walk the tower list so any stale tier-derived
            // bonus from a prior wave is wiped to 0f.
            if (!_waveRunning)
            {
                ForceClearAllTowers();
                return;
            }

            // Accumulate timer + recompute tier per player. Track the
            // highest tier across active players so multi-player games
            // share the strongest player's progress.
            int playerCount = ComponentStore.MAX_PLAYERS;
            int maxTiers = cfg.MaxTiers;
            float tierDuration = cfg.TierDuration;
            float dmgPerTier = cfg.DamageBonusPerTier;
            float spdPerTier = cfg.SpeedBonusPerTier;
            int maxActiveTier = 0;
            for (int p = 0; p < playerCount; p++)
            {
                float timer = store.PlayerMomentumTimer[p] + deltaTime;
                store.PlayerMomentumTimer[p] = timer;
                int tier = (int)(timer / tierDuration);
                if (tier > maxTiers) tier = maxTiers;
                if (tier < 0) tier = 0;
                store.PlayerMomentumCurrentTier[p] = tier;
                if (tier > maxActiveTier) maxActiveTier = tier;
            }
            // Sentinel 0-tier → no bonus. Force the cache to 0f so the
            // hot path takes the no-bonus branch (cheap branch).
            if (maxActiveTier <= 0)
            {
                ForceClearAllTowers();
                return;
            }
            float dmgBonus = maxActiveTier * dmgPerTier;
            float spdBonus = maxActiveTier * spdPerTier;
            var active = store.ActiveTowerIds;
            for (int i = 0; i < active.Count; i++)
            {
                int id = active[i];
                if (id < 0 || id >= ComponentStore.MAX_ENTITIES) continue;
                if (!store.TowerActive[id]) continue;
                store.TowerMomentumBonusDamage[id] = dmgBonus;
                store.TowerMomentumBonusSpeed[id] = spdBonus;
            }
        }

        /// <summary>
        /// Force-clear the cached Momentum bonuses on every active tower.
        /// Used by both the disabled-config fast path AND the
        /// wave-not-running gate. The bonus must be 0f for inactive states
        /// so the TowerAttack hot path takes the no-bonus branch and a
        /// stale tier value from a prior wave does not leak.
        /// </summary>
        private void ForceClearAllTowers()
        {
            var active = store.ActiveTowerIds;
            for (int i = 0; i < active.Count; i++)
            {
                int id = active[i];
                if (id < 0 || id >= ComponentStore.MAX_ENTITIES) continue;
                if (!store.TowerActive[id]) continue;
                store.TowerMomentumBonusDamage[id] = 0f;
                store.TowerMomentumBonusSpeed[id] = 0f;
            }
        }
    }
}
