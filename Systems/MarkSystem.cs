using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Mark System — Round 107 Direction 6.
    /// Stack-based mark debuff applied by tower/player attacks. Each mark hit
    /// increments EnemyMarkStacks by +1 (capped by EnemyMarkMaxThreshold) and resets
    /// EnemyMarkDecayTimer. When the timer expires, one stack is consumed. When
    /// stacks reach EnemyMarkMaxThreshold, the target is "vulnerable" and the system
    /// can fire payoff effects (e.g., bonus damage on hit, or auto-execute).
    ///
    /// Distinction from Bleed/DoT:
    ///   - Mark itself does not deal damage; it counts hits for payoff effects.
    ///   - Bleed = damage-over-time; Mark = stack count tracker.
    ///   - Decay is per-stack (1 stack / interval) rather than refresh-on-hit.
    ///
    /// Lifecycle (per WavePhase tick, runs in CombatSetup group AFTER tower/player attack):
    ///   1. Decay loop: iterate active enemies. If EnemyMarkDecayTimer > 0, decrement.
    ///      When timer reaches 0:
    ///        a. Decrement EnemyMarkStacks (clamp at 0).
    ///        b. If still > 0, reset timer to MarkConfig.DecayInterval.
    ///        c. If stacks == 0, leave timer at 0 (no ticking).
    ///   2. Threshold-triggered events (vulnerability callback) are fire-and-forget
    ///      via the public OnMarkThreshold event. Systems that want a payoff
    ///      (e.g., +50% damage taken) can subscribe.
    ///
    /// Per-frame cost: O(active enemies) with one cheap timer decrement + branch.
    /// Enemies with EnemyMarkStacks == 0 && EnemyMarkDecayTimer == 0 (the default)
    /// skip with a single bool check, so non-mark enemies incur ~zero cost.
    /// </summary>
    public class MarkSystem
    {
        private readonly ComponentStore store;
        private readonly int playerId;
        private MarkConfig config;

        /// <summary>
        /// Fired when an enemy crosses EnemyMarkMaxThreshold from below (false → >= threshold).
        /// Subscriber signature: (enemyId, playerId, stackCount). Not fired on subsequent hits
        /// while already at or above threshold (one-shot per "activation" cycle).
        /// </summary>
        public event Action<int, int, int> OnMarkThreshold;

        // Tracking which enemies have already had the threshold event fired this
        // activation cycle. Reset whenever stacks drop back below threshold (e.g.
        // after a decay) so the next crossing fires the event again. This avoids
        // re-firing the same event every frame while stacks remain at threshold.
        private readonly bool[] _thresholdFired = new bool[ComponentStore.MAX_ENTITIES];

        public MarkSystem(ComponentStore store, int playerId = 0)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.playerId = playerId;
            this.config = MarkConfig.Default;
        }

        /// <summary>
        /// Override the default mark configuration (decay interval, max stacks cap,
        /// default threshold). Typically called by GameManager.Initialize after
        /// loading marks.json.
        /// </summary>
        public void LoadConfig(MarkConfig cfg)
        {
            this.config = cfg ?? MarkConfig.Default;
        }

        /// <summary>Read-only access to current config (used by tests).</summary>
        public MarkConfig Config => config;

        /// <summary>
        /// Per-tick decay pass. Called by FrameScheduler in the CombatSetup group
        /// after tower/player attack damage is queued, BEFORE the death-resolve pass
        /// (so dead enemies are skipped naturally next frame).
        /// </summary>
        public void Update(float deltaTime)
        {
            if (deltaTime <= 0f) return;

            var activeEnemyIds = store.ActiveEnemyIds;
            int count = activeEnemyIds.Count;
            for (int i = 0; i < count; i++)
            {
                int enemyId = activeEnemyIds[i];
                if (!store.EnemyActive[enemyId]) continue;

                int stacks = store.EnemyMarkStacks[enemyId];
                if (stacks == 0 && store.EnemyMarkDecayTimer[enemyId] <= 0f)
                    continue; // Fast-path: not marked, no timer → no work

                // Tick the decay timer
                float timer = store.EnemyMarkDecayTimer[enemyId] - deltaTime;
                if (timer <= 0f)
                {
                    // Decay one stack
                    if (stacks > 0)
                    {
                        stacks -= 1;
                        store.EnemyMarkStacks[enemyId] = stacks;
                    }

                    // Reset _thresholdFired latch so the next threshold crossing fires
                    // the OnMarkThreshold event again (only if we actually dropped below).
                    int threshold = store.EnemyMarkMaxThreshold[enemyId];
                    if (threshold > 0 && stacks < threshold)
                    {
                        _thresholdFired[enemyId] = false;
                    }

                    if (stacks > 0)
                    {
                        // Re-arm the timer for the next stack's decay
                        store.EnemyMarkDecayTimer[enemyId] = config.DecayInterval;
                    }
                    else
                    {
                        // No stacks left, stop the timer
                        store.EnemyMarkDecayTimer[enemyId] = 0f;
                    }
                }
                else
                {
                    store.EnemyMarkDecayTimer[enemyId] = timer;
                }
            }
        }

        /// <summary>
        /// Add mark stacks to a target enemy (e.g., from a tower hit or player
        /// skill). Resets the decay timer. No-op if:
        ///   - target is invalid / inactive
        ///   - target has not opted in (EnemyMarkMaxThreshold == 0)
        ///   - stacksToAdd <= 0
        /// </summary>
        /// <param name="enemyId">Target enemy entity id.</param>
        /// <param name="stacksToAdd">Number of stacks to add (default 1).</param>
        /// <returns>The new total stack count after addition, or 0 if no-op.</returns>
        public int AddMark(int enemyId, int stacksToAdd = 1)
        {
            if (enemyId < 0 || enemyId >= ComponentStore.MAX_ENTITIES) return 0;
            if (!store.EnemyActive[enemyId]) return 0;
            if (stacksToAdd <= 0) return 0;

            int threshold = store.EnemyMarkMaxThreshold[enemyId];
            if (threshold <= 0) return 0; // opt-out

            int prevStacks = store.EnemyMarkStacks[enemyId];
            int newStacks = prevStacks + stacksToAdd;

            // Cap at threshold (threshold also acts as a soft cap for the "active" state)
            int cap = config.MaxStackCap > 0 ? Math.Min(threshold, config.MaxStackCap) : threshold;
            if (newStacks > cap) newStacks = cap;

            store.EnemyMarkStacks[enemyId] = newStacks;
            // Reset decay timer
            store.EnemyMarkDecayTimer[enemyId] = config.DecayInterval;

            // Fire threshold event on transition from < threshold to >= threshold
            if (!_thresholdFired[enemyId] && prevStacks < threshold && newStacks >= threshold)
            {
                _thresholdFired[enemyId] = true;
                OnMarkThreshold?.Invoke(enemyId, playerId, newStacks);
            }

            return newStacks;
        }

        /// <summary>
        /// Clear all marks on a target enemy (e.g., on dispel, banish, or wave end).
        /// Resets stacks + timer + threshold-firing latch.
        /// </summary>
        public void ClearMark(int enemyId)
        {
            if (enemyId < 0 || enemyId >= ComponentStore.MAX_ENTITIES) return;
            store.EnemyMarkStacks[enemyId] = 0;
            store.EnemyMarkDecayTimer[enemyId] = 0f;
            // Note: do NOT reset EnemyMarkMaxThreshold here — it's a static config
            // field (set at spawn). The _thresholdFired latch IS reset so a future
            // re-mark can re-fire the event.
            if (enemyId < _thresholdFired.Length)
                _thresholdFired[enemyId] = false;
        }

        /// <summary>
        /// Read-only access to the threshold-fired latch (used by tests).
        /// </summary>
        public bool IsThresholdFired(int enemyId)
        {
            if (enemyId < 0 || enemyId >= _thresholdFired.Length) return false;
            return _thresholdFired[enemyId];
        }

        /// <summary>
        /// Reset the per-entity threshold latch on entity destroy (called by
        /// SystemRegistry / GameManager when an enemy dies, to free the
        /// per-id slot in _thresholdFired).
        /// </summary>
        public void OnEnemyDestroyed(int enemyId)
        {
            if (enemyId < 0 || enemyId >= _thresholdFired.Length) return;
            _thresholdFired[enemyId] = false;
        }
    }

    /// <summary>
    /// MarkSystem tunable configuration. DecayInterval = seconds between single-stack
    /// decays. MaxStackCap is a hard cap on total stacks (default 100, 0 = no cap).
    /// </summary>
    public class MarkConfig
    {
        public float DecayInterval { get; set; } = 1.0f;
        public int MaxStackCap { get; set; } = 100;

        public static MarkConfig Default => new MarkConfig { DecayInterval = 1.0f, MaxStackCap = 100 };
    }
}
