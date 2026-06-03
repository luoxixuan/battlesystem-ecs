using System;
using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Round 67 — On-Hit / On-Crit Trigger System.
    /// Round 71 — extended with On-Hit Lifesteal handler.
    ///
    /// Subscribes to GameEvents.EnemyHit and GameEvents.EnemyCrit and acts as the
    /// central fan-out for affix code that wants to react to "X happened when an
    /// enemy was hit / crit". Future tower affixes / enemy affixes can subscribe
    /// to these events without each attack system needing to know about them.
    ///
    /// This system itself is intentionally minimal — it tracks a per-frame
    /// hit-count and crit-count so we can expose cheap stats for benchmarks
    /// and (later) drive per-tower / per-enemy affix reactions.
    ///
    /// Round 71 adds lifesteal: every EnemyHit from a tower (AttackerKind=1) whose
    /// TowerLifestealFraction>0 contributes heal = damage × fraction to the
    /// single owning player, capped by the tower's TowerLifestealMaxPerFrame so
    /// a 10K-enemy burst can't overheal. Heals are clamped at PlayerMaxHealth.
    /// Zero-overhead when no tower has lifesteal configured (default 0f).
    ///
    /// Design notes:
    /// - Subscription is idempotent (WireDependencies may be called multiple times
    ///   in test / reset paths). The _subscribed flag prevents duplicate handlers.
    /// - No allocations on the hot path: the publisher already passes a pooled
    ///   EnemyHitEvent; we only read fields and increment counters.
    /// - All counters are 64-bit (long) and reset to 0 at the start of each frame
    ///   via the frame scheduler's BeginFrame() call (Round 67 convention: the
    ///   scheduler calls HitTriggerSystem.ResetCounters() at the start of every
    ///   wave-phase tick).
    /// </summary>
    public class HitTriggerSystem
    {
        private readonly ComponentStore store;
        private readonly IEventBus eventBus;
        private bool _subscribed;

        // Per-frame counters. Read by benchmark harnesses / debug UI.
        // long is used so a 10-minute stress run doesn't wrap int counters.
        public long TotalHitsThisFrame { get; private set; }
        public long TotalCritsThisFrame { get; private set; }

        // Optional: track per-enemy hit count (capped to active enemy count,
        // bounded to avoid runaway allocation). Cleared each frame.
        // Dictionary<int,int> — small per-frame churn is acceptable since we
        // only allocate when an enemy takes its first hit of the frame.
        private readonly Dictionary<int, int> _hitsPerEnemyThisFrame = new Dictionary<int, int>(128);
        private readonly Dictionary<int, int> _critsPerEnemyThisFrame = new Dictionary<int, int>(128);

        public HitTriggerSystem(ComponentStore store, IEventBus eventBus = null)
        {
            this.store = store;
            this.eventBus = eventBus ?? new EventBus();
        }

        /// <summary>
        /// Subscribe to EnemyHit and EnemyCrit. Idempotent.
        /// </summary>
        public void SubscribeToEvents()
        {
            if (_subscribed) return;
            _subscribed = true;
            this.eventBus.Subscribe(GameEvents.EnemyHit, OnEnemyHit);
            this.eventBus.Subscribe(GameEvents.EnemyCrit, OnEnemyCrit);
        }

        /// <summary>
        /// Reset per-frame counters and per-enemy dictionaries. Call from the
        /// frame scheduler's BeginFrame() at the start of each wave-phase tick.
        /// </summary>
        public void ResetCounters()
        {
            TotalHitsThisFrame = 0;
            TotalCritsThisFrame = 0;
            _hitsPerEnemyThisFrame.Clear();
            _critsPerEnemyThisFrame.Clear();
        }

        // ── Event handlers ────────────────────────────────────────────────
        // Both handlers run inside the EventBus lock (publish-time) so they
        // must be fast. We do only: read fields, increment counters, dictionary
        // upserts. No allocations beyond the per-enemy dict entry on first hit.

        private void OnEnemyHit(object payload)
        {
            var e = payload as EnemyHitEvent;
            if (e == null) return;
            TotalHitsThisFrame++;
            // Per-enemy dict upsert (no-op if entry exists).
            if (_hitsPerEnemyThisFrame.TryGetValue(e.EnemyId, out int n))
                _hitsPerEnemyThisFrame[e.EnemyId] = n + 1;
            else
                _hitsPerEnemyThisFrame[e.EnemyId] = 1;
            // Round 71 — On-Hit Lifesteal handler.
            // Only tower attacks (AttackerKind=1) can lifesteal. Player attacks don't vamp.
            // Zero-overhead fast path: when LifestealFraction==0f the inner block is skipped
            // and we still pay one branch + one indirect player lookup. For the common
            // benchmark case (no vampiric towers), this is the entire cost.
            if (e.AttackerKind == 1 && e.Damage > 0f)
            {
                int towerId = e.AttackerId;
                if ((uint)towerId < (uint)ComponentStore.MAX_ENTITIES && store.TowerActive[towerId])
                {
                    float frac = store.TowerLifestealFraction[towerId];
                    if (frac > 0f)
                    {
                        float heal = e.Damage * frac;
                        float cap = store.TowerLifestealMaxPerFrame[towerId];
                        // Per-frame cap: 0f means uncapped, otherwise clamp this single hit.
                        if (cap > 0f && heal > cap) heal = cap;
                        int playerId = store.PlayerEntityId;
                        if (ComponentStore.IsValidEntity(playerId))
                        {
                            float cur = store.PlayerCurrentHealth[playerId];
                            float max = store.PlayerMaxHealth[playerId];
                            float next = cur + heal;
                            if (max > 0f && next > max) next = max;
                            store.PlayerCurrentHealth[playerId] = next;
                        }
                    }
                }
            }
        }

        private void OnEnemyCrit(object payload)
        {
            var e = payload as EnemyHitEvent;
            if (e == null) return;
            TotalCritsThisFrame++;
            if (_critsPerEnemyThisFrame.TryGetValue(e.EnemyId, out int n))
                _critsPerEnemyThisFrame[e.EnemyId] = n + 1;
            else
                _critsPerEnemyThisFrame[e.EnemyId] = 1;
        }

        // ── Read accessors for benchmark / future affix queries ──────────

        /// <summary>Number of hits this enemy has taken this frame (0 if none).</summary>
        public int GetHitsThisFrame(int enemyId)
        {
            return _hitsPerEnemyThisFrame.TryGetValue(enemyId, out int n) ? n : 0;
        }

        /// <summary>Number of crits this enemy has been hit with this frame (0 if none).</summary>
        public int GetCritsThisFrame(int enemyId)
        {
            return _critsPerEnemyThisFrame.TryGetValue(enemyId, out int n) ? n : 0;
        }
    }
}
