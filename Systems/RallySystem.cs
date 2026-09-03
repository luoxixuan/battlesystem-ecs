#nullable enable
using System;
using System.Collections.Generic;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;

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
    /// 激活通道是 <see cref="ResourceResolver.Events"/> 上的 <c>DamageApplied</c>
    /// （<c>ApplyPlayerDamageAuthority</c> 的事实），不再订阅 <c>EventBus.PlayerDamaged</c>。
    /// <c>combat.rally.consume</c> 在 tower-attack 之前消费本帧已有事实并重写塔加成；
    /// <c>skill-buff.rally.update</c> 再消费一次以覆盖 thorns / projectile 等战斗段伤害。
    /// </summary>
    public class RallySystem
    {
        private readonly ComponentStore _store;
        private readonly IRenderer _logger;
        private int _resourceEventCursor;

        // Cached list of active rally towers per player — used by the expiry
        // pass to find which towers to clear when a rally ends. Reused frame-to-frame
        // to avoid allocations. (int playerId, List<int> towerIds)
        private readonly Dictionary<int, List<int>> _activeRallyTowers = new Dictionary<int, List<int>>(4);

        public RallySystem(ComponentStore store, IRenderer logger, EventBus? eventBus = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _ = eventBus;
        }

        /// <summary>Round 187 — per-turn setup hook. Currently no-op (Rally is event-driven).</summary>
        public void SetTurn(int turn, float deltaTime)
        {
            // No-op: 激活由 DamageApplied 消费驱动。Per-turn setup 留给后续效果。
        }

        /// <summary>
        /// 只读消费 ResourceResolver 上的玩家 DamageApplied，不移除事件
        /// （gameplay-event.commit 的 ConsumeOnly 还要读同一队列）。
        /// </summary>
        public void ConsumePlayerDamageFacts()
        {
            var events = _store.ResourceResolver.Events;
            int count = events.Count;
            if (count < _resourceEventCursor) _resourceEventCursor = 0;
            for (int i = _resourceEventCursor; i < count; i++)
            {
                var ev = events.Get(i);
                if (ev.Type != GameplayEventType.DamageApplied) continue;
                int pid = ev.OwnerPlayerId;
                if (pid < 0) pid = ev.Target.IsValid ? ev.Target.Index : -1;
                if ((uint)pid >= ComponentStore.MAX_PLAYERS) continue;
                if (!ev.Target.IsValid || ev.Target.Index != pid) continue;
                TryActivateRally(pid);
            }
            _resourceEventCursor = count;
        }

        /// <summary>
        /// 对当前仍激活的 Rally 重写 TowerRallyAtkSpdBonus。BeginFrame 每帧清零该列，
        /// 必须在 tower-attack 之前再写一次，持续帧塔攻才能吃到加成。
        /// </summary>
        public void ApplyActiveBonuses()
        {
            for (int pid = 0; pid < ComponentStore.MAX_PLAYERS; pid++)
            {
                if (!_store.PlayerRallyActive[pid]) continue;
                ApplyRallyBonusesForPlayer(pid);
            }
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
            ConsumePlayerDamageFacts();
            if (deltaTime <= 0f)
            {
                ApplyActiveBonuses();
                return;
            }

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

            ApplyActiveBonuses();
        }

        private void TryActivateRally(int pid)
        {
            if (_store.PlayerRallyCooldown[pid] > 0f) return;

            _store.PlayerRallyActive[pid] = true;
            _store.PlayerRallyCooldown[pid] = RallyConfig.RallyCooldown;
            _store.PlayerRallyDurationLeft[pid] = RallyConfig.RallyDuration;
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
    }
}
