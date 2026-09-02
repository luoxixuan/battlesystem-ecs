using System;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Fire Trail System — thin wrapper that spawns short-lived burning ground zones at
    /// a position. The actual zone storage and per-frame DoT resolution is handled by
    /// <see cref="ComponentStore"/>'s CorpseEffect slots (effectType 3 = fire DoT).
    ///
    /// This system is intentionally a passive API: it does not run a per-frame Update
    /// and owns no SOA state of its own. It exists to give other systems a single,
    /// well-named entry point for "drop a fire patch at this position" without
    /// needing to know about CorpseEffect's internal slot indices.
    ///
    /// Integration:
    ///   - <see cref="TowerAttackSystem"/> Firewall case calls <see cref="SpawnTrail"/>
    ///     after applying its burn DoT, so a hit leaves a brief burning patch at the
    ///     enemy's position. Multiple hits in different frames naturally form a trail.
    ///   - Future emitters (Burning minion on death, fire-projectile impact, etc.)
    ///     can call SpawnTrail directly without any new wiring.
    ///
    /// Performance contract:
    ///   - SpawnTrail is O(MAX_CORPSE_EFFECTS) linear scan via AddCorpseEffect to
    ///     find a free slot. Callers should avoid spawning in tight loops; per-tower
    ///     per-hit is the intended cadence.
    ///   - The system holds zero per-frame allocations and no per-tower caches.
    /// </summary>
    public class FireTrailSystem : global::BattleSystemECS.Content.Contracts.IFireTrailCommandPort
    {
        private readonly ComponentStore _store;

        // Total successful spawns since construction (debug / observability).
        private int _totalSpawned;

        // Total spawns that failed because CorpseEffect storage was full.
        private int _totalFailedFull;

        public FireTrailSystem(ComponentStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>
        /// Spawn a fire trail zone at the given world position.
        /// Returns the assigned CorpseEffect slot id (>=0) on success, or -1 if
        /// the CorpseEffect storage is full and the spawn was dropped.
        ///
        /// Defaults are tuned for a single Firewall tower hit:
        ///   - radius 1.5 cells (small burning patch)
        ///   - dps    8.0   (about half a Firewall hit per second)
        ///   - duration 2.0s
        ///   - tickInterval 0.5s
        /// Callers may override any of these.
        /// </summary>
        public int SpawnTrail(float x, float y, float radius = 1.5f, float dps = 8.0f,
            float duration = 2.0f, float tickInterval = 0.5f)
        {
            if (radius <= 0f || duration <= 0f || dps < 0f)
                return -1;

            // Defensive clamps — keep the trail reasonable even if a misconfigured
            // tower passes huge numbers.
            if (radius > 50f) radius = 50f;
            if (duration > 30f) duration = 30f;
            if (tickInterval <= 0f) tickInterval = 0.25f;

            int id = _store.AddCorpseEffect(
                x: x,
                y: y,
                effectType: 3, // 3 = Fire (DoT) — see CorpseEffectSystem
                radius: radius,
                duration: duration,
                damagePerTick: dps * tickInterval,
                slowAmount: 1f, // Fire does not slow (poison/ice are slow types)
                tickInterval: tickInterval);

            if (id >= 0)
            {
                _totalSpawned++;
            }
            else
            {
                _totalFailedFull++;
            }
            return id;
        }

        /// <summary>
        /// Number of trails spawned successfully since construction.
        /// </summary>
        public int TotalSpawned => _totalSpawned;

        /// <summary>
        /// Number of spawns that failed because CorpseEffect storage was at capacity.
        /// Sustained non-zero values indicate the caller is spawning too aggressively.
        /// </summary>
        public int TotalFailedFull => _totalFailedFull;
    }
}
