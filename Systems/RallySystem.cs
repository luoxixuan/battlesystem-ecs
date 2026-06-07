#nullable enable
using System;
using System.Collections.Generic;
using BattleSystemECS.Config;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Rally Buff — Round 187 Direction 4 (Player-Tower Linkage).
    ///
    /// Niche mechanic: when the player takes damage, all friendly towers within
    /// <c>RallyRadius</c> tiles of the player receive a temporary
    /// <c>RallyAtkSpdBonus</c> (+attack speed) for <c>RallyDuration</c> seconds.
    /// Cooldown <c>RallyCooldown</c> prevents stacking on every hit.
    ///
    /// Wires up via the existing <see cref="GameEvents.PlayerDamaged"/> event —
    /// published in 3 places (EnemyAISystem, EnemyAbilitySystem, TelegraphSystem),
    /// all of which are the canonical "player lost HP" signal. No new event hooks
    /// needed.
    ///
    /// Hot-path design:
    ///   - <c>PlayerRallyCooldown</c> / <c>PlayerRallyDurationLeft</c> default 0
    ///     (zero-overhead fast path; Update returns early when no player has an
    ///     active rally).
    ///   - When a player takes damage, OnPlayerDamagedHandler does an O(active_towers)
    ///     radius scan to mark nearby towers' <c>TowerRallyAtkSpdBonus</c>.
    ///   - BeginFrame() in ComponentStore resets TowerRallyAtkSpdBonus to 0 every
    ///     frame (RallySystem re-derives it from the active-player set every frame).
    ///   - The hot-path in TowerAttackSystem reads TowerRallyAtkSpdBonus as an
    ///     additive bonus on top of Fortress/HotZone/Desperation — same model.
    ///
    /// Lazy-init: SystemRegistry wires the EventBus at construction time; if no
    /// bus is supplied the system degrades to a "no-op" (Update is gated by the
    /// presence of any active rally).
    /// </summary>
    public class RallySystem
    {
        private readonly ComponentStore _store;
        private readonly IRenderer _logger;
        private readonly IEventBus? _eventBus;

        // Cached list of active rally towers per player — used by the expiry
        // pass to find which towers to clear when a rally ends. Reused frame-to-frame
        // to avoid allocations. (int playerId, List<int> towerIds)
        private readonly Dictionary<int, List<int>> _activeRallyTowers = new Dictionary<int, List<int>>(4);

        public RallySystem(ComponentStore store, IRenderer logger, IEventBus? eventBus = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus;

            if (_eventBus != null)
            {
                // Wrap method group in a lambda so the signature matches EventBus's
                // Action<object> exactly (some .NET runtimes reject method-group
                // conversion from Action<object?> to Action<object>; lambda always works).
                _eventBus.Subscribe(GameEvents.PlayerDamaged, data => OnPlayerDamagedHandler(data));
            }
        }

        /// <summary>Round 187 — per-turn setup hook. Currently no-op (Rally is event-driven).</summary>
        public void SetTurn(int turn, float deltaTime)
        {
            // No-op: the rally activation is fully event-driven via the PlayerDamaged
            // subscription in the constructor. Per-turn setup is reserved for future
            // effects (e.g. "rally morale" on the first enemy of a wave).
        }

        /// <summary>
        /// Per-frame tick: decrement PlayerRallyCooldown and PlayerRallyDurationLeft.
        /// When DurationLeft hits 0, clear all rally-tower bonuses for that player.
        /// Also recomputes per-tower <c>TowerRallyAtkSpdBonus</c> by re-scanning the
        /// active-player set's rally zones. (This means a tower that previously
        /// received the bonus this frame sees it disappear as soon as the rally
        /// expires — same model as SapperSystem's per-frame re-derivation.)
        /// </summary>
        public void Update(float deltaTime)
        {
            if (deltaTime <= 0f) return;

            // Step 1: per-frame rewrite of TowerRallyAtkSpdBonus from the active-player
            // set. The reset already happened in BeginFrame(), so we just need to
            // re-derive for any player that currently has PlayerRallyActive=true.
            // Zero-cost fast path: if no player is rallying, no writes happen.
            for (int pid = 0; pid < ComponentStore.MAX_PLAYERS; pid++)
            {
                if (!_store.PlayerRallyActive[pid]) continue;

                // Tick duration. If it just hit 0, deactivate + clear per-player list.
                float dur = _store.PlayerRallyDurationLeft[pid] - deltaTime;
                if (dur <= 0f)
                {
                    _store.PlayerRallyActive[pid] = false;
                    _store.PlayerRallyDurationLeft[pid] = 0f;
                    // Clear bonus on the towers that were affected by the expired rally.
                    // (BeginFrame resets TowerRallyAtkSpdBonus to 0 every frame anyway,
                    // but if the affected towers get re-derived by ANOTHER active rally
                    // this same frame, we want to make sure this player's expired list
                    // does not leak its bonus into the next frame.)
                    if (_activeRallyTowers.TryGetValue(pid, out var expired))
                    {
                        for (int i = 0; i < expired.Count; i++)
                        {
                            int tid = expired[i];
                            if (_store.TowerRallyAtkSpdBonus[tid] > 0f)
                            {
                                // Only clear if the bonus was contributed by this player.
                                // (Single-player game: this is always the case. Multi-player
                                // safety: if another rally is also active, the re-derivation
                                // in Step 3 below will overwrite the field correctly.)
                                _store.TowerRallyAtkSpdBonus[tid] = 0f;
                            }
                        }
                        expired.Clear();
                    }
                    _logger.Log($"[RALLY] Player {pid} rally expired");
                    continue;
                }
                _store.PlayerRallyDurationLeft[pid] = dur;
            }

            // Step 2: tick PlayerRallyCooldown (clamped at 0).
            for (int pid = 0; pid < ComponentStore.MAX_PLAYERS; pid++)
            {
                float cd = _store.PlayerRallyCooldown[pid];
                if (cd <= 0f) continue;
                _store.PlayerRallyCooldown[pid] = cd > deltaTime ? cd - deltaTime : 0f;
            }

            // Step 3: for each active-rally player, scan towers in radius and write
            // TowerRallyAtkSpdBonus. We re-scan every frame (not just at activation)
            // because towers can be built / destroyed between activation and expiry.
            for (int pid = 0; pid < ComponentStore.MAX_PLAYERS; pid++)
            {
                if (!_store.PlayerRallyActive[pid]) continue;
                ApplyRallyBonusesForPlayer(pid);
            }
        }

        /// <summary>
        /// Event handler: PlayerDamaged → if cooldown == 0, activate rally for that
        /// player and write the rally zones into the per-player tower list.
        /// </summary>
        private void OnPlayerDamagedHandler(object? data)
        {
            if (data is not PlayerDamagedEvent ev) return;
            int pid = ResolvePlayerIdFromEvent(ev);
            if (pid < 0) return;

            // Cooldown gate.
            if (_store.PlayerRallyCooldown[pid] > 0f) return;

            // Activate. Reset cooldown and stamp duration. Clear any stale list.
            _store.PlayerRallyActive[pid] = true;
            _store.PlayerRallyCooldown[pid] = RallyConfig.RallyCooldown;
            _store.PlayerRallyDurationLeft[pid] = RallyConfig.RallyDuration;

            // Re-derive the affected tower list immediately so the rest of this
            // frame's TowerAttackSystem reads see the buff. (The Update() pass
            // would also do this, but applying it here closes the gap.)
            ApplyRallyBonusesForPlayer(pid);

            _logger.Log($"[RALLY] Player {pid} triggered rally (radius={RallyConfig.RallyRadius}, +{RallyConfig.RallyAtkSpdBonus:P0} atk spd for {RallyConfig.RallyDuration}s, cd={RallyConfig.RallyCooldown}s)");
        }

        /// <summary>
        /// Walk the active tower set; for every active non-dispelled tower, write
        /// RallyAtkSpdBonus into <c>TowerRallyAtkSpdBonus</c>. Records the affected
        /// tower IDs in <c>_activeRallyTowers[pid]</c> for future expiry passes.
        ///
        /// Round 187 design note: the ComponentStore has no per-tower owner
        /// field (single-player game; all towers implicitly belong to player 0).
        /// Rally therefore affects ALL friendly towers. The <paramref name="pid"/>
        /// argument is preserved for future multi-player support and for the
        /// per-player cooldown gate.
        /// </summary>
        private void ApplyRallyBonusesForPlayer(int pid)
        {
            float bonus = RallyConfig.RallyAtkSpdBonus;
            if (bonus <= 0f) return;

            // Squared-distance check: only towers within RallyRadius of the player
            // (squared to avoid sqrt) receive the buff. Default RallyRadius=5 tiles.
            float radiusSq = RallyConfig.RallyRadius * RallyConfig.RallyRadius;
            float px = _store.PositionX[pid];
            float py = _store.PositionY[pid];

            if (!_activeRallyTowers.TryGetValue(pid, out var affected))
            {
                affected = new List<int>(16);
                _activeRallyTowers[pid] = affected;
            }
            affected.Clear();

            var activeTowers = _store.ActiveTowerIds;
            for (int i = 0; i < activeTowers.Count; i++)
            {
                int tid = activeTowers[i];
                if (!_store.TowerActive[tid]) continue;
                if (_store.TowerIsDispelled[tid]) continue;
                if (_store.TowerMaxHp[tid] > 0f && _store.TowerCurrentHp[tid] <= 0f) continue;

                // Distance check: only towers within RallyRadius receive the buff.
                // (Active tower 0/0 default PositionX/Y is 0f — when player is also
                // at 0/0 (test default) all towers are within radius, so the fast
                // path "towers in zone" stays working for the test environment.)
                float dx = _store.PositionX[tid] - px;
                float dy = _store.PositionY[tid] - py;
                if (dx * dx + dy * dy > radiusSq) continue;

                _store.TowerRallyAtkSpdBonus[tid] = bonus;
                affected.Add(tid);
            }
        }

        /// <summary>
        /// Best-effort player-id resolution from the PlayerDamagedEvent. The event
        /// does not currently carry a playerId field; we use the player who has
        /// the lowest current health as a proxy (matches the "the player who got
        /// hit" semantic — there's only one such player per game in single-player
        /// and in multi-player the EventBus is delivered synchronously so the
        /// most-recently-damaged player is the right one).
        ///
        /// In single-player (the common case) playerId is always 0.
        /// </summary>
        private int ResolvePlayerIdFromEvent(PlayerDamagedEvent ev)
        {
            // Multi-player heuristic: pick the player whose current health matches
            // the event's RemainingHealth, within a small tolerance.
            float target = ev.RemainingHealth;
            float bestDelta = float.MaxValue;
            int best = -1;
            for (int pid = 0; pid < ComponentStore.MAX_PLAYERS; pid++)
            {
                if (_store.PlayerCurrentHealth[pid] <= 0f) continue;  // dead
                float delta = Math.Abs(_store.PlayerCurrentHealth[pid] - target);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    best = pid;
                }
            }
            return best;
        }
    }
}
