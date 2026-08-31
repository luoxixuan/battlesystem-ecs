using System;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Death Mark System — Round 200 Direction 5.
    /// Stack-based execute counter applied by tower/player attacks. Each procced hit
    /// increments EnemyDeathMarkStacks by +N (N = TowerDeathMarkStacksPerHit) and resets
    /// EnemyDeathMarkTimer. When the timer expires, one stack is consumed. When stacks
    /// reach EnemyDeathMarkMaxStacks, the system fires OnDeathMarkFull and auto-executes
    /// the target (QueueEnemyDeath + bonus gold payout via goldSystem reference).
    ///
    /// Distinction from MarkSystem (Round 107):
    ///   - MarkSystem = binary threshold tracker (one-shot OnMarkThreshold event when stacks
    ///     cross a fixed cap; payoff is handled by subscribers).
    ///   - DeathMarkSystem = *linear-scaling* damage bonus (each stack adds
    ///     EnemyDeathMarkBonusPerStack fraction to incoming damage) + an auto-execute payoff
    ///     when stacks hit the per-enemy cap. Decay is 1-stack-per-interval; immune
    ///     (EnemyExecuteImmune) enemies cannot be Death Marked.
    ///
    /// Distinction from BleedSystem:
    ///   - Bleed = damage-over-time (stack * dmgPerStack * maxHP per tick).
    ///   - Death Mark = counter + damage bonus + execute; does not directly deal damage,
    ///     only modifies incoming damage and triggers one-shot execution on cap.
    ///
    /// Lifecycle (per WavePhase tick, runs in CombatSetup group AFTER tower/player attack):
    ///   1. Decay loop: iterate active enemies. If EnemyDeathMarkTimer > 0, decrement.
    ///      When timer reaches 0:
    ///        a. Decrement EnemyDeathMarkStacks (clamp at 0).
    ///        b. If still > 0, reset timer to DeathMarkSubsystemConfig.DefaultDecayInterval.
    ///        c. If stacks == 0, leave timer at 0 (no ticking).
    ///   2. Threshold-triggered events (auto-execute) are fire-and-forget
    ///      via the public OnDeathMarkFull event. Subscribers can hook gold bonuses.
    ///
    /// Per-frame cost: O(active enemies) with one cheap timer decrement + branch.
    /// Enemies with EnemyDeathMarkStacks == 0 && EnemyDeathMarkTimer == 0 (the default)
    /// skip with a single bool check, so non-marked enemies incur ~zero cost.
    /// </summary>
    public class DeathMarkSystem
    {
        private readonly ComponentStore store;
        private readonly int playerId;
        private DeathMarkConfig config;

        /// <summary>
        /// Fired when an enemy crosses EnemyDeathMarkMaxStacks from below (false → >= cap).
        /// Subscriber signature: (enemyId, playerId, stackCount). Not fired on subsequent
        /// hits while already at or above cap. DeathMarkSystem itself calls QueueEnemyDeath
        /// to auto-execute the target.
        /// </summary>
        public event Action<int, int, int> OnDeathMarkFull;

        // Tracking which enemies have already had the full event fired this activation cycle.
        // Reset whenever stacks drop back below the cap (e.g. after a decay) so the next
        // crossing fires the event again. Avoids re-firing the same event every frame
        // while stacks remain at cap.
        private readonly bool[] _fullFired = new bool[ComponentStore.MAX_ENTITIES];

        public DeathMarkSystem(ComponentStore store, int playerId = 0)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.playerId = playerId;
            this.config = DeathMarkConfig.Default;
        }

        /// <summary>
        /// Override the default Death Mark configuration (decay interval, max stacks cap,
        /// default bonus per stack). Typically called by GameManager.Initialize after
        /// loading marks.json.
        /// </summary>
        public void LoadConfig(DeathMarkConfig cfg)
        {
            this.config = cfg ?? DeathMarkConfig.Default;
        }

        /// <summary>Read-only access to current config (used by tests).</summary>
        public DeathMarkConfig Config => config;

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

                int stacks = store.EnemyDeathMarkStacks[enemyId];
                if (stacks == 0 && store.EnemyDeathMarkTimer[enemyId] <= 0f)
                    continue; // Fast-path: not marked, no timer → no work

                // Tick the decay timer
                float timer = store.EnemyDeathMarkTimer[enemyId] - deltaTime;
                if (timer <= 0f)
                {
                    // Decay one stack
                    if (stacks > 0)
                    {
                        stacks -= 1;
                        store.EnemyDeathMarkStacks[enemyId] = stacks;
                    }

                    // Reset _fullFired latch so the next cap crossing fires
                    // the OnDeathMarkFull event again (only if we actually dropped below).
                    int cap = store.EnemyDeathMarkMaxStacks[enemyId];
                    if (cap > 0 && stacks < cap)
                    {
                        _fullFired[enemyId] = false;
                    }

                    if (stacks > 0)
                    {
                        // Re-arm the timer for the next stack's decay
                        store.EnemyDeathMarkTimer[enemyId] = config.DecayInterval;
                    }
                    else
                    {
                        // No stacks left, stop the timer
                        store.EnemyDeathMarkTimer[enemyId] = 0f;
                    }
                }
                else
                {
                    store.EnemyDeathMarkTimer[enemyId] = timer;
                }
            }
        }

        /// <summary>
        /// Add Death Mark stacks to a target enemy (e.g., from a tower hit or player
        /// skill). Resets the decay timer. No-op if:
        ///   - target is invalid / inactive
        ///   - target has ExecuteImmunity (Round 132 Dir 8 — Bosses opt out)
        ///   - target has not opted in (EnemyDeathMarkMaxStacks == 0)
        ///   - stacksToAdd <= 0
        /// </summary>
        /// <param name="enemyId">Target enemy entity id.</param>
        /// <param name="stacksToAdd">Number of stacks to add (default 1).</param>
        /// <returns>The new total stack count after addition, or 0 if no-op.</returns>
        public int AddDeathMark(int enemyId, int stacksToAdd = 1)
        {
            if (enemyId < 0 || enemyId >= ComponentStore.MAX_ENTITIES) return 0;
            if (!store.EnemyActive[enemyId]) return 0;
            if (stacksToAdd <= 0) return 0;
            // Round 132 Dir 8 — Execute Immunity: Death Mark auto-execute payoff means immune
            // enemies (Bosses) opt out entirely. Zero cost on immune enemies (single bool read).
            if (store.EnemyExecuteImmune[enemyId]) return 0;

            int cap = store.EnemyDeathMarkMaxStacks[enemyId];
            if (cap <= 0) return 0; // opt-out

            int prevStacks = store.EnemyDeathMarkStacks[enemyId];
            int newStacks = prevStacks + stacksToAdd;

            // Cap at per-enemy max (also hard-capped by config.MaxStackCap as upper bound)
            int hardCap = config.MaxStackCap > 0 ? Math.Min(cap, config.MaxStackCap) : cap;
            if (newStacks > hardCap) newStacks = hardCap;

            store.EnemyDeathMarkStacks[enemyId] = newStacks;
            // Reset decay timer
            store.EnemyDeathMarkTimer[enemyId] = config.DecayInterval;

            // Fire full-stack event on transition from < hardCap to >= hardCap; auto-execute target.
            // Note: use hardCap (the effective ceiling) as the threshold, not cap, so the event fires
            // even when config.MaxStackCap < per-enemy EnemyDeathMarkMaxStacks.
            if (!_fullFired[enemyId] && prevStacks < hardCap && newStacks >= hardCap)
            {
                _fullFired[enemyId] = true;
                OnDeathMarkFull?.Invoke(enemyId, playerId, newStacks);

                // Auto-execute: zero out the enemy's HP and queue death (gold bonus paid by
                // existing death-resolution pipeline via EnemyExecuteBonusGold). Skip if
                // already dead (defensive — shouldn't happen in normal flow).
                float currentHp = store.EnemyHealth[enemyId];
                if (currentHp > 0f && !store.EnemyIsInvulnerable[enemyId])
                {
                    store.ApplyDamageAuthority(playerId, enemyId, currentHp, playerId, flags: Core.GAS.DamageFlags.Execute, stage: Core.GAS.DamageAmountStage.PostMitigation);
                }
            }

            return newStacks;
        }

        /// <summary>
        /// Get the additive damage multiplier for an enemy based on its current Death Mark
        /// stacks. Returns 1.0f for unmarked enemies (no bonus). At N stacks with bonus
        /// per stack = B, returns 1.0f + N*B.
        ///
        /// Used by damage resolution (e.g., TowerAttackSystem / PlayerTowerAttackSystem)
        /// to apply the bonus AFTER armor/resistance calculations.
        /// </summary>
        public float GetDamageMultiplier(int enemyId)
        {
            if (enemyId < 0 || enemyId >= ComponentStore.MAX_ENTITIES) return 1.0f;
            int stacks = store.EnemyDeathMarkStacks[enemyId];
            if (stacks <= 0) return 1.0f;
            float bonusPerStack = store.EnemyDeathMarkBonusPerStack[enemyId];
            if (bonusPerStack <= 0f) return 1.0f;
            return 1.0f + stacks * bonusPerStack;
        }

        /// <summary>True if the enemy has any Death Mark stacks active (test helper).</summary>
        public bool IsMarked(int enemyId)
        {
            if (enemyId < 0 || enemyId >= ComponentStore.MAX_ENTITIES) return false;
            return store.EnemyDeathMarkStacks[enemyId] > 0;
        }

        /// <summary>
        /// Clear all Death Mark stacks on a target enemy (e.g., on dispel, banish, or wave end).
        /// Resets stacks + timer + full-fired latch.
        /// </summary>
        public void ClearDeathMark(int enemyId)
        {
            if (enemyId < 0 || enemyId >= ComponentStore.MAX_ENTITIES) return;
            store.EnemyDeathMarkStacks[enemyId] = 0;
            store.EnemyDeathMarkTimer[enemyId] = 0f;
            // Note: do NOT reset EnemyDeathMarkMaxStacks or EnemyDeathMarkBonusPerStack here —
            // they're static config fields (set at spawn). The _fullFired latch IS reset so a
            // future re-mark can re-fire the event.
            if (enemyId < _fullFired.Length)
                _fullFired[enemyId] = false;
        }

        /// <summary>
        /// Read-only access to the full-fired latch (used by tests).
        /// </summary>
        public bool IsFullFired(int enemyId)
        {
            if (enemyId < 0 || enemyId >= _fullFired.Length) return false;
            return _fullFired[enemyId];
        }

        /// <summary>
        /// Reset the per-entity full-stack latch on entity destroy (called by
        /// SystemRegistry / GameManager when an enemy dies, to free the
        /// per-id slot in _fullFired).
        /// </summary>
        public void OnEnemyDestroyed(int enemyId)
        {
            if (enemyId < 0 || enemyId >= _fullFired.Length) return;
            _fullFired[enemyId] = false;
        }
    }

    /// <summary>
    /// DeathMarkSystem tunable configuration. DecayInterval = seconds between single-stack
    /// decays. MaxStackCap is a hard cap on total stacks (default 50, 0 = no cap).
    /// </summary>
    public class DeathMarkConfig
    {
        public float DecayInterval { get; set; } = DeathMarkSubsystemConfig.DefaultDecayInterval;
        public int MaxStackCap { get; set; } = DeathMarkSubsystemConfig.DefaultMaxStackCap;

        public static DeathMarkConfig Default => new DeathMarkConfig
        {
            DecayInterval = DeathMarkSubsystemConfig.DefaultDecayInterval,
            MaxStackCap = DeathMarkSubsystemConfig.DefaultMaxStackCap,
        };
    }
}
