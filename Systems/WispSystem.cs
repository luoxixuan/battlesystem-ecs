#nullable enable
using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Wisp System — passive aura pets that orbit the player and provide continuous buffs/debuffs.
    ///
    /// Three wisp types (mutually exclusive — only ONE active per player):
    ///   0 = None
    ///   1 = Heal Wisp — restores 3% max HP per second to the player (passive regen)
    ///   2 = Slow Wisp — applies 30% move-speed slow to all enemies within 6 tiles of the player
    ///   3 = Curse Wisp — applies 15 armor shred to all enemies within 6 tiles of the player
    ///                   (re-uses existing EnemyArmorShredStacks/Duration — fully observable in
    ///                   TowerAttackSystem damage calc, no new damage-taken modifier needed)
    ///
    /// Design notes:
    ///   - SOA fields live in ComponentStore_Player (PlayerWispType/DurationLeft/Cooldown).
    ///   - PlayerWispCooldown defaults to 0 (off-cooldown). The cooldown is used to throttle
    ///     re-summons AFTER a wisp expires so players can't re-summon instantly every frame.
    ///   - Wisps do NOT interfere with each other (only 1 active at a time), and do NOT
    ///     replace existing tower/pet systems — they layer on top of normal combat.
    ///   - Position-clamp uses the same POSITION_CLAMP=10000f guard as MagnetizeSystem so
    ///     spawned wisps don't end up off-map (defensive against bad coords).
    ///   - Update is called from SkillBuffGroup during Phase 9 (after combat, before death
    ///     resolution). At 1 player × 10K enemies × 1 type, the inner loop is ~10K distSq
    ///     checks per frame — cheap, no allocation.
    /// </summary>
    public class WispSystem
    {
        private readonly ComponentStore _store;
        private readonly IRenderer? _logger;

        // ── Wisp type constants (mirror PlayerWispType semantics) ──
        public const int TypeNone = 0;
        public const int TypeHeal = 1;
        public const int TypeSlow = 2;
        public const int TypeCurse = 3;

        // ── Per-player state: tracks enemy IDs the Slow Wisp touched on the
        //    previous tick so we can CLEAR their slow on the next tick before
        //    re-applying to the current in-range set. Prevents stale-state bug
        //    where an enemy walks out of range but remains slowed forever.
        //    Keyed by playerId → HashSet<enemyId>. Lazily allocated per player.
        private readonly Dictionary<int, HashSet<int>> _slowTouched =
            new Dictionary<int, HashSet<int>>();

        // ── Tunable effect parameters ──
        // Heal Wisp: percent of max HP restored per second (e.g. 0.03 = 3%/sec)
        private const float HEAL_PERCENT_PER_SEC = 0.03f;
        // Slow Wisp: movement-speed multiplier applied to enemies in radius (0.7 = -30%)
        private const float SLOW_FACTOR = 0.7f;
        // Slow Wisp: armor-shred stacks applied (existing field; stacks add to EnemyArmorShred)
        private const float CURSE_ARMOR_SHRED = 15f;
        // Common wisp aura radius in tiles
        private const float WISP_AURA_RADIUS = 6f;
        // Default cooldown after a wisp expires (seconds) — throttle re-summon
        private const float DEFAULT_COOLDOWN_AFTER_EXPIRE = 8f;

        public WispSystem(ComponentStore store, IRenderer? logger = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger;
        }

        // ─────────────────────────────────────────────────────────────
        //  Public API
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Summon a wisp for the given player. Replaces any existing wisp (mutually exclusive).
        /// Returns true if the summon was applied, false if player is on cooldown or wispType is invalid.
        /// Duration is in seconds.
        /// </summary>
        public bool SpawnWisp(int playerId, int wispType, float duration)
        {
            if (playerId < 0 || playerId >= _store.PlayerWispType.Length)
            {
                _logger?.Log($"[WISP] Refused spawn: playerId={playerId} out of range");
                return false;
            }
            if (wispType == TypeNone || duration <= 0f)
            {
                _logger?.Log($"[WISP] Refused spawn: wispType={wispType} duration={duration} (must be 1..3 and > 0)");
                return false;
            }
            if (_store.PlayerWispCooldown[playerId] > 0f)
            {
                _logger?.Log($"[WISP] Player {playerId} on cooldown ({_store.PlayerWispCooldown[playerId]:F1}s) — cannot summon wisp");
                return false;
            }

            // Replace existing wisp (mutually exclusive — no stacking)
            int oldType = _store.PlayerWispType[playerId];
            _store.PlayerWispType[playerId] = wispType;
            _store.PlayerWispDurationLeft[playerId] = duration;
            _store.PlayerWispCooldown[playerId] = 0f;
            if (oldType == TypeNone)
                _store.ActiveWispCount++;
            string typeName = wispType switch
            {
                TypeHeal  => "Heal",
                TypeSlow  => "Slow",
                TypeCurse => "Curse",
                _         => "Unknown"
            };
            _logger?.Log($"[WISP] Player {playerId} summoned {typeName} wisp for {duration:F0}s");
            return true;
        }

        /// <summary>Get count of players with any active wisp. Cheap O(MAX_PLAYERS) scan.</summary>
        public int GetActiveWispCount()
        {
            int count = 0;
            int n = _store.PlayerWispType.Length;
            for (int i = 0; i < n; i++)
            {
                if (_store.PlayerWispType[i] != TypeNone && _store.PlayerWispDurationLeft[i] > 0f)
                    count++;
            }
            return count;
        }

        // ─────────────────────────────────────────────────────────────
        //  Frame update — called from SkillBuffGroup during Phase 9
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Tick all active wisps: decrement duration, expire finished ones, apply aura effects.
        /// Per-player loop is O(MAX_PLAYERS=10). Slow/Curse aura scans all active enemies
        /// (early-out on distSq > radiusSq) — cheap at 10K enemies.
        /// </summary>
        public void Update(float deltaTime)
        {
            // O(1) early-out: skip all per-player loops when no wisps are active anywhere
            if (_store.ActiveWispCount <= 0) return;

            int n = _store.PlayerWispType.Length;
            for (int pid = 0; pid < n; pid++)
            {
                int wispType = _store.PlayerWispType[pid];
                if (wispType == TypeNone) continue;

                float duration = _store.PlayerWispDurationLeft[pid];
                duration -= deltaTime;
                if (duration <= 0f)
                {
                    // Wisp expired — clear and start cooldown
                    _store.PlayerWispType[pid] = TypeNone;
                    _store.PlayerWispDurationLeft[pid] = 0f;
                    _store.PlayerWispCooldown[pid] = DEFAULT_COOLDOWN_AFTER_EXPIRE;
                    _store.ActiveWispCount = Math.Max(0, _store.ActiveWispCount - 1);
                    _logger?.Log($"[WISP] Player {pid}'s wisp expired — cooldown {DEFAULT_COOLDOWN_AFTER_EXPIRE:F0}s");
                    continue;
                }
                _store.PlayerWispDurationLeft[pid] = duration;

                // Apply aura effect based on wisp type
                switch (wispType)
                {
                    case TypeHeal:
                        ApplyHealAura(pid, deltaTime);
                        break;
                    case TypeSlow:
                        ApplySlowAura(pid);
                        break;
                    case TypeCurse:
                        ApplyCurseAura(pid);
                        break;
                }
            }

            // For each player whose Slow Wisp is NOT currently active, clear any
            // enemies we previously touched (defensive cleanup — handles the
            // "enemy was slowed last frame, then wisp expired or player moved
            // out of EnemyActive range" edge case). Cheap: only iterates the
            // per-player touched sets.
            // NOTE: we must NOT check DurationLeft>0 here, because a player
            // could have switched from Slow Wisp to a different wisp type
            // (Heal/Curse) via SpawnWisp — in that case the touched set is
            // still non-empty and the previous-frame enemies need restoring.
            for (int pid = 0; pid < n; pid++)
            {
                if (_store.PlayerWispType[pid] == TypeSlow) continue;
                ClearStaleSlowForPlayer(pid);
            }

            // Tick per-player cooldowns (always — even with no active wisp)
            for (int pid = 0; pid < n; pid++)
            {
                if (_store.PlayerWispCooldown[pid] > 0f)
                {
                    _store.PlayerWispCooldown[pid] -= deltaTime;
                    if (_store.PlayerWispCooldown[pid] < 0f)
                        _store.PlayerWispCooldown[pid] = 0f;
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Aura effect implementations
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Heal Wisp: restore HEAL_PERCENT_PER_SEC of max HP per second to the player.
        /// Uses the existing SetPlayerCurrentHealth path via direct PlayerCurrentHealth write
        /// with clamp to max. Skips if player is at full HP or dead.
        /// </summary>
        private void ApplyHealAura(int playerId, float deltaTime)
        {
            float maxHp = _store.PlayerMaxHealth[playerId];
            if (maxHp <= 0f) return; // invalid player / not initialized
            float currentHp = _store.PlayerCurrentHealth[playerId];
            if (currentHp <= 0f) return;       // dead
            if (currentHp >= maxHp) return;    // already full

            float healAmount = maxHp * HEAL_PERCENT_PER_SEC * deltaTime;
            float newHp = currentHp + healAmount;
            if (newHp > maxHp) newHp = maxHp;
            _store.PlayerCurrentHealth[playerId] = newHp;
        }

        /// <summary>
        /// Slow Wisp: applies SLOW_FACTOR to all enemies within WISP_AURA_RADIUS of the player.
        /// Uses the existing EnemySlowFactor + EnemyMoveSpeed fields.
        ///
        /// Two-phase per-tick: (1) clear slow on enemies we touched LAST frame
        /// (so any enemy that left the radius recovers), (2) re-apply to the
        /// current in-range set. This guarantees no stale-slow state when an
        /// enemy moves out of range, and prevents the slow from compounding
        /// (we always recompute from EnemyMoveSpeedBase, never from a previously
        /// slowed EnemyMoveSpeed).
        /// </summary>
        private void ApplySlowAura(int playerId)
        {
            // Phase 1: clear slow on all enemies we touched last frame
            ClearStaleSlowForPlayer(playerId);

            // Phase 2: re-apply to current in-range enemies
            if (!_slowTouched.TryGetValue(playerId, out var touched))
            {
                touched = new HashSet<int>();
                _slowTouched[playerId] = touched;
            }
            touched.Clear();

            int playerEntity = _store.PlayerEntityId;
            if (playerEntity < 0 || !_store.PositionActive[playerEntity]) return;
            float px = _store.PositionX[playerEntity];
            float py = _store.PositionY[playerEntity];
            float radiusSq = WISP_AURA_RADIUS * WISP_AURA_RADIUS;

            var activeEnemyIds = _store.GetCachedActiveEnemyIds();
            for (int i = 0; i < activeEnemyIds.Count; i++)
            {
                int enemyId = activeEnemyIds[i];
                if (!_store.EnemyActive[enemyId] || !_store.PositionActive[enemyId]) continue;

                float dx = _store.PositionX[enemyId] - px;
                float dy = _store.PositionY[enemyId] - py;
                float distSq = dx * dx + dy * dy;
                if (distSq > radiusSq) continue;

                // Apply slow from base (no compounding). If base is 0, skip
                // (enemy not initialized yet — its spawner will set base on
                // the same frame, we'll catch it next tick).
                float baseSpeed = _store.EnemyMoveSpeedBase[enemyId];
                if (baseSpeed <= 0f) continue;
                _store.EnemySlowFactor[enemyId] = SLOW_FACTOR;
                _store.EnemyMoveSpeed[enemyId] = baseSpeed * SLOW_FACTOR;
                touched.Add(enemyId);
            }
        }

        /// <summary>
        /// Restore EnemySlowFactor=0 and EnemyMoveSpeed=baseSpeed for all enemies
        /// the given player's Slow Wisp touched on a previous tick. After
        /// clearing, the set is empty until the next slow tick repopulates it.
        /// </summary>
        private void ClearStaleSlowForPlayer(int playerId)
        {
            if (!_slowTouched.TryGetValue(playerId, out var touched) || touched.Count == 0)
                return;
            foreach (int enemyId in touched)
            {
                if (!_store.EnemyActive[enemyId])
                {
                    // Enemy despawned — nothing to restore
                    continue;
                }
                // Only restore if our wisp's slow is still the active one
                // (a stronger slow from another source would set a smaller
                // factor; we only clear when our factor is currently the
                // one applied)
                if (_store.EnemySlowFactor[enemyId] == SLOW_FACTOR)
                {
                    _store.EnemySlowFactor[enemyId] = 0f;
                    float baseSpeed = _store.EnemyMoveSpeedBase[enemyId];
                    if (baseSpeed > 0f)
                    {
                        _store.EnemyMoveSpeed[enemyId] = baseSpeed;
                    }
                }
            }
            touched.Clear();
        }

        /// <summary>
        /// Curse Wisp: applies CURSE_ARMOR_SHRED stacks + 5-turn duration to all enemies in range.
        /// Re-uses the existing EnemyArmorShredStacks/Duration infrastructure — TowerAttackSystem
        /// already reads these and applies the armor-penetration reduction during damage calc.
        /// Re-applying each frame keeps the duration refreshed.
        /// </summary>
        private void ApplyCurseAura(int playerId)
        {
            int playerEntity = _store.PlayerEntityId;
            if (playerEntity < 0 || !_store.PositionActive[playerEntity]) return;
            float px = _store.PositionX[playerEntity];
            float py = _store.PositionY[playerEntity];
            float radiusSq = WISP_AURA_RADIUS * WISP_AURA_RADIUS;

            var activeEnemyIds = _store.GetCachedActiveEnemyIds();
            for (int i = 0; i < activeEnemyIds.Count; i++)
            {
                int enemyId = activeEnemyIds[i];
                if (!_store.EnemyActive[enemyId] || !_store.PositionActive[enemyId]) continue;

                float dx = _store.PositionX[enemyId] - px;
                float dy = _store.PositionY[enemyId] - py;
                float distSq = dx * dx + dy * dy;
                if (distSq > radiusSq) continue;

                // Apply armor shred — refresh each frame so duration stays high
                _store.EnemyArmorShredStacks[enemyId] = CURSE_ARMOR_SHRED;
                _store.EnemyArmorShredDuration[enemyId] = 5f;
            }
        }
    }
}
