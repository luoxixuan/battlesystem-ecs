#nullable enable
using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Sapper (Engineer) enemy system — Round 186 Direction 2.
    ///
    /// Sappers are tower-attacking enemies that periodically swing at the nearest
    /// tower on the path, dealing damage AND applying a cumulative attack-speed
    /// slow (stacks, capped at SapperMaxSlowStacks × SapperAtkSpdSlowPerStack).
    ///
    /// Design notes:
    ///   - Runs in the AI group (after EnemyAI, before Movement). Each frame, every
    ///     active Sapper enemy:
    ///       1. Increments EnemySapperAttackTimer by deltaTime
    ///       2. If the timer reached the attack interval, picks the nearest tower
    ///          within SapperRange and deals damage + applies slow stacks
    ///       3. Resets the timer to 0
    ///   - The target tower's TowerCurrentHp and TowerSapperSlowMult are written
    ///     directly here. BeginFrame() in ComponentStore resets TowerSapperSlowMult
    ///     to 0 each tick, and SapperSystem re-derives it from the live Sapper set
    ///     — so no drift if a Sapper dies, retargets, or moves away.
    ///   - TowerCurrentHp ≤ 0 → tower is "destroyed" by Sapper pressure; the
    ///     TowerAttackSystem hot path checks HP>0 before firing (no-op for legacy
    ///     indestructible towers with TowerMaxHp == 0).
    ///   - Sentinel-gated: non-sapper enemies pay one bool read + branch in the
    ///     active-enemy loop (zero work otherwise).
    ///
    /// No allocations in the hot path (List is reused frame-to-frame).
    /// </summary>
    public class SapperSystem
    {
        private readonly ComponentStore _store;
        private readonly IRenderer _logger;

        // Reused per-frame event list to avoid allocations. Cleared at the start
        // of every Update() call. (int sapperId, int towerId, float damage, float slowPerStack, int maxStacks)
        private readonly List<(int sapperId, int towerId, float damage, float slowPerStack, int maxStacks)>
            _attackEvents = new(64);

        public SapperSystem(ComponentStore store, IRenderer logger)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void SetTurn(int turn, float deltaTime)
        {
            // Per-turn setup hook: nothing needed. SapperAttackTimer is incremented
            // in Update() so it stays continuous across turns.
        }

        public void Update(float deltaTime)
        {
            if (deltaTime <= 0f) return;
            var activeEnemies = _store.GetCachedActiveEnemyIds();
            if (activeEnemies.Count == 0) return;

            _attackEvents.Clear();

            // ── Phase 1: walk all Sapper enemies, decide attacks (read-only) ─────
            for (int i = 0; i < activeEnemies.Count; i++)
            {
                int sapperId = activeEnemies[i];
                if (!_store.EnemyActive[sapperId]) continue;
                if (!_store.EnemyIsSapper[sapperId]) continue;  // fast-path: non-sappers pay 1 bool read

                // Tick the attack timer (deltaTime-based; frame-rate independent).
                float interval = _store.EnemySapperAttackInterval[sapperId];
                if (interval <= 0f) continue;  // mis-configured sapper (0 interval) — skip safely
                float timer = _store.EnemySapperAttackTimer[sapperId] + deltaTime;
                if (timer < interval)
                {
                    _store.EnemySapperAttackTimer[sapperId] = timer;
                    continue;
                }
                _store.EnemySapperAttackTimer[sapperId] = 0f;  // reset for next swing

                // Find the nearest tower within range. Towers with TowerMaxHp == 0
                // are indestructible (legacy path) and are excluded from targeting.
                float range = _store.EnemySapperRange[sapperId];
                int targetTower = FindNearestTower(sapperId, range);
                if (targetTower < 0) continue;  // no eligible tower in range — skip this swing

                float damage = _store.EnemySapperDamage[sapperId];
                float slowPerStack = _store.EnemySapperAtkSpdSlowPerStack[sapperId];
                int maxStacks = _store.EnemySapperMaxSlowStacks[sapperId];

                _attackEvents.Add((sapperId, targetTower, damage, slowPerStack, maxStacks));
            }

            // ── Phase 2: serial — apply damage and slow stacks to towers ────────
            for (int i = 0; i < _attackEvents.Count; i++)
            {
                var (sapperId, towerId, damage, slowPerStack, maxStacks) = _attackEvents[i];
                if (!_store.EnemyActive[sapperId]) continue;
                if (!_store.TowerActive[towerId]) continue;
                if (_store.TowerMaxHp[towerId] <= 0f) continue;  // indestructible tower (no MaxHp set)
                if (_store.TowerCurrentHp[towerId] <= 0f) continue;  // already destroyed

                // Apply HP damage.
                float newHp = _store.TowerCurrentHp[towerId] - damage;
                _store.TowerCurrentHp[towerId] = newHp;

                // Accumulate slow stacks for this Sapper→tower pair (capped at
                // slowPerStack × maxStacks). The Sapper's current slow is stored on
                // the Sapper (not the tower) so it tracks the Sapper's own stack
                // counter; the roll-up to TowerSapperSlowMult happens in
                // RecomputeTowerSlows() at the end of the frame.
                if (maxStacks > 0 && slowPerStack > 0f)
                {
                    float currentSlow = _store.EnemySapperAtkSpdSlow[sapperId];
                    float cap = slowPerStack * maxStacks;
                    if (currentSlow < cap)
                    {
                        _store.EnemySapperAtkSpdSlow[sapperId] = Math.Min(currentSlow + slowPerStack, cap);
                    }
                }

                _logger.Log($"[SAPPER] Enemy {sapperId} swings at tower {towerId} for {damage:F1} dmg ({newHp:F1}/{_store.TowerMaxHp[towerId]:F1} HP) | slow={_store.EnemySapperAtkSpdSlow[sapperId]:P0}");

                if (newHp <= 0f)
                {
                    _logger.Log($"[SAPPER] Tower {towerId} destroyed by Sapper {sapperId}!");
                    // The tower is now inert (HP=0 means TowerAttackSystem skips it).
                    // The player can rebuild/replace via the normal placement flow.
                }
            }
        }

        /// <summary>
        /// Recompute each tower's TowerSapperSlowMult by summing the per-Sapper
        /// slow contributions from every Sapper currently targeting it. Called
        /// once per frame AFTER Update() to keep the multiplier in sync.
        ///
        /// BeginFrame() has already reset TowerSapperSlowMult to 0 for every
        /// active tower, so this is a pure additive roll-up. The "what tower is
        /// each Sapper targeting?" question is answered by re-running the same
        /// nearest-tower scan we used for the swing decision.
        /// </summary>
        public void RecomputeTowerSlows()
        {
            var activeEnemies = _store.GetCachedActiveEnemyIds();
            if (activeEnemies.Count == 0) return;
            for (int e = 0; e < activeEnemies.Count; e++)
            {
                int sapperId = activeEnemies[e];
                if (!_store.EnemyActive[sapperId]) continue;
                if (!_store.EnemyIsSapper[sapperId]) continue;
                float slow = _store.EnemySapperAtkSpdSlow[sapperId];
                if (slow <= 0f) continue;
                int target = FindNearestTower(sapperId, _store.EnemySapperRange[sapperId]);
                if (target < 0) continue;
                if (_store.TowerMaxHp[target] <= 0f) continue;
                if (_store.TowerCurrentHp[target] <= 0f) continue;
                _store.TowerSapperSlowMult[target] += slow;
            }
        }

        /// <summary>
        /// Find the nearest active, damageable tower to the Sapper. Returns -1
        /// if no eligible tower is within <paramref name="range"/> tiles. Uses
        /// manhattan distance to match the existing ProjectileSystem scan
        /// convention. Excludes towers with TowerMaxHp == 0 (indestructible
        /// legacy path) and TowerCurrentHp == 0 (already destroyed by Sapper).
        /// </summary>
        private int FindNearestTower(int sapperId, float range)
        {
            if (range <= 0f) return -1;
            float sx = _store.PositionX[sapperId];
            float sy = _store.PositionY[sapperId];
            var activeTowers = _store.ActiveTowerIds;
            int best = -1;
            float bestDist = range;
            for (int i = 0; i < activeTowers.Count; i++)
            {
                int tid = activeTowers[i];
                if (!_store.TowerActive[tid]) continue;
                if (_store.TowerMaxHp[tid] <= 0f) continue;       // indestructible
                if (_store.TowerCurrentHp[tid] <= 0f) continue;  // destroyed
                float dx = Math.Abs(_store.PositionX[tid] - sx);
                float dy = Math.Abs(_store.PositionY[tid] - sy);
                float dist = dx + dy;  // manhattan, matches existing convention
                if (dist <= bestDist)
                {
                    bestDist = dist;
                    best = tid;
                }
            }
            return best;
        }
    }
}
